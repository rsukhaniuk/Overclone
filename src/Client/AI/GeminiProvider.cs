using System.Text;
using System.Text.Json;

namespace Client.AI;

public sealed class GeminiProvider(HttpClient http) : AiHttpProvider(http)
{
    public override AiProviderKind Kind => AiProviderKind.Gemini;
    protected override object Payload(AiConnection connection, AiTextRequest request) => new
    {
        contents = new[] { new { role = "user", parts = new[] { new { text = request.Prompt } } } },
        generationConfig = new { maxOutputTokens = AiLimits.OutputTokens, candidateCount = 1 }
    };
    protected override void Authenticate(HttpRequestMessage message, string? key) => message.Headers.Add("x-goog-api-key", key);
    protected override AiReply Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return AiReply.Failure(AiOutcome.Malformed);
        if (root.TryGetProperty("promptFeedback", out var feedback) && feedback.TryGetProperty("blockReason", out _)) return AiReply.Failure(AiOutcome.Refused);
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0) return AiReply.Failure(AiOutcome.Refused);
        var first = candidates[0];
        if (!AiJson.String(first, "finishReason", out var reason) || reason != "STOP") return AiReply.Failure(AiOutcome.Refused);
        if (!first.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array) return AiReply.Failure(AiOutcome.Refused);
        var text = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True) continue;
            if (!AiJson.String(part, "text", out var value)) return AiReply.Failure(AiOutcome.Refused);
            text.Append(value);
        }
        return AiJson.Text(text.ToString());
    }
}
