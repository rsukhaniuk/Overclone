using System.Text;

namespace Client.AI;

public enum AiProviderKind { Ollama, Gemini, Claude }
public enum AiOutcome { Success, Unconfigured, Authentication, Quota, Connectivity, MissingModel, Cancelled, Timeout, Malformed, TooLarge, Refused, Unavailable }
public static class AiLimits
{
    public const int ContextBytes = 65536;
    public const int InstructionBytes = 8192;
    public const int OutputBytes = 131072;
    public const int BodyBytes = 524288;
    public const int OutputTokens = 4096;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static bool Fits(string? value, int bytes)
    {
        if (value is null || value.Length > bytes) return false;
        try { return Utf8.GetByteCount(value) <= bytes; }
        catch (EncoderFallbackException) { return false; }
    }
}

// Deliberately not records: credentials/context never enter generated ToString.
// Clear releases references; immutable .NET strings cannot be physically zeroized.
public sealed class AiConnection
{
    private string? _key;
    private AiConnection(AiProviderKind provider, string model, Uri destination, string? key)
    { Provider = provider; Model = model; Destination = destination; _key = key; }
    public AiProviderKind Provider { get; }
    public string Model { get; }
    public Uri Destination { get; }
    public bool Connected { get; private set; } = true;
    public bool HasCredential => _key is not null;
    internal string? Key => _key;
    internal bool ContainsKey(string text) => _key is { Length: > 0 } key && text.Contains(key, StringComparison.Ordinal);
    public void Disconnect() { Connected = false; _key = null; }
    public override string ToString() => "AI connection (credentials omitted)";

    public static AiConnection? Create(AiProviderKind provider, string model, string? key = null, string localUrl = "http://localhost:11434")
    {
        if (!Enum.IsDefined(provider) || string.IsNullOrWhiteSpace(model) || model.Length > 128
            || !model.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '/')) return null;
        if (provider != AiProviderKind.Ollama && (string.IsNullOrWhiteSpace(key) || key.Length > 512
            || !key.All(c => c is >= '!' and <= '~'))) return null;
        Uri uri;
        if (provider == AiProviderKind.Ollama)
        {
            if (localUrl is null || localUrl.Length > 256 || !Uri.TryCreate(localUrl, UriKind.Absolute, out var local) || local.Scheme is not ("http" or "https")
                || local.UserInfo.Length != 0 || local.Query.Length != 0 || local.Fragment.Length != 0 || local.AbsolutePath != "/"
                || !IsLocal(local)) return null;
            uri = new Uri(local, "/api/chat");
        }
        else if (provider == AiProviderKind.Gemini)
        {
            // A model ID, not a URL/path or model resource name.
            if (model.Contains('/') || model.Contains(':')) return null;
            uri = new Uri($"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");
        }
        else uri = new Uri("https://api.anthropic.com/v1/messages");
        // Reject accidental placement of a credential in disclosed metadata.
        if (provider != AiProviderKind.Ollama && (model.Contains(key!, StringComparison.Ordinal)
            || uri.AbsoluteUri.Contains(key!, StringComparison.Ordinal))) return null;
        return new(provider, model, uri, provider == AiProviderKind.Ollama ? null : key);
    }
    private static bool IsLocal(Uri uri)
    {
        if (uri.IsLoopback) return true;
        if (!System.Net.IPAddress.TryParse(uri.Host, out var ip)) return false;
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31);
    }
}

public sealed class AiTextRequest(string prompt)
{
    public string Prompt { get; } = prompt;
    public override string ToString() => "AI text request (content omitted)";
}
public sealed class AiReply
{
    private AiReply(AiOutcome outcome, string? text) { Outcome = outcome; Text = text; }
    public AiOutcome Outcome { get; }
    public string? Text { get; }
    public static AiReply Answer(string text) => new(AiOutcome.Success, text);
    public static AiReply Failure(AiOutcome outcome) => new(outcome, null);
    public override string ToString() => $"AI reply: {Outcome} (content omitted)";
}
public interface IAiProvider
{
    AiProviderKind Kind { get; }
    Task<AiReply> SendAsync(AiConnection connection, AiTextRequest request, CancellationToken ct);
}
public static class AiMessages
{
    public static string For(AiOutcome outcome) => outcome switch
    {
        AiOutcome.Success => "Answer received. Nothing has been edited.",
        AiOutcome.Unconfigured => "Connect a provider and specify a model first.",
        AiOutcome.Authentication => "Provider authentication failed. Check your separate app key.",
        AiOutcome.Quota => "Provider quota or rate limit reached. No retry was made.",
        AiOutcome.Connectivity => "Cannot reach the provider. Check connectivity, browser origin/CORS, mixed-content and local-network permissions. No proxy or fallback was used.",
        AiOutcome.MissingModel => "The requested model or endpoint is unavailable.",
        AiOutcome.Cancelled => "Cancelled. Waiting requests must settle before another operation.",
        AiOutcome.Timeout => "The request timed out. No retry was made.",
        AiOutcome.Malformed => "The provider returned an invalid response.",
        AiOutcome.TooLarge => "The context, request or answer exceeds the finite size limit. Nothing was truncated.",
        AiOutcome.Refused => "The provider blocked the request or returned no usable text.",
        _ => "The provider request could not be completed safely. No retry was made."
    };
}
