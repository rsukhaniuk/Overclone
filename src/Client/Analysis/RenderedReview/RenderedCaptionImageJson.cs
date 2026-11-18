using System.Text;
using System.Text.Json;
using Client.Analysis.RenderedExtent;
using Client.Pdf;

namespace Client.Analysis.RenderedReview;

internal static class RenderedCaptionImageJson
{
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static string Pack(RenderedCaptionImageRequest request)
    {
        var review = request.Review;
        string result = JsonSerializer.Serialize(new
        {
            protocolVersion = 1,
            requestId = review.RequestId,
            operation = "evaluateRenderedReview",
            payload = new
            {
                review.PdfSha256, review.CompileRequestId, review.CompileEpoch, review.InputDigest, review.CaptureId,
                profile = new { id = RenderedReviewContract.ProfileId, version = RenderedCaptionImageRequest.ProfileVersion },
                review.WorkType, review.AnnotationVersion, review.PageCount, review.RequestedPages, review.Annotations,
                review.FactsSha256, captionImagePair = request.Pair
            }
        }, Camel);
        if (Encoding.UTF8.GetByteCount(result) > RenderedReviewContract.RequestMaxBytes)
            throw new ArgumentException("Caption and image review request limit.", nameof(request));
        return result;
    }

    internal static RenderedCaptionImageEvaluationReply Parse(RenderedCaptionImageRequest expected, long workerEpoch, string json)
    {
        try { return ParseCore(expected, workerEpoch, json); }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException)
        { throw new JsonException("Invalid caption and image review response shape.", error); }
    }

    private static RenderedCaptionImageEvaluationReply ParseCore(RenderedCaptionImageRequest expected, long epoch, string json)
    {
        var review = expected.Review;
        Require(RenderedExtentContract.Epoch(epoch) && json.Length <= RenderedReviewContract.ResponseMaxBytes
            && PdfHashes.ValidUtf16(json) && Encoding.UTF8.GetByteCount(json) <= RenderedReviewContract.ResponseMaxBytes);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        PdfWireParser.Unique(root);
        Require(root.ValueKind == JsonValueKind.Object && root.TryGetProperty("status", out _));
        if (Text(root, "status") == "failed")
        {
            Keys(root, "protocolVersion", "status", "error");
            var error = root.GetProperty("error");
            Keys(error, "code");
            string code = Text(error, "code");
            Require(root.GetProperty("protocolVersion").GetInt32() == 1
                && code is "InvalidInput" or "InvalidFacts" or "InvalidAnnotations" or "QuotaExceeded");
            return new(RenderedExtentEvaluationStatus.InvalidInput, review.RequestId, epoch, Code: code);
        }

        Keys(root, "protocolVersion", "requestId", "status", "payload");
        Require(root.GetProperty("protocolVersion").GetInt32() == 1
            && Text(root, "requestId") == review.RequestId && Text(root, "status") == "completed");
        var p = root.GetProperty("payload");
        Keys(p, "pdfSha256", "compileRequestId", "compileEpoch", "inputDigest", "captureId", "profile", "workType",
            "annotationVersion", "pageCount", "requestedPages", "factsSha256", "captionImagePair", "pageIndex",
            "relation", "reason", "geometry", "method", "limitations");
        Require(Text(p, "pdfSha256") == review.PdfSha256 && Text(p, "compileRequestId") == review.CompileRequestId
            && p.GetProperty("compileEpoch").GetInt64() == review.CompileEpoch && Text(p, "inputDigest") == review.InputDigest
            && Text(p, "captureId") == review.CaptureId.ToString() && Text(p, "workType") == review.WorkType
            && p.GetProperty("annotationVersion").GetInt64() == review.AnnotationVersion
            && p.GetProperty("pageCount").GetInt32() == review.PageCount && Text(p, "factsSha256") == review.FactsSha256
            && p.GetProperty("pageIndex").GetInt32() == expected.PageIndex
            && p.GetProperty("requestedPages").EnumerateArray().Select(v => v.GetInt32()).SequenceEqual(review.RequestedPages));
        var profile = p.GetProperty("profile");
        Keys(profile, "id", "version");
        Require(Text(profile, "id") == RenderedReviewContract.ProfileId && Text(profile, "version") == RenderedCaptionImageRequest.ProfileVersion);
        ValidatePair(p.GetProperty("captionImagePair"), expected.Pair);
        Require(Text(p, "method") == RenderedCaptionImageObservation.MethodCode
            && p.GetProperty("limitations").EnumerateArray().Select(v => v.GetString())
                .SequenceEqual(RenderedCaptionImageObservation.RequiredLimitations));

        var relation = Text(p, "relation") switch
        {
            "captionAbove" => RenderedCaptionImageRelation.CaptionAbove,
            "captionBelow" => RenderedCaptionImageRelation.CaptionBelow,
            "indeterminate" => RenderedCaptionImageRelation.Indeterminate,
            _ => throw new JsonException("Unknown caption and image relation.")
        };
        var reason = Text(p, "reason") switch
        {
            "measured-separation" => RenderedCaptionImageReason.MeasuredSeparation,
            "unsupported-page-rotation" => RenderedCaptionImageReason.UnsupportedPageRotation,
            "unavailable-physical-scale" => RenderedCaptionImageReason.UnavailablePhysicalScale,
            "unavailable-image-placement" => RenderedCaptionImageReason.UnavailableImagePlacement,
            "unavailable-caption-ink" => RenderedCaptionImageReason.UnavailableCaptionInk,
            "caption-ink-crosses-effective-page" => RenderedCaptionImageReason.CaptionInkCrossesEffectivePage,
            "horizontal-overlap-unestablished" => RenderedCaptionImageReason.HorizontalOverlapUnestablished,
            "vertical-separation-unestablished" => RenderedCaptionImageReason.VerticalSeparationUnestablished,
            _ => throw new JsonException("Unknown caption and image reason.")
        };
        bool measured = relation != RenderedCaptionImageRelation.Indeterminate;
        var geometry = p.GetProperty("geometry");
        Require(measured == (reason == RenderedCaptionImageReason.MeasuredSeparation)
            && geometry.ValueKind == (measured ? JsonValueKind.Object : JsonValueKind.Null));
        return new(RenderedExtentEvaluationStatus.Completed, review.RequestId, epoch,
            new(expected, relation, reason, measured ? ParseGeometry(geometry, expected, relation) : null));
    }

    private static void ValidatePair(JsonElement value, RenderedCaptionImagePair expected)
    {
        Keys(value, "captionAnnotationId", "objectIndex", "inspectionRequestId", "inspectionEpoch", "rendererLoaderSha256",
            "rendererWasmSha256", "layoutFactsSha256", "objectFactsSha256");
        Require(value.GetProperty("captionAnnotationId").GetInt64() == expected.CaptionAnnotationId
            && value.GetProperty("objectIndex").GetInt32() == expected.ObjectIndex
            && Text(value, "inspectionRequestId") == expected.InspectionRequestId
            && value.GetProperty("inspectionEpoch").GetInt64() == expected.InspectionEpoch
            && Text(value, "rendererLoaderSha256") == expected.RendererLoaderSha256
            && Text(value, "rendererWasmSha256") == expected.RendererWasmSha256
            && Text(value, "layoutFactsSha256") == expected.LayoutFactsSha256
            && Text(value, "objectFactsSha256") == expected.ObjectFactsSha256);
    }

    private static RenderedCaptionImageGeometry ParseGeometry(JsonElement value, RenderedCaptionImageRequest expected,
        RenderedCaptionImageRelation relation)
    {
        Keys(value, "captionInkBox", "imagePlacementBox", "rawSignedGap", "absoluteErrorRaw", "signedGapMm", "absoluteErrorMm");
        var caption = ReadBox(value.GetProperty("captionInkBox"));
        var image = ReadBox(value.GetProperty("imagePlacementBox"));
        double gap = Number(value, "rawSignedGap"), error = Number(value, "absoluteErrorRaw");
        double gapMm = Number(value, "signedGapMm"), errorMm = Number(value, "absoluteErrorMm");
        bool above = relation == RenderedCaptionImageRelation.CaptionAbove;
        double edgeGap = above ? caption.Bottom - image.Top : caption.Top - image.Bottom;
        var page = expected.Layout.Effective;
        double scale = expected.Layout.UserUnit.Value.GetValueOrDefault() * 25.4 / 72;
        double captionError = ValidateCaptionInk(expected, caption);
        Require(expected.Layout.Rotation == 0 && expected.Layout.UserUnit.Status == "available" && scale > 0
            && expected.Image.RawBox == image && expected.Image.Uncertainty is { } imageUncertainty
            && caption.Left - captionError >= page.Left && caption.Bottom - captionError >= page.Bottom
            && caption.Right + captionError <= page.Right && caption.Top + captionError <= page.Top
            && Math.Max(caption.Left + captionError, image.Left + imageUncertainty.AbsoluteErrorRaw)
                < Math.Min(caption.Right - captionError, image.Right - imageUncertainty.AbsoluteErrorRaw)
            && error >= captionError + imageUncertainty.AbsoluteErrorRaw && errorMm > 0
            && (above ? gap > error && gapMm > errorMm : gap < -error && gapMm < -errorMm)
            && gap == edgeGap && Near(gapMm, gap * scale) && errorMm >= error * scale * (1 - 1e-12));
        return new(caption, image, gap, error, gapMm, errorMm);
    }

    // Check the reported operands against the selected native rows. Rust owns
    // run classification and the decision to report or withhold a measurement.
    private static double ValidateCaptionInk(RenderedCaptionImageRequest expected, PdfBox reported)
    {
        double error = 0;
        PdfBox? union = null;
        for (int i = expected.Caption.CharacterStart!.Value; i < expected.Caption.CharacterEnd!.Value; i++)
        {
            var character = expected.Layout.Characters[i];
            if (character.InkBox is not { } box) continue;
            if (character.InkUncertainty is not { } uncertainty) throw new JsonException("Caption ink uncertainty is absent.");
            union = union is { } previous
                ? new(Math.Min(previous.Left, box.Left), Math.Min(previous.Bottom, box.Bottom),
                    Math.Max(previous.Right, box.Right), Math.Max(previous.Top, box.Top))
                : box;
            error = Math.Max(error, uncertainty.AbsoluteErrorRaw);
        }
        Require(reported == union);
        return error;
    }

    private static PdfBox ReadBox(JsonElement value)
    {
        Keys(value, "left", "bottom", "right", "top");
        var box = new PdfBox(Number(value, "left"), Number(value, "bottom"), Number(value, "right"), Number(value, "top"));
        Require(box.Left < box.Right && box.Bottom < box.Top
            && Math.Abs(box.Left) <= 1000000 && Math.Abs(box.Bottom) <= 1000000
            && Math.Abs(box.Right) <= 1000000 && Math.Abs(box.Top) <= 1000000);
        return box;
    }

    private static bool Near(double left, double right) => Math.Abs(left - right) <= 1e-12 * Math.Max(Math.Abs(left), Math.Abs(right));
    private static double Number(JsonElement value, string key)
        => value.GetProperty(key).GetDouble() is var n && double.IsFinite(n) && Math.Abs(n) <= 1000000000000
            ? n : throw new JsonException("Invalid bounded caption and image measurement.");
    private static string Text(JsonElement value, string key) => value.GetProperty(key).GetString() ?? throw new JsonException();
    private static void Keys(JsonElement value, params string[] keys) => PdfWireParser.Keys(value, keys);
    private static void Require(bool condition) { if (!condition) throw new JsonException("Invalid caption and image review protocol."); }
}
