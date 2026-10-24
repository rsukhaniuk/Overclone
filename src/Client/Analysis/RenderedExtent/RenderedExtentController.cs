using System.Text.Json;
using Client.Compilation.Presentation;
using Client.Pdf;
using Client.Workspace;
using Client.Workspace.Domain;

namespace Client.Analysis.RenderedExtent;

/// <summary>UI-context orchestration. One whole-check reservation survives
/// logical cancellation/staleness/timeout until every ACTUAL foreign task settles.
/// Borrows workspace/compile/inspector; owns evaluator cleanup only.</summary>
public sealed class RenderedExtentController : IDisposable
{
    private readonly WorkspaceCoordinator _workspace;
    private readonly CompileWorkspaceSession _compile;
    private readonly IRenderedExtentEvaluator _evaluator;
    private readonly Func<Action, Task> _dispatch;
    private readonly TimeProvider _clock;
    private Operation? _active;
    private RenderedExtentAuthority? _reportAuthority;
    private RenderedExtentAuthority? _rangesAuthority;
    private (ProjectId? Project, Guid Session) _view;
    private long _intent, _evaluationEpoch, _inspectionEpoch;
    private IPdfInspector? _lastInspector;
    private bool _disposed;

    public RenderedExtentController(WorkspaceCoordinator workspace, CompileWorkspaceSession compile,
        IRenderedExtentEvaluator evaluator, Func<Action, Task> dispatch, TimeProvider? clock = null)
    {
        _workspace = workspace; _compile = compile; _evaluator = evaluator; _dispatch = dispatch; _clock = clock ?? TimeProvider.System;
        _view = View(); workspace.WorkspaceChanged += SourceChanged; workspace.RevisionCommitted += Saved; compile.Changed += SourceChanged;
    }
    public event Action? Changed;
    public string WorkType { get; private set; } = "unspecified";
    public RenderedSectionRanges? Sections { get; private set; }
    public bool SectionRangesCurrent => Sections is null || _rangesAuthority?.Current(_workspace, _compile) == true;
    private RenderedExtentCheckState _state;
    private RenderedExtentReport? _cachedReport;
    private bool HasCurrentReport => !_disposed && _cachedReport is not null && _reportAuthority?.Current(_workspace, _compile) == true;
    // A borrowed service may dispose without a Changed event. Public reads must
    // never expose its stale cached report or a misleading Completed notice.
    // These views only validate authority: no mutation, notification or work.
    public RenderedExtentCheckState State
    {
        get => _disposed ? RenderedExtentCheckState.Disposed
            : _cachedReport is not null && !HasCurrentReport || _active is { Interrupted: null } op && !Current(op)
                ? RenderedExtentCheckState.Stale : _state;
        private set => _state = value;
    }
    public RenderedExtentReport? Report
    {
        get => HasCurrentReport ? _cachedReport : null;
        private set => _cachedReport = value;
    }
    public bool Busy => _active is not null;
    public bool CanCancel => !_disposed && _active is { Interrupted: null } op && Volatile.Read(ref op.Expired) == 0 && !op.Stop.IsCancellationRequested;
    public bool CanCheck => !_disposed && !Busy && SectionRangesCurrent && (Sections is null || Sections.IsValidFor(512))
        && RenderedExtentAuthority.Capture(_workspace, _compile) is not null;
    public bool ReportCurrent => HasCurrentReport;
    public string Notice => !SectionRangesCurrent && !_disposed
        ? "Page ranges belong to an earlier PDF. Review and confirm them for the current PDF before checking."
        : State switch
    {
        RenderedExtentCheckState.Idle => "Choose a work type and explicitly check the current compiled PDF.",
        RenderedExtentCheckState.Inspecting => "Inspecting total pages in the current PDF…",
        RenderedExtentCheckState.Evaluating => "Evaluating the limited usual-length observation locally…",
        RenderedExtentCheckState.Completed => "Measured total PDF pages; overall compliance is not assessed.",
        RenderedExtentCheckState.Stale => "PDF, project, selection or work type changed. Check the current PDF again.",
        RenderedExtentCheckState.Cancelled => "Rendered check cancelled.",
        RenderedExtentCheckState.TimedOut => "Rendered check timed out; retry is explicit.",
        RenderedExtentCheckState.InspectorBusy => "PDF inspection is busy; no check was queued. Try again when ready.",
        RenderedExtentCheckState.RendererUnavailable => "Supported PDF renderer unavailable. Install or recheck its local resources.",
        RenderedExtentCheckState.EvaluationUnavailable => "Local extent evaluator unavailable. Check offline app provisioning; source analysis is independent.",
        RenderedExtentCheckState.InvalidResponse => "Rendered observation rejected: response did not match its bounded request.",
        RenderedExtentCheckState.InvalidRanges => "Page ranges must be ordered, non-overlapping, within the current PDF, and before marked appendices.",
        RenderedExtentCheckState.Limits => "Rendered observation exceeded an inspection or transport limit.",
        RenderedExtentCheckState.NativeFailure => "The PDF inspector could not inspect this document.",
        RenderedExtentCheckState.Disposed => "Rendered checks closed.",
        _ => "Rendered check unavailable; no observation was published."
    };
    private (ProjectId? Project, Guid Session) View() => (_workspace.Project?.Id, _workspace.SessionId);
    public void SelectSections(RenderedSectionRanges? ranges)
    {
        if (_disposed || ranges == Sections && SectionRangesCurrent) return;
        Sections = ranges; _rangesAuthority = ranges is null ? null : RenderedExtentAuthority.Capture(_workspace, _compile);
        _intent++; Report = null; _reportAuthority = null;
        if (_active is { } op) Interrupt(op, RenderedExtentCheckState.Stale);
        else State = ranges is not null && !ranges.IsValidFor(512) ? RenderedExtentCheckState.InvalidRanges : RenderedExtentCheckState.Stale;
        Notify(); // Explicit inputs, never an implicit inspection or evaluation.
    }
    public void SelectWorkType(string value)
    {
        if (_disposed || !RenderedExtentContract.WorkType(value) || value == WorkType) return;
        WorkType = value; _intent++; Report = null; _reportAuthority = null;
        if (_active is { } op) Interrupt(op, RenderedExtentCheckState.Stale);
        else State = RenderedExtentCheckState.Stale;
        Notify(); // No implicit inspection/evaluation.
    }
    private void Saved(WorkspaceSaveReceipt receipt) => SourceChanged();
    private void SourceChanged()
    {
        if (_disposed) return;
        if (_view != View())
        {
            _view = View(); WorkType = "unspecified"; Sections = null; _rangesAuthority = null; _intent++;
            Report = null; _reportAuthority = null;
            if (_active is { } op) Interrupt(op, RenderedExtentCheckState.Stale);
            else State = RenderedExtentCheckState.Idle;
        }
        // Preview refreshes also raise CompileSession.Changed: only actual
        // authority changes invalidate, never the notification by itself.
        if (_active is { Interrupted: null } active && !active.Authority.Current(_workspace, _compile))
        { _intent++; Interrupt(active, RenderedExtentCheckState.Stale); }
        if (_reportAuthority is { } authority && !authority.Current(_workspace, _compile))
        { _intent++; Report = null; _reportAuthority = null; State = RenderedExtentCheckState.Stale; }
        // Availability can change while idle with neither a report nor an
        // active operation (notably the first successful compile). The child
        // has no changing parent parameters: notify its view even when there
        // was nothing to invalidate. This never starts or cancels work merely
        // because a same-authority preview notification arrived.
        Notify();
    }
    public void Cancel()
    {
        if (!CanCancel || _active is not { } op) return;
        _intent++; Interrupt(op, RenderedExtentCheckState.Cancelled); Notify();
    }
    private void Interrupt(Operation op, RenderedExtentCheckState reason)
    {
        if (_disposed || !ReferenceEquals(_active, op) || op.Interrupted is not null) return;
        op.Interrupted = reason; State = reason; Report = null; _reportAuthority = null;
        op.Stop.Cancel(); // Logical settlement ONLY, no slot retirement here.
    }
    private bool Current(Operation op) => !_disposed && ReferenceEquals(_active, op) && op.Interrupted is null
        && op.Intent == _intent && op.WorkType == WorkType && op.Sections == Sections
        && SectionRangesCurrent && op.Authority.Current(_workspace, _compile);
    private bool Continue(Operation op, Stage stage)
    {
        if (_disposed || !ReferenceEquals(_active, op)) return false;
        if (op.Interrupted is not null) return false;
        if (!Current(op)) { Interrupt(op, RenderedExtentCheckState.Stale); return false; }
        if (Volatile.Read(ref op.Expired) != 0) { Interrupt(op, RenderedExtentCheckState.TimedOut); return false; }
        if (stage.Token.IsCancellationRequested)
        { Interrupt(op, stage.Deadline.IsCancellationRequested ? RenderedExtentCheckState.TimedOut : RenderedExtentCheckState.Cancelled); return false; }
        return true;
    }
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || Busy) return; // No queue or implicit retry.
        if (!SectionRangesCurrent || Sections is not null && !Sections.IsValidFor(512))
        { State = RenderedExtentCheckState.InvalidRanges; Notify(); return; }
        var authority = RenderedExtentAuthority.Capture(_workspace, _compile);
        Report = null; _reportAuthority = null;
        if (authority is null) { State = RenderedExtentCheckState.Stale; Notify(); return; }
        var op = new Operation(authority, ++_intent, WorkType, Sections);
        _active = op; State = RenderedExtentCheckState.Inspecting; Notify();
        try
        {
            using var inspection = new Stage(this, op, 30000, cancellationToken);
            if (!Continue(op, inspection)) return;
            // Slot reserved BEFORE clones/hashes. Owned payload only; no PDF
            // bytes/hash/count or historical label supplied by UI callers.
            var bytes = authority.Output.ToPayloadArray();
            if (bytes is null || bytes.LongLength != authority.Output.OutputLength)
            { State = RenderedExtentCheckState.InvalidResponse; return; }
            var document = new PdfDocument(bytes);
            if (document.Sha256 != authority.Output.OutputHash!.Value.Hex)
            { State = RenderedExtentCheckState.InvalidResponse; return; }
            if (!Continue(op, inspection)) return;
            if (authority.Inspector is not { } inspector)
            { State = RenderedExtentCheckState.RendererUnavailable; return; }
            var request = new PdfInspectRequest(document, 0, new PdfInspectionBudget(Pages: 512), search: null,
                raster: false, rasterWidth: 1, rasterHeight: 1, inspectActions: false);
            var outcome = await inspector.InspectAsync(request, inspection.Token); // ACTUAL task, never WaitAsync/WhenAny.
            if (!Continue(op, inspection)) return;
            if (outcome is null) { State = RenderedExtentCheckState.InvalidResponse; return; }
            if (outcome.Status != PdfInspectionStatus.Completed) { State = InspectionFailure(outcome.Status); return; }
            if (!ValidInspection(outcome, document) || ReferenceEquals(_lastInspector, inspector) && outcome.WorkerEpoch <= _inspectionEpoch)
            { State = RenderedExtentCheckState.InvalidResponse; return; }
            _lastInspector = inspector; _inspectionEpoch = outcome.WorkerEpoch;
            inspection.StopDeadline();
            using var evaluation = new Stage(this, op, RenderedExtentContract.EvaluationDeadlineMs, cancellationToken);
            if (!Continue(op, evaluation)) return;
            State = RenderedExtentCheckState.Evaluating; Notify();
            if (op.Sections is not null && !op.Sections.IsValidFor(outcome.Page!.PageCount))
            { State = RenderedExtentCheckState.InvalidRanges; return; }
            var input = new RenderedExtentRequest(Guid.NewGuid().ToString("N"), document.Sha256, outcome.Page!.PageCount, op.WorkType, op.Sections);
            if (!Continue(op, evaluation)) return;
            var reply = await _evaluator.EvaluateAsync(input, evaluation.Token); // ACTUAL task keeps the whole reservation.
            if (!Continue(op, evaluation)) return;
            if (reply is null || reply.RequestId != input.RequestId)
            { State = RenderedExtentCheckState.InvalidResponse; return; }
            if (reply.Status != RenderedExtentEvaluationStatus.Completed) { State = EvaluationFailure(reply.Status); return; }
            if (!RenderedExtentContract.Epoch(reply.WorkerEpoch) || reply.WorkerEpoch <= _evaluationEpoch)
            { State = RenderedExtentCheckState.InvalidResponse; return; }
            _evaluationEpoch = reply.WorkerEpoch;
            var observation = RenderedExtentJson.Parse(input, reply.ResponseJson);
            if (observation is null) { State = RenderedExtentCheckState.InvalidResponse; return; }
            if (!Continue(op, evaluation)) return;
            var output = authority.Output;
            var evidence = new RenderedExtentEvidence(document.Sha256, document.Length, output.RequestId.ToString(),
                output.WorkerEpoch, output.InputContentDigest.Hex, output.SnapshotIdentity.CaptureId,
                outcome.RequestId, outcome.WorkerEpoch, outcome.Runtime!, reply.WorkerEpoch);
            // Final synchronous full-authority/intent cutpoint, then publication.
            if (!Continue(op, evaluation)) return;
            _reportAuthority = authority; Report = new(observation, evidence); State = RenderedExtentCheckState.Completed;
        }
        catch (OperationCanceledException)
        { if (!_disposed && ReferenceEquals(_active, op) && op.Interrupted is null) State = !Current(op) ? RenderedExtentCheckState.Stale
                : Volatile.Read(ref op.Expired) != 0 ? RenderedExtentCheckState.TimedOut : RenderedExtentCheckState.Cancelled; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!_disposed && ReferenceEquals(_active, op) && op.Interrupted is null)
                State = !Current(op) ? RenderedExtentCheckState.Stale : Volatile.Read(ref op.Expired) != 0
                    ? RenderedExtentCheckState.TimedOut : cancellationToken.IsCancellationRequested ? RenderedExtentCheckState.Cancelled
                    : error is JsonException or InvalidOperationException ? RenderedExtentCheckState.InvalidResponse : RenderedExtentCheckState.Error;
        }
        finally
        {
            if (ReferenceEquals(_active, op)) _active = null; // Only after all actual awaits settled.
            op.Stop.Dispose();
            if (!_disposed) Notify();
        }
    }

    private static bool ValidInspection(PdfInspectionOutcome outcome, PdfDocument document) =>
        outcome.DocumentSha256 == document.Sha256 && RenderedExtentContract.RequestId(outcome.RequestId)
        && RenderedExtentContract.Epoch(outcome.WorkerEpoch) && outcome.NativeStatus == 0 && outcome.NativeError == 0 && outcome.EnteredNative == true
        && outcome.Runtime is { } runtime && PdfHashes.IsSha(runtime.LoaderSha256) && PdfHashes.IsSha(runtime.WasmSha256)
        && outcome.Page is { PageIndex: 0, PageCount: >= 1 and <= 512, RasterWidth: 0, RasterHeight: 0 } page
        && page.CopyBgra().Length == 0 && page.SearchHits.Count == 0 && page.Text is { } text && PdfHashes.ValidUtf16(text) && text.Length <= 100000
        && page.Utf16Units.Count <= 100000 && page.Glyphs.Count <= 100000
        && outcome.Actions?.Availability is null or PdfActionAvailability.NotRequested;
    private static RenderedExtentCheckState InspectionFailure(PdfInspectionStatus status) => status switch
    {
        PdfInspectionStatus.Busy => RenderedExtentCheckState.InspectorBusy,
        PdfInspectionStatus.Unavailable or PdfInspectionStatus.Disposed => RenderedExtentCheckState.RendererUnavailable,
        PdfInspectionStatus.TimedOut => RenderedExtentCheckState.TimedOut,
        PdfInspectionStatus.Cancelled => RenderedExtentCheckState.Cancelled,
        PdfInspectionStatus.OutputLimit or PdfInspectionStatus.InvalidInput => RenderedExtentCheckState.Limits,
        PdfInspectionStatus.NativeFailure => RenderedExtentCheckState.NativeFailure,
        PdfInspectionStatus.InvalidResponse => RenderedExtentCheckState.InvalidResponse,
        _ => RenderedExtentCheckState.Error
    };
    private static RenderedExtentCheckState EvaluationFailure(RenderedExtentEvaluationStatus status) => status switch
    {
        RenderedExtentEvaluationStatus.TimedOut => RenderedExtentCheckState.TimedOut,
        RenderedExtentEvaluationStatus.Cancelled => RenderedExtentCheckState.Cancelled,
        RenderedExtentEvaluationStatus.Busy or RenderedExtentEvaluationStatus.Unavailable or RenderedExtentEvaluationStatus.Disposed => RenderedExtentCheckState.EvaluationUnavailable,
        RenderedExtentEvaluationStatus.InvalidInput or RenderedExtentEvaluationStatus.InvalidResponse => RenderedExtentCheckState.InvalidResponse,
        _ => RenderedExtentCheckState.Error
    };
    private void Notify()
    {
        if (_disposed) return;
        try { Changed?.Invoke(); } catch (Exception error) when (error is not OutOfMemoryException) { /* Observer cannot strand admission. */ }
    }
    private void ScheduleInterrupt(Operation op, RenderedExtentCheckState reason)
    {
        try { Observe(_dispatch(() => { if (!_disposed && ReferenceEquals(_active, op) && op.Interrupted is null) { Interrupt(op, reason); Notify(); } })); }
        catch (Exception error) when (error is not OutOfMemoryException) { /* Dead view; final await guards still apply. */ }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _intent++; State = RenderedExtentCheckState.Disposed; Report = null; _reportAuthority = null;
        _workspace.WorkspaceChanged -= SourceChanged; _workspace.RevisionCommitted -= Saved; _compile.Changed -= SourceChanged;
        _active?.Stop.Cancel(); Changed = null;
        try { Observe(_evaluator.DisposeAsync().AsTask()); } // Owned adapter ONLY; no waiting on foreign work.
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }
    private static void Observe(Task task) => _ = task.ContinueWith(t => { _ = t.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private sealed class Operation(RenderedExtentAuthority authority, long intent, string workType, RenderedSectionRanges? sections)
    {
        internal RenderedExtentAuthority Authority { get; } = authority;
        internal long Intent { get; } = intent;
        internal string WorkType { get; } = workType;
        internal RenderedSectionRanges? Sections { get; } = sections;
        internal CancellationTokenSource Stop { get; } = new();
        internal RenderedExtentCheckState? Interrupted;
        internal int Expired;
    }
    private sealed class Stage : IDisposable
    {
        private readonly CancellationTokenRegistration _timeout, _caller;
        private readonly CancellationTokenSource _linked;
        internal Stage(RenderedExtentController owner, Operation op, int milliseconds, CancellationToken caller)
        {
            Deadline = new(TimeSpan.FromMilliseconds(milliseconds), owner._clock);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(op.Stop.Token, Deadline.Token, caller);
            _timeout = Deadline.Token.Register(() => { Interlocked.Exchange(ref op.Expired, 1); owner.ScheduleInterrupt(op, RenderedExtentCheckState.TimedOut); });
            _caller = caller.Register(() => owner.ScheduleInterrupt(op, RenderedExtentCheckState.Cancelled));
        }
        internal CancellationTokenSource Deadline { get; }
        internal CancellationToken Token => _linked.Token;
        internal void StopDeadline() => Deadline.CancelAfter(Timeout.InfiniteTimeSpan);
        public void Dispose() { _timeout.Dispose(); _caller.Dispose(); _linked.Dispose(); Deadline.Dispose(); }
    }
}
