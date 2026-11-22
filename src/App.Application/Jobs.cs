using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Portfel.Domain;

namespace Portfel.Application;

public sealed class WorkspaceJobs(IWorkspaceStore store, TimeProvider clock)
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    public Task<JobReceipt> QueueAsync(string owner, JobKind kind, string? asset, string key, bool demo, CancellationToken cancellationToken,
        string? unsupportedBody = null)
    {
        if (key.Length is < 1 or > 128 || !key.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or ':'))
            throw new AppFault("invalid_idempotency_key", "Задайте корректный Idempotency-Key.");

        return store.UpdateAsync(owner, state =>
        {
            string fingerprint = unsupportedBody is null ? Fingerprint(kind, asset) : PortfelJson.Hash(new { kind, asset, unsupportedBody });
            if (state.Jobs.SingleOrDefault(job => job.IdempotencyKey == key) is { } existing)
            {
                if (existing.RequestHash != fingerprint) throw new AppFault("idempotency_conflict", "Ключ уже использован для другого запроса.", 409);
                return (state, PortfolioQueries.Receipt(existing));
            }

            if (unsupportedBody is not null) throw new AppFault("invalid_request", "Эта операция не принимает параметры в теле.");
            if (asset is not null && !state.Assets.Any(item => item.Asset.Id == asset))
                throw new AppFault("asset_not_found", "Актив не найден.", 404);
            if (asset is not null && state.Current?.Positions.SingleOrDefault(item => item.AssetId == asset)?.IsSpot == false)
                throw new AppFault("unsupported_position", "Анализ этой позиции не поддерживается.");
            if (!demo) throw new AppFault("CMC_ACCESS_UNVERIFIED", "Чтение личного портфеля CMC не подтверждено.", 409);
            Job job = new(Guid.NewGuid(), kind, asset, key, fingerprint, JobStatus.Queued, clock.GetUtcNow());
            return (state with { Jobs = [.. state.Jobs, job] }, PortfolioQueries.Receipt(job));
        }, cancellationToken);
    }

    public Task<JobReceipt> CancelAsync(string owner, Guid id, CancellationToken cancellationToken) =>
        store.UpdateAsync(owner, state =>
        {
            Job job = Get(state, id);
            if (job.Status is JobStatus.Queued or JobStatus.RetryWait or JobStatus.Running)
            {
                job = job with { Status = JobStatus.Cancelled, Lease = null, LeaseUntil = null, FinishedAt = clock.GetUtcNow() };
                state = MarkUnsettled(state, job.Id);
            }
            return (Replace(state, job), PortfolioQueries.Receipt(job));
        }, cancellationToken);

    public Task<Job?> ClaimAsync(string owner, CancellationToken cancellationToken) => store.ClaimAsync(owner, clock, cancellationToken);

    public static Job? ClaimCandidate(Job[] active, DateTimeOffset now)
    {
        var busy = active.Where(item => item.Status == JobStatus.Running && item.LeaseUntil > now)
            .Select(item => (item.Kind, item.AssetId)).ToHashSet();
        return Array.Find(active, item => (item.Status == JobStatus.Queued
            || item.Status is JobStatus.Running or JobStatus.RetryWait && item.LeaseUntil <= now)
            && !busy.Contains((item.Kind, item.AssetId)));
    }

    public static Job ClaimTransition(Job job, DateTimeOffset now, bool dispatched)
    {
        if (dispatched) return Finish(job, now, JobStatus.Failed, "outcome_unknown");
        if (job.Attempt >= 3)
            return Finish(job, now, JobStatus.Failed, "attempts_exhausted");
        return job with { Status = JobStatus.Running, Lease = Guid.NewGuid(), LeaseUntil = now + LeaseDuration, Attempt = job.Attempt + 1, BlockReason = null };
    }

    public Task<bool> HeartbeatAsync(string owner, Job lease, CancellationToken cancellationToken) => store.UpdateAsync(owner, state =>
    {
        Job current = Get(state, lease.Id);
        DateTimeOffset now = clock.GetUtcNow();
        if (!Owns(current, lease, now)) return (state, false);
        return (Replace(state, current with { LeaseUntil = now + LeaseDuration }), true);
    }, cancellationToken);

    public Task<bool> ExecuteDemoAsync(string owner, Job lease, CancellationToken cancellationToken) =>
        store.UpdateAsync(owner, state =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Job job = Get(state, lease.Id);
            DateTimeOffset now = clock.GetUtcNow();
            if (!Owns(job, lease, now)) return (state, false);

            if (lease.Kind == JobKind.PortfolioSync)
            {
                var previous = state.Current?.Positions.Select(position => position.AssetId).ToHashSet(StringComparer.Ordinal) ?? [];
                state = DemoPortfolio.Sync(state, now);
                foreach (Position added in state.Current!.Positions.Where(item => item.IsSpot != false && !previous.Contains(item.AssetId)))
                {
                    string key = $"initial:{PortfelJson.Hash(new { job.Id, added.AssetId })}";
                    if (!state.Jobs.Any(item => item.IdempotencyKey == key))
                        state = state with { Jobs = [.. state.Jobs, new(Guid.NewGuid(), JobKind.AssetAnalysis,
                            added.AssetId, key, Fingerprint(JobKind.AssetAnalysis, added.AssetId), JobStatus.Queued, now)] };
                }
            }
            else
            {
                (AnalysisInputV1 snapshot, AnalysisResultV1 analysis) = DemoPortfolio.Analyze(state, lease.AssetId!, now);
                string? error = AnalysisValidation.Validate(snapshot, analysis);
                if (error is not null)
                    return (Replace(state, Finish(job, now, JobStatus.Failed, error)), false);
                state = Publish(state, owner, snapshot, analysis, now);
            }

            return (Replace(state, Finish(job, now, JobStatus.Succeeded)), true);
        }, cancellationToken);

    public Task<bool> FailAsync(string owner, Job lease, CancellationToken cancellationToken) => store.UpdateAsync(owner, state =>
    {
        Job job = Get(state, lease.Id);
        DateTimeOffset now = clock.GetUtcNow();
        if (!Owns(job, lease, now)) return (state, false);
        if (state.AiExecutions?.Any(item => item.JobId == job.Id) == true)
            return (Replace(MarkUnsettled(state, job.Id), Finish(job, now, JobStatus.Failed, "outcome_unknown")), true);
        Job end = job.Attempt >= 3 ? Finish(job, now, JobStatus.Failed, "internal_error") : job with
        {
            Status = JobStatus.RetryWait,
            Lease = null,
            LeaseUntil = now.AddSeconds(job.Attempt * job.Attempt * 5 + Random.Shared.Next(1, 4)),
            BlockReason = "retry_scheduled",
        };
        return (Replace(state, end), true);
    }, cancellationToken);

    public Task<bool> FinishAnalysisAsync(string owner, Job lease, AnalysisInputV1? snapshot, AnalysisResultV1? result,
        JobStatus status, string? reason, CancellationToken ct) => store.UpdateAsync(owner, state =>
    {
        ct.ThrowIfCancellationRequested();
        DateTimeOffset now = clock.GetUtcNow();
        Job job = Get(state, lease.Id);
        if (!Owns(job, lease, now)) return (state, false);
        if (status == JobStatus.Succeeded)
        {
            if (snapshot is null || result is null || job.AssetId != snapshot.AssetId || snapshot.Demo
                || state.AnalysisSnapshots?.Any(item => item.SnapshotId == snapshot.SnapshotId && item.ContentHash == snapshot.SnapshotHash) != true)
                return (Replace(state, Finish(job, now, JobStatus.Failed, "invalid_snapshot")), false);
            try { state = Publish(state, owner, snapshot, result, now); }
            catch (AppFault fault) { return (Replace(state, Finish(job, now, JobStatus.Failed, fault.Code)), false); }
        }
        return (Replace(state, Finish(job, now, status, reason)), status == JobStatus.Succeeded);
    }, ct);

    public static Workspace Publish(Workspace state, string owner, AnalysisInputV1 snapshot, AnalysisResultV1 analysis, DateTimeOffset now,
        ProjectEvent? trigger = null)
    {
        if (snapshot.AsOf > now || AnalysisValidation.Validate(snapshot, analysis) is not null)
            throw new AppFault("invalid_output", "Результат не прошёл проверку публикации.", 409);
        if (state.Feed.Any(item => item.Analysis.SnapshotHash == analysis.SnapshotHash)) return state;
        bool current = AnalysisSnapshots.IsCurrentContext(state, snapshot, now);
        if (!current && !snapshot.Demo && AnalysisSnapshots.HasPrivateContext(snapshot))
            throw new AppFault("portfolio_changed", "Состав портфеля изменился после начала анализа.", 409);
        if (trigger is not null && (trigger.AssetId != snapshot.AssetId || !snapshot.Events.Contains(trigger)))
            throw new AppFault("invalid_event", "Событие не входит в снимок.", 409);

        DateTimeOffset local = TimeZoneInfo.ConvertTime(snapshot.AsOf, TimeZoneInfo.FindSystemTimeZoneById(state.Settings.Timezone));
        string? portfolio = snapshot.SourcePortfolioId ?? state.Current?.SourcePortfolioId;
        string type = trigger is null ? "daily_analysis" : "event_analysis";
        string key = trigger is null
            ? PortfelJson.Hash(new { owner, portfolio, analysis.AssetId, Date = DateOnly.FromDateTime(local.DateTime), type })
            : PortfelJson.Hash(new { owner, portfolio, analysis.AssetId, trigger.Id, trigger.Version, type });
        FeedItem? previous = state.Feed.Where(item => item.PublishKey == key).MaxBy(item => item.Version);
        // The pre-review demo used a readable daily key; do not create a second card on upgrade.
        if (previous is null && snapshot.Demo && trigger is null)
        {
            string legacy = FormattableString.Invariant($"{owner}:{portfolio}:{analysis.AssetId}:{local:yyyy-MM-dd}:daily_analysis");
            previous = state.Feed.Where(item => item.PublishKey == legacy).MaxBy(item => item.Version);
            if (previous is not null) key = legacy;
        }
        if (trigger is not null && previous is not null) return state;
        FeedItem item = new(Guid.NewGuid(), analysis.AssetId, type, key, now, snapshot.AsOf,
            current ? "current" : "asset_only", analysis, snapshot.Evidence, snapshot.Demo,
            (previous?.Version ?? 0) + 1, previous?.Id, false, snapshot.PortfolioHash, snapshot.SnapshotId, snapshot.Asset?.Symbol);
        return StoreAnalysisSnapshot(state, snapshot) with { Feed = [.. state.Feed, item] };
    }

    internal static Workspace StoreAnalysisSnapshot(Workspace state, AnalysisInputV1 snapshot)
    {
        string hash = snapshot.SnapshotHash;
        if (state.AnalysisSnapshots?.SingleOrDefault(item => item.SnapshotId == snapshot.SnapshotId) is { } existing)
        {
            if (existing.ContentHash != hash) throw new AppFault("invalid_snapshot", "Содержимое снимка уже зафиксировано.", 409);
            return state;
        }
        return state with { AnalysisSnapshots = [.. state.AnalysisSnapshots ?? [], new(snapshot.SnapshotId, hash,
            System.Text.Json.JsonSerializer.Serialize(snapshot, PortfelJson.Options), snapshot.AsOf)] };
    }

    public static Workspace MarkUnsettled(Workspace state, Guid jobId) => state.AiExecutions is null ? state : state with
    {
        AiExecutions = state.AiExecutions.Select(item => item.JobId == jobId && item.Status == "reserved"
            ? item with { Status = "outcome_unknown" } : item).ToArray(),
    };

    public static Job Get(Workspace state, Guid id) => state.Jobs.SingleOrDefault(item => item.Id == id)
        ?? throw new AppFault("job_not_found", "Задание не найдено.", 404);

    internal static bool Owns(Job current, Job lease, DateTimeOffset now) =>
        current.Status == JobStatus.Running && lease.Lease is not null && current.Lease == lease.Lease && current.LeaseUntil > now;

    private static string Fingerprint(JobKind kind, string? asset) => PortfelJson.Hash(new { kind, asset });

    private static Job Finish(Job job, DateTimeOffset now, JobStatus status, string? reason = null) => job with
    {
        Status = status, FinishedAt = now, Lease = null, LeaseUntil = null, BlockReason = reason,
    };

    private static Workspace Replace(Workspace state, Job job) => state with
    {
        Jobs = state.Jobs.Select(item => item.Id == job.Id ? job : item).ToArray(),
    };
}

