using System.Diagnostics;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Portfel.Application;
using Portfel.CoinMarketCap;

namespace Portfel.Infrastructure;

public sealed record CmcChartResult(string State, string? Json, DateTimeOffset? FetchedAt, DateTimeOffset? NextAttemptAt, int Status);
public sealed record CmcCoinStatus(string Slug, bool HasPlatform, DateTimeOffset? FetchedAt, DateTimeOffset? NextAttemptAt);
public sealed record CmcRequestStatus(string Id, string Slug, string Kind, string Outcome, int? HttpStatus,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);
public sealed record CmcMonitor(DateTimeOffset AsOf, DateTimeOffset? GlobalNextAttemptAt, CmcCoinStatus[] Coins, CmcRequestStatus[] Requests);

public sealed class CoinMarketCapHistory(DbContext db, HttpClient http, TimeProvider clock, ILogger<CoinMarketCapHistory>? logger = null)
{
    private static readonly TimeSpan GlobalGap = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CoinGap = TimeSpan.FromHours(1);
    // Longer than the HTTP deadline plus the global gap, even after a crash.
    private static readonly TimeSpan ReservationWindow = TimeSpan.FromMinutes(2);
    private readonly ILogger<CoinMarketCapHistory> log = logger ?? NullLogger<CoinMarketCapHistory>.Instance;

    public static HttpClient CreateHttpClient() => CoinMarketCapChart.CreateClient();

