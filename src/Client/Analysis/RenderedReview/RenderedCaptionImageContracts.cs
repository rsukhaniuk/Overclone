using System.Buffers;
using System.Text;
using System.Text.Json;
using Client.Analysis.RenderedExtent;
using Client.Pdf;

namespace Client.Analysis.RenderedReview;

public sealed record RenderedCaptionImagePair(long CaptionAnnotationId, int ObjectIndex, string InspectionRequestId,
    long InspectionEpoch, string RendererLoaderSha256, string RendererWasmSha256,
    string LayoutFactsSha256, string ObjectFactsSha256);

/// <summary>One explicitly selected role and image from the same admitted PDF inspection.</summary>
public sealed class RenderedCaptionImageRequest
{
    public const string ProfileVersion = "1.1.0";
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private RenderedCaptionImageRequest(RenderedReviewRequest review, RenderedCaptionImagePair pair,
        PdfLayoutFacts layout, PdfImagePlacement image, byte[] factsUtf8)
    {
        Review = review;
        Pair = pair;
        Layout = layout;
        Image = image;
        FactsUtf8 = factsUtf8;
    }

    public RenderedReviewRequest Review { get; }
    public RenderedCaptionImagePair Pair { get; }
    public RenderedRoleAnnotation Caption => Review.Annotations[0];
    public int PageIndex => Layout.PageIndex;
    internal PdfLayoutFacts Layout { get; }
    internal PdfImagePlacement Image { get; }
    internal ReadOnlyMemory<byte> FactsUtf8 { get; }

    /// <summary>Uses the review identity and explicit caption selection, and supplies the exact aggregate facts hash.</summary>
    public static RenderedCaptionImageRequest Create(RenderedReviewRequest review, PdfObjectLayoutRequest inspectionRequest,
        PdfObjectLayoutOutcome inspection, PdfRuntimeIdentity expectedRuntime, long captionAnnotationId, int objectIndex)
    {
        if (inspectionRequest is not { IsValid: true, IncludeTextLayout: true }
            || inspection is not { Status: PdfInspectionStatus.Completed, Facts: { ScanComplete: true } objects,
                LayoutFacts: { SchemaVersion: 2, Complete: true } layout,
                LayoutCapabilities: { SchemaVersion: 2, CoordinateSpace: "pdf-raw-unrotated-bottom-left",
                    GlyphInkMethod: "embedded-static-font-outline-opaque-fill" } capabilities }
            || expectedRuntime is null || inspection.Runtime != expectedRuntime
            || !PdfHashes.IsSha(expectedRuntime.LoaderSha256) || !PdfHashes.IsSha(expectedRuntime.WasmSha256)
            || !RenderedExtentContract.RequestId(inspection.RequestId) || !RenderedExtentContract.Epoch(inspection.WorkerEpoch)
            || inspection.DocumentSha256 != inspectionRequest.Document.Sha256
            || inspection.PageIndex != inspectionRequest.PageIndex
            || layout.PageIndex != inspection.PageIndex || objects.PageIndex != inspection.PageIndex
            || objects.PageCount != layout.PageCount || layout.PageCount > inspectionRequest.Budget.Pages
            || objects.Media != layout.Media || objects.Crop != layout.Crop || objects.Effective != layout.Effective
            || objects.Rotation != layout.Rotation || objects.UserUnit != layout.UserUnit)
            throw new ArgumentException("Invalid atomic caption and image inspection.", nameof(inspection));

        if (review is not { RequestedPages.Count: 1, Annotations.Count: <= 512 }
            || review.PdfSha256 != inspection.DocumentSha256 || review.PageCount != layout.PageCount
            || review.RequestedPages[0] != layout.PageIndex || !RenderedExtentContract.Epoch(captionAnnotationId))
            throw new ArgumentException("Invalid caption and image review identity.", nameof(review));

        var matchingCaptions = review.Annotations.Where(a => a?.AnnotationId == captionAnnotationId).Take(2).ToArray();
        var caption = matchingCaptions.Length == 1 ? matchingCaptions[0] : null;
        if (caption is null || caption.PageIndex != layout.PageIndex || caption.Role is not ("figureCaption" or "tableCaption")
            || caption.Certainty != "confirmedObservation"
            || caption.Provenance is not ("authorLabel" or "literalSourceAndNativeMap" or "validatedNativeClassification")
            || caption.CharacterStart is not int start || caption.CharacterEnd is not int end
            || start < 0 || end <= start || end > layout.Characters.Count
            || (caption.SourceFileId is null) != (caption.SourceSpan is null)
            || caption.SourceFileId is string fileId && (caption.Provenance == "authorLabel" || !PdfHashes.ValidUtf16(fileId)
                || Encoding.UTF8.GetByteCount(fileId) is < 1 or > 128)
            || caption.SourceSpan is { } span && (span.StartByte < 0 || span.EndByte < span.StartByte))
            throw new ArgumentException("Select one confirmed caption on the inspected page.", nameof(captionAnnotationId));

        var image = objects.Images.FirstOrDefault(candidate => candidate.ObjectIndex == objectIndex);
        if (image is null) throw new ArgumentException("Select a direct image from this inspection.", nameof(objectIndex));
        if (!HasExactDigest(layout.Json, inspection.LayoutFactsSha256) || !HasExactDigest(objects.Json, objects.Sha256))
            throw new ArgumentException("Invalid atomic caption and image fact digests.", nameof(inspection));

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 2);
            writer.WriteString("documentSha256", inspection.DocumentSha256);
            writer.WriteStartArray("pages");
            writer.WriteStartObject();
            writer.WritePropertyName("capabilities");
            JsonSerializer.Serialize(writer, capabilities, Camel);
            writer.WritePropertyName("facts");
            writer.WriteRawValue(layout.Json);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WritePropertyName("objectLayout");
            writer.WriteRawValue(objects.Json);
            writer.WriteEndObject();
        }
        if (buffer.WrittenCount > RenderedReviewContract.FactsMaxBytes)
            throw new ArgumentException("Caption and image facts exceed the evaluation quota.", nameof(inspection));
        byte[] bytes = buffer.WrittenSpan.ToArray();
        var frozen = review with
        {
            RequestedPages = Array.AsReadOnly(new[] { layout.PageIndex }),
            Annotations = Array.AsReadOnly(new[] { caption }),
            FactsSha256 = PdfHashes.Of(bytes)
        };
        if (!frozen.IsValid) throw new ArgumentException("Invalid caption and image review identity.", nameof(review));
        var pair = new RenderedCaptionImagePair(captionAnnotationId, objectIndex, inspection.RequestId, inspection.WorkerEpoch,
            expectedRuntime.LoaderSha256, expectedRuntime.WasmSha256, inspection.LayoutFactsSha256!, objects.Sha256);
        return new(frozen, pair, layout, image, bytes);
    }

    private static bool HasExactDigest(string json, string? sha256)
        => PdfHashes.IsSha(sha256) && json.Length <= RenderedReviewContract.FactsMaxBytes && PdfHashes.ValidUtf16(json)
            && Encoding.UTF8.GetByteCount(json) <= RenderedReviewContract.FactsMaxBytes
            && PdfHashes.Of(Encoding.UTF8.GetBytes(json)) == sha256;
}

