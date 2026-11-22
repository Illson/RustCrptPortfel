using Portfel.Domain;

namespace Portfel.Application;

public static class PortfolioQueries
{
    private static readonly TimeSpan SnapshotAge = TimeSpan.FromHours(1);

    public static bool IsFresh(PortfolioSnapshot? snapshot, DateTimeOffset now) =>
        snapshot is not null && snapshot.LastCheckedAt <= now && now - snapshot.LastCheckedAt <= SnapshotAge;

    public static bool CanPersonalize(Workspace state, DateTimeOffset now) =>
        IsFresh(state.Current, now) && (state.LatestCheck is null || state.LatestCheck.Status == "ok");

    public static PortfolioView Portfolio(Workspace state, DateTimeOffset now, bool demo)
    {
        PortfolioSnapshot? snapshot = state.Current;
        var assets = state.Assets.ToDictionary(row => row.Asset.Id, StringComparer.Ordinal);
        bool stale = snapshot is not null && !IsFresh(snapshot, now);
        PortfolioPosition[] positions = (snapshot?.Positions ?? []).Select(position =>
        {
            AssetData? asset = assets.GetValueOrDefault(position.AssetId);
            bool spot = position.IsSpot ?? asset?.Asset.Spot ?? true;
            Observation? price = asset is null ? null : Latest(asset, "price", now, "USD");
            Quality quality = !spot ? Quality.NotApplicable : price is null ? Quality.Missing
                : price.QualityStatus != Quality.Ok ? price.QualityStatus : price.ValueDecimal is not { Sign: > 0 } ? Quality.Error
                : stale || now - price.SourceAsOf > SnapshotAge ? Quality.Stale : Quality.Ok;
            return new PortfolioPosition(position.AssetId, position.SourceSymbol ?? asset?.Asset.Symbol ?? position.AssetId,
                position.SourceName ?? asset?.Asset.Name ?? position.AssetId, position.Quantity,
                quality == Quality.Ok ? position.Quantity * price!.ValueDecimal!.Value : null, null, quality, spot);
        }).ToArray();

        var valued = positions.Where(row => row.ValueUsd.HasValue).ToArray();
        bool complete = snapshot is not null && !stale && valued.Length == positions.Length;
        ExactNumber? total = complete || valued.Length > 0
            ? valued.Aggregate(ExactNumber.Zero, (sum, row) => sum + row.ValueUsd!.Value) : null;
        if (complete && total is { Sign: > 0 } denominator)
            positions = positions.Select(row => row with { SharePct = (row.ValueUsd!.Value * ExactNumber.From(100)).Divide(denominator, 8) }).ToArray();

        var owned = positions.Select(position => position.AssetId).ToHashSet(StringComparer.Ordinal);
        var timezone = TimeZoneInfo.FindSystemTimeZoneById(state.Settings.Timezone);
        DateOnly today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timezone).DateTime);
        var upcoming = state.Assets.Where(asset => owned.Contains(asset.Asset.Id)).SelectMany(asset => CurrentEvents(asset.Events))
            .Where(item => item.Status is "scheduled" or "postponed" or "announced"
                && ((item.DeadlineAt ?? item.EventAt) is { } time ? time >= now : item.LocalDate >= today))
            .OrderBy(item => (item.DeadlineAt ?? item.EventAt) is { } time
                ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time, timezone).DateTime) : item.LocalDate).Take(5).ToArray();
        Job? last = state.Jobs.LastOrDefault(job => job.Kind == JobKind.PortfolioSync);
        return new("USD", total, snapshot is null ? Quality.Missing : stale ? Quality.Stale : complete ? Quality.Ok : Quality.Partial,
            snapshot?.SourcePortfolioId, snapshot?.LastCheckedAt,
            demo ? "demo_fixture" : state.LatestCheck?.Status ?? Integrations.CmcAccess,
            demo, positions, last is null ? null : Receipt(last), upcoming);
    }

    public static JobReceipt Receipt(Job job) => new(job.Id, job.Status, job.CreatedAt, job.BlockReason);

    public static Observation? Latest(AssetData asset, string key, DateTimeOffset now, string unit = "token_units") => asset.Observations
        .Where(item => item.MetricKey == key && item.SourceAsOf <= now && item.FetchedAt <= now
            && item.AssetId == asset.Asset.Id && item.Scope == MetricScope.Token && item.Unit == unit)
        .OrderByDescending(item => item.SourceAsOf).ThenByDescending(item => item.Revision).FirstOrDefault();

    public static FlowSeries[] Economy(AssetData asset, DateTimeOffset now)
    {
        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
        var month = new DateOnly(today.Year, today.Month, 1);
        var linked = asset.Links.Where(link => link.Status == MappingStatus.Confirmed && link.AssetId == asset.Asset.Id)
            .Select(link => link.EntityId).ToHashSet(StringComparer.Ordinal);
        return asset.Observations.Where(item => linked.Contains(item.EntityId) && (item.AssetId is null || item.AssetId == asset.Asset.Id)
                && item.MetricKey is "chain_revenue" or "app_revenue" or "fees" or "revenue" or "holders_revenue"
                    or "incentives" or "operating_expenses" or "chain_fees" or "app_fees"
                && item.FetchedAt <= now && item.SourceAsOf <= now && item.PeriodEnd - item.PeriodStart == TimeSpan.FromDays(1)
                && item.PeriodStart.Offset == TimeSpan.Zero && item.PeriodStart.TimeOfDay == TimeSpan.Zero
                && (item.Scope != MetricScope.ProtocolChain || item.ChainId is not null))
            .GroupBy(item => (item.EntityId, item.AssetId, item.ChainId, item.Scope, item.MetricKey, item.Unit, item.SourceId, item.MethodologyVersion))
            .Select(group =>
            {
                var latest = group.GroupBy(item => item.PeriodStart).SelectMany(items =>
                {
                    int revision = items.Max(row => row.Revision);
                    return items.Where(item => item.Revision == revision);
                }).ToArray();
                var series = latest.Where(item => item.QualityStatus is Quality.Ok or Quality.Stale && item.ValueDecimal.HasValue)
                    .Select(item => new DailyFlow(item.Id, DateOnly.FromDateTime(item.PeriodStart.UtcDateTime), item.ValueDecimal!.Value, item.QualityStatus)).ToArray();
                bool stale = now - latest.Max(item => item.CheckedAsOf(now)) > TimeSpan.FromHours(36)
                    || (today > month && !series.Any(item => item.Day >= today.AddDays(-2)));
                MonthlyFlow Month(DateOnly start, DateOnly end)
                {
                    MonthlyFlow result = Metrics.SumMonth(series, start, end);
                    return stale && result.QualityStatus == Quality.Ok ? result with { QualityStatus = Quality.Stale } : result;
                }
                var months = Enumerable.Range(-13, 14).Select(offset => Month(month.AddMonths(offset), today)).ToArray();
                var shown = months.Skip(1).Select((value, index) =>
                {
                    if (value.Mtd)
                    {
                        DateOnly earlier = month.AddMonths(-1);
                        return value with { ComparableMtd = value.ExpectedIntervals <= DateTime.DaysInMonth(earlier.Year, earlier.Month)
                            ? Metrics.Compare(value, Month(earlier, earlier.AddDays(value.ExpectedIntervals)) with { Mtd = true }) : null };
                    }
                    int previousYear = index + 1 - 12;
                    return value with
                    {
                        MonthOverMonth = Metrics.Compare(value, months[index]),
                        YearOverYear = Metrics.Compare(value, previousYear >= 0 ? months[previousYear] : Month(value.Month.AddYears(-1), today)),
                    };
                }).ToArray();
                return new FlowSeries(group.Key.MetricKey, group.Key.Scope, group.Key.Unit, shown,
                    group.Key.EntityId, group.Key.AssetId, group.Key.ChainId, group.Key.SourceId, group.Key.MethodologyVersion,
                    months[^4..^1].All(value => value.QualityStatus == Quality.Ok)
                        ? Metrics.Compare(months[^2], months[^4]) : null);
            }).ToArray();
    }

    public static SupplyView Supply(AssetData asset, DateTimeOffset now)
    {
        Observation? circulating = Latest(asset, "circulating_supply", now);
        Quality quality = circulating is null ? Quality.Missing : circulating.QualityStatus != Quality.Ok ? circulating.QualityStatus
            : circulating.ValueDecimal is not { Sign: > 0 } ? Quality.Error
            : now - circulating.SourceAsOf > TimeSpan.FromHours(24) ? Quality.Stale : Quality.Ok;
        ExactNumber? amount = circulating?.ValueDecimal;
        var maximum = Latest(asset, "max_supply", now);
        var total = Latest(asset, "total_supply", now);
        return new(asset.MaximumSupplyKind, asset.MaximumSupplyKind == "finite" && maximum is { QualityStatus: Quality.Ok } ? maximum.ValueDecimal : null,
            total is { QualityStatus: Quality.Ok } ? total.ValueDecimal : null, amount, circulating?.SourceAsOf,
            new[] { 30, 90, 365 }.Select(days =>
            {
                SupplyWindow window = Metrics.Supply(asset.Supply, asset.Asset.Id, now, days,
                    quality == Quality.Ok ? amount : null, asset.SupplyCoveredThrough, asset.SupplyCoveredFrom, asset.BurnsCovered);
                return quality != Quality.Ok && window.QualityStatus == Quality.Missing
                    ? window with { QualityStatus = quality } : window;
            }).ToArray(), Metrics.CurrentSchedule(asset.Supply.Where(item => item.AssetId == asset.Asset.Id)), quality);
    }

    public static IEnumerable<ProjectEvent> CurrentEvents(IEnumerable<ProjectEvent> events) =>
        events.GroupBy(item => (item.AssetId, item.Id)).Select(group => group.MaxBy(item => item.Version)!);

    public static MarketSummary Markets(AssetData asset, DateTimeOffset now, string[] major) =>
        Metrics.Markets(asset.Markets, asset.Asset.Id, now, major, asset.MarketCoverage?.Any(row => !row.Complete) == true);

    public static FeedPage Feed(Workspace state, string? asset, string? type, string? importance,
        DateOnly? from, DateOnly? to, bool? read, Guid? cursor, DateTimeOffset? now = null)
    {
        if (from > to) throw new AppFault("invalid_feed_filter", "Начало периода позже окончания.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(state.Settings.Timezone);
        DateOnly LocalDay(FeedItem item) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(item.PublishedAt, zone).DateTime);
        var anchor = cursor is { } id ? state.Feed.SingleOrDefault(item => item.Id == id)
            ?? throw new AppFault("invalid_cursor", "Курсор ленты устарел.") : null;
        var items = state.Feed.GroupBy(item => item.PublishKey).Select(group => group.MaxBy(item => item.Version)!)
            .Where(item => (asset is null || item.AssetId == asset) && (type is null || item.Type == type)
                && (importance is null || item.Analysis.Severity == importance
                    || importance == "normal" && LegacyImportance(item.Analysis.Severity) == "normal") && (read is null || item.Read == read)
                && (from is null || LocalDay(item) >= from) && (to is null || LocalDay(item) <= to)
                && (anchor is null || (item.PublishedAt, item.Id).CompareTo((anchor.PublishedAt, anchor.Id)) < 0))
            .OrderByDescending(item => item.PublishedAt).ThenByDescending(item => item.Id).Take(21).ToArray();
        bool updating = state.Jobs.Any(job => job.Status is JobStatus.Queued or JobStatus.Running or JobStatus.RetryWait
            && (asset is null || job.Kind == JobKind.PortfolioSync || job.AssetId == asset));
        var symbols = state.Assets.ToDictionary(row => row.Asset.Id, row => row.Asset.Symbol, StringComparer.Ordinal);
        return new(items.Take(20).Select(item => now.HasValue ? WithCurrentPersonalization(state, item, now.Value) : item).ToArray(),
            items.Length > 20 ? items[19].Id : null, updating,
            (state.Current?.Positions ?? []).Select(p => new FeedAsset(p.AssetId, p.SourceSymbol ?? symbols.GetValueOrDefault(p.AssetId) ?? p.AssetId)).ToArray());
    }

    public static FeedItem WithCurrentPersonalization(Workspace state, FeedItem item, DateTimeOffset now) =>
        item.Personalization == "current" && !item.Demo && (!CanPersonalize(state, now) || state.Current?.Hash != item.PortfolioHash)
            ? item with { Personalization = "historical" } : item;

    public static string LegacyImportance(string severity) => severity is "info" or "watch" ? "normal" : severity;
}
