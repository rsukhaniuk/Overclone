using Client.Analysis.RenderedExtent;
using Client.Compilation.Provisioning;
using Client.Pdf;
using System.Text.Json;

namespace Client.Analysis.RenderedReview;

public sealed partial class CompiledReviewController
{
    private sealed record CaptionImageDraft(RenderedExtentAuthority Authority, RenderedRoleAnnotation Caption,
        long AnnotationVersion, string WorkType, PdfObjectLayoutRequest Request, PdfObjectLayoutOutcome Inspection);

    private static readonly PdfRuntimeIdentity CaptionImageRuntime = new(
        LocalInstalledRuntimeProviders.ObjectLayoutPdfLoaderHash, LocalInstalledRuntimeProviders.ObjectLayoutPdfWasmHash);
    private CaptionImageDraft? _captionImageDraft;
    private RenderedCaptionImageObservation? _captionImageObservation;
    private IPdfObjectLayoutInspector? _lastObjectInspector;
    private long _objectInspectionEpoch;

    public IReadOnlyList<RenderedRoleAnnotation> ConfirmedCaptions => AnnotationsCurrent
        ? _annotations.Where(ConfirmedCaption).ToArray() : [];
    public bool CanInspectCaptionImages => !_disposed && !Busy && AnnotationsCurrent
        && _evaluator is IRenderedCaptionImageEvaluator
        && RenderedExtentAuthority.Capture(_workspace, _compile)?.Inspector is IPdfObjectLayoutInspector;
    public long? InspectedCaptionId => _captionImageDraft is { } draft && CaptionDraftCurrent(draft)
        ? draft.Caption.AnnotationId : null;
    public IReadOnlyList<PdfImagePlacement> InspectedImages => InspectedCaptionId is not null
        && _captionImageDraft?.Inspection.Facts is { } facts ? facts.Images : [];
    public RenderedCaptionImageObservation? CaptionImageObservation => InspectedCaptionId is not null ? _captionImageObservation : null;

    private static bool ConfirmedCaption(RenderedRoleAnnotation annotation) => annotation.Role is "figureCaption" or "tableCaption"
        && annotation.Certainty == "confirmedObservation" && annotation.CharacterStart is >= 0
        && annotation.CharacterEnd > annotation.CharacterStart;

    private bool CaptionDraftCurrent(CaptionImageDraft draft) => !_disposed && draft.AnnotationVersion == _annotationVersion
        && draft.WorkType == WorkType && AnnotationsCurrent && _annotations.Any(a => ReferenceEquals(a, draft.Caption))
        && draft.Authority.Current(_workspace, _compile);

    private void ClearCaptionImages() { _captionImageDraft = null; _captionImageObservation = null; }

