using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Portfel.Infrastructure.Migrations;

[DbContext(typeof(PortfelDb))]
[Migration("20261123000100_CoinMarketCapCache")]
public sealed class CoinMarketCapCache : Migration
{
    private const string Text = "TEXT";
    private const string Integer = "INTEGER";

    protected override void Up(MigrationBuilder builder)
    {
        builder.CreateTable("cmc_gate", table => new
        {
            id = table.Column<int>(type: Integer, nullable: false),
            active_id = table.Column<string>(type: Text, nullable: true),
            next_at = table.Column<long>(type: Integer, nullable: false),
        }, constraints: table => table.PrimaryKey("PK_cmc_gate", row => row.id));
        builder.Sql("INSERT INTO cmc_gate (id, next_at) VALUES (1, 0)");

        builder.CreateTable("cmc_coins", table => new
        {
            slug = table.Column<string>(type: Text, nullable: false),
            next_at = table.Column<long>(type: Integer, nullable: false),
            crypto_id = table.Column<int>(type: Integer, nullable: true),
            platform_json = table.Column<string>(type: Text, nullable: true),
            chart_json = table.Column<string>(type: Text, nullable: true),
            fetched_at = table.Column<long>(type: Integer, nullable: true),
        }, constraints: table => table.PrimaryKey("PK_cmc_coins", row => row.slug));
        builder.CreateIndex("IX_cmc_coins_crypto_id", "cmc_coins", "crypto_id");

        builder.CreateTable("cmc_requests", table => new
        {
            id = table.Column<string>(type: Text, nullable: false),
            slug = table.Column<string>(type: Text, nullable: false),
            kind = table.Column<string>(type: Text, nullable: false),
            outcome = table.Column<string>(type: Text, nullable: false),
            http_status = table.Column<int>(type: Integer, nullable: true),
            started_at = table.Column<long>(type: Integer, nullable: false),
            finished_at = table.Column<long>(type: Integer, nullable: true),
        }, constraints: table => table.PrimaryKey("PK_cmc_requests", row => row.id));
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.DropTable("cmc_requests");
        builder.DropTable("cmc_coins");
        builder.DropTable("cmc_gate");
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => BuildCacheModel(modelBuilder);

    internal static void BuildCacheModel(ModelBuilder modelBuilder)
    {
        PersistentQueue.BuildQueueModel(modelBuilder);
        Configure(modelBuilder);
    }

    internal static void Configure(ModelBuilder model)
    {
        var gate = model.Entity<CmcGateRow>().ToTable("cmc_gate");
        gate.HasKey(row => row.Id);
        gate.Property(row => row.Id).HasColumnName("id").HasColumnType(Integer).ValueGeneratedNever();
        gate.Property(row => row.NextAt).HasColumnName("next_at").HasColumnType(Integer);
        gate.Property(row => row.ActiveId).HasColumnName("active_id").HasColumnType(Text).IsRequired(false);

        var coin = model.Entity<CmcCoinRow>().ToTable("cmc_coins");
        coin.HasKey(row => row.Slug);
        coin.Property(row => row.Slug).HasColumnName("slug").HasColumnType(Text);
        coin.Property(row => row.NextAt).HasColumnName("next_at").HasColumnType(Integer);
        coin.Property(row => row.CryptoId).HasColumnName("crypto_id").HasColumnType(Integer);
        coin.HasIndex(row => row.CryptoId).HasDatabaseName("IX_cmc_coins_crypto_id");
        coin.Property(row => row.PlatformJson).HasColumnName("platform_json").HasColumnType(Text).IsRequired(false);
        coin.Property(row => row.ChartJson).HasColumnName("chart_json").HasColumnType(Text).IsRequired(false);
        coin.Property(row => row.FetchedAt).HasColumnName("fetched_at").HasColumnType(Integer);

        var request = model.Entity<CmcRequestRow>().ToTable("cmc_requests");
        request.HasKey(row => row.Id);
        request.Property(row => row.Id).HasColumnName("id").HasColumnType(Text);
        request.Property(row => row.Slug).HasColumnName("slug").HasColumnType(Text).IsRequired();
        request.Property(row => row.Kind).HasColumnName("kind").HasColumnType(Text).IsRequired();
        request.Property(row => row.Outcome).HasColumnName("outcome").HasColumnType(Text).IsRequired();
        request.Property(row => row.HttpStatus).HasColumnName("http_status").HasColumnType(Integer);
        request.Property(row => row.StartedAt).HasColumnName("started_at").HasColumnType(Integer);
        request.Property(row => row.FinishedAt).HasColumnName("finished_at").HasColumnType(Integer);
    }
}
