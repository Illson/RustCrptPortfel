using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Portfel.Infrastructure.Migrations;

[DbContext(typeof(PortfelDb))]
[Migration("20261122000300_PersistentQueue")]
public sealed class PersistentQueue : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        migration.CreateTable("jobs", table => new
        {
            queue_order = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
            owner_id = table.Column<string>(type: "TEXT", nullable: false),
            id = table.Column<string>(type: "TEXT", nullable: false),
            idempotency_key = table.Column<string>(type: "TEXT", nullable: false),
            data = table.Column<string>(type: "TEXT", nullable: false),
        }, constraints: table =>
        {
            table.PrimaryKey("PK_jobs", row => row.queue_order);
            table.ForeignKey("FK_jobs_workspaces_owner_id", row => row.owner_id, "workspaces", "owner_id", onDelete: ReferentialAction.Cascade);
        });
        migration.CreateIndex("IX_jobs_owner_id_id", "jobs", new[] { "owner_id", "id" }, unique: true);
        migration.CreateIndex("IX_jobs_owner_id_idempotency_key", "jobs", new[] { "owner_id", "idempotency_key" }, unique: true);
        migration.CreateIndex("IX_jobs_owner_id_queue_order", "jobs", new[] { "owner_id", "queue_order" });

        migration.CreateTable("feed_publications", table => new
        {
            owner_id = table.Column<string>(type: "TEXT", nullable: false),
            publish_key = table.Column<string>(type: "TEXT", nullable: false),
        }, constraints: table =>
        {
            table.PrimaryKey("PK_feed_publications", row => new { row.owner_id, row.publish_key });
            table.ForeignKey("FK_feed_publications_workspaces_owner_id", row => row.owner_id, "workspaces", "owner_id", onDelete: ReferentialAction.Cascade);
        });

        migration.CreateTable("feed_versions", table => new
        {
            owner_id = table.Column<string>(type: "TEXT", nullable: false),
            publish_key = table.Column<string>(type: "TEXT", nullable: false),
            version = table.Column<int>(type: "INTEGER", nullable: false),
            id = table.Column<string>(type: "TEXT", nullable: false),
            data = table.Column<string>(type: "TEXT", nullable: false),
        }, constraints: table =>
        {
            table.PrimaryKey("PK_feed_versions", row => new { row.owner_id, row.publish_key, row.version });
            table.ForeignKey("FK_feed_versions_feed_publications_owner_id_publish_key", row => new { row.owner_id, row.publish_key },
                "feed_publications", new[] { "owner_id", "publish_key" }, onDelete: ReferentialAction.Cascade);
        });
        migration.CreateIndex("IX_feed_versions_owner_id_id", "feed_versions", new[] { "owner_id", "id" }, unique: true);

        // Copy versioned DTOs as text, including decimal strings and unknown fields.
        migration.Sql("""
            INSERT INTO jobs (owner_id, id, idempotency_key, data)
            SELECT w.owner_id, lower(json_extract(j.value, '$.id')), json_extract(j.value, '$.idempotency_key'), j.value
            FROM workspaces AS w, json_each(w.data, '$.jobs') AS j
            ORDER BY w.owner_id, CAST(j.key AS INTEGER);

            INSERT INTO feed_publications (owner_id, publish_key)
            SELECT DISTINCT w.owner_id, json_extract(f.value, '$.publish_key')
            FROM workspaces AS w, json_each(w.data, '$.feed') AS f;

            INSERT INTO feed_versions (owner_id, publish_key, version, id, data)
            SELECT w.owner_id, json_extract(f.value, '$.publish_key'), json_extract(f.value, '$.version'),
                   lower(json_extract(f.value, '$.id')), f.value
            FROM workspaces AS w, json_each(w.data, '$.feed') AS f;

            UPDATE workspaces SET data = json_set(data, '$.jobs', json('[]'), '$.feed', json('[]'));
            """);
    }

    protected override void Down(MigrationBuilder migration)
    {
        migration.Sql("""
            UPDATE workspaces AS w
            SET data = json_set(data,
                '$.jobs', json((SELECT json_group_array(json(j.data) ORDER BY j.queue_order) FROM jobs AS j WHERE j.owner_id = w.owner_id)),
                '$.feed', json((SELECT json_group_array(json(f.data) ORDER BY f.publish_key, f.version) FROM feed_versions AS f WHERE f.owner_id = w.owner_id)));
            """);
        migration.DropTable("feed_versions");
        migration.DropTable("feed_publications");
        migration.DropTable("jobs");
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => BuildQueueModel(modelBuilder);

    internal static void BuildQueueModel(ModelBuilder modelBuilder)
    {
        InitialWorkspace.BuildInitialModel(modelBuilder);
        const string workspaces = "Portfel.Infrastructure.WorkspaceRow";
        const string jobs = "Portfel.Infrastructure.JobRow";
        const string publications = "Portfel.Infrastructure.PublicationRow";
        const string versions = "Portfel.Infrastructure.FeedVersionRow";

        modelBuilder.Entity(jobs, row =>
        {
            row.Property<long>("QueueOrder").ValueGeneratedOnAdd().HasColumnName("queue_order").HasColumnType("INTEGER");
            row.Property<string>("OwnerId").IsRequired().HasColumnName("owner_id").HasColumnType("TEXT");
            row.Property<string>("Id").IsRequired().HasColumnName("id").HasColumnType("TEXT");
            row.Property<string>("IdempotencyKey").IsRequired().HasColumnName("idempotency_key").HasColumnType("TEXT");
            row.Property<string>("Data").IsRequired().HasColumnName("data").HasColumnType("TEXT");
            row.HasKey("QueueOrder");
            row.HasIndex("OwnerId", "Id").IsUnique();
            row.HasIndex("OwnerId", "IdempotencyKey").IsUnique();
            row.HasIndex("OwnerId", "QueueOrder");
            row.HasOne(workspaces).WithMany().HasForeignKey("OwnerId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            row.ToTable("jobs");
        });
        modelBuilder.Entity(publications, row =>
        {
            row.Property<string>("OwnerId").HasColumnName("owner_id").HasColumnType("TEXT");
            row.Property<string>("PublishKey").HasColumnName("publish_key").HasColumnType("TEXT");
            row.HasKey("OwnerId", "PublishKey");
            row.HasOne(workspaces).WithMany().HasForeignKey("OwnerId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            row.ToTable("feed_publications");
        });
        modelBuilder.Entity(versions, row =>
        {
            row.Property<string>("OwnerId").HasColumnName("owner_id").HasColumnType("TEXT");
            row.Property<string>("PublishKey").HasColumnName("publish_key").HasColumnType("TEXT");
            row.Property<int>("Version").HasColumnName("version").HasColumnType("INTEGER");
            row.Property<string>("Id").IsRequired().HasColumnName("id").HasColumnType("TEXT");
            row.Property<string>("Data").IsRequired().HasColumnName("data").HasColumnType("TEXT");
            row.HasKey("OwnerId", "PublishKey", "Version");
            row.HasIndex("OwnerId", "Id").IsUnique();
            row.HasOne(publications).WithMany().HasForeignKey("OwnerId", "PublishKey").IsRequired().OnDelete(DeleteBehavior.Cascade);
            row.ToTable("feed_versions");
        });
    }
}