    /// <summary>The first gesture captures one page. The second measures only
    /// the direct image explicitly picked from that exact capture.</summary>
    public async Task CheckCaptionImagesAsync(long annotationId, int? objectIndex = null, CancellationToken cancellationToken = default)
    {
        if (!CanInspectCaptionImages || _evaluator is not IRenderedCaptionImageEvaluator evaluator
            || RenderedExtentAuthority.Capture(_workspace, _compile) is not { Inspector: IPdfObjectLayoutInspector inspector } authority
            || _annotations.FirstOrDefault(a => a.AnnotationId == annotationId && ConfirmedCaption(a)) is not { } caption) return;
        var draft = _captionImageDraft;
        if (objectIndex is { } selected && (draft is null || !CaptionDraftCurrent(draft) || !ReferenceEquals(draft.Caption, caption)
            || draft.Inspection.Facts?.Images.Any(image => image.ObjectIndex == selected) != true)) return;

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        _active = stop; _activeAuthority = authority;
        long intent = ++_intent, annotationVersion = _annotationVersion;
        string workType = WorkType;
        _captionImageObservation = null;
        if (objectIndex is null) _captionImageDraft = null;
        using var notification = stop.Token.Register(() => Observe(_dispatch(() => { if (!_disposed && ReferenceEquals(_active, stop)) Notify(); })));
        bool Current() => !_disposed && _intent == intent && _annotationVersion == annotationVersion && WorkType == workType
            && AnnotationsCurrent && authority.Current(_workspace, _compile);
        async Task RequireCurrentAsync()
        {
            if (!Current()) throw new OperationCanceledException();
            stop.Token.ThrowIfCancellationRequested();
            if (_sourceBindings.TryGetValue(caption, out var proof)
                && (_binding is null || !await _binding.VerifyCurrentAsync(proof, workType, stop.Token))) throw new OperationCanceledException();
            if (!Current()) throw new OperationCanceledException();
            stop.Token.ThrowIfCancellationRequested();
        }
        _notice = objectIndex is null ? "Inspecting direct images on the caption's PDF page." : "Measuring the selected caption and image.";
        Notify();
        try
        {
            await RequireCurrentAsync();
            if (objectIndex is not int index)
            {
                var payload = authority.Output.ToPayloadArray();
                if (payload is null || payload.LongLength != authority.Output.OutputLength) throw new JsonException();
                var document = authority.CreateDocument(payload);
                if (document.Sha256 != authority.Output.OutputHash!.Value.Hex) throw new JsonException();
                var request = new PdfObjectLayoutRequest(document, caption.PageIndex, RenderedExtentAuthority.InspectionBudget(document), IncludeTextLayout: true);
                var inspection = await inspector.InspectObjectLayoutAsync(request, stop.Token);
                await RequireCurrentAsync();
                if (inspection.Status != PdfInspectionStatus.Completed)
                { _notice = $"Image placement inspection was unavailable ({inspection.Status})."; return; }
                if (inspection.Runtime != (authority.ExpandedRenderer ?? CaptionImageRuntime) || inspection.DocumentSha256 != document.Sha256
                    || inspection.PageIndex != caption.PageIndex || !RenderedExtentContract.RequestId(inspection.RequestId)
                    || !RenderedExtentContract.Epoch(inspection.WorkerEpoch)
                    || ReferenceEquals(_lastObjectInspector, inspector) && inspection.WorkerEpoch <= _objectInspectionEpoch
                    || ReferenceEquals(_lastInspector, inspector) && inspection.WorkerEpoch <= _inspectionEpoch
                    || inspection is not { Facts: { ScanComplete: true } images, LayoutFacts: { Complete: true } layout }
                    || caption.CharacterEnd > layout.Characters.Count || layout.PageIndex != caption.PageIndex || images.PageIndex != caption.PageIndex)
                    throw new JsonException();
                if (_sourceBindings.TryGetValue(caption, out var proof) && (proof.Runtime != inspection.Runtime
                    || proof.NativeCharacters.Any(c => c.Index < 0 || c.Index >= layout.Characters.Count
                        || layout.Characters[c.Index].Index != c.Index || layout.Characters[c.Index].Unicode != c.UnicodeValue)))
                    throw new JsonException();
                _lastObjectInspector = inspector; _objectInspectionEpoch = inspection.WorkerEpoch;
                if (inspector is IPdfLayoutInspector layoutInspector)
                { _lastInspector = layoutInspector; _inspectionEpoch = inspection.WorkerEpoch; }
                _captionImageDraft = new(authority, caption, annotationVersion, workType, request, inspection);
                _notice = images.Images.Count == 0 ? "No direct images were found on this PDF page." : "Select the image that goes with this caption.";
                return;
            }

            var activeDraft = draft!;
            var output = authority.Output;
            var review = new RenderedReviewRequest(Guid.NewGuid().ToString("N"), activeDraft.Request.Document.Sha256,
                output.RequestId.Value.ToString("N"), output.WorkerEpoch, output.InputContentDigest.Hex, output.SnapshotIdentity.CaptureId,
                workType, annotationVersion, activeDraft.Inspection.Facts!.PageCount, [caption.PageIndex], [caption], string.Empty);
            var input = RenderedCaptionImageRequest.Create(review, activeDraft.Request, activeDraft.Inspection, authority.ExpandedRenderer ?? CaptionImageRuntime, annotationId, index);
            var reply = await evaluator.EvaluateCaptionImageAsync(input, stop.Token);
            await RequireCurrentAsync();
            if (!ReferenceEquals(_captionImageDraft, activeDraft) || !CaptionDraftCurrent(activeDraft)) return;
            if (reply.Status != RenderedExtentEvaluationStatus.Completed)
            { _notice = $"Caption and image measurement stopped ({reply.Status})."; return; }
            if (reply.RequestId != input.Review.RequestId || !RenderedExtentContract.Epoch(reply.WorkerEpoch) || reply.WorkerEpoch <= _evaluationEpoch
                || reply.Observation is not { } observation || !ReferenceEquals(observation.Request, input)) throw new JsonException();
            _evaluationEpoch = reply.WorkerEpoch;
            _captionImageObservation = observation;
            _notice = "Measured the selected caption and image.";
        }
        catch (OperationCanceledException)
        {
            if (Current()) _notice = deadline.IsCancellationRequested ? "Caption and image measurement reached its time limit."
                : "Caption and image measurement cancelled.";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (Current())
            { ClearCaptionImages(); _notice = "The caption and image measurements could not be verified for this PDF."; }
        }
        finally
        {
            if (ReferenceEquals(_active, stop)) { _active = null; _activeAuthority = null; }
            if (!_disposed) Notify();
        }
    }
}
