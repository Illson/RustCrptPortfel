using Portfel.Domain;

namespace Portfel.Application;

public static class AnalysisSnapshots
{
    public static AnalysisInputV1 ForAsset(Workspace workspace, string assetId, DateTimeOffset now)
    {
        AssetData data = workspace.Assets.SingleOrDefault(asset => asset.Asset.Id == assetId)
            ?? throw new AppFault("asset_not_found", "Актив не найден.", 404);
        var sources = data.Sources.Where(source => AnalysisValidation.IsPublicUrl(source.Url)).ToDictionary(source => source.Id);
        var records = (data.SourceRecords ?? []).Where(source => AnalysisValidation.IsPublicUrl(source.Url)).ToDictionary(source => source.Id);
        var confirmed = data.Links.Where(link => link.AssetId == assetId && link.Status == MappingStatus.Confirmed).ToArray();
        var events = PortfolioQueries.CurrentEvents(data.Events).Where(item => item.AssetId == assetId && item.AnnouncedAt <= now)
            .Where(item => item.SourceRefs.All(id => records.ContainsKey(id))).OrderBy(item => item.AnnouncedAt).TakeLast(30).ToArray();
        var evidence = new Dictionary<string, Evidence>(StringComparer.Ordinal);
        var cover = Enum.GetValues<AnalysisBlock>().ToDictionary(block => block, _ => new List<string>());
        var statuses = new Dictionary<AnalysisBlock, Quality>();

        void Add(AnalysisBlock block, Observation row, TimeSpan maxAge)
        {
            if (row.FetchedAt > now || row.SourceAsOf > now || (row.AssetId is not null && row.AssetId != assetId)) return;
            records.TryGetValue(row.RawRecordId, out SourceRecord? record);
            sources.TryGetValue(row.SourceId, out Source? source);
            string? url = record?.Url ?? source?.Url;
            if (url is null) return;
            bool stale = now - row.FetchedAt > maxAge || row.Scope == MetricScope.Token && now - row.SourceAsOf > maxAge;
            Quality quality = row.QualityStatus == Quality.Ok && stale ? Quality.Stale : row.QualityStatus;
            string period = row.PeriodStart == row.PeriodEnd ? row.SourceAsOf.ToString("O") : $"{row.PeriodStart:O}/{row.PeriodEnd:O}";
            evidence.TryAdd(row.Id, new(row.Id, assetId, row.SourceId, url, record?.Title ?? source!.Name,
                row.FetchedAt, row.MetricKey, row.ValueDecimal, row.Unit, period, row.Scope, quality,
                PortfelJson.Hash(new { row.Id, row.Revision, record?.ContentHash, row.AdapterVersion, row.MethodologyVersion }),
                Primary: record?.Primary == true));
            if (!cover[block].Contains(row.Id)) cover[block].Add(row.Id);
        }

        var flows = PortfolioQueries.Economy(data, now);
        var rowsById = data.Observations.GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.MaxBy(item => item.Revision)!);
        foreach (FlowSeries flow in flows)
        {
            foreach (MonthlyFlow month in flow.Months.TakeLast(3).Where(month => month.Total.HasValue))
            {
                Observation[] inputs = month.EvidenceIds.Select(id => rowsById[id]).ToArray();
                foreach (Observation observation in inputs) Add(AnalysisBlock.Economics, observation, TimeSpan.FromHours(36));
                if (inputs.Length == 0 || inputs.Any(observation => !evidence.ContainsKey(observation.Id))) continue;
                string id = PortfelJson.Hash(new { Formula = "daily_flow_sum_v1", month.EvidenceIds, month.Month, month.Total, month.Mtd, flow.Scope });
                Evidence first = evidence[inputs[0].Id];
                evidence.TryAdd(id, new(id, assetId, flow.SourceId, first.Url, first.Title, inputs.Max(row => row.FetchedAt), flow.MetricKey,
                    month.Total, flow.Unit, month.Mtd ? $"{month.Month:yyyy-MM-dd}/{now.UtcDateTime:yyyy-MM-dd}" : $"{month.Month:yyyy-MM}",
                    flow.Scope, month.QualityStatus, FormulaVersion: "daily_flow_sum_v1", ObservationIds: month.EvidenceIds));
                cover[AnalysisBlock.Economics].Add(id);
            }
        }
        statuses[AnalysisBlock.Economics] = Aggregate(cover[AnalysisBlock.Economics].Select(id => evidence[id].QualityStatus));
        foreach (string key in new[] { "circulating_supply", "total_supply", "max_supply" })
            if (PortfolioQueries.Latest(data, key, now) is { } amount) Add(AnalysisBlock.Supply, amount, TimeSpan.FromHours(24));
        statuses[AnalysisBlock.Supply] = Aggregate(cover[AnalysisBlock.Supply].Select(id => evidence[id].QualityStatus), true);
        foreach (string key in new[] { "price", "reported_volume_24h", "market_cap" })
            if (PortfolioQueries.Latest(data, key, now, "USD") is { } quote) Add(AnalysisBlock.Markets, quote, TimeSpan.FromHours(1));
        statuses[AnalysisBlock.Markets] = Aggregate(cover[AnalysisBlock.Markets].Select(id => evidence[id].QualityStatus), true);
        foreach (string sourceId in events.SelectMany(item => item.SourceRefs).Distinct(StringComparer.Ordinal))
        {
            SourceRecord record = records[sourceId];
            if (record.FetchedAt > now) continue;
            evidence.TryAdd(record.Id, new(record.Id, assetId, record.SourceId, record.Url, record.Title,
                record.FetchedAt, RecordRevision: record.ContentHash, Primary: record.Primary));
            cover[AnalysisBlock.Project].Add(record.Id);
        }
        statuses[AnalysisBlock.Project] = cover[AnalysisBlock.Project].Count == 0 ? Quality.Missing : Quality.Partial;

