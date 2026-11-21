using System.Text.Json;

namespace Client.Analysis.RenderedReview;

public static partial class RenderedReviewJson
{
    private const string HeadingSpacingRuleVersion = "1.3.0";
    private const string HeadingSpacingDifference = "confirmed-heading-baselines-differ-from-local-body";
    private const string HeadingSpacingInconclusive = "conditional-heading-body-baseline-separation-unestablished";
    private static readonly string[] HeadingSpacingLimitations =
    [
        "one-confirmed-two-line-heading-and-three-line-body-paragraph",
        "observed-baseline-intervals-only",
        "nominal-spacing-factor-unassessed",
        "unselected-headings-unassessed"
    ];

    private static RenderedRuleResult ParseHeadingSpacing(JsonElement row, RenderedReviewRequest request,
        int page, string outcome, string support, string reason)
    {
        Require(Text(row, "ruleVersion") == HeadingSpacingRuleVersion && support == "partial" && outcome == "indeterminate");
        var citation = row.GetProperty("citation");
        Keys(citation, "section", "printedPages", "pdfPages");
        Require(Text(citation, "section") == "3.2"
            && citation.GetProperty("printedPages").EnumerateArray().Select(item => item.GetInt32()).SequenceEqual(new[] { 19 })
            && citation.GetProperty("pdfPages").EnumerateArray().Select(item => item.GetInt32()).SequenceEqual(new[] { 19 }));
        var limitations = Codes(row.GetProperty("limitations"));
        Require(limitations.SequenceEqual(HeadingSpacingLimitations));
        var evidence = row.GetProperty("evidence");
        Require(evidence.ValueKind == JsonValueKind.Array && evidence.GetArrayLength() <= 1);
        if (evidence.GetArrayLength() == 0)
            Require(reason is "confirmed-heading-and-paragraph-ranges-required" or "ambiguous-heading-or-paragraph-ranges"
                or "heading-spacing-complete-adjacent-lines-required" or "heading-spacing-line-geometry-unavailable"
                or "heading-spacing-body-reference-unestablished" or "heading-spacing-physical-scale-unavailable");
        else
            ValidateHeadingSpacingEvidence(evidence[0], request, page, reason);

        return new(HeadingRuleId, HeadingSpacingRuleVersion, "renderedObservation", support, outcome, reason, "warning",
            citation.Clone(), Array.AsReadOnly(evidence.EnumerateArray().Select(item => item.Clone()).ToArray()),
            Array.AsReadOnly(limitations));
    }

    private static void ValidateHeadingSpacingEvidence(JsonElement evidence, RenderedReviewRequest request, int page, string reason)
    {
        Keys(evidence, "pageIndex", "characterStart", "characterEnd", "rawBox", "measured", "expected", "unit",
            "method", "uncertainty", "roleProvenance", "sourceLocation", "observation");
        Require(evidence.GetProperty("pageIndex").GetInt32() == page && Text(evidence, "unit") == "mm"
            && Text(evidence, "method") == "confirmed-heading-body-baseline-interval"
            && Text(evidence, "expected") == "same observed baseline interval as adjacent body text"
            && evidence.GetProperty("sourceLocation").ValueKind == JsonValueKind.Null);
        _ = PositiveReviewBox(evidence.GetProperty("rawBox"));
        var role = evidence.GetProperty("roleProvenance");
        Keys(role, "annotationId", "role", "provenance", "certainty");
        string headingRole = Text(role, "role");
        Require(headingRole is "headingMajor" or "headingMinor" && Text(role, "provenance") == "authorLabel"
            && Text(role, "certainty") == "confirmedObservation");
        int start = evidence.GetProperty("characterStart").GetInt32(), end = evidence.GetProperty("characterEnd").GetInt32();
        Require(request.Annotations.Count(a => a is not null && a.PageIndex == page && a.Role is "headingMajor" or "headingMinor") == 1);
        ValidateSingleConfirmedRange(request, page, headingRole, role.GetProperty("annotationId").GetInt64(), start, end);

        var uncertainty = evidence.GetProperty("uncertainty");
        Keys(uncertainty, "absoluteError", "method", "limitations");
        Require(Text(uncertainty, "method") == "native-outline-and-float32-bound"
            && Codes(uncertainty.GetProperty("limitations")).SequenceEqual(new[]
            {
                "conditional-on-confirmed-heading-and-body-roles", "source-text-layout-transform-error-unmeasured"
            }));
        var headingInterval = new HeadingBaselineInterval(evidence.GetProperty("measured").GetDouble(),
            uncertainty.GetProperty("absoluteError").GetDouble());
        Require(headingInterval.IsValid);

        var observation = evidence.GetProperty("observation");
        Keys(observation, "kind", "bodyAnnotationId", "bodyCharacterStart", "bodyCharacterEnd", "bodyIntervals", "comparison");
        Require(Text(observation, "kind") == "confirmed-heading-body-baseline-comparison");
        int bodyStart = observation.GetProperty("bodyCharacterStart").GetInt32(), bodyEnd = observation.GetProperty("bodyCharacterEnd").GetInt32();
        ValidateSingleConfirmedRange(request, page, "paragraph", observation.GetProperty("bodyAnnotationId").GetInt64(), bodyStart, bodyEnd);
        Require(end <= 100000 && bodyStart == end + 2 && bodyEnd <= 100000 && bodyEnd - start <= 8192);
        var bodyIntervals = observation.GetProperty("bodyIntervals");
        Require(bodyIntervals.ValueKind == JsonValueKind.Array && bodyIntervals.GetArrayLength() == 2);
        var first = ReadHeadingBaselineInterval(bodyIntervals[0]);
        var second = ReadHeadingBaselineInterval(bodyIntervals[1]);
        Require(!first.Disjoint(second));
        bool different = headingInterval.Disjoint(first) && headingInterval.Disjoint(second);
        Require(Text(observation, "comparison") == (different ? "outsideBothBodyIntervals" : "noSeparationEstablished")
            && reason == (different ? HeadingSpacingDifference : HeadingSpacingInconclusive));
    }

    private readonly record struct HeadingBaselineInterval(double Measured, double Error)
    {
        public bool IsValid => double.IsFinite(Measured + Error) && Error > 0 && Measured > Error;
        public bool Disjoint(HeadingBaselineInterval other) =>
            Measured + Error < other.Measured - other.Error || other.Measured + other.Error < Measured - Error;
    }

    private static HeadingBaselineInterval ReadHeadingBaselineInterval(JsonElement item)
    {
        Keys(item, "measuredMm", "absoluteErrorMm");
        var interval = new HeadingBaselineInterval(item.GetProperty("measuredMm").GetDouble(), item.GetProperty("absoluteErrorMm").GetDouble());
        Require(interval.IsValid);
        return interval;
    }
}
