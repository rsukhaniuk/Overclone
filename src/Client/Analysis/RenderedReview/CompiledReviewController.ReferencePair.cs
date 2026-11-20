using Client.Analysis.RenderedExtent;
using Client.Pdf;
using Client.Pdf.Preview;
using System.Text.Json;

namespace Client.Analysis.RenderedReview;

public sealed partial class CompiledReviewController
{
    public sealed record ReferenceSelectionCapture(RenderedReferenceSelection Selection, long PreviewGeneration);

    private RenderedExtentAuthority? _referencePairAuthority;
    private PdfRuntimeIdentity? _referencePairRuntime;
    private ReferenceSelectionCapture? _referenceCitation, _referenceEntry;
    private RenderedReferencePairObservation? _referencePairObservation;

    private bool ReferenceSelectionsCurrent => !_disposed && _referencePairAuthority?.Current(_workspace, _compile) == true;
    public ReferenceSelectionCapture? ReferenceCitation => ReferenceSelectionsCurrent ? _referenceCitation : null;
    public ReferenceSelectionCapture? ReferenceEntry => ReferenceSelectionsCurrent ? _referenceEntry : null;
    public RenderedReferencePairObservation? ReferencePairObservation => ReferenceSelectionsCurrent ? _referencePairObservation : null;
    public string ReferencePairNotice { get; private set; } = "";
    public bool CanCaptureReferenceSelection => !_disposed && !Busy && _evaluator is IRenderedReferencePairEvaluator
        && RenderedExtentAuthority.Capture(_workspace, _compile)?.Inspector is IPdfLayoutInspector;
    public bool CanCheckReferencePair => CanCaptureReferenceSelection && ReferenceCitation is not null && ReferenceEntry is not null;

    public void CaptureReferenceSelection(RenderedReferencePairRole role, PdfPreviewState state, PdfPreviewHit hit)
    {
        if (!CanCaptureReferenceSelection || RenderedExtentAuthority.Capture(_workspace, _compile) is not { } authority
            || !ReferenceEquals(_compile.Preview.State, state) || state.Status != PdfPreviewStatus.Available
            || state.DocumentSha256 != authority.Output.OutputHash?.Hex || state.Runtime is not { } runtime
            || state.Generation <= 0 || state.NativeRequestId is not { } requestId || state.NativeEpoch is not { } epoch
            || state.PageIndex != hit.PageIndex || state.SelectedHit < 0 || state.SelectedHit >= state.Search.Hits.Count
            || !ReferenceEquals(state.Search.Hits[state.SelectedHit], hit) || hit.CharacterCount is < 1 or > RenderedReferenceSelection.MaxCharacters
            || (long)hit.StartCharacterIndex + hit.CharacterCount > 100000)
            return;
        var selection = new RenderedReferenceSelection(requestId, epoch, hit.PageIndex, hit.StartCharacterIndex,
            hit.StartCharacterIndex + hit.CharacterCount, hit.GlyphText);
        if (!PdfHashes.IsSha(runtime.LoaderSha256) || !PdfHashes.IsSha(runtime.WasmSha256) || !selection.IsValid
            || !selection.Matches(state.Glyphs, g => g.Index, g => g.UnicodeValue)) return;
        if (!ReferenceSelectionsCurrent || _referencePairRuntime != runtime) ResetReferencePair();
        var capture = new ReferenceSelectionCapture(selection, state.Generation);
        switch (role)
        {
            case RenderedReferencePairRole.Citation: _referenceCitation = capture; break;
            case RenderedReferencePairRole.Entry: _referenceEntry = capture; break;
            default: return;
        }
        _referencePairAuthority = authority; _referencePairRuntime = runtime; _referencePairObservation = null;
        ReferencePairNotice = "";
        Notify();
    }

    private void ResetReferencePair()
    {
        _referencePairAuthority = null; _referencePairRuntime = null;
        _referenceCitation = null; _referenceEntry = null; _referencePairObservation = null;
        ReferencePairNotice = "";
    }

    public void ClearReferencePair()
    {
        if (_disposed || Busy) return;
        ResetReferencePair(); Notify();
    }

