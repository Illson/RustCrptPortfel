using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Portfel.Application;
using Portfel.Domain;
using Portfel.Infrastructure;
using static Checks;

internal static class VerifiedJobChecks
{
    internal static async Task RunAsync(DbContextOptions<PortfelDb> options, DateTimeOffset now)
    {
        var clock = new MutableClock(now);
        var services = new ServiceCollection().AddScoped(_ => new PortfelDb(options))
            .AddScoped<IWorkspaceStore, SqliteWorkspaceStore>().AddScoped<WorkspaceJobs>().AddSingleton<TimeProvider>(clock);
        await using var provider = services.BuildServiceProvider();
        var runner = new VerifiedAnalysisJob(provider.GetRequiredService<IServiceScopeFactory>(), clock);
        Guid researchId = Guid.NewGuid(), synthesisId = Guid.NewGuid();
        var limits = new AiDailyLimits(ExactNumber.From(100), 20, 2_000_000);
        int searchCount = 0, writerCount = 0;
        AiReceipt Search() => new("openai", "test-research", "completed", null, 10, 10, "api_usage", null,
            [new("web_search_call", "test-tool", ["Synthetic release"], [new(ProviderChecks.Article, "Обновление")])]);
        AiReceipt Synthesis(AiRequest request)
        {
            writerCount++;
            using var json = JsonDocument.Parse(request.PublicOrPrivateInput);
            var root = json.RootElement;
            var blocks = root.GetProperty("blocks").Deserialize<AnalysisCoverage[]>(PortfelJson.Options)!;
            var result = ProviderChecks.Result(root.GetProperty("asset_id").GetString()!, root.GetProperty("snapshot_hash").GetString()!, blocks);
            return new("anthropic", "test-synthesis", "completed", null, 10, 20, "api_usage", JsonSerializer.Serialize(result, PortfelJson.Options), []);
        }
        var offsite = new SyntheticProvider("openai", now, true, (_, _) => { searchCount++; return Task.FromResult(Search()); });
        var writer = new SyntheticProvider("anthropic", now, false, (request, _) => Task.FromResult(Synthesis(request)));
        var research = new AiRoute(researchId, "synthetic", offsite, "api_usage", ExactNumber.From(1), ExactNumber.From(30));
        var synthesis = new AiRoute(synthesisId, "synthetic", writer, "api_usage", ExactNumber.From(1), ExactNumber.From(30));

        async Task<Job> Seed(string owner, string asset = "5426")
        {
            await using var scope = provider.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IWorkspaceStore>();
            await store.UpdateAsync(owner, workspace => (DemoPortfolio.Sync(workspace, clock.GetUtcNow()) with
            {
                AiConnections = [new(researchId, "openai", "Test research", "synthetic", "api_key", "api_usage", "active"),
                    new(synthesisId, "anthropic", "Test synthesis", "synthetic", "api_key", "api_usage", "active")],
                AiRoutes = new(researchId, synthesisId, false),
            }, true), default);
            var jobs = scope.ServiceProvider.GetRequiredService<WorkspaceJobs>();
            await jobs.QueueAsync(owner, JobKind.AssetAnalysis, asset, Guid.NewGuid().ToString("N"), true, default);
            return (await jobs.ClaimAsync(owner, default))!;
        }

        async Task<Workspace> Read(string owner)
        {
            await using var scope = provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IWorkspaceStore>().ReadAsync(owner, default);
        }

        const string done = "test:verified-analysis";
        var first = await Seed(done);
        await runner.RunAsync(done, first, research, synthesis, limits, default);
        var completed = await Read(done);
        Require(completed.Feed is [{ Personalization: "current", Demo: false }] && completed.Jobs.Single().Status == JobStatus.Succeeded &&
            completed.AiExecutions!.Length == 2 && completed.AnalysisSnapshots!.Single().ContentHash == completed.Feed[0].Analysis.SnapshotHash,
            "Full guarded workflow persists both calls and exact final snapshot");
        Require(PortfolioQueries.WithCurrentPersonalization(completed, completed.Feed[0], now.AddHours(2)).Personalization == "historical",
            "Old personalization is not displayed as current");
        var revoked = completed with { LatestCheck = new(now, "permission_denied") };
        Require(AnalysisSnapshots.ForAsset(revoked, "5426", now) is { PortfolioContext: null, SourcePortfolioId: null, PortfolioHash: null }
            && PortfolioQueries.WithCurrentPersonalization(revoked, completed.Feed[0], now).Personalization == "historical",
            "Loss of portfolio access disables personalization even when the previous snapshot is recent");

        foreach (var reason in new[] { "revoked", "profile", "holdings" })
        {
            string owner = "test:before-synthesis:" + reason;
            var lease = await Seed(owner);
            await using var edit = provider.CreateAsyncScope();
            var store = edit.ServiceProvider.GetRequiredService<IWorkspaceStore>();
            await store.UpdateAsync(owner, s => (s with { Settings = s.Settings with { InvestmentProfile = "synthetic profile" } }, true), default);
            var changingResearch = new SyntheticProvider("openai", now, true, async (_, ct) =>
            {
                await store.UpdateAsync(owner, s => (reason switch
                {
                    "revoked" => s with { LatestCheck = new(now, "permission_denied") },
                    "profile" => s with { Settings = s.Settings with { InvestmentProfile = null } },
                    _ => s.RecordSync(new(s.Current!.SourcePortfolioId, now, now, true, "v1", "ok", [])),
                }, true), ct);
                return Search();
            });
            int calls = writerCount;
            await runner.RunAsync(owner, lease, research with { Provider = changingResearch }, synthesis, limits, default);
            var denied = await Read(owner);
            Require(writerCount == calls && denied.AiExecutions is [{ Research: true }] && denied.Feed.Length == 0
                && denied.AnalysisSnapshots is null && denied.Jobs.Single().BlockReason == "portfolio_changed",
                $"No synthesis dispatch, private snapshot or reservation after {reason}");
        }

        const string changed = "test:position-changed";
        var changingLease = await Seed(changed);
        var changingProvider = new SyntheticProvider("anthropic", now, false, async (request, ct) =>
        {
            await using var scope = provider.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IWorkspaceStore>();
            await store.UpdateAsync(changed, state => (state.RecordSync(new(state.Current!.SourcePortfolioId, now, now, true, "v1", "ok",
                state.Current.Positions.Where(item => item.AssetId != changingLease.AssetId).ToArray())), true), ct);
            return Synthesis(request);
        });
        await runner.RunAsync(changed, changingLease, research, synthesis with { Provider = changingProvider }, limits, default);
        var updated = await Read(changed);
        Require(updated.Feed.Length == 0 && updated.Jobs.Single().BlockReason == "portfolio_changed", "Changed positions fence stale personalization");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitingProvider = new SyntheticProvider("openai", now, true, async (_, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Search();
        });
        const string budget = "test:budget";
        var paidLease = await Seed(budget);
        var limited = limits with { DailyUsd = ExactNumber.From(1) };
        Task pending = runner.RunAsync(budget, paidLease, research with { Provider = waitingProvider }, synthesis, limited, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var competing = await Seed(budget, "1");
        int before = searchCount;
        await runner.RunAsync(budget, competing, research, synthesis, limited, default);
        var afterBlocked = await Read(budget);
        Require(afterBlocked.Jobs.Single(item => item.Id == competing.Id).BlockReason == "budget_exhausted" && searchCount == before,
            "Parallel reservation blocks the second outbound request");
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<WorkspaceJobs>().CancelAsync(budget, paidLease.Id, default);
        release.TrySetResult();
        await pending;
        var cancelled = await Read(budget);
        Require(cancelled.Jobs.Single(item => item.Id == paidLease.Id).Status == JobStatus.Cancelled && cancelled.Feed.Length == 0,
            "Late response cannot post after cancellation");

        const string unknown = "test:unknown";
        var unknownLease = await Seed(unknown);
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IWorkspaceStore>().UpdateAsync(unknown, state =>
                (state with { AiExecutions = [new(Guid.NewGuid(), unknownLease.Id, researchId, "synthetic", "api_usage", true, now, 100,
                    ExactNumber.From(1), "reserved")] }, true), default);
        clock.Now = now.AddMinutes(1);
        await using (var scope = provider.CreateAsyncScope())
            Require(await scope.ServiceProvider.GetRequiredService<WorkspaceJobs>().ClaimAsync(unknown, default) is null, "Crash recovery does not resend outstanding paid requests");
        var crashed = await Read(unknown);
        Require(crashed.Jobs.Single().BlockReason == "outcome_unknown" && crashed.AiExecutions!.Single().Status == "outcome_unknown",
            "Unconfirmed billing remains reserved");
        Require(writerCount == 2, "No synthesis after blocked or cancelled research");
        Console.WriteLine("PASS guarded analysis workflow, portfolio race, concurrent budget reservations, cancel fence and no blind retry");
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class SyntheticProvider(string id, DateTimeOffset now, bool research,
        Func<AiRequest, CancellationToken, Task<AiReceipt>> run) : IAiProvider
    {
        public string Id => id;
        public ValueTask<AiCapabilities> CapabilitiesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AiCapabilities(research, true, false, true, false, "unknown", now, Model: "synthetic"));
        public Task<AiReceipt> RunAsync(AiRequest request, CancellationToken cancellationToken) => run(request, cancellationToken);
    }
}
