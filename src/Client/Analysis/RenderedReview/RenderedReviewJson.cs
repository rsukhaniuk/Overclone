using Client.Analysis.RenderedExtent;
using Client.Pdf;
using System.Text;
using System.Text.Json;

namespace Client.Analysis.RenderedReview;

public static partial class RenderedReviewJson
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
        "appendix-presence-and-designations-unknown", "confirmed-caption-and-illustration-roles-required",
        "glyph-ink-uncertainty-unavailable", "metric-horizontal-frame-required", "metric-classification-quota", "metric-no-measured-glyphs",
        "metric-incomplete-glyph-coverage", "metric-ambiguous-line-order", "metric-ambiguous-columns", "metric-continuation-not-established",
        "metric-evidence-quota", "metric-role-scope-overlap", "conditional-paragraph-indent", "conditional-body-ink-margins",
        "metric-painted-whitespace-ambiguous", "role-evidence-quota", "role-ambiguous-duplicates", "role-text-quota", "role-unknown-unicode", "role-empty-text",
        "conditional-page-number-literal", "conditional-heading-literal", "conditional-caption-literal", "conditional-typography-observations" };
    public static string Pack(RenderedReviewRequest request)
    {
        if (request is not { IsValid: true }) throw new ArgumentException("Invalid rendered review request.");
        string result = JsonSerializer.Serialize(new { protocolVersion = 1, requestId = request.RequestId, operation = "evaluateRenderedReview", payload = new {
            pdfSha256 = request.PdfSha256, compileRequestId = request.CompileRequestId, compileEpoch = request.CompileEpoch, inputDigest = request.InputDigest,
            captureId = request.CaptureId, profile = new { id = RenderedReviewContract.ProfileId, version = request.ProfileVersion },
            workType = request.WorkType, annotationVersion = request.AnnotationVersion, pageCount = request.PageCount, requestedPages = request.RequestedPages,
            annotations = request.Annotations, factsSha256 = request.FactsSha256 } }, Camel);
        if (Encoding.UTF8.GetByteCount(result) > RenderedReviewContract.RequestMaxBytes) throw new ArgumentException("Rendered review request limit.");
        return result;
    }
    public static RenderedReviewObservation Parse(RenderedReviewRequest expected, string json)
    {
        try { return ParseCore(expected, json); }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException)
        { throw new JsonException("Invalid rendered review response shape.", error); }
    }
    private static RenderedReviewObservation ParseCore(RenderedReviewRequest expected, string json)
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
        Require(Text(profile, "id") == RenderedReviewContract.ProfileId && Text(profile, "version") == expected.ProfileVersion);
        Require(p.GetProperty("requestedPages").EnumerateArray().Select(v => v.GetInt32()).SequenceEqual(expected.RequestedPages));
        var coverage = p.GetProperty("coverage"); Keys(coverage, "pagesEvaluated", "complete", "standardsComplete");
        Require(coverage.GetProperty("pagesEvaluated").GetInt32() == expected.RequestedPages.Count && coverage.GetProperty("complete").GetBoolean()
            && !coverage.GetProperty("standardsComplete").GetBoolean());
        Require(Codes(p.GetProperty("limitations")).SequenceEqual(new[] { "finite-catalog-not-compliance-certificate", "unassessed-guideline-categories" }));
        var array = p.GetProperty("results"); Require(array.ValueKind == JsonValueKind.Array && array.GetArrayLength() == Ids.Length * expected.RequestedPages.Count);
        var results = new List<RenderedRuleResult>(); var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenRoles = new HashSet<(string, int, long)>();
        foreach (var item in array.EnumerateArray())
        {
            Keys(item, "ruleId", "ruleVersion", "domain", "supportState", "outcome", "reason", "severity", "citation", "evidence", "limitations");
            string id = Text(item, "ruleId"), outcome = Text(item, "outcome"), support = Text(item, "supportState"), reason = Text(item, "reason");
            Require(Ids.Contains(id) && Text(item, "domain") == "renderedObservation" && Text(item, "severity") == "warning");
            int occurrence = counts.GetValueOrDefault(id);
            counts[id] = occurrence + 1;
            if (id == PaginationRuleId && expected.ProfileVersion != RenderedReviewContract.ProfileVersion)
            {
                Require(occurrence < expected.RequestedPages.Count);
                results.Add(ParsePagination(item, expected, expected.RequestedPages[occurrence], outcome, support, reason));
                continue;
            }
            if (id == HeadingRuleId && expected.ProfileVersion == RenderedReviewContract.HeadingProfileVersion)
            {
                Require(occurrence < expected.RequestedPages.Count);
                results.Add(ParseHeading(item, expected, expected.RequestedPages[occurrence], outcome, support, reason));
                continue;
            }
            Require(Text(item, "ruleVersion") == "1.0.0"
                && support is "supported" or "partial" or "unsupported" && outcome is "pass" or "fail" or "indeterminate" or "unavailable" or "notApplicable"
                && Reasons.Contains(reason) && (support != "unsupported" || outcome == "unavailable"));
            var citation = item.GetProperty("citation"); Keys(citation, "section", "printedPages", "pdfPages");
            Require(Text(citation, "section").Length is > 0 and <= 64);
            var printed = citation.GetProperty("printedPages").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            Require(printed.Length is > 0 and <= 16 && printed.All(i => i is >= 1 and <= 54)
                && printed.SequenceEqual(citation.GetProperty("pdfPages").EnumerateArray().Select(v => v.GetInt32())));
            var evidence = item.GetProperty("evidence"); Require(evidence.ValueKind == JsonValueKind.Array && evidence.GetArrayLength() <= (RoleRule(id) ? 8 : id is "KNU-MARGINS-001" or "KNU-PARAGRAPH-RENDERED-001" or "KNU-TYPOGRAPHY-001" ? 32 : expected.RequestedPages.Count));
            var owned = new List<JsonElement>();
            foreach (var e in evidence.EnumerateArray()) { ValidateEvidence(e, expected, id); owned.Add(e.Clone()); }
            Require(id is "KNU-PAPER-001" or "KNU-MARGINS-001" or "KNU-PARAGRAPH-RENDERED-001" or "KNU-TYPOGRAPHY-001" || RoleRule(id) || owned.Count == 0);
            if (reason is "conditional-body-ink-margins" or "conditional-paragraph-indent" or "conditional-typography-observations") Require(owned.Count > 0);
            if (RoleRule(id) && owned.Count > 0)
            {
                Require(support == "partial" && outcome == "indeterminate" && reason == RoleReason(id));
                Require(id != "KNU-PAGINATION-001" || owned.Count == 1);
                foreach (var e in owned) Require(seenRoles.Add((id, e.GetProperty("pageIndex").GetInt32(), e.GetProperty("roleProvenance").GetProperty("annotationId").GetInt64())));
                Require(owned.Select(e => e.GetProperty("pageIndex").GetInt32()).Distinct().Count() == 1
                    && owned.Select(e => e.GetProperty("roleProvenance").GetProperty("annotationId").GetInt64()).Distinct().Count() == owned.Count);
            }
            if (RoleRule(id) && reason == RoleReason(id)) Require(owned.Count > 0);
            if (owned.Count > 0 && id != "KNU-PAPER-001" && !RoleRule(id))
            {
                Require(support == "partial" && outcome == "indeterminate"
                    && reason == (id == "KNU-MARGINS-001" ? "conditional-body-ink-margins" : id == "KNU-TYPOGRAPHY-001" ? "conditional-typography-observations" : "conditional-paragraph-indent"));
                Require(owned.Select(e => e.GetProperty("pageIndex").GetInt32()).Distinct().Count() == 1);
                Require(owned.Select(e => (e.GetProperty("roleProvenance").GetProperty("annotationId").GetInt64(), Text(e, "metric"))).Distinct().Count() == owned.Count);
                if (id == "KNU-TYPOGRAPHY-001")
                    foreach (var group in owned.GroupBy(e => e.GetProperty("roleProvenance").GetProperty("annotationId").GetInt64()))
                    {
                        var metrics = group.ToDictionary(e => Text(e, "metric"));
                        Require(metrics.Count is 2 or 4);
                        foreach (string prefix in new[] { "letter-ink-height", "baseline-distance" })
                        {
                            bool low = metrics.TryGetValue(prefix + "-min", out var minimum), high = metrics.TryGetValue(prefix + "-max", out var maximum);
                            Require(low == high);
                            if (low) Require(minimum.GetProperty("measured").GetDouble() <= maximum.GetProperty("measured").GetDouble());
                        }
                    }
                if (id == "KNU-MARGINS-001")
                    foreach (var group in owned.GroupBy(e => e.GetProperty("roleProvenance").GetProperty("annotationId").GetInt64()))
                        Require(group.Select(e => Text(e, "metric")).Order().SequenceEqual(new[] { "margin-bottom", "margin-left", "margin-right", "margin-top" }));
            }
            Require(outcome is not ("pass" or "fail") || id == "KNU-PAPER-001" && owned.Count > 0
                && (outcome == "pass" || expected.ProfileVersion == RenderedReviewContract.ProfileVersion));
            results.Add(new(id, "1.0.0", "renderedObservation", support, outcome, reason, "warning", citation.Clone(), owned.AsReadOnly(), Codes(item.GetProperty("limitations"))));
        }
        Require(Ids.All(id => counts.GetValueOrDefault(id) == expected.RequestedPages.Count));
        return new(expected, results.AsReadOnly());
    }
    private static void ValidateEvidence(JsonElement e, RenderedReviewRequest request, string id)
    {
        if (id == "KNU-TYPOGRAPHY-001") { ValidateTypographyEvidence(e, request); return; }
        if (RoleRule(id)) { ValidateRoleEvidence(e, request, id); return; }
        if (id != "KNU-PAPER-001") { ValidateMetricEvidence(e, request, id); return; }
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
    private static void ValidateMetricEvidence(JsonElement e, RenderedReviewRequest request, string id)
    {
        Keys(e, "pageIndex", "characterStart", "characterEnd", "rawBox", "measured", "expected", "unit", "method", "metric", "classification", "uncertainty", "roleProvenance", "sourceLocation");
        bool paragraph = id == "KNU-PARAGRAPH-RENDERED-001";
        Require(paragraph || id == "KNU-MARGINS-001");
        int page = e.GetProperty("pageIndex").GetInt32();
        Require(request.RequestedPages.Contains(page) && Text(e, "unit") == "mm" && e.GetProperty("sourceLocation").ValueKind == JsonValueKind.Null
            && Text(e, "method") == (paragraph ? "classified-paragraph-start" : "native-glyph-ink")
            && (paragraph ? Text(e, "metric") == "paragraph-indent" : Text(e, "metric") is "margin-left" or "margin-top" or "margin-right" or "margin-bottom"));
        var box = PdfWireParser.Box(e.GetProperty("rawBox"));
        Require(new[] { box.Left, box.Bottom, box.Right, box.Top }.All(v => double.IsFinite(v) && Math.Abs(v) <= 1000000));
        foreach (string field in new[] { "measured", "expected" })
        { double value = e.GetProperty(field).GetDouble(); Require(double.IsFinite(value) && Math.Abs(value) <= 1000000000000); }
        var provenance = e.GetProperty("roleProvenance"); Keys(provenance, "annotationId", "role", "provenance", "certainty");
        var matches = request.Annotations.Where(a => a.AnnotationId == provenance.GetProperty("annotationId").GetInt64()).ToArray();
        Require(matches.Length == 1); var annotation = matches[0];
        Require(annotation.PageIndex == page && annotation.Role == (paragraph ? "paragraph" : "body")
            && Text(provenance, "role") == annotation.Role && Text(provenance, "provenance") == annotation.Provenance
            && Text(provenance, "certainty") == annotation.Certainty
            && (annotation.Provenance is "authorLabel" or "literalSourceAndNativeMap" or "validatedNativeClassification")
            && (annotation.Certainty is "confirmedObservation" or "tentative"));
        foreach (var pair in new[] { ("characterStart", annotation.CharacterStart), ("characterEnd", annotation.CharacterEnd) })
            Require(pair.Item2 is null ? e.GetProperty(pair.Item1).ValueKind == JsonValueKind.Null : e.GetProperty(pair.Item1).GetInt32() == pair.Item2);
        var classification = e.GetProperty("classification"); Keys(classification, "lineCount", "characterCount", "firstBaselineRaw", "continuationBaselineRaw");
        int lines = classification.GetProperty("lineCount").GetInt32(), characters = classification.GetProperty("characterCount").GetInt32();
        Require(lines is >= 1 and <= 256 && characters is >= 1 and <= 8192 && characters >= lines);
        double baseline = classification.GetProperty("firstBaselineRaw").GetDouble(); Require(double.IsFinite(baseline) && Math.Abs(baseline) <= 1000000);
        var next = classification.GetProperty("continuationBaselineRaw");
        Require(lines == 1 ? next.ValueKind == JsonValueKind.Null : next.ValueKind == JsonValueKind.Number && double.IsFinite(next.GetDouble()) && Math.Abs(next.GetDouble()) <= 1000000 && next.GetDouble() < baseline);
        Require(!paragraph || lines >= 2 && characters >= 4);
        var u = e.GetProperty("uncertainty"); Keys(u, "absoluteError", "method", "limitations");
        double error = u.GetProperty("absoluteError").GetDouble();
        Require(double.IsFinite(error) && error >= 0 && error <= 1000000000000 && Text(u, "method") == "native-outline-and-float32-bound"
            && Codes(u.GetProperty("limitations")).SequenceEqual(new[] { "conditional-role-scope-only", "other-page-paint-not-established", "ink-extents-not-layout-box" }));
    }
    private static void ValidateTypographyEvidence(JsonElement e, RenderedReviewRequest request)
    {
        Keys(e, "pageIndex", "characterStart", "characterEnd", "rawBox", "measured", "expected", "unit", "method", "metric", "classification", "uncertainty", "roleProvenance", "sourceLocation");
        int page = e.GetProperty("pageIndex").GetInt32(); string metric = Text(e, "metric");
        bool height = metric is "letter-ink-height-min" or "letter-ink-height-max";
        Require(height || metric is "baseline-distance-min" or "baseline-distance-max");
        Require(request.RequestedPages.Contains(page) && Text(e, "unit") == "mm" && e.GetProperty("sourceLocation").ValueKind == JsonValueKind.Null
            && Text(e, "method") == (height ? "native-glyph-ink-height" : "classified-baseline-distance")
            && Text(e, "expected") == (height ? "Font height is undefined in section 3.1; letter ink height is an observation only" : "1.5 line spacing has no baseline-distance definition in section 3.1"));
        double measured = e.GetProperty("measured").GetDouble(); Require(double.IsFinite(measured) && measured > 0 && measured <= 1000000000000);
        var box = PdfWireParser.Box(e.GetProperty("rawBox"));
        Require(new[] { box.Left, box.Bottom, box.Right, box.Top }.All(v => double.IsFinite(v) && Math.Abs(v) <= 1000000)
            && box.Left < box.Right && box.Bottom < box.Top);
        var provenance = e.GetProperty("roleProvenance"); Keys(provenance, "annotationId", "role", "provenance", "certainty");
        var matches = request.Annotations.Where(a => a.AnnotationId == provenance.GetProperty("annotationId").GetInt64()).ToArray();
        Require(matches.Length == 1); var annotation = matches[0];
        Require(annotation.PageIndex == page && annotation.Role is "body" or "paragraph" && Text(provenance, "role") == annotation.Role
            && Text(provenance, "provenance") == annotation.Provenance && Text(provenance, "certainty") == annotation.Certainty
            && annotation.Provenance is "authorLabel" or "literalSourceAndNativeMap" or "validatedNativeClassification"
            && annotation.Certainty is "confirmedObservation" or "tentative");
        foreach (var pair in new[] { ("characterStart", annotation.CharacterStart), ("characterEnd", annotation.CharacterEnd) })
            Require(pair.Item2 is null ? e.GetProperty(pair.Item1).ValueKind == JsonValueKind.Null : e.GetProperty(pair.Item1).GetInt32() == pair.Item2);
        var c = e.GetProperty("classification"); Keys(c, "lineCount", "characterCount", "firstBaselineRaw", "continuationBaselineRaw");
        int lines = c.GetProperty("lineCount").GetInt32(), characters = c.GetProperty("characterCount").GetInt32();
        Require(lines is >= 1 and <= 256 && characters is >= 1 and <= 8192 && characters >= lines && (height || lines >= 2));
        double first = c.GetProperty("firstBaselineRaw").GetDouble(); Require(double.IsFinite(first) && Math.Abs(first) <= 1000000);
        var next = c.GetProperty("continuationBaselineRaw");
        Require(lines == 1 ? next.ValueKind == JsonValueKind.Null : next.ValueKind == JsonValueKind.Number && double.IsFinite(next.GetDouble()) && Math.Abs(next.GetDouble()) <= 1000000 && next.GetDouble() < first);
        var u = e.GetProperty("uncertainty"); Keys(u, "absoluteError", "method", "limitations"); double error = u.GetProperty("absoluteError").GetDouble();
        Require(double.IsFinite(error) && error > 0 && error <= 1000000000000 && Text(u, "method") == "native-outline-and-float32-bound"
            && Codes(u.GetProperty("limitations")).SequenceEqual(new[] { "conditional-role-scope-only", "extracted-unicode-not-visual-glyph-proof", "ink-height-not-font-height", "baseline-distance-not-leading-definition" }));
    }
    private static bool RoleRule(string id) => id is "KNU-PAGINATION-001" or "KNU-HEADINGS-RENDERED-001" or "KNU-CAPTION-RENDERED-001";
    private static string RoleReason(string id) => id switch
    { "KNU-PAGINATION-001" => "conditional-page-number-literal", "KNU-HEADINGS-RENDERED-001" => "conditional-heading-literal", _ => "conditional-caption-literal" };
    private static void ValidateRoleEvidence(JsonElement e, RenderedReviewRequest request, string id)
    {
        Keys(e, "pageIndex", "characterStart", "characterEnd", "rawBox", "measured", "expected", "unit", "method", "uncertainty", "roleProvenance", "sourceLocation", "observation");
        int page = e.GetProperty("pageIndex").GetInt32();
        Require(request.RequestedPages.Contains(page) && Text(e, "unit") == "text" && Text(e, "method") == "annotation-scoped-native-text"
            && e.GetProperty("sourceLocation").ValueKind == JsonValueKind.Null);
        foreach (string field in new[] { "measured", "expected" })
        {
            string value = Text(e, field); Require(value.Length > 0 && Encoding.UTF8.GetByteCount(value) <= 256
                && value.EnumerateRunes().All(r => r.Value is not (0 or 0xfffd) && (!System.Text.Rune.IsControl(r) || r.Value is 9 or 10 or 13)));
        }
        var provenance = e.GetProperty("roleProvenance"); Keys(provenance, "annotationId", "role", "provenance", "certainty");
        var matches = request.Annotations.Where(a => a.AnnotationId == provenance.GetProperty("annotationId").GetInt64()).ToArray();
        Require(matches.Length == 1); var annotation = matches[0];
        Require(annotation.PageIndex == page && Text(provenance, "role") == annotation.Role
            && Text(provenance, "provenance") == annotation.Provenance && Text(provenance, "certainty") == annotation.Certainty
            && annotation.Provenance is "authorLabel" or "literalSourceAndNativeMap" or "validatedNativeClassification"
            && annotation.Certainty is "confirmedObservation" or "tentative");
        foreach (var pair in new[] { ("characterStart", annotation.CharacterStart), ("characterEnd", annotation.CharacterEnd) })
            Require(pair.Item2 is null ? e.GetProperty(pair.Item1).ValueKind == JsonValueKind.Null : e.GetProperty(pair.Item1).GetInt32() == pair.Item2);
        var o = e.GetProperty("observation"); Keys(o, "kind", "textOrder", "unicodeCase", "asciiDigitValue", "oneBasedPdfPage", "captionPrefix", "finalFullStop", "placement", "placementReason", "relativeOrder");
        string kind = id switch { "KNU-PAGINATION-001" => "page-number", "KNU-HEADINGS-RENDERED-001" => "heading", _ => "caption" };
        Require(Text(o, "kind") == kind && (kind switch { "page-number" => annotation.Role == "pageNumber", "heading" => annotation.Role is "headingMajor" or "headingMinor", _ => annotation.Role is "figureCaption" or "tableCaption" })
            && o.GetProperty("oneBasedPdfPage").GetInt32() == page + 1
            && Text(o, "textOrder") is "native-index" or "validated-horizontal" && Text(o, "unicodeCase") is "upper" or "lower" or "mixed" or "uncased"
            && Text(o, "captionPrefix") is "figure" or "table" or "none" && o.GetProperty("finalFullStop").ValueKind is JsonValueKind.True or JsonValueKind.False);
        var number = o.GetProperty("asciiDigitValue"); Require(number.ValueKind == JsonValueKind.Null || number.GetInt64() is >= 0 and <= 9007199254740991);
        Require(kind == "caption" ? Text(o, "relativeOrder") == "illustration-geometry-unavailable" : o.GetProperty("relativeOrder").ValueKind == JsonValueKind.Null);
        var u = e.GetProperty("uncertainty"); Keys(u, "absoluteError", "method", "limitations");
        Require(u.GetProperty("absoluteError").ValueKind == JsonValueKind.Null && Text(u, "method") == "native-character-unicode"
            && Codes(u.GetProperty("limitations")).SequenceEqual(new[] { "conditional-role-scope-only", "extracted-text-not-visual-glyph-proof", "role-and-document-order-not-established", "page-centre-not-text-layout-centre" }));
        var placement = o.GetProperty("placement");
        if (placement.ValueKind == JsonValueKind.Null)
        {
            Require(e.GetProperty("rawBox").ValueKind == JsonValueKind.Null);
            string reason = Text(o, "placementReason");
            Require(Text(o, "textOrder") == "validated-horizontal" ? reason == "physical-scale-unavailable" : reason is "glyph-ink-uncertainty-unavailable"
                or "metric-horizontal-frame-required" or "metric-classification-quota" or "metric-no-measured-glyphs" or "metric-incomplete-glyph-coverage"
                or "metric-ambiguous-line-order" or "metric-ambiguous-columns" or "metric-painted-whitespace-ambiguous");
        }
        else
        {
            Keys(placement, "media", "inkBox", "topClearanceMm", "rightClearanceMm", "pageCentreOffsetMm", "absoluteErrorMm", "lineCount");
            Require(Text(o, "textOrder") == "validated-horizontal" && Text(o, "placementReason") == "native-outline-location");
            var media = PdfWireParser.Box(placement.GetProperty("media")); var ink = PdfWireParser.Box(placement.GetProperty("inkBox"));
            Require(PdfWireParser.Box(e.GetProperty("rawBox")) == ink && new[] { media.Left, media.Bottom, media.Right, media.Top, ink.Left, ink.Bottom, ink.Right, ink.Top }.All(v => double.IsFinite(v) && Math.Abs(v) <= 1000000));
            foreach (string field in new[] { "topClearanceMm", "rightClearanceMm", "pageCentreOffsetMm", "absoluteErrorMm" })
            { double value = placement.GetProperty(field).GetDouble(); Require(double.IsFinite(value) && Math.Abs(value) <= 1000000000000); }
            Require(placement.GetProperty("absoluteErrorMm").GetDouble() >= 0 && placement.GetProperty("lineCount").GetInt32() is >= 1 and <= 256);
        }
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
