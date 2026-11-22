using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Portfel.Infrastructure.Migrations;

[DbContext(typeof(PortfelDb))]
[Migration("20261123000200_MarketRequestResponses")]
public sealed class MarketRequestResponses : Migration
{
    protected override void Up(MigrationBuilder builder) =>
        builder.AddColumn<string>("response_json", "cmc_requests", type: "TEXT", nullable: true);

    protected override void Down(MigrationBuilder builder) => builder.DropColumn("response_json", "cmc_requests");

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        CoinMarketCapCache.BuildCacheModel(modelBuilder);
        Configure(modelBuilder);
    }

    internal static void Configure(ModelBuilder model) => model.Entity<CmcRequestRow>()
        .Property(row => row.ResponseJson).HasColumnName("response_json").HasColumnType("TEXT").IsRequired(false);
}
