using System.Text.Json;

namespace Client.Analysis.RenderedReview;

public static partial class RenderedReviewJson
{
    private const string HeadingRuleId = "KNU-HEADINGS-RENDERED-001";
    private const string HeadingRuleVersion = "1.1.0";
    private static readonly string[] HeadingLimitations =
    [
        "heading-and-body-roles-author-confirmed",
        "selected-same-page-ink-only",
        "unselected-content-unassessed",
        "effective-style-and-text-alignment-unestablished",
        "section-sequence-outside-page-unassessed"
    ];
    private static readonly string[] HeadingEvidenceLimitations =
    [
        "positive-ink-only",
        "only-selected-author-ranges",
        "no-immediate-neighbor-or-page-start-proof"
    ];

    private static RenderedRuleResult ParseHeading(JsonElement row, RenderedReviewRequest request, int pageIndex,
        string outcome, string support, string reason)
    {
        Require(Text(row, "ruleVersion") == HeadingRuleVersion && support == "partial");
        var citation = row.GetProperty("citation");
        Keys(citation, "section", "printedPages", "pdfPages");
        Require(Text(citation, "section") == "3.2"
            && citation.GetProperty("printedPages").EnumerateArray().Select(p => p.GetInt32()).SequenceEqual(new[] { 18, 19 })
            && citation.GetProperty("pdfPages").EnumerateArray().Select(p => p.GetInt32()).SequenceEqual(new[] { 18, 19 }));
        var limitations = Codes(row.GetProperty("limitations"));
        Require(limitations.SequenceEqual(HeadingLimitations));

        var evidence = row.GetProperty("evidence");
        Require(evidence.ValueKind == JsonValueKind.Array && evidence.GetArrayLength() <= 1);
        if (evidence.GetArrayLength() == 0)
        {
            Require(outcome == "indeterminate" && reason is "confirmed-major-heading-range-required" or "confirmed-heading-body-range-required"
                or "ambiguous-heading-role-pair" or "heading-upright-layout-required" or "heading-physical-scale-unavailable"
                or "heading-ink-unavailable" or "heading-ink-outside-effective-page" or "heading-horizontal-overlap-unestablished"
                or "heading-native-and-vertical-order-unestablished");
            return new(HeadingRuleId, HeadingRuleVersion, "renderedObservation", support, outcome, reason, "warning",
                citation.Clone(), [], Array.AsReadOnly(limitations));
        }

        var item = evidence[0];
        ValidateHeadingEvidence(item, request, pageIndex, outcome, reason);
        return new(HeadingRuleId, HeadingRuleVersion, "renderedObservation", support, outcome, reason, "warning",
            citation.Clone(), Array.AsReadOnly(new[] { item.Clone() }), Array.AsReadOnly(limitations));
    }

