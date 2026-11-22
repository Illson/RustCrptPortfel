using System.Text.Json.Serialization;
using Portfel.Domain;

namespace Portfel.Application;

public sealed record Settings(string Timezone, string DailyTime, string[] MajorExchangeIds, bool ScheduleEnabled,
    string? InvestmentProfile = null)
{
    public static Settings Default => new("Africa/Nairobi", "08:00", [], false);
}

public sealed record Integration(string Id, string Name, string Status, string Details, string Url, DateTimeOffset? LastSuccessAt = null);
public sealed record IntegrationSet(bool DemoAvailable, string Authentication, string PortfolioAccess, Integration[] Sources);

public static class Integrations
{
    public const string CmcAuth = "BLOCKED_CMC_AUTH";
    public const string CmcAccess = "BLOCKED_CMC_ACCESS";

    public static IntegrationSet Get(bool demoAvailable) => new(demoAvailable, CmcAuth, CmcAccess,
    [
        new("cmc_identity", "CoinMarketCap · вход", CmcAuth, "Официальный контракт входа для приложения не подтверждён.", "https://coinmarketcap.com/api/documentation/guides/authentication/"),
        new("cmc_portfolio", "CoinMarketCap · портфель", CmcAccess, "Разрешённый канал чтения личного портфеля не подтверждён.", "https://coinmarketcap.com/api/mcp/"),
        new("cmc_market", "CoinMarketCap · рыночные данные", "not_connected", "Публичные котировки доступны отдельно от входа и портфеля. Сбор рабочих данных отключён.", "https://coinmarketcap.com/api/documentation/pro-api-reference/keyless-public-api/"),
        new("defillama", "DefiLlama", "not_connected", "История сборов и выручки. Покрытие зависит от проекта.", "https://defillama.com/data-definitions"),
        new("defillama_pro", "DefiLlama Pro", "unsupported_by_plan", "Для расписаний выпуска требуется подтверждённый доступ Pro.", "https://api-docs.defillama.com/llms-pro.txt"),
        new("coingecko", "CoinGecko", "not_connected", "Наблюдения по торговым парам и качеству рынка.", "https://docs.coingecko.com/reference/coins-id-tickers"),
        new("research", "Веб-поиск", "not_connected", "Для проверки новостей нужен подключённый маршрут исследования.", "https://developers.openai.com/api/docs/guides/tools-web-search"),
    ]);
}

public enum JobStatus { Queued, Running, Succeeded, RetryWait, Failed, Blocked, Cancelled }
public enum JobKind { PortfolioSync, AssetAnalysis }
public sealed record Job(Guid Id, JobKind Kind, string? AssetId, string IdempotencyKey, string RequestHash,
    JobStatus Status, DateTimeOffset CreatedAt, int Attempt = 0, Guid? Lease = null, DateTimeOffset? LeaseUntil = null,
    string? BlockReason = null, DateTimeOffset? FinishedAt = null);
public sealed record JobReceipt(Guid JobId, JobStatus Status, DateTimeOffset CreatedAt, string? BlockReason);

public sealed record AssetData(Asset Asset, AssetEntityLink[] Links, Observation[] Observations, SupplyEvent[] Supply,
    DateTimeOffset? SupplyCoveredThrough, Market[] Markets, ProjectEvent[] Events, Source[] Sources, string MaximumSupplyKind,
    DateTimeOffset? SupplyCoveredFrom = null, bool BurnsCovered = false, SourceRecord[]? SourceRecords = null,
    MarketCoverage[]? MarketCoverage = null);

public sealed record MarketCoverage(string SourceId, DateTimeOffset CheckedAt, bool Complete);

public sealed record PortfolioCheck(DateTimeOffset FetchedAt, string Status, string? SnapshotHash = null);

