using System.Text.Json;
using System.Text.Json.Serialization;

namespace Portfel.Domain;

public sealed record Evidence(string Id, string AssetId, string SourceId, string Url, string Title,
    DateTimeOffset CheckedAt, string? MetricKey = null, ExactNumber? ValueDecimal = null, string? Unit = null,
    string? Period = null, MetricScope? Scope = null, Quality QualityStatus = Quality.Ok, string? RecordRevision = null,
    string? FormulaVersion = null, string[]? ObservationIds = null, bool Primary = false);
public sealed record SearchReceipt(string Provider, string Type, string ToolId, string[] Queries,
    string[] Urls, string[] EvidenceIds, DateTimeOffset CompletedAt, bool IsFixture = false);
public sealed record AnalysisInputV1(string SchemaVersion, string AssetId, DateTimeOffset AsOf, string? PortfolioHash,
    [property: JsonPropertyName("evidence_registry")] Evidence[] Evidence, SearchReceipt[] Research, bool Demo)
{
    public Guid SnapshotId { get; init; } = Guid.NewGuid();
    public Asset? Asset { get; init; }
    public string? SourcePortfolioId { get; init; }
    public AnalysisPortfolioContext? PortfolioContext { get; init; }
    public AnalysisCoverage[] Blocks { get; init; } = [];
    public AssetEntityLink[] EntityLinks { get; init; } = [];
    public ProjectEvent[] Events { get; init; } = [];
    public string[] Gaps { get; init; } = [];
    public string[] ChangesSincePrevious { get; init; } = [];
    public Guid? PreviousAnalysisId { get; init; }
    public string? PreviousAnalysisHash { get; init; }
    public string? InvestmentProfile { get; init; }
    [JsonIgnore]
    public string SnapshotHash => PortfelJson.Hash(this);
}

public sealed record AnalysisPortfolioContext(string Mode, DateTimeOffset CheckedAt, ExactNumber Quantity,
    ExactNumber? ValueUsd, ExactNumber? SharePct);
public sealed record AnalysisCoverage(AnalysisBlock Block, Quality DataStatus, string[] EvidenceIds);
public sealed record AnalysisSection(AnalysisBlock Block, Quality DataStatus, string Assessment, string Rationale, string[] EvidenceIds);
public sealed record NumericClaim(string MetricRef, ExactNumber ValueDecimal, string Unit, string Period, MetricScope Scope);
public sealed record Fact(string Id, string Text, string[] EvidenceIds, string Certainty, NumericClaim[] NumericClaims);
public sealed record Interpretation(string Text, string[] FactIds, string Uncertainty);
public sealed record AnalysisAction(string Type, string Rationale, string Condition, string[] EvidenceIds, string Urgency);
public sealed record AnalysisResultV1(string SchemaVersion, string AssetId, string SnapshotHash, string Headline,
    string Summary, string Severity, AnalysisSection[] Sections, string[] Changes, Fact[] Facts,
    Interpretation[] Interpretation, string PortfolioImpact, AnalysisAction[] Actions, string[] Unknowns, string NextReview);
public sealed record FeedItem(Guid Id, string AssetId, string Type, string PublishKey, DateTimeOffset PublishedAt,
    DateTimeOffset DataAsOf, string Personalization, AnalysisResultV1 Analysis, Evidence[] Evidence, bool Demo,
    int Version, Guid? SupersedesId, bool Read, string? PortfolioHash = null, Guid? SnapshotId = null,
    string? AssetSymbol = null);

