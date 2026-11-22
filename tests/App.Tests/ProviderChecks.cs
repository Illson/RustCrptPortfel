using System.Net;
using System.Text.Json;
using Portfel.Application;
using Portfel.Domain;
using Portfel.Infrastructure;
using static Checks;

internal static class ProviderChecks
{
    internal const string Article = "https://news.example.com/releases/asset-upgrade";

    internal static async Task RunAsync(DateTimeOffset now)
    {
        var (demo, _) = DemoPortfolio.Analyze(DemoPortfolio.Sync(Workspace.Empty, now), "5426", now);
        var seed = demo with { Demo = false, Research = [], Evidence = [], PortfolioContext = new("current", now, ExactNumber.From(987654321), null, null),
            InvestmentProfile = "PRIVATE-SYNTHETIC-PROFILE",
            Blocks = demo.Blocks.Select(item => item with { EvidenceIds = [] }).ToArray() };
        var clock = new FixedClock(now);
        var fullCaps = new AiCapabilities(true, true, true, true, false, "unknown", now, Model: "test");
        const string openAiSearch = """
            {"status":"completed","id":"provider-request","usage":{"input_tokens":12,"output_tokens":24},"output":[
              {"type":"web_search_call","id":"search_1","status":"completed","action":{"type":"search","query":"Solana release","sources":[
                {"type":"url","url":"https://news.example.com/releases/asset-upgrade","title":"Обновление"},
                {"type":"url","url":"https://news.example.com/releases/asset-upgrade","title":"Дубликат"}
              ]}}
            ]}
            """;
        using var researchHttp = new HttpClient(new MockHttp(async (request, ct) =>
        {
            string json = await request.Content!.ReadAsStringAsync(ct);
            Require(!json.Contains("987654321", StringComparison.Ordinal) && !json.Contains("PRIVATE-SYNTHETIC", StringComparison.Ordinal)
                && !json.Contains("portfolio", StringComparison.OrdinalIgnoreCase), "Research does not disclose holdings or profile");
            Require(request.RequestUri!.Host == "api.openai.com" && json.Contains("web_search", StringComparison.Ordinal), "OpenAI request attaches actual tool");
            return MockHttp.Reply(openAiSearch);
        }));
        var researcher = new AiHttpProvider(researchHttp, AiTransport.OpenAi, "synthetic-key", fullCaps);
        var (snapshot, research) = await AiAnalysis.ResearchAsync(researcher, "test", seed, clock, default);
        Require(snapshot is not null && research.Status == "completed" && snapshot.Evidence.Length == 1 && snapshot.Research.Single().EvidenceIds.Length == 1,
            "Research registers tool result and deduplicates sources");
        Require(snapshot!.SnapshotHash != (snapshot with { Evidence = [] }).SnapshotHash &&
            snapshot.SnapshotHash != (snapshot with { InvestmentProfile = null }).SnapshotHash, "Research and permitted private context covered by hash");
        var clean = Result(snapshot);
        var brokenLink = clean with { Facts = [new("unknown", "Нужна проверка.", ["invented"], "reported", [])] };
        foreach (var transport in new[] { AiTransport.OpenAi, AiTransport.Anthropic, AiTransport.Compatible })
        {
            var cases = new (string? Data, string? Failure, string Mode)[]
            {
                (JsonSerializer.Serialize(clean, PortfelJson.Options), null, "completed"),
                (JsonSerializer.Serialize(brokenLink, PortfelJson.Options), "invalid_evidence", "completed"),
                ("{\"schema_version\":", "invalid_output", "completed"),
                (null, "incomplete_response", "incomplete"),
                (null, "permission_denied", "refusal"),
            };
            foreach (var (data, failure, mode) in cases)
            {
                bool native = transport != AiTransport.Compatible;
                using var client = new HttpClient(new MockHttp(async (request, ct) =>
                {
                    string body = await request.Content!.ReadAsStringAsync(ct);
                    Require(!body.Contains("\"tools\"", StringComparison.Ordinal), "Synthesis cannot perform tool calls");
                    Require(body.Contains("987654321", StringComparison.Ordinal), "Synthesis gets allowed portfolio context");
                    if (!native) Require(!body.Contains("response_format", StringComparison.Ordinal) && body.Contains("JSON Schema", StringComparison.Ordinal),
                        "Explicit JSON fallback doesn't send unsupported native schema option");
                    return MockHttp.Reply(ProviderEnvelope(transport, mode, data));
                }));
                var provider = new AiHttpProvider(client, transport, "synthetic-key", fullCaps with { StructuredOutput = native, JsonOutput = !native },
                    transport == AiTransport.Compatible ? new Uri("https://vendor.example.com/v1/chat/completions") : null);
                var (result, receipt) = await AiAnalysis.SynthesizeAsync(provider, "test", snapshot, default);
                Require(receipt.Failure == failure && (result is not null) == (failure is null), $"{transport} {mode} expected {failure}: {receipt.Failure}");
            }
        }

        using (var client = new HttpClient(new MockHttp((_, _) => Task.FromResult(MockHttp.Reply(openAiSearch.Replace("\"search\"", "\"open_page\""))))))
        {
            var provider = new AiHttpProvider(client, AiTransport.OpenAi, "synthetic-key", fullCaps);
            var (unverified, receipt) = await AiAnalysis.ResearchAsync(provider, "test", seed, clock, default);
            Require(unverified is null && receipt.Status != "completed", "Opening a page without actual search doesn't count as research");
        }
        using (var client = new HttpClient(new MockHttp((_, _) => Task.FromResult(MockHttp.Reply(ProviderEnvelope(AiTransport.OpenAi, "completed", "Проверил новости"))))))
        {
            var provider = new AiHttpProvider(client, AiTransport.OpenAi, "synthetic-key", fullCaps);
            var (unverified, receipt) = await AiAnalysis.ResearchAsync(provider, "test", seed, clock, default);
            Require(unverified is null && receipt.Status == "limited_research" && receipt.Text is null, "Model assertion is not a search receipt");
        }

        using (var client = new HttpClient(new MockHttp((_, _) => Task.FromException<HttpResponseMessage>(new TaskCanceledException("synthetic timeout")))))
        {
            var provider = new AiHttpProvider(client, AiTransport.OpenAi, "synthetic-key", fullCaps);
            var receipt = await provider.RunAsync(new("test", "synthetic", false, null, 30), default);
            Require(receipt.Status == "outcome_unknown" && receipt.Text is null, "Timeout after dispatch is not a successful response");
        }

        string payload = JsonSerializer.Serialize(clean, PortfelJson.Options);
        string receiptJson = ProviderEnvelope(AiTransport.OpenAi, "completed", payload);
        string prefix = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n";
        foreach (var (tail, expected) in new[]
        {
            ($"data: {{\"type\":\"response.completed\",\"response\":{receiptJson}}}\n\n", (string?)null),
            ("data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"subscription_sharing_usage_limit_exceeded\"}}}\n\n", "quota_exhausted"),
            ("", "incomplete_response"),
        })
        {
            using var client = new HttpClient(new MockHttp(async (request, ct) =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var args = body.RootElement;
                Require(request.RequestUri!.AbsoluteUri == "https://api.openai.com/v1/responses" &&
                    args.GetProperty("stream").GetBoolean() && !args.GetProperty("store").GetBoolean() &&
                    args.GetProperty("input").ValueKind == JsonValueKind.Array && !args.TryGetProperty("max_output_tokens", out _) && !args.TryGetProperty("background", out _),
                    "ChatGPT plan uses only the documented streaming interface");
                return MockHttp.Reply(prefix + tail, media: "text/event-stream");
            }));
            var provider = new AiHttpProvider(client, AiTransport.ChatGptPlan, "synthetic-token", fullCaps with { WebSearch = false });
            var (result, receipt) = await AiAnalysis.SynthesizeAsync(provider, "test", snapshot, default);
            Require(receipt.Failure == expected && (result is not null) == (expected is null), $"Plan terminal event ({expected}): {receipt.Failure}");
            var (blocked, noResearch) = await AiAnalysis.ResearchAsync(provider, "test", seed, clock, default);
            Require(blocked is null && noResearch.Failure == "blocked_capability", "No hidden plan fallback. Research uses a separately selected provider.");
        }
        await MarketSourcesAsync(clock);
        Console.WriteLine("PASS provider HTTP contracts, research privacy, shared validation, SSE completion/limit/EOF, and public market contracts");
    }

    internal static AnalysisResultV1 Result(AnalysisInputV1 snapshot) =>
        Result(snapshot.AssetId, snapshot.SnapshotHash, snapshot.Blocks);

    internal static AnalysisResultV1 Result(string assetId, string hash, AnalysisCoverage[] blocks) =>
        new(AnalysisValidation.SchemaVersion, assetId, hash, "Данные об активе", "Покрытие ограничено.", "watch",
            blocks.Select(block => new AnalysisSection(block.Block, block.DataStatus, "undetermined", "Нужно уточнить данные.", [])).ToArray(),
            [], [], [], "Персональные действия не требуются.", [], ["Недостаточно сведений."], "После обновления данных.");

    private static string ProviderEnvelope(AiTransport kind, string mode, string? data) => JsonSerializer.Serialize(kind switch
    {
        AiTransport.OpenAi => new
        {
            status = mode == "incomplete" ? "incomplete" : "completed",
            output = new[] { new { type = "message", content = new[] { new { type = mode == "refusal" ? "refusal" : "output_text", text = data } } } },
        },
        AiTransport.Anthropic => (object)new
        {
            stop_reason = mode switch { "incomplete" => "max_tokens", "refusal" => "refusal", _ => "end_turn" },
            content = new[] { new { type = "text", text = data } },
        },
        _ => new { choices = new[] { new { finish_reason = mode == "incomplete" ? "length" : "stop", message = new { content = data, refusal = mode == "refusal" ? "denied" : null } } } },
    });

    private static async Task MarketSourcesAsync(TimeProvider clock)
    {
        var demo = DemoPortfolio.Sync(Workspace.Empty, clock.GetUtcNow());
        var sol = demo.Assets.Single(item => item.Asset.Id == "5426");
        const string cmcFixture = """
            {"status":{"error_code":"0"},"data":[{"id":5426,"last_updated":"2026-11-22T07:00:00Z","total_supply":11111122222233333344444455.123456789,
              "infinite_supply":false,"max_supply":null,"circulating_supply":1100,"quote":[{"symbol":"EUR","price":100},
              {"symbol":"USD","price":0.123456789012345678901234567890123,"last_updated":"2026-11-22T07:59:00Z","volume_24h_reported":12.1}]}]}
            """;
        using var cmc = new HttpClient(new MockHttp((request, _) =>
        {
            Require(request.RequestUri!.Query.Contains("id=5426") && request.RequestUri.AbsolutePath.StartsWith("/public-api/")
                && !request.Headers.Contains("X-CMC_PRO_API_KEY"), "Public quotes are separate from identity and private holdings");
            return Task.FromResult(MockHttp.Reply(cmcFixture));
        }));
        var quotes = await new CmcPublicQuotes(cmc, clock).ReadAsync(sol.Asset, default);
        Require(quotes.Status == "ok" && quotes.MaximumSupplyKind == "unknown" && quotes.Observations.Single(item => item.MetricKey == "price").ValueDecimal
            == ExactNumber.Parse("0.123456789012345678901234567890123"), "CMC uses exact stable-ID USD quote");
        var merged = PublicSourceRevisions.Merge(sol, quotes, "cmc_market");
        var correction = quotes with { Observations = quotes.Observations.Select(item => item.MetricKey == "price"
            ? item with { ValueDecimal = ExactNumber.From(32) } : item).ToArray() };
        merged = PublicSourceRevisions.Merge(merged, correction, "cmc_market");
        Require(merged.Observations.Where(item => item.SourceId == "cmc_market" && item.MetricKey == "price").Select(item => item.Revision)
            .SequenceEqual([1, 2]), "Observation keeps corrected and original versions");

        using var llama = new HttpClient(new MockHttp((_, _) => Task.FromResult(MockHttp.Reply("""
            {"id":"chain#solana","slug":"solana","protocolType":"chain","cmcId":"5426","gecko_id":"solana","totalDataChart":[[1795219200,1.23],[1795305600,null]]}
            """))));
        var flows = new DefiLlamaFlows(llama, clock);
        var chain = await flows.ReadAsync(sol, new("sol", "solana", MetricScope.Chain), "chain_revenue", default);
        Require(chain.Status == "ok" && chain.Observations.Single().Scope == MetricScope.Chain, "DefiLlama chain is separated from apps and token holders");
        Require((await flows.ReadAsync(sol, new("sol", "solana", MetricScope.ProtocolGlobal), "revenue", default)).Status == "conflict",
            "Chain revenue cannot be interpreted as protocol global");

        const string ticker = """
            {"base":"SOL","target":"USDT","market":{"identifier":"fixture-cex"},"last_traded_at":"2026-11-22T07:55:00Z",
             "coin_id":"solana","target_coin_id":"tether","converted_volume":{"usd":22.25},"is_anomaly":false,"is_stale":false}
            """;
        int requestCount = 0;
        using var cg = new HttpClient(new MockHttp((request, _) =>
        {
            Require(request.Headers.Contains("x-cg-pro-api-key") && !request.RequestUri!.Query.Contains("synthetic"), "CoinGecko credentials only in header");
            requestCount++;
            return Task.FromResult(requestCount == 1
                ? MockHttp.Reply("{\"tickers\":[" + string.Join(",", Enumerable.Repeat(ticker, 100)) + "]}")
                : MockHttp.Reply("{}", HttpStatusCode.TooManyRequests));
        }));
        var cgBatch = await new CoinGeckoSpotMarkets(cg, clock, "synthetic-key", new HashSet<string> { "fixture-cex" }).ReadAsync(sol.Asset, default);
        Require(cgBatch.Status == "rate_limited" && cgBatch.Markets.Length == 0
            && ReferenceEquals(sol, PublicSourceRevisions.Merge(sol, cgBatch, "coingecko")), "Failed second page preserves market observations");
    }
}
