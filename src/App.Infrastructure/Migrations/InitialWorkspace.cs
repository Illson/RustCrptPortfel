using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Portfel.Infrastructure.Migrations;

[DbContext(typeof(PortfelDb))]
[Migration("20261122000100_InitialWorkspace")]
public sealed class InitialWorkspace : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable(
        "workspaces", table => new
        {
            owner_id = table.Column<string>(type: "TEXT", nullable: false),
            data = table.Column<string>(type: "TEXT", nullable: false),
        }, constraints: table => table.PrimaryKey("PK_workspaces", row => row.owner_id));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("workspaces");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => BuildInitialModel(modelBuilder);

    internal static void BuildInitialModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "11.0.0");
        modelBuilder.Entity("Portfel.Infrastructure.WorkspaceRow", entity =>
        {
            entity.Property<string>("OwnerId").HasColumnType("TEXT").HasColumnName("owner_id");
            entity.Property<string>("Data").IsRequired().HasColumnType("TEXT").HasColumnName("data");
            entity.HasKey("OwnerId");
            entity.ToTable("workspaces");
        });
    }
}

[DbContext(typeof(PortfelDb))]
public sealed class WorkspaceModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder) => CoinMarketCapCache.BuildCacheModel(modelBuilder);
}
