using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Portfel.Application;

namespace Portfel.Infrastructure;

public sealed record ChatGptModel(string Id, string Name);
public sealed record ChatGptIdentity(string Subject, string? Email);
public sealed record ChatGptTokens(string Access, string? Refresh, string? Identity, string[] Scopes, DateTimeOffset ExpiresAt)
{
    public bool PlanAllowed => Scopes.Contains("chatgpt.tokens.use.direct", StringComparer.Ordinal) &&
        Scopes.Contains("resource.invoke", StringComparer.Ordinal);
}

public sealed class ChatGptPlanClient(HttpClient http, TimeProvider clock)
{
    public const string Issuer = "https://auth.openai.com";
    public const string AuthorizeEndpoint = Issuer + "/api/accounts/authorize";
    public const string Resource = "https://api.openai.com/v1";
    public const string RegistrationClient = "dynamic_agent_client";
    public const string RequestedScopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    private const string TokenEndpoint = Issuer + "/api/accounts/oauth/token";

    public static bool ValidClient(string? id) => id is { Length: > 7 and <= 180 } && id.StartsWith("oaiapp_", StringComparison.Ordinal)
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public static bool ValidModel(string? id) => id is { Length: > 0 and <= 120 } &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    public Task<ChatGptTokens> ExchangeAsync(string client, string code, string verifier, string redirectUri, CancellationToken ct) =>
        TokenAsync(new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = client, ["code"] = code,
            ["code_verifier"] = verifier, ["redirect_uri"] = redirectUri, ["resource"] = Resource,
        }, null, ct);

    public Task<ChatGptTokens> RefreshAsync(string client, ChatGptTokens previous, CancellationToken ct) =>
        TokenAsync(new()
        {
            ["grant_type"] = "refresh_token", ["client_id"] = client, ["refresh_token"] = previous.Refresh!, ["resource"] = Resource,
        }, previous.Scopes, ct);

    public async Task<ChatGptIdentity> ValidateIdentityAsync(string jwt, string client, string nonce, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Issuer + "/.well-known/jwks.json");
        using var keys = await ReadAsync(request, ct);
        var handler = new JsonWebTokenHandler { MaximumTokenSizeInBytes = 24 * 1024, MapInboundClaims = false };
        var validation = await handler.ValidateTokenAsync(jwt, new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = client,
            IgnoreTrailingSlashWhenValidatingAudience = false,
            IssuerSigningKeys = new JsonWebKeySet(keys.RootElement.GetRawText()).GetSigningKeys(),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        });
        if (!validation.IsValid || validation.SecurityToken is not JsonWebToken token ||
            !token.Audiences.SequenceEqual([client], StringComparer.Ordinal) ||
            !token.TryGetPayloadValue("nonce", out string? actualNonce) || actualNonce != nonce ||
            token.Subject is not { Length: > 0 and <= 512 } subject)
            throw new AppFault("chatgpt_identity_invalid", "Не удалось проверить личность аккаунта ChatGPT.", 502);
        token.TryGetPayloadValue("email", out string? email);
        return new(subject, email is { Length: > 0 and <= 320 } ? email : null);
    }

    public async Task<ChatGptModel[]> ModelsAsync(string access, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Resource + "/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var json = await ReadAsync(request, ct);
        var models = Property(json.RootElement, "models");
        if (models.ValueKind != JsonValueKind.Array) throw InvalidReply();
        return models.EnumerateArray().Where(row => Text(row, "visibility") == "list" && ValidModel(Text(row, "slug")))
            .Select(row => new ChatGptModel(Text(row, "slug")!, Text(row, "display_name") is { Length: > 0 and <= 120 } name
                ? name : Text(row, "slug")!)).DistinctBy(row => row.Id).Take(150).ToArray();
    }

    public async Task<bool> RevokeAsync(string client, string refresh, CancellationToken ct)
    {
        for (int i = 0; i < 2; i++)
        {
            try
            {
                using var discovery = new HttpRequestMessage(HttpMethod.Get, Issuer + "/.well-known/openid-configuration");
                using var metadata = await ReadAsync(discovery, ct);
                if (Text(metadata.RootElement, "issuer") != Issuer ||
                    Text(metadata.RootElement, "revocation_endpoint") is not { } endpoint || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                    || uri.GetLeftPart(UriPartial.Authority) != Issuer || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                    return false;
                using var request = new HttpRequestMessage(HttpMethod.Post, uri)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["token"] = refresh, ["token_type_hint"] = "refresh_token", ["client_id"] = client,
                    }),
                };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode == HttpStatusCode.OK) return true;
                if ((int)response.StatusCode < 500) return false;
            }
            catch (AppFault ex) when (ex.Status >= 500) { }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException) { }
            if (i == 0) await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
        }
        return false;
    }

    private async Task<ChatGptTokens> TokenAsync(Dictionary<string, string> form, string[]? previousScopes, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        using var reply = await ReadAsync(request, ct);
        var root = reply.RootElement;
        if (Text(root, "access_token") is not { Length: > 0 and <= 24576 } access || access.Any(c => !char.IsAscii(c) || char.IsWhiteSpace(c) || char.IsControl(c)) ||
            !string.Equals(Text(root, "token_type"), "Bearer", StringComparison.OrdinalIgnoreCase) ||
            Property(root, "expires_in") is not { ValueKind: JsonValueKind.Number } lifespan || !lifespan.TryGetInt32(out int seconds) ||
            seconds is < 1 or > 86400)
            throw InvalidReply();
        string[] scopes = Text(root, "scope")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? previousScopes ?? [];
        string? refresh = Text(root, "refresh_token");
        if (refresh?.Length > 24576 || (scopes.Contains("offline_access", StringComparer.Ordinal) && string.IsNullOrEmpty(refresh)))
            throw InvalidReply();
        return new(access, refresh, Text(root, "id_token"), scopes, clock.GetUtcNow().AddSeconds(seconds));
    }

    private async Task<JsonDocument> ReadAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            await using var body = new BoundedReadStream(await response.Content.ReadAsStreamAsync(timeout.Token), 1024 * 1024);
            if (!response.IsSuccessStatusCode)
            {
                string? error = null;
                try { using var payload = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token); error = Text(payload.RootElement, "error"); }
                catch (JsonException) { }
                throw error == "invalid_grant" || response.StatusCode == HttpStatusCode.Unauthorized
                    ? new AppFault("chatgpt_reconnect", "Сессия ChatGPT недействительна. Войдите заново.", 401)
                    : response.StatusCode switch
                    {
                        HttpStatusCode.Forbidden => new("chatgpt_permission_denied", "ChatGPT не разрешил доступ для этого аккаунта или размещения.", 403),
                        HttpStatusCode.TooManyRequests => new("chatgpt_rate_limited", "ChatGPT ограничил запросы. Проверьте лимиты аккаунта.", 429),
                        _ => new("chatgpt_unavailable", "Не удалось подключиться к ChatGPT.", 502),
                    };
            }
            return await JsonDocument.ParseAsync(body, new JsonDocumentOptions { MaxDepth = 32, AllowDuplicateProperties = false }, timeout.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
        {
            throw new AppFault("chatgpt_unavailable", "ChatGPT не ответил. Попробуйте позже.", 502);
        }
        catch (JsonException) { throw InvalidReply(); }
    }

    private static AppFault InvalidReply() => new("chatgpt_invalid_reply", "ChatGPT вернул ответ в неожиданном формате.", 502);
    private static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field : default;
    private static string? Text(JsonElement value, string name) =>
        Property(value, name) is { ValueKind: JsonValueKind.String } field ? field.GetString() : null;
}
