using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Portfel.Application;
using Portfel.Infrastructure;

namespace Portfel.Api;

internal static class ApiEndpoints
{
    private const string MarketToolsUser = "local:market-tools";

    internal static void MapPortfelApi(this WebApplication app)
    {
        RouteGroupBuilder api = app.MapGroup("/api/v1");
        bool marketToolsAvailable = app.Environment.IsDevelopment();
        api.MapGet("/auth/session", (HttpContext http, IAntiforgery csrf) =>
            new Session(marketToolsAvailable ? csrf.GetAndStoreTokens(http).RequestToken : null,
                marketToolsAvailable, marketToolsAvailable && HasMarketSession(http)));

        if (marketToolsAvailable)
        {
            api.MapGet("/public/market/coins", (CoinGeckoMarket market, CancellationToken ct) => market.CoinsAsync(ct));
            api.MapGet("/public/market/quotes", (string? ids, CoinGeckoMarket market, CancellationToken ct) =>
                market.QuotesAsync(ids is { Length: > 0 and <= CoinGeckoMarket.MaxQueryLength } ? ids.Split(',') : [], ct));
            api.MapPost("/auth/market-tools", MarketToolsLogin);
            api.MapPost("/auth/logout", async (HttpContext http, IAntiforgery csrf) =>
            {
                await csrf.ValidateRequestAsync(http);
                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.NoContent();
            }).RequireAuthorization("market-tools");

            var market = api.MapGroup("/internal/market").RequireAuthorization("market-tools");
            market.MapGet("/requests", async (CoinMarketCapHistory history, CoinGeckoMarket gecko, CancellationToken ct) =>
            {
                var cmc = await history.MonitorAsync(ct);
                return cmc with
                {
                    Requests = cmc.Requests.Concat(gecko.Requests()).OrderByDescending(row => row.StartedAt)
                        .ThenByDescending(row => row.Id).Take(100).ToArray(),
                };
            });
            var coinmarketcap = market.MapGroup("/coinmarketcap");
            coinmarketcap.MapGet("/charts/{slug}", ReadCmcChart);
            coinmarketcap.MapGet("/cached/{slug}", async (string slug, CoinMarketCapHistory history, CancellationToken ct) =>
                (await history.CachedChartAsync(slug, ct)) is { } json ? Results.Text(json, "application/json") :
                    throw new AppFault("cached_chart_not_found", "Сохранённой истории для этой монеты нет.", 404));
            coinmarketcap.MapGet("/monitor", (CoinMarketCapHistory history, CancellationToken ct) => history.MonitorAsync(ct));
        }

        api.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        api.MapGet("/health/ready", Ready);
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", Ready);
        app.MapGet("/openapi/v1.json", () => Results.File(Path.Combine(AppContext.BaseDirectory, "openapi.json"), "application/json"));
        app.MapFallback("/api/{**path}", (HttpContext http) => WriteFault(http, new AppFault("not_found", "Метод API не найден.", 404)));
    }

    private static async Task<IResult> MarketToolsLogin(HttpContext http, IAntiforgery csrf)
    {
        await csrf.ValidateRequestAsync(http);
        if (!HasMarketSession(http))
        {
            var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, MarketToolsUser)], CookieAuthenticationDefaults.AuthenticationScheme);
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        }
        return Results.NoContent();
    }

    internal static bool HasMarketSession(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier) == MarketToolsUser;

    private static async Task<IResult> ReadCmcChart(string slug, HttpContext http, CoinMarketCapHistory history, TimeProvider clock,
        CancellationToken ct)
    {
        CmcChartResult result = await history.ChartAsync(slug, ct);
        if (result.NextAttemptAt is { } next)
        {
            http.Response.Headers["X-Chart-Next-Attempt-At"] = next.ToString("O", CultureInfo.InvariantCulture);
            if (result.Status is 202 or 429)
                http.Response.Headers.RetryAfter = Math.Max(1, Math.Ceiling((next - clock.GetUtcNow()).TotalSeconds))
                    .ToString(CultureInfo.InvariantCulture);
        }
        if (result.Json is { } json)
        {
            http.Response.Headers["X-Chart-Cache"] = result.State;
            http.Response.Headers["X-Chart-Fetched-At"] = result.FetchedAt!.Value.ToString("O", CultureInfo.InvariantCulture);
            return Results.Text(json, "application/json");
        }

        return result.State switch
        {
            "platform_cached" => Results.Json(new { status = result.State, next_attempt_at = result.NextAttemptAt }, statusCode: 202),
            "rate_limited" => throw new AppFault("cmc_rate_limited", "Лимит CoinMarketCap. Проверьте время следующей попытки в мониторинге.", 429),
            "coin_not_found" => throw new AppFault("cmc_coin_not_found", "Монета не найдена на CoinMarketCap.", 404),
            _ => throw new AppFault("cmc_provider_error", "Не удалось получить данные CoinMarketCap.", 502),
        };
    }

    private static async Task<IResult> Ready(IWorkspaceStore store, CancellationToken ct) => await store.ReadyAsync(ct)
        ? Results.Ok(new { status = "ready" }) : Results.Json(new { status = "not_ready" }, statusCode: 503);

    internal static Task WriteFault(HttpContext http, AppFault fault)
    {
        http.Response.StatusCode = fault.Status;
        return http.Response.WriteAsJsonAsync(new ApiError(fault.Code, fault.Message, fault.Status >= 500, http.TraceIdentifier, null),
            cancellationToken: http.RequestAborted);
    }

    private sealed record Session(string? CsrfToken, bool MarketToolsAvailable, bool MarketToolsAuthenticated);
    private sealed record ApiError(string Code, string Message, bool Retryable, string RequestId, object? Details);
}
