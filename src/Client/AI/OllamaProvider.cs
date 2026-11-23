using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Client.AI;

/// <summary>Sends one bounded Ollama request and reads its nonstreaming response.</summary>
public sealed class OllamaProvider(HttpClient http) : IAiProvider
{
    private static readonly JsonSerializerOptions PayloadJson = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public async Task<AiReply> SendAsync(AiConnection connection, AiTextRequest request, CancellationToken ct)
    {
        if (!connection.Connected) return AiReply.Failure(AiOutcome.Unconfigured);
        if (!AiLimits.Fits(request.Prompt, AiLimits.ContextBytes + AiLimits.InstructionBytes + 4096)) return AiReply.Failure(AiOutcome.TooLarge);
        var payload = JsonSerializer.SerializeToUtf8Bytes(Payload(connection, request), PayloadJson);
        if (payload.Length > AiLimits.BodyBytes) return AiReply.Failure(AiOutcome.TooLarge);
        using var message = new HttpRequestMessage(HttpMethod.Post, connection.Destination);
        message.Content = new ByteArrayContent(payload);
        message.Content.Headers.ContentType = new("application/json");
        var result = await ReadAsync(message, AiLimits.Timeout, Parse, AiReply.Failure, ct);
        return connection.Connected ? result : AiReply.Failure(AiOutcome.Cancelled);
    }

    public async Task<OllamaModelCatalog> DiscoverModelsAsync(string localUrl, CancellationToken ct)
    {
        if (AiConnection.LocalService(localUrl) is not { } local)
            return OllamaModelCatalog.Failed(AiOutcome.Unconfigured);
        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(local, "/api/tags"));
        return await ReadAsync(message, TimeSpan.FromSeconds(5), ParseModels, OllamaModelCatalog.Failed, ct);
    }

    private async Task<T> ReadAsync<T>(HttpRequestMessage message, TimeSpan deadline, Func<JsonElement, T> parse,
        Func<AiOutcome, T> failure, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(deadline);
        try
        {
            ct.ThrowIfCancellationRequested();
            message.SetBrowserRequestCredentials(BrowserRequestCredentials.Omit);
            message.SetBrowserRequestCache(BrowserRequestCache.NoStore);
            message.SetBrowserRequestOption("redirect", "error");
            message.SetBrowserResponseStreamingEnabled(true);
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            if (!response.IsSuccessStatusCode) return failure(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => AiOutcome.AccessDenied,
                HttpStatusCode.TooManyRequests => AiOutcome.Quota,
                HttpStatusCode.NotFound => AiOutcome.MissingModel,
                _ => AiOutcome.Unavailable
            });
            if (response.Content.Headers.ContentLength > AiLimits.BodyBytes) return failure(AiOutcome.TooLarge);
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
            if (count > AiLimits.BodyBytes) return failure(AiOutcome.TooLarge);
            AiLimits.Utf8.GetCharCount(bytes, 0, count);
            using var json = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 24 });
            if (!AiJson.Unique(json.RootElement)) return failure(AiOutcome.Malformed);
            var result = parse(json.RootElement);
            timeout.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) { return failure(ct.IsCancellationRequested ? AiOutcome.Cancelled : AiOutcome.Timeout); }
        catch (HttpRequestException) { return failure(AiOutcome.Connectivity); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        { return failure(AiOutcome.Malformed); }
        catch (Exception) { return failure(AiOutcome.Unavailable); }
    }

    private static OllamaModelCatalog ParseModels(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array)
            return OllamaModelCatalog.Failed(AiOutcome.Malformed);
        if (models.GetArrayLength() > 512) return OllamaModelCatalog.Failed(AiOutcome.TooLarge);

        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in models.EnumerateArray())
        {
            if (!AiJson.String(entry, "model", out var name) && !AiJson.String(entry, "name", out name)) continue;
            if (!AiConnection.ValidModel(name) || name.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase)
                || AiJson.String(entry, "remote_host", out var host) && host.Length > 0
                || AiJson.String(entry, "remote_model", out var remote) && remote.Length > 0) continue;
            if (entry.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Array
                && capabilities.GetArrayLength() > 0
                && !capabilities.EnumerateArray().Any(cap => cap.ValueKind == JsonValueKind.String && cap.GetString() == "completion"))
                continue;
            names.Add(name);
        }
        return new(AiOutcome.Success, names.ToArray());
    }

    private static object Payload(AiConnection connection, AiTextRequest request) => new
    {
        model = connection.Model,
        messages = new[] { new { role = "user", content = request.Prompt } },
        stream = false,
        format = request.InlineReview ? AiInlineReview.Schema : (JsonElement?)null,
        options = new { num_predict = AiLimits.OutputTokens }
    };

    private static AiReply Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return AiReply.Failure(AiOutcome.Malformed);
        if (root.TryGetProperty("error", out _)) return AiReply.Failure(AiOutcome.Unavailable);
        if (!root.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True) return AiReply.Failure(AiOutcome.Malformed);
        if (AiJson.String(root, "done_reason", out var reason) && reason == "length") return AiReply.Failure(AiOutcome.Refused);
        if (!root.TryGetProperty("message", out var message) || !AiJson.String(message, "content", out var text)) return AiReply.Failure(AiOutcome.Malformed);
        if (message.TryGetProperty("tool_calls", out var tools) && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0) return AiReply.Failure(AiOutcome.Refused);
        if (string.IsNullOrWhiteSpace(text)) return AiReply.Failure(AiOutcome.Refused);
        return AiLimits.Fits(text, AiLimits.OutputBytes) ? AiReply.Answer(text) : AiReply.Failure(AiOutcome.TooLarge);
    }
}

public sealed record OllamaModelCatalog(AiOutcome Outcome, IReadOnlyList<string> Models)
{
    internal static OllamaModelCatalog Failed(AiOutcome outcome) => new(outcome, []);
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
}
