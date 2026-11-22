using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Portfel.Domain;

namespace Portfel.Application;

public sealed class WorkspaceJobs(IWorkspaceStore store, TimeProvider clock)
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    public Task<JobReceipt> QueueAsync(string owner, JobKind kind, string? asset, string key, bool demo, CancellationToken cancellationToken)
    {
        if (key.Length is < 1 or > 128 || !key.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or ':'))
            throw new AppFault("invalid_idempotency_key", "Задайте корректный Idempotency-Key.");

        return store.UpdateAsync(owner, state =>
        {
            string fingerprint = PortfelJson.Hash(new { kind, asset });
            if (state.Jobs.SingleOrDefault(job => job.IdempotencyKey == key) is { } existing)
            {
                if (existing.RequestHash != fingerprint) throw new AppFault("idempotency_conflict", "Ключ уже использован для другого запроса.", 409);
                return (state, PortfolioQueries.Receipt(existing));
            }

            if (asset is not null && !state.Assets.Any(item => item.Asset.Id == asset))
                throw new AppFault("asset_not_found", "Актив не найден.", 404);
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
                job = job with { Status = JobStatus.Cancelled, Lease = null, LeaseUntil = null, FinishedAt = clock.GetUtcNow() };
            return (Replace(state, job), PortfolioQueries.Receipt(job));
        }, cancellationToken);

    public Task<Job?> ClaimAsync(string owner, CancellationToken cancellationToken) => store.UpdateAsync<Job?>(owner, state =>
    {
        DateTimeOffset now = clock.GetUtcNow();
        Job? job = Array.Find(state.Jobs, item => (item.Status == JobStatus.Queued
            || item.Status is JobStatus.Running or JobStatus.RetryWait && item.LeaseUntil <= now)
            && !state.Jobs.Any(other => other.Id != item.Id && other.Kind == item.Kind && other.AssetId == item.AssetId
                && other.Status == JobStatus.Running && other.LeaseUntil > now));
        if (job is null) return (state, null);
        if (job.Attempt >= 3)
        {
            Job failed = job with { Status = JobStatus.Failed, Lease = null, LeaseUntil = null, BlockReason = "attempts_exhausted", FinishedAt = now };
            return (Replace(state, failed), null);
        }
        Job started = job with { Status = JobStatus.Running, Lease = Guid.NewGuid(), LeaseUntil = now + LeaseDuration, Attempt = job.Attempt + 1 };
        return (Replace(state, started), started);
    }, cancellationToken);

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
                foreach (Position added in state.Current!.Positions.Where(item => !previous.Contains(item.AssetId)))
                {
                    string key = $"initial:{state.Current.Hash}:{added.AssetId}";
                    if (!state.Jobs.Any(item => item.IdempotencyKey == key))
                        state = state with { Jobs = [.. state.Jobs, new(Guid.NewGuid(), JobKind.AssetAnalysis,
                            added.AssetId, key, key, JobStatus.Queued, now)] };
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
        Job end = job.Attempt >= 3 ? Finish(job, now, JobStatus.Failed, "internal_error") : job with
        {
            Status = JobStatus.RetryWait,
            Lease = null,
            LeaseUntil = now.AddSeconds(job.Attempt * job.Attempt * 5 + Random.Shared.Next(1, 4)),
            BlockReason = "retry_scheduled",
        };
        return (Replace(state, end), true);
    }, cancellationToken);

    public static Workspace Publish(Workspace state, string owner, AnalysisInputV1 snapshot, AnalysisResultV1 analysis, DateTimeOffset now)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(state.Settings.Timezone));
        string key = FormattableString.Invariant($"{owner}:{state.Current?.SourcePortfolioId}:{analysis.AssetId}:{local:yyyy-MM-dd}:daily_analysis");
        FeedItem? previous = state.Feed.LastOrDefault(item => item.PublishKey == key);
        if (previous?.Analysis.SnapshotHash == analysis.SnapshotHash) return state;
        bool current = snapshot.PortfolioHash is not null && snapshot.PortfolioHash == state.Current?.Hash
            && state.Current.LastCheckedAt.AddHours(1) >= now;
        if (!current && !snapshot.Demo)
            throw new AppFault("portfolio_changed", "Состав портфеля изменился после начала анализа.", 409);
        FeedItem item = new(Guid.NewGuid(), analysis.AssetId, "daily_analysis", key, now, snapshot.AsOf,
            current ? "current" : "asset_only", analysis, snapshot.Evidence, snapshot.Demo,
            (previous?.Version ?? 0) + 1, previous?.Id, false);
        return state with { Feed = [.. state.Feed, item] };
    }

    public static Job Get(Workspace state, Guid id) => state.Jobs.SingleOrDefault(item => item.Id == id)
        ?? throw new AppFault("job_not_found", "Задание не найдено.", 404);

    private static bool Owns(Job current, Job lease, DateTimeOffset now) =>
        current.Status == JobStatus.Running && current.Lease == lease.Lease && current.LeaseUntil > now;

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
        while (!stoppingToken.IsCancellationRequested)
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            WorkspaceJobs jobs = scope.ServiceProvider.GetRequiredService<WorkspaceJobs>();
            try
            {
                await ScheduleAsync(scope.ServiceProvider, jobs, stoppingToken);
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
        if (TimeOnly.FromDateTime(local.DateTime) < TimeOnly.ParseExact(state.Settings.DailyTime, "HH:mm")) return;
        foreach (Position position in state.Current.Positions)
        {
            string key = FormattableString.Invariant($"daily:{local:yyyy-MM-dd}:{position.AssetId}");
            if (state.Jobs.All(item => item.IdempotencyKey != key))
                await jobs.QueueAsync(DemoPortfolio.Owner, JobKind.AssetAnalysis, position.AssetId, key, true, cancellationToken);
        }
    }
}
