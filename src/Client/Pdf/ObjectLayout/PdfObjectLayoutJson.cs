using System.Text;
using System.Text.Json;

namespace Client.Pdf;

// Strict native schema 1. Keep authored semantic claims outside the transport boundary.
internal static class PdfObjectLayoutJson
{
    internal const int MaxOutputBytes = 1024 * 1024;
    private const int MaxObjects = 10000, MaxImages = 512;
    private const double FloatEpsilon = 1.1920928955078125e-7;
    private const double DoubleEpsilon = 2.2204460492503131e-16;
    private static readonly string[] RootLimitations = ["direct-page-objects-only", "native-parsed-inventory-not-content-validation",
        "image-placement-not-ink", "image-visibility-not-established", "object-role-unestablished",
        "caption-association-unestablished", "nested-forms-not-inspected", "clip-or-compositing-uncertain-refused", PdfImagePlacement.Limitation];

    internal static PdfObjectLayoutFacts Parse(string json, PdfInspectRequest request, int pageCount)
    {
        int cap = Math.Min(MaxOutputBytes, request.Budget.OutputBytes);
        if (json.Length > cap || !PdfHashes.ValidUtf16(json) || Encoding.UTF8.GetByteCount(json) > cap)
            throw new JsonException("Object layout output limit.");
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        PdfWireParser.Unique(root);
        PdfWireParser.Keys(root, "schemaVersion", "coordinateSpace", "scope", "pageIndex", "pageCount", "media", "crop", "effective",
            "rotation", "userUnit", "directObjectCount", "objectTypeCounts", "images", "scanComplete", "limitations");
        int index = root.GetProperty("pageIndex").GetInt32(), pages = root.GetProperty("pageCount").GetInt32();
        int rotation = root.GetProperty("rotation").GetInt32();
        if (root.GetProperty("schemaVersion").GetInt32() != 1 || Text(root, "coordinateSpace") != "pdf-raw-unrotated-bottom-left"
            || Text(root, "scope") != "direct-page-image-placement" || !root.GetProperty("scanComplete").GetBoolean()
            || index != request.PageIndex || pages != pageCount || index < 0 || pages < 1 || index >= pages || pages > request.Budget.Pages
            || rotation is not (0 or 90 or 180 or 270)) throw new JsonException();
        var media = Box(root.GetProperty("media"));
        var cropJson = root.GetProperty("crop");
        PdfBox? crop = cropJson.ValueKind == JsonValueKind.Null ? null : Box(cropJson);
        var effective = Box(root.GetProperty("effective"));
        if (effective != new PdfBox(Math.Max(media.Left, crop?.Left ?? media.Left), Math.Max(media.Bottom, crop?.Bottom ?? media.Bottom),
            Math.Min(media.Right, crop?.Right ?? media.Right), Math.Min(media.Top, crop?.Top ?? media.Top))
            || effective.Left >= effective.Right || effective.Bottom >= effective.Top) throw new JsonException();
        var unit = UserUnit(root.GetProperty("userUnit"));
        int count = Count(root, "directObjectCount", MaxObjects);
        var types = ObjectCounts(root.GetProperty("objectTypeCounts"), count);
        var rows = root.GetProperty("images");
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != types.Image) throw new JsonException();
        var images = new PdfImagePlacement[types.Image];
        int i = 0, previous = -1;
        foreach (var row in rows.EnumerateArray())
        {
            var image = Image(row, effective, previous, count);
            images[i++] = image;
            previous = image.ObjectIndex;
        }
        Limits(root.GetProperty("limitations"), RootLimitations);
        return new(index, pages, media, crop, effective, rotation, unit, count, types, images, RootLimitations, json, PdfHashes.Of(bytes));
    }

    private static PdfUserUnit UserUnit(JsonElement unit)
    {
        PdfWireParser.Keys(unit, "status", "value", "method");
        string status = Text(unit, "status"), method = Text(unit, "method");
        var value = unit.GetProperty("value");
        if (status == "indeterminate" && method == "invalid" && value.ValueKind == JsonValueKind.Null)
            return new(status, null, method);
        if (status != "available" || method is not ("default-userunit" or "page-dictionary-userunit")) throw new JsonException();
        double scale = Number(value);
        if (scale <= 0 || scale > 75000 || method == "default-userunit" && scale != 1) throw new JsonException();
        return new(status, scale, method);
    }

    private static PdfDirectObjectCounts ObjectCounts(JsonElement types, int count)
    {
        PdfWireParser.Keys(types, "text", "path", "image", "shading", "form", "unknown");
        var result = new PdfDirectObjectCounts(Count(types, "text", MaxObjects), Count(types, "path", MaxObjects),
            Count(types, "image", MaxImages), Count(types, "shading", MaxObjects), Count(types, "form", MaxObjects), Count(types, "unknown", MaxObjects));
        if (result.Text + result.Path + result.Image + result.Shading + result.Form + result.Unknown != count) throw new JsonException();
        return result;
    }

    private static PdfImagePlacement Image(JsonElement row, PdfBox effective, int previous, int count)
    {
        PdfWireParser.Keys(row, "objectIndex", "active", "status", "method", "matrix", "quad", "rawBox", "uncertainty", "reason", "limitations");
        int index = row.GetProperty("objectIndex").GetInt32();
        bool active = row.GetProperty("active").GetBoolean();
        string status = Text(row, "status"), reason = Text(row, "reason");
        if (index <= previous || index >= count || Text(row, "method") != PdfImagePlacement.Method) throw new JsonException();
        Limits(row.GetProperty("limitations"), [PdfImagePlacement.Limitation]);
        var matrix = row.GetProperty("matrix"); var quad = row.GetProperty("quad");
        var box = row.GetProperty("rawBox"); var uncertainty = row.GetProperty("uncertainty");
        if (status == "indeterminate")
        {
            bool known = reason is "inactive-object" or "object-geometry-out-of-domain" or "invalid-image-dimensions" or
                "page-compositing-group" or "clip-path-present" or "optional-content-unresolved" or "image-mask-or-softmask" or
                "nondefault-compositing" or "invalid-object-matrix" or "crosses-effective-page";
            if (!known || active == (reason == "inactive-object") || matrix.ValueKind != JsonValueKind.Null || quad.ValueKind != JsonValueKind.Null
                || box.ValueKind != JsonValueKind.Null || uncertainty.ValueKind != JsonValueKind.Null) throw new JsonException();
            return new(index, active, status, reason, null, null, null, null);
        }
        if (status != "available" || !active || reason != "parsed-native-image-matrix") throw new JsonException();
        var m = Numbers(matrix, 6);
        var parsedMatrix = new PdfImageMatrix(m[0], m[1], m[2], m[3], m[4], m[5]);
        var (parsedQuad, parsedBox, parsedUncertainty) = Geometry(parsedMatrix, quad, box, uncertainty, effective);
        return new(index, active, status, reason, parsedMatrix, parsedQuad, parsedBox, parsedUncertainty);
    }

    private static (PdfImageQuad Quad, PdfBox Box, PdfImagePlacementUncertainty Error) Geometry(
        PdfImageMatrix matrix, JsonElement quad, JsonElement box, JsonElement uncertainty, PdfBox effective)
    {
        var (a, b, c, d, e, f) = matrix;
        double determinant = a * d - b * c;
        double determinantError = 16 * FloatEpsilon * Math.Max(1, Math.Abs(a * d) + Math.Abs(b * c));
        if (Math.Abs(determinant) <= determinantError || quad.ValueKind != JsonValueKind.Array || quad.GetArrayLength() != 4)
            throw new JsonException();
        var expectedQuad = new PdfImageQuad(new(e, f), new(a + e, b + f), new(a + c + e, b + d + f), new(c + e, d + f));
        var q = new PdfImageQuad(Point(quad[0]), Point(quad[1]), Point(quad[2]), Point(quad[3]));
        if (q != expectedQuad) throw new JsonException();
        PdfPoint[] points = [q.P00, q.P10, q.P11, q.P01];
        var actual = Box(box);
        if (actual != new PdfBox(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y)))
            throw new JsonException();
        PdfWireParser.Keys(uncertainty, "absoluteErrorRaw", "method");
        string method = Text(uncertainty, "method");
        double error = Number(uncertainty.GetProperty("absoluteErrorRaw"));
        double magnitude = Math.Max(1, Math.Max(Math.Abs(a) + Math.Abs(c) + Math.Abs(e), Math.Abs(b) + Math.Abs(d) + Math.Abs(f)));
        double expectedError = (8 * FloatEpsilon + 16 * DoubleEpsilon) * magnitude;
        if (method != "pdfium-float32-matrix-bound" || error != expectedError
            || actual.Left - error < effective.Left || actual.Bottom - error < effective.Bottom
            || actual.Right + error > effective.Right || actual.Top + error > effective.Top) throw new JsonException();
        return (q, actual, new(error, method));
    }

    private static void Limits(JsonElement value, string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != expected.Length
            || !value.EnumerateArray().Select(x => x.GetString()).SequenceEqual(expected)) throw new JsonException();
    }
    private static string Text(JsonElement element, string key) => element.GetProperty(key).GetString() ?? throw new JsonException();
    private static int Count(JsonElement element, string key, int maximum)
        => element.GetProperty(key).GetInt32() is var n && n >= 0 && n <= maximum ? n : throw new JsonException();
    private static double Number(JsonElement element)
        => element.GetDouble() is var n && double.IsFinite(n) && Math.Abs(n) <= 1000000 ? n : throw new JsonException();
    private static double[] Numbers(JsonElement value, int count)
        => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == count
            ? value.EnumerateArray().Select(Number).ToArray() : throw new JsonException();
    private static PdfPoint Point(JsonElement element) { var xy = Numbers(element, 2); return new(xy[0], xy[1]); }
    private static PdfBox Box(JsonElement element)
    {
        var b = Numbers(element, 4);
        if (b[0] > b[2] || b[1] > b[3]) throw new JsonException();
        return new(b[0], b[1], b[2], b[3]);
    }
}
