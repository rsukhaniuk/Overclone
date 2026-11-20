using System.Text.Json;

namespace Client.Analysis.RenderedReview;

public static partial class RenderedReviewJson
{
    private const string ParagraphIndentRuleId = "KNU-PARAGRAPH-RENDERED-001";
    private const string ParagraphIndentRuleVersion = "1.1.0";
    private const double ParagraphIndentTargetMm = 12.7;
    private static readonly string[] ParagraphIndentLimitations =
    [
        "three-complete-lines-of-one-author-confirmed-paragraph",
        "rendered-text-origins-only",
        "unselected-paragraphs-and-uniformity-unassessed"
    ];

    private static RenderedRuleResult ParseParagraphIndent(JsonElement row, RenderedReviewRequest request, int pageIndex,
        string outcome, string support, string reason)
    {
        Require(Text(row, "ruleVersion") == ParagraphIndentRuleVersion && support == "partial");
        var citation = row.GetProperty("citation");
        Keys(citation, "section", "printedPages", "pdfPages");
        Require(Text(citation, "section") == "3.1"
            && citation.GetProperty("printedPages").EnumerateArray().Select(p => p.GetInt32()).SequenceEqual(new[] { 18 })
            && citation.GetProperty("pdfPages").EnumerateArray().Select(p => p.GetInt32()).SequenceEqual(new[] { 18 }));
        var limitations = Codes(row.GetProperty("limitations"));
        Require(limitations.SequenceEqual(ParagraphIndentLimitations));
        var evidence = row.GetProperty("evidence");
        Require(evidence.ValueKind == JsonValueKind.Array && evidence.GetArrayLength() <= 1);
        if (evidence.GetArrayLength() == 0)
        {
            Require(outcome == "indeterminate" && reason is "confirmed-paragraph-three-line-range-required"
                or "ambiguous-paragraph-selected-range" or "paragraph-physical-scale-unavailable"
                or "paragraph-line-ink-unavailable" or "paragraph-continuation-origin-unestablished"
                or "paragraph-isolated-line-order-unestablished");
            return new(ParagraphIndentRuleId, ParagraphIndentRuleVersion, "renderedObservation", support, outcome, reason,
                "warning", citation.Clone(), [], Array.AsReadOnly(limitations));
        }

        var item = evidence[0];
        Keys(item, "pageIndex", "characterStart", "characterEnd", "rawBox", "measured", "expected", "unit",
            "method", "uncertainty", "roleProvenance", "sourceLocation", "observation");
        Require(item.GetProperty("pageIndex").GetInt32() == pageIndex && Text(item, "unit") == "mm"
            && Text(item, "method") == "confirmed-paragraph-first-line-origin"
            && item.GetProperty("expected").GetDouble() == ParagraphIndentTargetMm
            && item.GetProperty("sourceLocation").ValueKind == JsonValueKind.Null);
        int start = item.GetProperty("characterStart").GetInt32(), end = item.GetProperty("characterEnd").GetInt32();
        var role = item.GetProperty("roleProvenance");
        Keys(role, "annotationId", "role", "provenance", "certainty");
        Require(Text(role, "role") == "paragraph" && Text(role, "provenance") == "authorLabel"
            && Text(role, "certainty") == "confirmedObservation");
        ValidateSingleConfirmedRange(request, pageIndex, "paragraph", role.GetProperty("annotationId").GetInt64(), start, end);

        var observation = item.GetProperty("observation");
        Keys(observation, "kind", "starts", "effectiveBox", "userUnit", "rawIndent", "absoluteErrorRaw", "comparison");
        Require(Text(observation, "kind") == "confirmed-paragraph-indent");
        var frame = PositiveReviewBox(observation.GetProperty("effectiveBox"));
        Require(Contains(frame, PositiveReviewBox(item.GetProperty("rawBox"))));
        var starts = observation.GetProperty("starts");
        Require(starts.GetArrayLength() == 3);
        var anchors = starts.EnumerateArray().Select(anchor =>
        {
            Keys(anchor, "index", "origin", "absoluteErrorRaw");
            var origin = anchor.GetProperty("origin");
            Require(origin.GetArrayLength() == 2);
            double x = origin[0].GetDouble(), y = origin[1].GetDouble(), error = anchor.GetProperty("absoluteErrorRaw").GetDouble();
            Require(x >= frame.Left && x <= frame.Right && y >= frame.Bottom && y <= frame.Top && error is > 0 and <= 2e6);
            return (Index: anchor.GetProperty("index").GetInt32(), X: x, Y: y, Error: error);
        }).ToArray();
        var first = anchors[0]; var next = anchors[1]; var last = anchors[2];
        Require(first.Index == start && next.Index >= first.Index + 4 && last.Index >= next.Index + 4 && last.Index < end - 1
            && first.Y > next.Y && next.Y > last.Y);
        double spread = Math.Abs(next.X - last.X);
        Require(spread <= next.Error + last.Error);
        double raw = observation.GetProperty("rawIndent").GetDouble(), rawError = observation.GetProperty("absoluteErrorRaw").GetDouble();
        double unit = observation.GetProperty("userUnit").GetDouble();
        Require(Near(raw, first.X - (next.X + last.X) / 2) && Near(rawError, first.Error + Math.Max(next.Error, last.Error) + spread / 2)
            && unit is > 0 and <= 75000);
        var uncertainty = item.GetProperty("uncertainty");
        Keys(uncertainty, "absoluteError", "method", "limitations");
        Require(Text(uncertainty, "method") == "native-outline-and-float32-bound"
            && Codes(uncertainty.GetProperty("limitations")).SequenceEqual(new[] {
                "conditional-on-left-aligned-paragraph-role", "source-text-layout-transform-error-unmeasured" }));
        double mm = item.GetProperty("measured").GetDouble(), errorMm = uncertainty.GetProperty("absoluteError").GetDouble();
        Require(Near(mm, raw * unit * 25.4 / 72) && errorMm is > 0 and <= 1e12 && errorMm >= rawError * unit * 25.4 / 72);
        bool differs = mm + errorMm < ParagraphIndentTargetMm || mm - errorMm > ParagraphIndentTargetMm;
        Require(differs
            ? outcome == "fail" && reason == "confirmed-paragraph-indent-differs" && Text(observation, "comparison") == "outsideTarget"
            : outcome == "indeterminate" && reason == "conditional-paragraph-indent-target-in-interval"
                && Text(observation, "comparison") == "targetWithinUncertainty");
        return new(ParagraphIndentRuleId, ParagraphIndentRuleVersion, "renderedObservation", support, outcome, reason,
            "warning", citation.Clone(), Array.AsReadOnly(new[] { item.Clone() }), Array.AsReadOnly(limitations));
    }

    private static bool Near(double actual, double expected)
        => double.IsFinite(actual) && Math.Abs(actual - expected) <= 8 * Math.Abs(Math.BitIncrement(expected) - expected);
}
