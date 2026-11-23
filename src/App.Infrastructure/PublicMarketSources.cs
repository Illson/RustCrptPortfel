using System.Globalization;
using System.Net;
using System.Text.Json;
using Portfel.Application;
using Portfel.Domain;

namespace Portfel.Infrastructure;

public static class ProviderHttp
{
    public static HttpClient Create(bool useCookies = true) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = useCookies,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
    });
}

public sealed class CmcPublicQuotes(HttpClient http, TimeProvider clock, string? apiKey = null) : IPublicQuoteSource
{
    private const string Docs = "https://coinmarketcap.com/api/documentation/pro-api-reference/cryptocurrency/";

    public SourceCapability Capability => new("cmc_market", ["price", "volume_24h", "reported_volume_24h", "market_cap", "circulating_supply", "total_supply", "max_supply"],
        "latest", TimeSpan.FromMinutes(15), apiKey is null ? "public_rate_pool" : "market_api_key");

    public Task<SourceBatch> ReadAsync(Asset asset, CancellationToken cancellationToken)
    {
        string? cmcId = asset.Identifiers.SingleOrDefault(id => id.Provider == "cmc")?.ExternalId;
        if (!int.TryParse(cmcId, NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id <= 0)
            return Task.FromResult(SourceBatch.Failed("unmapped", clock.GetUtcNow()));
        string prefix = apiKey is null ? "/public-api" : "";
        var url = new Uri(FormattableString.Invariant($"https://pro-api.coinmarketcap.com{prefix}/v3/cryptocurrency/quotes/latest?id={id}&convert=USD"));
        return PublicSourceHttp.ReadAsync(http, clock, url, apiKey is null ? null : ("X-CMC_PRO_API_KEY", apiKey),
            (json, at) => Parse(json, asset.Id, id, at), cancellationToken);
    }

    private SourceBatch Parse(JsonElement json, string assetId, int id, DateTimeOffset at)
    {
        JsonElement error = json.GetProperty("status").GetProperty("error_code");
        if (error.ValueKind == JsonValueKind.String ? error.GetString() != "0" : error.GetInt32() != 0)
            return SourceBatch.Failed("provider_unavailable", at);
        var coin = json.GetProperty("data").EnumerateArray().SingleOrDefault(item => item.GetProperty("id").GetInt32() == id);
        if (coin.ValueKind == JsonValueKind.Undefined) return SourceBatch.Failed("missing", at);
        JsonElement usd = coin.GetProperty("quote").EnumerateArray()
            .SingleOrDefault(item => PublicSourceHttp.Text(item, "symbol") == "USD");
        var quoteAt = PublicSourceHttp.Instant(usd.GetProperty("last_updated"), at);
        var supplyAt = PublicSourceHttp.Instant(coin.GetProperty("last_updated"), at);
        string source = Capability.Id;
        var record = PublicSourceHttp.Record(source, Docs, "CoinMarketCap · котировка и предложение", coin, at, "cmc-quotes-v3");
        List<Observation> observations = [];

        ExactNumber? Add(JsonElement parent, string sourceKey, string metric, string unit, DateTimeOffset asOf)
        {
            ExactNumber? number = PublicSourceHttp.Number(PublicSourceHttp.Property(parent, sourceKey));
            if (number is not { } value) return null;
            if (value.Sign < 0 || metric == "price" && value.Sign == 0) throw new FormatException();
            observations.Add(PublicSourceHttp.Point(assetId, assetId, metric, MetricScope.Token, value, unit, asOf, asOf, record, at));
            return value;
        }

        if (Add(usd, "price", "price", "USD", quoteAt) is null) return SourceBatch.Failed("missing", at);
        Add(usd, "volume_24h_reported", "reported_volume_24h", "USD", quoteAt);
        Add(usd, "volume_24h", "volume_24h", "USD", quoteAt);
        Add(usd, "market_cap", "market_cap", "USD", quoteAt);
        Add(coin, "circulating_supply", "circulating_supply", "token_units", supplyAt);
        Add(coin, "total_supply", "total_supply", "token_units", supplyAt);
        ExactNumber? max = Add(coin, "max_supply", "max_supply", "token_units", supplyAt);
        bool unbounded = PublicSourceHttp.Property(coin, "infinite_supply").ValueKind == JsonValueKind.True;
        if (unbounded && max is { Sign: > 0 }) return SourceBatch.Failed("conflict", at);
        return new("ok", at, observations.ToArray(), [record], [], unbounded ? "unbounded" : max is { Sign: > 0 } ? "finite" : "unknown");
    }
}

public sealed record LlamaMapping(string EntityId, string Slug, MetricScope Scope);

public sealed class DefiLlamaFlows(HttpClient http, TimeProvider clock)
{
    private const string Docs = "https://defillama.com/data-definitions";
    public static SourceCapability Capability { get; } = new("defillama", ["fees", "revenue", "holders_revenue", "chain_fees", "chain_revenue"],
        "daily_flows_up_to_24_months", TimeSpan.FromHours(6), "free");

