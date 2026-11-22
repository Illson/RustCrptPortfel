using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Portfel.Application;
using Portfel.Domain;
using Portfel.Infrastructure;
using static Checks;

internal static class SqliteChecks
{
    internal static void VerifyMigrationModel(PortfelDb db)
    {
        if (!db.Database.HasPendingModelChanges()) return;
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
        var old = db.GetService<IModelRuntimeInitializer>().Initialize(snapshot, designTime: true).GetRelationalModel();
        var current = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var operations = db.GetService<IMigrationsModelDiffer>().GetDifferences(old, current).Select(operation => operation switch
        {
            AlterColumnOperation column => $"alter {column.Table}.{column.Name}: nullable {column.OldColumn.IsNullable} -> {column.IsNullable}",
            AddForeignKeyOperation fk => $"add foreign key {fk.Name}",
            DropForeignKeyOperation fk => $"drop foreign key {fk.Name}",
            CreateIndexOperation index => $"create index {index.Name}",
            DropIndexOperation index => $"drop index {index.Name}",
            _ => operation.GetType().Name,
        });
        throw new InvalidOperationException("Pending SQLite schema changes: " + string.Join(", ", operations));
    }

    internal static async Task RunAsync(DateTimeOffset now)
    {
        const string owner = "test:sqlite-legacy";
        const string other = "test:sqlite-concurrent";
        const string initial = "20261122000100_InitialWorkspace";
        string database = Path.Combine(Path.GetTempPath(), $"queue-{Guid.NewGuid():N}.db");
        var connection = new SqliteConnectionStringBuilder { DataSource = database, Pooling = false, ForeignKeys = true, DefaultTimeout = 10 };
        var options = new DbContextOptionsBuilder<PortfelDb>().UseSqlite(connection.ToString()).Options;
        var state = DemoPortfolio.Sync(Workspace.Empty, now);
        var (snapshot, analysis) = DemoPortfolio.Analyze(state, "5426", now);
        var published = WorkspaceJobs.Publish(state, owner, snapshot, analysis, now);
        string key = published.Feed[0].PublishKey;
        string headline = analysis.Headline;
        Job oldJob = new(Guid.NewGuid(), JobKind.PortfolioSync, null, "legacy-submit", "request-hash", JobStatus.Succeeded, now);
        Job nextJob = oldJob with { Id = Guid.NewGuid(), IdempotencyKey = "legacy-next", Status = JobStatus.Queued };

        try
        {
            await using (var db = new PortfelDb(options))
            {
                var store = new SqliteWorkspaceStore(db);
                Require(!await store.ReadyAsync(default), "Opening an empty SQLite file is not readiness");
                await db.GetService<IMigrator>().MigrateAsync(initial);
                Require(!await store.ReadyAsync(default), "Old schema is not ready before migration");
                var legacy = JsonSerializer.SerializeToNode(published with { Jobs = [oldJob, nextJob], Feed = [published.Feed[0] with { Read = true }] },
                    PortfelJson.Options)!;
                var card = legacy["feed"]![0]!["analysis"]!;
                card["title"] = card["headline"]!.DeepClone();
                card.AsObject().Remove("headline");
                card["importance"] = "normal";
                card.AsObject().Remove("severity");
                card["schema_version"] = "v1";
                card["sections"]![0]!["block"] = "economy";
                card["sections"]![3]!["block"] = "development";
                await db.Database.ExecuteSqlAsync($"INSERT INTO workspaces (owner_id, data) VALUES ({owner}, {legacy.ToJsonString()})");
                await db.Database.MigrateAsync();
                Require(await store.ReadyAsync(default), "Migrated schema is ready");
                var loaded = await store.ReadAsync(owner, default);
                Require(loaded.Jobs.Select(row => row.Id).SequenceEqual([oldJob.Id, nextJob.Id]), "Legacy job order survives SQLite migration");
                Require(PortfolioQueries.Feed(loaded, null, null, null, null, null, null, null).Updating,
                    "The feed indicates pending analysis even before a new card exists");
                Require(loaded.Feed.Single() is { Read: true, PublishedAt: var date, Analysis: { Severity: "info" } item }
                    && item.Headline == headline && item.Sections[3].Block == AnalysisBlock.Project && date == now, "Legacy feed survives SQLite migration");
                Require(loaded.Current!.Positions.Single(item => item.AssetId == "1").Quantity.ToString() == "0.1250000000000000000000000000042",
                    "SQLite migration preserves exact quantities");
                await ExpectConstraintAsync(() => db.Database.ExecuteSqlAsync($"""
                    INSERT INTO jobs (owner_id, id, idempotency_key, data)
                    SELECT owner_id, 'test-duplicate', idempotency_key, data FROM jobs WHERE owner_id = {owner} AND id = {oldJob.Id.ToString("D")}
                    """));
                await ExpectConstraintAsync(() => db.Database.ExecuteSqlAsync($"""
                    INSERT INTO feed_publications (owner_id, publish_key) VALUES ({owner}, {key})
                    """));
                await db.GetService<IMigrator>().MigrateAsync(initial);
                await db.Database.MigrateAsync();
                Require((await store.ReadAsync(owner, default)).Feed.Single().Id == loaded.Feed.Single().Id,
                    "Downgrade and upgrade keep legacy feed");
            }

            async Task<JobReceipt> QueueAsync()
            {
                await Task.Yield(); // Launch independent caller continuations despite the SQLite driver's synchronous I/O.
                await using var db = new PortfelDb(options);
                return await new WorkspaceJobs(new SqliteWorkspaceStore(db), new FixedClock(now))
                    .QueueAsync(other, JobKind.PortfolioSync, null, "concurrent", true, default);
            }

            var receipts = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => QueueAsync()));
            Require(receipts.Select(row => row.JobId).Distinct().Count() == 1, "SQLite concurrent callers get one job");
            await using (var db = new PortfelDb(options))
            {
                var store = new SqliteWorkspaceStore(db);
                var jobs = new WorkspaceJobs(store, new FixedClock(now));
                var sync = await jobs.ClaimAsync(other, default);
                Require(sync?.Id == receipts[0].JobId && await jobs.ExecuteDemoAsync(other, sync, default), "Executes the shared queued sync");
                var run = await jobs.ClaimAsync(other, default) ?? throw new InvalidOperationException("Expected analysis job");
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TRIGGER fail_test_publication BEFORE INSERT ON feed_versions
                    BEGIN SELECT RAISE(ABORT, 'test write failed'); END;
                    """);
                try
                {
                    await jobs.ExecuteDemoAsync(other, run, default);
                    throw new InvalidOperationException("Expected publication failure");
                }
                catch (DbUpdateException error) when (error.InnerException is SqliteException { SqliteErrorCode: 19 }) { }
                var afterFailure = await store.ReadAsync(other, default);
                Require(afterFailure.Feed.Length == 0 && afterFailure.AnalysisSnapshots is null
                    && WorkspaceJobs.Get(afterFailure, run.Id).Status == JobStatus.Running, "Failed insert rolls back the whole publication");
                await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_test_publication");
                await using var restarted = new PortfelDb(options);
                var clock = new FixedClock(now.Add(WorkspaceJobs.LeaseDuration));
                var recovered = new WorkspaceJobs(new SqliteWorkspaceStore(restarted), clock);
                Job claimed = await recovered.ClaimAsync(other, default) ?? throw new InvalidOperationException("Expected expired lease recovery");
                Require(run.Id == claimed.Id && run.Lease != claimed.Lease && !await recovered.ExecuteDemoAsync(other, run, default)
                    && await recovered.ExecuteDemoAsync(other, claimed, default), "Only the recovered lease can publish");
            }
            await using (var db = new PortfelDb(options))
            {
                var store = new SqliteWorkspaceStore(db);
                var reloaded = await store.ReadAsync(other, default);
                Require(reloaded.Feed.Length == 1 && reloaded.AnalysisSnapshots?.Length == 1 && reloaded.Jobs.Count(row => row.Status == JobStatus.Succeeded) == 2,
                    "Atomic publication survives restart");
                Require((await store.ReadAsync(owner + ":absent", default)).Jobs.Length == 0, "Owner isolation remains intact");
            }
            Console.WriteLine("PASS SQLite schema upgrade, duplicate constraints, concurrent enqueue, atomic rollback, lease recovery and restart");
        }
        finally
        {
            File.Delete(database);
            File.Delete(database + "-wal");
            File.Delete(database + "-shm");
        }
    }

    private static async Task ExpectConstraintAsync(Func<Task<int>> insert)
    {
        try { await insert(); }
        catch (SqliteException error) when (error.SqliteErrorCode == 19) { return; }
        throw new InvalidOperationException("SQLite allowed a duplicate identity");
    }
}
