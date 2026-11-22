using System.Text.Json;

namespace Portfel.Infrastructure;

public sealed record MarketRequestStatus(string Id, string? Slug, string Kind, string Outcome, int? HttpStatus,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Provider, JsonElement? Response = null);
