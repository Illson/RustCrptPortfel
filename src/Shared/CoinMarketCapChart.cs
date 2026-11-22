using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Portfel.CoinMarketCap;

internal static partial class CoinMarketCapChart
{
    private const string Site = "https://coinmarketcap.com/";
    private const string ChartEndpoint = "https://api.coinmarketcap.com/data-api/v3.3/cryptocurrency/detail/chart";
    private const string Interval = "7d";
    private const int ConvertId = 2781;
    private const string Range = "All";
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    internal static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = Timeout,
            MaxResponseContentBufferSize = 16 * 1024 * 1024,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("dotnetCrptPortfel/1.0");
        return client;
    }

    internal static bool ValidSlug(string slug) => slug.Length is >= 1 and <= 120 && slug[0] != '-' && slug[^1] != '-' &&
        slug.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    internal static async Task<string> FetchAsync(HttpClient http, string slug)
    {
        if (!ValidSlug(slug))
            throw new FormatException("slug must use lowercase ASCII letters, digits or internal hyphens (for example dash)");
        var platform = await FetchPlatformAsync(http, slug, default);
        var chart = await FetchChartAsync(http, platform.CryptoId, default);
        return Format(slug, platform.Info, chart);
    }

    internal static async Task<CoinMarketCapPlatform> FetchPlatformAsync(HttpClient http, string slug, CancellationToken ct)
    {
        using var pageRequest = new HttpRequestMessage(HttpMethod.Get, $"{Site}currencies/{slug}/");
        pageRequest.Headers.Accept.ParseAdd("text/html");
        pageRequest.Headers.AcceptLanguage.ParseAdd("ru,en;q=0.9");
        string html = await GetAsync(http, pageRequest, "coin page", ct);
        return ReadPlatformInfo(html);
    }

    internal static async Task<JsonElement> FetchChartAsync(HttpClient http, int cryptoId, CancellationToken ct)
    {
        string chartUrl = FormattableString.Invariant(
            $"{ChartEndpoint}?id={cryptoId}&interval={Interval}&convertId={ConvertId}&range={Range}");
        using var chartRequest = new HttpRequestMessage(HttpMethod.Get, chartUrl);
        chartRequest.Headers.Accept.ParseAdd("application/json");
        chartRequest.Headers.Add("platform", "web");
        chartRequest.Headers.Add("x-request-id", Guid.NewGuid().ToString("N"));
        chartRequest.Headers.Referrer = new Uri(Site);
        string chartJson = await GetAsync(http, chartRequest, "chart", ct);
        if (chartJson.Length > 2 * 1024 * 1024)
            throw new CoinMarketCapException("CoinMarketCap chart response is too large");

        using JsonDocument chart = ParseJson(chartJson, "chart response");
        JsonElement errorCode = Property(Property(chart.RootElement, "status"), "error_code");
        if (!(errorCode.ValueKind == JsonValueKind.String && errorCode.ValueEquals("0") ||
              errorCode.ValueKind == JsonValueKind.Number && errorCode.TryGetInt32(out int code) && code == 0))
        {
            throw new CoinMarketCapException("CoinMarketCap chart response does not report success (status.error_code must be 0)");
        }

        JsonElement points = Property(Property(chart.RootElement, "data"), "points");
        if (points.ValueKind != JsonValueKind.Array || points.GetArrayLength() == 0)
        {
            throw new CoinMarketCapException("CoinMarketCap chart response is missing nonempty data.points");
        }

        return chart.RootElement.Clone();
    }

    internal static string Format(string slug, JsonElement platformInfo, JsonElement chart) =>
        JsonSerializer.Serialize(new
        {
            source = "CoinMarketCap",
            slug,
            interval = Interval,
            convertId = ConvertId,
            range = Range,
            platformInfo,
            chart,
        });

    internal static CoinMarketCapPlatform ReadStoredPlatform(string json)
    {
        using var info = JsonDocument.Parse(json);
        return Platform(info.RootElement);
    }

    private static CoinMarketCapPlatform ReadPlatformInfo(string html)
    {
        Match nextData;
        try
        {
            nextData = NextDataScript().Match(html);
        }
        catch (RegexMatchTimeoutException error)
        {
            throw new CoinMarketCapException("timed out reading CoinMarketCap coin page data", error);
        }

        if (!nextData.Success)
        {
            throw new CoinMarketCapException("CoinMarketCap coin page is missing the __NEXT_DATA__ script");
        }

        using JsonDocument page = ParseJson(nextData.Groups["data"].Value, "coin page data");
        JsonElement info = page.RootElement;
        foreach (string key in new[] { "props", "pageProps", "detailRes", "popularCryptoInfo", "platformInfo" })
        {
            info = Property(info, key);
        }

        return Platform(info);
    }

    private static CoinMarketCapPlatform Platform(JsonElement info)
    {
        JsonElement id = Property(info, "cryptocurrencyId");
        if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out int cryptoId) || cryptoId <= 0)
        {
            throw new CoinMarketCapException("CoinMarketCap coin page is missing a positive platformInfo.cryptocurrencyId");
        }

        return new(info.Clone(), cryptoId);
    }

    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            ? value
            : default;

    private static JsonDocument ParseJson(string text, string stage)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException error)
        {
            throw new CoinMarketCapException($"CoinMarketCap {stage} is not valid JSON", error);
        }
    }

    private static async Task<string> GetAsync(HttpClient http, HttpRequestMessage request, string stage, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new CoinMarketCapException($"CoinMarketCap {stage} returned HTTP {(int)response.StatusCode}")
                {
                    Status = response.StatusCode,
                    RetryAfter = response.Headers.RetryAfter,
                };
            }

            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException error)
        {
            throw new CoinMarketCapException($"CoinMarketCap {stage} request failed: {error.Message}", error);
        }
        catch (OperationCanceledException error) when (!ct.IsCancellationRequested)
        {
            throw new CoinMarketCapException($"CoinMarketCap {stage} request timed out", error);
        }
    }

    [GeneratedRegex("""<script\b[^>]*\sid\s*=\s*(?:"__NEXT_DATA__"|'__NEXT_DATA__')[^>]*>(?<data>.*?)</script\s*>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NextDataScript();
}

internal sealed record CoinMarketCapPlatform(JsonElement Info, int CryptoId);

internal sealed class CoinMarketCapException(string message, Exception? inner = null) : Exception(message, inner)
{
    internal HttpStatusCode? Status { get; init; }
    internal RetryConditionHeaderValue? RetryAfter { get; init; }
}
