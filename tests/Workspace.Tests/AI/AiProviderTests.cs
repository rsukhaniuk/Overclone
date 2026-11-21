using System.Net;
using System.Text;
using System.Text.Json;
using Client.AI;
using Xunit;

namespace Workspace.Tests.AI;

public class AiProviderTests
{
    private const string InternalDetail = "private-transport-detail";
    private const string Prompt = "Перевір 🙂 <script>literal source</script>";
    private static string Success(string text = "Відповідь🙂") =>
        JsonSerializer.Serialize(new { done = true, message = new { content = text } });

    [Fact]
    public async Task Exact_local_wire_contract_omits_credentials_and_requires_a_complete_response()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(Success())));
        using var http = new HttpClient(handler);
        var answer = await new OllamaProvider(http).SendAsync(AiConnection.Create("test-model")!, new(Prompt), default);
        Assert.Equal(AiOutcome.Success, answer.Outcome);
        Assert.Equal("Відповідь🙂", answer.Text);
        Assert.DoesNotContain("Відповідь🙂", answer.ToString());
        var wire = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, wire.Method);
        Assert.Equal("http://localhost:11434/api/chat", wire.Uri);
        Assert.Empty(wire.Headers);
        Assert.Empty(http.DefaultRequestHeaders);
        Assert.Equal("application/json", wire.ContentType);
        Assert.Equal("omit", wire.Options["credentials"]);
        Assert.Equal("no-store", wire.Options["cache"]);
        Assert.Equal("error", wire.Options["redirect"]);
        Assert.True(wire.Streaming);
        using var json = JsonDocument.Parse(wire.Body);
        var root = json.RootElement;
        Assert.Equal("test-model", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        var message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal(Prompt, message.GetProperty("content").GetString());
        Assert.Equal(AiLimits.OutputTokens, root.GetProperty("options").GetProperty("num_predict").GetInt32());
    }

    [Theory]
    [InlineData(401, AiOutcome.AccessDenied)] [InlineData(403, AiOutcome.AccessDenied)]
    [InlineData(429, AiOutcome.Quota)] [InlineData(404, AiOutcome.MissingModel)] [InlineData(500, AiOutcome.Unavailable)]
    public async Task Status_errors_hide_response_details_without_retrying(int status, AiOutcome outcome)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent(InternalDetail) }));
        using var http = new HttpClient(handler);
        var reply = await new OllamaProvider(http).SendAsync(AiConnection.Create("test")!, new(Prompt), default);
        Assert.Equal(outcome, reply.Outcome);
        Assert.Null(reply.Text);
        Assert.DoesNotContain(InternalDetail, AiMessages.For(reply.Outcome));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("{\"done\":true,\"message\":{\"content\":\"\"}}", AiOutcome.Refused)]
    [InlineData("{\"done\":true,\"done_reason\":\"length\",\"message\":{\"content\":\"partial\"}}", AiOutcome.Refused)]
    [InlineData("{\"done\":true,\"message\":{\"content\":\"text\",\"tool_calls\":[{}]}}", AiOutcome.Refused)]
    [InlineData("{\"done\":true,\"message\":{}}", AiOutcome.Malformed)]
    [InlineData("{\"done\":false,\"done\":true,\"message\":{\"content\":\"x\"}}", AiOutcome.Malformed)]
    [InlineData("not JSON", AiOutcome.Malformed)]
    public async Task Refusals_truncation_malformed_and_duplicate_fields_are_typed(string body, AiOutcome outcome)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(body)));
        using var http = new HttpClient(handler);
        var reply = await new OllamaProvider(http).SendAsync(AiConnection.Create("test")!, new(Prompt), default);
        Assert.Equal(outcome, reply.Outcome);
        Assert.Null(reply.Text);
    }

    [Fact]
    public async Task Body_and_output_are_bounded_separately()
    {
        var body = new UnknownLengthContent(new byte[AiLimits.BodyBytes + 100]);
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body }));
        using var http = new HttpClient(handler);
        var provider = new OllamaProvider(http);
        var connection = AiConnection.Create("test")!;
        Assert.Equal(AiOutcome.TooLarge, (await provider.SendAsync(connection, new(Prompt), default)).Outcome);
        Assert.Equal(AiLimits.BodyBytes + 1, body.Stream.BytesRead);
        handler.Reply = (_, _) => Task.FromResult(Json(Success(new string('a', AiLimits.OutputBytes + 1))));
        Assert.Equal(AiOutcome.TooLarge, (await provider.SendAsync(connection, new(Prompt), default)).Outcome);
        handler.Reply = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xff, 0xfe]) });
        Assert.Equal(AiOutcome.Malformed, (await provider.SendAsync(connection, new(Prompt), default)).Outcome);
    }

    [Fact]
    public async Task Cancellation_is_typed_and_does_not_retry()
    {
        using var handler = new Handler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json("{}"); });
        using var http = new HttpClient(handler);
        using var cts = new CancellationTokenSource();
        var request = new OllamaProvider(http).SendAsync(AiConnection.Create("test")!, new(Prompt), cts.Token);
        await handler.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        Assert.Equal(AiOutcome.Cancelled, (await request).Outcome);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Timeout_connectivity_and_disconnect_have_safe_terminal_outcomes()
    {
        using var handler = new Handler((_, _) => throw new TaskCanceledException(InternalDetail));
        using var http = new HttpClient(handler);
        var provider = new OllamaProvider(http);
        var connection = AiConnection.Create("test")!;
        var timeout = await provider.SendAsync(connection, new(Prompt), default);
        Assert.Equal(AiOutcome.Timeout, timeout.Outcome);
        Assert.Null(timeout.Text);
        handler.Reply = (_, _) => throw new HttpRequestException(InternalDetail);
        var connectivity = await provider.SendAsync(connection, new(Prompt), default);
        Assert.Equal(AiOutcome.Connectivity, connectivity.Outcome);
        Assert.Null(connectivity.Text);
        Assert.DoesNotContain(InternalDetail, AiMessages.For(connectivity.Outcome));
        connection.Disconnect();
        Assert.Equal(AiOutcome.Unconfigured, (await provider.SendAsync(connection, new(Prompt), default)).Outcome);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("https://example.com")] [InlineData("http://localhost:11434/path")]
    [InlineData("http://user:secret@localhost:11434")] [InlineData("http://localhost:11434/?key=secret")]
    [InlineData("file:///tmp/ollama")] [InlineData("http://8.8.8.8:11434")]
    [InlineData("http://[::1]:11434")] [InlineData("https://localhost:11434")]
    [InlineData("https://192.168.1.2:11434")] [InlineData("http://localhost:12345")]
    public void Address_outside_the_browser_policy_is_refused(string url)
        => Assert.Null(AiConnection.Create("test", url));

    [Theory]
    [InlineData("http://localhost:11434")] [InlineData("http://127.0.0.1:11434")]
    public void Supported_address_and_exact_model_id_are_preserved(string url)
    {
        var connection = Assert.IsType<AiConnection>(AiConnection.Create("library/test:tag", url));
        Assert.Equal("library/test:tag", connection.Model);
        Assert.Equal(url + "/api/chat", connection.Destination.AbsoluteUri);
    }

    [Theory]
    [InlineData("")] [InlineData("https://example.com?q=1")] [InlineData("model with spaces")]
    public void An_invalid_model_id_is_refused(string model) => Assert.Null(AiConnection.Create(model));

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
