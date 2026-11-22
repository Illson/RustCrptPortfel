using Portfel.Domain;

namespace Portfel.Application;

public static class DemoPortfolio
{
    public const string Owner = "demo:fixture";
    public const string PortfolioId = "fixture:portfolio";
    private const string Fixture = "demo_fixture";
    private const string FixtureUrl = "https://example.com/portfel/fixture";
    private static readonly Source DemoSource = new(Fixture, "Учебный набор", FixtureUrl,
        "Синтетические значения для проверки расчётов и отображения качества данных.");

    public static Workspace Sync(Workspace state, DateTimeOffset now)
    {
        Asset sol = new("5426", "Solana", "SOL", true, [new("cmc", "5426"), new("coingecko", "solana"), new("defillama", "solana", "solana")]);
        Asset arb = new("11841", "Arbitrum", "ARB", true, [new("cmc", "11841"), new("coingecko", "arbitrum")]);
        Asset btc = new("1", "Bitcoin", "BTC", true, [new("cmc", "1"), new("coingecko", "bitcoin")]);
        var positions = new Position[] { new(sol.Id, ExactNumber.Parse("42.000000000000000001")), new(arb.Id, ExactNumber.From(1200)),
            new(btc.Id, ExactNumber.Parse("0.1250000000000000000000000000042")) };
        state = state.Accept(new(PortfolioId, now, now, true, "v1", "ok", positions));

        AssetData solData = Solana(sol, now);
        AssetData arbData = Basic(arb, now, "0.82", "2800000000", "finite") with
        {
            Supply = [new("arb-cliff", arb.Id, SupplyKind.Unlock, now.AddDays(16), null, ExactNumber.From(65000000),
                "fixture-grant", "Ранние участники", Circulation.Yes, Fixture, "fixture-v1", true)],
            SupplyCoveredFrom = now,
            SupplyCoveredThrough = now.AddDays(365),
        };
        return state with { Assets = [solData, arbData, Basic(btc, now, "84200.08", "19800000", "finite")] };
    }

    private static AssetData Basic(Asset asset, DateTimeOffset now, string price, string circulation, string max) => new(asset,
        [new(asset.Id, asset.Symbol.ToLowerInvariant(), MappingStatus.Confirmed, Fixture)],
        [Observation(asset, "price", price, "USD", now), Observation(asset, "circulating_supply", circulation, "token_units", now),
            .. max == "finite" ? new[] { Observation(asset, "max_supply", asset.Symbol == "BTC" ? "21000000" : "10000000000", "token_units", now) } : []],
        [], null, [], [], [DemoSource], max);

    private static Observation Observation(Asset asset, string key, string value, string unit, DateTimeOffset now) =>
        new($"fixture-{asset.Id}-{key}", asset.Symbol.ToLowerInvariant(), asset.Id, null, key, MetricScope.Token,
            ExactNumber.Parse(value), unit, now, now, Fixture, now, now, Quality.Ok, "fixture-v1", "fixture-v1", Fixture, 1);

