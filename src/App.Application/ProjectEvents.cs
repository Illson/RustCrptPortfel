using Portfel.Domain;

namespace Portfel.Application;

public static class ProjectEvents
{
    // Notices can be merged only if the connector identifies the same confirmed primary source
    // and its event key. Headline, date alone, and copied text are not identities.
    public static AssetData Register(AssetData asset, string primarySourceId, string canonicalKey,
        ProjectEvent notice, SourceRecord[] records)
    {
        if (string.IsNullOrWhiteSpace(canonicalKey) || notice.AssetId != asset.Asset.Id
            || !asset.Sources.Any(item => item.Id == primarySourceId)
            || !records.Any(item => item.Primary && item.SourceId == primarySourceId)
            || records.Length == 0 || records.Any(item => !AnalysisValidation.IsPublicUrl(item.Url))
            || notice.Status is not ("announced" or "scheduled" or "completed" or "postponed" or "cancelled" or "disputed" or "unknown")
            || notice.Precision == "date" && (notice.LocalDate is null || notice.EventAt is not null || notice.DeadlineAt is not null))
            throw new AppFault("invalid_event", "Для события нужен подтверждённый первоисточник и корректная дата.");
        string id = PortfelJson.Hash(new { AssetId = asset.Asset.Id, primarySourceId, canonicalKey });
        var previous = asset.Events.Where(item => item.Id == id).MaxBy(item => item.Version);
        if (previous is not null && notice.AnnouncedAt < previous.AnnouncedAt) return asset;
        string[] sourceRefs = records.Select(item => item.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        ProjectEvent normalized = notice with
        {
            Id = id, Version = (previous?.Version ?? 0) + 1, SourceRefs = sourceRefs,
            AnnouncedAt = notice.AnnouncedAt.ToUniversalTime(),
            EventAt = notice.EventAt?.ToUniversalTime(),
            DeadlineAt = notice.DeadlineAt?.ToUniversalTime(),
        };
        if (previous is not null && (normalized with { Version = previous.Version, SourceRefs = previous.SourceRefs,
                AnnouncedAt = previous.AnnouncedAt }) == previous)
        {
            if (sourceRefs.All(previous.SourceRefs.Contains) && normalized.AnnouncedAt == previous.AnnouncedAt) return asset;
            normalized = normalized with { SourceRefs = previous.SourceRefs.Concat(sourceRefs).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() };
        }
        else if (previous is not null && normalized.AnnouncedAt == previous.AnnouncedAt)
            throw new AppFault("event_conflict", "Первоисточник сообщил разные состояния события с одним временем объявления.");
        return asset with
        {
            Events = [.. asset.Events, normalized],
            SourceRecords = (asset.SourceRecords ?? []).Concat(records).DistinctBy(item => item.Id).ToArray(),
        };
    }
}
