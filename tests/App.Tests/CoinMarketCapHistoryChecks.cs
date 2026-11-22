using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Portfel.Application;
using Portfel.Infrastructure;
using static Checks;

internal static class CoinMarketCapHistoryChecks
{
    internal static async Task RunAsync(Func<DbContext> open, DateTimeOffset now)
    {
        const string price = "0.213899001479148860000000000123";
        const string points = $$$"""{"status":{"error_code":"0"},"data":{"points":[{"s":"1392336000","v":[{{{price}}},1,2]}]}}""";
        var clock = new MutableClock(now);
        var upstream = new List<(DateTimeOffset At, string Path)>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool fail = false;
        bool malformed = false;
        bool interrupted = false;
        using var http = new HttpClient(new MockHttp(async (request, ct) =>
        {
            var uri = request.RequestUri!;
            Require(request.Method == HttpMethod.Get && request.Content is null && !request.Headers.Contains("Cookie"),
                "CoinMarketCap requests are anonymous GETs");
            upstream.Add((clock.Now, uri.PathAndQuery));
            if (interrupted) throw new InvalidOperationException("simulated interruption after reservation");
            if (uri.Host == "coinmarketcap.com")
            {
                if (upstream.Count == 1)
                {
                    started.SetResult();
                    await release.Task.WaitAsync(ct);
                }
                if (malformed) return MockHttp.Reply("<html>fixture</html>", media: "text/html");
                return MockHttp.Reply("""
                    <script id="__NEXT_DATA__">
                    {"props":{"pageProps":{"detailRes":{"popularCryptoInfo":{"platformInfo":{"id":161,"cryptocurrencyId":131}}}}}}
                    </script>
                    """, media: "text/html");
            }
            Require(uri.Host == "api.coinmarketcap.com" &&
                uri.Query == "?id=131&interval=7d&convertId=2781&range=All", "Chart uses stored cryptocurrencyId and fixed USD options");
            if (!fail) return MockHttp.Reply(points);
            var throttled = MockHttp.Reply("{}", HttpStatusCode.TooManyRequests);
            throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return throttled;
        }));

        async Task<CmcChartResult> Chart(string slug)
        {
            await using var db = open();
            return await new CoinMarketCapHistory(db, http, clock).ChartAsync(slug, default);
        }
        async Task<CmcMonitor> Monitor()
        {
            await using var db = open();
            return await new CoinMarketCapHistory(db, http, clock).MonitorAsync(default);
        }

        try { _ = await Chart("../dash"); throw new InvalidOperationException("Invalid slug passed validation"); }
        catch (AppFault fault) when (fault is { Code: "invalid_cmc_slug", Status: 400 }) { }
        Require((await Monitor()).Requests.Length == 0 && upstream.Count == 0, "Monitoring and validation do not call upstream");
        var first = Chart("dash");
        await Task.WhenAny(started.Task, first).WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            if (!started.Task.IsCompleted) _ = await first;
            Require((await Chart("dash")).Status == 429 && (await Chart("other")).Status == 429 && upstream.Count == 1,
                "In-flight reservation blocks other callers across contexts");
            Require((await Monitor()).Requests.Single().Outcome == "running", "Monitoring shows the reserved request before the reply");
        }
        finally { release.TrySetResult(); }