    public async Task<CmcChartResult> ChartAsync(string slug, CancellationToken ct)
    {
        if (!CoinMarketCapChart.ValidSlug(slug))
            throw new AppFault("invalid_cmc_slug", "Укажите slug CoinMarketCap: до 120 строчных латинских букв, цифр или дефисов.", 400);

        var (coin, id, retryAt) = await ReserveAsync(slug, ct);
        if (id is null)
        {
            if (retryAt is null)
            {
                log.LogInformation("CoinMarketCap chart: served from cache");
                return new("hit", coin!.ChartJson, At(coin.FetchedAt), null, 200);
            }
            log.LogInformation("CoinMarketCap GET: skipped (cooldown until {RetryAt:O}, cached chart: {HasChart})",
                retryAt, coin?.ChartJson is not null);
            return Unavailable(coin, retryAt.Value, "rate_limited", 429);
        }

        string stage = coin?.PlatformJson is null ? "platform lookup" : "chart";
        log.LogInformation("CoinMarketCap GET ({Stage}): started, attempt {AttemptId}", stage, id);
        long started = Stopwatch.GetTimestamp();
        CoinMarketCapPlatform? platform = null;
        string? chart = null;
        CoinMarketCapException? failure = null;
        bool cancelled = false;
        try
        {
            if (coin?.PlatformJson is not { } stored)
                platform = await CoinMarketCapChart.FetchPlatformAsync(http, slug, ct);
            else
            {
                var mapping = CoinMarketCapChart.ReadStoredPlatform(stored);
                var raw = await CoinMarketCapChart.FetchChartAsync(http, mapping.CryptoId, ct);
                chart = CoinMarketCapChart.Format(slug, mapping.Info, raw);
            }
        }
        catch (CoinMarketCapException error) { failure = error; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { cancelled = true; }

        var when = clock.GetUtcNow();
        var globalNext = when + GlobalGap;
        if (failure?.RetryAfter is { } header && failure.Status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            // Bound untrusted values before adding them to a date.
            DateTimeOffset? until = header.Date ?? (header.Delta is { } delta ? when.AddTicks(Math.Min(delta.Ticks, (DateTimeOffset.MaxValue - when).Ticks)) : null);
            if (until > globalNext) globalNext = until.Value;
        }

        var coinNext = platform is not null ? (DateTimeOffset?)null : when + CoinGap;
        string outcome = failure?.Status == HttpStatusCode.TooManyRequests ? "rate_limited" :
            failure is not null ? "failed" : cancelled ? "cancelled" : "succeeded";
        int? status = failure?.Status is { } upstreamStatus ? (int)upstreamStatus : outcome == "succeeded" ? 200 : null;
        log.LogInformation("CoinMarketCap GET ({Stage}): {Outcome}, HTTP {Status}, {ElapsedMs:F0} ms, attempt {AttemptId}",
            stage, outcome, status?.ToString() ?? "-", Stopwatch.GetElapsedTime(started).TotalMilliseconds, id);
        coinNext = await FinishAsync(id, slug, coin?.CryptoId, platform, chart, when, globalNext, coinNext, outcome, status);
        ct.ThrowIfCancellationRequested();

        if (chart is not null) return new("refreshed", chart, At(Millis(when)), coinNext, 200);
        DateTimeOffset next = coinNext > globalNext ? coinNext.Value : globalNext;
        if (platform is not null) return new("platform_cached", null, null, next, 202);
        return failure?.Status switch
        {
            HttpStatusCode.TooManyRequests => Unavailable(coin, next, "rate_limited", 429),
            HttpStatusCode.NotFound when coin?.PlatformJson is null => Unavailable(coin, next, "coin_not_found", 404),
            _ => Unavailable(coin, next, "provider_error", 502),
        };
    }

    public async Task<CmcMonitor> MonitorAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var gate = await db.Set<CmcGateRow>().AsNoTracking().SingleAsync(ct);
        var coins = await db.Set<CmcCoinRow>().AsNoTracking().OrderBy(row => row.Slug)
            .Select(row => new { row.Slug, HasPlatform = row.PlatformJson != null, row.FetchedAt, row.NextAt }).ToArrayAsync(ct);
        var requests = await db.Set<CmcRequestRow>().AsNoTracking().OrderByDescending(row => row.StartedAt)
            .ThenByDescending(row => row.Id).Take(100).ToArrayAsync(ct);
        return new(now, Future(gate.NextAt, now),
            coins.Select(row => new CmcCoinStatus(row.Slug, row.HasPlatform, At(row.FetchedAt), Future(row.NextAt, now))).ToArray(),
            requests.Select(row => new CmcRequestStatus(row.Id, row.Slug, row.Kind,
                row.Outcome == "running" && gate.ActiveId == row.Id && gate.NextAt <= Millis(now) ? "interrupted" : row.Outcome,
                row.HttpStatus, DateTimeOffset.FromUnixTimeMilliseconds(row.StartedAt), At(row.FinishedAt))).ToArray());
    }

    private async Task<(CmcCoinRow? Coin, string? Id, DateTimeOffset? RetryAt)> ReserveAsync(string slug, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // The single gate row serializes decisions across API instances. No DB transaction spans HTTP.
        await db.Database.ExecuteSqlAsync($"UPDATE cmc_gate SET next_at = next_at WHERE id = 1", ct);
        var gate = await db.Set<CmcGateRow>().AsNoTracking().SingleAsync(ct);
        var coin = await db.Set<CmcCoinRow>().AsNoTracking().SingleOrDefaultAsync(row => row.Slug == slug, ct);
        var now = clock.GetUtcNow();
        if (coin?.FetchedAt is { } fetched && Millis(now) - fetched < (long)CoinGap.TotalMilliseconds)
            return (coin, null, null);
        var nextAt = Future(Math.Max(coin?.NextAt ?? 0, gate.NextAt), now);
        if (nextAt is not null) return (coin, null, nextAt);

        string id = Guid.NewGuid().ToString("N");
        if (gate.ActiveId is { } interrupted)
            await db.Database.ExecuteSqlAsync($"UPDATE cmc_requests SET outcome = 'interrupted' WHERE id = {interrupted}", ct);
        await db.Database.ExecuteSqlAsync($"UPDATE cmc_gate SET next_at = {Millis(now + ReservationWindow)}, active_id = {id} WHERE id = 1", ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO cmc_coins (slug, next_at) VALUES ({slug}, {Millis(now + CoinGap)})
            ON CONFLICT (slug) DO UPDATE SET next_at = excluded.next_at
            """, ct);
        if (coin?.CryptoId is { } cryptoId)
            await SetCoinCooldown(cryptoId, Millis(now + CoinGap), ct);
        string kind = coin?.PlatformJson is null ? "platform" : "chart";
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO cmc_requests (id, slug, kind, outcome, started_at) VALUES ({id}, {slug}, {kind}, 'running', {Millis(now)})
            """, ct);
        await db.Database.ExecuteSqlAsync($"""
            DELETE FROM cmc_requests WHERE id NOT IN (SELECT id FROM cmc_requests ORDER BY started_at DESC, id DESC LIMIT 100)
            """, ct);
        await tx.CommitAsync(ct);
        return (coin, id, null);
    }

