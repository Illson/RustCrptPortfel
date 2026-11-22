using System.Text.Json;
using System.Text.Json.Serialization;
using Portfel.Application;
using Portfel.Domain;

namespace Portfel.Api;

// Only the HTTP v1 representation uses these names. Stored snapshots and provider schemas are unchanged.
internal static class LegacyFeedJson
{
    internal static JsonSerializerOptions Options { get; } = new(PortfelJson.Options)
    {
        Converters = { new LegacyAnalysisWriter() },
    };

    private sealed class LegacyAnalysisWriter : JsonConverter<AnalysisResultV1>
    {
        public override AnalysisResultV1 Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, AnalysisResultV1 value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new
            {
                SchemaVersion = "v1", value.AssetId, value.SnapshotHash, Title = value.Headline, value.Summary,
                Importance = PortfolioQueries.LegacyImportance(value.Severity),
                Sections = value.Sections.Select(section => new
                {
                    Block = section.Block switch
                    {
                        AnalysisBlock.Economics => "economy", AnalysisBlock.Project => "development",
                        AnalysisBlock.Supply => "supply", AnalysisBlock.Markets => "markets",
                        _ => throw new JsonException("Unknown analysis block."),
                    },
                    section.DataStatus, section.Assessment, section.Rationale, section.EvidenceIds,
                }),
                value.Changes, value.Facts, value.Interpretation, value.PortfolioImpact, value.Actions, value.Unknowns, value.NextReview,
            }, options);
    }
}
