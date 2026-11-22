using System.Text.Json;
using Portfel.Application;
using Portfel.Domain;
using static Checks;

internal static class DomainChecks
{
    internal static void Run(DateTimeOffset now)
    {
        var a = new Position("1", ExactNumber.Parse("2.00000"));
        var b = new Position("5426", ExactNumber.From(4));
        PortfolioFetch fetch = new("approved:test-portfolio", now, now, true, "v1", "ok", [a, b]);
        Workspace state = Workspace.Empty.RecordSync(fetch);
        string original = state.Current!.Hash;
        for (int i = 1; i <= 5; i++)
            state = state.RecordSync(fetch with { FetchedAt = now.AddMinutes(i), Positions = [b, a] });
        Require(state.Snapshots.Length == 1 && state.Current!.Hash == original && state.Current.LastCheckedAt == now.AddMinutes(5), "Five identical complete snapshots");
        state = state.RecordSync(fetch with { FetchedAt = now.AddMinutes(6), IsComplete = false, Positions = [] });
        Require(state.LatestCheck?.Status == "incomplete_snapshot" && state.Current!.Positions.Length == 2, "Second-page failure preserves first good snapshot");
        var adjusted = state.RecordSync(fetch with { FetchedAt = now.AddMinutes(7), Positions = [a with { Quantity = ExactNumber.From(8) }] });
        Require(adjusted.Snapshots.Length == 2 && adjusted.Current!.Positions.Single().Quantity == ExactNumber.From(8), "Full change and removal");
        var future = adjusted.RecordSync(fetch with { FetchedAt = now.AddMinutes(8), SourceAsOf = now.AddDays(1) });
        var recovered = future.RecordSync(fetch with { FetchedAt = now.AddMinutes(9), SourceAsOf = now.AddMinutes(9) });
        Require(future.LatestCheck?.Status == "invalid_snapshot" && future.Current == adjusted.Current
            && recovered.LatestCheck?.Status == "ok" && recovered.Current?.Positions.Length == 2,
            "Future source time cannot poison the next valid sync");
        var positions = Enumerable.Range(0, 52).Select(i => new Position(i.ToString(), ExactNumber.From(1), "Synthetic", "SAME", i < 50)).ToArray();
        var mixed = Workspace.Empty.RecordSync(fetch with { Positions = positions });
        Require(PortfolioQueries.Portfolio(mixed, now, false).Positions.Count(row => row.QualityStatus == Quality.NotApplicable) == 2,
            "Unsupported positions remain visible beyond 50 spot assets");
        Require(PortfolioQueries.Portfolio(mixed, now, false).Positions.Single(row => row.AssetId == "0").QualityStatus == Quality.Missing,
            "Unmapped spot isn't displayed as unsupported");
        Require(ExactNumber.ParseProviderNumber("1.234567890123456789012345678901e-5")
            == ExactNumber.Parse("0.00001234567890123456789012345678901"), "Provider scientific notation is exact");
        Require(ExactNumber.From(5).Divide(ExactNumber.From(2), 0) == ExactNumber.From(2), "Banker's rounding");

        DateOnly beginning = new(2026, 10, 1), next = beginning.AddMonths(1);
        var days = Enumerable.Range(0, 31).Select(i => new DailyFlow($"obs-{i}", beginning.AddDays(i), ExactNumber.From(2))).ToArray();
        var full = Metrics.SumMonth(days, beginning, next);
        Require(full.Total == ExactNumber.From(62) && full.IsComplete, "Monthly exact flows");
        var incomplete = Metrics.SumMonth(days.Skip(1), beginning, next);
        Require(incomplete.QualityStatus == Quality.Partial && incomplete.ObservedIntervals == 30 && Metrics.Compare(full, incomplete) is null, "No comparison against partial month");
        var zero = full with { Total = ExactNumber.Zero };
        Require(Metrics.Compare(full, zero) is { PercentagePoints: null, Transition: "zero_base", Absolute: var diff } && diff == ExactNumber.From(62),
            "Absolute change for zero baseline");
        Require(Metrics.Compare(full, full with { Mtd = true }) is null, "Do not compare MTD to full month");
        Require(Metrics.SumMonth([.. days, new("conflicting", beginning, ExactNumber.From(3))], beginning, next).QualityStatus == Quality.Conflict,
            "Conflicting duplicate periods are not summed");

        ExactNumber circulation = ExactNumber.From(1000);
        const string assetId = "synthetic";
        SupplyEvent Supply(string id, SupplyKind kind, int start, int? end, long amount, string? allocation, Circulation enters = Circulation.Yes,
            Circulation exits = Circulation.Unknown) =>
            new(id, assetId, kind, now.AddDays(start), end.HasValue ? now.AddDays(end.Value) : null, ExactNumber.From(amount),
                allocation, "category", enters, "provider", "v1", true, exits);
        var unlock = Supply("cliff-start", SupplyKind.Unlock, 0, null, 20, "unlock");
        var issuance = Supply("linear-part-one", SupplyKind.Issuance, -5, 5, 200, "mint");
        var step = Supply("linear-part-two", SupplyKind.Issuance, 5, 10, 25, "mint");
        var outOfWindow = Supply("cliff-end", SupplyKind.Unlock, 365, null, 999, "end");
        var burn = Supply("burn", SupplyKind.Burn, 3, null, 5, "burns", Circulation.No, Circulation.Yes);
        SupplyWindow window = Metrics.Supply([unlock, issuance, step, outOfWindow, burn, unlock with { Id = "duplicate", SourceId = "second" }],
            assetId, now, 365, circulation, now.AddDays(365), now, true);
        Require(window.Unlocks == ExactNumber.From(20) && window.NewIssuance == ExactNumber.From(125)
            && window.GrossSupplyPressurePct == ExactNumber.Parse("14.5"), "Half-open window, prorating, rate change, duplicate removal");
        Require(window.NetTotalSupplyChange == ExactNumber.From(120) && window.NetCirculatingSupplyChange == ExactNumber.From(140),
            "Unlocks do not increase total supply");
        var conflict = Metrics.Supply([unlock, Supply("same", SupplyKind.Issuance, 0, null, 5, null)], assetId, now, 365, circulation, now.AddDays(365), now);
        Require(conflict.GrossSupplyPressurePct is null && conflict.QualityStatus == Quality.Partial, "Unresolved overlap does not produce gross figure");
        Require(Metrics.Supply([unlock], assetId, now, 30, ExactNumber.Zero, now.AddDays(30), now).UnlocksPct is null,
            "Zero denominator");
        Require(Metrics.Supply([], assetId, now, 30, circulation, null).UnlocksPct is null &&
            Metrics.Supply([], assetId, now, 30, circulation, now.AddDays(30), now).UnlocksPct == ExactNumber.Zero,
            "Unknown schedule versus confirmed zero");
        Require(Metrics.Supply([issuance with { EndsAt = issuance.StartsAt.AddDays(-1) }], assetId, now, 30, circulation, now.AddDays(30), now).QualityStatus == Quality.Error,
            "Invalid schedule must not be treated as an empty complete window");
        SupplyWindow Grants(params SupplyEvent[] events) => Metrics.Supply(events, assetId, now, 30, circulation, now.AddDays(30), now);
        var sibling = unlock with { Id = "sibling" };
        Require(Grants(unlock, sibling) is { QualityStatus: Quality.Ok } both
            && both.Unlocks == ExactNumber.From(40) && both.GrossSupplyPressurePct == ExactNumber.From(4),
            "Different installment IDs in the same allocation must both count");
        Require(Grants(sibling, unlock, unlock with { SourceId = "other" }).QualityStatus == Quality.Conflict,
            "Inconsistent cross-source multiplicity is not a precise total");
        var moved = unlock with { StartsAt = now.AddDays(20), ScheduleVersion = "v10" };
        var originalGrant = unlock with { StartsAt = now.AddDays(10), ScheduleVersion = "v2" };
        Require(Grants(moved, originalGrant).Unlocks == unlock.QuantityTotal &&
            Grants(originalGrant, moved with { StartsAt = now.AddDays(30) }).Unlocks == ExactNumber.Zero,
            "Latest version of one grant wins before window filtering, regardless of input order");
        Require(Grants(originalGrant, moved with { ScheduleVersion = "unranked" }) is { QualityStatus: Quality.Conflict, Unlocks: null }
            && Grants(moved, moved with { QuantityTotal = circulation }) is { QualityStatus: Quality.Conflict, GrossSupplyPressurePct: null },
            "Ambiguous grant revisions cannot produce a precise supply figure");

        var market = new Market("m", "venue", assetId, "token:A", "SAME/USD", "spot", "CEX", ExactNumber.From(4), now, true, Quality.Ok, "a");
        var derivative = market with { ExchangeId = "major", MarketType = "derivative" };
        var duplicate = market with { Id = "m-other", Pair = "SAME-USD", SourceId = "b" };
        var differentToken = market with { QuoteAssetId = "token:other", Id = "contract", ReportedVolumeUsd = ExactNumber.From(1) };
        var summary = Metrics.Markets([market, derivative, duplicate, differentToken], assetId, now, ["major"]);
        Require(summary.ObservedVolume == ExactNumber.From(5) && summary.ActivePairs.Length == 2 && summary.MajorExchangeStatus == "unconfirmed",
            "Stable pair identity, market deduplication and derivatives excluded");
        Require(Metrics.Markets([market], assetId, now.AddHours(2), ["venue"]).QualityStatus == Quality.Stale, "Stale is not a delisting");
        var demo = DemoPortfolio.Sync(Workspace.Empty, now);
        var sol = demo.Assets.Single(asset => asset.Asset.Id == "5426");
        var badCirculation = sol with { Observations = sol.Observations.Select(row => row.MetricKey == "circulating_supply"
            ? row with { QualityStatus = Quality.Error } : row).ToArray() };
        Require(PortfolioQueries.Supply(badCirculation, now).Windows.All(w => w is
            { QualityStatus: Quality.Error, NewIssuance: not null, GrossSupplyPressurePct: null, NewIssuancePct: null, UnlocksPct: null }),
            "An erroneous positive denominator never produces percentages");
        var cex = market with { BaseAssetId = sol.Asset.Id, SourceId = "coingecko", ReportedVolumeUsd = ExactNumber.From(10) };
        var marketData = sol with { Markets = [] };
        SourceBatch MarketBatch(int minutes, string status, params Market[] rows) => new(status, now.AddMinutes(minutes), [], [], rows);
        marketData = PublicSourceRevisions.Merge(marketData, MarketBatch(0, "partial", cex), cex.SourceId);
        var correctionBatch = MarketBatch(5, "partial", cex with { ReportedVolumeUsd = ExactNumber.From(100) });
        marketData = PublicSourceRevisions.Merge(marketData, correctionBatch, cex.SourceId);
        marketData = PublicSourceRevisions.Merge(marketData, MarketBatch(2, "ok", cex), cex.SourceId);
        marketData = JsonSerializer.Deserialize<AssetData>(JsonSerializer.Serialize(marketData, PortfelJson.Options), PortfelJson.Options)!;
        Require(PortfolioQueries.Markets(marketData, now.AddMinutes(5), []) is { QualityStatus: Quality.Partial, TopVenueShare: null } ms
            && ms.ActivePairs.Length == 1 && ms.ObservedVolume == ExactNumber.From(100) && ms.Distribution.Single().SharePct is null,
            "Partial market correction survives reload and out-of-order batches without claiming complete distribution");
        var allMarkets = PublicSourceRevisions.Merge(marketData, MarketBatch(6, "ok", cex), cex.SourceId);
        Require(PortfolioQueries.Markets(allMarkets, now.AddMinutes(6), []) is { QualityStatus: Quality.Ok, TopVenueShare: not null },
            "A complete refresh restores market coverage");
        var staleDays = sol with { Observations = sol.Observations.Select(row => row.MetricKey == "chain_revenue"
            && row.PeriodStart.Month == beginning.Month ? row with { QualityStatus = Quality.Stale } : row).ToArray() };
        var staleMonth = PortfolioQueries.Economy(staleDays, now).Single(row => row.MetricKey == "chain_revenue")
            .Months.Single(month => month.Month == beginning);
        Require(staleMonth is { QualityStatus: Quality.Stale, ObservedIntervals: 31, MonthOverMonth: null, Total: not null },
            "Complete but explicitly stale daily values remain stale after aggregation");
        var ambiguous = sol with { Links = sol.Links.Select(link => link with { Status = MappingStatus.Ambiguous }).ToArray() };
        Require(PortfolioQueries.Economy(ambiguous, now).Length == 0, "Unconfirmed links cannot contribute financial metrics");
        Require(PortfolioQueries.CurrentEvents(sol.Events).Single() is { Version: 2, EventAt: null, LocalDate: not null } &&
            sol.Events[0].LocalDate != sol.Events[1].LocalDate, "Postponement retains date-only history");
        var notice = sol.Events[0];
        var official = new SourceRecord("canonical", sol.Sources[0].Id, "https://project.example.com/update", "Объявление",
            now, null, now, "test-hash", "primary-notice-v1", Primary: true);
        var copies = Enumerable.Range(1, 3).Select(i => new SourceRecord($"copy-{i}", "press", $"https://press.example.com/{i}", "Копия",
            now, null, now, "test-hash", "syndicated")).ToArray();
        var canonical = ProjectEvents.Register(sol with { Events = [] }, official.SourceId, "fixture-upgrade", notice, [official, .. copies]);
        Require(canonical.Events.Single().SourceRefs.Length == 4 &&
            ReferenceEquals(canonical, ProjectEvents.Register(canonical, official.SourceId, "fixture-upgrade", notice, [official, .. copies])),
            "Syndication with the same confirmed event identity does not add events");
        var postponed = ProjectEvents.Register(canonical, official.SourceId, "fixture-upgrade", sol.Events[1], [official]);
        Require(postponed.Events.Length == 2 && PortfolioQueries.CurrentEvents(postponed.Events).Single().Status == "postponed",
            "Event revision preserves the previous deadline");
        var replay = ProjectEvents.Register(postponed, official.SourceId, "fixture-upgrade", notice, [official]);
        Require(ReferenceEquals(replay, postponed), "Replaying an older primary notice cannot undo a postponement");
        Reject<AppFault>(() => ProjectEvents.Register(postponed, official.SourceId, "fixture-upgrade",
            notice with { AnnouncedAt = sol.Events[1].AnnouncedAt }, [official]), "Conflicting notices with equal timestamps cannot silently win");
        var (snapshot, result) = DemoPortfolio.Analyze(demo, "5426", now);
        Require(AnalysisValidation.Parse(JsonSerializer.Serialize(result, PortfelJson.Options)).Headline == result.Headline, "Result JSON Schema");
        _ = AnalysisSchemas.SynthesisInput(snapshot);
        Require(AnalysisValidation.Validate(snapshot, result with { SnapshotHash = new string('0', 64) }) == "invalid_snapshot",
            "Wrong snapshot hash blocks publication");
        Require(AnalysisValidation.Validate(snapshot, result with { Summary = "Неподтверждённое значение 999." }) == "invalid_output", "No unchecked number in prose");
        Require(AnalysisValidation.Validate(snapshot, result with { Sections = [null!, .. result.Sections.Skip(1)] }) == "invalid_output", "Null section rejected");
        var metric = new Evidence("verified-number", snapshot.AssetId, "demonstration", "https://example.com/metrics", "Ряд", now,
            "revenue", ExactNumber.From(4), "USD", "2026-10", MetricScope.Chain);
        var withMetric = snapshot with { Evidence = [.. snapshot.Evidence, metric] };
        var number = new NumericClaim(metric.Id, metric.ValueDecimal!.Value, "USD", "2026-10", MetricScope.Chain);
        var numeric = result with { SnapshotHash = withMetric.SnapshotHash,
            Facts = [.. result.Facts, new("check-revenue", "Проверено значение выручки.", [metric.Id], "confirmed", [number])],
        };
        Require(AnalysisValidation.Validate(withMetric, numeric) is null, "Exact claim matches evidence");
        numeric.Facts[^1] = numeric.Facts[^1] with { NumericClaims = [number with { Scope = MetricScope.EcosystemApps }] };
        Require(AnalysisValidation.Validate(withMetric, numeric) == "invalid_numeric_claim", "Same number in a different scope is not evidence");
        var published = WorkspaceJobs.Publish(demo, DemoPortfolio.Owner, snapshot, result, now);
        Require(ReferenceEquals(published, WorkspaceJobs.Publish(published, DemoPortfolio.Owner, snapshot, result, now)), "Snapshot replay is idempotent");
        var (subsequent, correction) = DemoPortfolio.Analyze(demo, "5426", now.AddSeconds(1));
        published = WorkspaceJobs.Publish(published, DemoPortfolio.Owner, subsequent, correction, now.AddSeconds(1));
        Require(published.Feed.Length == 2 && published.Feed[1].SupersedesId == published.Feed[0].Id
            && PortfolioQueries.Feed(published, null, null, null, null, null, null, null).Items.Length == 1, "History without daily duplicate");
        Console.WriteLine("PASS portfolio snapshots, supported positions, flow/supply/market controls, event history, schema and feed versions");
    }
}