    private static AssetData Solana(Asset asset, DateTimeOffset now)
    {
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var firstMonth = new DateTimeOffset(today.Year, today.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(-12);
        AssetData baseline = Basic(asset, now, "176.25000001", "480000000.125000000000000001", "unknown");
        List<Observation> observations = [.. baseline.Observations,
            Observation(asset, "total_supply", "599843321.125000000000000001", "token_units", now)];
        for (DateTimeOffset day = firstMonth; day < today; day = day.AddDays(1))
        {
            int sequence = (day - firstMonth).Days;
            observations.Add(Flow(asset, "sol", "chain_revenue", MetricScope.Chain, 229000 + sequence * 500 + day.Day * 9200, day, now));
            if (day.Day != 11 || day.Month != today.AddMonths(-1).Month)
                observations.Add(Flow(asset, "solana-apps", "app_revenue", MetricScope.EcosystemApps, 720000 + sequence * 1900 + day.Day * 33500, day, now));
        }

        return baseline with
        {
            Links = [.. baseline.Links, new(asset.Id, "solana-apps", MappingStatus.Confirmed, Fixture)],
            Observations = [.. observations],
            Supply = [new("sol-linear", asset.Id, SupplyKind.Issuance, today, today.AddDays(366), ExactNumber.From(17500000),
                "fixture-staking", "Стейкеры", Circulation.Yes, Fixture, "fixture-v1", true)],
            SupplyCoveredFrom = today,
            SupplyCoveredThrough = today.AddDays(366),
            Markets = [new("sol-fixture-a", "fixture-a", asset.Id, "usd", "SOL/USD", "spot", "CEX", ExactNumber.From(67800000), now.AddMinutes(-5), true, Quality.Ok, Fixture),
                new("sol-fixture-b", "fixture-b", asset.Id, "usdc", "SOL/USDC", "spot", "DEX", ExactNumber.From(22700000), now.AddMinutes(-10), true, Quality.Ok, Fixture)],
            Events = [new("sol-fixture-event", asset.Id, "Тестовое обновление сети", today.AddDays(-2), DateOnly.FromDateTime(today.UtcDateTime.AddDays(12)),
                null, null, null, "date", "scheduled", [Fixture], false, 1),
                new("sol-fixture-event", asset.Id, "Тестовое обновление сети", today.AddDays(-1), DateOnly.FromDateTime(today.UtcDateTime.AddDays(18)),
                null, null, null, "date", "postponed", [Fixture], false, 2)],
        };
    }

    private static Observation Flow(Asset asset, string entityId, string metric, MetricScope scope, int value, DateTimeOffset day, DateTimeOffset now) =>
        new($"fixture-{metric}-{day:yyyyMMdd}", entityId, asset.Id, null, metric, scope, ExactNumber.From(value), "USD",
            day, day.AddDays(1), Fixture, day.AddDays(1), now, Quality.Ok, "fixture-v1", "fixture-v1", Fixture, 1);

    public static (AnalysisInputV1, AnalysisResultV1) Analyze(Workspace state, string assetId, DateTimeOffset now)
    {
        AssetData asset = state.Assets.Single(item => item.Asset.Id == assetId);
        var source = new Evidence($"{Fixture}-announcement", assetId, Fixture, FixtureUrl, "Сценарий проверки публикации", now);
        var snapshot = new AnalysisInputV1(AnalysisValidation.SchemaVersion, assetId, now, state.Current?.Hash, [source],
            [new(Fixture, "fixture", "fixture-search", [], [FixtureUrl], [source.Id], now, true)], true)
        {
            Asset = asset.Asset, SourcePortfolioId = PortfolioId, EntityLinks = asset.Links,
        };
        AnalysisSection[] sections = Enum.GetValues<AnalysisBlock>().Select(block =>
            new AnalysisSection(block, block == AnalysisBlock.Project ? Quality.Partial : Quality.Missing,
                "undetermined", block == AnalysisBlock.Project ? "Набор показывает пример изменения срока события."
                    : "Демо не формирует вывод о реальных данных проекта.", block == AnalysisBlock.Project ? [source.Id] : [])).ToArray();
        snapshot = snapshot with { Blocks = sections.Select(section => new AnalysisCoverage(section.Block, section.DataStatus, section.EvidenceIds)).ToArray() };
        return (snapshot, new(AnalysisValidation.SchemaVersion, assetId, snapshot.SnapshotHash, $"{asset.Asset.Symbol} · демонстрационный обзор",
            "Учебная карточка показывает четыре блока анализа, ссылки на основания и статус покрытия.", "info", sections,
            [], [new("fixture-fact", "В карточку включён синтетический сценарий проверки источника.", [source.Id], "reported", [])],
            [new("Карточка иллюстрирует обработку неполных данных.", ["fixture-fact"], "Источник учебный.")],
            "Используется демонстрационный состав. Реальная позиция не подключена.",
            [new("verify", "Сверить подтверждение у первоисточника.", "Когда появится официальный источник.", [source.Id], "normal")],
            ["Реальная новостная проверка не выполнялась."], "При следующем запуске анализа."));
    }
}
