using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Portfel.Application;
using Portfel.Domain;
using Portfel.Infrastructure;

string database = Path.Combine(Path.GetTempPath(), $"portfel-{Guid.NewGuid():N}.db");
var connection = new SqliteConnectionStringBuilder { DataSource = database, Pooling = false, ForeignKeys = true };
var options = new DbContextOptionsBuilder<PortfelDb>().UseSqlite(connection.ToString()).Options;
var now = new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);
const string owner = DemoPortfolio.Owner;

try
{
    Guid jobId;
    await using (var db = new PortfelDb(options))
    {
        SqliteChecks.VerifyMigrationModel(db);
        await db.Database.MigrateAsync();
        var store = new SqliteWorkspaceStore(db);
        await store.UpdateAsync(owner, state => (DemoPortfolio.Sync(state, now), true), default);
        var jobs = new WorkspaceJobs(store, new FixedClock(now));
        jobId = (await jobs.QueueAsync(owner, JobKind.PortfolioSync, null, "test-key", true, default)).JobId;
    }

    await using (var db = new PortfelDb(options))
    {
        var store = new SqliteWorkspaceStore(db);
        var jobs = new WorkspaceJobs(store, new FixedClock(now));
        Check((await jobs.QueueAsync(owner, JobKind.PortfolioSync, null, "test-key", true, default)).JobId == jobId, "Restart retains idempotency");
        var state = await store.ReadAsync(owner, default);
        Check(state.Current!.Positions.Single(position => position.AssetId == "1").Quantity.ToString() == "0.1250000000000000000000000000042", "SQLite preserves more than 29 digits");
        Check((await store.ReadAsync("other-owner", default)).Current is null, "Owner isolation");
        await jobs.CancelAsync(owner, jobId, default);
        Check(await jobs.ClaimAsync(owner, default) is null, "Cancellation prevents execution");
        try
        {
            await store.UpdateAsync<string>(owner, _ => throw new InvalidOperationException("injected"), default);
            throw new Exception("Expected rollback");
        }
        catch (InvalidOperationException exception) when (exception.Message == "injected") { }
        Check((await store.ReadAsync(owner, default)).Current!.Hash == state.Current.Hash, "Rollback preserves previous snapshot");

        var (snapshot, result) = DemoPortfolio.Analyze(state, "5426", now);
        Check(AnalysisValidation.Validate(snapshot, result) is null, "Fixture result validates");
        result.Sections[0] = result.Sections[0] with { EvidenceIds = ["unknown"] };
        Check(AnalysisValidation.Validate(snapshot, result) == "invalid_section", "Unknown evidence blocks publication");
        var sol = state.Assets.Single(asset => asset.Asset.Id == "5426");
        Check(PortfolioQueries.Economy(sol, now).Select(series => series.Scope).Distinct().Count() == 2, "Network and app flows are separate");
        Check(PortfolioQueries.Supply(sol, now).Windows.Single(window => window.Days == 365).UnlocksPct == ExactNumber.Zero, "Issuance is separate from unlocks");
        var sync = new PortfolioSync(store, new FixedClock(now.AddMinutes(3)));
        PortfolioCheck failed = await sync.FetchAsync(owner, DemoPortfolio.PortfolioId, new SyntheticPages(fail: true, now), default);
        Check(failed.Status == "rate_limited" && (await store.ReadAsync(owner, default)).Current!.Hash == state.Current.Hash,
            "Incomplete paginated CMC-like response cannot replace the previous portfolio");
        PortfolioCheck complete = await sync.FetchAsync(owner, DemoPortfolio.PortfolioId, new SyntheticPages(fail: false, now), default);
        Check(complete.Status == "ok" && (await store.ReadAsync(owner, default)).Current!.Positions.Length == 2,
            "All pages are accepted together");
        var good = (await store.ReadAsync(owner, default)).Current!;
        foreach (Exception error in new Exception[] { new HttpRequestException(), new IOException(), new TimeoutException(), new TaskCanceledException() })
        {
            var check = await sync.FetchAsync(owner, DemoPortfolio.PortfolioId, new SyntheticPages(false, now, () => throw error), default);
            var after = await store.ReadAsync(owner, default);
            Check(check.Status == "provider_unavailable" && after.LatestCheck == check
                && after.Current!.Hash == good.Hash && !PortfolioQueries.CanPersonalize(after, now.AddMinutes(3)),
                "A second-page transport failure preserves holdings and disables personalization");
        }
        await sync.FetchAsync(owner, DemoPortfolio.PortfolioId, new SyntheticPages(false, now), default);
        using var cancel = new CancellationTokenSource();
        try
        {
            await sync.FetchAsync(owner, DemoPortfolio.PortfolioId, new SyntheticPages(false, now, () =>
            {
                cancel.Cancel();
                cancel.Token.ThrowIfCancellationRequested();
            }), cancel.Token);
            throw new Exception("Expected caller cancellation");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        Check((await store.ReadAsync(owner, default)).LatestCheck?.Status == "ok", "Caller cancellation is not a source failure");
    }
    Console.WriteLine("PASS SQLite migration, restart, idempotency, precision, isolation, cancellation, rollback and metrics");
    DomainChecks.Run(now);
    await ProviderChecks.RunAsync(now);
    await VerifiedJobChecks.RunAsync(options, now);
    await SqliteChecks.RunAsync(now);
}
finally
{
    File.Delete(database);
    File.Delete(database + "-wal");
    File.Delete(database + "-shm");
}

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
}

sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

sealed class SyntheticPages(bool fail, DateTimeOffset now, Action? onSecondPage = null) : IReadOnlyPortfolioSource
{
    public Task<PortfolioPage> ReadPageAsync(string id, string? cursor, CancellationToken ct)
    {
        if (cursor is not null) onSecondPage?.Invoke();
        return Task.FromResult(cursor is null
            ? new PortfolioPage(id, "ok", "v1", "same-version", now, [new("5426", ExactNumber.From(9))], false, "second")
            : new PortfolioPage(id, fail ? "rate_limited" : "ok", "v1", "same-version", now, [new("1", ExactNumber.From(2))], true, null));
    }
}