    public Task<SourceBatch> ReadAsync(AssetData asset, LlamaMapping mapping, string metric, CancellationToken cancellationToken)
    {
        if (!asset.Links.Any(link => link.AssetId == asset.Asset.Id && link.EntityId == mapping.EntityId && link.Status == MappingStatus.Confirmed)
            || mapping.Slug.Length is < 1 or > 100 || !mapping.Slug.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
            return Task.FromResult(SourceBatch.Failed("unmapped", clock.GetUtcNow()));
        var supported = (mapping.Scope, metric) switch
        {
            (MetricScope.ProtocolGlobal, "fees") => ("summary/fees", "dailyFees"),
            (MetricScope.ProtocolGlobal, "revenue") => ("summary/fees", "dailyRevenue"),
            (MetricScope.ProtocolGlobal, "holders_revenue") => ("summary/fees", "dailyHoldersRevenue"),
            (MetricScope.Chain, "chain_fees") => ("summary/fees", "dailyFees"),
            (MetricScope.Chain, "chain_revenue") => ("summary/fees", "dailyRevenue"),
            _ => default,
        };
        if (supported.Item1 is null) return Task.FromResult(SourceBatch.Failed("unsupported_capability", clock.GetUtcNow()));
        var url = new Uri($"https://api.llama.fi/{supported.Item1}/{mapping.Slug}?dataType={supported.Item2}&excludeTotalDataChart=false&excludeTotalDataChartBreakdown=true");
        return PublicSourceHttp.ReadAsync(http, clock, url, null, (json, at) => Parse(json, asset.Asset, mapping, metric, at), cancellationToken);
    }

    private static SourceBatch Parse(JsonElement json, Asset asset, LlamaMapping mapping, string metric, DateTimeOffset at)
    {
        if (PublicSourceHttp.Property(json, "latestFetchIsOk").ValueKind == JsonValueKind.False)
            return SourceBatch.Failed("provider_unavailable", at);
        if (!string.Equals(PublicSourceHttp.Text(json, "slug"), mapping.Slug, StringComparison.OrdinalIgnoreCase)) throw new FormatException();
        bool isChain = PublicSourceHttp.Text(json, "protocolType") == "chain"
            && PublicSourceHttp.Text(json, "id")?.StartsWith("chain#", StringComparison.Ordinal) == true;
        if ((mapping.Scope == MetricScope.Chain) != isChain) return SourceBatch.Failed("conflict", at);
        JsonElement cmc = PublicSourceHttp.Property(json, "cmcId");
        string? returnedId = cmc.ValueKind == JsonValueKind.Number ? cmc.GetRawText() : PublicSourceHttp.Text(json, "cmcId");
        string? expected = asset.Identifiers.SingleOrDefault(item => item.Provider == "cmc")?.ExternalId;
        if (returnedId is not null && expected is not null && returnedId != expected)
            return SourceBatch.Failed("conflict", at);
        if (PublicSourceHttp.Text(json, "gecko_id") is { } gecko &&
            asset.Identifiers.SingleOrDefault(item => item.Provider == "coingecko")?.ExternalId is { } expectedGecko && gecko != expectedGecko)
            return SourceBatch.Failed("conflict", at);
        JsonElement chart = json.GetProperty("totalDataChart");
        DateTimeOffset midnight = new(at.UtcDateTime.Date, TimeSpan.Zero);
        DateTimeOffset from = midnight.AddDays(1 - midnight.Day).AddMonths(-24);
        var methodology = PublicSourceHttp.Property(json, "methodology");
        string version = methodology.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "llama-fees-v1"
            : "llama-fees-v1-" + PortfelJson.Hash(methodology)[..12];
        var record = PublicSourceHttp.Record(Capability.Id, Docs, $"DefiLlama · {mapping.Slug} · {metric}", chart, at, version);
        List<Observation> values = [];
        foreach (var row in chart.EnumerateArray())
        {
            if (row.GetArrayLength() != 2 || !row[0].TryGetInt64(out long timestamp) || timestamp < 0
                || timestamp > DateTimeOffset.MaxValue.ToUnixTimeSeconds() - 86400 || timestamp % 86400 != 0)
                throw new FormatException();
            DateTimeOffset day = DateTimeOffset.FromUnixTimeSeconds(timestamp);
            if (day < from || day >= midnight) continue;
            if (PublicSourceHttp.Number(row[1]) is { } value)
                values.Add(PublicSourceHttp.Point(mapping.EntityId, asset.Id, metric, mapping.Scope, value, "USD", day, day.AddDays(1), record, at));
        }
        if (values.Count == 0) return SourceBatch.Failed("missing", at);
        if (values.GroupBy(row => row.PeriodStart).Any(group => group.Select(item => item.ValueDecimal).Distinct().Count() > 1))
            return SourceBatch.Failed("conflict", at);
        return new("ok", at, values.DistinctBy(item => item.Id).ToArray(), [record], []);
    }
}

public sealed class CoinGeckoSpotMarkets(HttpClient http, TimeProvider clock, string apiKey, IReadOnlySet<string> confirmedCex)
{
    public static SourceCapability Capability { get; } = new("coingecko", ["spot_pairs", "reported_volume_24h", "spread", "depth"],
        "latest, confirmed CEX only", TimeSpan.FromMinutes(15), "pro_api_key");

