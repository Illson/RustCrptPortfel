using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Portfel.Application;
using Portfel.Domain;
using Portfel.Infrastructure;

namespace Portfel.Api;

internal static class ApiEndpoints
{
    internal static void MapPortfelApi(this WebApplication app)
    {
        RouteGroupBuilder api = app.MapGroup("/api/v1");
        api.MapGet("/auth/session", (HttpContext http, IAntiforgery csrf, DemoSettings demo) =>
        {
            bool loggedIn = demo.Enabled && http.User.FindFirstValue(ClaimTypes.NameIdentifier) == DemoPortfolio.Owner;
            return new Session(loggedIn, loggedIn, demo.Enabled, Integrations.CmcAuth, Integrations.CmcAccess,
                demo.Enabled ? csrf.GetAndStoreTokens(http).RequestToken : null);
        });
        api.MapGet("/auth/cmc/start", () => CmcAuth());
        api.MapMethods("/auth/cmc/callback", ["GET", "POST"], () => CmcAuth());
        api.MapPost("/auth/demo", DemoLogin);
        api.MapGet("/public/integrations", (DemoSettings demo) => Integrations.Get(demo.Enabled));
        if (app.Environment.IsDevelopment())
        {
            api.MapGet("/public/market/coins", (CoinGeckoMarket market, CancellationToken ct) => market.CoinsAsync(ct));
            api.MapGet("/public/market/quotes", (string? ids, CoinGeckoMarket market, CancellationToken ct) =>
                market.QuotesAsync(ids is { Length: > 0 and <= CoinGeckoMarket.MaxQueryLength } ? ids.Split(',') : [], ct));
        }
        api.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        api.MapGet("/health/ready", Ready);
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", Ready);
        app.MapGet("/openapi/v1.json", () => Results.File(Path.Combine(AppContext.BaseDirectory, "openapi.json"), "application/json"));

        RouteGroupBuilder owner = api.MapGroup("").RequireAuthorization("workspace");
        owner.AddEndpointFilter(async (context, next) =>
        {
            HttpContext http = context.HttpContext;
            if (!(HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)))
                await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
            return await next(context);
        });
        if (app.Environment.IsDevelopment())
        {
            var cmc = owner.MapGroup("/internal/market/coinmarketcap");
            cmc.MapGet("/charts/{slug}", ReadCmcChart);
            cmc.MapGet("/monitor", (CoinMarketCapHistory history, CancellationToken ct) => history.MonitorAsync(ct));
        }

