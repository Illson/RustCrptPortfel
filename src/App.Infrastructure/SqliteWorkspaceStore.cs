using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Portfel.Application;
using Portfel.Domain;

namespace Portfel.Infrastructure;

public sealed class PortfelDb(DbContextOptions<PortfelDb> options) : DbContext(options)
{
    internal DbSet<WorkspaceRow> Workspaces => Set<WorkspaceRow>();
    internal DbSet<JobRow> Jobs => Set<JobRow>();
    internal DbSet<PublicationRow> Publications => Set<PublicationRow>();
    internal DbSet<FeedVersionRow> FeedVersions => Set<FeedVersionRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var workspace = model.Entity<WorkspaceRow>();
        workspace.ToTable("workspaces");
        workspace.HasKey(row => row.OwnerId);
        workspace.Property(row => row.OwnerId).HasColumnName("owner_id");
        workspace.Property(row => row.Data).HasColumnName("data");

        var job = model.Entity<JobRow>();
        job.ToTable("jobs");
        job.HasKey(row => row.QueueOrder);
        job.HasIndex(row => new { row.OwnerId, row.Id }).IsUnique();
        job.HasIndex(row => new { row.OwnerId, row.IdempotencyKey }).IsUnique();
        job.HasIndex(row => new { row.OwnerId, row.QueueOrder });
        job.Property(row => row.QueueOrder).ValueGeneratedOnAdd().HasColumnName("queue_order");
        job.Property(row => row.OwnerId).HasColumnName("owner_id");
        job.Property(row => row.Id).IsRequired().HasColumnName("id");
        job.Property(row => row.IdempotencyKey).HasColumnName("idempotency_key");
        job.Property(row => row.Data).HasColumnName("data");
        job.HasOne<WorkspaceRow>().WithMany().HasForeignKey(row => row.OwnerId).IsRequired().OnDelete(DeleteBehavior.Cascade);

        var publication = model.Entity<PublicationRow>();
        publication.ToTable("feed_publications");
        publication.HasKey(row => new { row.OwnerId, row.PublishKey });
        publication.Property(row => row.OwnerId).HasColumnName("owner_id");
        publication.Property(row => row.PublishKey).HasColumnName("publish_key");
        publication.HasOne<WorkspaceRow>().WithMany().HasForeignKey(row => row.OwnerId).IsRequired().OnDelete(DeleteBehavior.Cascade);

        var version = model.Entity<FeedVersionRow>();
        version.ToTable("feed_versions");
        version.HasKey(row => new { row.OwnerId, row.PublishKey, row.Version });
        version.HasIndex(row => new { row.OwnerId, row.Id }).IsUnique();
        version.Property(row => row.OwnerId).HasColumnName("owner_id");
        version.Property(row => row.PublishKey).HasColumnName("publish_key");
        version.Property(row => row.Version).HasColumnName("version");
        version.Property(row => row.Id).IsRequired().HasColumnName("id");
        version.Property(row => row.Data).HasColumnName("data");
        version.HasOne<PublicationRow>().WithMany().HasForeignKey(row => new { row.OwnerId, row.PublishKey })
            .IsRequired().OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class WorkspaceRow
{
    public required string OwnerId { get; init; }
    public required string Data { get; set; }
}

internal sealed class JobRow
{
    public long QueueOrder { get; init; }
    public required string OwnerId { get; init; }
    public required string Id { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string Data { get; set; }
}

internal sealed class PublicationRow
{
    public required string OwnerId { get; init; }
    public required string PublishKey { get; init; }
}

internal sealed class FeedVersionRow
{
    public required string OwnerId { get; init; }
    public required string PublishKey { get; init; }
    public required int Version { get; init; }
    public required string Id { get; init; }
    public required string Data { get; set; }
}

public sealed class SqliteWorkspaceStore(PortfelDb db) : IWorkspaceStore
{
    public async Task<Workspace> ReadAsync(string ownerId, CancellationToken cancellationToken)
    {
        // One SQLite read snapshot: the feed must never come from a different commit than the job or portfolio.
        var row = await db.Database.SqlQuery<ReadSnapshot>($"""
            SELECT w.data AS RootJson,
                (SELECT json_group_array(json(j.data) ORDER BY j.queue_order) FROM jobs AS j WHERE j.owner_id = w.owner_id) AS JobsJson,
                (SELECT json_group_array(json(f.data) ORDER BY f.publish_key, f.version) FROM feed_versions AS f WHERE f.owner_id = w.owner_id) AS FeedJson
            FROM workspaces AS w WHERE w.owner_id = {ownerId}
            """).SingleOrDefaultAsync(cancellationToken);
        return row is null ? Workspace.Empty : WorkspaceJson.Decode<Workspace>(row.RootJson) with
        {
            Jobs = WorkspaceJson.Decode<Job[]>(row.JobsJson),
            Feed = WorkspaceJson.Decode<FeedItem[]>(row.FeedJson),
        };
    }

