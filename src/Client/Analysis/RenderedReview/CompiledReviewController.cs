using Client.Analysis.RenderedExtent;
using Client.Compilation.Presentation;
using Client.Pdf;
using Client.Workspace;
using System.Text;
using System.Text.Json;

namespace Client.Analysis.RenderedReview;

/// <summary>Explicit, revision-bound review; no background compile or fixes.
/// One actual inspection/evaluation task owns admission until settlement. Only
/// one page's exact facts are retained; aggregate report is separately bounded.</summary>
public sealed class CompiledReviewController : IDisposable
{
    private readonly WorkspaceCoordinator _workspace;
    private readonly CompileWorkspaceSession _compile;
    private readonly IRenderedReviewEvaluator _evaluator;
    private readonly Func<Action, Task> _dispatch;
    private readonly RenderedRoleBinding? _binding;
    private Dictionary<RenderedRoleAnnotation, RenderedRoleBinding.Proof> _sourceBindings = new(ReferenceEqualityComparer.Instance);
    private RenderedExtentAuthority? _reportAuthority, _annotationAuthority;
    private RenderedReviewReport? _report;
    private IReadOnlyList<RenderedRoleAnnotation> _annotations = Array.AsReadOnly(Array.Empty<RenderedRoleAnnotation>());
    private CancellationTokenSource? _active;
    private long _intent, _annotationVersion, _annotationId, _evaluationEpoch, _inspectionEpoch;
    private IPdfLayoutInspector? _lastInspector;
    private string _notice = "Explicitly check measured PDF facts. The finite faculty profile does not certify compliance.";
    private bool _disposed;
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public CompiledReviewController(WorkspaceCoordinator workspace, CompileWorkspaceSession compile,
        IRenderedReviewEvaluator evaluator, Func<Action, Task> dispatch, RenderedRoleBinding? binding = null)
    {
        _workspace = workspace; _compile = compile; _evaluator = evaluator; _dispatch = dispatch; _binding = binding;
        workspace.WorkspaceChanged += SourceChanged; workspace.RevisionCommitted += Saved; compile.Changed += SourceChanged;
    }
    public event Action? Changed;
    public bool Busy => _active is not null;
    public bool CanCancel => !_disposed && _active is { IsCancellationRequested: false };
    public bool AnnotationsCurrent => _annotations.Count == 0 || _annotationAuthority?.Current(_workspace, _compile) == true && SourceProofsCurrent();
    public bool CanCheck => !_disposed && !Busy && AnnotationsCurrent && RenderedExtentAuthority.Capture(_workspace, _compile)?.Inspector is IPdfLayoutInspector;
    public bool CanBindSourceEvent => !_disposed && !Busy && _binding is not null && AnnotationsCurrent
        && WorkType != "unspecified" && _annotations.Count < 512 && RenderedExtentAuthority.Capture(_workspace, _compile)?.Inspector is not null;
    public string WorkType { get; private set; } = "unspecified";
    public IReadOnlyList<RenderedRoleAnnotation> Annotations => _annotations;
    /// <summary>Range metadata for display, including stale bindings. Neither
    /// headingText nor captionText is a section extent or glyph-ink metric.</summary>
    public IReadOnlyList<RenderedRoleBinding.Proof> SourceBindings => Array.AsReadOnly(_annotations
        .Where(_sourceBindings.ContainsKey).Select(a => _sourceBindings[a]).ToArray());
    public long AnnotationVersion => _annotationVersion;
    public bool ReportCurrent => !_disposed && _report is not null && _report.Evidence.AnnotationVersion == _annotationVersion
        && AnnotationsCurrent && _reportAuthority?.Current(_workspace, _compile) == true;
    public RenderedReviewReport? Report => ReportCurrent ? _report : null;
    public string Notice => _disposed ? "Rendered review closed." : !AnnotationsCurrent ? "Relabel page roles after a new compile or reopen before checking."
        : _report is not null && !ReportCurrent ? "Project or PDF changed. Check the current compiled revision again." : _notice;
    public void SelectWorkType(string value)
    {
        if (_disposed || !RenderedExtentContract.WorkType(value) || value == WorkType) return;
        WorkType = value; Invalidate("Work type changed. Check the current PDF again."); Notify();
    }
    public void SelectAnnotations(IReadOnlyList<RenderedRoleAnnotation> annotations)
    {
        if (_disposed) return;
        if (annotations is null || annotations.Count > 512 || _annotationVersion >= 9007199254740991
            || _annotationId > 9007199254740991 - annotations.Count(AuthorAnnotation)
            || annotations.Any(a => !AuthorAnnotation(a) && !RetainSourceAnnotation(a)))
        { Invalidate("Invalid or stale page-role annotations. Bind source events explicitly; copied machine labels cannot be confirmed."); Notify(); return; }
        var copy = annotations.Select(a => AuthorAnnotation(a) ? a with { AnnotationId = ++_annotationId } : a).ToArray();
        if (Overlapping(copy)) { Invalidate("Incompatible overlapping page roles were refused."); Notify(); return; }
        _sourceBindings = copy.Where(_sourceBindings.ContainsKey).ToDictionary(a => a, a => _sourceBindings[a],
            (IEqualityComparer<RenderedRoleAnnotation>)ReferenceEqualityComparer.Instance);
        _annotations = Array.AsReadOnly(copy); _annotationVersion++;
        _annotationAuthority = copy.Length == 0 ? null : RenderedExtentAuthority.Capture(_workspace, _compile);
        Invalidate("Page roles changed. Author labels scope observations; they do not prove rendered roles. Relabel after a new compile or reopen."); Notify();
    }
    private bool RetainSourceAnnotation(RenderedRoleAnnotation? annotation) => annotation is not null && _binding is not null
        && _annotations.Any(a => ReferenceEquals(a, annotation)) && _sourceBindings.TryGetValue(annotation, out var proof)
        && ReferenceEquals(proof.Annotation, annotation) && _binding.IsCurrent(proof, WorkType);
    private bool SourceProofsCurrent() => _annotations.All(a => AuthorAnnotation(a) || RetainSourceAnnotation(a));
    /// <summary>Removal does not reconfirm remaining stale labels against a new
    /// PDF. Clear via SelectAnnotations([]) is always an explicit safe path.</summary>
    public void RemoveAnnotation(long annotationId)
    {
        if (_disposed || _annotationVersion >= 9007199254740991 || !_annotations.Any(a => a.AnnotationId == annotationId)) return;
        var removed = _annotations.Where(a => a.AnnotationId == annotationId).ToArray();
        _annotations = Array.AsReadOnly(_annotations.Where(a => a.AnnotationId != annotationId).ToArray());
        foreach (var a in removed) _sourceBindings.Remove(a);
        _annotationVersion++; if (_annotations.Count == 0) _annotationAuthority = null;
        Invalidate("Page role removed. Remaining labels retain their original compiled/source authority."); Notify();
    }