        owner.MapPost("/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });
        owner.MapGet("/portfolio", async (HttpContext http, IWorkspaceStore store, TimeProvider clock, CancellationToken ct) =>
            PortfolioQueries.Portfolio(await Read(store, http, ct), clock.GetUtcNow(), true));
        owner.MapPost("/portfolio/sync", (HttpContext http, WorkspaceJobs jobs, CancellationToken ct) =>
            Queue(http, jobs, JobKind.PortfolioSync, null, ct));
        owner.MapGet("/jobs/{id:guid}", async (Guid id, HttpContext http, IWorkspaceStore store, CancellationToken ct) =>
            PortfolioQueries.Receipt(WorkspaceJobs.Get(await Read(store, http, ct), id)));
        owner.MapPost("/jobs/{id:guid}/cancel", (Guid id, HttpContext http, WorkspaceJobs jobs, CancellationToken ct) =>
            jobs.CancelAsync(Owner(http), id, ct));

        owner.MapGet("/assets", async (HttpContext http, IWorkspaceStore store, CancellationToken ct) =>
            (await Read(store, http, ct)).Assets.Select(asset => new { asset.Asset, asset.Links }));
        var legacyReads = owner.MapGroup("");
        legacyReads.AddEndpointFilter(async (context, next) => Results.Json(await next(context), LegacyFeedJson.Options));
        MapAnalysisReads(legacyReads);
        var v2 = app.MapGroup("/api/v2");
        MapAnalysisReads(v2.MapGroup("").RequireAuthorization("workspace"));
        v2.MapFallback("/{**path}", (HttpContext http) => WriteFault(http, new AppFault("not_found", "Метод API не найден.", 404)));
        owner.MapGet("/assets/{id}/metrics", async (string id, string? block, DateOnly? from, DateOnly? to, string? granularity,
            HttpContext http, IWorkspaceStore store, TimeProvider clock, CancellationToken ct) =>
        {
            AssetData asset = Asset(await Read(store, http, ct), id);
            if (block is not (null or "economy") || granularity is not (null or "month") || from > to)
                throw new AppFault("invalid_metrics_filter", "Поддерживаются месячные показатели экономики в заданном диапазоне.");
            return PortfolioQueries.Economy(asset, clock.GetUtcNow()).Select(series => series with
            {
                Months = series.Months.Where(item => (from is null || item.Month >= from) && (to is null || item.Month <= to)).ToArray(),
            });
        });
        owner.MapGet("/assets/{id}/supply", async (string id, HttpContext http, IWorkspaceStore store, TimeProvider clock, CancellationToken ct) =>
            PortfolioQueries.Supply(Asset(await Read(store, http, ct), id), clock.GetUtcNow()));
        owner.MapGet("/assets/{id}/markets", async (string id, HttpContext http, IWorkspaceStore store, TimeProvider clock, CancellationToken ct) =>
        {
            Workspace state = await Read(store, http, ct);
            return PortfolioQueries.Markets(Asset(state, id), clock.GetUtcNow(), state.Settings.MajorExchangeIds);
        });
        owner.MapGet("/assets/{id}/events", async (string id, HttpContext http, IWorkspaceStore store, CancellationToken ct) =>
            Asset(await Read(store, http, ct), id).Events.OrderByDescending(item => item.Version));
        owner.MapGet("/assets/{id}/sources", async (string id, HttpContext http, IWorkspaceStore store, CancellationToken ct) =>
            Asset(await Read(store, http, ct), id).Sources);
        owner.MapPost("/assets/{id}/analysis-runs", (string id, HttpContext http, WorkspaceJobs jobs, CancellationToken ct) =>
            Queue(http, jobs, JobKind.AssetAnalysis, id, ct));
        owner.MapGet("/events", async (HttpContext http, IWorkspaceStore store, CancellationToken ct) =>
            (await Read(store, http, ct)).Assets.SelectMany(asset => asset.Events).OrderByDescending(item => item.Version));

        owner.MapPatch("/feed/{id:guid}/read", async (Guid id, ReadState value, HttpContext http, IWorkspaceStore store, CancellationToken ct) =>
           await store.UpdateAsync(Owner(http), state =>
            {
                _ = state.Feed.SingleOrDefault(item => item.Id == id)
                    ?? throw new AppFault("feed_item_not_found", "Карточка не найдена.", 404);
                return (state with { Feed = state.Feed.Select(row => row.Id == id ? row with { Read = value.Read } : row).ToArray() }, new ReadState(value.Read));
            }, ct));
        owner.MapPost("/feed/{id:guid}/reports", Report);
        owner.MapGet("/integrations/status", (DemoSettings demo) => Integrations.Get(demo.Enabled));
        owner.MapGet("/settings", async (HttpContext http, IWorkspaceStore store, CancellationToken ct) => (await Read(store, http, ct)).Settings);
        owner.MapPatch("/settings", (Settings settings, HttpContext http, IWorkspaceStore store, CancellationToken ct) =>
            UpdateSettings(settings, http, store, ct));

        owner.MapGet("/ai/connections", async (HttpContext http, IWorkspaceStore store, CancellationToken ct) =>
            (await Read(store, http, ct)).AiConnections.Select(row => new AiConnectionView(row.Id, row.Provider, row.Name, row.Model,
                row.AuthMode, row.BillingMode, row.Status, row.CheckedAt)));
        owner.MapPost("/ai/connections", () => AiBlocked());
        owner.MapDelete("/ai/connections/{id:guid}", () => AiBlocked());
        owner.MapGet("/ai/connections/{id:guid}/capabilities", () => AiBlocked());
        owner.MapPost("/ai/chatgpt/connect", () => AiBlocked());
        owner.MapGet("/ai/chatgpt/callback", () => AiBlocked());
        owner.MapGet("/ai/routes", async (HttpContext http, IWorkspaceStore store, CancellationToken ct) => (await Read(store, http, ct)).AiRoutes);
        owner.MapPatch("/ai/routes", () => AiBlocked());
        api.MapFallback("/{**path}", (HttpContext http) => WriteFault(http, new AppFault("not_found", "Метод API не найден.", 404)));
    }

