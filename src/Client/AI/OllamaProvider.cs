using System.Text.Json;

namespace Client.AI;

public sealed class OllamaProvider(HttpClient http) : AiHttpProvider(http)
{
    public override AiProviderKind Kind => AiProviderKind.Ollama;
    protected override object Payload(AiConnection connection, AiTextRequest request) => new
    {
        model = connection.Model,
        messages = new[] { new { role = "user", content = request.Prompt } },
        stream = false,
        options = new { num_predict = AiLimits.OutputTokens }
    };
    protected override void Authenticate(HttpRequestMessage message, string? key) { }
    protected override AiReply Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return AiReply.Failure(AiOutcome.Malformed);
        if (root.TryGetProperty("error", out _)) return AiReply.Failure(AiOutcome.Unavailable);
        if (!root.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True) return AiReply.Failure(AiOutcome.Malformed);
        if (AiJson.String(root, "done_reason", out var reason) && reason == "length") return AiReply.Failure(AiOutcome.Refused);
        if (!root.TryGetProperty("message", out var message) || !AiJson.String(message, "content", out var text)) return AiReply.Failure(AiOutcome.Malformed);
        if (message.TryGetProperty("tool_calls", out var tools) && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0) return AiReply.Failure(AiOutcome.Refused);
        return AiJson.Text(text);
    }
}
