using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Portfel.Application;
using Portfel.Infrastructure;

namespace Portfel.Api;

internal static class ChatGptLabEndpoints
{
    private const string BrowserCookie = "portfel-chatgpt-lab";

    public static void AddChatGptLab(this WebApplicationBuilder builder)
    {
        if (!builder.Environment.IsDevelopment() || !OperatingSystem.IsWindows() ||
            !builder.Configuration.GetValue<bool>("ChatGptLab:Enabled")) return;
        string? directory = builder.Configuration["ChatGptLab:Directory"];
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) ||
            !int.TryParse(builder.Configuration["ChatGptLab:Port"], out int port) || port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(builder.Configuration["Security:KeyDirectory"]))
            throw new InvalidOperationException("ChatGPT Lab requires an absolute data path, a local port and a Data Protection key directory.");

        // This preview is a local desktop tool, not a remotely hosted OAuth client.
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.AddSingleton(new ChatGptLabSettings(port, directory));
        builder.Services.AddSingleton(_ =>
        {
            var http = ProviderHttp.Create(useCookies: false);
            http.Timeout = Timeout.InfiniteTimeSpan;
            return http;
        });
        builder.Services.AddSingleton<ChatGptPlanClient>();
        builder.Services.AddSingleton<ChatGptLabSessions>();
        builder.Services.AddSingleton<LabOpinionRunner>(services =>
        {
            var sessions = services.GetRequiredService<ChatGptLabSessions>();
            var http = services.GetRequiredService<HttpClient>();
            var clock = services.GetRequiredService<TimeProvider>();
            return async (browser, coin, model, ct) =>
            {
                string access = await sessions.AccessAsync(browser, ct);
                var now = clock.GetUtcNow();
                var provider = new AiHttpProvider(http, AiTransport.ChatGptPlan, access,
                    new(true, true, true, true, false, "unknown", now, Model: model));
                return await CoinOpinion.GenerateAsync(provider, model, coin, now, ct);
            };
        });
        builder.Services.AddSingleton<ChatGptLabJobs>();
        builder.Services.AddHostedService(services => services.GetRequiredService<ChatGptLabJobs>());
    }

    public static void MapChatGptLab(this WebApplication app)
    {
        if (app.Services.GetService<ChatGptLabSettings>() is not { } options) return;
        var group = app.MapGroup(ChatGptLabSettings.ApiPath);
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var request = http.Request;
            if (request.Scheme != "http" || request.Host.Host != "127.0.0.1" || request.Host.Port != options.Port ||
                http.Connection.LocalIpAddress is not { } local || !IPAddress.IsLoopback(local) ||
                http.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
                return Results.NotFound();

            if (!HttpMethods.IsGet(request.Method))
                await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
            return await next(context);
        });

        group.MapGet("/session", async (HttpContext http, IAntiforgery csrf, ChatGptLabSessions sessions, CancellationToken ct) =>
        {
            Guid browser;
            if (CurrentBrowser(http) is { } existing) browser = existing;
            else
            {
                browser = Guid.NewGuid();
                http.Response.Cookies.Append(BrowserCookie, browser.ToString("N"), new CookieOptions
                {
                    HttpOnly = true, SameSite = SameSiteMode.Lax, IsEssential = true, MaxAge = TimeSpan.FromDays(30),
                    Path = ChatGptLabSettings.ApiPath,
                });
            }
            var (status, email) = await sessions.StatusAsync(browser, ct);
            return new { status, email, csrf_token = csrf.GetAndStoreTokens(http).RequestToken };
        });

        group.MapPost("/connect", async (HttpContext http, ChatGptLabSessions sessions, CancellationToken ct) =>
            new { url = await sessions.BeginAsync(RequireBrowser(http), ct) });
        group.MapGet("/callback", Callback);
        group.MapPost("/disconnect", async (HttpContext http, ChatGptLabSessions sessions, ChatGptLabJobs jobs, CancellationToken ct) =>
        {
            Guid browser = RequireBrowser(http);
            jobs.Cancel(browser);
            bool revoked = await sessions.DisconnectAsync(browser, ct);
            jobs.Cancel(browser);
            return new { revoked };
        });
        group.MapGet("/models", (HttpContext http, ChatGptLabSessions sessions, CancellationToken ct) =>
            sessions.ModelsAsync(RequireBrowser(http), ct));
        group.MapGet("/jobs", (HttpContext http, ChatGptLabJobs jobs) => jobs.List(RequireBrowser(http)));
        group.MapPost("/jobs", async (StartJob body, HttpContext http, ChatGptLabSessions sessions, ChatGptLabJobs jobs, CancellationToken ct) =>
        {
            Guid browser = RequireBrowser(http);
            if (body.Id == Guid.Empty || body.CoinId is null || !CoinGeckoMarket.ValidId(body.CoinId) || !ChatGptPlanClient.ValidModel(body.Model))
                throw new AppFault("lab_bad_job", "Выберите монету по CoinGecko ID и доступную модель ChatGPT.");
            if (!(await sessions.ModelsAsync(browser, ct)).Any(model => model.Id == body.Model))
                throw new AppFault("lab_bad_model", "Модель не входит в список доступных на вашем аккаунте.", 409);
            return Results.Json(jobs.Submit(browser, body.Id, body.CoinId, body.Model), statusCode: 202);
        }).WithMetadata(new RequestSizeLimitAttribute(4096));
        group.MapPost("/batches", async (StartBatch body, HttpContext http, ChatGptLabSessions sessions, ChatGptLabJobs jobs, CancellationToken ct) =>
        {
            Guid browser = RequireBrowser(http);
            if (body.Id == Guid.Empty || body.CoinIds is not { Length: > 0 and <= ChatGptLabJobs.MaxBatchCoins } ||
                body.CoinIds.Any(coin => coin is null || !CoinGeckoMarket.ValidId(coin)) ||
                body.CoinIds.Length != body.CoinIds.Distinct(StringComparer.Ordinal).Count() || !ChatGptPlanClient.ValidModel(body.Model))
                throw new AppFault("lab_bad_batch", $"Укажите до {ChatGptLabJobs.MaxBatchCoins} уникальных CoinGecko ID и доступную модель.");
            if (!(await sessions.ModelsAsync(browser, ct)).Any(model => model.Id == body.Model))
                throw new AppFault("lab_bad_model", "Модель не входит в список доступных на вашем аккаунте.", 409);
            return Results.Json(jobs.SubmitBatch(browser, body.Id, body.CoinIds, body.Model), statusCode: 202);
        }).WithMetadata(new RequestSizeLimitAttribute(8192));
        group.MapPost("/batches/{id:guid}/cancel", (Guid id, HttpContext http, ChatGptLabJobs jobs) =>
        {
            jobs.Cancel(RequireBrowser(http), id);
            return Results.NoContent();
        });
    }

    private static async Task<IResult> Callback(HttpContext http, ChatGptLabSessions sessions, CancellationToken ct)
    {
        var query = http.Request.Query;
        string status = "invalid_state";
        if (CurrentBrowser(http) is { } browser && new[] { "state", "code", "client_id", "error" }.All(key => query[key].Count <= 1))
        {
            try
            {
                status = await sessions.CompleteAsync(browser, query["state"].FirstOrDefault(), query["code"].FirstOrDefault(),
                    query["client_id"].FirstOrDefault(), query["error"].FirstOrDefault(), ct);
            }
            catch (AppFault fault) { status = fault.Code; }
            catch (Exception) when (!ct.IsCancellationRequested) { status = "failed"; }
        }
        return Results.Redirect($"{ChatGptLabSettings.Page}?connection={Uri.EscapeDataString(status)}");
    }

    private static Guid? CurrentBrowser(HttpContext http) =>
        Guid.TryParseExact(http.Request.Cookies[BrowserCookie], "N", out Guid id) && id != Guid.Empty ? id : null;

    private static Guid RequireBrowser(HttpContext http) => CurrentBrowser(http)
        ?? throw new AppFault("lab_session_required", "Откройте тестовую страницу ChatGPT заново.", 401);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StartJob(Guid Id, string CoinId, string Model);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StartBatch(Guid Id, string[] CoinIds, string Model);
}
