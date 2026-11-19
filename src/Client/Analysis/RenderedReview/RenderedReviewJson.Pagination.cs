using System.Globalization;
using System.Text.Json;
using Client.Pdf;

namespace Client.Analysis.RenderedReview;

public static partial class RenderedReviewJson
{
    private const string PaginationRuleId = "KNU-PAGINATION-001";
    private const string PaginationRuleVersion = "1.1.0";
    private const long SafeInteger = 9007199254740991;
    private static readonly string[] PaginationLimitations =
    [
        "empty-text-does-not-prove-number-absence",
        "extracted-unicode-not-visual-digit-proof",
        "work-number-role-is-author-confirmed",
        "top-right-corner-criterion-unspecified",
        "uncaptured-pages-unassessed",
        "copied-appendix-own-numbering-unassessed"
    ];
    private static readonly string[] PaginationEvidenceLimitations =
    [
        "conditional-on-confirmed-work-page-number",
        "extracted-unicode-not-visual-digit-proof",
        "uncaptured-pages-unassessed"
    ];

    private static RenderedRuleResult ParsePagination(JsonElement row, RenderedReviewRequest request, int pageIndex,
        string outcome, string support, string reason)
    {
        Require(Text(row, "ruleVersion") == PaginationRuleVersion && support == "partial");
        var citation = row.GetProperty("citation");
        Keys(citation, "section", "printedPages", "pdfPages");
        Require(Text(citation, "section") == "3.3;3.16"
            && citation.GetProperty("printedPages").EnumerateArray().Select(p => p.GetInt32()).SequenceEqual(new[] { 19, 31 })
            && citation.GetProperty("pdfPages").EnumerateArray().Select(p => p.GetInt32()).SequenceEqual(new[] { 19, 31 }));
        var limitations = Codes(row.GetProperty("limitations"));
        Require(limitations.SequenceEqual(PaginationLimitations));

        var evidence = row.GetProperty("evidence");
        Require(evidence.ValueKind == JsonValueKind.Array && evidence.GetArrayLength() <= 1);
        if (evidence.GetArrayLength() == 0)
        {
            Require(outcome == "indeterminate" && reason is "confirmed-title-role-required" or "conflicting-title-role"
                or "confirmed-page-number-range-required" or "ambiguous-page-number-labels" or "page-number-non-ascii-or-out-of-range"
                or "page-number-unavailable-ink" or "page-number-physical-scale-unavailable" or "page-number-ink-outside-effective-page");
            Require(reason != "confirmed-title-role-required" || pageIndex == 0);
            return new(PaginationRuleId, PaginationRuleVersion, "renderedObservation", support, outcome, reason, "warning",
                citation.Clone(), [], Array.AsReadOnly(limitations));
        }

        var item = evidence[0];
        ValidatePaginationEvidence(item, request, pageIndex, outcome, reason);
        return new(PaginationRuleId, PaginationRuleVersion, "renderedObservation", support, outcome, reason, "warning",
            citation.Clone(), Array.AsReadOnly(new[] { item.Clone() }), Array.AsReadOnly(limitations));
    }

