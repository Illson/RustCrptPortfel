using System.Text.Json;
using Portfel.Domain;

namespace Portfel.Application;

public sealed record AiCapabilities(bool WebSearch, bool StructuredOutput, bool Streaming, bool Usage,
    bool RemoteCancellation, string QuotaStatus, DateTimeOffset CheckedAt);
public sealed record AiRequest(string Model, string PublicOrPrivateInput, bool Research, JsonElement? OutputSchema, int TokenLimit);
public sealed record AiToolUse(string Type, string ToolId, string[] Queries, AiSource[] Sources);
public sealed record AiSource(string Url, string Title);
public sealed record AiReceipt(string Provider, string? RequestId, string Status, string? Failure,
    long? InputTokens, long? OutputTokens, string BillingMode, string? Text, AiToolUse[] Tools);

public interface IAiProvider
{
    string Id { get; }
    ValueTask<AiCapabilities> CapabilitiesAsync(CancellationToken cancellationToken);
    Task<AiReceipt> RunAsync(AiRequest request, CancellationToken cancellationToken);
}

public static class AiAnalysis
{
    public static async Task<(AnalysisInputV1? Snapshot, AiReceipt Receipt)> ResearchAsync(IAiProvider provider,
        string model, string assetId, string publicContext, DateTimeOffset now, CancellationToken cancellationToken)
    {
        AiCapabilities capabilities = await provider.CapabilitiesAsync(cancellationToken);
        if (!capabilities.WebSearch)
            return (null, new(provider.Id, null, "blocked", "unsupported_capability", null, null, "unknown", null, []));

        var receipt = await provider.RunAsync(new(model, publicContext, true, null, 2000), cancellationToken);
        if (receipt.Status != "completed") return (null, receipt);

        AiSource[] unique = receipt.Tools.SelectMany(tool => tool.Sources).Where(item => AnalysisValidation.IsPublicUrl(item.Url))
            .DistinctBy(item => item.Url, StringComparer.Ordinal).OrderBy(item => item.Url, StringComparer.Ordinal).ToArray();
        if (unique.Length == 0) return (null, receipt with { Status = "limited_research", Failure = "no_verified_search_receipt" });
        Evidence[] evidence = unique.Select(source => new Evidence(PortfelJson.Hash(source.Url), assetId, provider.Id,
            source.Url, source.Title, now)).ToArray();
        var byUrl = evidence.ToDictionary(item => item.Url, StringComparer.Ordinal);
        var search = receipt.Tools.Select(tool => new SearchReceipt(provider.Id, tool.Type, tool.ToolId, tool.Queries,
            tool.Sources.Select(source => source.Url).Where(byUrl.ContainsKey).Distinct(StringComparer.Ordinal).ToArray(),
            tool.Sources.Select(source => source.Url).Where(byUrl.ContainsKey).Distinct(StringComparer.Ordinal)
                .Select(url => byUrl[url].Id).ToArray(), now)).Where(tool => tool.EvidenceIds.Length > 0).ToArray();
        return (new("v1", assetId, now, null, evidence, search, false), receipt);
    }

    public static async Task<(AnalysisResultV1? Result, AiReceipt Receipt)> SynthesizeAsync(IAiProvider provider,
        string model, AnalysisInputV1 snapshot, JsonElement schema, CancellationToken cancellationToken)
    {
        AiCapabilities capabilities = await provider.CapabilitiesAsync(cancellationToken);
        if (!capabilities.StructuredOutput)
            return (null, new(provider.Id, null, "blocked", "unsupported_capability", null, null, "unknown", null, []));
        string input = JsonSerializer.Serialize(new { snapshot.SnapshotHash, Snapshot = snapshot }, PortfelJson.Options);
        var receipt = await provider.RunAsync(new(model, input, false, schema, 4000), cancellationToken);
        if (receipt.Status != "completed" || receipt.Text is null) return (null, receipt);
        try
        {
            AnalysisResultV1 result = AnalysisValidation.Parse(receipt.Text);
            string? error = AnalysisValidation.Validate(snapshot, result);
            return error is null ? (result, receipt) : (null, receipt with { Status = "failed", Failure = error, Text = null });
        }
        catch (JsonException) { return (null, receipt with { Status = "failed", Failure = "invalid_output", Text = null }); }
    }
}
