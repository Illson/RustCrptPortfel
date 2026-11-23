using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Portfel.Application;
using Portfel.Infrastructure;

namespace Portfel.Api;

internal sealed record ChatGptLabSettings(int Port, string Directory)
{
    public const string ApiPath = "/api/v1/chatgpt-lab";
    public const string Page = "/chatgpt-lab";
    public string Callback => $"http://127.0.0.1:{Port}{ApiPath}/callback";
}

internal sealed class ChatGptLabSessions(ChatGptLabSettings options, IDataProtectionProvider protection, ChatGptPlanClient client, TimeProvider clock)
{
    private readonly IDataProtector secrets = protection.CreateProtector("ChatGptLab", "credentials", "v1");
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<Guid, Attempt> pending = [];
    private readonly Dictionary<Guid, ModelCache> models = [];
    private string? hostId;

    public Task<(string Status, string? Email)> StatusAsync(Guid browser, CancellationToken ct) => LockedAsync(() =>
    {
        var profile = Read(browser);
        string status = profile?.Tokens is not { } tokens ? "not_connected" : !tokens.PlanAllowed ? "plan_disabled"
            : HasAccess(tokens) ? "connected" : "not_connected";
        return Task.FromResult((status, profile?.Identity.Email));
    }, ct);

    public Task<string> BeginAsync(Guid browser, CancellationToken ct) => LockedAsync(() =>
    {
        DateTimeOffset now = clock.GetUtcNow();
        foreach (Guid expired in pending.Where(entry => entry.Value.ExpiresAt <= now).Select(entry => entry.Key).ToArray()) pending.Remove(expired);
        if (pending.Count >= 8 && !pending.ContainsKey(browser)) throw new AppFault("too_many_sign_ins", "Другой вход в ChatGPT ещё не завершён.", 429);
        Profile? profile = Read(browser);
        string host = HostId();
        var attempt = new Attempt(RandomToken(), RandomToken(), RandomToken(), now.AddMinutes(10), profile?.Client);
        pending[browser] = attempt;
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = attempt.Client ?? ChatGptPlanClient.RegistrationClient,
            ["ext_agent_host_id"] = host,
            ["response_type"] = "code",
            ["redirect_uri"] = options.Callback,
            ["scope"] = ChatGptPlanClient.RequestedScopes,
            ["resource"] = ChatGptPlanClient.Resource,
            ["state"] = attempt.State, ["nonce"] = attempt.Nonce,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(attempt.Verifier))),
        };
        if (profile is null) query["agent_name_hint"] = "dotnetCrptPortfel";
        if (profile?.Tokens?.PlanAllowed == false) query["prompt"] = "consent";
        return Task.FromResult(QueryHelpers.AddQueryString(ChatGptPlanClient.AuthorizeEndpoint, query));
    }, ct);

    public Task<string> CompleteAsync(Guid browser, string? state, string? code, string? clientId, string? error, CancellationToken ct) =>
        LockedAsync(async () =>
        {
            if (!pending.TryGetValue(browser, out var attempt) || attempt.ExpiresAt <= clock.GetUtcNow() ||
                state is not { Length: 43 } ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(attempt.State), Encoding.UTF8.GetBytes(state)))
                return "invalid_state";
            pending.Remove(browser); // A callback can only be used once, even on an upstream failure.
            if (error is not null) return error == "access_denied" ? "denied" : "failed";
            string? issued = attempt.Client ?? clientId;
            if (!ChatGptPlanClient.ValidClient(issued) || code is not { Length: > 0 and <= 4096 } ||
                (attempt.Client is not null && clientId is not null && attempt.Client != clientId))
                return "invalid_callback";

            Profile? previous = Read(browser);
            var tokens = await client.ExchangeAsync(issued!, code, attempt.Verifier, options.Callback, ct);
            if (tokens.Identity is not { Length: > 0 and <= 24576 } jwt) return "identity_invalid";
            var identity = await client.ValidateIdentityAsync(jwt, issued!, attempt.Nonce, ct);
            if (previous is not null && (previous.Client != issued || previous.Identity.Subject != identity.Subject))
                return "identity_invalid";
            Write(browser, new(issued!, identity, tokens));
            models.Remove(browser);
            return tokens.PlanAllowed ? "connected" : "plan_disabled";
        }, ct);

    public Task<ChatGptModel[]> ModelsAsync(Guid browser, CancellationToken ct) => LockedAsync(async () =>
    {
        string access = await AccessCoreAsync(browser, ct);
        var now = clock.GetUtcNow();
        if (models.TryGetValue(browser, out var cached) && cached.ExpiresAt > now) return cached.Models;
        try
        {
            var found = await client.ModelsAsync(access, ct);
            foreach (var id in models.Where(entry => entry.Value.ExpiresAt <= now).Select(entry => entry.Key).ToArray()) models.Remove(id);
            models[browser] = new(found, now.AddMinutes(5));
            return found;
        }
        catch (AppFault fault) when (fault.Code == "chatgpt_reconnect")
        {
            ForgetTokens(browser);
            throw;
        }
    }, ct);

    public Task<string> AccessAsync(Guid browser, CancellationToken ct) => LockedAsync(() => AccessCoreAsync(browser, ct), ct);

    public Task<bool> DisconnectAsync(Guid browser, CancellationToken ct) => LockedAsync(async () =>
    {
        pending.Remove(browser);
        var profile = Read(browser);
        if (profile?.Tokens is not { } tokens) return true;
        // Sign out locally even if upstream revocation fails.
        ForgetTokens(browser);
        return tokens.Refresh is { } refresh && await client.RevokeAsync(profile.Client, refresh, ct);
    }, ct);

    private async Task<string> AccessCoreAsync(Guid browser, CancellationToken ct)
    {
        var profile = Read(browser);
        if (profile?.Tokens is not { } tokens || !HasAccess(tokens))
            throw new AppFault("chatgpt_reconnect", "Сначала подключите аккаунт ChatGPT.", 401);
        if (!tokens.PlanAllowed) throw new AppFault("chatgpt_plan_disabled", "Аккаунт не разрешил использовать подписку ChatGPT.", 403);
        if (tokens.ExpiresAt > clock.GetUtcNow().AddMinutes(2) || tokens.Refresh is null) return tokens.Access;
        ChatGptTokens fresh;
        try { fresh = await client.RefreshAsync(profile.Client, tokens, ct); }
        catch (AppFault fault) when (fault.Code == "chatgpt_reconnect") { ForgetTokens(browser); throw; }
        // The original ID token was validated with this registration's nonce; don't replace it with an unverified hint.
        fresh = fresh with { Identity = tokens.Identity };
        Write(browser, profile with { Tokens = fresh });
        if (!fresh.PlanAllowed)
        {
            models.Remove(browser);
            throw new AppFault("chatgpt_plan_disabled", "Право на использование подписки ChatGPT больше не выдано.", 403);
        }
        return fresh.Access;
    }

    private bool HasAccess(ChatGptTokens tokens) => tokens.ExpiresAt > clock.GetUtcNow() || tokens.Refresh is not null;

    private void ForgetTokens(Guid browser)
    {
        if (Read(browser) is { } profile) Write(browser, profile with { Tokens = null });
        models.Remove(browser);
    }

    private string HostId()
    {
        if (hostId is not null) return hostId;
        string path = Path.Combine(options.Directory, "host-id");
        if (File.Exists(path))
        {
            string saved = File.ReadAllText(path);
            if (saved.StartsWith("urn:uuid:", StringComparison.Ordinal) && Guid.TryParseExact(saved[9..], "D", out _))
                return hostId = saved;
            throw new InvalidDataException("Invalid local ChatGPT host ID.");
        }
        string created = $"urn:uuid:{Guid.NewGuid():D}";
        AtomicWrite(path, created);
        return hostId = created;
    }

    private Profile? Read(Guid browser)
    {
        string path = ProfilePath(browser);
        return File.Exists(path) ? JsonSerializer.Deserialize<Profile>(secrets.Unprotect(File.ReadAllText(path))) : null;
    }

    private void Write(Guid browser, Profile value) => AtomicWrite(ProfilePath(browser), secrets.Protect(JsonSerializer.Serialize(value)));

    private string ProfilePath(Guid browser) => Path.Combine(options.Directory, $"{browser:N}.protected");

    private static void AtomicWrite(string path, string data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, data);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private async Task<T> LockedAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return await action(); }
        finally { gate.Release(); }
    }

    private static string RandomToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private sealed record Attempt(string State, string Nonce, string Verifier, DateTimeOffset ExpiresAt, string? Client);
    private sealed record Profile(string Client, ChatGptIdentity Identity, ChatGptTokens? Tokens);
    private sealed record ModelCache(ChatGptModel[] Models, DateTimeOffset ExpiresAt);
}
