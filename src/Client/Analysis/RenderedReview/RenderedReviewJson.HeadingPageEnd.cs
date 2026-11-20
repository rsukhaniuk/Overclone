using Client.Pdf;
using System.Text.Json;

namespace Client.Analysis.RenderedReview;

public static partial class RenderedReviewJson
{
    private const string HeadingPageEndRuleVersion = "1.2.0";
    private const int HeadingPageEndCharacterLimit = 8192;
    private static readonly string[] HeadingPageEndLimitations =
    [
        "minor-heading-and-page-text-coverage-author-confirmed",
        "one-upright-single-column-page",
        "heading-spacing-and-lower-page-region-unassessed",
        "unselected-pages-unassessed"
    ];
    private static readonly string[] HeadingPageEndEvidenceLimitations =
    [
        "native-extraction-does-not-establish-page-text-coverage",
        "text-line-order-conditional-on-author-confirmation"
    ];

    private static RenderedRuleResult ParseHeadingPageEnd(JsonElement row, RenderedReviewRequest request,
        int pageIndex, string outcome, string support, string reason)
    {
        Require(request.AuthorConfirmedPageTextCoverage && Text(row, "ruleVersion") == HeadingPageEndRuleVersion
            && support == "partial");
        var citation = row.GetProperty("citation");
        Keys(citation, "section", "printedPages", "pdfPages");
        Require(Text(citation, "section") == "3.2"
            && citation.GetProperty("printedPages").EnumerateArray().Select(p => p.GetInt32()).SequenceEqual(new[] { 18, 19 })
            && citation.GetProperty("pdfPages").EnumerateArray().Select(p => p.GetInt32()).SequenceEqual(new[] { 18, 19 }));
        var limitations = Codes(row.GetProperty("limitations"));
        Require(limitations.SequenceEqual(HeadingPageEndLimitations));

        var evidence = row.GetProperty("evidence");
        Require(evidence.ValueKind == JsonValueKind.Array && evidence.GetArrayLength() <= 1);
        if (evidence.GetArrayLength() == 0)
            Require(outcome == "indeterminate" && reason is "confirmed-minor-heading-range-required"
                or "ambiguous-minor-heading-ranges" or "minor-heading-coverage-unestablished"
                or "minor-heading-not-complete-single-line" or "minor-heading-page-order-unestablished");
        else
            ValidateHeadingPageEndEvidence(evidence[0], request, pageIndex, outcome, reason);

        return new(HeadingRuleId, HeadingPageEndRuleVersion, "renderedObservation", support, outcome, reason, "warning",
            citation.Clone(), Array.AsReadOnly(evidence.EnumerateArray().Select(item => item.Clone()).ToArray()),
            Array.AsReadOnly(limitations));
    }

    private static void ValidateHeadingPageEndEvidence(JsonElement item, RenderedReviewRequest request, int pageIndex,
        string outcome, string reason)
    {
        Keys(item, "pageIndex", "characterStart", "characterEnd", "rawBox", "measured", "expected", "unit",
            "method", "uncertainty", "roleProvenance", "sourceLocation", "observation");
        Require(item.GetProperty("pageIndex").GetInt32() == pageIndex && Text(item, "unit") == "lines"
            && Text(item, "method") == "confirmed-minor-heading-complete-native-text"
            && item.GetProperty("expected").ValueKind == JsonValueKind.Null && item.GetProperty("sourceLocation").ValueKind == JsonValueKind.Null);

        var role = item.GetProperty("roleProvenance");
        Keys(role, "annotationId", "role", "provenance", "certainty");
        Require(Text(role, "role") == "headingMinor" && Text(role, "provenance") == "authorLabel"
            && Text(role, "certainty") == "confirmedObservation");
        var start = item.GetProperty("characterStart").GetInt32();
        var end = item.GetProperty("characterEnd").GetInt32();
        ValidateSingleConfirmedRange(request, pageIndex, "headingMinor", role.GetProperty("annotationId").GetInt64(), start, end);
        Require(end <= HeadingPageEndCharacterLimit);

        var uncertainty = item.GetProperty("uncertainty");
        Keys(uncertainty, "absoluteError", "method", "limitations");
        Require(uncertainty.GetProperty("absoluteError").ValueKind == JsonValueKind.Null
            && Text(uncertainty, "method") == "text-line-count-with-native-ink-bound"
            && Codes(uncertainty.GetProperty("limitations")).SequenceEqual(HeadingPageEndEvidenceLimitations));

        var observation = item.GetProperty("observation");
        Keys(observation, "kind", "authorConfirmedPageTextCoverage", "textLineCount", "headingLineIndex", "effectiveBox",
            "headingInkErrorRaw", "neighbor");
        Require(Text(observation, "kind") == "confirmed-minor-heading-page-end"
            && observation.GetProperty("authorConfirmedPageTextCoverage").GetBoolean());
        int lines = observation.GetProperty("textLineCount").GetInt32(), index = observation.GetProperty("headingLineIndex").GetInt32();
        int following = item.GetProperty("measured").GetInt32();
        Require(lines is >= 2 and <= 256 && index >= 0 && index < lines && following == lines - index - 1);

        var neighbor = observation.GetProperty("neighbor");
        Keys(neighbor, "side", "characterStart", "characterEnd", "rawBox", "absoluteInkErrorRaw");
        int neighborStart = neighbor.GetProperty("characterStart").GetInt32(), neighborEnd = neighbor.GetProperty("characterEnd").GetInt32();
        Require(neighborStart >= 0 && neighborEnd > neighborStart && neighborEnd <= HeadingPageEndCharacterLimit);
        var headingBox = PositiveReviewBox(item.GetProperty("rawBox"));
        var neighborBox = PositiveReviewBox(neighbor.GetProperty("rawBox"));
        var frame = PositiveReviewBox(observation.GetProperty("effectiveBox"));
        double headingError = observation.GetProperty("headingInkErrorRaw").GetDouble();
        double neighborError = neighbor.GetProperty("absoluteInkErrorRaw").GetDouble();
        Require(headingError is > 0 and <= 2_000_000 && neighborError is > 0 and <= 2_000_000
            && WithinInkBounds(frame, headingBox, headingError) && WithinInkBounds(frame, neighborBox, neighborError)
            && Math.Max(headingBox.Left + headingError, neighborBox.Left + neighborError)
                < Math.Min(headingBox.Right - headingError, neighborBox.Right - neighborError));
        if (following == 0)
            Require(Text(neighbor, "side") == "preceding" && neighborEnd + 2 == start
                && neighborBox.Bottom - neighborError > headingBox.Top + headingError
                && outcome == "fail" && reason == "confirmed-minor-heading-is-final-page-text-line");
        else
            Require(Text(neighbor, "side") == "following" && end + 2 == neighborStart
                && headingBox.Bottom - headingError > neighborBox.Top + neighborError
                && outcome == "indeterminate" && reason == "conditional-minor-heading-has-following-page-text");
    }

    private static bool WithinInkBounds(PdfBox frame, PdfBox box, double error) =>
        box.Left - error >= frame.Left && box.Bottom - error >= frame.Bottom
        && box.Right + error <= frame.Right && box.Top + error <= frame.Top;
}
