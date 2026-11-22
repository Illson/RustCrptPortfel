using System.Net;
using System.Text.Json;
using Portfel.CoinMarketCap;

namespace DotnetCrptPortfel.Tests;

internal static class CoinMarketCapChartTests
{
    private const string ChartJson = """
        {"data":{"points":[{"s":"1700000000","v":[0.012345678901234567890123456789]}]},"status":{"error_code":"0"}}
        """;
    private const string PrecisePrice = "0.012345678901234567890123456789";

    public static void FetchesChartUsingCryptocurrencyId()
    {
        using var handler = new StubHandler((request, index) =>
        {
            Check(request.Method == HttpMethod.Get && request.Content is null, "expected a GET without a body");
            switch (index)
            {
                case 0:
                    Check(request.RequestUri?.AbsoluteUri == "https://coinmarketcap.com/currencies/fixture-coin/",
                        "incorrect coin page URL");
                    Check(!request.Headers.Contains("If-None-Match"), "unexpected conditional request");
                    return Reply(PageWith("""{"id":161,"cryptocurrencyId":321,"name":"Fixture","dexerPlatformName":"Fixture"}"""));
                case 1:
                    Check(request.RequestUri?.AbsoluteUri ==
                        "https://api.coinmarketcap.com/data-api/v3.3/cryptocurrency/detail/chart?id=321&interval=7d&convertId=2781&range=All",
                        "incorrect chart URL; it must use cryptocurrencyId, not id");
                    Check(request.Headers.GetValues("platform").Single() == "web" &&
                        Guid.TryParseExact(request.Headers.GetValues("x-request-id").Single(), "N", out _) &&
                        request.Headers.Referrer?.AbsoluteUri == "https://coinmarketcap.com/", "incorrect chart headers");
                    return Reply(ChartJson);
                default:
                    throw new InvalidOperationException("unexpected request");
            }
        });
        using var http = new HttpClient(handler);
        using JsonDocument output = JsonDocument.Parse(CoinMarketCapChart.FetchAsync(http, "fixture-coin").GetAwaiter().GetResult());
        JsonElement result = output.RootElement;
        JsonElement platformInfo = result.GetProperty("platformInfo");
        Check(platformInfo.GetProperty("id").GetInt32() == 161 &&
            platformInfo.GetProperty("cryptocurrencyId").GetInt32() == 321 &&
            platformInfo.GetProperty("name").GetString() == "Fixture" &&
            platformInfo.GetProperty("dexerPlatformName").GetString() == "Fixture", "incorrect platformInfo");
        JsonElement point = result.GetProperty("chart").GetProperty("data").GetProperty("points")[0];
        Check(point.GetProperty("s").GetString() == "1700000000" &&
            point.GetProperty("v")[0].GetRawText() == PrecisePrice, "chart point was changed");
    }

    public static void RejectsInvalidPageWithoutFetchingChart()
    {
        string htmlWithoutNextData = """<script>{"platformInfo":{"cryptocurrencyId":123}}</script>""";
        AssertFailure(Reply(htmlWithoutNextData), "__NEXT_DATA__");
        AssertFailure(Reply("""<script id="__NEXT_DATA__">{</script>"""), "not valid JSON");
        AssertFailure(Reply(PageWith("""{"id":161}""")), "platformInfo.cryptocurrencyId");
        AssertFailure(Reply(PageWith("""{"cryptocurrencyId":0}""")), "platformInfo.cryptocurrencyId");
        AssertFailure(Reply("", HttpStatusCode.NotFound), "coin page returned HTTP 404");
    }

    public static void ReportsChartFailures()
    {
        HttpResponseMessage Page() => Reply(PageWith("""{"cryptocurrencyId":321}"""));
        AssertFailure(Page(), "chart returned HTTP 429", Reply("", HttpStatusCode.TooManyRequests));
        AssertFailure(Page(), "not valid JSON", Reply("<html>unavailable</html>"));
        AssertFailure(Page(), "status.error_code", Reply("""{"status":{"error_code":"123"}}"""));
        AssertFailure(Page(), "data.points", Reply("""{"status":{"error_code":"0"},"data":{}}"""));
    }

    private static string PageWith(string platformInfo) => $$"""
        <script type="application/json" id='__NEXT_DATA__'>
        {
          "platformInfo": {"cryptocurrencyId": 999},
          "props": {
            "pageProps": {
              "detailRes": {
                "popularCryptoInfo": {
                  "platformInfo": {{platformInfo}}
                }
              }
            }
          }
        }
        </script>
        """;

    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    private static void AssertFailure(HttpResponseMessage page, string expected, HttpResponseMessage? chart = null)
    {
        using var handler = new StubHandler((_, index) => index switch
        {
            0 => page,
            1 when chart is not null => chart,
            _ => throw new InvalidOperationException("unexpected request after a failure"),
        });
        using var http = new HttpClient(handler);
        try
        {
            CoinMarketCapChart.FetchAsync(http, "fixture-coin").GetAwaiter().GetResult();
        }
        catch (CoinMarketCapException error)
        {
            Check(error.Message.Contains(expected, StringComparison.Ordinal), $"expected [{expected}], got [{error.Message}]");
            return;
        }

        throw new InvalidOperationException($"expected failure: {expected}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> reply) : HttpMessageHandler
    {
        private int index;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(reply(request, index++));
    }
}
