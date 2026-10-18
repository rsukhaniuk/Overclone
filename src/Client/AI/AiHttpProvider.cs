using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Client.AI;

/// <summary>One nonstreaming protocol response, read through a bounded transport
/// stream. No retries, ambient credentials, raw errors or shared auth headers.</summary>
public abstract class AiHttpProvider(HttpClient http) : IAiProvider
{
    public abstract AiProviderKind Kind { get; }
    protected abstract object Payload(AiConnection connection, AiTextRequest request);
    protected abstract void Authenticate(HttpRequestMessage message, string? key);
    protected abstract AiReply Parse(JsonElement root);

    public async Task<AiReply> SendAsync(AiConnection connection, AiTextRequest request, CancellationToken ct)
    {
        if (!connection.Connected || connection.Provider != Kind) return AiReply.Failure(AiOutcome.Unconfigured);
        if (!AiLimits.Fits(request.Prompt, AiLimits.ContextBytes + AiLimits.InstructionBytes + 4096)) return AiReply.Failure(AiOutcome.TooLarge);
        if (connection.ContainsKey(request.Prompt)) return AiReply.Failure(AiOutcome.Malformed);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(AiLimits.Timeout);
        try
        {
            ct.ThrowIfCancellationRequested();
            var payload = JsonSerializer.SerializeToUtf8Bytes(Payload(connection, request));
            if (payload.Length > AiLimits.BodyBytes) return AiReply.Failure(AiOutcome.TooLarge);
            using var message = new HttpRequestMessage(HttpMethod.Post, connection.Destination);
            message.SetBrowserRequestCredentials(BrowserRequestCredentials.Omit);
            message.SetBrowserRequestCache(BrowserRequestCache.NoStore);
            message.SetBrowserRequestOption("redirect", "error");
            message.SetBrowserResponseStreamingEnabled(true);
            message.Content = new ByteArrayContent(payload);
            message.Content.Headers.ContentType = new("application/json");
            Authenticate(message, connection.Key);
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            if (!connection.Connected) return AiReply.Failure(AiOutcome.Cancelled);
            if (!response.IsSuccessStatusCode) return AiReply.Failure(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => AiOutcome.Authentication,
                HttpStatusCode.TooManyRequests => AiOutcome.Quota,
                HttpStatusCode.NotFound => AiOutcome.MissingModel,
                _ => AiOutcome.Unavailable
            });
            if (response.Content.Headers.ContentLength > AiLimits.BodyBytes) return AiReply.Failure(AiOutcome.TooLarge);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            // Fixed upper allocation, even when content length is missing/dishonest.
            var bytes = new byte[AiLimits.BodyBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var n = await stream.ReadAsync(bytes.AsMemory(count), timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                if (n == 0) break;
                count += n;
            }
            if (count > AiLimits.BodyBytes) return AiReply.Failure(AiOutcome.TooLarge);
            AiLimits.Utf8.GetCharCount(bytes, 0, count);
            using var json = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 24 });
            if (!AiJson.Unique(json.RootElement)) return AiReply.Failure(AiOutcome.Malformed);
            var result = Parse(json.RootElement);
            timeout.Token.ThrowIfCancellationRequested();
            if (!connection.Connected) return AiReply.Failure(AiOutcome.Cancelled);
            if (result.Text is { } text)
            {
                if (connection.ContainsKey(text)) return AiReply.Failure(AiOutcome.Malformed);
                if (!AiLimits.Fits(text, AiLimits.OutputBytes)) return AiReply.Failure(AiOutcome.TooLarge);
            }
            return result;
        }
        catch (OperationCanceledException) { return AiReply.Failure(ct.IsCancellationRequested ? AiOutcome.Cancelled : AiOutcome.Timeout); }
        catch (HttpRequestException) { return AiReply.Failure(AiOutcome.Connectivity); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        { return AiReply.Failure(AiOutcome.Malformed); }
        catch (Exception) { return AiReply.Failure(AiOutcome.Unavailable); }
    }
}
internal static class AiJson
{
    internal static bool Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || !Unique(property.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) if (!Unique(child)) return false;
        return true;
    }
    internal static bool String(JsonElement parent, string name, out string value)
    {
        value = "";
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString()!;
        return true;
    }
    internal static AiReply Text(string text) => string.IsNullOrWhiteSpace(text) ? AiReply.Failure(AiOutcome.Refused) : AiReply.Answer(text);
}
