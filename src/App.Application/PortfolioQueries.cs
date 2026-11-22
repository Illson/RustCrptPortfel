using Portfel.Domain;

namespace Portfel.Application;

public static class PortfolioQueries
{
    public static PortfolioView Portfolio(Workspace state, DateTimeOffset now, bool demo)
    {
        PortfolioSnapshot? snapshot = state.Current;
        var assets = state.Assets.ToDictionary(row => row.Asset.Id, StringComparer.Ordinal);
        bool stale = snapshot?.LastCheckedAt.AddHours(1) < now;
        PortfolioPosition[] positions = (snapshot?.Positions ?? []).Select(position =>
        {
            AssetData? asset = assets.GetValueOrDefault(position.AssetId);
            bool spot = asset is { Asset.Spot: true };
            Observation? price = asset is null ? null : Latest(asset, "price", now);
            Quality quality = !spot ? Quality.NotApplicable : price?.ValueDecimal is null ? Quality.Missing
                : price.QualityStatus != Quality.Ok ? price.QualityStatus
                : stale || price.SourceAsOf.AddHours(1) < now ? Quality.Stale : Quality.Ok;
            return new PortfolioPosition(position.AssetId, asset?.Asset.Symbol ?? position.AssetId,
                asset?.Asset.Name ?? position.AssetId, position.Quantity,
                quality == Quality.Ok ? position.Quantity * price!.ValueDecimal!.Value : null, null, quality, spot);
        }).ToArray();

        var valued = positions.Where(row => row.ValueUsd.HasValue).ToArray();
        ExactNumber? total = valued.Length == 0 ? null : valued.Aggregate(ExactNumber.Zero, (sum, row) => sum + row.ValueUsd!.Value);
        bool complete = snapshot is not null && !stale && valued.Length == positions.Length;
        if (complete && total is { Sign: > 0 } denominator)
            positions = positions.Select(row => row with { SharePct = (row.ValueUsd!.Value * ExactNumber.From(100)).Divide(denominator, 8) }).ToArray();

        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
        var upcoming = state.Assets.SelectMany(asset => CurrentEvents(asset.Events))
            .Where(item => item.Status is "scheduled" or "postponed" or "announced"
                && (item.DeadlineAt >= now || item.EventAt >= now || item.LocalDate >= today))
            .OrderBy(item => item.LocalDate ?? DateOnly.FromDateTime((item.DeadlineAt ?? item.EventAt)!.Value.UtcDateTime)).Take(5).ToArray();
        Job? last = state.Jobs.LastOrDefault(job => job.Kind == JobKind.PortfolioSync);
        return new("USD", total, snapshot is null ? Quality.Missing : stale ? Quality.Stale : complete ? Quality.Ok : Quality.Partial,
            snapshot?.SourcePortfolioId, snapshot?.LastCheckedAt, demo ? "demo_fixture" : Integrations.CmcAccess,
            demo, positions, last is null ? null : Receipt(last), upcoming);
    }

    public static JobReceipt Receipt(Job job) => new(job.Id, job.Status, job.CreatedAt, job.BlockReason);

    public static Observation? Latest(AssetData asset, string key, DateTimeOffset now) => asset.Observations
        .Where(item => item.MetricKey == key && item.SourceAsOf <= now && item.AssetId == asset.Asset.Id)
        .OrderByDescending(item => item.SourceAsOf).ThenByDescending(item => item.Revision).FirstOrDefault();

    public static FlowSeries[] Economy(AssetData asset, DateTimeOffset now)
    {
        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
        var month = new DateOnly(today.Year, today.Month, 1);
        var linked = asset.Links.Where(link => link.Status == MappingStatus.Confirmed).Select(link => link.EntityId).ToHashSet(StringComparer.Ordinal);
        return asset.Observations.Where(item => linked.Contains(item.EntityId) && item.ValueDecimal.HasValue
                && item.MetricKey is "chain_revenue" or "app_revenue" or "fees" or "revenue" or "holders_revenue"
                    or "incentives" or "operating_expenses" or "chain_fees" or "app_fees"
                && item.QualityStatus == Quality.Ok && item.PeriodEnd - item.PeriodStart == TimeSpan.FromDays(1)
                && item.PeriodStart.Offset == TimeSpan.Zero && item.PeriodStart.TimeOfDay == TimeSpan.Zero
                && (item.Scope != MetricScope.ProtocolChain || item.ChainId is not null))
            .GroupBy(item => (item.EntityId, item.ChainId, item.Scope, item.MetricKey, item.Unit, item.SourceId, item.MethodologyVersion))
            .Select(group =>
            {
                var series = group.GroupBy(item => item.PeriodStart).Select(items => items.MaxBy(item => item.Revision)!)
                    .Select(item => new DailyFlow(item.Id, DateOnly.FromDateTime(item.PeriodStart.UtcDateTime), item.ValueDecimal!.Value)).ToArray();
                return new FlowSeries(group.Key.MetricKey, group.Key.Scope, group.Key.Unit,
                    Enumerable.Range(-12, 13).Select(offset => Metrics.SumMonth(series, month.AddMonths(offset), today)).ToArray());
            }).ToArray();
    }

    public static SupplyView Supply(AssetData asset, DateTimeOffset now)
    {
        Observation? circulating = Latest(asset, "circulating_supply", now);
        ExactNumber? amount = circulating is { QualityStatus: Quality.Ok } ? circulating.ValueDecimal : null;
        return new(asset.MaximumSupplyKind, Latest(asset, "max_supply", now)?.ValueDecimal,
            Latest(asset, "total_supply", now)?.ValueDecimal, amount, circulating?.SourceAsOf,
            new[] { 30, 90, 365 }.Select(days => Metrics.Supply(asset.Supply, asset.Asset.Id, now, days, amount, asset.SupplyCoveredThrough)).ToArray(), asset.Supply);
    }

    public static IEnumerable<ProjectEvent> CurrentEvents(IEnumerable<ProjectEvent> events) =>
        events.GroupBy(item => item.Id, StringComparer.Ordinal).Select(group => group.MaxBy(item => item.Version)!);

    public static FeedPage Feed(Workspace state, string? asset, string? type, string? importance,
        DateOnly? from, DateOnly? to, bool? read, Guid? cursor)
    {
        var items = state.Feed.GroupBy(item => item.PublishKey).Select(group => group.MaxBy(item => item.Version)!)
            .Where(item => (asset is null || item.AssetId == asset) && (type is null || item.Type == type)
                && (importance is null || item.Analysis.Importance == importance) && (read is null || item.Read == read)
                && (from is null || DateOnly.FromDateTime(item.PublishedAt.UtcDateTime) >= from)
                && (to is null || DateOnly.FromDateTime(item.PublishedAt.UtcDateTime) <= to))
            .OrderByDescending(item => item.PublishedAt).ThenByDescending(item => item.Id).ToArray();
        int start = cursor is null ? 0 : Array.FindIndex(items, item => item.Id == cursor) + 1;
        if (cursor.HasValue && start == 0) throw new AppFault("invalid_cursor", "Курсор ленты устарел.");
        var page = items.Skip(start).Take(20).ToArray();
        return new(page, items.Length > start + page.Length ? page[^1].Id : null);
    }
}