public static class AnalysisValidation
{
    public const string SchemaVersion = "1.0";
    private static readonly JsonSerializerOptions StrictJson = new(PortfelJson.Options)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
    };

    public static AnalysisResultV1 Parse(string json)
    {
        using var payload = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32, AllowDuplicateProperties = false });
        AnalysisSchemas.RequireResult(payload.RootElement);
        return payload.RootElement.Deserialize<AnalysisResultV1>(StrictJson) ?? throw new JsonException("Ответ анализа пуст.");
    }

    public static string? Validate(AnalysisInputV1 snapshot, AnalysisResultV1 result)
    {
        if (snapshot.SchemaVersion != SchemaVersion || result.SchemaVersion != SchemaVersion || result.SnapshotHash != snapshot.SnapshotHash
            || result.AssetId != snapshot.AssetId || snapshot.Asset is not null && snapshot.Asset.Id != result.AssetId)
            return "invalid_snapshot";
        if (snapshot.Evidence.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Evidence.Length
            || snapshot.Evidence.Any(item => string.IsNullOrWhiteSpace(item.Id) || item.AssetId != result.AssetId
                || !IsPublicUrl(item.Url) || item.CheckedAt > snapshot.AsOf)) return "invalid_evidence";

        var evidence = snapshot.Evidence.ToDictionary(item => item.Id, StringComparer.Ordinal);
        if (!snapshot.Research.Any(receipt => MatchesSearch(receipt, snapshot.Demo)
            && receipt.CompletedAt <= snapshot.AsOf && receipt.EvidenceIds.Length > 0
            && receipt.EvidenceIds.All(id => evidence.TryGetValue(id, out Evidence? item) && receipt.Urls.Contains(item.Url) && item.SourceId == receipt.Provider)))
            return "limited_research";
        if (result.Sections.Length != 4 || result.Sections.Any(section => section is null || !Enum.IsDefined(section.Block))
            || result.Sections.Select(section => section.Block).Distinct().Count() != 4
            || result.Headline.EnumerateRunes().Count() > 140 || result.Summary.EnumerateRunes().Count() > 1200
            || !PlainText(result.Headline, result.Summary, result.PortfolioImpact, result.NextReview)
            || !PlainText(result.Changes) || !PlainText(result.Unknowns)
            || result.Severity is not ("info" or "watch" or "important" or "critical")) return "invalid_output";

        bool References(string[] entries) => entries.All(id => id is not null && evidence.ContainsKey(id));
        foreach (AnalysisSection section in result.Sections)
        {
            AnalysisCoverage? coverage = snapshot.Blocks.SingleOrDefault(item => item.Block == section.Block);
            if (!References(section.EvidenceIds) || !PlainText(section.Rationale) || !Enum.IsDefined(section.DataStatus)
                || coverage is not null && (section.DataStatus != coverage.DataStatus || section.EvidenceIds.Any(id => !coverage.EvidenceIds.Contains(id)))
                || section.Assessment is not ("improving" or "stable" or "deteriorating" or "mixed" or "undetermined")
                || section.Assessment != "undetermined" && (section.EvidenceIds.Length == 0
                    || section.DataStatus is Quality.Missing or Quality.Stale or Quality.Conflict or Quality.Error or Quality.NotApplicable))
                return "invalid_section";
        }

        var facts = new HashSet<string>(StringComparer.Ordinal);
        foreach (Fact fact in result.Facts)
        {
            if (fact is null || string.IsNullOrWhiteSpace(fact.Id) || !PlainText(fact.Text) || !facts.Add(fact.Id) || fact.EvidenceIds.Length == 0
                || !References(fact.EvidenceIds) || fact.Certainty is not ("confirmed" or "reported" or "disputed" or "unverified"))
                return "invalid_evidence";
            if (fact.Certainty == "confirmed" && fact.NumericClaims.Length == 0 && !fact.EvidenceIds.Any(id => evidence[id].Primary))
                return "unconfirmed_fact";
            foreach (NumericClaim claim in fact.NumericClaims)
            {
                if (claim is null || !fact.EvidenceIds.Contains(claim.MetricRef) || !evidence.TryGetValue(claim.MetricRef, out Evidence? item)
                    || item.MetricKey is null || item.ValueDecimal != claim.ValueDecimal || item.Unit != claim.Unit
                    || item.Period != claim.Period || item.Scope != claim.Scope || item.QualityStatus is not (Quality.Ok or Quality.Partial))
                    return "invalid_numeric_claim";
            }
        }

        if (result.Interpretation.Any(item => item is null || !PlainText(item.Text, item.Uncertainty)
            || item.FactIds.Length == 0 || !item.FactIds.All(facts.Contains))) return "invalid_interpretation";
        if (result.Actions.Any(action => action is null || !PlainText(action.Rationale, action.Condition, action.Urgency)
            || !References(action.EvidenceIds) || action.EvidenceIds.Length == 0
            || action.Type == "review_allocation" && snapshot.PortfolioContext is null
            || action.Type is not ("observe" or "verify" or "review_allocation" or "prepare"))) return "invalid_action";

        return null;
    }

    // Model prose is plain text. Numeric values are rendered separately from validated claims.
    private static bool PlainText(params string[] values) => values.All(value => !string.IsNullOrWhiteSpace(value)
        && !value.EnumerateRunes().Any(System.Text.Rune.IsNumber)
        && !value.Contains("://", StringComparison.Ordinal) && !value.Contains("www.", StringComparison.OrdinalIgnoreCase)
        && !value.Contains('<') && !value.Contains('>'));

    private static bool MatchesSearch(SearchReceipt receipt, bool demo) => !string.IsNullOrWhiteSpace(receipt.ToolId)
        && (receipt.IsFixture ? demo && receipt.Type == "fixture"
            : (receipt.Provider, receipt.Type) is ("openai" or "chatgpt_plan_oauth", "web_search_call")
                or ("anthropic", "web_search_tool_result") or ("search_connector", "web_search"));

    public static bool IsPublicUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.HostNameType == UriHostNameType.Dns && !uri.IsLoopback
        && uri.IdnHost.TrimEnd('.') is { } host && host.Contains('.') && host.Split('.')[^1].Any(char.IsAsciiLetter)
        && !new[] { ".localhost", ".local", ".internal", ".test", ".invalid", ".example", ".home.arpa" }
            .Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
}
