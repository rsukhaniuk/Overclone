using System.Text;
using System.Text.Json;

namespace Client.AI;

public sealed class ClaudeProvider(HttpClient http) : AiHttpProvider(http)
{
    public override AiProviderKind Kind => AiProviderKind.Claude;
    protected override object Payload(AiConnection connection, AiTextRequest request) => new
    {
        model = connection.Model,
        max_tokens = AiLimits.OutputTokens,
        stream = false,
        messages = new[] { new { role = "user", content = request.Prompt } }
    };
    protected override void Authenticate(HttpRequestMessage message, string? key)
    {
        message.Headers.Add("x-api-key", key);
        message.Headers.Add("anthropic-version", "2023-06-01");
        // The official SDK's raw HTTP browser opt-in (not secret isolation).
        message.Headers.Add("anthropic-dangerous-direct-browser-access", "true");
    }
    protected override AiReply Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return AiReply.Failure(AiOutcome.Malformed);
        if (!AiJson.String(root, "stop_reason", out var reason) || reason is not ("end_turn" or "stop_sequence")) return AiReply.Failure(AiOutcome.Refused);
        if (!root.TryGetProperty("content", out var blocks) || blocks.ValueKind != JsonValueKind.Array) return AiReply.Failure(AiOutcome.Malformed);
        var text = new StringBuilder();
        foreach (var block in blocks.EnumerateArray())
        {
            if (!AiJson.String(block, "type", out var type) || type != "text" || !AiJson.String(block, "text", out var value)) return AiReply.Failure(AiOutcome.Refused);
            text.Append(value);
        }
        return AiJson.Text(text.ToString());
    }
}