    public async Task<bool> BindSourceEventAsync(AnalysisReport report, int eventIndex, CancellationToken cancellationToken = default)
    {
        if (_disposed || Busy) return false;
        if (!CanBindSourceEvent || _annotationVersion >= 9007199254740991 || _annotationId >= 9007199254740991)
        { _notice = "Source binding requires a current compile, matching source report and explicit work type. Clear stale labels before rebinding."; Notify(); return false; }
        var authority = RenderedExtentAuthority.Capture(_workspace, _compile);
        if (authority is null) return false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        _active = stop; _activeAuthority = authority; long intent = ++_intent, version = _annotationVersion, id = ++_annotationId;
        string workType = WorkType; var annotations = _annotations; _report = null; _reportAuthority = null;
        using var notification = stop.Token.Register(() => Observe(_dispatch(() => { if (!_disposed && ReferenceEquals(_active, stop)) Notify(); })));
        bool Current() => !_disposed && !stop.IsCancellationRequested && _intent == intent && _annotationVersion == version
            && WorkType == workType && ReferenceEquals(_annotations, annotations) && AnnotationsCurrent && authority.Current(_workspace, _compile);
        _notice = "Binding a literal source observation to actual native PDF characters. This does not prove a section extent or glyph ink."; Notify();
        try
        {
            if (!Current()) return false;
            // Await the actual borrowed binding task, including noncooperative
            // inspection/epoch work. Logical cancel never frees this reservation.
            var result = await _binding!.BindAsync(new(report, eventIndex, id, workType), stop.Token);
            if (!Current()) return false;
            if (result.Value is not { } proof || !ReferenceEquals(proof.Output, authority.Output))
            { _notice = "Source role was not bound: " + result.Code + ". No machine annotation was added."; return false; }
            if (!await _binding.VerifyCurrentAsync(proof, workType, stop.Token) || !Current() || !_binding.IsCurrent(proof, workType, stop.Token)) return false;
            var copy = annotations.Append(proof.Annotation).ToArray();
            if (Overlapping(copy)) { _notice = "The bound literal range overlaps another incompatible page role. No annotation was added."; return false; }
            _sourceBindings.Add(proof.Annotation, proof); _annotations = Array.AsReadOnly(copy);
            _annotationAuthority = authority; _annotationVersion++;
            _notice = "Bound literal " + proof.RangeKind + " only. Section extent, glyph ink and normative compliance are not established.";
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (Current()) _notice = "Source role binding was unavailable. No annotation was added."; return false; }
        finally
        {
            if (!_disposed && stop.IsCancellationRequested && _intent == intent)
                _notice = deadline.IsCancellationRequested ? "Source binding reached120 seconds. Retry is explicit; no annotation was added."
                    : "Source binding cancelled; no annotation was added.";
            if (ReferenceEquals(_active, stop)) { _active = null; _activeAuthority = null; } if (!_disposed) Notify();
        }
    }
    private static bool AuthorAnnotation(RenderedRoleAnnotation? a) => a is not null && a.PageIndex is >= 0 and < 512
        && a.Provenance == "authorLabel" && a.Certainty is "confirmedObservation" or "tentative" && a.SourceFileId is null && a.SourceSpan is null
        && a.Role is "title" or "abstract" or "contents" or "introduction" or "body" or "conclusions" or "references" or "appendix" or "headingMajor"
            or "headingMinor" or "paragraph" or "pageNumber" or "figureCaption" or "tableCaption" or "footnote" or "note" or "illustration"
        && (a.CharacterStart is null && a.CharacterEnd is null || a.CharacterStart is >= 0 && a.CharacterEnd > a.CharacterStart);
    private static bool Overlapping(RenderedRoleAnnotation[] annotations)
    {
        static bool Container(RenderedRoleAnnotation a, RenderedRoleAnnotation b) => a.Provenance == "authorLabel" && a.Role is "body" or "appendix"
            && b.Role is "headingMajor" or "headingMinor" or "paragraph" or "pageNumber" or "illustration" or "figureCaption" or "tableCaption" or "footnote" or "note";
        for (int i = 0; i < annotations.Length; i++) for (int j = i + 1; j < annotations.Length; j++)
        {
            var a = annotations[i]; var b = annotations[j];
            bool overlap = a.CharacterStart is null || b.CharacterStart is null || a.CharacterStart < b.CharacterEnd && b.CharacterStart < a.CharacterEnd;
            if (a.PageIndex == b.PageIndex && overlap && !Container(a, b) && !Container(b, a)) return true;
        }
        return false;
    }
    private void Saved(WorkspaceSaveReceipt _) => SourceChanged();
    private void SourceChanged()
    {
        if (_disposed) return;
        if (_reportAuthority is { } r && !r.Current(_workspace, _compile) || _annotationAuthority is { } a && (!a.Current(_workspace, _compile) || !SourceProofsCurrent()))
            Invalidate("Project or PDF changed. Rendered review is stale; relabel after a new compile or reopen.");
        // Even when no report exists, an active operation must stop immediately
        // on a compile/edit authority change. Its actual task retains admission.
        if (_active is not null && _activeAuthority?.Current(_workspace, _compile) != true)
            Invalidate("Project or PDF changed. Rendered review cancelled as stale.");
        Notify();
    }
    private RenderedExtentAuthority? _activeAuthority;
    private void Invalidate(string message) { _intent++; _report = null; _reportAuthority = null; _notice = message; _active?.Cancel(); }
    public void Cancel() { if (CanCancel) { Invalidate("Rendered review cancelled. Continue or retry is explicit."); Notify(); } }
    public async Task CheckAsync(IReadOnlyList<int>? selectedPages = null, CancellationToken cancellationToken = default)
    {
        if (_disposed || Busy) return;
        if (!AnnotationsCurrent) { Notify(); return; }
        var authority = RenderedExtentAuthority.Capture(_workspace, _compile);
        if (authority?.Inspector is not IPdfLayoutInspector inspector) { _notice = "Layout inspection unavailable for the current PDF renderer."; Notify(); return; }
        int[]? selection = selectedPages?.ToArray();
        if (selection is not null && (selection.Length is < 1 or > 512 || selection.Any(i => i is < 0 or >= 512)
            || selection.Zip(selection.Skip(1)).Any(p => p.First >= p.Second))) { _notice = "Choose unique ascending page indexes within the current PDF."; Notify(); return; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        _active = stop; _activeAuthority = authority; long intent = ++_intent, annotationVersion = _annotationVersion;
        using var notification = stop.Token.Register(() => Observe(_dispatch(() => { if (!_disposed && ReferenceEquals(_active, stop)) Notify(); })));
        string workType = WorkType; var annotations = _annotations; _report = null; _reportAuthority = null;
        var results = new Dictionary<string, RenderedRuleResult>(StringComparer.Ordinal); var pages = new List<RenderedReviewPageEvidence>();
        PdfRuntimeIdentity? runtime = null; RenderedExtentEvidence? compilation = null;
        bool Current() => !_disposed && _intent == intent && annotationVersion == _annotationVersion && WorkType == workType
            && AnnotationsCurrent && authority.Current(_workspace, _compile);
        void CheckCurrent() { if (!Current()) throw new OperationCanceledException(); stop.Token.ThrowIfCancellationRequested(); }
        async Task VerifyBindings()
        {
            CheckCurrent();
            foreach (var proof in annotations.Where(_sourceBindings.ContainsKey).Select(a => _sourceBindings[a]).ToArray())
            {
                if (_binding is null || !await _binding.VerifyCurrentAsync(proof, workType, stop.Token)) throw new OperationCanceledException();
                CheckCurrent();
            }
        }
        _notice = "Inspecting pages sequentially. Standards coverage stays partial; nominal font size does not measure glyph ink."; Notify();
        try
        {
            await VerifyBindings(); CheckCurrent(); var payload = authority.Output.ToPayloadArray();
            if (payload is null || payload.LongLength != authority.Output.OutputLength) throw new JsonException();
            var document = new PdfDocument(payload);
            if (document.Sha256 != authority.Output.OutputHash!.Value.Hex) throw new JsonException();
            int total = selection?.Length ?? 1; int position = 0;
            while (position < total)
            {
                CheckCurrent(); int index = selection is null ? position : selection[position];
                var inspected = await inspector.InspectLayoutAsync(new(document, index, new PdfInspectionBudget(Pages: 512)), stop.Token);
                CheckCurrent(); await VerifyBindings(); CheckCurrent();
                if (inspected.Status != PdfInspectionStatus.Completed)
                { _notice = $"Rendered review stopped: {inspected.Status} ({inspected.Code}). Completed pages remain an incomplete observation."; return; }
                if (inspected.DocumentSha256 != document.Sha256 || !RenderedExtentContract.RequestId(inspected.RequestId)
                    || !RenderedExtentContract.Epoch(inspected.WorkerEpoch) || ReferenceEquals(_lastInspector, inspector) && inspected.WorkerEpoch <= _inspectionEpoch
                    || inspected.Runtime is not { } measuredRuntime || !PdfHashes.IsSha(measuredRuntime.LoaderSha256) || !PdfHashes.IsSha(measuredRuntime.WasmSha256)
                    || inspected.Facts is not { Complete: true } facts || facts.PageIndex != index || inspected.Capabilities is null
                    || runtime is not null && runtime != measuredRuntime) throw new JsonException();
                _lastInspector = inspector; _inspectionEpoch = inspected.WorkerEpoch; runtime = measuredRuntime;
                if (position == 0)
                {
                    if (selection is not null && selection.Any(i => i >= facts.PageCount)) throw new ArgumentException();
                    total = selection?.Length ?? facts.PageCount;
                }
                if (annotations.Any(a => a.PageIndex >= facts.PageCount || a.PageIndex == index && a.CharacterEnd > facts.Characters.Count))
                { _notice = "Page-role annotations do not fit the current inspected PDF. Relabel before checking."; return; }
                foreach (var proof in annotations.Where(a => a.PageIndex == index && _sourceBindings.ContainsKey(a)).Select(a => _sourceBindings[a]))
                {
                    // A later renderer/layout API must preserve the exact native
                    // character indexes certified by this binding. Ink boxes are
                    // deliberately not equated with older text correlation boxes.
                    if (proof.Runtime != measuredRuntime || proof.NativeCharacters.Any(c => c.Index < 0 || c.Index >= facts.Characters.Count
                        || facts.Characters[c.Index].Index != c.Index || facts.Characters[c.Index].Unicode != c.UnicodeValue)) throw new JsonException();
                }
                using var factDoc = JsonDocument.Parse(facts.Json);
                byte[] factBytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, documentSha256 = document.Sha256,
                    pages = new[] { new { capabilities = inspected.Capabilities, facts = factDoc.RootElement } } }, Camel);
                if (factBytes.Length > RenderedReviewContract.FactsMaxBytes) { _notice = "Page facts exceed the evaluation quota; coverage is incomplete."; return; }
                var input = new RenderedReviewRequest(Guid.NewGuid().ToString("N"), document.Sha256, authority.Output.RequestId.Value.ToString("N"),
                    authority.Output.WorkerEpoch, authority.Output.InputContentDigest.Hex, authority.Output.SnapshotIdentity.CaptureId, workType,
                    annotationVersion, facts.PageCount, Array.AsReadOnly(new[] { index }), Array.AsReadOnly(annotations.Where(a => a.PageIndex == index).ToArray()), PdfHashes.Of(factBytes));
                CheckCurrent(); var reply = await _evaluator.EvaluateAsync(input, factBytes, stop.Token); CheckCurrent(); await VerifyBindings(); CheckCurrent();
                if (reply.Status != RenderedExtentEvaluationStatus.Completed)
                { _notice = $"Rendered evaluator stopped: {reply.Status}. Coverage is incomplete."; return; }
                if (reply.RequestId != input.RequestId || !RenderedExtentContract.Epoch(reply.WorkerEpoch) || reply.WorkerEpoch <= _evaluationEpoch || reply.ResponseJson is null) throw new JsonException();
                _evaluationEpoch = reply.WorkerEpoch;
                var observation = RenderedReviewJson.Parse(input, reply.ResponseJson);
                foreach (var result in observation.Results) results[result.RuleId] = results.TryGetValue(result.RuleId, out var previous) ? Merge(previous, result) : result;
                var pageEvidence = new RenderedReviewPageEvidence(index, inspected.RequestId, inspected.WorkerEpoch, input.FactsSha256, input.RequestId, reply.WorkerEpoch);
                pages.Add(pageEvidence); position++;
                compilation ??= new(document.Sha256, document.Length, input.CompileRequestId, input.CompileEpoch, input.InputDigest, input.CaptureId,
                    inspected.RequestId, inspected.WorkerEpoch, measuredRuntime, reply.WorkerEpoch);
                var report = new RenderedReviewReport(Array.AsReadOnly(results.Values.ToArray()), new(compilation, authority.Output.ContentIdentity!, annotationVersion, Array.AsReadOnly(pages.ToArray())),
                    total, position, position == total, position == total ? "SelectedPagesCollectedStandardsPartial" : "PageCollectionIncomplete");
                if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(report, Camel)) > RenderedReviewContract.ResponseMaxBytes || results.Count > 512)
                { _notice = "Rendered report limit reached; coverage is incomplete. Check remaining pages explicitly."; return; }
                CheckCurrent(); _reportAuthority = authority; _report = report;
                _notice = position == total ? "Measured selected pages. The finite faculty profile and ink/style coverage remain partial; overall compliance is not assessed."
                    : $"Measured {position} of {total} pages; coverage is incomplete."; Notify();
            }
        }
        catch (OperationCanceledException)
        { if (!_disposed && Current()) _notice = deadline.IsCancellationRequested ? "Review collection reached120 seconds. Continue with remaining pages explicitly; coverage is incomplete."
                : "Rendered review cancelled; coverage is incomplete."; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (!_disposed && Current()) { _report = null; _reportAuthority = null; _notice = "Rendered review rejected invalid, mismatched or unavailable measured facts."; } }
        finally { if (ReferenceEquals(_active, stop)) { _active = null; _activeAuthority = null; } if (!_disposed) Notify(); }
    }
    private static RenderedRuleResult Merge(RenderedRuleResult a, RenderedRuleResult b)
    {
        static int Rank(string value) => value switch { "fail" => 5, "indeterminate" => 4, "unavailable" => 3, "pass" => 2, _ => 1 };
        var worst = Rank(a.Outcome) >= Rank(b.Outcome) ? a : b;
        return worst with { Evidence = Array.AsReadOnly(a.Evidence.Concat(b.Evidence).ToArray()),
            SupportState = a.SupportState == b.SupportState ? a.SupportState : "partial",
            Limitations = Array.AsReadOnly(a.Limitations.Concat(b.Limitations).Distinct().ToArray()) };
    }
    private void Notify() { if (!_disposed) Changed?.Invoke(); }
    private static void Observe(Task task) => _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _intent++; _active?.Cancel(); _report = null;
        _workspace.WorkspaceChanged -= SourceChanged; _workspace.RevisionCommitted -= Saved; _compile.Changed -= SourceChanged;
        _ = DisposeEvaluatorAsync();
    }
    private async Task DisposeEvaluatorAsync() { try { await _evaluator.DisposeAsync(); } catch (Exception e) when (e is not OutOfMemoryException) { } }
}