public sealed record Workspace(PortfolioSnapshot[] Snapshots, AssetData[] Assets, Settings Settings, Job[] Jobs,
    FeedItem[] Feed, AiConnection[] AiConnections, AiRouteSettings AiRoutes, PortfolioCheck? LatestCheck = null,
    StoredAnalysis[]? AnalysisSnapshots = null, AiRunEntry[]? AiExecutions = null, FeedReport[]? FeedReports = null)
{
    public static Workspace Empty => new([], [], Settings.Default, [], [], [], new(null, null, false));
    [JsonIgnore]
    public PortfolioSnapshot? Current => Snapshots.LastOrDefault();

    public Workspace Accept(PortfolioFetch fetch)
    {
        PortfolioSnapshot snapshot = PortfolioSnapshot.Accept(fetch);
        if (Current is { } current && snapshot.LastCheckedAt < current.LastCheckedAt) return this;
        if (Current is { } previous && previous.SourcePortfolioId == snapshot.SourcePortfolioId
            && previous.SourceAsOf is { } lastSource && snapshot.SourceAsOf < lastSource)
            return this with { LatestCheck = new(snapshot.LastCheckedAt, "stale_response") };
        return this with
        {
            LatestCheck = new(snapshot.LastCheckedAt, "ok", snapshot.Hash),
            Snapshots = Current?.Hash == snapshot.Hash ? [.. Snapshots[..^1], snapshot] : [.. Snapshots, snapshot],
        };
    }

    public Workspace RecordSync(PortfolioFetch fetch)
    {
        if (LatestCheck is { } last && fetch.FetchedAt < last.FetchedAt) return this;
        try { return Accept(fetch); }
        catch (InvalidOperationException)
        {
            string status = fetch.Status switch
            {
                "authentication_required" or "permission_denied" or "rate_limited" or "provider_unavailable"
                    or "schema_error" or "snapshot_changed" or "portfolio_limit" => fetch.Status,
                _ => fetch.IsComplete ? "invalid_snapshot" : "incomplete_snapshot",
            };
            return this with { LatestCheck = new(fetch.FetchedAt.ToUniversalTime(), status) };
        }
    }
}

public sealed record StoredAnalysis(Guid SnapshotId, string ContentHash, string ContentJson, DateTimeOffset AsOf);
public sealed record FeedReport(Guid Id, Guid FeedItemId, string Reason, DateTimeOffset CreatedAt);

public interface IWorkspaceStore
{
    Task<Job?> ClaimAsync(string ownerId, TimeProvider clock, CancellationToken ct);
    Task<Workspace> ReadAsync(string ownerId, CancellationToken cancellationToken);
    Task<T> UpdateAsync<T>(string ownerId, Func<Workspace, (Workspace Next, T Result)> change, CancellationToken cancellationToken);
    Task<bool> ReadyAsync(CancellationToken cancellationToken);
}

public sealed record AiConnection(Guid Id, string Provider, string Name, string Model, string AuthMode,
    string BillingMode, string Status, string? ProtectedSecret = null, DateTimeOffset? CheckedAt = null);
public sealed record AiConnectionView(Guid Id, string Provider, string Name, string Model, string AuthMode,
    string BillingMode, string Status, DateTimeOffset? CheckedAt);
public sealed record AiRouteSettings(Guid? Research, Guid? Synthesis, bool PaidFallback);

public sealed record PortfolioPosition(string AssetId, string Symbol, string Name, ExactNumber Quantity,
    ExactNumber? ValueUsd, ExactNumber? SharePct, Quality QualityStatus, bool Spot);
public sealed record PortfolioView(string Currency, ExactNumber? TotalUsd, Quality QualityStatus,
    string? SourcePortfolioId, DateTimeOffset? LastSyncAt, string SyncStatus, bool Demo, PortfolioPosition[] Positions,
    JobReceipt? LatestJob, ProjectEvent[] UpcomingEvents);
public sealed record AssetView(Asset Asset, PortfolioPosition? Position, AssetEntityLink[] Links,
    DateTimeOffset? DataAsOf, string? LastConclusion, FeedItem[] Latest, bool Updating);
public sealed record FlowSeries(string MetricKey, MetricScope Scope, string Unit, MonthlyFlow[] Months,
    string EntityId, string? AssetId, string? ChainId, string SourceId, string MethodologyVersion, Change? ThreeMonths);
public sealed record SupplyView(string MaximumSupplyKind, ExactNumber? MaximumSupply, ExactNumber? TotalSupply,
    ExactNumber? CirculatingSupply, DateTimeOffset? DataAsOf, SupplyWindow[] Windows, SupplyEvent[] Schedule, Quality CirculationStatus);
public sealed record FeedAsset(string AssetId, string Symbol);
public sealed record FeedPage(FeedItem[] Items, Guid? NextCursor, bool Updating, FeedAsset[] Assets);

public sealed class AppFault(string code, string message, int status = 422) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
