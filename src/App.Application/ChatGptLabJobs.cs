using System.Threading.Channels;
using Microsoft.Extensions.Hosting;

namespace Portfel.Application;

public sealed record LabJob(Guid Id, string CoinId, string Model, DateTimeOffset CreatedAt, string Status,
    string? Error = null, OpinionPost? Post = null, AiSource[]? Sources = null, string? ProviderRequestId = null,
    int? ProviderHttpStatus = null, AiOutputPreview? RawOutput = null, OpinionPost? Draft = null, string? ProviderTerminalState = null,
    Guid? BatchId = null, string[]? UnverifiedBlocks = null, OpinionDecision? Decision = null,
    OpinionReview? Review = null, DateTimeOffset? CompletedAt = null);

public delegate Task<OpinionOutcome> LabOpinionRunner(Guid browser, string coin, string model, CancellationToken ct);

public sealed class ChatGptLabJobs(LabOpinionRunner run, TimeProvider clock) : BackgroundService
{
    public const int MaxBatchCoins = 40;
    private const int MaxReviewsPerHour = 50;
    private const int MaxHistory = 150;
    private readonly object sync = new();
    private readonly Dictionary<Guid, Work> jobs = [];
    private readonly Dictionary<Guid, Batch> batches = [];
    private readonly Queue<DateTimeOffset> submissions = new();
    private readonly Channel<Work[]> queue = Channel.CreateBounded<Work[]>(new BoundedChannelOptions(4) { SingleReader = true });
    private DateTimeOffset retryAt;

    public LabJob Submit(Guid browser, Guid id, string coin, string model)
    {
        lock (sync)
        {
            if (jobs.TryGetValue(id, out var previous))
            {
                if (previous.Browser != browser || previous.Job.CoinId != coin || previous.Job.Model != model || previous.Job.BatchId.HasValue)
                    throw new AppFault("lab_job_conflict", "Этот идентификатор уже использован другим заданием.", 409);
                return previous.Job;
            }
            if (batches.ContainsKey(id)) throw new AppFault("lab_job_conflict", "Этот идентификатор уже использован пакетом.", 409);
            var now = RequireCapacity(browser, 1);
            var work = new Work(browser, new(id, coin, model, now, "queued"));
            Enqueue([work], now);
            return work.Job;
        }
    }

    public LabJob[] SubmitBatch(Guid browser, Guid id, string[] coins, string model)
    {
        if (coins.Length is < 1 or > MaxBatchCoins || coins.Length != coins.Distinct(StringComparer.Ordinal).Count())
            throw new AppFault("lab_bad_batch", $"Укажите до {MaxBatchCoins} уникальных CoinGecko ID.");
        lock (sync)
        {
            if (batches.TryGetValue(id, out var existing))
            {
                if (existing.Browser != browser || existing.Work.Any(work => work.Job.Model != model) ||
                    !existing.Work.Select(work => work.Job.CoinId).SequenceEqual(coins, StringComparer.Ordinal))
                    throw new AppFault("lab_job_conflict", "Этот идентификатор уже использован другим пакетом.", 409);
                return existing.Work.Select(work => work.Job).ToArray();
            }
            if (jobs.ContainsKey(id)) throw new AppFault("lab_job_conflict", "Этот идентификатор уже использован обзором.", 409);
            var now = RequireCapacity(browser, coins.Length);
            var work = coins.Select(coin => new Work(browser, new(Guid.NewGuid(), coin, model, now, "queued", BatchId: id))).ToArray();
            Enqueue(work, now);
            batches.Add(id, new(browser, work));
            return work.Select(item => item.Job).ToArray();
        }
    }

    private DateTimeOffset RequireCapacity(Guid browser, int count)
    {
        var now = clock.GetUtcNow();
        if (retryAt > now)
            throw new AppFault("lab_rate_limited", "ChatGPT временно ограничил запросы. Проверьте лимиты в ChatGPT.", 429);
        if (jobs.Values.Any(work => work.Browser == browser && Active(work.Job)))
            throw new AppFault("lab_job_pending", "Дождитесь завершения текущих обзоров.", 409);
        while (submissions.TryPeek(out var stamp) && now - stamp >= TimeSpan.FromHours(1)) submissions.Dequeue();
        if (count > MaxReviewsPerHour - submissions.Count)
            throw new AppFault("lab_rate_limited", $"Можно поставить ещё {MaxReviewsPerHour - submissions.Count} обзоров в текущем часовом окне.", 429);
        return now;
    }