public enum RenderedCaptionImageRelation { CaptionAbove, CaptionBelow, Indeterminate }

public enum RenderedCaptionImageReason
{
    MeasuredSeparation, UnsupportedPageRotation, UnavailablePhysicalScale, UnavailableImagePlacement, UnavailableCaptionInk,
    CaptionInkCrossesEffectivePage, HorizontalOverlapUnestablished, VerticalSeparationUnestablished
}

public sealed record RenderedCaptionImageGeometry(PdfBox CaptionInkBox, PdfBox ImagePlacementBox,
    double RawSignedGap, double AbsoluteErrorRaw, double SignedGapMm, double AbsoluteErrorMm);

public sealed class RenderedCaptionImageObservation
{
    internal const string MethodCode = "conditional-native-caption-image-vertical-gap";
    internal static readonly IReadOnlyList<string> RequiredLimitations = Array.AsReadOnly(new[]
    {
        "explicit-pair-only", "positive-gap-means-caption-above-image", "image-placement-not-ink",
        "image-visibility-not-established", "source-ctm-accumulation-error-unmeasured", "no-normative-outcome"
    });

    internal RenderedCaptionImageObservation(RenderedCaptionImageRequest request, RenderedCaptionImageRelation relation,
        RenderedCaptionImageReason reason, RenderedCaptionImageGeometry? geometry)
    {
        Request = request;
        Relation = relation;
        Reason = reason;
        Geometry = geometry;
    }

    public RenderedCaptionImageRequest Request { get; }
    public RenderedCaptionImageRelation Relation { get; }
    public RenderedCaptionImageReason Reason { get; }
    public RenderedCaptionImageGeometry? Geometry { get; }
    public int PageIndex => Request.PageIndex;
    public string Method => MethodCode;
    public IReadOnlyList<string> Limitations => RequiredLimitations;
}

public sealed record RenderedCaptionImageEvaluationReply(RenderedExtentEvaluationStatus Status, string RequestId, long WorkerEpoch,
    RenderedCaptionImageObservation? Observation = null, string Code = "");

public interface IRenderedCaptionImageEvaluator
{
    Task<RenderedCaptionImageEvaluationReply> EvaluateCaptionImageAsync(RenderedCaptionImageRequest request,
        CancellationToken cancellationToken = default);
}