    public async Task<T> UpdateAsync<T>(string ownerId, Func<Workspace, (Workspace Next, T Result)> change,
        CancellationToken cancellationToken)
    {
        // Microsoft.Data.Sqlite acquires the writer lock before reading the current state.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            WorkspaceRow? root = await db.Workspaces.SingleOrDefaultAsync(row => row.OwnerId == ownerId, cancellationToken);
            List<JobRow> jobs = root is null ? [] : await db.Jobs.Where(row => row.OwnerId == ownerId)
                .OrderBy(row => row.QueueOrder).ToListAsync(cancellationToken);
            List<FeedVersionRow> feed = root is null ? [] : await db.FeedVersions.Where(row => row.OwnerId == ownerId)
                .OrderBy(row => row.PublishKey).ThenBy(row => row.Version).ToListAsync(cancellationToken);
            var previous = root is null ? Workspace.Empty : WorkspaceJson.Decode<Workspace>(root.Data) with
            {
                Jobs = jobs.Select(row => WorkspaceJson.Decode<Job>(row.Data)).ToArray(),
                Feed = feed.Select(row => WorkspaceJson.Decode<FeedItem>(row.Data)).ToArray(),
            };
            (Workspace next, T result) = change(previous);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(previous, next))
            {
                if (root is null)
                    db.Workspaces.Add(new() { OwnerId = ownerId, Data = EncodeRoot(next) });
                else if (previous with { Jobs = next.Jobs, Feed = next.Feed } != next)
                    root.Data = EncodeRoot(next);
                PersistJobs(ownerId, previous.Jobs, next.Jobs, jobs);
                PersistFeed(ownerId, previous.Feed, next.Feed, feed);
                await db.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally { db.ChangeTracker.Clear(); }
    }

    public async Task<bool> ReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            int tables = await db.Database.SqlQueryRaw<int>("""
                SELECT count(*) AS Value FROM sqlite_schema
                WHERE type = 'table' AND name IN ('workspaces', 'jobs', 'feed_publications', 'feed_versions')
                """).SingleAsync(cancellationToken);
            return tables == 4;
        }
        catch (SqliteException) { return false; }
    }

    private static string EncodeRoot(Workspace workspace) => WorkspaceJson.Encode(workspace with { Jobs = [], Feed = [] });

    private void PersistJobs(string owner, Job[] previous, Job[] next, List<JobRow> rows)
    {
        if (ReferenceEquals(previous, next)) return;
        var known = rows.ToDictionary(row => row.Id, StringComparer.Ordinal);
        var old = previous.ToDictionary(job => job.Id);
        foreach (Job job in next)
        {
            string id = job.Id.ToString("D");
            if (known.Remove(id, out JobRow? row))
            {
                if (row.IdempotencyKey != job.IdempotencyKey) throw new InvalidOperationException("Job key is immutable.");
                if (old[job.Id] != job) row.Data = WorkspaceJson.Encode(job);
            }
            else db.Jobs.Add(new() { OwnerId = owner, Id = id, IdempotencyKey = job.IdempotencyKey, Data = WorkspaceJson.Encode(job) });
        }
        if (known.Count != 0) throw new InvalidOperationException("Job history cannot be removed during a workspace update.");
    }

    private void PersistFeed(string owner, FeedItem[] previous, FeedItem[] next, List<FeedVersionRow> rows)
    {
        if (ReferenceEquals(previous, next)) return;
        var known = rows.ToDictionary(row => row.Id, StringComparer.Ordinal);
        var old = previous.ToDictionary(item => item.Id);
        var publications = rows.Select(row => row.PublishKey).ToHashSet(StringComparer.Ordinal);
        foreach (FeedItem item in next)
        {
            string id = item.Id.ToString("D");
            if (known.Remove(id, out FeedVersionRow? row))
            {
                if (row.PublishKey != item.PublishKey || row.Version != item.Version)
                    throw new InvalidOperationException("Publication identity is immutable.");
                if (old[item.Id] != item) row.Data = WorkspaceJson.Encode(item);
                continue;
            }
            if (publications.Add(item.PublishKey)) db.Publications.Add(new() { OwnerId = owner, PublishKey = item.PublishKey });
            db.FeedVersions.Add(new()
            {
                OwnerId = owner, PublishKey = item.PublishKey, Version = item.Version, Id = id, Data = WorkspaceJson.Encode(item),
            });
        }
        if (known.Count != 0) throw new InvalidOperationException("Publication history cannot be removed during a workspace update.");
    }

    private sealed record ReadSnapshot(string RootJson, string JobsJson, string FeedJson);
}

internal sealed class DatabaseStartup(IServiceScopeFactory scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortfelDb>();
        await db.Database.MigrateAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL", cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class WorkspaceJson
{
    private static readonly JsonSerializerOptions StorageOptions = new(PortfelJson.Options)
    {
        Converters = { new LegacyAnalysisConverter() },
    };

    internal static string Encode<T>(T value) => JsonSerializer.Serialize(value, PortfelJson.Options);

    internal static T Decode<T>(string json) => JsonSerializer.Deserialize<T>(json, StorageOptions)
        ?? throw new InvalidDataException("Состояние портфеля не читается.");

    private sealed class LegacyAnalysisConverter : JsonConverter<AnalysisResultV1>
    {
        public override AnalysisResultV1 Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var json = JsonDocument.ParseValue(ref reader);
            if (json.RootElement.TryGetProperty("schema_version", out JsonElement schema) && schema.GetString() == "v1")
            {
                var legacy = JsonNode.Parse(json.RootElement.GetRawText())!.AsObject();
                legacy["schema_version"] = AnalysisValidation.SchemaVersion;
                legacy["headline"] = legacy["title"]?.DeepClone();
                string? severity = legacy["importance"]?.GetValue<string>();
                legacy["severity"] = severity == "normal" ? "info" : severity;
                foreach (var section in legacy["sections"]!.AsArray())
                {
                    string? block = section?["block"]?.GetValue<string>();
                    if (block is "economy" or "development") section!["block"] = block == "economy" ? "economics" : "project";
                }
                return legacy.Deserialize<AnalysisResultV1>(PortfelJson.Options)!;
            }
            return json.RootElement.Deserialize<AnalysisResultV1>(PortfelJson.Options)!;
        }

        public override void Write(Utf8JsonWriter writer, AnalysisResultV1 value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value, PortfelJson.Options);
    }
}
