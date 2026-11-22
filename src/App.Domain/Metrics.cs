namespace Portfel.Domain;

public static class Metrics
{
    public const int PercentageDigits = 8;
    private static readonly ExactNumber Hundred = ExactNumber.From(100);

    public static MonthlyFlow SumMonth(IEnumerable<DailyFlow> days, DateOnly month, DateOnly asOf)
    {
        if (month.Day != 1 || month > asOf) throw new ArgumentOutOfRangeException(nameof(month));
        bool mtd = month.Year == asOf.Year && month.Month == asOf.Month;
        DateOnly end = mtd ? asOf : month.AddMonths(1);
        int expected = end.DayNumber - month.DayNumber;
        var groups = days.Where(day => day.Day >= month && day.Day < end).GroupBy(day => day.Day).ToArray();
        if (groups.Any(group => group.Select(day => day.Value).Distinct().Skip(1).Any()))
            return new(month, null, expected, groups.Length, null, Quality.Conflict, mtd, []);

        DailyFlow[] observed = groups.Select(group => group.OrderBy(day => day.ObservationId, StringComparer.Ordinal).First()).ToArray();
        return new(month, observed.Length == 0 ? null : observed.Aggregate(ExactNumber.Zero, (sum, day) => sum + day.Value),
            expected, observed.Length, expected == 0 ? null : ExactNumber.From(observed.Length).Divide(ExactNumber.From(expected), PercentageDigits),
            observed.Length == 0 ? Quality.Missing : observed.Length == expected ? Quality.Ok : Quality.Partial,
            mtd, observed.Select(day => day.ObservationId).Order(StringComparer.Ordinal).ToArray());
    }

    public static Change? Compare(MonthlyFlow current, MonthlyFlow previous)
    {
        if (current.QualityStatus != Quality.Ok || previous.QualityStatus != Quality.Ok
            || current.Mtd != previous.Mtd || (current.Mtd && current.ExpectedIntervals != previous.ExpectedIntervals)
            || current.Total is not { } value || previous.Total is not { } baseline) return null;
        ExactNumber difference = value - baseline;
        return new(difference, baseline.Sign > 0 ? (difference * Hundred).Divide(baseline, PercentageDigits) : null,
            baseline.Sign switch { 0 => "zero_base", < 0 => "negative_base", _ => "positive_base" });
    }

    public static SupplyWindow Supply(IEnumerable<SupplyEvent> source, string assetId, DateTimeOffset asOf,
        int days, ExactNumber? circulating, DateTimeOffset? coveredThrough, DateTimeOffset? coveredFrom = null, bool burnsCovered = false)
    {
        if (days is not (30 or 90 or 365)) throw new ArgumentOutOfRangeException(nameof(days));
        DateTimeOffset end = asOf.AddDays(days);
        SupplyEvent[] schedule = source.Where(item => item.AssetId == assetId).ToArray();
        if (schedule.Any(item => item.QuantityTotal.Sign < 0 || item.EndsAt <= item.StartsAt))
            return new(days, Quality.Error, null, null, null, null, null, null);
        SupplyEvent[] candidates = schedule.Where(item => Overlaps(item, asOf, end)).ToArray();
        if (coveredThrough is null && candidates.Length == 0)
            return new(days, Quality.Missing, null, null, null, null, null, null);

        SupplyEvent[] events = candidates.OrderBy(item => item.Id, StringComparer.Ordinal)
            .DistinctBy(item => (item.Kind, item.AllocationId, FallbackId: item.AllocationId is null ? (item.SourceId, item.Id) : default,
                item.Recipient, item.StartsAt, item.EndsAt, item.QuantityTotal, item.EntersCirculation, item.ExitsCirculation)).ToArray();
        ExactNumber unlocks = ExactNumber.Zero, issuance = ExactNumber.Zero, burns = ExactNumber.Zero, gross = ExactNumber.Zero;
        ExactNumber exits = ExactNumber.Zero;
        HashSet<SupplyKind> ambiguous = [];
        bool uncertain = false, uncertainBurns = false;
        for (int index = 0; index < events.Length; index++)
        {
            SupplyEvent item = events[index];
            foreach (SupplyEvent other in events.Take(index))
            {
                if (item.Recipient != other.Recipient || (item.Kind == SupplyKind.Burn) != (other.Kind == SupplyKind.Burn)
                    || (item.AllocationId is not null && other.AllocationId is not null && item.AllocationId != other.AllocationId)
                    || !Overlaps(other, item.StartsAt, item.EndsAt ?? item.StartsAt.AddTicks(1))) continue;
                if (item.Kind == SupplyKind.Burn) uncertainBurns = true;
                else if (item.EntersCirculation != Circulation.No || other.EntersCirculation != Circulation.No) uncertain = true;
                if (item.Kind == other.Kind) ambiguous.Add(item.Kind);
            }

            ExactNumber quantity = Within(item, asOf, end);
            if (item.Kind == SupplyKind.Unlock) unlocks += quantity;
            if (item.Kind == SupplyKind.Issuance) issuance += quantity;
            if (item.Kind == SupplyKind.Burn)
            {
                burns += quantity;
                if (item.ExitsCirculation == Circulation.Yes) exits += quantity;
                else if (item.ExitsCirculation == Circulation.Unknown) uncertainBurns = true;
            }
            else if (item.EntersCirculation == Circulation.Yes) gross += quantity;
            else if (item.EntersCirculation == Circulation.Unknown) uncertain = true;
        }

        bool complete = coveredFrom <= asOf && coveredThrough >= end;
        ExactNumber? unlockAmount = ambiguous.Contains(SupplyKind.Unlock) || (!complete && unlocks.Sign == 0) ? null : unlocks;
        ExactNumber? issueAmount = ambiguous.Contains(SupplyKind.Issuance) || (!complete && issuance.Sign == 0) ? null : issuance;
        ExactNumber? burnAmount = ambiguous.Contains(SupplyKind.Burn) || (!(complete && burnsCovered) && burns.Sign == 0) ? null : burns;
        Quality quality = circulating is null ? Quality.Missing : circulating.Value.Sign <= 0 ? Quality.Error
            : ambiguous.Count > 0 ? Quality.Conflict : uncertain || !complete ? Quality.Partial : Quality.Ok;
        return new(days, quality, unlockAmount, issueAmount, burnAmount, Percent(unlockAmount, circulating),
            Percent(issueAmount, circulating), complete && !uncertain ? Percent(gross, circulating) : null,
            complete && burnsCovered ? issueAmount - burnAmount : null,
            complete && burnsCovered && !uncertain && !uncertainBurns ? gross - exits : null);
    }

