using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Client.Compilation.SyncTeX;

// Transport DTO only. Every field, including native numeric status, is untrusted.
public sealed class SyncTeXWireReply
{
    public int Version { get; set; }
    public string? RequestId { get; set; }
    public string? Status { get; set; }
    public int? NativeStatus { get; set; }
    public byte[]? Json { get; set; }
}

internal static class SyncTeXWire
{
    internal const int MapLimit = 1048575, OutputLimit = 32768, DeadlineMs = 15000;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static SyncTeXQueryOutcome End(SyncTeXQueryStatus status, string code = "") =>
        new(status, Array.Empty<SyncTeXMatch>(), Code: code);
    internal static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static string Digest(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    internal static bool Coordinate(double value) => double.IsFinite(value) && Math.Abs(value) <= 1000000;

    internal static bool Name(string? value, bool relative)
    {
        // Bound UTF-16 before the UTF-8 walk/encoding; reject lone surrogates.
        if (value is null || value.Length is < 1 or > 1024) return false;
        try { if (Utf8.GetByteCount(value) > 1024) return false; }
        catch (EncoderFallbackException) { return false; }
        foreach (var c in value)
            if (c < 0x20 || c is >= '\u007f' and <= '\u009f' or '\u2028' or '\u2029') return false;
        if (!relative) return true; // Absolute/untrusted names remain DATA, not filesystem authority.
        if (value.Contains('\\') || value.Contains(':')) return false;
        foreach (var part in value.Split('/')) if (part is "" or "." or "..") return false;
        return true;
    }

    internal static bool Capture(SyncTeXQueryRequest? request, out SyncTeXQueryRequest? owned)
    {
        owned = null;
        if (request?.Map is not { Length: >= 1 and <= MapLimit } || !Hash(request.MapSha256) ||
            !Coordinate(request.H) || !Coordinate(request.V)) return false;
        switch (request.Direction)
        {
            case SyncTeXDirection.Forward:
                if (!Name(request.File, true) || request.Line <= 0 || request.Page is < 0 or > 512) return false;
                break;
            case SyncTeXDirection.Inverse:
                if (request.File is not null || request.Line != 0 || request.Page is < 1 or > 512) return false;
                break;
            default: return false;
        }
        // Never hash caller-owned mutable memory and then dispatch a different copy.
        var map = request.Map.ToArray();
        if (Digest(map) != request.MapSha256) return false;
        owned = request with { Map = map };
        return true; // Canonical LF magic/no-NUL and format parsing belong to native ABI.
    }

    internal static SyncTeXQueryOutcome Parse(SyncTeXWireReply? wire, string id, SyncTeXDirection direction)
    {
        var invalid = End(SyncTeXQueryStatus.InvalidResponse, "InvalidResponse");
        if (wire is null || wire.Version != 1 || wire.RequestId is not { Length: 32 } || wire.RequestId != id ||
            wire.Status is null || wire.Status.Length > 32 || wire.Json is { Length: > OutputLimit }) return invalid;
        if (wire.Status != "native")
        {
            if (wire.NativeStatus is not null || wire.Json is not null) return invalid;
            return wire.Status switch
            {
                "unavailable" => End(SyncTeXQueryStatus.Unavailable, "WorkerUnavailable"),
                "busy" => End(SyncTeXQueryStatus.Busy, "Busy"),
                "cancelled" => End(SyncTeXQueryStatus.Cancelled, "Cancelled"),
                "timed-out" => End(SyncTeXQueryStatus.TimedOut, "Deadline"),
                "disposed" => End(SyncTeXQueryStatus.Disposed, "Disposed"),
                "failed" => End(SyncTeXQueryStatus.Failed, "TransportFailed"),
                _ => invalid
            };
        }
        if (wire.NativeStatus is null or < -8 or > 1) return invalid;
        if (wire.NativeStatus != 0)
        {
            if (wire.Json is not null) return invalid;
            return wire.NativeStatus == 1 ? End(SyncTeXQueryStatus.NoMatches, "NoMatches")
                : End(SyncTeXQueryStatus.Failed, "NativeRejected");
        }
        if (wire.Json is not { Length: >= 1 and <= OutputLimit } bytes) return invalid;
        try
        {
            // Strict byte decode before JsonDocument (which may otherwise replace invalid UTF-8).
            string json = Utf8.GetString(bytes);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            Fields(root, "schema", "direction", "coordinateSpace", "retainedCount", "truncated", "matches");
            if (Integer(root, "schema") != 1 || Text(root, "direction", 7) != (direction == SyncTeXDirection.Forward ? "forward" : "inverse") ||
                Text(root, "coordinateSpace", 30) != "synctex-visible-bp-top-left") return invalid;
            int count = Integer(root, "retainedCount");
            var matches = root.GetProperty("matches");
            if (count is < 1 or > 32 || matches.ValueKind != JsonValueKind.Array || matches.GetArrayLength() != count) return invalid;
            var truncated = root.GetProperty("truncated").GetBoolean();
            var result = new SyncTeXMatch[count];
            int i = 0;
            foreach (var match in matches.EnumerateArray())
            {
                Fields(match, "file", "tag", "line", "column", "page", "point", "box");
                string file = Text(match, "file", 1024);
                if (!Name(file, false)) return invalid;
                int tag = Integer(match, "tag"), line = Integer(match, "line"), page = Integer(match, "page");
                var columnValue = match.GetProperty("column");
                int? column = columnValue.ValueKind == JsonValueKind.Null ? null : columnValue.GetInt32();
                if (tag <= 0 || line <= 0 || column is <= 0 || page is < 1 or > 512) return invalid;
                var point = match.GetProperty("point"); var box = match.GetProperty("box");
                Fields(point, "h", "v"); Fields(box, "h", "v", "width", "height", "depth");
                result[i++] = new(file, tag, line, column, page,
                    new(Number(point, "h"), Number(point, "v")),
                    new(Number(box, "h"), Number(box, "v"), Number(box, "width", true), Number(box, "height", true), Number(box, "depth", true)));
            }
            return new(SyncTeXQueryStatus.Completed, Array.AsReadOnly(result), truncated);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { return invalid; }
    }

    private static void Fields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException();
        // Both duplicates and unknown fields fail; enumeration bounded by output byte cap.
        int seen = 0;
        foreach (var property in value.EnumerateObject())
        {
            int index = Array.IndexOf(names, property.Name);
            if (index < 0 || (seen & (1 << index)) != 0) throw new FormatException();
            seen |= 1 << index;
        }
        if (seen != (1 << names.Length) - 1) throw new FormatException();
    }
    private static int Integer(JsonElement value, string key) => value.GetProperty(key).GetInt32();
    private static string Text(JsonElement value, string key, int limit)
    {
        var item = value.GetProperty(key);
        // JSON itself is already capped before any string allocation.
        string text = item.GetString() ?? throw new FormatException();
        if (text.Length > limit) throw new FormatException();
        return text;
    }
    private static double Number(JsonElement value, string key, bool dimension = false)
    {
        double number = value.GetProperty(key).GetDouble();
        if (!Coordinate(number) || (dimension && number < 0)) throw new FormatException();
        return number;
    }
}