        var discovery = await first;
        Require(discovery is { Status: 202, State: "platform_cached" } && discovery.NextAttemptAt == now.AddSeconds(30),
            "First GET persists the platform and separates the two upstream requests");
        Require((await Monitor()).Requests[0].Response!.Value.GetProperty("cryptocurrency_id").GetInt32() == 131,
            "Persist a small lookup response, not the HTML");
        clock.Now = now.AddSeconds(29);
        Require((await Chart("other")).Status == 429 && (await Chart("dash")).Status == 429 && upstream.Count == 1,
            "Both upstream endpoints share one cooldown even with new DbContexts");
        clock.Now = now.AddSeconds(30);
        var fresh = await Chart("dash");
        Require(fresh is { Status: 200, State: "refreshed" } && fresh.FetchedAt == clock.Now &&
            fresh.Json!.Contains(price, StringComparison.Ordinal), "Platform survives reopen and chart keeps numeric precision");
        await using (var db = open())
        {
            var history = new CoinMarketCapHistory(db, http, clock);
            Require(await history.CachedChartAsync("dash", default) == fresh.Json && upstream.Count == 2,
                "Reading full JSON from cache never starts an external request");
        }
        Require((await Monitor()).Requests[0].Response!.Value.GetProperty("points").GetInt32() == 1,
            "Persist a chart summary, not another copy of the chart JSON");

        clock.Now = now.AddSeconds(60);
        var alias = await Chart("dash-alias");
        Require(alias.Status == 202 && alias.NextAttemptAt == now.AddHours(1).AddSeconds(30),
            "Different slugs resolving to the same cryptocurrencyId share the hourly chart limit");
        clock.Now = now.AddHours(1).AddSeconds(29);
        Require((await Chart("dash-alias")).Status == 429 && (await Chart("dash")).State == "hit" && upstream.Count == 3,
            "Fresh cache and throttled requests do not reach CoinMarketCap");
        clock.Now = clock.Now.AddSeconds(1);
        fail = true;
        var stale = await Chart("dash");
        Require(stale is { Status: 200, State: "stale" } && stale.Json == fresh.Json && stale.FetchedAt == fresh.FetchedAt &&
            stale.NextAttemptAt == clock.Now.AddHours(1), "Provider failure preserves last good chart and starts a new hourly cooldown");
        clock.Now = clock.Now.AddSeconds(30);
        Require((await Chart("other")).NextAttemptAt == now.AddHours(1).AddSeconds(150), "Upstream Retry-After extends the persisted global pause");
        var monitor = await Monitor();
        Require(monitor.Requests.Length == 4 && monitor.Requests[0] is { Kind: "chart", Outcome: "rate_limited", HttpStatus: 429 } &&
            monitor.Requests[0].Response!.Value.GetProperty("error").GetString() == "http_error" &&
            monitor.Coins.Single(coin => coin.Slug == "dash").FetchedAt == fresh.FetchedAt && upstream.Count == 4,
            "Monitoring shows persisted cache and external attempts only");

        clock.Now = now.AddHours(2).AddSeconds(30);
        interrupted = true;
        try { _ = await Chart("dash"); throw new Exception("expected interrupted update"); }
        catch (InvalidOperationException error) when (error.Message == "simulated interruption after reservation") { }
        Require((await Chart("other")).Status == 429 && (await Chart("dash")).State == "stale",
            "Interrupted reservation survives reopen without overwriting the previous chart");
        clock.Now = clock.Now.AddMinutes(2);
        Require((await Monitor()).Requests[0] is { Outcome: "interrupted", FinishedAt: null }, "Monitor marks an expired reservation");
        interrupted = false;
        Require((await Chart("other")).Status == 202 && (await Monitor()).Requests[1].Outcome == "interrupted",
            "Next caller recovers expired global reservation");
        clock.Now = clock.Now.AddSeconds(30);
        malformed = true;
        Require((await Chart("broken")).Status == 502 &&
            (await Monitor()).Requests[0] is { Outcome: "failed", HttpStatus: 200, Response: { } response } &&
            response.GetProperty("error").GetString() == "invalid_response", "Keep upstream HTTP 200 even if parsing fails");
        Require(upstream.Zip(upstream.Skip(1)).All(pair => pair.Second.At - pair.First.At >= TimeSpan.FromSeconds(30)),
            "Every actual GET was separated by at least 30 seconds");
        Console.WriteLine("PASS CoinMarketCap cache, rate limits, concurrency, precision and recovery");
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
