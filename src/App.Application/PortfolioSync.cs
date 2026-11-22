using Portfel.Domain;

namespace Portfel.Application;

public sealed record PortfolioPage(string SourcePortfolioId, string Status, string FormatVersion, string? Revision,
    DateTimeOffset? SourceAsOf, Position[] Positions, bool LastPage, string? NextCursor);

public interface IReadOnlyPortfolioSource
{
    // Implementation must already be authorized to read the selected owner's portfolio.
    Task<PortfolioPage> ReadPageAsync(string sourcePortfolioId, string? cursor, CancellationToken cancellationToken);
}

public sealed class PortfolioSync(IWorkspaceStore store, TimeProvider clock)
{
    public async Task<PortfolioCheck> FetchAsync(string owner, string sourcePortfolioId, IReadOnlyPortfolioSource source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourcePortfolioId)) throw new ArgumentException("Укажите портфель источника.", nameof(sourcePortfolioId));
        PortfolioFetch fetch = await FetchAllAsync(sourcePortfolioId, source, ct);
        return await store.UpdateAsync(owner, state =>
        {
            Workspace next = state.RecordSync(fetch);
            return (next, next.LatestCheck ?? new(fetch.FetchedAt, "stale_response"));
        }, ct);
    }

    private async Task<PortfolioFetch> FetchAllAsync(string id, IReadOnlyPortfolioSource source, CancellationToken ct)
    {
        var positions = new List<Position>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null, revision = null;
        DateTimeOffset? asOf = null;
        for (int page = 0; page < 20; page++)
        {
            ct.ThrowIfCancellationRequested();
            PortfolioPage next;
            try { next = await source.ReadPageAsync(id, cursor, ct); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
                return Failure("provider_unavailable");
            }
            if (next.Status != "ok") return Failure(next.Status);
            if (next.SourcePortfolioId != id || next.FormatVersion != "v1" || next.Positions is null) return Failure("schema_error");
            if (page == 0) { revision = next.Revision; asOf = next.SourceAsOf; }
            else if (next.Revision != revision || next.SourceAsOf != asOf) return Failure("snapshot_changed");
            if (next.Positions.Length > 500 - positions.Count) return Failure("portfolio_limit");
            positions.AddRange(next.Positions);
            if (next.LastPage)
                return next.NextCursor is not null ? Failure("schema_error")
                    : new(id, clock.GetUtcNow(), asOf, true, "v1", "ok", positions.ToArray());
            if (string.IsNullOrWhiteSpace(revision) || next.NextCursor is not { Length: > 0 and <= 512 } value || !visited.Add(value))
                return Failure("incomplete_snapshot");
            cursor = value;
        }
        return Failure("portfolio_limit");

        PortfolioFetch Failure(string status) => new(id, clock.GetUtcNow(), asOf, false, "v1", status, []);
    }
}
