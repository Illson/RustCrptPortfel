using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.FileProviders;
using Portfel.Api;
using Portfel.Application;
using Portfel.Domain;
using Portfel.Infrastructure;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddPortfel(builder.Configuration, builder.Environment);
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = PortfelJson.Options.PropertyNamingPolicy;
    options.SerializerOptions.Converters.Add(PortfelJson.Options.Converters[0]);
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
});
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

bool local = builder.Environment.IsDevelopment();
if (local)
{
    builder.Logging.AddSimpleConsole(options => { options.SingleLine = true; options.TimestampFormat = "HH:mm:ss "; });
    builder.Services.AddSingleton(services => new CoinGeckoMarket(ProviderHttp.Create(), services.GetRequiredService<TimeProvider>(),
        services.GetRequiredService<ILogger<CoinGeckoMarket>>()));
    builder.Services.AddKeyedSingleton("coinmarketcap", (_, _) => CoinMarketCapHistory.CreateHttpClient());
    builder.Services.AddScoped(services => new CoinMarketCapHistory(
        services.GetRequiredService<PortfelDb>(),
        services.GetRequiredKeyedService<HttpClient>("coinmarketcap"), services.GetRequiredService<TimeProvider>(),
        services.GetRequiredService<ILogger<CoinMarketCapHistory>>()));
}
var protection = builder.Services.AddDataProtection().SetApplicationName($"dotnetCrptPortfel:{builder.Environment.EnvironmentName}");
if (builder.Configuration["Security:KeyDirectory"] is { Length: > 0 } keyDirectory)
    protection.PersistKeysToFileSystem(Directory.CreateDirectory(keyDirectory));
else if (local && builder.Configuration["Demo:DataDirectory"] is { Length: > 0 } demoDirectory)
    protection.PersistKeysToFileSystem(Directory.CreateDirectory(Path.Combine(demoDirectory, "keys")));
if (builder.Configuration["Security:KeyCertificatePath"] is { Length: > 0 } certificatePath)
    protection.ProtectKeysWithCertificate(X509CertificateLoader.LoadPkcs12FromFile(certificatePath, builder.Configuration["Security:KeyCertificatePassword"]));
else if (OperatingSystem.IsWindows())
    protection.ProtectKeysWithDpapi();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = local ? "portfel-dev" : "__Host-portfel";
    options.Cookie.Path = "/";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = local ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context => ApiEndpoints.WriteFault(context.HttpContext,
        new AppFault("unauthenticated", "Требуется вход.", 401));
    options.Events.OnRedirectToAccessDenied = context => ApiEndpoints.WriteFault(context.HttpContext,
        new AppFault("forbidden", "Доступ запрещён.", 403));
});
builder.Services.AddAuthorizationBuilder().AddPolicy("workspace", policy => policy.RequireAuthenticatedUser()
    .RequireAssertion(context => context.Resource is HttpContext http && http.RequestServices.GetRequiredService<DemoSettings>().Enabled
        && context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value == DemoPortfolio.Owner));
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-Token";
    options.SuppressReadingTokenFromFormBody = true;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = local ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
builder.AddChatGptLab();

WebApplication app = builder.Build();
app.Use(async (http, next) =>
{
    http.Response.Headers["Referrer-Policy"] = "no-referrer";
    http.Response.Headers["X-Content-Type-Options"] = "nosniff";
    http.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
    if (http.Request.Path.StartsWithSegments("/api")) http.Response.Headers.CacheControl = "no-store";
    try { await next(http); }
    catch (AppFault fault) when (!http.Response.HasStarted) { await ApiEndpoints.WriteFault(http, fault); }
    catch (AntiforgeryValidationException) when (!http.Response.HasStarted)
    {
        await ApiEndpoints.WriteFault(http, new AppFault("invalid_csrf", "Обновите страницу и повторите действие.", 403));
    }
    catch (BadHttpRequestException) when (!http.Response.HasStarted)
    {
        await ApiEndpoints.WriteFault(http, new AppFault("invalid_request", "Неверный формат запроса."));
    }
    catch (Exception) when (!http.Response.HasStarted && !http.RequestAborted.IsCancellationRequested)
    {
        app.Logger.LogError("Unhandled request failure {RequestId}", http.TraceIdentifier);
        await ApiEndpoints.WriteFault(http, new AppFault("internal_error", "Не удалось обработать запрос.", 500));
    }
});

string? frontend = app.Configuration["Frontend:Directory"];
if (!string.IsNullOrWhiteSpace(frontend) && Directory.Exists(frontend))
{
    var files = new PhysicalFileProvider(Path.GetFullPath(frontend));
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = files });
}

app.UseAuthentication();
app.UseAuthorization();
app.MapPortfelApi();
app.MapChatGptLab();
await app.RunAsync();
