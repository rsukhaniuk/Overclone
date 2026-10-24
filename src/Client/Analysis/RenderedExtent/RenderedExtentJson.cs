using System.Text;
using System.Text.Json;
using Client.Pdf;

namespace Client.Analysis.RenderedExtent;

/// <summary>Strict transport validation, not extent-policy evaluation. All strings
/// are bounded/canonical correlation or fixed metadata; no arbitrary message is
/// admitted. Rust alone supplies the usual range, reason and observation status.</summary>
public static class RenderedExtentJson
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] ObservationKeys = ["schemaVersion", "kind", "requestId", "pdfSha256", "pageCount",
        "profileId", "profileVersion", "ruleId", "evaluationDomain", "workType", "status", "reason", "usualMinimum",
        "usualMaximum", "nonAppendixPages", "mainBodyPages", "compliance", "guidelineSha256", "sourceSection", "printedPage", "sourceUrl"];

    public static string Pack(RenderedExtentRequest request)
    {
        if (request is null || !request.IsValid) throw new ArgumentException("Invalid rendered extent request.");
        var json = request.Sections is { } sections
            ? JsonSerializer.Serialize(new { schemaVersion = 2, requestId = request.RequestId, pdfSha256 = request.PdfSha256,
                pageCount = request.PageCount, workType = request.WorkType, sections = RenderedSectionJson.Pack(sections) })
            : JsonSerializer.Serialize(new { schemaVersion = 1, requestId = request.RequestId, pdfSha256 = request.PdfSha256,
                pageCount = request.PageCount, workType = request.WorkType });
        if (Utf8.GetByteCount(json) > RenderedExtentContract.RequestMaxBytes) throw new ArgumentException("Rendered extent request limit.");
        return json;
    }

    public static RenderedExtentObservation? Parse(RenderedExtentRequest expected, string? json)
    {
        if (expected is null || !expected.IsValid || json is null || json.Length > RenderedExtentContract.ResponseMaxBytes
            || !PdfHashes.ValidUtf16(json)) throw new JsonException("Invalid rendered extent response.");
        if (Utf8.GetByteCount(json) > RenderedExtentContract.ResponseMaxBytes) throw new JsonException("Rendered extent response limit.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = expected.Sections is null ? 4 : 6 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Expected observation object.");
        if (Text(root, "kind") == "error")
        {
            Keys(root, ["schemaVersion", "kind", "code"]);
            Require(Integer(root, "schemaVersion") == 1 && Text(root, "code") == "invalidInput");
            return null; // The ONLY admitted error response, never a partial observation.
        }
        bool classified = expected.Sections is not null;
        Keys(root, classified ? [.. ObservationKeys, "sections"] : ObservationKeys);
        Require(Integer(root, "schemaVersion") == (classified ? 2 : 1) && Text(root, "kind") == "observation"
            && Text(root, "requestId") == expected.RequestId && Text(root, "pdfSha256") == expected.PdfSha256
            && Integer(root, "pageCount") == expected.PageCount && Text(root, "workType") == expected.WorkType
            && Text(root, "profileId") == RenderedExtentContract.ProfileId && Text(root, "profileVersion") == (classified ? "2.0.0" : RenderedExtentContract.ProfileVersion)
            && Text(root, "ruleId") == RenderedExtentContract.RuleId && Text(root, "evaluationDomain") == RenderedExtentContract.Domain
            && Text(root, "guidelineSha256") == RenderedExtentContract.GuidelineSha256 && Text(root, "sourceSection") == "2"
            && Integer(root, "printedPage") == 11 && Text(root, "sourceUrl") == RenderedExtentContract.SourceUrl
            && Text(root, "compliance") == "notAssessed");
        RenderedSectionResults? sections = null;
        int? nonAppendix = null;
        if (classified) sections = RenderedSectionJson.Parse(root, expected, out nonAppendix);
        else Require(root.GetProperty("nonAppendixPages").ValueKind == JsonValueKind.Null
            && root.GetProperty("mainBodyPages").ValueKind == JsonValueKind.Null);
        string status = Text(root, "status"), reason = Text(root, "reason");
        int? minimum = OptionalInteger(root, "usualMinimum"), maximum = OptionalInteger(root, "usualMaximum");
        // Validate discriminator/nullable shape only. Do NOT derive ranges or
        // compare pageCount against a threshold here (that is the Rust policy).
        if (expected.WorkType == "unspecified")
            Require(status == "indeterminate" && reason == "workTypeRequired" && minimum is null && maximum is null);
        else
        {
            Require(minimum is >= 1 and <= 512 && maximum is >= 1 and <= 512 && maximum >= minimum);
            Require(classified
                ? status == "belowUsualMinimum" && reason == "classifiedNonAppendixBelowUsualMinimum"
                    || status == "aboveUsualMaximum" && reason == "classifiedNonAppendixAboveUsualMaximum"
                    || status == "withinUsualRange" && reason == "classifiedNonAppendixWithinUsualRange"
                    || status == "indeterminate" && reason == "nonAppendixBoundaryUnknown"
                : status == "belowUsualMinimum" && reason == "totalBelowUsualNonAppendixMinimum"
                    || status == "indeterminate" && reason == "nonAppendixBoundaryUnknown");
        }
        return new(expected, status, reason, minimum, maximum, nonAppendix, sections);
    }

    internal static void Keys(JsonElement element, string[] expected)
    {
        Require(element.ValueKind == JsonValueKind.Object);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            Require(seen.Add(property.Name) && expected.Contains(property.Name, StringComparer.Ordinal));
        Require(seen.Count == expected.Length);
    }
    internal static string Text(JsonElement element, string name)
    {
        Require(element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String);
        string text;
        try { text = value.GetString() ?? throw new JsonException(); }
        catch (InvalidOperationException) { throw new JsonException("Invalid protocol string."); }
        Require(PdfHashes.ValidUtf16(text));
        return text;
    }
    internal static int Integer(JsonElement element, string name)
    {
        Require(element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _));
        return value.GetInt32();
    }
    internal static int? OptionalInteger(JsonElement element, string name) =>
        element.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Integer(element, name);
    internal static void Require(bool valid) { if (!valid) throw new JsonException("Invalid rendered extent protocol."); }
}
