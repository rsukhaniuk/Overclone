using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Client.Pdf;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PdfWireReply
{
    public int Version { get; set; }
    public string RequestId { get; set; } = "";
    public string DocumentSha256 { get; set; } = "";
    public long Epoch { get; set; }
    public string LoaderSha256 { get; set; } = "";
    public string WasmSha256 { get; set; } = "";
    public string? ActionState { get; set; }
    public bool HasCapabilitiesApi { get; set; }
    public bool HasActionsApi { get; set; }
    public string? CapabilitiesJson { get; set; }
    public string? ActionsJson { get; set; }
    public string Status { get; set; } = "";
    public int? NativeStatus { get; set; }
    public uint NativeError { get; set; }
    public bool? EnteredNative { get; set; }
    public int LiveAfter { get; set; }
    public int PageCount { get; set; }
    public string? FactsJson { get; set; }
    public string? TextJson { get; set; }
    public string? SearchJson { get; set; }
    public byte[]? Raster { get; set; }
    public string[] NativeCalls { get; set; } = [];
}

public static class PdfWireParser
{
    public static PdfInspectionOutcome Parse(PdfWireReply wire, PdfInspectRequest request, string id, long epoch, PdfRuntimeIdentity runtime)
    {
        PdfInspectionOutcome Invalid() => new(PdfInspectionStatus.InvalidResponse, id, request.Document.Sha256, epoch, Code: "InvalidResponse", Runtime: runtime);
        try
        {
            if (wire is null || wire.Version != 2 || wire.LoaderSha256 != runtime.LoaderSha256 || wire.WasmSha256 != runtime.WasmSha256 || wire.RequestId != id || wire.DocumentSha256 != request.Document.Sha256 || wire.Epoch != epoch
                || epoch is < 1 or > 9007199254740991 || wire.LiveAfter != 0 || wire.NativeCalls is null || wire.NativeCalls.Length > 32
                || wire.NativeCalls.Any(s => s is null || s.Length > 64)) return Invalid();
            var calls = Array.AsReadOnly((string[])wire.NativeCalls.Clone());
            var status = wire.Status switch
            {
                "completed" => PdfInspectionStatus.Completed,
                "native-failure" => PdfInspectionStatus.NativeFailure,
                "output-limit" => PdfInspectionStatus.OutputLimit,
                "cancelled" => PdfInspectionStatus.Cancelled,
                "disposed" => PdfInspectionStatus.Disposed,
                "timed-out" => PdfInspectionStatus.TimedOut,
                "unavailable" => PdfInspectionStatus.Unavailable,
                "transport-error" => PdfInspectionStatus.TransportError,
                _ => PdfInspectionStatus.InvalidResponse
            };
            if (status == PdfInspectionStatus.InvalidResponse) return Invalid();
            if (status != PdfInspectionStatus.Completed)
            {
                if (wire.ActionState is not null || wire.CapabilitiesJson is not null || wire.ActionsJson is not null || wire.HasActionsApi || wire.HasCapabilitiesApi || wire.FactsJson is not null || wire.TextJson is not null || wire.SearchJson is not null || wire.Raster is not null || wire.PageCount != 0) return Invalid();
                if (status == PdfInspectionStatus.NativeFailure && (wire.NativeStatus is < 1 or > 8 || wire.NativeStatus is null || wire.EnteredNative != true)) return Invalid();
                if (status != PdfInspectionStatus.NativeFailure && wire.NativeStatus is not null) return Invalid();
                return new(status, id, request.Document.Sha256, epoch, wire.NativeStatus, wire.NativeError, wire.EnteredNative, Code: wire.Status, NativeCalls: calls, Runtime: runtime);
            }
            if (wire.NativeStatus != 0 || wire.NativeError != 0 || wire.EnteredNative != true || wire.PageCount < 1 || wire.PageCount > request.Budget.Pages || request.PageIndex >= wire.PageCount) return Invalid();
            string[] required = ["e1_version", "e1_open", "e1_page", "e1_facts", "e1_text", "e1_reset"];
            if (required.Any(s => !calls.Contains(s)) || request.Search is not null && !calls.Contains("e1_search") || request.Raster && !calls.Contains("e1_raster")) return Invalid();
            long total = wire.Raster?.Length ?? 0;
            foreach (var value in new[] { wire.FactsJson, wire.TextJson, wire.SearchJson })
            {
                if (value is null || value.Length > request.Budget.OutputBytes) return Invalid();
                total += Encoding.UTF8.GetByteCount(value);
            }
            foreach (var value in new[] { wire.CapabilitiesJson, wire.ActionsJson })
            {
                if (value is null) continue;
                if (value.Length > request.Budget.OutputBytes) return Invalid();
                total += Encoding.UTF8.GetByteCount(value);
            }
            if (total > request.Budget.OutputBytes) return Invalid();
            var actions = PdfActionsParser.Parse(wire, request);
            using var facts = JsonDocument.Parse(wire.FactsJson!); using var text = JsonDocument.Parse(wire.TextJson!); using var search = JsonDocument.Parse(wire.SearchJson!);
            Unique(facts.RootElement); Unique(text.RootElement); Unique(search.RootElement);
            var f = facts.RootElement; Keys(f, "media", "crop", "cropDefaulted", "effective", "rotation", "displayPoints", "nativeWidthHeight", "sourceMapping");
            var media = Box(f.GetProperty("media")); var cropJson = f.GetProperty("crop"); PdfBox? crop = cropJson.ValueKind == JsonValueKind.Null ? null : Box(cropJson);
            bool defaulted = f.GetProperty("cropDefaulted").GetBoolean(); if (defaulted != (crop is null)) return Invalid();
            var effective = Box(f.GetProperty("effective")); int rotation = f.GetProperty("rotation").GetInt32();
            if (rotation is not (0 or 90 or 180 or 270) || f.GetProperty("sourceMapping").GetString() != "unsupported") return Invalid();
            var display = Point(f.GetProperty("displayPoints")); var native = Point(f.GetProperty("nativeWidthHeight"));
            if (display.X <= 0 || display.Y <= 0 || native.X <= 0 || native.Y <= 0) return Invalid();
            var t = text.RootElement; Keys(t, "text", "utf16", "characterCount", "characters", "sourceMapping");
            string content = t.GetProperty("text").GetString() ?? throw new JsonException();
            if (!PdfHashes.ValidUtf16(content) || content.Length > request.Budget.TextUnits || t.GetProperty("sourceMapping").GetString() != "unsupported") return Invalid();
            var unitsJson = t.GetProperty("utf16"); var charsJson = t.GetProperty("characters"); int count = t.GetProperty("characterCount").GetInt32();
            if (unitsJson.ValueKind != JsonValueKind.Array || unitsJson.GetArrayLength() != content.Length || charsJson.ValueKind != JsonValueKind.Array
                || count < 0 || count > request.Budget.TextUnits || charsJson.GetArrayLength() != count) return Invalid();
            int[] units = unitsJson.EnumerateArray().Select(v => v.GetInt32()).ToArray();
            if (units.Where((unit, index) => unit is < 0 or > 65535 || unit != content[index]).Any()) return Invalid();
            var glyphs = new PdfGlyph[count]; int i = 0;
            foreach (var g in charsJson.EnumerateArray())
            {
                Keys(g, "index", "unicode", "origin", "box", "displayBox", "boxStatus", "font", "fontStatus");
                int index = g.GetProperty("index").GetInt32(), unicode = g.GetProperty("unicode").GetInt32();
                if (index != i || unicode is < 0 or > 1114111) return Invalid();
                var origin = g.GetProperty("origin"); var box = g.GetProperty("box"); var device = g.GetProperty("displayBox");
                string boxStatus = g.GetProperty("boxStatus").GetString() ?? "", font = g.GetProperty("font").GetString() ?? "", fontStatus = g.GetProperty("fontStatus").GetString() ?? "";
                if (boxStatus != (box.ValueKind == JsonValueKind.Null ? "indeterminate" : "available") || font.Length > 1024 || fontStatus != (font.Length == 0 ? "indeterminate" : "available")) return Invalid();
                glyphs[i++] = new(index, unicode, origin.ValueKind == JsonValueKind.Null ? null : Point(origin), box.ValueKind == JsonValueKind.Null ? null : Box(box),
                    device.ValueKind == JsonValueKind.Null ? null : DisplayBox(device), boxStatus, font, fontStatus);
            }
            var hitsJson = search.RootElement;
            if (hitsJson.ValueKind != JsonValueKind.Array || hitsJson.GetArrayLength() > request.Budget.SearchHits) return Invalid();
            var hits = new List<PdfSearchHit>();
            foreach (var hit in hitsJson.EnumerateArray())
            {
                Keys(hit, "start", "count"); int start = hit.GetProperty("start").GetInt32(), length = hit.GetProperty("count").GetInt32();
                if (start < 0 || length <= 0 || (long)start + length > count || request.Search is null) return Invalid(); hits.Add(new(start, length));
            }
            byte[] pixels = []; int width = 0, height = 0;
            if (request.Raster)
            {
                var bytes = wire.Raster; if (bytes is null || bytes.Length < 16) return Invalid();
                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes), w = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)), h = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)), stride = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
                if (magic != 0x31424752 || w != request.RasterWidth || h != request.RasterHeight || stride != (long)w * 4 || (long)w * h > request.Budget.RasterPixels || (long)w * h * 4 + 16 != bytes.Length) return Invalid();
                width = (int)w; height = (int)h; pixels = bytes.AsSpan(16).ToArray();
            }
            else if (wire.Raster is not null) return Invalid();
            var page = new PdfPageInspection(wire.PageCount, request.PageIndex, media, crop, defaulted, effective, rotation, display, native,
                content, units, glyphs, hits.ToArray(), pixels, width, height);
            return new(PdfInspectionStatus.Completed, id, request.Document.Sha256, epoch, 0, 0, true, page, NativeCalls: calls, Runtime: runtime, Actions: actions);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException) { return Invalid(); }
    }
    internal static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject()) { if (!seen.Add(p.Name)) throw new JsonException("Duplicate native field."); Unique(p.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
    internal static void Keys(JsonElement value, params string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(keys)) throw new JsonException("Native schema.");
    }
    private static double Number(JsonElement e) { double v = e.GetDouble(); if (!double.IsFinite(v) || Math.Abs(v) > 1e9) throw new JsonException("Native number."); return v; }
    private static PdfPoint Point(JsonElement e) { if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 2) throw new JsonException(); return new(Number(e[0]), Number(e[1])); }
    private static PdfDisplayBox DisplayBox(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 4) throw new JsonException();
        var box = new PdfDisplayBox(Number(e[0]), Number(e[1]), Number(e[2]), Number(e[3])); if (box.Left > box.Right || box.Top > box.Bottom) throw new JsonException(); return box;
    }
    internal static PdfBox Box(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 4) throw new JsonException();
        var box = new PdfBox(Number(e[0]), Number(e[1]), Number(e[2]), Number(e[3])); if (box.Left > box.Right || box.Bottom > box.Top) throw new JsonException(); return box;
    }
}
