using Microsoft.Extensions.DependencyInjection;
using System.Text;
using Portfel.Domain;

namespace Portfel.Application;

public sealed record AiRoute(Guid ConnectionId, string Model, IAiProvider Provider, string BillingMode,
    ExactNumber? ReservedUsd = null, ExactNumber? ConnectionDailyUsd = null, int? ConnectionDailyResearch = null,
    long? ConnectionDailyTokens = null);
public sealed record AiDailyLimits(ExactNumber DailyUsd, int ResearchRuns, long Tokens);
public sealed record AiRunEntry(Guid Id, Guid JobId, Guid ConnectionId, string Model, string BillingMode, bool Research,
    DateTimeOffset StartedAt, int TokenAllowance, ExactNumber? ReservedUsd, string Status, AiReceipt? Receipt = null);

public sealed class VerifiedAnalysisJob(IServiceScopeFactory scopes, TimeProvider clock)
{
    // The caller must supply routes for the verified owner of a leased job, never a browser-supplied URL or provider.
    public async Task RunAsync(string owner, Job lease, AiRoute research, AiRoute synthesis, AiDailyLimits limits, CancellationToken stoppingToken)
    {
        if (owner == DemoPortfolio.Owner || lease.Kind != JobKind.AssetAnalysis || lease.AssetId is null)
            throw new ArgumentException("Для рабочего анализа нужен реальный владелец и задание актива.");

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task monitor = MonitorLease(owner, lease, lifetime);
        try
        {
            Workspace state;
            await using (var scope = scopes.CreateAsyncScope())
                state = await scope.ServiceProvider.GetRequiredService<IWorkspaceStore>().ReadAsync(owner, lifetime.Token);
            var seed = AnalysisSnapshots.ForAsset(state, lease.AssetId, clock.GetUtcNow());
            var researcher = new GuardedProvider(scopes, clock, owner, lease, research, limits, null);

            var synthesisCaps = await synthesis.Provider.CapabilitiesAsync(lifetime.Token);
            if ((!synthesisCaps.StructuredOutput && !synthesisCaps.JsonOutput) ||
                (synthesisCaps.Model is { } supportedModel && supportedModel != synthesis.Model) ||
                !GuardedProvider.IsSelected(state, synthesis, false))
            {
                await Complete(null, null, JobStatus.Blocked, "blocked_capability");
                return;
            }
            var (snapshot, researched) = await AiAnalysis.ResearchAsync(researcher, research.Model, seed, clock, lifetime.Token);
            if (snapshot is null)
            {
                await Complete(null, null, FailureStatus(researched), researched.Failure);
                return;
            }
            var writer = new GuardedProvider(scopes, clock, owner, lease, synthesis, limits, snapshot);
            var (result, synthesized) = await AiAnalysis.SynthesizeAsync(writer, synthesis.Model, snapshot, lifetime.Token);
            if (result is null) await Complete(null, null, FailureStatus(synthesized), synthesized.Failure);
            else await Complete(snapshot, result, JobStatus.Succeeded, null);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (AppFault fault) when (!lifetime.IsCancellationRequested)
        {
            await Complete(null, null, JobStatus.Failed, fault.Code);
        }
        finally
        {
            await lifetime.CancelAsync();
            await monitor;
        }

        async Task Complete(AnalysisInputV1? snapshot, AnalysisResultV1? result, JobStatus status, string? reason)
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<WorkspaceJobs>()
                .FinishAnalysisAsync(owner, lease, snapshot, result, status, reason, lifetime.Token);
        }
    }

    private static JobStatus FailureStatus(AiReceipt receipt) => receipt.Status == "blocked"
        || receipt.Failure is "quota_exhausted" or "authentication_required" or "ineligible_account" or "unsupported_capability"
            ? JobStatus.Blocked : JobStatus.Failed;