    public async Task<SourceBatch> ReadAsync(Asset asset, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        if (string.IsNullOrWhiteSpace(apiKey)) return SourceBatch.Failed("authentication_required", now);
        string? slug = asset.Identifiers.SingleOrDefault(id => id.Provider == "coingecko")?.ExternalId;
        if (slug is null || slug.Length is < 1 or > 150 || !slug.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            return SourceBatch.Failed("unmapped", now);
        if (confirmedCex.Count == 0) return SourceBatch.Failed("not_configured", now);
        var pairs = new List<Market>();
        var records = new List<SourceRecord>();
        bool complete = false;
        bool skipped = false;
        for (int page = 1; page <= 3; page++)
        {
            var url = new Uri(FormattableString.Invariant($"https://pro-api.coingecko.com/api/v3/coins/{slug}/tickers?page={page}&depth=true"));
            var batch = await PublicSourceHttp.ReadAsync(http, clock, url, ("x-cg-pro-api-key", apiKey), (root, at) =>
            {
                JsonElement tickers = root.GetProperty("tickers");
                complete = tickers.GetArrayLength() < 100;
                var record = PublicSourceHttp.Record(Capability.Id, "https://docs.coingecko.com/reference/coins-id-tickers",
                    "CoinGecko · спотовые пары", tickers, at, "coingecko-cex-v1");
                List<Market> values = [];
                foreach (JsonElement ticker in tickers.EnumerateArray())
                {
                    string? exchange = PublicSourceHttp.Text(ticker.GetProperty("market"), "identifier");
                    string? target = PublicSourceHttp.Text(ticker, "target_coin_id");
                    if (exchange is null || !confirmedCex.Contains(exchange) || target is null || PublicSourceHttp.Text(ticker, "coin_id") != slug)
                    {
                        skipped = true;
                        continue;
                    }
                    string pair = $"{ticker.GetProperty("base").GetString()}/{ticker.GetProperty("target").GetString()}";
                    var tradedAt = ticker.GetProperty("last_traded_at");
                    if (tradedAt.ValueKind == JsonValueKind.Null) { skipped = true; continue; }
                    DateTimeOffset traded = PublicSourceHttp.Instant(tradedAt, at);
                    Quality quality = ticker.GetProperty("is_anomaly").GetBoolean() ? Quality.Conflict
                        : ticker.GetProperty("is_stale").GetBoolean() ? Quality.Stale : Quality.Ok;
                    ExactNumber? volume = PublicSourceHttp.Number(PublicSourceHttp.Property(ticker.GetProperty("converted_volume"), "usd"));
                    ExactNumber? up = PublicSourceHttp.Number(PublicSourceHttp.Property(ticker, "cost_to_move_up_usd"));
                    ExactNumber? down = PublicSourceHttp.Number(PublicSourceHttp.Property(ticker, "cost_to_move_down_usd"));
                    ExactNumber? depth = up is { Sign: >= 0 } first && down is { Sign: >= 0 } last ? (first.CompareTo(last) <= 0 ? first : last) : null;
                    values.Add(new(PortfelJson.Hash(new { asset.Id, exchange, target, pair, traded }), exchange, asset.Id, $"cg:{target}", pair,
                        "spot", "CEX", volume, traded, true, quality, Capability.Id,
                        PublicSourceHttp.Number(PublicSourceHttp.Property(ticker, "bid_ask_spread_percentage")), depth));
                }
                return new("ok", at, [], [record], values.ToArray());
            }, cancellationToken);
            if (batch.Status != "ok") return batch;
            pairs.AddRange(batch.Markets);
            records.AddRange(batch.Records);
            if (complete) break;
        }
        return new(complete && !skipped ? "ok" : "partial", clock.GetUtcNow(), [], records.ToArray(), pairs.ToArray());
    }
}

internal static class PublicSourceHttp
{
    internal static async Task<SourceBatch> ReadAsync(HttpClient client, TimeProvider clock, Uri url, (string Name, string Value)? header,
        Func<JsonElement, DateTimeOffset, SourceBatch> parse, CancellationToken ct) =>
        await ReadAsync(client, clock, url, header, parse, SourceBatch.Failed, ct);