    public async Task CheckReferencePairAsync(CancellationToken cancellationToken = default)
    {
        if (Busy || !ReferenceSelectionsCurrent || _referencePairAuthority is not { Inspector: IPdfLayoutInspector inspector } authority
            || _evaluator is not IRenderedReferencePairEvaluator evaluator || _referencePairRuntime is not { } runtime
            || _referenceCitation is not { Selection: var citation } || _referenceEntry is not { Selection: var entry }) return;
        var pair = new RenderedReferencePair(runtime, citation, entry);
        int[] selectedPages = pair.Pages;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        _active = stop; _activeAuthority = authority;
        long intent = ++_intent;
        bool Current() => !_disposed && _intent == intent && authority.Current(_workspace, _compile);
        void CheckCurrent() { if (!Current()) throw new OperationCanceledException(); stop.Token.ThrowIfCancellationRequested(); }
        _referencePairObservation = null;
        ReferencePairNotice = "Rechecking the selected printed numbers on the current PDF.";
        Notify();
        try
        {
            CheckCurrent();
            var output = authority.Output;
            var bytes = output.ToPayloadArray();
            if (bytes is null || bytes.LongLength != output.OutputLength) throw new JsonException();
            var document = authority.CreateDocument(bytes);
            if (document.Sha256 != output.OutputHash!.Value.Hex) throw new JsonException();
            int pageCount = 0;
            var pages = new List<(PdfLayoutCapabilities Capabilities, PdfLayoutFacts Facts)>();
            foreach (int index in selectedPages)
            {
                var inspected = await inspector.InspectLayoutAsync(new(document, index, RenderedExtentAuthority.InspectionBudget(document)), stop.Token);
                CheckCurrent();
                if (inspected.Status != PdfInspectionStatus.Completed || inspected.Capabilities is not { SchemaVersion: 2 })
                { ReferencePairNotice = "Indeterminate. Complete PDF glyph facts are unavailable for a selected page."; return; }
                if (inspected.DocumentSha256 != document.Sha256 || inspected.Runtime != pair.Runtime
                    || !RenderedExtentContract.RequestId(inspected.RequestId) || !RenderedExtentContract.Epoch(inspected.WorkerEpoch)
                    || inspected.WorkerEpoch <= Math.Max(citation.SelectionEpoch, entry.SelectionEpoch)
                    || ReferenceEquals(_lastInspector, inspector) && inspected.WorkerEpoch <= _inspectionEpoch
                    || inspected.Facts is not { Complete: true, SchemaVersion: 2 } facts || facts.PageIndex != index
                    || facts.PageCount <= selectedPages[^1] || pageCount != 0 && facts.PageCount != pageCount)
                    throw new JsonException();
                _lastInspector = inspector; _inspectionEpoch = inspected.WorkerEpoch;
                pageCount = facts.PageCount;
                foreach (var selection in new[] { citation, entry })
                    if (selection.PageIndex == index && !selection.Matches(facts.Characters, g => g.Index, g => g.Unicode))
                        throw new JsonException();
                pages.Add((inspected.Capabilities, facts));
            }

            // The aggregate remains bounded; keep each native JSON page unchanged.
            if (pages.Sum(p => System.Text.Encoding.UTF8.GetByteCount(p.Facts.Json)) > RenderedReviewContract.FactsMaxBytes)
            { ReferencePairNotice = "Indeterminate. The selected pages exceed the glyph-facts limit."; return; }
            using var batch = new MemoryStream();
            using (var writer = new Utf8JsonWriter(batch))
            {
                writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1);
                writer.WriteString("documentSha256", document.Sha256); writer.WriteStartArray("pages");
                foreach (var page in pages)
                {
                    writer.WriteStartObject(); writer.WritePropertyName("capabilities");
                    JsonSerializer.Serialize(writer, page.Capabilities, Camel);
                    writer.WritePropertyName("facts"); writer.WriteRawValue(page.Facts.Json);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            if (batch.Length > RenderedReviewContract.FactsMaxBytes)
            { ReferencePairNotice = "Indeterminate. The selected pages exceed the glyph-facts limit."; return; }
            var factBytes = batch.ToArray();
            var review = new RenderedReviewRequest(Guid.NewGuid().ToString("N"), document.Sha256, output.RequestId.Value.ToString("N"),
                output.WorkerEpoch, output.InputContentDigest.Hex, output.SnapshotIdentity.CaptureId, "unspecified", 0,
                pageCount, Array.AsReadOnly(selectedPages), [], PdfHashes.Of(factBytes));
            var request = new RenderedReferencePairRequest(review, pair, factBytes);
            CheckCurrent();
            var reply = await evaluator.EvaluateReferencePairAsync(request, stop.Token);
            CheckCurrent();
            if (reply.Status != RenderedExtentEvaluationStatus.Completed)
            { ReferencePairNotice = "Indeterminate. The rendered number evaluator was unavailable."; return; }
            if (reply.RequestId != review.RequestId || !RenderedExtentContract.Epoch(reply.WorkerEpoch) || reply.WorkerEpoch <= _evaluationEpoch
                || reply.Observation is not { } observation || !ReferenceEquals(observation.Request, request))
                throw new JsonException();
            _evaluationEpoch = reply.WorkerEpoch; _referencePairObservation = observation;
            ReferencePairNotice = "";
        }
        catch (OperationCanceledException)
        {
            if (ReferenceSelectionsCurrent && ReferenceEquals(_active, stop))
                ReferencePairNotice = deadline.IsCancellationRequested
                    ? "Indeterminate. The comparison reached its time limit." : "Comparison cancelled.";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (Current()) ReferencePairNotice = "Indeterminate. The selected PDF glyphs could not be verified.";
        }
        finally
        {
            if (ReferenceEquals(_active, stop)) { _active = null; _activeAuthority = null; }
            if (!_disposed) Notify();
        }
    }
}
