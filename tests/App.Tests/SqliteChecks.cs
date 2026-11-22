using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Portfel.Application;
using Portfel.Domain;
using Portfel.Infrastructure;
using static Checks;

internal static class SqliteChecks
{
    private const string InitialMigration = "20261122000100_InitialWorkspace";
    private const int ConstraintPrimaryKey = 1555;
    private const int ConstraintTrigger = 1811;
    private const int ConstraintUnique = 2067;

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
        string database = Path.Combine(Path.GetTempPath(), $"queue-{Guid.NewGuid():N}.db");
        var connection = new SqliteConnectionStringBuilder { DataSource = database, Pooling = false, ForeignKeys = true, DefaultTimeout = 10 };
        var options = new DbContextOptionsBuilder<PortfelDb>().UseSqlite(connection.ToString()).Options;
        var state = DemoPortfolio.Sync(Workspace.Empty, now);
        var (snapshot, analysis) = DemoPortfolio.Analyze(state, "5426", now);
        var published = WorkspaceJobs.Publish(state, owner, snapshot, analysis, now);
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(state.Settings.Timezone));
        string key = FormattableString.Invariant($"{owner}:{DemoPortfolio.PortfolioId}:5426:{local:yyyy-MM-dd}:daily_analysis");
        string headline = analysis.Headline;
        Job oldJob = new(Guid.NewGuid(), JobKind.PortfolioSync, null, "legacy-submit", "request-hash", JobStatus.Succeeded, now);
        Job nextJob = oldJob with { Id = Guid.NewGuid(), IdempotencyKey = "legacy-next", Status = JobStatus.Queued };

        try
        {
            await using (var db = new PortfelDb(options))
            {
                var store = new SqliteWorkspaceStore(db);
                Require(!await store.ReadyAsync(default), "Opening an empty SQLite file is not readiness");
                await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
                Require(!await store.ReadyAsync(default), "Old schema is not ready before migration");
                var oldItem = published.Feed[0] with { PublishKey = key, Read = true, SnapshotId = null };
                var legacy = JsonSerializer.SerializeToNode(state with { Jobs = [oldJob, nextJob], Feed = [oldItem] }, PortfelJson.Options)!;
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
                    """), ConstraintUnique);
                await ExpectConstraintAsync(() => db.Database.ExecuteSqlAsync($"""
                    INSERT INTO feed_publications (owner_id, publish_key) VALUES ({owner}, {key})
                    """), ConstraintPrimaryKey);
                await ExpectConstraintAsync(() => db.Database.ExecuteSqlAsync($"""
                    INSERT INTO feed_versions (owner_id, publish_key, version, id, data)
                    SELECT owner_id, publish_key, version, 'test-duplicate', data FROM feed_versions WHERE owner_id = {owner}
                    """), ConstraintPrimaryKey);
                await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
                await db.Database.MigrateAsync();
                Require((await store.ReadAsync(owner, default)).Feed.Single().Id == loaded.Feed.Single().Id,
                    "Downgrade and upgrade keep legacy feed");

                var later = now.AddMinutes(1);
                var (newSnapshot, newResult) = DemoPortfolio.Analyze(loaded, "5426", later);
                await store.UpdateAsync(owner, current => (WorkspaceJobs.Publish(current, owner, newSnapshot, newResult, later), true), default);
                var updated = await store.ReadAsync(owner, default);
                Require(updated.Feed is
                    [{ Read: true }, { PublishKey: var updatedKey, Version: 2, SupersedesId: var superseded }]
                    && updatedKey == key && superseded == oldItem.Id
                    && PortfolioQueries.Feed(updated, null, null, null, null, null, null, null).Items.Single().Version == 2,
                    "The visible feed uses the new version under the legacy key, preserving history");
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
            var leases = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
            {
                await Task.Yield();
                await using var db = new PortfelDb(options);
                return await new SqliteWorkspaceStore(db).ClaimAsync(other, new FixedClock(now), default);
            }));
            var sync = leases.Single(job => job is not null)!;
            await using (var db = new PortfelDb(options))
            {
                var store = new SqliteWorkspaceStore(db);
                var jobs = new WorkspaceJobs(store, new FixedClock(now));
                Require(sync.Id == receipts[0].JobId && await jobs.ExecuteDemoAsync(other, sync, default), "Only one worker claims the queued sync");
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

        await VerifyStartupRecoveryAsync(now);
    }

    private static async Task VerifyStartupRecoveryAsync(DateTimeOffset now)
    {
        const string owner = "test:interrupted-upgrade";
        string database = Path.Combine(Path.GetTempPath(), $"upgrade-{Guid.NewGuid():N}.db");
        var connection = new SqliteConnectionStringBuilder { DataSource = database, Pooling = false, ForeignKeys = true };
        var options = new DbContextOptionsBuilder<PortfelDb>().UseSqlite(connection.ToString()).Options;
        Job job = new(Guid.NewGuid(), JobKind.PortfolioSync, null, "old-job", "hash", JobStatus.Queued, now);
        string original = JsonSerializer.Serialize(Workspace.Empty with { Jobs = [job] }, PortfelJson.Options);
        using var api = BuildHost();
        using var worker = BuildHost();

        try
        {
            await using (var db = new PortfelDb(options))
            {
                await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
                await db.Database.ExecuteSqlAsync($"INSERT INTO workspaces (owner_id, data) VALUES ({owner}, {original})");
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "__EFMigrationsLock" ("Id", "Timestamp") VALUES (1, '2026-11-22 08:00:00');
                    CREATE TRIGGER fail_test_upgrade BEFORE UPDATE ON workspaces
                    BEGIN SELECT RAISE(ABORT, 'test upgrade failed'); END;
                    """);
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using (var failed = BuildHost())
                await ExpectConstraintAsync(() => failed.StartAsync(timeout.Token), ConstraintTrigger);

            await using (var db = new PortfelDb(options))
            {
                Require((await db.Database.GetAppliedMigrationsAsync()).SequenceEqual([InitialMigration]),
                    "Failed migration leaves migration history unchanged");
                Require(!await new SqliteWorkspaceStore(db).ReadyAsync(default)
                    && await db.Database.SqlQueryRaw<string>("SELECT data AS Value FROM workspaces").SingleAsync() == original,
                    "Failed migration rolls back tables and the original workspace");
                await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_test_upgrade");
            }

            await Task.WhenAll(Task.Run(() => api.StartAsync(timeout.Token)), Task.Run(() => worker.StartAsync(timeout.Token)));
            using (var scope = api.Services.CreateScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IWorkspaceStore>();
                Require(await store.ReadyAsync(default) && (await store.ReadAsync(owner, default)).Jobs.Single().Id == job.Id,
                    "Concurrent startup retries the migration once despite the legacy lock row");
                var jobs = scope.ServiceProvider.GetRequiredService<WorkspaceJobs>();
                Job? first = await jobs.ClaimAsync(owner, default);
                Require(first?.Id == job.Id, "Claims a migrated queue entry");
                await jobs.CancelAsync(owner, job.Id, default);
                // A transaction in another process holds the writer lock. Idle polling needs only a WAL read.
                await using var blocker = new PortfelDb(options);
                await using var held = await blocker.Database.BeginTransactionAsync();
                var timed = new DbContextOptionsBuilder<PortfelDb>().UseSqlite(
                    new SqliteConnectionStringBuilder(connection.ToString()) { DefaultTimeout = 1 }.ToString()).Options;
                await using var reader = new PortfelDb(timed);
                Require(await new SqliteWorkspaceStore(reader).ClaimAsync(owner, TimeProvider.System, default) is null,
                    "Idle queue polling succeeds while another SQLite writer holds the lock");
            }
            await Task.WhenAll(api.StopAsync(timeout.Token), worker.StopAsync(timeout.Token));
            Console.WriteLine("PASS SQLite startup recovery after a failed migration and concurrent restart");
        }
        finally
        {
            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PortfelDb>();
            SqliteConnection.ClearPool((SqliteConnection)db.Database.GetDbConnection());
            File.Delete(database);
            File.Delete(database + "-wal");
            File.Delete(database + "-shm");
        }

        IHost BuildHost()
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
            builder.Configuration["Storage:DatabasePath"] = database;
            builder.Logging.ClearProviders();
            builder.Services.AddPortfel(builder.Configuration, builder.Environment);
            return builder.Build();
        }
    }

    private static async Task ExpectConstraintAsync(Func<Task> write, int extendedCode)
    {
        try { await write(); }
        catch (SqliteException error) when (error.SqliteExtendedErrorCode == extendedCode) { return; }
        throw new InvalidOperationException($"Expected SQLite constraint failure {extendedCode}");
    }
}
