using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Portfel.Application;
using Portfel.Domain;
using Portfel.Infrastructure;
using static Checks;

internal static class ChatGptLabChecks
{
    internal static async Task RunAsync(DateTimeOffset now)
    {
        await OAuthAsync();
        await OpinionAsync(now);
        await VerdictsAsync(now);
        await BatchAsync(now);
        Console.WriteLine("PASS ChatGPT lab OAuth, search privacy, source validation, decisions, and countercheck");
    }

    private static async Task OAuthAsync()
    {
        const string id = "oaiapp_synthetic";
        const string callback = "http://127.0.0.1:5080/api/v1/chatgpt-lab/callback";
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "fixture-key" };
        var parameters = rsa.ExportParameters(false);
        string jwks = JsonSerializer.Serialize(new
        {
            keys = new[] { new { kty = "RSA", kid = key.KeyId, n = Base64UrlEncoder.Encode(parameters.Modulus), e = Base64UrlEncoder.Encode(parameters.Exponent) } },
        });
        var handler = new JsonWebTokenHandler();
        string Sign(string audience, DateTime expires) => handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = ChatGptPlanClient.Issuer, Audience = audience,
            IssuedAt = expires.AddMinutes(-6), NotBefore = expires.AddMinutes(-6), Expires = expires,
            Claims = new Dictionary<string, object> { ["sub"] = "synthetic-owner", ["nonce"] = "synthetic-nonce" },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });
        string jwt = Sign(id, DateTime.UtcNow.AddMinutes(5));
        var calls = new List<string>();
        using var http = new HttpClient(new MockHttp(async (request, ct) =>
        {
            var uri = request.RequestUri!;
            Require(uri.Scheme == "https" && string.IsNullOrEmpty(uri.Query), "ChatGPT credentials should not appear in request URLs");
            calls.Add(uri.AbsoluteUri);
            if (uri.AbsolutePath == "/.well-known/jwks.json") return MockHttp.Reply(jwks);
            if (uri.AbsolutePath == "/.well-known/openid-configuration")
                return MockHttp.Reply("""{"issuer":"https://auth.openai.com","revocation_endpoint":"https://auth.openai.com/api/accounts/oauth/revoke"}""");
            if (uri.AbsolutePath == "/v1/models")
            {
                Require(request.Headers.Authorization?.Parameter == "synthetic-access", "Account model catalog has the selected bearer token");
                return MockHttp.Reply("""{"models":[{"slug":"fixture-web","display_name":"Web Fixture","visibility":"list"},{"slug":"hidden","visibility":"hidden"}]}""");
            }
            Require(uri.Host == "auth.openai.com", "Protected grants only go to the auth issuer");
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct));
            Require(form["client_id"] == id && !form.ContainsKey("client_secret"), "Only the issued public OAuth client is used");
            if (uri.AbsolutePath.EndsWith("/revoke", StringComparison.Ordinal))
            {
                Require(form["token"] == "synthetic-rotated" && form["token_type_hint"] == "refresh_token", "Revoke the newest refresh token");
                return MockHttp.Reply("");
            }
            Require(form["resource"] == ChatGptPlanClient.Resource, "ChatGPT plan resource is scoped to OpenAI Responses");
            if (form["grant_type"] == "authorization_code")
            {
                Require(form["redirect_uri"] == callback && form["code_verifier"] == "synthetic-verifier", "OAuth exchange binds callback and PKCE");
                return MockHttp.Reply(JsonSerializer.Serialize(new
                {
                    access_token = "synthetic-access", refresh_token = "synthetic-refresh", id_token = jwt, token_type = "Bearer",
                    expires_in = 3600, scope = ChatGptPlanClient.RequestedScopes,
                }));
            }
            Require(form["grant_type"] == "refresh_token" && form["refresh_token"] == "synthetic-refresh" && !form.ContainsKey("scope"),
                "Refresh carries the issued client, rotating credential and existing grant");
            return MockHttp.Reply("""{"access_token":"synthetic-access","refresh_token":"synthetic-rotated","token_type":"Bearer","expires_in":3600}""");
        }));
        var client = new ChatGptPlanClient(http, TimeProvider.System);
        var tokens = await client.ExchangeAsync(id, "synthetic-code", "synthetic-verifier", callback, default);
        Require(tokens.PlanAllowed && !((tokens with { Scopes = ["openid"] }).PlanAllowed), "Identity is not permission for plan use");
        Require((await client.ValidateIdentityAsync(jwt, id, "synthetic-nonce", default)).Subject == "synthetic-owner", "Verify the signed identity");
        foreach (var invalid in new[] { (jwt, "wrong-nonce"), (Sign("wrong-audience", DateTime.UtcNow.AddMinutes(5)), "synthetic-nonce"),
            (Sign(id, DateTime.UtcNow.AddMinutes(-5)), "synthetic-nonce") })
        {
            try
            {
                _ = await client.ValidateIdentityAsync(invalid.Item1, id, invalid.Item2, default);
                throw new InvalidOperationException("Invalid OAuth identity was accepted");
            }
            catch (AppFault fault) when (fault.Code == "chatgpt_identity_invalid") { }
        }
        var fresh = await client.RefreshAsync(id, tokens, default);
        Require(fresh.Refresh == "synthetic-rotated" && fresh.PlanAllowed, "Rotating refresh retains permissions if scope is not reissued");
        Require((await client.ModelsAsync(fresh.Access, default)).Single().Id == "fixture-web", "Only list-visible account models are exposed");
        Require(await client.RevokeAsync(id, fresh.Refresh!, default) && calls.All(url => !url.Contains("synthetic", StringComparison.Ordinal)),
            "Revocation and credential confinement");
    }

    private static async Task OpinionAsync(DateTimeOffset now)
    {
        const string source = "https://issuer.example.org/updates";
        const string falseSource = "https://different.example.org/unseen";
        const string trackedSource = source + "?utm_source=chatgpt.com#latest";
        const string spotSource = source + "?type=spot";
        const string dashDocs = "https://docs.dash.org/fixture/page.html";
        var post = new OpinionPost("Обзор актива", "Краткий обзор публичных сведений.",
            [
                Gap("economics"), Gap("supply"), Gap("markets"),
                new("project", "reported", "Проект сообщил о подготовке обновления.", [source], DateOnly.FromDateTime(now.UtcDateTime), "mixed", "none"),
            ], "Срок выпуска может измениться.", "Следить за объявлениями проекта.", Verdict("watch", "low", "project"));
        foreach (var mode in new[] { "ok", "native_citation", "wrapped", "broken_format", "wrong_source", "meaningful_query",
            "registered_query", "wrong_query_value", "opened_query", "stream_opened_query", "dash_highlight", "partial_sources",
            "tracking_trk", "unknown_trk", "no_search", "truncated", "preview_limit", "quota", "large_stream", "oversize",
            "invalid_event", "invalid_completion", "incomplete_limit", "incomplete_filter", "incomplete_without_reason",
            "compact_items", "compact_text", "compact_delta", "completed_without_text", "conflicting_response_id",
            "alternate_content_type", "wrong_content_type", "nonstream_preview", "schema_error" })
        {
            string selectedSource = mode switch
            {
                "wrong_source" => falseSource, "native_citation" => trackedSource,
                "registered_query" or "wrong_query_value" => spotSource + "&utm_medium=test",
                "opened_query" or "stream_opened_query" => spotSource,
                "dash_highlight" => dashDocs + "?highlight=reward#fixture",
                "tracking_trk" => source + "?trk=article-ssr-frontend-pulse_little-text-block",
                "unknown_trk" => source + "?trk=other",
                "meaningful_query" => source + "?page=2", _ => source,
            };
            string model = mode is "incomplete_limit" or "tracking_trk" ? "gpt-6.1-terra"
                : mode == "incomplete_filter" ? "gpt-6.2-sol" : "fixture-web";
            var draft = post with { Sections = [.. post.Sections[..3], post.Sections[3] with { SourceUrls = [selectedSource] }] };
            if (mode == "partial_sources")
                draft = draft with
                {
                    Summary = "Недоказанный итог из рынка.", Opinion = "Непроверенное мнение.", Watch = "Непроверенное условие.",
                    Sections = [.. draft.Sections[..2], new("markets", "reported", "Непроверенное утверждение о торгах.",
                        [source, falseSource], DateOnly.FromDateTime(now.UtcDateTime), "critical", "trading_unavailable"), draft.Sections[3]],
                    Verdict = Verdict("exit_review", "high", "markets", "project"),
                };
            string json = JsonSerializer.Serialize(draft, PortfelJson.Options);
            string payload = mode switch
            {
                "wrapped" => $"```json\r\n{json}\r\n```",
                "broken_format" => "Мнение о монете без JSON.",
                _ => json,
            };
            object[] annotations = mode is "native_citation" or "no_search"
                ? [new { type = "url_citation", url = trackedSource, title = "Цитата веб-поиска" }] : [];
            var output = new List<object> { new { type = "message", content = new[] { new { type = "output_text", text = payload, annotations } } } };
            string toolUrl = mode switch
            {
                "registered_query" => spotSource, "wrong_query_value" => source + "?type=futures",
                "dash_highlight" => dashDocs, _ => source,
            };
            if (mode != "no_search")
                output.Insert(0, new { type = "web_search_call", status = "completed", id = "search-fixture",
                    action = new { type = "search", sources = mode is "native_citation" or "compact_text" or "opened_query" or "stream_opened_query"
                        ? [] : new[] { new { url = toolUrl, title = "Источник проекта" } } } });
            var opened = new { type = "web_search_call", status = "completed", id = "opened-fixture", action = new { type = "open_page", url = spotSource } };
            if (mode == "opened_query") output.Insert(1, opened);
            string complete = $"data: {JsonSerializer.Serialize(new { type = "response.completed", response = new { status = "completed", output } })}\n\n";
            string searchItem = Frame("response.output_item.done", new { output_index = 0, item = output[0] });
            string compact = Frame("response.completed", new { response = new { id = "resp_fixture" } });
            string delta = Frame("response.output_text.delta", new { delta = payload });
            string sse = mode switch
            {
                "truncated" => "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n",
                "preview_limit" => $"event: response.output_text.delta\ndata: {{\"delta\":\"{new string('x', 12_000)}\"}}\n\n" +
                    "event: response.output_text.delta\ndata: {\"delta\":\"continuation\"}\n\n",
                "quota" => "data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"subscription_sharing_usage_limit_exceeded\"}}}\n\n",
                "large_stream" => "event: response.output_item.done\r\ndata: {\"type\":\"response.output_item.done\",\"item\":{\"sources\":\"" +
                    new string('x', 3 * 1024 * 1024) + "\"}}\r\n\r\n" + complete,
                "oversize" => $"data: {new string('x', 9 * 1024 * 1024)}\n\n" + complete,
                "invalid_event" => "event: response.completed\ndata: {bad}\n\n" + complete,
                "invalid_completion" => "event: response.completed\ndata: {}\n\n",
                "incomplete_limit" => "event: response.output_text.delta\ndata: {\"delta\":\"prefix\"}\n\n" +
                    $"data: {JsonSerializer.Serialize(new { type = "response.incomplete", response = new
                    {
                        status = "incomplete", incomplete_details = new { reason = "max_output_tokens" }, output,
                    } })}\n\n",
                "incomplete_filter" => "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"content_filter\"}}}\n\n",
                "incomplete_without_reason" => searchItem + delta +
                    Frame("response.incomplete", new { response = new { status = "incomplete" } }),
                "compact_items" => searchItem + Frame("response.output_item.done", new
                {
                    output_index = 1,
                    item = new { type = "message", status = "completed", content = new[] { new { type = "output_text", text = payload } } },
                }) + compact,
                "compact_text" => searchItem +
                    Frame("response.output_text.annotation.added", new { annotation = new { type = "url_citation", url = source, title = "Цитата" } }) +
                    Frame("response.output_text.done", new { output_index = 1, content_index = 0, text = payload }) + compact,
                "compact_delta" => searchItem + delta + compact,
                "stream_opened_query" => searchItem + Frame("response.output_item.done", new { output_index = 1, item = opened }) + delta + compact,
                "completed_without_text" => searchItem + compact,
                "conflicting_response_id" => Frame("response.created", new { response = new { id = "resp_other" } }) +
                    searchItem + delta + compact,
                "alternate_content_type" => $"event: response.completed\r\ndata: {JsonSerializer.Serialize(new { response = new { status = "completed", output } })}\r\n\r\n",
                _ => complete,
            };
            using var http = new HttpClient(new MockHttp(async (request, ct) =>
            {
                Require(request.RequestUri!.AbsoluteUri == ChatGptPlanClient.Resource + "/responses" &&
                    request.Headers.Authorization?.Parameter == "synthetic-subscription", "No private ChatGPT endpoint or API-key fallback");
                using var data = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var root = data.RootElement;
                using var context = JsonDocument.Parse(root.GetProperty("input")[0].GetProperty("content").GetString()!);
                Require(context.RootElement.GetProperty("coinGeckoId").GetString() == "solana" && context.RootElement.EnumerateObject().Count() == 2,
                    "No private balance or portfolio is sent by the laboratory");
                var stamp = context.RootElement.GetProperty("asOfUtc");
                Require(stamp.GetString()!.EndsWith('Z') && stamp.GetDateTimeOffset() == now,
                    "Give the model an explicit UTC cutoff rather than a calendar date from an unknown time zone");
                Require(!root.GetProperty("store").GetBoolean() && root.GetProperty("stream").GetBoolean()
                    && root.GetProperty("tools")[0].GetProperty("type").GetString() == "web_search"
                    && root.GetProperty("tools")[0].GetProperty("search_context_size").GetString() == "high"
                    && !root.TryGetProperty("max_output_tokens", out _), "Bound search context without adding unsupported plan fields");
                bool hasReasoning = root.TryGetProperty("reasoning", out var reasoning);
                Require(model == "fixture-web" ? !hasReasoning : hasReasoning && reasoning.GetProperty("effort").GetString() == "medium",
                    "Raise reasoning effort only for known models that support it");
                var format = root.GetProperty("text").GetProperty("format");
                Require(format.GetProperty("strict").GetBoolean() && format.GetProperty("name").GetString() == "coin_opinion_v4" &&
                    format.GetProperty("schema").GetProperty("properties").TryGetProperty("verdict", out _), "Request the dedicated strict verdict schema");
                var response = mode switch
                {
                    "wrong_content_type" => MockHttp.Reply("<html>not SSE</html>", media: "text/html"),
                    "nonstream_preview" => MockHttp.Reply(JsonSerializer.Serialize(new { status = "completed", output })),
                    "schema_error" => MockHttp.Reply("""{"error":{"code":"invalid_json_schema"}}""", HttpStatusCode.BadRequest),
                    "alternate_content_type" => MockHttp.Reply(sse, media: "text/plain"),
                    _ => MockHttp.Reply(sse, media: "text/event-stream"),
                };
                response.Headers.Add("x-request-id", "req_fixture");
                return response;
            }));
            var provider = new AiHttpProvider(http, AiTransport.ChatGptPlan, "synthetic-subscription",
                new(true, true, true, true, false, "unknown", now, Model: model));
            var result = await CoinOpinion.GenerateAsync(provider, model, "solana", now.ToOffset(TimeSpan.FromHours(3)), default);
            if (mode is "opened_query" or "stream_opened_query")
            {
                var receipt = await provider.RunAsync(new(model, JsonSerializer.Serialize(new { coinGeckoId = "solana", asOfUtc = now.UtcDateTime }),
                    true, JsonSerializer.SerializeToElement(new { properties = new { verdict = new { } } }), 4000,
                    OutputSchemaName: "coin_opinion_v4", CaptureOutput: true, WebSearchContextSize: "high"), default);
                Require(receipt.OpenedWebUrls is [spotSource], "Carry only actual completed open/find tool URLs separately from search hits");
            }
            string? expected = mode switch
            {
                "no_search" => "limited_research", "wrong_source" or "meaningful_query" or "unknown_trk" or "wrong_query_value" => "unverified_post_sources",
                "broken_format" => "invalid_post_format", "truncated" or "preview_limit" => "outcome_unknown",
                "quota" => "quota_exhausted", "oversize" => "plan_stream_too_large",
                "invalid_event" or "invalid_completion" or "conflicting_response_id" => "plan_invalid_sse",
                "wrong_content_type" or "nonstream_preview" => "plan_unexpected_content_type", "schema_error" => "provider_rejected_schema",
                "incomplete_limit" => "incomplete_output_limit", "incomplete_filter" => "incomplete_content_filter",
                "incomplete_without_reason" => "incomplete_response", "completed_without_text" => "plan_completed_without_text",
                _ => null,
            };
            string acceptedUrl = mode is "registered_query" or "opened_query" or "stream_opened_query" ? spotSource : toolUrl;
            Require(expected == result.Error && (expected is null ? result.Post is { Sections.Length: 4 } &&
                result.Post.Sections[3].SourceUrls.Single() == acceptedUrl && result.Sources.Single().Url == acceptedUrl
                : result.Post is null && result.Sources.Length == 0), $"Publish only complete referenced opinions, mode={mode}");
            if (mode == "native_citation") Require(result.Sources.Single().Title == "Цитата веб-поиска", "Use the native citation title");
            if (expected is not null)
                Require(result.RequestId == "req_fixture" && result.UpstreamStatus == (mode == "schema_error" ? 400 : 200),
                    "Failure carries safe OpenAI diagnostics without response bodies");
            if (mode is "ok" or "broken_format" or "wrong_source")
                Require(result.Output == new AiOutputPreview(payload, false, false), "Keep the model text even when the post is rejected");
            if (mode == "truncated") Require(result.Output == new AiOutputPreview("partial", true, false), "Show text received before an interrupted stream");
            if (mode == "preview_limit") Require(result.Output is { Text.Length: 12_000, Partial: true, Truncated: true }, "Limit how much text is retained");
            if (mode == "nonstream_preview") Require(result.Output == new AiOutputPreview(payload, true, false), "A non-stream body is previewed but never published");
            if (mode is "incomplete_limit" or "incomplete_without_reason")
                Require(result.Output == new AiOutputPreview(payload, true, false)
                    && result.Draft is { Sections.Length: 4 } && result.Draft.Sections.All(section => section.SourceUrls.Length == 0)
                    && result.TerminalState?.StartsWith("response.incomplete / incomplete", StringComparison.Ordinal) == true,
                    "Even fully formed JSON stays a local unlinked draft when the provider marks the response incomplete");
            else if (expected == "unverified_post_sources")
                Require(result.Draft is not null && result.Draft.Sections.All(section => section.SourceUrls.Length == 0) &&
                    result.UnverifiedBlocks is ["project"], "No verified blocks leaves an unlinked draft, not a published opinion");
            else Require(result.Draft is null, "Do not present malformed, filtered or published results as drafts");
            if (mode == "partial_sources")
            {
                Require(result.UnverifiedBlocks is ["markets"] && result.Post?.Sections[2] is
                    { Status: "insufficient", Signal: "unknown", RiskFlag: "none", SourceUrls.Length: 0 } &&
                    result.Post.Verdict.Classification == "insufficient" && result.Decision is null &&
                    result.Post.Sections[2].Text != draft.Sections[2].Text &&
                    result.Post.Opinion != draft.Opinion && result.Post.Watch != draft.Watch && result.Post.Summary != draft.Summary,
                    "A bad link suppresses the whole affected block and dependent verdict, without discarding other evidence");
            }
            if (mode is "wrong_content_type" or "schema_error") Require(result.Output is null, "No raw HTML or provider error bodies are included");
        }
    }

    private static async Task VerdictsAsync(DateTimeOffset now)
    {
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        const string projectUrl = "https://project.example.org/status";
        const string marketUrl = "https://exchange.example.com/notices";
        const string economyUrl = "https://research.example.net/valuation";
        var project = new OpinionSection("project", "reported", "Публикация о состоянии проекта.", [projectUrl], day.AddDays(-2), "critical", "confirmed_shutdown");
        var market = new OpinionSection("markets", "reported", "Биржа сообщила о состоянии торгов.", [marketUrl], day.AddDays(-3), "critical", "trading_unavailable");
        var economics = new OpinionSection("economics", "reported", "Компания опубликовала отчёт.", [economyUrl], day.AddDays(-28), "positive", "none");
        var thesis = new OpinionTokenThesis("direct", "Поток комиссий финансирует выкупы токена, но доходы цикличны.", [economyUrl]);
        string mode = "supported";
        int checks = 0;
        AiReceipt Countercheck(AiRequest request)
        {
            checks++;
            using var doc = JsonDocument.Parse(request.PublicOrPrivateInput);
            var input = doc.RootElement;
            var topics = input.GetProperty("topics").EnumerateArray().Select(item => item.GetProperty("topic").GetString()!).ToArray();
            bool add = input.GetProperty("candidate").GetString() == "add";
            Require(input.EnumerateObject().Select(property => property.Name).SequenceEqual(
                ["coin_gecko_id", "as_of_utc", "candidate", "thesis", "counterpoint", "topics"]) &&
                input.GetProperty("coin_gecko_id").GetString() == "fixture" && request.Research && request.CaptureOutput &&
                request.OutputSchemaName == "coin_opinion_check_v1" &&
                topics.ToHashSet().SetEquals(add ? ["token_thesis", "valuation"] : ["markets", "project"]),
                "The separate request only receives the two pivotal public claims and does not retest a hold");
            if (mode == "rate_limited")
                return new("fixture", null, "failed", "rate_limited", null, null, "chatgpt_plan", null, [], RetryAt: now.AddMinutes(17));
            string[] urls = [add ? economyUrl : projectUrl, marketUrl + "?type=spot"];
            if (mode == "same_publisher") urls[1] = urls[0].Replace("https://", "https://news.", StringComparison.Ordinal);
            var lines = topics.Select(topic => topic switch
            {
                "token_thesis" or "project" => new OpinionReviewCheck(topic, mode == "contradicted" ? "contradicted" : "supported",
                    "В первичном отчёте указан новый статус.", day.AddDays(-2), urls[0]),
                _ => new OpinionReviewCheck(topic, "supported", "На спотовой странице есть датированное наблюдение.",
                    mode == "stale" ? day.AddDays(-15) : day.AddDays(-3), urls[1] + (urls[1].Contains('?') ? "&" : "?") + "utm_source=fixture"),
            }).ToArray();
            return new AiReceipt("fixture", "review_fixture", "completed", null, null, null, "chatgpt_plan",
                JsonSerializer.Serialize(new { Checks = lines }, PortfelJson.Options),
                [new("web_search_call", "check_fixture", [], urls.Select(url => new AiSource(url, "Проверка")).ToArray())])
            {
                OpenedWebUrls = mode switch
                {
                    "query_mismatch" => [urls[0], marketUrl + "?type=futures"],
                    "search_only" => [],
                    _ => urls,
                },
            };
        }
        async Task<OpinionOutcome> Score(OpinionSection projectBlock, OpinionSection marketBlock, OpinionVerdict verdict)
        {
            var post = new OpinionPost("Сводка", "Аналитическое мнение.", [economics, Gap("supply"), marketBlock, projectBlock],
                "Сигнал требует проверки.", "Проверять состояние торгов.", verdict);
            var urls = post.Sections.SelectMany(section => section.SourceUrls).Distinct().Select(url => new AiSource(url, "Источник")).ToArray();
            return await CoinOpinion.GenerateAsync(new OpinionFixture(post, urls, Countercheck), "fixture-web", "fixture", now, default);
        }
        var proposed = Verdict("likely_inactive", "high", "project", "markets");
        var supported = await Score(project, market, proposed);
        Require(supported.Post?.Verdict is { Classification: "likely_inactive", Confidence: "medium" } && supported.Decision?.Action == "sell",
            "Two dated critical signals across publishers permit likely inactive; two blocks cannot be high confidence");
        foreach (var unsupported in new[]
        {
            await Score(project, Gap("markets"), proposed),
            await Score(project, market with { SourceUrls = ["https://mirror.example.org/markets"] }, proposed),
            await Score(project with { DataAsOf = day.AddDays(-91) }, market, proposed),
            await Score(project, market with { RiskFlag = "none" }, proposed),
            await Score(project with { Signal = "positive", RiskFlag = "none" }, Gap("markets"), Verdict("hold_case", "high", "project")),
        })
            Require(unsupported.Post?.Verdict is { Classification: "insufficient", Confidence: "low" } && unsupported.Decision?.Action == "no_signal",
                "Missing, same publisher, or stale support cannot imply hold or likely inactive");
        var activeProject = project with { Signal = "positive", RiskFlag = "none" };
        var activeMarket = market with { Signal = "positive", RiskFlag = "none" };
        var hold = Verdict("hold_case", "medium", "project", "markets", "economics") with { TokenThesis = thesis };
        var watch = hold with { Classification = "watch" };
        Require((await Score(activeProject, activeMarket, hold)).Decision is { Action: "hold", Revisit: not null } && checks == 1,
            "Current project and spot evidence with a positive signal warrants a reasoned hold case");
        Require((await Score(activeProject, activeMarket, watch)).Decision?.Action == "hold",
            "A watchlist assessment does not hide the conservative action when the position has recent support");
        Require((await Score(activeProject, activeMarket with { DataAsOf = day.AddDays(-31) }, hold)).Decision?.Action == "no_signal",
            "An old spot market check is not a hold signal");
        Require((await Score(activeProject, activeMarket, watch with { Confidence = "low" })).Decision?.Action == "no_signal",
            "Do not turn a low-confidence assessment into hold");
        foreach (var weak in new[] { null, thesis with { Mechanism = "none" }, thesis with { SourceUrls = [marketUrl] } })
            Require((await Score(activeProject, activeMarket, hold with { TokenThesis = weak })).Decision?.Action == "no_signal",
                "An active project alone is not a reason to hold its token");
        Require((await Score(project, Gap("markets"), Verdict("exit_review", "high", "project"))).Decision?.Action == "no_signal",
            "No sell signal from a single critical block");
        Require((await Score(project, market, Verdict("exit_review", "medium", "project", "markets"))).Decision?.Action == "sell",
            "Fresh independent adverse evidence supports sell");
        Require((await Score(project, market with { DataAsOf = day.AddDays(1) }, proposed)).Error == "invalid_post_format",
            "Future dated evidence is rejected");

        var addPost = new OpinionPost("Сводка", "Мнение.", [
            economics,
            new("supply", "reported", "График опубликован.", [projectUrl], day, "mixed", "none"), activeMarket, activeProject],
            "Аналитический сигнал.", "Проверить оценку.", Verdict("add_case", "medium", "economics", "markets", "project", "supply") with
            {
                PriceCase = new("Мультипликатор 8 против медианы 12 за сопоставимый период.", day.AddDays(-2), [marketUrl, economyUrl]),
                TokenThesis = thesis,
            });
        AiSource[] evidence = [new(marketUrl, "Рынок"), new(economyUrl, "Отчёт"), new(projectUrl, "Раскрытие")];
        Task<OpinionOutcome> CheckAdd(OpinionPost post) =>
            CoinOpinion.GenerateAsync(new OpinionFixture(post, evidence, Countercheck), "fixture-web", "fixture", now, default);
        Require((await CheckAdd(addPost)) is { Decision.Action: "add", Review.Status: "supported" },
            "Buying needs a fresh price comparison, favorable dated fundamentals and a completed countercheck");
        var priceCase = addPost.Verdict.PriceCase!;
        foreach (var rejected in new[]
        {
            addPost with { Verdict = addPost.Verdict with { PriceCase = null } },
            addPost with { Verdict = addPost.Verdict with { TokenThesis = null } },
            addPost with { Verdict = addPost.Verdict with { PriceCase = priceCase with { DataAsOf = day.AddDays(-15) } } },
            addPost with { Verdict = addPost.Verdict with { PriceCase = priceCase with { SourceUrls = [marketUrl, projectUrl] } } },
            addPost with { Verdict = addPost.Verdict with { PriceCase = priceCase with { SourceUrls = [marketUrl, economyUrl + "?other=3"] } } },
            addPost with { Verdict = addPost.Verdict with { Confidence = "low" } },
        })
            Require((await CheckAdd(rejected)).Decision?.Action == "no_signal", "Do not suggest buying without a fresh, sourced and credible price case");
        Require(checks == 3, "Only candidates eligible for add/sell trigger the second call");
        foreach (var failure in new[] { "contradicted", "query_mismatch", "search_only", "same_publisher", "stale", "rate_limited" })
        {
            mode = failure;
            var denied = await CheckAdd(addPost);
            Require(denied is { Decision.Action: "no_signal", Post.Verdict.Classification: "add_case", Error: null, Review: not null }
                && denied.Review.Status == (mode is "contradicted" or "same_publisher" ? "not_supported" : "unavailable"),
                $"Do not publish add on a failed countercheck; keep the first report ({mode})");
            if (mode == "rate_limited")
                Require(denied.Review?.Error == "rate_limited" && denied.RetryAt == now.AddMinutes(17), "Preserve the stage-two quota deadline");
        }
    }

    private static async Task BatchAsync(DateTimeOffset now)
    {
        var owner = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var calls = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opinion = new OpinionPost("Сводка.", "Оценка.", [], "Мнение.", "Наблюдение.", Verdict("exit_review", "medium", "project", "markets"));
        using var queue = new ChatGptLabJobs(async (_, coin, _, ct) =>
        {
            calls.Enqueue(coin);
            if (coin == "coin-00") { first.TrySetResult(); await release.Task.WaitAsync(ct); }
            return coin == "coin-01"
                ? new(opinion, [], null, RetryAt: now.AddMinutes(12), Decision: new("no_signal", "Проверка не завершилась."),
                    Review: new("sell", "unavailable", [], [], "quota_exhausted"))
                : new(null, [], "invalid_post_format");
        }, new FixedClock(now));
        string[] coins = Enumerable.Range(0, ChatGptLabJobs.MaxBatchCoins).Select(i => $"coin-{i:D2}").ToArray();
        var submitted = queue.SubmitBatch(owner, batchId, coins, "fixture-web");
        Require(submitted.Length == 40 && submitted.All(job => job.BatchId == batchId && job.Status == "queued")
            && queue.SubmitBatch(owner, batchId, coins, "fixture-web").Select(job => job.Id).SequenceEqual(submitted.Select(job => job.Id)),
            "Stage forty jobs atomically and deduplicate a repeated batch");
        Reject<AppFault>(() => queue.SubmitBatch(owner, batchId, coins[..2], "fixture-web"), "Idempotency rejects changed membership");
        Reject<AppFault>(() => queue.Submit(owner, Guid.NewGuid(), "other", "fixture-web"), "No second active group per owner");
        Reject<AppFault>(() => queue.Cancel(Guid.NewGuid(), batchId), "Cannot cancel another owner's batch");
        Require(queue.List(Guid.NewGuid()).Length == 0, "Do not disclose another owner's coins");

        // Start while the first call is blocked to verify that cancellation and a provider limit are independent.
        var cancelledOwner = Guid.NewGuid();
        var cancelled = Guid.NewGuid();
        queue.SubmitBatch(cancelledOwner, cancelled, ["cancelled-a", "cancelled-b"], "fixture-web");
        await queue.StartAsync(default);
        try
        {
            await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queue.Cancel(cancelledOwner, cancelled);
            release.TrySetResult();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (queue.List(owner).Any(job => job.Status is "queued" or "running"))
                await Task.Delay(10, deadline.Token);
            Require(calls.SequenceEqual(["coin-00", "coin-01"]) && queue.List(owner).Count(job => job.Status == "skipped") == 38,
                "A limit stops the batch after the first limited charge; no blind calls to the remaining coins");
            var firstReport = queue.List(owner).Single(job => job.CoinId == "coin-01");
            Require(firstReport is { Status: "succeeded", Decision.Action: "no_signal", Review.Error: "quota_exhausted" }
                && firstReport.CompletedAt == now, "A second-stage quota stop preserves the initial report and final timestamp");
            Reject<AppFault>(() => queue.Submit(owner, Guid.NewGuid(), "later", "fixture-web"), "A second-stage quota applies to future submissions");
            Require(queue.List(cancelledOwner).All(job => job.Status == "cancelled"), "A cancelled batch retains its terminal state");
        }
        finally { await queue.StopAsync(default); }
    }

    private static OpinionSection Gap(string block) => new(block, "insufficient", "Данных пока недостаточно.", [], null, "unknown", "none");

    private static OpinionVerdict Verdict(string classification, string confidence, params string[] basis) =>
        new(classification, confidence, "Краткий тезис.", "Контраргумент к оценке.", "Появление новых сведений.", basis, null, null);

    private sealed class OpinionFixture(OpinionPost post, AiSource[] sources, Func<AiRequest, AiReceipt> review) : IAiProvider
    {
        public string Id => "fixture";
        public ValueTask<AiCapabilities> CapabilitiesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<AiReceipt> RunAsync(AiRequest request, CancellationToken ct) => Task.FromResult(request.OutputSchemaName == "coin_opinion_check_v1"
            ? review(request) : new AiReceipt(
            Id, "req_fixture", "completed", null, null, null, "chatgpt_plan",
            JsonSerializer.Serialize(post, PortfelJson.Options), [new("web_search_call", "call_fixture", [], sources)]));
    }

    private static string Frame(string eventName, object body) => $"event: {eventName}\ndata: {JsonSerializer.Serialize(body)}\n\n";
}
