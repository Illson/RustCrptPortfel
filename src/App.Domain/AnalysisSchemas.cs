using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Portfel.Domain;

public static class AnalysisSchemas
{
    private static readonly Lazy<(JsonElement Json, JsonSchema Schema)> InputContract = new(() => Load("analysis-input-v1.schema.json"));
    private static readonly Lazy<(JsonElement Json, JsonSchema Schema)> ResultContract = new(() => Load("analysis-result-v1.schema.json"));
    private static readonly Lazy<JsonElement> ProviderContract = new(() =>
    {
        var schema = JsonNode.Parse(ResultContract.Value.Json.GetRawText())!.AsObject();
        MakePortable(schema);
        return JsonSerializer.SerializeToElement(schema);
    });

    public static JsonElement ProviderResultSchema => ProviderContract.Value;

    public static string SynthesisInput(AnalysisInputV1 snapshot)
    {
        var node = JsonSerializer.SerializeToNode(snapshot, PortfelJson.Options)!.AsObject();
        node["snapshot_hash"] = snapshot.SnapshotHash;
        JsonElement payload = JsonSerializer.SerializeToElement(node);
        if (!InputContract.Value.Schema.Evaluate(payload).IsValid) throw new JsonException("Некорректный снимок анализа.");
        return payload.GetRawText();
    }

    internal static void RequireResult(JsonElement payload)
    {
        if (!ResultContract.Value.Schema.Evaluate(payload).IsValid) throw new JsonException("Ответ не соответствует AnalysisResultV1.");
    }

    private static (JsonElement, JsonSchema) Load(string name)
    {
        using Stream stream = typeof(AnalysisSchemas).Assembly.GetManifestResourceStream(name)!;
        using var document = JsonDocument.Parse(stream);
        JsonElement schema = document.RootElement.Clone();
        return (schema, JsonSchema.Build(schema, new BuildOptions { Dialect = Dialect.Draft202012, SchemaRegistry = new() }));
    }

    // The three provider dialects do not implement the whole 2020-12 vocabulary.
    // Keep the same shape/enums for native output and enforce these constraints on the server.
    private static void MakePortable(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (!obj.ContainsKey("type") && obj["enum"] is JsonArray enumeration && enumeration.All(item => item?.GetValueKind() == JsonValueKind.String))
                obj["type"] = "string";
            foreach (string keyword in new[] { "$schema", "title", "minLength", "maxLength", "minItems", "maxItems", "pattern" })
                obj.Remove(keyword);
            foreach (var item in obj)
                if (item.Value is not null) MakePortable(item.Value);
        }
        else if (node is JsonArray items)
        {
            foreach (var item in items)
                if (item is not null) MakePortable(item);
        }
    }
}