        PortfolioSnapshot? current = workspace.Current;
        PortfolioPosition? position = PortfolioQueries.CanPersonalize(workspace, now) ? PortfolioQueries.Portfolio(workspace, now, false).Positions
            .FirstOrDefault(item => item.AssetId == assetId && item.Spot) : null;
        var previous = workspace.Feed.Where(item => item.AssetId == assetId && item.PublishedAt <= now && !item.Demo)
            .MaxBy(item => item.PublishedAt);
        AnalysisCoverage[] blocks = cover.Select(item => new AnalysisCoverage(item.Key, statuses[item.Key],
            item.Value.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())).ToArray();
        return new(AnalysisValidation.SchemaVersion, assetId, now, current?.Hash, evidence.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(), [], false)
        {
            Asset = data.Asset,
            SourcePortfolioId = current?.SourcePortfolioId,
            PortfolioContext = position is null ? null : new("current", current!.LastCheckedAt, position.Quantity, position.ValueUsd, position.SharePct),
            EntityLinks = confirmed,
            Blocks = blocks,
            Events = events.Where(item => item.SourceRefs.All(evidence.ContainsKey)).ToArray(),
            PreviousAnalysisId = previous?.Id,
            PreviousAnalysisHash = previous?.Analysis.SnapshotHash,
            InvestmentProfile = position is null ? null : workspace.Settings.InvestmentProfile,
            Gaps = blocks.Where(block => block.DataStatus != Quality.Ok).Select(block => $"{block.Block}: {block.DataStatus}").ToArray(),
        };
    }

    private static Quality Aggregate(IEnumerable<Quality> rows, bool missingOtherMetrics = false)
    {
        Quality[] values = rows.Distinct().ToArray();
        return values.Length switch
        {
            0 => Quality.Missing,
            1 when !missingOtherMetrics || values[0] != Quality.Ok => values[0],
            _ => Quality.Partial,
        };
    }
}
