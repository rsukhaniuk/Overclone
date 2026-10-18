using System.Net;
using System.Text;
using System.Text.Json;
using Client.AI;
using Xunit;

namespace Workspace.Tests.AI;

public class AiProviderTests
{
    private const string Key = "separate-app-test-key";
    private const string Prompt = "Перевір 🙂 <script>literal source</script>";
    internal static string Success(AiProviderKind kind, string text = "Відповідь🙂") => kind switch
    {
        AiProviderKind.Ollama => JsonSerializer.Serialize(new { done = true, message = new { content = text } }),
        AiProviderKind.Gemini => JsonSerializer.Serialize(new { candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text } } } } } }),
        _ => JsonSerializer.Serialize(new { stop_reason = "end_turn", content = new[] { new { type = "text", text } } })
    };
    private static IAiProvider Provider(AiProviderKind kind, HttpClient http) => kind switch
    { AiProviderKind.Ollama => new OllamaProvider(http), AiProviderKind.Gemini => new GeminiProvider(http), _ => new ClaudeProvider(http) };

    [Theory]
    [InlineData(AiProviderKind.Ollama)]
    [InlineData(AiProviderKind.Gemini)]
    [InlineData(AiProviderKind.Claude)]
    public async Task Exact_wire_contract_is_nonstreaming_and_uses_per_request_auth_only(AiProviderKind kind)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(Success(kind))));
        using var http = new HttpClient(handler);
        var connection = AiConnection.Create(kind, "test-model", Key)!;
        var answer = await Provider(kind, http).SendAsync(connection, new(Prompt), CancellationToken.None);
        Assert.Equal(AiOutcome.Success, answer.Outcome); Assert.Equal("Відповідь🙂", answer.Text);
        var wire = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, wire.Method);
        Assert.Equal(kind switch
        {
            AiProviderKind.Ollama => "http://localhost:11434/api/chat",
            AiProviderKind.Gemini => "https://generativelanguage.googleapis.com/v1beta/models/test-model:generateContent",
            _ => "https://api.anthropic.com/v1/messages"
        }, wire.Uri);
        Assert.DoesNotContain(Key, wire.Body); Assert.DoesNotContain(Key, wire.Uri);
        Assert.DoesNotContain(Key, connection.ToString()); Assert.DoesNotContain(Key, answer.ToString());
        Assert.Empty(http.DefaultRequestHeaders);
        Assert.Equal("application/json", wire.ContentType);
        Assert.Equal("omit", wire.Options["credentials"]); Assert.Equal("no-store", wire.Options["cache"]);
        Assert.Equal("error", wire.Options["redirect"]); Assert.True(wire.Streaming);
        using var json = JsonDocument.Parse(wire.Body); var root = json.RootElement;
        if (kind == AiProviderKind.Gemini)
        {
            Assert.Equal(Key, wire.Headers["x-goog-api-key"]);
            Assert.Equal(Prompt, root.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
            Assert.Equal(AiLimits.OutputTokens, root.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32());
            Assert.Equal(1, root.GetProperty("generationConfig").GetProperty("candidateCount").GetInt32());
        }
        else
        {
            Assert.Equal("test-model", root.GetProperty("model").GetString());
            Assert.False(root.GetProperty("stream").GetBoolean());
            var message = Assert.Single(root.GetProperty("messages").EnumerateArray());
            Assert.Equal("user", message.GetProperty("role").GetString()); Assert.Equal(Prompt, message.GetProperty("content").GetString());
            if (kind == AiProviderKind.Claude)
            {
                Assert.Equal(Key, wire.Headers["x-api-key"]);
                Assert.Equal("2023-06-01", wire.Headers["anthropic-version"]);
                Assert.Equal("true", wire.Headers["anthropic-dangerous-direct-browser-access"]);
                Assert.Equal(AiLimits.OutputTokens, root.GetProperty("max_tokens").GetInt32());
            }
            else
            {
                Assert.DoesNotContain(wire.Headers, h => h.Key.Contains("key", StringComparison.OrdinalIgnoreCase));
                Assert.Equal(AiLimits.OutputTokens, root.GetProperty("options").GetProperty("num_predict").GetInt32());
            }
        }
    }

    [Theory]
    [InlineData(401, AiOutcome.Authentication)] [InlineData(403, AiOutcome.Authentication)]
    [InlineData(429, AiOutcome.Quota)] [InlineData(404, AiOutcome.MissingModel)] [InlineData(500, AiOutcome.Unavailable)]
    public async Task Status_errors_never_reflect_body_headers_or_keys(int status, AiOutcome expected)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(Key) }));
        using var http = new HttpClient(handler);
        var reply = await new GeminiProvider(http).SendAsync(AiConnection.Create(AiProviderKind.Gemini, "test", Key)!, new(Prompt), default);
        Assert.Equal(expected, reply.Outcome); Assert.Null(reply.Text);
        Assert.DoesNotContain(Key, AiMessages.For(reply.Outcome)); Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(AiProviderKind.Ollama, "{\"done\":true,\"message\":{\"content\":\"\"}}", AiOutcome.Refused)]
    [InlineData(AiProviderKind.Gemini, "{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}", AiOutcome.Refused)]
    [InlineData(AiProviderKind.Gemini, "{\"candidates\":[]}", AiOutcome.Refused)]
    [InlineData(AiProviderKind.Gemini, "{\"candidates\":[{\"finishReason\":\"MAX_TOKENS\"}]}", AiOutcome.Refused)]
    [InlineData(AiProviderKind.Claude, "{\"stop_reason\":\"refusal\",\"content\":[]}", AiOutcome.Refused)]
    [InlineData(AiProviderKind.Claude, "{\"stop_reason\":\"tool_use\",\"content\":[]}", AiOutcome.Refused)]
    [InlineData(AiProviderKind.Ollama, "{\"done\":true,\"message\":{}}", AiOutcome.Malformed)]
    [InlineData(AiProviderKind.Gemini, "not JSON", AiOutcome.Malformed)]
    [InlineData(AiProviderKind.Claude, "{\"stop_reason\":\"end_turn\",\"content\":null}", AiOutcome.Malformed)]
    [InlineData(AiProviderKind.Ollama, "{\"done\":false,\"done\":true,\"message\":{\"content\":\"x\"}}", AiOutcome.Malformed)]
    public async Task Refusals_truncations_malformed_and_duplicate_fields_are_typed(AiProviderKind kind, string body, AiOutcome outcome)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(body)));
        using var http = new HttpClient(handler);
        var reply = await Provider(kind, http).SendAsync(AiConnection.Create(kind, "test", Key)!, new(Prompt), default);
        Assert.Equal(outcome, reply.Outcome); Assert.Null(reply.Text);
    }

    [Theory]
    [InlineData(AiProviderKind.Ollama)] [InlineData(AiProviderKind.Gemini)] [InlineData(AiProviderKind.Claude)]
    public async Task Body_is_bounded_before_parsing_and_output_is_separately_bounded(AiProviderKind kind)
    {
        var body = new UnknownLengthContent(new byte[AiLimits.BodyBytes + 100]);
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body }));
        using var http = new HttpClient(handler);
        var connection = AiConnection.Create(kind, "test", Key)!;
        Assert.Equal(AiOutcome.TooLarge, (await Provider(kind, http).SendAsync(connection, new(Prompt), default)).Outcome);
        Assert.Equal(AiLimits.BodyBytes + 1, body.Stream.BytesRead);
        handler.Reply = (_, _) => Task.FromResult(Json(Success(kind, new string('a', AiLimits.OutputBytes + 1))));
        Assert.Equal(AiOutcome.TooLarge, (await Provider(kind, http).SendAsync(connection, new(Prompt), default)).Outcome);
        handler.Reply = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xff, 0xfe]) });
        Assert.Equal(AiOutcome.Malformed, (await Provider(kind, http).SendAsync(connection, new(Prompt), default)).Outcome);
    }

    [Fact]
    public async Task Auth_echo_and_exception_details_are_not_answers_and_no_retry_occurs()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(Success(AiProviderKind.Claude, Key))));
        using var http = new HttpClient(handler); var provider = new ClaudeProvider(http);
        var connection = AiConnection.Create(AiProviderKind.Claude, "test", Key)!;
        Assert.True(connection.HasCredential); Assert.DoesNotContain(Key, JsonSerializer.Serialize(connection));
        Assert.Equal(AiOutcome.Malformed, (await provider.SendAsync(connection, new(Prompt), default)).Outcome);
        handler.Reply = (_, _) => throw new HttpRequestException(Key);
        var reply = await provider.SendAsync(connection, new(Prompt), default);
        Assert.Equal(AiOutcome.Connectivity, reply.Outcome); Assert.Null(reply.Text); Assert.DoesNotContain(Key, AiMessages.For(reply.Outcome));
        Assert.Equal(2, handler.Requests.Count);
        // Even a source/prompt containing the entered credential is not sent.
        Assert.Equal(AiOutcome.Malformed, (await provider.SendAsync(connection, new(Key), default)).Outcome);
        Assert.Equal(2, handler.Requests.Count);
        connection.Disconnect();
        Assert.False(connection.Connected); Assert.False(connection.HasCredential);
        Assert.DoesNotContain(Key, JsonSerializer.Serialize(connection));
        Assert.Equal(AiOutcome.Unconfigured, (await provider.SendAsync(connection, new(Prompt), default)).Outcome);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Cancellation_is_typed_and_does_not_retry_or_report_exception_text()
    {
        using var handler = new Handler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json("{}"); });
        using var http = new HttpClient(handler); using var cts = new CancellationTokenSource();
        var request = new OllamaProvider(http).SendAsync(AiConnection.Create(AiProviderKind.Ollama, "test")!, new(Prompt), cts.Token);
        await handler.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5)); cts.Cancel();
        Assert.Equal(AiOutcome.Cancelled, (await request).Outcome); Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Provider_transport_timeout_is_safe_and_has_no_retry()
    {
        using var handler = new Handler((_, _) => throw new TaskCanceledException(Key));
        using var http = new HttpClient(handler);
        var result = await new ClaudeProvider(http).SendAsync(AiConnection.Create(AiProviderKind.Claude, "test", Key)!, new(Prompt), default);
        Assert.Equal(AiOutcome.Timeout, result.Outcome); Assert.Null(result.Text);
        Assert.DoesNotContain(Key, AiMessages.For(result.Outcome)); Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(AiProviderKind.Gemini, "{\"candidates\":[{\"finishReason\":\"STOP\",\"content\":{\"parts\":[{\"thought\":true,\"text\":\"private reasoning\"},{\"text\":\"one\"},{\"text\":\"two\"}]}}]}")]
    [InlineData(AiProviderKind.Claude, "{\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"text\",\"text\":\"one\"},{\"type\":\"text\",\"text\":\"two\"}]}")]
    public async Task Multiple_text_parts_are_combined_without_exposing_reasoning(AiProviderKind kind, string body)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(body)));
        using var http = new HttpClient(handler);
        var result = await Provider(kind, http).SendAsync(AiConnection.Create(kind, "test", Key)!, new(Prompt), default);
        Assert.Equal(AiOutcome.Success, result.Outcome); Assert.Equal("onetwo", result.Text);
    }

    [Theory]
    [InlineData("https://example.com")] [InlineData("http://localhost:11434/path")]
    [InlineData("http://user:secret@localhost:11434")] [InlineData("http://localhost:11434/?key=secret")]
    [InlineData("file:///tmp/ollama")] [InlineData("http://8.8.8.8:11434")]
    public void Local_endpoint_never_silently_routes_to_cloud_or_embeds_credentials(string url)
        => Assert.Null(AiConnection.Create(AiProviderKind.Ollama, "test", localUrl: url));

    [Theory]
    [InlineData("http://localhost:11434")] [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://[::1]:11434")] [InlineData("https://192.168.1.2:11434")]
    public void Explicit_local_service_addresses_are_allowed(string url)
        => Assert.NotNull(AiConnection.Create(AiProviderKind.Ollama, "test:tag", localUrl: url));

    [Fact]
    public void Blank_model_and_resource_url_model_are_not_invented_or_sent()
    {
        Assert.Null(AiConnection.Create(AiProviderKind.Ollama, ""));
        Assert.Null(AiConnection.Create(AiProviderKind.Gemini, "models/test", Key));
        Assert.Null(AiConnection.Create(AiProviderKind.Claude, "test", "bad\r\nkey"));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed record Wire(HttpMethod Method, string Uri, string Body, string? ContentType,
        Dictionary<string, string> Headers, IDictionary<string, object> Options, bool Streaming);
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply = reply;
        internal List<Wire> Requests = [];
        internal TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.Options.TryGetValue(new HttpRequestOptionsKey<IDictionary<string, object>>("WebAssemblyFetchOptions"), out var options);
            request.Options.TryGetValue(new HttpRequestOptionsKey<bool>("WebAssemblyEnableStreamingResponse"), out var streaming);
            Requests.Add(new(request.Method, request.RequestUri!.AbsoluteUri, await request.Content!.ReadAsStringAsync(ct),
                request.Content.Headers.ContentType?.MediaType, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase),
                options ?? new Dictionary<string, object>(), streaming));
            Reached.TrySetResult(); return await Reply(request, ct);
        }
    }
    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal int BytesRead;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { var n = await base.ReadAsync(buffer, ct); BytesRead += n; return n; }
    }
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        internal CountingStream Stream = new(bytes);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(Stream);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
    }
}