    private async Task MonitorLease(string owner, Job lease, CancellationTokenSource lifetime)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                await using var scope = scopes.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<WorkspaceJobs>().HeartbeatAsync(owner, lease, lifetime.Token)) continue;
                await lifetime.CancelAsync();
                break;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            await lifetime.CancelAsync();
        }
    }

    private sealed class GuardedProvider(IServiceScopeFactory scopes, TimeProvider clock, string owner, Job lease,
        AiRoute route, AiDailyLimits limits, AnalysisInputV1? synthesisInput) : IAiProvider
    {
        public string Id => route.Provider.Id;
        public ValueTask<AiCapabilities> CapabilitiesAsync(CancellationToken ct) => route.Provider.CapabilitiesAsync(ct);

        internal static bool IsSelected(Workspace state, AiRoute route, bool research)
        {
            var id = research ? state.AiRoutes.Research : state.AiRoutes.Synthesis;
            return id == route.ConnectionId && state.AiConnections.Any(item => item.Id == id && item.Status == "active"
                && item.Provider == route.Provider.Id && item.Model == route.Model && item.BillingMode == route.BillingMode);
        }

        public async Task<AiReceipt> RunAsync(AiRequest request, CancellationToken ct)
        {
            bool research = synthesisInput is null;
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IWorkspaceStore>();
            bool paid = route.BillingMode is "api_usage" or "chatgpt_api_credits";
            Guid reservationId = Guid.NewGuid();
            int tokenAllowance = checked(1024 + request.TokenLimit + Encoding.UTF8.GetByteCount(request.PublicOrPrivateInput)
                + (request.OutputSchema is { } schema ? Encoding.UTF8.GetByteCount(schema.GetRawText()) : 0));
            string? blocked = await store.UpdateAsync<string?>(owner, state =>
            {
                var now = clock.GetUtcNow();
                if (!WorkspaceJobs.Owns(WorkspaceJobs.Get(state, lease.Id), lease, now))
                    return (state, "lease_lost");
                if (!IsSelected(state, route, research)) return (state, "connection_disabled");
                if (synthesisInput is not null && AnalysisSnapshots.HasPrivateContext(synthesisInput)
                    && !AnalysisSnapshots.IsCurrentContext(state, synthesisInput, now))
                    return (state, "portfolio_changed");
                var all = state.AiExecutions ?? [];
                if (all.Any(item => item.JobId == lease.Id && item.Research == research))
                    return (state, "outcome_unknown");
                var today = all.Where(item => item.StartedAt.UtcDateTime.Date == now.UtcDateTime.Date
                    || item.Status is "reserved" or "outcome_unknown").ToArray();
                var connection = today.Where(item => item.ConnectionId == route.ConnectionId).ToArray();
                if (all.Any(item => item.ConnectionId == route.ConnectionId && item.Receipt?.RetryAt > now))
                    return (state, "rate_limited");
                if (research && today.Count(item => item.Research) >= limits.ResearchRuns
                    || research && connection.Count(item => item.Research) >= route.ConnectionDailyResearch.GetValueOrDefault(limits.ResearchRuns)
                    || today.Sum(Tokens) + tokenAllowance > limits.Tokens
                    || connection.Sum(Tokens) + tokenAllowance > route.ConnectionDailyTokens.GetValueOrDefault(limits.Tokens))
                    return (state, "usage_limit");
                if (paid)
                {
                    if (route.ReservedUsd is not { Sign: > 0 } estimate || route.ConnectionDailyUsd is not { Sign: > 0 } connectionLimit
                        || limits.DailyUsd.Sign <= 0) return (state, "budget_not_configured");
                    if ((today.Aggregate(estimate, (sum, row) => sum + Charge(row))).CompareTo(limits.DailyUsd) > 0
                        || connection.Aggregate(estimate, (sum, row) => sum + Charge(row)).CompareTo(connectionLimit) > 0)
                        return (state, "budget_exhausted");
                }
                else if (route.BillingMode != "chatgpt_plan") return (state, "blocked_capability");
                // Save only the snapshot authorized for dispatch, in the same commit as its reservation.
                if (synthesisInput is not null) state = WorkspaceJobs.StoreAnalysisSnapshot(state, synthesisInput);
                var reservation = new AiRunEntry(reservationId, lease.Id, route.ConnectionId, route.Model, route.BillingMode,
                    research, now, tokenAllowance, paid ? route.ReservedUsd : null, "reserved");
                return (state with { AiExecutions = [.. all, reservation] }, null);
            }, ct);
            if (blocked is not null) return new(Id, null, "blocked", blocked, null, null, route.BillingMode, null, []);

            ct.ThrowIfCancellationRequested();
            AiReceipt receipt = await route.Provider.RunAsync(request, ct);
            var redacted = receipt with { Text = null, Tools = [], CostUsd = receipt.CostUsd is { Sign: >= 0 } ? receipt.CostUsd : null };
            await store.UpdateAsync(owner, state =>
            {
                return (state with { AiExecutions = state.AiExecutions!.Select(item => item.Id == reservationId
                    ? item with { Status = receipt.Status, Receipt = redacted } : item).ToArray() }, true);
            }, ct);
            return receipt;
        }

        private static long Tokens(AiRunEntry item) =>
            item.Receipt is { InputTokens: >= 0 and <= int.MaxValue, OutputTokens: >= 0 and <= int.MaxValue } receipt
                ? receipt.InputTokens!.Value + receipt.OutputTokens!.Value : item.TokenAllowance;

        private static ExactNumber Charge(AiRunEntry item) =>
            item.Receipt?.CostUsd ?? item.ReservedUsd ?? ExactNumber.Zero;
    }
}
