using Portfel.Domain;

namespace Portfel.Application;

public sealed record SourceCapability(string Id, string[] Metrics, string History, TimeSpan RefreshInterval, string Access);
public sealed record SourceBatch(string Status, DateTimeOffset FetchedAt, Observation[] Observations, SourceRecord[] Records,
    Market[] Markets, string? MaximumSupplyKind = null, DateTimeOffset? RetryAt = null)
{
    public static SourceBatch Failed(string status, DateTimeOffset at, DateTimeOffset? retryAt = null) =>
        new(status, at, [], [], [], RetryAt: retryAt);
}

public interface IPublicQuoteSource
{
    SourceCapability Capability { get; }
    Task<SourceBatch> ReadAsync(Asset asset, CancellationToken cancellationToken);
}

public static class PublicSourceRevisions
{
    public static AssetData Merge(AssetData asset, SourceBatch batch, string sourceId)
    {
        // Diagnostic failures, including partial pages with an error, must not replace the last good data.
        if (batch.Status is not ("ok" or "partial")) return asset;
        if (batch.Observations.Any(item => item.AssetId != asset.Asset.Id || item.SourceId != sourceId
                || item.Scope == MetricScope.ProtocolChain && item.ChainId is null)
            || batch.Records.Any(item => item.SourceId != sourceId || !AnalysisValidation.IsPublicUrl(item.Url))
            || batch.Markets.Any(item => item.BaseAssetId != asset.Asset.Id || item.SourceId != sourceId))
            throw new InvalidOperationException("Пакет источника не относится к активу или нарушает область метрики.");
        var rows = asset.Observations.ToList();
        var indices = Enumerable.Range(0, rows.Count).GroupBy(index => Key(rows[index]))
            .ToDictionary(group => group.Key, group => group.MaxBy(index => rows[index].Revision));
        foreach (Observation sample in batch.Observations)
        {
            var key = Key(sample);
            int revision = 1;
            if (indices.TryGetValue(key, out int index))
            {
                Observation current = rows[index];
                var lastCheck = current.ConfirmedAt is { Length: > 0 } checks ? checks[^1] : current.FetchedAt;
                if (lastCheck > sample.FetchedAt) continue;
                if (current.ValueDecimal == sample.ValueDecimal && current.QualityStatus == sample.QualityStatus)
                {
                    if (sample.FetchedAt > lastCheck)
                        rows[index] = current with { ConfirmedAt = [.. current.ConfirmedAt ?? [], sample.FetchedAt] };
                    continue;
                }
                revision = current.Revision + 1;
            }
            indices[key] = rows.Count;
            rows.Add(sample with { Revision = revision, Id = PortfelJson.Hash(new { key, revision }), ConfirmedAt = null });
        }

        Market[] markets = batch.Status == "ok" && batch.Markets.Length == 0 && sourceId == "coingecko"
            ? asset.Markets.Where(item => item.SourceId != sourceId).ToArray()
            : batch.Markets.Length == 0 ? asset.Markets
            : (batch.Status == "ok" ? asset.Markets.Where(item => item.SourceId != sourceId) : asset.Markets)
                .Concat(batch.Markets).DistinctBy(item => (item.SourceId, item.Id)).ToArray();
        return asset with
        {
            Observations = rows.ToArray(),
            SourceRecords = (asset.SourceRecords ?? []).Concat(batch.Records).DistinctBy(item => item.Id).ToArray(),
            Markets = markets,
            MaximumSupplyKind = batch.MaximumSupplyKind ?? asset.MaximumSupplyKind,
        };
    }

    private static ObservationKey Key(Observation row) => new(row.SourceId, row.EntityId, row.AssetId, row.ChainId, row.MetricKey,
        row.Scope, row.Unit, row.PeriodStart, row.PeriodEnd, row.MethodologyVersion);

    private sealed record ObservationKey(string SourceId, string EntityId, string? AssetId, string? ChainId, string MetricKey, MetricScope Scope,
        string Unit, DateTimeOffset Start, DateTimeOffset End, string MethodologyVersion);
}
