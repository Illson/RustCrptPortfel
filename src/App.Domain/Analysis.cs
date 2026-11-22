using System.Text.Json;
using System.Text.Json.Serialization;

namespace Portfel.Domain;

public sealed record Evidence(string Id, string AssetId, string SourceId, string Url, string Title,
    DateTimeOffset CheckedAt, string? MetricKey = null, ExactNumber? ValueDecimal = null, string? Unit = null,
    string? Period = null, MetricScope? Scope = null);
public sealed record SearchReceipt(string Provider, string Type, string ToolId, string[] Queries,
    string[] Urls, string[] EvidenceIds, DateTimeOffset CompletedAt, bool IsFixture = false);
public sealed record AnalysisInputV1(string SchemaVersion, string AssetId, DateTimeOffset AsOf, string? PortfolioHash,
    Evidence[] Evidence, SearchReceipt[] Research, bool Demo)
{
    [JsonIgnore]
    public string SnapshotHash => PortfelJson.Hash(this);
}

public sealed record AnalysisSection(AnalysisBlock Block, Quality DataStatus, string Assessment, string Rationale, string[] EvidenceIds);
public sealed record NumericClaim(string MetricRef, ExactNumber ValueDecimal, string Unit, string Period, MetricScope Scope);
public sealed record Fact(string Id, string Text, string[] EvidenceIds, string Certainty, NumericClaim[] NumericClaims);
public sealed record Interpretation(string Text, string[] FactIds, string Uncertainty);
public sealed record AnalysisAction(string Type, string Rationale, string Condition, string[] EvidenceIds, string Urgency);
public sealed record AnalysisResultV1(string SchemaVersion, string AssetId, string SnapshotHash, string Title,
    string Summary, string Importance, AnalysisSection[] Sections, string[] Changes, Fact[] Facts,
    Interpretation[] Interpretation, string PortfolioImpact, AnalysisAction[] Actions, string[] Unknowns, string NextReview);
public sealed record FeedItem(Guid Id, string AssetId, string Type, string PublishKey, DateTimeOffset PublishedAt,
    DateTimeOffset DataAsOf, string Personalization, AnalysisResultV1 Analysis, Evidence[] Evidence, bool Demo,
    int Version, Guid? SupersedesId, bool Read);

public static class AnalysisValidation
{
    private static readonly JsonSerializerOptions StrictJson = new(PortfelJson.Options)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static AnalysisResultV1 Parse(string json) =>
        JsonSerializer.Deserialize<AnalysisResultV1>(json, StrictJson) ?? throw new JsonException("Ответ анализа пуст.");

    public static string? Validate(AnalysisInputV1 snapshot, AnalysisResultV1 result)
    {
        if (snapshot.SchemaVersion != "v1" || result.SchemaVersion != "v1" || result.SnapshotHash != snapshot.SnapshotHash
            || result.AssetId != snapshot.AssetId) return "invalid_snapshot";
        if (snapshot.Evidence.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Evidence.Length
            || snapshot.Evidence.Any(item => item.AssetId != result.AssetId || !IsPublicUrl(item.Url))) return "invalid_evidence";

        var evidence = snapshot.Evidence.ToDictionary(item => item.Id, StringComparer.Ordinal);
        if (!snapshot.Research.Any(receipt => (!receipt.IsFixture || snapshot.Demo) && receipt.EvidenceIds.Length > 0
            && receipt.EvidenceIds.All(id => evidence.TryGetValue(id, out Evidence? item) && receipt.Urls.Contains(item.Url))))
            return "limited_research";
        if (result.Sections.Length != 4 || result.Sections.Select(section => section.Block).Distinct().Count() != 4
            || !Present(result.Title, result.Summary, result.PortfolioImpact, result.NextReview)
            || result.Importance is not ("normal" or "important" or "critical")) return "invalid_output";

        bool References(string[] entries) => entries.All(id => id is not null && evidence.ContainsKey(id));
        foreach (AnalysisSection section in result.Sections)
        {
            if (section is null || !References(section.EvidenceIds) || !Present(section.Rationale)
                || section.Assessment is not ("improving" or "stable" or "deteriorating" or "mixed" or "undetermined")
                || (section.Assessment != "undetermined" && section.EvidenceIds.Length == 0)) return "invalid_section";
        }

        var facts = new HashSet<string>(StringComparer.Ordinal);
        foreach (Fact fact in result.Facts)
        {
            if (fact is null || !Present(fact.Id, fact.Text) || !facts.Add(fact.Id) || fact.EvidenceIds.Length == 0
                || !References(fact.EvidenceIds) || fact.Certainty is not ("confirmed" or "reported" or "disputed" or "unverified"))
                return "invalid_evidence";
            foreach (NumericClaim claim in fact.NumericClaims)
            {
                if (claim is null || !fact.EvidenceIds.Contains(claim.MetricRef) || !evidence.TryGetValue(claim.MetricRef, out Evidence? item)
                    || item.MetricKey is null || item.ValueDecimal != claim.ValueDecimal || item.Unit != claim.Unit
                    || item.Period != claim.Period || item.Scope != claim.Scope) return "invalid_numeric_claim";
            }
        }

        if (result.Interpretation.Any(item => item is null || !Present(item.Text, item.Uncertainty)
            || item.FactIds.Length == 0 || !item.FactIds.All(facts.Contains))) return "invalid_interpretation";
        if (result.Actions.Any(action => action is null || !Present(action.Rationale, action.Condition, action.Urgency)
            || !References(action.EvidenceIds) || action.EvidenceIds.Length == 0
            || action.Type is not ("observe" or "verify" or "review_allocation" or "prepare"))) return "invalid_action";

        return null;
    }

    private static bool Present(params string[] values) => values.All(value => !string.IsNullOrWhiteSpace(value));

    public static bool IsPublicUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.HostNameType == UriHostNameType.Dns && !uri.IsLoopback
        && uri.Host.Contains('.') && !uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
        && !uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
}
