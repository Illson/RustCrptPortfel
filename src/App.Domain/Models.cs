using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Portfel.Domain;

public static class PortfelJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, false) }
    };

    public static string Hash<T>(T value) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Options)));
}

public enum Quality { Ok, Partial, Stale, Missing, NotApplicable, Conflict, Error }
public enum MetricScope { Chain, ProtocolGlobal, ProtocolChain, EcosystemApps, Token }
public enum MappingStatus { Confirmed, Ambiguous, Unmapped }
public enum AnalysisBlock { Economics, Supply, Markets, Project }

public sealed record Asset(string Id, string Name, string Symbol, bool Spot, AssetIdentifier[] Identifiers);
public sealed record AssetIdentifier(string Provider, string ExternalId, string? Chain = null, string? Contract = null);
public sealed record Entity(string Id, string Name, MetricScope Scope);
public sealed record AssetEntityLink(string AssetId, string EntityId, MappingStatus Status, string? SourceId);
public sealed record Position(string AssetId, ExactNumber Quantity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceSymbol = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsSpot = null);
public sealed record PortfolioFetch(string SourcePortfolioId, DateTimeOffset FetchedAt, DateTimeOffset? SourceAsOf,
    bool IsComplete, string FormatVersion, string Status, Position[] Positions);
public sealed record PortfolioSnapshot(string SourcePortfolioId, DateTimeOffset LastCheckedAt, DateTimeOffset? SourceAsOf,
    string Hash, Position[] Positions)
{
    public static PortfolioSnapshot Accept(PortfolioFetch incoming)
    {
        if (!incoming.IsComplete || incoming.Status != "ok" || incoming.FormatVersion != "v1")
            throw new InvalidOperationException("Снимок CMC неполон или имеет неподдерживаемый формат.");
        if (incoming.SourceAsOf > incoming.FetchedAt)
            throw new InvalidOperationException("Время снимка источника позже его получения.");
        if (incoming.Positions is null || incoming.Positions.Any(position => position is null))
            throw new InvalidOperationException("Состав снимка CMC нарушает контракт.");
        Position[] sorted = incoming.Positions.OrderBy(position => position.AssetId, StringComparer.Ordinal).ToArray();
        if (string.IsNullOrWhiteSpace(incoming.SourcePortfolioId) || sorted.Count(position => position.IsSpot != false) > 50
            || sorted.Any(position => position.Quantity.Sign <= 0 || string.IsNullOrWhiteSpace(position.AssetId))
            || sorted.Select(position => position.AssetId).Distinct(StringComparer.Ordinal).Count() != sorted.Length)
            throw new InvalidOperationException("Состав снимка CMC нарушает контракт.");
        return new(incoming.SourcePortfolioId, incoming.FetchedAt.ToUniversalTime(), incoming.SourceAsOf?.ToUniversalTime(),
            PortfelJson.Hash(new { incoming.SourcePortfolioId, Positions = sorted }), sorted);
    }
}

public sealed record Source(string Id, string Name, string Url, string Methodology);
public sealed record SourceRecord(string Id, string SourceId, string Url, string Title, DateTimeOffset? PublishedAt,
    DateTimeOffset? EventAt, DateTimeOffset FetchedAt, string ContentHash, string Methodology, bool Primary = false);
public sealed record Observation(string Id, string EntityId, string? AssetId, string? ChainId, string MetricKey,
    MetricScope Scope, ExactNumber? ValueDecimal, string Unit, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd,
    string SourceId, DateTimeOffset SourceAsOf, DateTimeOffset FetchedAt, Quality QualityStatus,
    string MethodologyVersion, string AdapterVersion, string RawRecordId, int Revision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset[]? ConfirmedAt = null)
{
    // Confirmations are strictly increasing; callers already exclude rows first fetched after asOf.
    public DateTimeOffset CheckedAsOf(DateTimeOffset asOf)
    {
        if (ConfirmedAt is not { Length: > 0 } checks) return FetchedAt;
        int index = Array.BinarySearch(checks, asOf);
        if (index < 0) index = ~index - 1;
        return index < 0 ? FetchedAt : checks[index];
    }
}
public sealed record DailyFlow(string ObservationId, DateOnly Day, ExactNumber Value, Quality QualityStatus = Quality.Ok);
public sealed record MonthlyFlow(DateOnly Month, ExactNumber? Total, int ExpectedIntervals, int ObservedIntervals,
    ExactNumber? CoverageRatio, Quality QualityStatus, bool Mtd, string[] EvidenceIds)
{
    public bool IsComplete => ExpectedIntervals > 0 && ExpectedIntervals == ObservedIntervals
        && QualityStatus is Quality.Ok or Quality.Stale;
    public Change? MonthOverMonth { get; init; }
    public Change? YearOverYear { get; init; }
    public Change? ComparableMtd { get; init; }
}
public sealed record Change(ExactNumber Absolute, ExactNumber? PercentagePoints, string Transition);

public enum SupplyKind { Unlock, Issuance, Burn, Treasury }
public enum Circulation { Yes, No, Unknown }
public sealed record SupplyEvent(string Id, string AssetId, SupplyKind Kind, DateTimeOffset StartsAt, DateTimeOffset? EndsAt,
    ExactNumber QuantityTotal, string? AllocationId, string Recipient, Circulation EntersCirculation,
    string SourceId, string ScheduleVersion, bool Projected, Circulation ExitsCirculation = Circulation.Unknown);
public sealed record SupplyWindow(int Days, Quality QualityStatus, ExactNumber? Unlocks, ExactNumber? NewIssuance,
    ExactNumber? Burns, ExactNumber? UnlocksPct, ExactNumber? NewIssuancePct, ExactNumber? GrossSupplyPressurePct,
    ExactNumber? NetTotalSupplyChange = null, ExactNumber? NetCirculatingSupplyChange = null);

public sealed record Market(string Id, string ExchangeId, string BaseAssetId, string QuoteAssetId, string Pair,
    string MarketType, string VenueType, ExactNumber? ReportedVolumeUsd, DateTimeOffset LastTradeAt, bool Active,
    Quality QualityStatus, string SourceId, ExactNumber? SpreadPercentagePoints = null, ExactNumber? DepthUsd = null,
    string? PoolId = null);
public sealed record VenueVolume(string ExchangeId, ExactNumber VolumeUsd, ExactNumber? SharePct);
public sealed record MarketSummary(int Venues, ExactNumber? ObservedVolume, ExactNumber? TopVenueShare,
    string MajorExchangeStatus, Quality QualityStatus, Market[] ActivePairs, VenueVolume[] Distribution);

public sealed record ProjectEvent(string Id, string AssetId, string Title, DateTimeOffset AnnouncedAt,
    DateOnly? LocalDate, DateTimeOffset? EventAt, DateTimeOffset? DeadlineAt, string? Timezone, string Precision,
    string Status, string[] SourceRefs, bool RequiresUserAction, int Version);