    private static void MapAnalysisReads(RouteGroupBuilder owner)
    {
        owner.MapGet("/feed", async (string? asset, string? type, string? importance, DateOnly? from, DateOnly? to, bool? read, Guid? cursor,
            HttpContext http, IWorkspaceStore store, TimeProvider clock, CancellationToken ct) =>
            PortfolioQueries.Feed(await Read(store, http, ct), asset, type, importance, from, to, read, cursor, clock.GetUtcNow()));
        owner.MapGet("/assets/{id}", async (string id, HttpContext http, IWorkspaceStore store, TimeProvider clock, CancellationToken ct) =>
        {
            Workspace state = await Read(store, http, ct);
            AssetData asset = Asset(state, id);
            var now = clock.GetUtcNow();
            var portfolio = PortfolioQueries.Portfolio(state, now, true);
            var history = PortfolioQueries.Feed(state, id, null, null, null, null, null, null, now);
            return new AssetView(asset.Asset, portfolio.Positions.SingleOrDefault(item => item.AssetId == id), asset.Links,
                asset.Observations.Max(item => (DateTimeOffset?)item.SourceAsOf), history.Items.FirstOrDefault()?.Analysis.Summary,
                history.Items, history.Updating);
        });
        owner.MapGet("/assets/{id}/analyses", async (string id, HttpContext http, IWorkspaceStore store, TimeProvider clock, CancellationToken ct) =>
        {
            Workspace state = await Read(store, http, ct);
            _ = Asset(state, id);
            var now = clock.GetUtcNow();
            return state.Feed.Where(item => item.AssetId == id).OrderByDescending(item => item.PublishedAt)
                .Select(item => PortfolioQueries.WithCurrentPersonalization(state, item, now));
        });
    }

