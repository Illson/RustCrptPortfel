using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Portfel.Application;
using Portfel.Domain;

namespace Portfel.Infrastructure;

public sealed record MarketCoin(string Id, string Name, string Symbol);
public sealed record UsdQuote(string Id, ExactNumber Usd, DateTimeOffset AsOf);

public sealed class CoinGeckoMarket : IDisposable
{
    private const string BaseUrl = "https://api.coingecko.com/api/v3";
    public const int MaxIds = 100;
    public const int MaxQueryLength = 5000;
    private static readonly TimeSpan RequestWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan QuoteClockSkew = TimeSpan.FromMinutes(1);
    private readonly HttpClient http;
    private readonly TimeProvider clock;
    private readonly ILogger<CoinGeckoMarket> log;
    private readonly SemaphoreSlim gate = new(1);
    private readonly Queue<DateTimeOffset> recent = new();
    private readonly Dictionary<string, (UsdQuote? Quote, DateTimeOffset At)> quotes = new(StringComparer.Ordinal);
    private MarketCoin[]? catalog;
    private DateTimeOffset catalogAt;
    private DateTimeOffset retryAt;

    public CoinGeckoMarket(HttpClient http, TimeProvider clock, ILogger<CoinGeckoMarket>? log = null)
    {
        this.http = http;
        this.clock = clock;
        this.log = log ?? NullLogger<CoinGeckoMarket>.Instance;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("dotnetCrptPortfel/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public static bool ValidId(string id) => id.Length is >= 1 and <= 120 &&
        id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_' or '.');

    public async Task<MarketCoin[]> CoinsAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (catalog is not null && clock.GetUtcNow() - catalogAt < TimeSpan.FromHours(6))
            {
                log.LogInformation("CoinGecko catalog: served from cache");
                return catalog;
            }
            var read = await FetchAsync("/coins/list?include_platform=false", ParseCatalog, ct);
            catalogAt = read.At;
            return catalog = read.Data;
        }
        finally { gate.Release(); }
    }

    public async Task<UsdQuote[]> QuotesAsync(string[] ids, CancellationToken ct)
    {
        if (ids.Length is < 1 or > MaxIds || ids.Any(id => !ValidId(id)) ||
            ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || string.Join(',', ids).Length > MaxQueryLength)
            throw new AppFault("invalid_asset_ids", "Укажите уникальные CoinGecko ID через запятую (не больше 100).", 400);
        await gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            foreach (var kv in quotes.Where(kv => now - kv.Value.At >= RequestWindow).ToArray()) quotes.Remove(kv.Key);
            string[] missing = ids.Where(id => !quotes.ContainsKey(id)).Order(StringComparer.Ordinal).ToArray();
            if (missing.Length > 0)
            {
                var read = await FetchAsync($"/simple/price?ids={string.Join(',', missing)}&vs_currencies=usd&include_last_updated_at=true&precision=full",
                    (json, at) => ParseQuotes(json, at, missing.ToHashSet(StringComparer.Ordinal)), ct);
                foreach (string id in missing) quotes[id] = (read.Data.GetValueOrDefault(id), read.At);
            }
            var result = ids.Select(id => quotes[id].Quote).OfType<UsdQuote>().ToArray();
            log.LogInformation("CoinGecko quotes: {ReturnedCount}/{RequestedCount} available, {CachedCount} cache entries used",
                result.Length, ids.Length, ids.Length - missing.Length);
            return result;
        }
        finally { gate.Release(); }
    }

    private async Task<(T Data, DateTimeOffset At)> FetchAsync<T>(string path, Func<JsonElement, DateTimeOffset, T> parse, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var url = new Uri(BaseUrl + path);
        while (recent.TryPeek(out var when) && now - when >= RequestWindow) recent.Dequeue();
        if (now < retryAt || recent.Count >= 4)
        {
            log.LogInformation("CoinGecko GET {Path}: skipped (cooldown)", url.AbsolutePath);
            throw new AppFault("rate_limited", "Лимит запросов котировок. Попробуйте через минуту.", 429);
        }
        recent.Enqueue(now);
        log.LogInformation("CoinGecko GET {Path}: started", url.AbsolutePath);
        long started = Stopwatch.GetTimestamp();
        var reply = await PublicSourceHttp.ReadAsync(http, clock, url, null,
            (json, at) => new Reply<T>(parse(json, at), at, null, null),
            (status, at, retry) => new Reply<T>(default, at, status, retry), ct);
        log.LogInformation("CoinGecko GET {Path}: {Outcome} ({ElapsedMs:F0} ms)", url.AbsolutePath, reply.Error ?? "ok",
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (reply.Error == "rate_limited")
        {
            retryAt = reply.RetryAt is { } until && until > reply.At ? until : reply.At.Add(RequestWindow);
            throw new AppFault("rate_limited", "CoinGecko ограничил запросы. Попробуйте позже.", 429);
        }
        if (reply.Error is not null || reply.Data is null)
            throw new AppFault(reply.Error ?? "schema_error", "Не удалось получить данные CoinGecko.", 502);
        return (reply.Data, reply.At);
    }

    private static MarketCoin[] ParseCatalog(JsonElement json, DateTimeOffset _)
    {
        var coins = new List<MarketCoin>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in json.EnumerateArray())
        {
            string? id = PublicSourceHttp.Text(row, "id");
            string? name = PublicSourceHttp.Text(row, "name");
            string? symbol = PublicSourceHttp.Text(row, "symbol");
            if (id is null || !ValidId(id) || string.IsNullOrWhiteSpace(name) || name.Length > 200
                || string.IsNullOrWhiteSpace(symbol) || symbol.Length > 60 || !seen.Add(id)) continue;
            coins.Add(new(id, name, symbol));
        }
        if (coins.Count == 0) throw new FormatException();
        return coins.ToArray();
    }

    private static Dictionary<string, UsdQuote> ParseQuotes(JsonElement json, DateTimeOffset now, HashSet<string> requested)
    {
        var result = new Dictionary<string, UsdQuote>(StringComparer.Ordinal);
        long latestTimestamp = (now + QuoteClockSkew).ToUnixTimeSeconds();
        foreach (var property in json.EnumerateObject())
        {
            if (!requested.Contains(property.Name)) throw new FormatException();
            var time = PublicSourceHttp.Property(property.Value, "last_updated_at");
            if (time.ValueKind != JsonValueKind.Number || !time.TryGetInt64(out long seconds)
                || seconds <= 0 || seconds > latestTimestamp) continue;
            ExactNumber? price;
            try { price = PublicSourceHttp.Number(PublicSourceHttp.Property(property.Value, "usd")); }
            catch (FormatException) { continue; }
            if (price is { Sign: > 0 } value)
                result.Add(property.Name, new(property.Name, value, DateTimeOffset.FromUnixTimeSeconds(seconds)));
        }
        return result;
    }

    public void Dispose()
    {
        gate.Dispose();
        http.Dispose();
    }

    private sealed record Reply<T>(T? Data, DateTimeOffset At, string? Error, DateTimeOffset? RetryAt);
}