    private static void ValidatePaginationEvidence(JsonElement item, RenderedReviewRequest request, int pageIndex,
        string outcome, string reason)
    {
        Keys(item, "pageIndex", "characterStart", "characterEnd", "rawBox", "measured", "expected", "unit",
            "method", "uncertainty", "roleProvenance", "sourceLocation", "observation");
        Require(item.GetProperty("pageIndex").GetInt32() == pageIndex
            && Text(item, "unit") == "text" && Text(item, "method") == "confirmed-page-number-native-ink"
            && item.GetProperty("sourceLocation").ValueKind == JsonValueKind.Null);

        int start = item.GetProperty("characterStart").GetInt32(), end = item.GetProperty("characterEnd").GetInt32();
        string literal = Text(item, "measured");
        Require(start >= 0 && end > start && end <= 100000 && literal.Length == end - start
            && literal.Length is >= 1 and <= 32 && literal.All(ch => ch is >= '0' and <= '9'));
        bool parsed = long.TryParse(literal, NumberStyles.None, CultureInfo.InvariantCulture, out long digitValue);
        Require(parsed && digitValue <= SafeInteger);

        var provenance = item.GetProperty("roleProvenance");
        Keys(provenance, "annotationId", "role", "provenance", "certainty");
        long annotationId = provenance.GetProperty("annotationId").GetInt64();
        Require(annotationId is > 0 and <= SafeInteger && Text(provenance, "role") == "pageNumber"
            && Text(provenance, "provenance") == "authorLabel" && Text(provenance, "certainty") == "confirmedObservation");
        var pageAnnotations = request.Annotations.Where(a => a is not null && a.PageIndex == pageIndex).ToArray();
        var numbers = pageAnnotations.Where(a => a.Role == "pageNumber").ToArray();
        Require(numbers.Length == 1 && numbers[0].AnnotationId == annotationId && ConfirmedAuthor(numbers[0])
            && numbers[0].CharacterStart == start && numbers[0].CharacterEnd == end
            && request.Annotations.Count(a => a?.AnnotationId == annotationId) == 1);

        var uncertainty = item.GetProperty("uncertainty");
        Keys(uncertainty, "absoluteError", "method", "limitations");
        Require(uncertainty.GetProperty("absoluteError").ValueKind == JsonValueKind.Null
            && Text(uncertainty, "method") == "native-character-unicode"
            && Codes(uncertainty.GetProperty("limitations")).SequenceEqual(PaginationEvidenceLimitations));

        var observation = item.GetProperty("observation");
        Keys(observation, "kind", "titleAnnotationId", "physicalPageNumber", "asciiDigitValue", "comparison", "placement");
        int physicalPageNumber = pageIndex + 1;
        Require(Text(observation, "kind") == "confirmed-page-number"
            && observation.GetProperty("physicalPageNumber").GetInt32() == physicalPageNumber
            && observation.GetProperty("asciiDigitValue").GetInt64() == digitValue);
        var titles = pageAnnotations.Where(a => a.Role == "title").ToArray();
        var titleAnnotation = observation.GetProperty("titleAnnotationId");
        var expectedNumber = item.GetProperty("expected");
        string comparison = Text(observation, "comparison");
        if (pageIndex == 0)
        {
            long titleId = titleAnnotation.GetInt64();
            Require(titleId is > 0 and <= SafeInteger && titleId != annotationId
                && titles.Length == 1 && titles[0].AnnotationId == titleId && ConfirmedAuthor(titles[0])
                && titles[0].CharacterStart is null && titles[0].CharacterEnd is null
                && request.Annotations.Count(a => a?.AnnotationId == titleId) == 1
                && expectedNumber.ValueKind == JsonValueKind.Null && comparison == "title-number-present"
                && outcome == "fail" && reason == "confirmed-title-number-present");
        }
        else
        {
            bool match = digitValue == physicalPageNumber;
            Require(titles.Length == 0 && titleAnnotation.ValueKind == JsonValueKind.Null
                && expectedNumber.GetInt32() == physicalPageNumber && comparison == (match ? "index-match" : "index-mismatch")
                && outcome == "indeterminate" && reason == (match ? "confirmed-page-number-index-match" : "confirmed-page-number-index-mismatch"));
        }

        var placement = observation.GetProperty("placement");
        Keys(placement, "media", "effective", "topClearanceMm", "rightClearanceMm", "absoluteErrorMm");
        var ink = PositiveReviewBox(item.GetProperty("rawBox"));
        var media = PositiveReviewBox(placement.GetProperty("media"));
        var effective = PositiveReviewBox(placement.GetProperty("effective"));
        Require(Contains(media, effective) && Contains(effective, ink));
        double top = placement.GetProperty("topClearanceMm").GetDouble();
        double right = placement.GetProperty("rightClearanceMm").GetDouble();
        double error = placement.GetProperty("absoluteErrorMm").GetDouble();
        Require(top is >= 0 and <= 1000000000000 && right is >= 0 and <= 1000000000000 && error is > 0 and <= 1000000000000);
    }

    private static bool ConfirmedAuthor(RenderedRoleAnnotation annotation)
        => annotation.Provenance == "authorLabel" && annotation.Certainty == "confirmedObservation"
            && annotation.SourceFileId is null && annotation.SourceSpan is null;

    private static PdfBox PositiveReviewBox(JsonElement value)
    {
        var box = PdfWireParser.Box(value);
        Require(box.Left < box.Right && box.Bottom < box.Top && Math.Abs(box.Left) <= 1_000_000
            && Math.Abs(box.Bottom) <= 1_000_000 && Math.Abs(box.Right) <= 1_000_000 && Math.Abs(box.Top) <= 1_000_000);
        return box;
    }

    private static bool Contains(PdfBox outer, PdfBox inner)
        => outer.Left <= inner.Left && outer.Bottom <= inner.Bottom && outer.Right >= inner.Right && outer.Top >= inner.Top;
}
