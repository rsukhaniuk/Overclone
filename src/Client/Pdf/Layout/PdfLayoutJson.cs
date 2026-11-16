using System.Text;
using System.Text.Json;

namespace Client.Pdf;

// Do not deserialize native JSON into permissive records: duplicate/extra fields,
// invalid scalar indexes and status/value contradictions are transport failures.
public static class PdfLayoutJson
{
    public static (PdfLayoutCapabilities Capabilities, PdfLayoutFacts Facts) Parse(string capabilitiesJson,
        string factsJson, PdfLayoutRequest request)
    {
        if (Encoding.UTF8.GetByteCount(capabilitiesJson) > 4096 || Encoding.UTF8.GetByteCount(factsJson) > request.Budget.OutputBytes)
            throw new JsonException("Layout output limit.");
        using var capabilityDoc = JsonDocument.Parse(capabilitiesJson);
        using var factDoc = JsonDocument.Parse(factsJson);
        PdfWireParser.Unique(capabilityDoc.RootElement); PdfWireParser.Unique(factDoc.RootElement);
        var c = capabilityDoc.RootElement;
        PdfWireParser.Keys(c, "schemaVersion", "coordinateSpace", "physicalScale", "glyphInk", "textStyle", "lineGrouping", "maxCharacters", "nominalSizeUnit", "nominalSizeMethod");
        if (c.GetProperty("schemaVersion").GetInt32() != 1 || Text(c, "coordinateSpace") != "pdf-raw-unrotated-bottom-left"
            || Text(c, "lineGrouping") != "unsupported" || c.GetProperty("maxCharacters").GetInt32() != 100000
            || Text(c, "nominalSizeUnit") != "raw-user-units" || Text(c, "nominalSizeMethod") != "pdfium-font-size") throw new JsonException();
        var capabilities = new PdfLayoutCapabilities(1, Text(c, "coordinateSpace"), Capability(c, "physicalScale"),
            Capability(c, "glyphInk"), Capability(c, "textStyle"), "unsupported", 100000, "raw-user-units", "pdfium-font-size");
        var f = factDoc.RootElement;
        PdfWireParser.Keys(f, "schemaVersion", "pageIndex", "pageCount", "media", "crop", "effective", "rotation", "userUnit", "characters", "complete", "limitations");
        int index = f.GetProperty("pageIndex").GetInt32(), pages = f.GetProperty("pageCount").GetInt32(), rotation = f.GetProperty("rotation").GetInt32();
        if (f.GetProperty("schemaVersion").GetInt32() != 1 || index != request.PageIndex || pages < 1 || pages > request.Budget.Pages
            || index >= pages || rotation is not (0 or 90 or 180 or 270) || !f.GetProperty("complete").GetBoolean()) throw new JsonException();
        var media = Box(f.GetProperty("media")); var effective = Box(f.GetProperty("effective"));
        PdfBox? crop = f.GetProperty("crop").ValueKind == JsonValueKind.Null ? null : Box(f.GetProperty("crop"));
        if (effective.Left != Math.Max(media.Left, crop?.Left ?? media.Left) || effective.Bottom != Math.Max(media.Bottom, crop?.Bottom ?? media.Bottom)
            || effective.Right != Math.Min(media.Right, crop?.Right ?? media.Right) || effective.Top != Math.Min(media.Top, crop?.Top ?? media.Top)
            || effective.Left >= effective.Right || effective.Bottom >= effective.Top) throw new JsonException();
        var u = f.GetProperty("userUnit"); PdfWireParser.Keys(u, "status", "value", "method");
        string state = FactStatus(u, "status"), method = Text(u, "method"); double? scale = NullableNumber(u.GetProperty("value"));
        if (state == "available" ? scale is not > 0 || scale > 75000 || method is not ("page-dictionary-userunit" or "default-userunit") || method == "default-userunit" && scale != 1
            : scale is not null || method is not ("unavailable" or "invalid")) throw new JsonException();
        if (state == "available" && capabilities.PhysicalScale != "available" || capabilities.PhysicalScale == "available" && state is "unsupported" or "missing") throw new JsonException();
        var array = f.GetProperty("characters");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > Math.Min(100000, request.Budget.TextUnits)) throw new JsonException();
        var chars = new PdfLayoutCharacter[array.GetArrayLength()]; int i = 0;
        foreach (var ch in array.EnumerateArray())
        {
            PdfWireParser.Keys(ch, "index", "unicode", "origin", "advance", "matrix", "nominalSize", "fontName", "fontFlags", "renderMode", "inkBox", "inkStatus", "styleStatus");
            int nativeIndex = ch.GetProperty("index").GetInt32(), scalar = ch.GetProperty("unicode").GetInt32();
            if (nativeIndex != i || scalar is < 0 or > 0x10ffff or >= 0xd800 and <= 0xdfff) throw new JsonException();
            string ink = FactStatus(ch, "inkStatus"), style = FactStatus(ch, "styleStatus");
            var boxJson = ch.GetProperty("inkBox"); PdfBox? inkBox = boxJson.ValueKind == JsonValueKind.Null ? null : Box(boxJson);
            if ((ink == "available") != (inkBox is not null) || ink == "available" && capabilities.GlyphInk != "available"
                || style == "available" && capabilities.TextStyle != "available") throw new JsonException();
            var fontJson = ch.GetProperty("fontName"); string? font = fontJson.ValueKind == JsonValueKind.Null ? null : fontJson.GetString();
            if (font is not null && (!PdfHashes.ValidUtf16(font) || Encoding.UTF8.GetByteCount(font) > 256 || font.Contains('\0'))) throw new JsonException();
            var flagsJson = ch.GetProperty("fontFlags"); uint? flags = flagsJson.ValueKind == JsonValueKind.Null ? null : flagsJson.GetUInt32();
            var modeJson = ch.GetProperty("renderMode"); int? mode = modeJson.ValueKind == JsonValueKind.Null ? null : modeJson.GetInt32();
            double? size = NullableNumber(ch.GetProperty("nominalSize")); if (size is < 0 || mode is < 0 or > 7) throw new JsonException();
            var matrixJson = ch.GetProperty("matrix"); PdfCharacterMatrix? matrix = null;
            if (matrixJson.ValueKind != JsonValueKind.Null)
            { var a = Numbers(matrixJson, 6); matrix = new(a[0], a[1], a[2], a[3], a[4], a[5]); }
            if (style == "available" && (matrix is null || size is null || flags is null || mode is null)) throw new JsonException();
            chars[i++] = new(nativeIndex, scalar, Point(ch.GetProperty("origin")), Point(ch.GetProperty("advance")), matrix,
                size, font, flags, mode, inkBox, ink, style);
        }
        var limitationsJson = f.GetProperty("limitations");
        if (limitationsJson.ValueKind != JsonValueKind.Array || limitationsJson.GetArrayLength() > 32) throw new JsonException();
        var limitations = limitationsJson.EnumerateArray().Select(p => p.GetString() ?? throw new JsonException()).ToArray();
        if (limitations.Any(p => p.Length is < 1 or > 64 || p.Any(c => c is < '!' or > '~')) || limitations.Distinct().Count() != limitations.Length) throw new JsonException();
        return (capabilities, new(index, pages, media, crop, effective, rotation, new(state, scale, method), chars, limitations, factsJson));
    }
    private static string Text(JsonElement e, string name) => e.GetProperty(name).GetString() ?? throw new JsonException();
    private static string Capability(JsonElement e, string name) => Text(e, name) is var s && s is "available" or "unsupported" or "indeterminate" ? s : throw new JsonException();
    private static string FactStatus(JsonElement e, string name) => Text(e, name) is var s && s is "available" or "unsupported" or "indeterminate" or "missing" ? s : throw new JsonException();
    private static double Number(JsonElement e) { double n = e.GetDouble(); return double.IsFinite(n) && Math.Abs(n) <= 1000000 ? n : throw new JsonException(); }
    private static double? NullableNumber(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : Number(e);
    private static double[] Numbers(JsonElement e, int count)
    { if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != count) throw new JsonException(); return e.EnumerateArray().Select(Number).ToArray(); }
    private static PdfPoint? Point(JsonElement e) { if (e.ValueKind == JsonValueKind.Null) return null; var a = Numbers(e, 2); return new(a[0], a[1]); }
    private static PdfBox Box(JsonElement e) { var a = Numbers(e, 4); if (a[0] > a[2] || a[1] > a[3]) throw new JsonException(); return new(a[0], a[1], a[2], a[3]); }
}
