using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Portfel.Application;
using Portfel.Domain;
using Portfel.Infrastructure;

string database = Path.Combine(Path.GetTempPath(), $"portfel-{Guid.NewGuid():N}.db");
var connection = new SqliteConnectionStringBuilder { DataSource = database, Pooling = false };
var options = new DbContextOptionsBuilder<PortfelDb>().UseSqlite(connection.ToString()).Options;
var now = new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);
const string owner = DemoPortfolio.Owner;

try
{
    Guid jobId;
    await using (var db = new PortfelDb(options))
    {
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
    }
    Console.WriteLine("PASS SQLite migration, restart, idempotency, precision, isolation, cancellation, rollback and metrics");
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