    private static async Task<IResult> DemoLogin(HttpContext http, DemoSettings demo, IAntiforgery csrf,
        IWorkspaceStore store, WorkspaceJobs jobs, CancellationToken ct)
    {
        if (!demo.Enabled) throw new AppFault("not_found", "Маршрут не найден.", 404);
        await csrf.ValidateRequestAsync(http);
        var state = await store.ReadAsync(DemoPortfolio.Owner, ct);
        if (state.Current is null)
            await jobs.QueueAsync(DemoPortfolio.Owner, JobKind.PortfolioSync, null, "demo-initial-sync", true, ct);
        var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, DemoPortfolio.Owner)], CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return Results.NoContent();
    }

    private static async Task<IResult> Queue(HttpContext http, WorkspaceJobs jobs, JobKind kind, string? id, CancellationToken ct)
    {
        var keys = http.Request.Headers["Idempotency-Key"];
        if (keys.Count > 1) throw new AppFault("invalid_idempotency_key", "Передайте ровно один Idempotency-Key.");
        string key = keys.FirstOrDefault() ?? Guid.NewGuid().ToString("N");
        string? unsupportedBody = null;
        if (http.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
        {
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = 4096;
            try
            {
                using var body = await JsonDocument.ParseAsync(http.Request.Body, new JsonDocumentOptions { MaxDepth = 8 }, ct);
                if (body.RootElement.ValueKind != JsonValueKind.Object || body.RootElement.EnumerateObject().Any())
                    unsupportedBody = body.RootElement.GetRawText();
            }
            catch (JsonException) { throw new AppFault("invalid_request", "Неверный формат тела запроса."); }
        }
        JobReceipt result = await jobs.QueueAsync(Owner(http), kind, id, key, true, ct, unsupportedBody);
        return Results.Accepted($"/api/v1/jobs/{result.JobId}", result);
    }

    private static Task<Settings> UpdateSettings(Settings settings, HttpContext http, IWorkspaceStore store, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Timezone) || (settings.Timezone != "UTC" && !settings.Timezone.Contains('/'))
            || !TimeZoneInfo.TryFindSystemTimeZoneById(settings.Timezone, out _)
            || !TimeOnly.TryParseExact(settings.DailyTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || settings.MajorExchangeIds is null || settings.MajorExchangeIds.Length > 50
            || settings.MajorExchangeIds.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 80)
            || settings.InvestmentProfile?.Length > 800)
            throw new AppFault("invalid_settings", "Проверьте часовой пояс IANA, время HH:mm и список бирж.");
        return store.UpdateAsync(Owner(http), state => (state with { Settings = settings }, settings), ct);
    }

    private static async Task<IResult> Report(Guid id, ReportRequest report, HttpContext http, IWorkspaceStore store,
        TimeProvider clock, CancellationToken ct)
    {
        string? reason = report.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 800)
            throw new AppFault("invalid_report", "Опишите замечание в пределах 800 символов.");
        await store.UpdateAsync(Owner(http), state =>
        {
            if (!state.Feed.Any(item => item.Id == id)) throw new AppFault("feed_item_not_found", "Карточка не найдена.", 404);
            FeedReport[] reports = state.FeedReports ?? [];
            if (reports.Any(item => item.FeedItemId == id && item.Reason == reason)) return (state, true);
            return (state with { FeedReports = [.. reports, new(Guid.NewGuid(), id, reason, clock.GetUtcNow())] }, true);
        }, ct);
        return Results.NoContent();
    }

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

    private static Task<Workspace> Read(IWorkspaceStore store, HttpContext http, CancellationToken ct) => store.ReadAsync(Owner(http), ct);
    private static string Owner(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private static AssetData Asset(Workspace state, string id) => state.Assets.SingleOrDefault(asset => asset.Asset.Id == id)
        ?? throw new AppFault("asset_not_found", "Актив не найден.", 404);
    private static IResult CmcAuth() => throw new AppFault("CMC_AUTH_UNVERIFIED", "Вход через CoinMarketCap пока недоступен.", 409);
    private static IResult AiBlocked() => throw new AppFault("blocked_capability", "Подключение моделей недоступно в учебном режиме.", 409);

    private static async Task<IResult> Ready(IWorkspaceStore store, CancellationToken ct) => await store.ReadyAsync(ct)
        ? Results.Ok(new { status = "ready" }) : Results.Json(new { status = "not_ready" }, statusCode: 503);

    internal static Task WriteFault(HttpContext http, AppFault fault)
    {
        http.Response.StatusCode = fault.Status;
        return http.Response.WriteAsJsonAsync(new ApiError(fault.Code, fault.Message, fault.Status >= 500, http.TraceIdentifier, null),
            cancellationToken: http.RequestAborted);
    }

    private sealed record Session(bool Authenticated, bool Demo, bool DemoAvailable, string Authentication, string PortfolioAccess, string? CsrfToken);
    private sealed record ReadState(bool Read);
    private sealed record ReportRequest(string Reason);
    private sealed record ApiError(string Code, string Message, bool Retryable, string RequestId, object? Details);
}