    private async Task<DateTimeOffset?> FinishAsync(string id, string slug, int? cryptoId, CoinMarketCapPlatform? platform, string? chart,
        DateTimeOffset when, DateTimeOffset globalNext, DateTimeOffset? coinNext, string outcome, int? status)
    {
        // A disconnected caller must not prevent recording the attempt or its cooldown.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        int updated = await db.Database.ExecuteSqlAsync($"""
            UPDATE cmc_gate SET next_at = {Millis(globalNext)}, active_id = NULL WHERE id = 1 AND active_id = {id}
            """, ct);
        if (updated != 1) throw new AppFault("cmc_reservation_expired", "Резерв запроса CoinMarketCap истёк.", 503);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE cmc_requests SET outcome = {outcome}, http_status = {status}, finished_at = {Millis(when)} WHERE id = {id}
            """, ct);
        if (platform is not null)
        {
            var aliasNext = await db.Set<CmcCoinRow>().Where(row => row.CryptoId == platform.CryptoId)
                .MaxAsync(row => (long?)row.NextAt, ct);
            coinNext = At(aliasNext);
        }
        long nextAt = coinNext is { } next ? Millis(next) : 0;
        if (cryptoId is { } known) await SetCoinCooldown(known, nextAt, ct);
        string? platformJson = platform?.Info.GetRawText();
        int? mappedId = platform?.CryptoId;
        long? fetched = chart is null ? null : Millis(when);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE cmc_coins SET next_at = {nextAt}, crypto_id = COALESCE({mappedId}, crypto_id),
                platform_json = COALESCE({platformJson}, platform_json), chart_json = COALESCE({chart}, chart_json),
                fetched_at = COALESCE({fetched}, fetched_at) WHERE slug = {slug}
            """, ct);
        await tx.CommitAsync(ct);
        return coinNext;
    }

    private Task<int> SetCoinCooldown(int cryptoId, long nextAt, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"UPDATE cmc_coins SET next_at = {nextAt} WHERE crypto_id = {cryptoId}", ct);

    private static CmcChartResult Unavailable(CmcCoinRow? coin, DateTimeOffset next, string state, int status) =>
        coin?.ChartJson is { } json ? new("stale", json, At(coin.FetchedAt), next, 200) : new(state, null, null, next, status);

    private static long Millis(DateTimeOffset time) => time.ToUnixTimeMilliseconds();
    private static DateTimeOffset? At(long? time) => time is { } value ? DateTimeOffset.FromUnixTimeMilliseconds(value) : null;
    private static DateTimeOffset? Future(long time, DateTimeOffset now) => time > Millis(now) ? At(time) : null;
}

internal sealed record CmcGateRow(int Id, long NextAt, string? ActiveId);
internal sealed record CmcCoinRow(string Slug, long NextAt, int? CryptoId, string? PlatformJson, string? ChartJson, long? FetchedAt);
internal sealed record CmcRequestRow(string Id, string Slug, string Kind, string Outcome, int? HttpStatus, long StartedAt, long? FinishedAt);