    private static bool Overlaps(SupplyEvent item, DateTimeOffset begin, DateTimeOffset end) =>
        item.StartsAt < end && (item.EndsAt is { } stop ? stop > begin : item.StartsAt >= begin);

    private static ExactNumber Within(SupplyEvent item, DateTimeOffset begin, DateTimeOffset end)
    {
        if (item.EndsAt is not { } stop) return item.QuantityTotal;
        TimeSpan overlap = (stop < end ? stop : end) - (item.StartsAt > begin ? item.StartsAt : begin);
        TimeSpan duration = stop - item.StartsAt;
        return overlap == duration ? item.QuantityTotal
            : (item.QuantityTotal * ExactNumber.From(overlap.Ticks)).Divide(ExactNumber.From(duration.Ticks), 96);
    }

    private static ExactNumber? Percent(ExactNumber? value, ExactNumber? denominator) =>
        value is { } amount && denominator is { Sign: > 0 } baseline ? (amount * Hundred).Divide(baseline, PercentageDigits) : null;

    public static MarketSummary Markets(IEnumerable<Market> markets, string assetId, DateTimeOffset asOf, string[] major)
    {
        Market[] observations = markets.Where(row => row.BaseAssetId == assetId && row.MarketType == "spot"
            && row.LastTradeAt <= asOf && !string.IsNullOrWhiteSpace(row.QuoteAssetId))
            .GroupBy(row => (row.ExchangeId, row.VenueType, row.BaseAssetId, row.QuoteAssetId, row.PoolId))
            .Select(group => group.OrderByDescending(row => row.LastTradeAt).ThenBy(row => row.SourceId, StringComparer.Ordinal).First()).ToArray();
        bool stale = observations.Any(row => row.QualityStatus == Quality.Stale || row.LastTradeAt < asOf.AddHours(-1));
        Market[] pairs = observations.Where(row => row.Active && row.LastTradeAt >= asOf.AddHours(-1) && row.QualityStatus == Quality.Ok)
            .Select(row => row.ReportedVolumeUsd is { Sign: < 0 } ? row with { ReportedVolumeUsd = null } : row)
            .OrderBy(row => row.ExchangeId, StringComparer.Ordinal).ThenBy(row => row.Pair, StringComparer.Ordinal).ToArray();
        var volumes = pairs.Where(row => row.ReportedVolumeUsd is { Sign: >= 0 }).GroupBy(row => row.ExchangeId)
            .Select(group => (group.Key, Total: group.Aggregate(ExactNumber.Zero, (sum, row) => sum + row.ReportedVolumeUsd!.Value))).ToArray();
        ExactNumber? total = volumes.Length == 0 ? null : volumes.Aggregate(ExactNumber.Zero, (sum, volume) => sum + volume.Total);
        return new(pairs.Select(row => row.ExchangeId).Distinct(StringComparer.Ordinal).Count(), total,
            volumes.Length == 0 ? null : Percent(volumes.Max(row => row.Total), total),
            major.Length == 0 ? "not_configured" : pairs.Any(row => major.Contains(row.ExchangeId, StringComparer.Ordinal))
                ? "confirmed_spot" : "unconfirmed",
            pairs.Length == 0 ? stale ? Quality.Stale : Quality.Missing
                : stale || pairs.Any(row => row.ReportedVolumeUsd is not { Sign: >= 0 }) ? Quality.Partial : Quality.Ok,
            pairs, volumes.Select(row => new VenueVolume(row.Key, row.Total, Percent(row.Total, total))).ToArray());
    }
}