    private static void ValidateHeadingEvidence(JsonElement item, RenderedReviewRequest request, int pageIndex,
        string outcome, string reason)
    {
        Keys(item, "pageIndex", "characterStart", "characterEnd", "rawBox", "measured", "expected", "unit",
            "method", "uncertainty", "roleProvenance", "sourceLocation", "observation");
        Require(item.GetProperty("pageIndex").GetInt32() == pageIndex && Text(item, "unit") == "mm"
            && Text(item, "method") == "confirmed-major-heading-and-body-ink"
            && item.GetProperty("expected").ValueKind == JsonValueKind.Null && item.GetProperty("sourceLocation").ValueKind == JsonValueKind.Null);
        var source = item.GetProperty("roleProvenance");
        Keys(source, "annotationId", "role", "provenance", "certainty");
        Require(Text(source, "role") == "headingMajor" && Text(source, "provenance") == "authorLabel"
            && Text(source, "certainty") == "confirmedObservation");
        long headingId = source.GetProperty("annotationId").GetInt64();
        int headingStart = item.GetProperty("characterStart").GetInt32(), headingEnd = item.GetProperty("characterEnd").GetInt32();
        ValidateHeadingRole(request, pageIndex, "headingMajor", headingId, headingStart, headingEnd);

        var observation = item.GetProperty("observation");
        Keys(observation, "kind", "bodyAnnotationId", "bodyCharacterStart", "bodyCharacterEnd", "bodyInkBox",
            "effectiveBox", "userUnit", "rawSignedGap", "absoluteErrorRaw", "relativeOrder");
        Require(Text(observation, "kind") == "confirmed-major-heading-page-order");
        long bodyId = observation.GetProperty("bodyAnnotationId").GetInt64();
        int bodyStart = observation.GetProperty("bodyCharacterStart").GetInt32(), bodyEnd = observation.GetProperty("bodyCharacterEnd").GetInt32();
        ValidateHeadingRole(request, pageIndex, "body", bodyId, bodyStart, bodyEnd);
        Require(headingId != bodyId);

        var heading = PositiveReviewBox(item.GetProperty("rawBox"));
        var body = PositiveReviewBox(observation.GetProperty("bodyInkBox"));
        var frame = PositiveReviewBox(observation.GetProperty("effectiveBox"));
        Require(Contains(frame, heading) && Contains(frame, body) && Math.Max(heading.Left, body.Left) < Math.Min(heading.Right, body.Right));
        var uncertainty = item.GetProperty("uncertainty");
        Keys(uncertainty, "absoluteError", "method", "limitations");
        Require(Text(uncertainty, "method") == "native-outline-and-float32-bound"
            && Codes(uncertainty.GetProperty("limitations")).SequenceEqual(HeadingEvidenceLimitations));
        double gap = item.GetProperty("measured").GetDouble(), absoluteError = uncertainty.GetProperty("absoluteError").GetDouble();
        double rawGap = observation.GetProperty("rawSignedGap").GetDouble(), rawError = observation.GetProperty("absoluteErrorRaw").GetDouble();
        double unit = observation.GetProperty("userUnit").GetDouble();
        Require(double.IsFinite(gap) && Math.Abs(gap) <= 1e12 && absoluteError is > 0 and <= 1e12
            && double.IsFinite(rawGap) && rawError is > 0 and <= 2e6 && unit is > 0 and <= 75000);
        double expectedGap = rawGap * unit * 25.4 / 72;
        Require(Math.Abs(gap - expectedGap) <= 8 * Math.Abs(Math.BitIncrement(expectedGap) - expectedGap)
            && absoluteError >= rawError * unit * 25.4 / 72);

        switch (Text(observation, "relativeOrder"))
        {
            case "body-above-heading":
                Require(bodyEnd <= headingStart && rawGap == body.Bottom - heading.Top && rawGap > rawError && gap > absoluteError
                    && outcome == "fail" && reason == "confirmed-major-heading-follows-body-on-page");
                break;
            case "body-below-heading":
                Require(headingEnd <= bodyStart && rawGap == body.Top - heading.Bottom && rawGap < -rawError && gap < -absoluteError
                    && outcome == "indeterminate" && reason == "conditional-major-heading-precedes-body-on-page");
                break;
            default:
                throw new JsonException("Unknown heading and body relation.");
        }
    }

    private static void ValidateHeadingRole(RenderedReviewRequest request, int pageIndex, string role, long annotationId,
        int start, int end)
    {
        Require(annotationId is > 0 and <= SafeInteger && start >= 0 && end > start && end <= 100000);
        var selected = request.Annotations.Where(a => a is not null && a.PageIndex == pageIndex && a.Role == role).ToArray();
        Require(selected.Length == 1 && selected[0].AnnotationId == annotationId && ConfirmedAuthor(selected[0])
            && selected[0].CharacterStart == start && selected[0].CharacterEnd == end
            && request.Annotations.Count(a => a?.AnnotationId == annotationId) == 1);
    }
}
