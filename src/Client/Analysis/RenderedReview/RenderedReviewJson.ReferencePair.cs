using System.Text.Json;
using Client.Analysis.RenderedExtent;
using Client.Pdf;

namespace Client.Analysis.RenderedReview;

public static partial class RenderedReviewJson
{
    internal const string ReferencePairProfile = "knu-csc-2025-rendered-reference-pair";
    private const string ReferencePairVersion = "1.0.0";
    private const string VerifiedReferencePair = "confirmed-printed-number-pair";

    private static object ReferencePairWire(RenderedReferencePair pair) => new {
        scope = "mainWork", rendererLoaderSha256 = pair.Runtime.LoaderSha256, rendererWasmSha256 = pair.Runtime.WasmSha256,
        citation = pair.Citation, entry = pair.Entry
    };

    internal static string PackReferencePair(RenderedReferencePairRequest request)
    {
        if (request is not { IsValid: true }) throw new ArgumentException("Invalid printed reference pair request.");
        var r = request.Review;
        return JsonSerializer.Serialize(new { protocolVersion = 1, requestId = r.RequestId, operation = "evaluateRenderedReview",
            payload = new { r.PdfSha256, r.CompileRequestId, r.CompileEpoch, r.InputDigest, r.CaptureId,
                profile = new { id = ReferencePairProfile, version = ReferencePairVersion },
                r.WorkType, r.AnnotationVersion, r.PageCount, r.RequestedPages, r.Annotations, r.FactsSha256,
                printedReferencePair = ReferencePairWire(request.Pair) } }, Camel);
    }

    internal static RenderedReferencePairReply ParseReferencePair(RenderedReferencePairRequest request, long epoch, string json)
    {
        try
        {
            Require(request.IsValid && RenderedExtentContract.Epoch(epoch));
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            PdfWireParser.Unique(root);
            Keys(root, "protocolVersion", "requestId", "status", "payload");
            var r = request.Review;
            Require(root.GetProperty("protocolVersion").GetInt32() == 1 && Text(root, "requestId") == r.RequestId
                && Text(root, "status") == "completed");
            var p = root.GetProperty("payload");
            Keys(p, "pdfSha256", "compileRequestId", "compileEpoch", "inputDigest", "captureId", "profile", "workType", "annotationVersion",
                "pageCount", "requestedPages", "factsSha256", "guidelineSha256", "printedReferencePair", "comparison", "reason", "numbers",
                "roleProvenance", "sourceLocation", "limitations");
            Require(Text(p, "pdfSha256") == r.PdfSha256 && Text(p, "compileRequestId") == r.CompileRequestId
                && p.GetProperty("compileEpoch").GetInt64() == r.CompileEpoch && Text(p, "inputDigest") == r.InputDigest
                && Text(p, "captureId") == r.CaptureId.ToString() && Text(p, "workType") == r.WorkType
                && p.GetProperty("annotationVersion").GetInt64() == r.AnnotationVersion && p.GetProperty("pageCount").GetInt32() == r.PageCount
                && p.GetProperty("requestedPages").EnumerateArray().Select(v => v.GetInt32()).SequenceEqual(r.RequestedPages)
                && Text(p, "factsSha256") == r.FactsSha256 && Text(p, "guidelineSha256") == RenderedExtentContract.GuidelineSha256);
            var profile = p.GetProperty("profile");
            Keys(profile, "id", "version");
            Require(Text(profile, "id") == ReferencePairProfile && Text(profile, "version") == ReferencePairVersion
                && JsonElement.DeepEquals(p.GetProperty("printedReferencePair"), JsonSerializer.SerializeToElement(ReferencePairWire(request.Pair), Camel))
                && Text(p, "roleProvenance") == "authorConfirmedPdfSearchSelections" && p.GetProperty("sourceLocation").ValueKind == JsonValueKind.Null
                && Codes(p.GetProperty("limitations")).SequenceEqual(new[] { "selected-printed-numbers-only", "citation-key-and-bibliography-completeness-unassessed" }));
            string reason = Text(p, "reason");
            var numbers = p.GetProperty("numbers");
            var comparison = Text(p, "comparison") switch
            {
                "numbersMatch" => RenderedReferenceComparison.NumbersMatch,
                "numbersDiffer" => RenderedReferenceComparison.NumbersDiffer,
                "indeterminate" => RenderedReferenceComparison.Indeterminate,
                _ => throw new JsonException()
            };
            int? citation = null, entry = null;
            if (comparison == RenderedReferenceComparison.Indeterminate)
            {
                Require(numbers.ValueKind == JsonValueKind.Null
                    && (request.Pair.Overlapping ? reason == "overlapping-selection-ranges"
                        : reason is "selection-glyph-mismatch" or "unsupported-printed-number" or "glyph-ink-unavailable"));
            }
            else
            {
                Keys(numbers, "citation", "entry");
                citation = numbers.GetProperty("citation").GetInt32();
                entry = numbers.GetProperty("entry").GetInt32();
                Require(reason == VerifiedReferencePair && !request.Pair.Overlapping
                    && citation == request.Pair.Citation.PrintedNumber() && entry == request.Pair.Entry.PrintedNumber()
                    && ((citation == entry) == (comparison == RenderedReferenceComparison.NumbersMatch)));
            }
            return new(RenderedExtentEvaluationStatus.Completed, r.RequestId, epoch, new(request, comparison, reason, citation, entry));
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        { throw new JsonException("Invalid printed reference response shape.", error); }
    }
}