    private void Enqueue(Work[] work, DateTimeOffset now)
    {
        if (!queue.Writer.TryWrite(work))
            throw new AppFault("lab_busy", "Очередь обзоров заполнена. Попробуйте позже.", 429);
        foreach (var item in work)
        {
            jobs.Add(item.Job.Id, item);
            submissions.Enqueue(now);
        }
        while (jobs.Count > MaxHistory)
        {
            var oldest = jobs.Values.Where(item => !Active(item.Job) &&
                (item.Job.BatchId is not { } id || batches[id].Work.All(entry => !Active(entry.Job))))
                .MinBy(item => item.Job.CreatedAt);
            if (oldest is null) break;
            if (oldest.Job.BatchId is { } batch && batches.Remove(batch, out var previous))
                foreach (var item in previous.Work) jobs.Remove(item.Job.Id);
            else jobs.Remove(oldest.Job.Id);
        }
    }

    public LabJob[] List(Guid browser)
    {
        lock (sync)
            return jobs.Values.Where(work => work.Browser == browser).Select(work => work.Job)
                .OrderByDescending(job => job.CreatedAt).ToArray();
    }

    public void Cancel(Guid browser, Guid? batchId = null)
    {
        lock (sync)
        {
            if (batchId is { } id && (!batches.TryGetValue(id, out var batch) || batch.Browser != browser))
                throw new AppFault("lab_batch_not_found", "Пакет обзоров не найден.", 404);
            foreach (var work in jobs.Values.Where(work => work.Browser == browser && Active(work.Job) &&
                (batchId is null || work.Job.BatchId == batchId)))
            {
                work.Job = work.Job with { Status = "cancelled" };
                work.Cancellation?.Cancel();
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var group in queue.Reader.ReadAllAsync(stoppingToken))
        {
            string? blocked = null;
            foreach (var work in group)
                blocked = await ExecuteOneAsync(work, blocked, stoppingToken);
        }
    }

    private async Task<string?> ExecuteOneAsync(Work work, string? blocked, CancellationToken stoppingToken)
    {
        stoppingToken.ThrowIfCancellationRequested();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        LabJob starting;
        lock (sync)
        {
            if (work.Job.Status != "queued") return blocked;
            blocked ??= retryAt > clock.GetUtcNow() ? "rate_limited" : null;
            if (blocked is not null)
            {
                work.Job = work.Job with { Status = "skipped", Error = blocked };
                return blocked;
            }
            work.Cancellation = cancellation;
            starting = work.Job = work.Job with { Status = "running" };
        }
        OpinionOutcome outcome;
        try { outcome = await run(work.Browser, starting.CoinId, starting.Model, cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { outcome = new(null, [], "cancelled"); }
        catch (AppFault fault) { outcome = new(null, [], fault.Code); }
        catch (Exception) { outcome = new(null, [], "internal_error"); }
        lock (sync)
        {
            work.Cancellation = null;
            if (!cancellation.IsCancellationRequested && work.Job.Status == "running")
            {
                string? providerError = outcome.Error ?? outcome.Review?.Error;
                if (providerError is "quota_exhausted" or "rate_limited" or "chatgpt_rate_limited")
                {
                    var now = clock.GetUtcNow();
                    retryAt = outcome.RetryAt is { } deadline && deadline > now ? deadline : now.AddMinutes(1);
                }
                work.Job = work.Job with
                {
                    Status = outcome.Post is null ? "failed" : outcome.UnverifiedBlocks is { Length: > 0 } ? "partial" : "succeeded",
                    Error = outcome.Error, Post = outcome.Post, Sources = outcome.Sources,
                    ProviderRequestId = outcome.RequestId, ProviderHttpStatus = outcome.UpstreamStatus,
                    RawOutput = outcome.Output, Draft = outcome.Draft, ProviderTerminalState = outcome.TerminalState,
                    UnverifiedBlocks = outcome.UnverifiedBlocks, Decision = outcome.Decision, Review = outcome.Review, CompletedAt = clock.GetUtcNow(),
                };
                if (providerError is "quota_exhausted" or "rate_limited" or "chatgpt_rate_limited" or "authentication_required"
                    or "chatgpt_reconnect" or "chatgpt_plan_disabled" or "ineligible_account" or "unsupported_capability"
                    or "provider_unavailable" or "provider_rejected_schema")
                    return providerError;
            }
        }
        return null;
    }

    private static bool Active(LabJob job) => job.Status is "queued" or "running";

    private sealed class Work(Guid browser, LabJob job)
    {
        public Guid Browser { get; } = browser;
        public LabJob Job { get; set; } = job;
        public CancellationTokenSource? Cancellation { get; set; }
    }

    private sealed record Batch(Guid Browser, Work[] Work);
}