    internal static async Task<T> ReadAsync<T>(HttpClient client, TimeProvider clock, Uri url, (string Name, string Value)? header,
        Func<JsonElement, DateTimeOffset, T> parse, Func<string, DateTimeOffset, DateTimeOffset?, T> failed, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        if (header is { } credential) message.Headers.Add(credential.Name, credential.Value);
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            DateTimeOffset now = clock.GetUtcNow();
            if (!response.IsSuccessStatusCode)
                return failed(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "authentication_required",
                    HttpStatusCode.Forbidden => "permission_denied",
                    HttpStatusCode.TooManyRequests => "rate_limited",
                    HttpStatusCode.NotFound => "missing",
                    _ => "provider_unavailable",
                }, now, response.Headers.RetryAfter?.Date ?? (response.Headers.RetryAfter?.Delta is { } delay ? now + delay : null));
            await using var body = new BoundedReadStream(await response.Content.ReadAsStreamAsync(deadline.Token), 8 * 1024 * 1024);
            using var data = await JsonDocument.ParseAsync(body, new JsonDocumentOptions { MaxDepth = 40, AllowDuplicateProperties = false }, deadline.Token);
            return parse(data.RootElement, now);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return failed("provider_unavailable", clock.GetUtcNow(), null); }
        catch (Exception error) when (error is HttpRequestException or IOException) { return failed("provider_unavailable", clock.GetUtcNow(), null); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        { return failed("schema_error", clock.GetUtcNow(), null); }
    }

    internal static JsonElement Property(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var found) ? found : default;

    internal static string? Text(JsonElement data, string name) =>
        Property(data, name) is { ValueKind: JsonValueKind.String } found ? found.GetString() : null;

    internal static ExactNumber? Number(JsonElement data) => data.ValueKind switch
    {
        JsonValueKind.Number => ExactNumber.ParseProviderNumber(data.GetRawText()),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => throw new FormatException("Некорректное число источника."),
    };

    internal static DateTimeOffset Instant(JsonElement data, DateTimeOffset now)
    {
        if (!data.TryGetDateTimeOffset(out var date) || date > now.AddSeconds(60)) throw new FormatException("Некорректное время источника.");
        return date.ToUniversalTime();
    }

    internal static SourceRecord Record(string source, string docs, string title, JsonElement content, DateTimeOffset at, string methodology)
    {
        string hash = PortfelJson.Hash(content);
        return new(PortfelJson.Hash(new { source, hash, methodology }), source, docs, title, null, null, at, hash, methodology);
    }

    internal static Observation Point(string entity, string asset, string metric, MetricScope scope, ExactNumber quantity, string unit,
        DateTimeOffset start, DateTimeOffset end, SourceRecord source, DateTimeOffset at) =>
        new(PortfelJson.Hash(new { entity, asset, source.SourceId, source.Methodology, metric, scope, unit, start, end, quantity }),
            entity, asset, null, metric, scope, quantity, unit, start, end, source.SourceId, end, at, Quality.Ok, source.Methodology, "1.0", source.Id, 1);
}