public sealed record DemoSettings(bool Enabled);

public sealed class PortfolioWorker(IServiceScopeFactory scopes, DemoSettings demo, TimeProvider clock,
    ILogger<PortfolioWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!demo.Enabled) return;
        DateTimeOffset nextSchedule = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            WorkspaceJobs jobs = scope.ServiceProvider.GetRequiredService<WorkspaceJobs>();
            try
            {
                if (clock.GetUtcNow() >= nextSchedule)
                {
                    await ScheduleAsync(scope.ServiceProvider, jobs, stoppingToken);
                    nextSchedule = clock.GetUtcNow().AddSeconds(30);
                }
                Job? job = await jobs.ClaimAsync(DemoPortfolio.Owner, stoppingToken);
                if (job is not null)
                {
                    try { await jobs.ExecuteDemoAsync(DemoPortfolio.Owner, job, stoppingToken); }
                    catch (Exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning("Job {JobId} failed during execution.", job.Id);
                        await jobs.FailAsync(DemoPortfolio.Owner, job, stoppingToken);
                    }
                }
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("The worker could not access its queue.");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
        }
    }

    private async Task ScheduleAsync(IServiceProvider services, WorkspaceJobs jobs, CancellationToken cancellationToken)
    {
        Workspace state = await services.GetRequiredService<IWorkspaceStore>().ReadAsync(DemoPortfolio.Owner, cancellationToken);
        if (!state.Settings.ScheduleEnabled || state.Current is null) return;
        DateTimeOffset now = clock.GetUtcNow();
        DateTimeOffset local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(state.Settings.Timezone));
        if (TimeOnly.FromDateTime(local.DateTime) < TimeOnly.ParseExact(state.Settings.DailyTime, "HH:mm", CultureInfo.InvariantCulture)) return;
        foreach (Position position in state.Current.Positions.Where(item => item.IsSpot != false))
        {
            string key = FormattableString.Invariant($"daily:{local:yyyy-MM-dd}:{position.AssetId}");
            if (state.Jobs.All(item => item.IdempotencyKey != key))
                await jobs.QueueAsync(DemoPortfolio.Owner, JobKind.AssetAnalysis, position.AssetId, key, true, cancellationToken);
        }
    }
}
