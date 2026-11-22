using System.Net;
using Portfel.Application;
using Portfel.Domain;
using Portfel.Infrastructure;
using static Checks;

internal static class CoinGeckoMarketChecks
{
    internal static async Task RunAsync(DateTimeOffset now)
    {
        var clock = new MutableClock(now);
        long timestamp = now.ToUnixTimeSeconds();
        string[] ids = ["future", "bitcoin", "dash"];
        int requests = 0;
        using var market = new CoinGeckoMarket(new HttpClient(new MockHttp((request, _) =>
        {
            Require(request.RequestUri!.Host == "api.coingecko.com" && request.Method == HttpMethod.Get && request.Content is null
                && request.Headers.UserAgent.ToString() == "dotnetCrptPortfel/1.0"
                && request.Headers.All(header => header.Key is "User-Agent" or "Accept"),
                "Public market requests are anonymous and have no holdings");
            requests++;
            if (request.RequestUri.AbsolutePath.EndsWith("/coins/list", StringComparison.Ordinal))
            {
                Require(request.RequestUri.Query == "?include_platform=false", "Catalog does not send portfolio data");
                return Task.FromResult(MockHttp.Reply("""[{"id":"bitcoin","name":"Bitcoin","symbol":"btc"}]"""));
            }
            Require(request.RequestUri.Query == "?ids=bitcoin,dash,future&vs_currencies=usd&include_last_updated_at=true&precision=full", "Only validated public IDs and quote options go upstream");
            return Task.FromResult(MockHttp.Reply($$$"""
                {"bitcoin":{"usd":9999.123456789012345678901234,"last_updated_at":{{{timestamp}}}},
                 "dash":{"usd":35,"last_updated_at":{{{timestamp + 60}}}},
                 "future":{"usd":2,"last_updated_at":{{{timestamp + 61}}}} }
                """));
        })), clock);
        Require((await market.CoinsAsync(default)).Single().Id == "bitcoin", "Catalog has stable IDs");
        await market.CoinsAsync(default);
        var quotes = await market.QuotesAsync(ids, default);
        Require(quotes is [{ Id: "bitcoin" } bitcoin, { Id: "dash" } dash] &&
            bitcoin.Usd == ExactNumber.Parse("9999.123456789012345678901234") && bitcoin.AsOf == now &&
            dash.AsOf == now.AddSeconds(60), "Keep exact price and original timestamp within clock tolerance; reject excessive future records");
        await market.QuotesAsync(["bitcoin", "future", "dash"], default);
        Require(requests == 2, "Catalog and positive and negative quote results are cached");
        await RejectAsync(() => market.QuotesAsync(["bitcoin&amount=123"], default), "invalid_asset_ids", 400);
        clock.Now = now.AddMinutes(1);
        await market.QuotesAsync(ids, default);
        Require(requests == 3, "Quote cache has bounded freshness");

        int limits = 0;
        using var throttled = new CoinGeckoMarket(new HttpClient(new MockHttp((_, _) =>
        {
            limits++;
            return Task.FromResult(MockHttp.Reply("{}", HttpStatusCode.TooManyRequests));
        })), clock);
        await RejectAsync(() => throttled.CoinsAsync(default), "rate_limited", 429);
        await RejectAsync(() => throttled.QuotesAsync(["bitcoin"], default), "rate_limited", 429);
        Require(limits == 1, "Provider throttling prevents immediate retry");
        Console.WriteLine("PASS CoinGecko live quote contract, privacy, precision, caching and rate limits");
    }

    private static async Task RejectAsync(Func<Task> action, string code, int status)
    {
        try { await action(); }
        catch (AppFault fault) when (fault.Code == code && fault.Status == status) { return; }
        throw new InvalidOperationException($"Expected {code} ({status}).");
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
