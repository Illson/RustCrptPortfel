using System.Text.Json;
using Portfel.Domain;

namespace Portfel.Application;

public sealed record AiCapabilities(bool WebSearch, bool StructuredOutput, bool Streaming, bool Usage,
    bool RemoteCancellation, string QuotaStatus, DateTimeOffset CheckedAt, bool JsonOutput = false,
    string? Model = null, string Version = "1.0");
public sealed record AiRequest(string Model, string PublicOrPrivateInput, bool Research, JsonElement? OutputSchema, int TokenLimit);
public sealed record AiToolUse(string Type, string ToolId, string[] Queries, AiSource[] Sources);
public sealed record AiSource(string Url, string Title);
public sealed record AiReceipt(string Provider, string? RequestId, string Status, string? Failure,
    long? InputTokens, long? OutputTokens, string BillingMode, string? Text, AiToolUse[] Tools,
    TimeSpan? Duration = null, DateTimeOffset? RetryAt = null, ExactNumber? CostUsd = null);

public interface IAiProvider
{
    string Id { get; }
    ValueTask<AiCapabilities> CapabilitiesAsync(CancellationToken cancellationToken);
    Task<AiReceipt> RunAsync(AiRequest request, CancellationToken cancellationToken);
}

public static class AiAnalysis
{
    public static async Task<(AnalysisInputV1? Snapshot, AiReceipt Receipt)> ResearchAsync(IAiProvider provider,
        string model, AnalysisInputV1 seed, TimeProvider clock, CancellationToken cancellationToken)
    {
        AiCapabilities capabilities = await provider.CapabilitiesAsync(cancellationToken);
        if (!capabilities.WebSearch || !ForModel(capabilities, model) || seed.Asset is null)
            return (null, Blocked(provider));

        Asset asset = seed.Asset;
        string publicContext = JsonSerializer.Serialize(new
        {
            asset.Id, asset.Name, asset.Symbol,
            Identifiers = asset.Identifiers.Where(item => item.Provider is "cmc" or "coingecko" or "defillama"),
            Topics = "Релизы, инциденты, миграции, изменения сроков, governance, объявления площадок. Начни с первоисточников.",
        }, PortfelJson.Options);
        var receipt = await provider.RunAsync(new(model, publicContext, true, null, 2000), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (receipt.Status != "completed") return (null, receipt with { Text = null });

        DateTimeOffset now = clock.GetUtcNow();
        AiToolUse[] calls = receipt.Tools.Where(tool => (provider.Id, tool.Type) is
            ("openai" or "chatgpt_plan_oauth", "web_search_call") or ("anthropic", "web_search_tool_result")
            or ("search_connector", "web_search")).ToArray();
        AiSource[] unique = calls.SelectMany(tool => tool.Sources).Where(item => AnalysisValidation.IsPublicUrl(item.Url))
            .DistinctBy(item => item.Url, StringComparer.Ordinal).OrderBy(item => item.Url, StringComparer.Ordinal).ToArray();
        if (unique.Length == 0) return (null, receipt with { Text = null, Status = "limited_research", Failure = "no_verified_search_receipt" });
        Evidence[] evidence = unique.Select(source => new Evidence(PortfelJson.Hash(new { provider.Id, source.Url }), asset.Id, provider.Id,
            source.Url, source.Title, now, QualityStatus: Quality.Partial, RecordRevision: PortfelJson.Hash(source))).ToArray();
        var byUrl = evidence.ToDictionary(item => item.Url, StringComparer.Ordinal);
        var search = calls.Where(tool => !string.IsNullOrWhiteSpace(tool.ToolId)).Select(tool => new SearchReceipt(provider.Id, tool.Type, tool.ToolId, tool.Queries,
            tool.Sources.Select(source => source.Url).Where(byUrl.ContainsKey).Distinct(StringComparer.Ordinal).ToArray(),
            tool.Sources.Select(source => source.Url).Where(byUrl.ContainsKey).Distinct(StringComparer.Ordinal)
                .Select(url => byUrl[url].Id).ToArray(), now)).Where(tool => tool.EvidenceIds.Length > 0).ToArray();
        if (search.Length == 0) return (null, receipt with { Text = null, Status = "limited_research", Failure = "no_verified_search_receipt" });
        var linked = search.SelectMany(item => item.EvidenceIds).ToHashSet(StringComparer.Ordinal);
        evidence = evidence.Where(item => linked.Contains(item.Id)).ToArray();
        var final = seed with
        {
            AsOf = now,
            Evidence = [.. seed.Evidence, .. evidence],
            Research = search,
            Blocks = seed.Blocks.Select(block => block.Block != AnalysisBlock.Project ? block : block with
            {
                DataStatus = block.DataStatus == Quality.Missing ? Quality.Partial : block.DataStatus,
                EvidenceIds = [.. block.EvidenceIds, .. evidence.Select(item => item.Id)],
            }).ToArray(),
        };
        return (final, receipt with { Text = null });
    }

    public static async Task<(AnalysisResultV1? Result, AiReceipt Receipt)> SynthesizeAsync(IAiProvider provider,
        string model, AnalysisInputV1 snapshot, CancellationToken cancellationToken)
    {
        AiCapabilities capabilities = await provider.CapabilitiesAsync(cancellationToken);
        if ((!capabilities.StructuredOutput && !capabilities.JsonOutput) || !ForModel(capabilities, model))
            return (null, Blocked(provider));
        string input;
        try { input = AnalysisSchemas.SynthesisInput(snapshot); }
        catch (JsonException) { return (null, Blocked(provider) with { Failure = "invalid_snapshot" }); }
        var receipt = await provider.RunAsync(new(model, input, false, AnalysisSchemas.ProviderResultSchema, 4000), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (receipt.Status != "completed" || receipt.Text is null)
            return (null, receipt.Text is null && receipt.Status == "completed"
                ? receipt with { Status = "failed", Failure = "incomplete_response" } : receipt with { Text = null });
        try
        {
            AnalysisResultV1 result = AnalysisValidation.Parse(receipt.Text);
            string? error = AnalysisValidation.Validate(snapshot, result);
            return error is null ? (result, receipt with { Text = null }) : (null, receipt with { Status = "failed", Failure = error, Text = null });
        }
        catch (JsonException) { return (null, receipt with { Status = "failed", Failure = "invalid_output", Text = null }); }
    }

    private static bool ForModel(AiCapabilities settings, string model) => settings.Model is null || settings.Model == model;

    private static AiReceipt Blocked(IAiProvider provider) =>
        new(provider.Id, null, "blocked", "blocked_capability", null, null, "unknown", null, []);
}
