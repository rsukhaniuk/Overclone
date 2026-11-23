using System.Text;

namespace Client.AI;

public enum AiOutcome { Success, Unconfigured, AccessDenied, Quota, Connectivity, MissingModel, Cancelled, Timeout, Malformed, TooLarge, Refused, Unavailable }
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

public sealed class AiConnection
{
    public const string DefaultLocalUrl = "http://localhost:11434";
    private AiConnection(string model, Uri destination)
    { Model = model; Destination = destination; }
    public string Model { get; }
    public Uri Destination { get; }
    public bool Connected { get; private set; } = true;
    public void Disconnect() => Connected = false;

    public static AiConnection? Create(string model, string localUrl = DefaultLocalUrl)
        => ValidModel(model) && LocalService(localUrl) is { } local
            ? new(model, new Uri(local, "/api/chat")) : null;

    internal static bool ValidModel(string model) => !string.IsNullOrWhiteSpace(model) && model.Length <= 128
        && model.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '/');

    internal static Uri? LocalService(string localUrl)
    {
        // Match the app's browser connection policy for the local service.
        if (localUrl is null || localUrl.Length > 256 || !Uri.TryCreate(localUrl, UriKind.Absolute, out var local)
            || local.Scheme != Uri.UriSchemeHttp || local.Port != 11434 || local.IdnHost is not ("localhost" or "127.0.0.1")
            || local.UserInfo.Length != 0 || local.Query.Length != 0 || local.Fragment.Length != 0 || local.AbsolutePath != "/") return null;
        return local;
    }
}

public sealed class AiTextRequest(string prompt, bool inlineReview = false)
{
    public string Prompt { get; } = prompt;
    public bool InlineReview { get; } = inlineReview;
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
    Task<AiReply> SendAsync(AiConnection connection, AiTextRequest request, CancellationToken ct);
}
public static class AiMessages
{
    public static string For(AiOutcome outcome) => outcome switch
    {
        AiOutcome.Success => "Answer received. Nothing has been edited.",
        AiOutcome.Unconfigured => "Connect to Ollama and specify a model first.",
        AiOutcome.AccessDenied => "Ollama denied access. Check the service's allowed browser origins.",
        AiOutcome.Quota => "Ollama reported a rate limit. Send again when the service is ready.",
        AiOutcome.Connectivity => "Cannot reach Ollama. Check that it is running and allows this browser origin.",
        AiOutcome.MissingModel => "The requested model or endpoint is unavailable.",
        AiOutcome.Cancelled => "Cancelled. Waiting requests must settle before another operation.",
        AiOutcome.Timeout => "The request timed out. No retry was made.",
        AiOutcome.Malformed => "The provider returned an invalid response.",
        AiOutcome.TooLarge => "The context, request or answer exceeds the finite size limit. Nothing was truncated.",
        AiOutcome.Refused => "The provider blocked the request or returned no usable text.",
        _ => "The provider request could not be completed safely. No retry was made."
    };
}
