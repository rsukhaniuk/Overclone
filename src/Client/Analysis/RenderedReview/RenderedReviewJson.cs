using Client.Analysis.RenderedExtent;
using Client.Pdf;
using System.Text;
using System.Text.Json;

namespace Client.Analysis.RenderedReview;

public static class RenderedReviewJson
{
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    // Profile wire vocabulary, not a second threshold/evaluation table.
    private static readonly string[] Ids = ["KNU-PAPER-001", "KNU-MARGINS-001", "KNU-TYPOGRAPHY-001", "KNU-PARAGRAPH-RENDERED-001",
        "KNU-HEADINGS-RENDERED-001", "KNU-PAGINATION-001", "KNU-STRUCTURE-001", "KNU-ABSTRACT-001", "KNU-LENGTH-001",
        "KNU-REFERENCES-001", "KNU-APPENDIX-001", "KNU-CAPTION-RENDERED-001", "KNU-BIBLIOGRAPHY-RENDERED-001"];
    private static readonly HashSet<string> Reasons = new(StringComparer.Ordinal) {
        "measured-page-matches-nominal-a4", "a3-confirmed-illustration-conditional-observation", "a3-necessary-illustration-role-not-established",
        "measured-page-differs-from-nominal-paper-review-required", "physical-scale-unavailable", "glyph-ink-unavailable", "confirmed-body-boundaries-required",
        "measurement-definition-unresolved", "confirmed-paragraph-starts-required", "effective-style-unavailable", "confirmed-heading-neighbors-required",
        "delegated-to-existing-rendered-extent-evaluation", "delegated-to-reference-predicates", "confirmed-page-number-and-title-roles-required",
        "confirmed-source-and-rendered-structure-required", "confirmed-abstract-and-reading-order-required", "compiled-bibliography-and-citation-order-required",
        "appendix-presence-and-designations-unknown", "confirmed-caption-and-illustration-roles-required" };
    public static string Pack(RenderedReviewRequest request)
    {
        if (request is not { IsValid: true }) throw new ArgumentException("Invalid rendered review request.");
        string result = JsonSerializer.Serialize(new { protocolVersion = 1, requestId = request.RequestId, operation = "evaluateRenderedReview", payload = new {
            pdfSha256 = request.PdfSha256, compileRequestId = request.CompileRequestId, compileEpoch = request.CompileEpoch, inputDigest = request.InputDigest,
            captureId = request.CaptureId, profile = new { id = RenderedReviewContract.ProfileId, version = RenderedReviewContract.ProfileVersion },
            workType = request.WorkType, annotationVersion = request.AnnotationVersion, pageCount = request.PageCount, requestedPages = request.RequestedPages,
            annotations = request.Annotations, factsSha256 = request.FactsSha256 } }, Camel);
        if (Encoding.UTF8.GetByteCount(result) > RenderedReviewContract.RequestMaxBytes) throw new ArgumentException("Rendered review request limit.");
        return result;
    }
    public static RenderedReviewObservation Parse(RenderedReviewRequest expected, string json)
    {
        if (!expected.IsValid || json.Length > RenderedReviewContract.ResponseMaxBytes || !PdfHashes.ValidUtf16(json)
            || Encoding.UTF8.GetByteCount(json) > RenderedReviewContract.ResponseMaxBytes) throw new JsonException();
        using var document = JsonDocument.Parse(json); var r = document.RootElement; PdfWireParser.Unique(r);
        Keys(r, "protocolVersion", "requestId", "status", "payload");
        Require(r.GetProperty("protocolVersion").GetInt32() == 1 && Text(r, "requestId") == expected.RequestId && Text(r, "status") == "completed");
        var p = r.GetProperty("payload");
        Keys(p, "pdfSha256", "compileRequestId", "compileEpoch", "inputDigest", "captureId", "profile", "workType", "annotationVersion", "pageCount",
            "requestedPages", "factsSha256", "guidelineSha256", "coverage", "results", "limitations");
        Require(Text(p, "pdfSha256") == expected.PdfSha256 && Text(p, "compileRequestId") == expected.CompileRequestId
            && p.GetProperty("compileEpoch").GetInt64() == expected.CompileEpoch && Text(p, "inputDigest") == expected.InputDigest
            && Text(p, "captureId") == expected.CaptureId.ToString() && Text(p, "workType") == expected.WorkType
            && p.GetProperty("annotationVersion").GetInt64() == expected.AnnotationVersion && p.GetProperty("pageCount").GetInt32() == expected.PageCount
            && Text(p, "factsSha256") == expected.FactsSha256 && Text(p, "guidelineSha256") == RenderedExtentContract.GuidelineSha256);
        var profile = p.GetProperty("profile"); Keys(profile, "id", "version");
        Require(Text(profile, "id") == RenderedReviewContract.ProfileId && Text(profile, "version") == RenderedReviewContract.ProfileVersion);
        Require(p.GetProperty("requestedPages").EnumerateArray().Select(v => v.GetInt32()).SequenceEqual(expected.RequestedPages));
        var coverage = p.GetProperty("coverage"); Keys(coverage, "pagesEvaluated", "complete", "standardsComplete");
        Require(coverage.GetProperty("pagesEvaluated").GetInt32() == expected.RequestedPages.Count && coverage.GetProperty("complete").GetBoolean()
            && !coverage.GetProperty("standardsComplete").GetBoolean());
        Require(Codes(p.GetProperty("limitations")).SequenceEqual(new[] { "finite-catalog-not-compliance-certificate", "unassessed-guideline-categories" }));
        var array = p.GetProperty("results"); Require(array.ValueKind == JsonValueKind.Array && array.GetArrayLength() == Ids.Length * expected.RequestedPages.Count);
        var results = new List<RenderedRuleResult>(); var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            Keys(item, "ruleId", "ruleVersion", "domain", "supportState", "outcome", "reason", "severity", "citation", "evidence", "limitations");
            string id = Text(item, "ruleId"), outcome = Text(item, "outcome"), support = Text(item, "supportState"), reason = Text(item, "reason");
            Require(Ids.Contains(id) && Text(item, "ruleVersion") == "1.0.0" && Text(item, "domain") == "renderedObservation"
                && support is "supported" or "partial" or "unsupported" && outcome is "pass" or "fail" or "indeterminate" or "unavailable" or "notApplicable"
                && Reasons.Contains(reason) && Text(item, "severity") == "warning" && (support != "unsupported" || outcome == "unavailable"));
            counts[id] = counts.GetValueOrDefault(id) + 1;
            var citation = item.GetProperty("citation"); Keys(citation, "section", "printedPages", "pdfPages");
            Require(Text(citation, "section").Length is > 0 and <= 64);
            var printed = citation.GetProperty("printedPages").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            Require(printed.Length is > 0 and <= 16 && printed.All(i => i is >= 1 and <= 54)
                && printed.SequenceEqual(citation.GetProperty("pdfPages").EnumerateArray().Select(v => v.GetInt32())));
            var evidence = item.GetProperty("evidence"); Require(evidence.ValueKind == JsonValueKind.Array && evidence.GetArrayLength() <= expected.RequestedPages.Count);
            var owned = new List<JsonElement>();
            foreach (var e in evidence.EnumerateArray()) { ValidateEvidence(e, expected, id); owned.Add(e.Clone()); }
            Require(id == "KNU-PAPER-001" || owned.Count == 0);
            Require(outcome is not ("pass" or "fail") || id == "KNU-PAPER-001" && owned.Count > 0);
            results.Add(new(id, "1.0.0", "renderedObservation", support, outcome, reason, "warning", citation.Clone(), owned.AsReadOnly(), Codes(item.GetProperty("limitations"))));
        }
        Require(Ids.All(id => counts.GetValueOrDefault(id) == expected.RequestedPages.Count));
        return new(expected, results.AsReadOnly());
    }
    private static void ValidateEvidence(JsonElement e, RenderedReviewRequest request, string id)
    {
        Keys(e, "pageIndex", "characterStart", "characterEnd", "rawBox", "measured", "expected", "unit", "method", "uncertainty", "roleProvenance", "sourceLocation");
        Require(id == "KNU-PAPER-001" && request.RequestedPages.Contains(e.GetProperty("pageIndex").GetInt32())
            && e.GetProperty("characterStart").ValueKind == JsonValueKind.Null && e.GetProperty("characterEnd").ValueKind == JsonValueKind.Null
            && Text(e, "unit") == "mm" && Text(e, "method") == "native-page-box" && e.GetProperty("roleProvenance").ValueKind == JsonValueKind.Null
            && e.GetProperty("sourceLocation").ValueKind == JsonValueKind.Null);
        var box = PdfWireParser.Box(e.GetProperty("rawBox"));
        Require(new[] { box.Left, box.Bottom, box.Right, box.Top }.All(v => Math.Abs(v) <= 1000000));
        Require(Encoding.UTF8.GetByteCount(Text(e, "measured")) <= 256 && Encoding.UTF8.GetByteCount(Text(e, "expected")) <= 256);
        var u = e.GetProperty("uncertainty"); Keys(u, "absoluteError", "method", "limitations");
        double error = u.GetProperty("absoluteError").GetDouble(); Require(double.IsFinite(error) && error >= 0 && Text(u, "method") == "pdfium-float32-roundoff-bound"
            && Codes(u.GetProperty("limitations")).SequenceEqual(new[] { "nominal-paper-tolerance-unspecified" }));
    }
    private static string[] Codes(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 32);
        var strings = value.EnumerateArray().Select(v => v.GetString() ?? throw new JsonException()).ToArray();
        Require(strings.All(s => s.Length is > 0 and <= 64 && s.All(c => c is >= '!' and <= '~')) && strings.Distinct().Count() == strings.Length); return strings;
    }
    private static string Text(JsonElement e, string key) => e.GetProperty(key).GetString() ?? throw new JsonException();
    private static void Keys(JsonElement e, params string[] keys) => PdfWireParser.Keys(e, keys);
    private static void Require(bool condition) { if (!condition) throw new JsonException("Invalid rendered review protocol."); }
}
