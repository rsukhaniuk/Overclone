using Client.Analysis;
using Client.Editing;
using Client.Editing.Assistance;
using Client.Workspace.Domain;
using Client.Workspace.Transfer;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Client.Pages;

public partial class Workspace
{
    private AnalysisSnapshotFactory? _assistanceFactory;
    private AuthoringAssistSession? _assistance;
    private FileId? _assistRoot;
    private IJSInProcessObjectReference? _assistDomModule; // Borrowed existing module; never separately disposed.
    private long _assistSequence = -1;
    private bool _assistDomReady, _assistComposing = true;
    private FileId? _assistObservedFile;
    private (AuthoringAssistEditorRead Request, SourceRange Selection, long Sequence)? _assistLastRead;
    private (AuthoringAssistNavigation Navigation, TaskCompletionSource<bool> Completion)? _assistPendingNavigation;

    private sealed class AssistanceObservation
    {
        public int Start { get; set; }
        public int End { get; set; }
        public long Sequence { get; set; }
        public bool Composing { get; set; }
    }
    private void InitializeAssistance()
    {
        _assistanceFactory = new(Coordinator);
        _assistance = new(Coordinator, StarterLocks, _assistanceFactory, AuthoringAssistProvider, new AssistanceEditor(this));
    }
    private void AssistanceRootChanged(ChangeEventArgs e)
    {
        CancelSelectionIntents();
        _assistRoot = Guid.TryParseExact(e.Value?.ToString(), "N", out var id) ? new FileId(id) : null;
        _assistance?.SetRoot(_assistRoot); // Explicit selection only; never repair a missing/stale root.
    }
    private void AssistanceEditorIntentChanged()
    {
        // A delayed managed event may describe a native epoch already captured
        // by a newer command. Only a fresh epoch invalidates that command.
        if (ObserveAssistanceSynchronously() is null)
        {
            _sourceIntent++;
            _assistance?.Invalidate();
        }
    }
    private AssistanceObservation? ObserveAssistanceSynchronously(bool invalidateOnChange = true)
    {
        if (_pageDisposed || _assistDomModule is null || Coordinator.ActiveFile is not { Deleted: false, MediaKind: "text" } file)
        { _assistDomReady = false; _assistComposing = true; return null; }
        try
        {
            var observation = _assistDomModule.Invoke<AssistanceObservation?>("inspectAssistanceSelection", _editorRef,
                file.Id.ToString(), SourceTools.TextareaText(file.Content));
            if (observation is null || observation.Sequence < 0)
            { _assistDomReady = false; _assistComposing = true; return null; }
            bool changed = _assistObservedFile != file.Id || _assistSequence != observation.Sequence;
            _assistObservedFile = file.Id; _assistSequence = observation.Sequence;
            _assistComposing = observation.Composing; _assistDomReady = true;
            if (changed && invalidateOnChange) { _sourceIntent++; _assistance?.Invalidate(); }
            return observation;
        }
        catch (Exception error) when (error is JSException or ObjectDisposedException)
        { _assistDomReady = false; _assistComposing = true; return null; }
    }
    private async Task AssistanceAfterRenderAsync()
    {
        // Claim the deferred selection BEFORE the first foreign await. A
        // cancelled not-yet-started render can settle immediately; a started
        // callback owns actual import/selection settlement through finally.
        var pending = _assistPendingNavigation;
        _assistPendingNavigation = null;
        bool selected = false;
        try
        {
            if (_pageDisposed || Coordinator.ActiveFile is not { Deleted: false, MediaKind: "text" } file) return;
            var text = file.Content;
            var module = await GetEditorSelectAsync(CancellationToken.None); // Actual shared import.
            if (_pageDisposed || !ReferenceEquals(Coordinator.ActiveFile, file) || file.Content != text) return;
            _assistDomModule = module as IJSInProcessObjectReference;
            // Standalone WASM has a synchronous final DOM guard. Unsupported hosts
            // fail closed, not by silently using an unsafe async-edit fallback.
            ObserveAssistanceSynchronously();
            if (pending is null) return;
            if (_assistDomModule is not null && Navigator.VerifyAuthoringNavigation(pending.Value.Navigation) is SpanMappingResult.Exact map)
            {
                string expected = SourceTools.TextareaText(TextContent.DecodeStrict(pending.Value.Navigation.File.Bytes.ToArray()));
                if (pending.Value.Navigation.IsCurrent())
                    selected = _assistDomModule.Invoke<bool>("trySelectForFind", _editorRef,
                        pending.Value.Navigation.File.FileId.ToString(), expected, map.TextareaStart, map.TextareaEnd);
                // Read-only owned selection, no await between guard/select/ack.
                // A later real user selection advances the native sequence again.
                if (selected) ObserveAssistanceSynchronously(false);
            }
        }
        catch (Exception error) when (error is JSException or ObjectDisposedException) { }
        finally { pending?.Completion.TrySetResult(selected); }
    }
    private void DisposeAssistance()
    {
        _assistance?.Dispose();
        if (_assistPendingNavigation is { } pending) pending.Completion.TrySetResult(false);
        _assistPendingNavigation = null;
        try { _assistDomModule?.Invoke<object?>("detachAssistanceEditor", _editorRef); } catch (Exception e) when (e is JSException or ObjectDisposedException) { }
        _assistDomModule = null;
        if (_assistanceFactory is { } factory) _ = factory.DisposeAsync(); // Existing factory disposal is synchronous/bounded.
    }
    private sealed class AssistanceEditor(Workspace page) : IAuthoringAssistEditor
    {
        public bool BeginRequest()
        {
            if (page._pageDisposed || page._sourceOperation is not null || page._completionOperation is not null) return false;
            page.ObserveAssistanceSynchronously(); // Acknowledge native caret events before the new request can be invalidated.
            page.NavCancelAndStart(); // Competing navigation cancelled BEFORE the session captures intent/root/source.
            return true;
        }
        public long Intent { get { page.ObserveAssistanceSynchronously(); return page._sourceIntent; } }
        public bool IsComposing { get { page.ObserveAssistanceSynchronously(); return !page._assistDomReady || page._assistComposing; } }
        public async Task<AuthoringAssistDom?> ReadAsync(AuthoringAssistEditorRead request)
        {
            if (page._pageDisposed || page._sourceOperation is not null || page._completionOperation is not null || Intent != request.Intent) return null;
            var module = await page.GetEditorSelectAsync(CancellationToken.None); // Actual import, no cancelled proxy.
            if (page._pageDisposed || Intent != request.Intent || page.Coordinator.ActiveFile?.Id != request.FileId) return null;
            page._assistDomModule = module as IJSInProcessObjectReference;
            var observation = page.ObserveAssistanceSynchronously();
            if (observation is null || IsComposing || Intent != request.Intent || page.Coordinator.ActiveFile?.Id != request.FileId
                || page.Coordinator.ActiveFile is not { } currentFile || SourceTools.TextareaText(currentFile.Content) != request.ExpectedTextareaText) return null;
            var range = new SourceRange(observation.Start, observation.End);
            page._assistLastRead = (request, range, observation.Sequence);
            return new(request.FileId, request.ExpectedTextareaText, range, request.Intent);
        }
        public bool TryApply(AuthoringAssistInsertion insertion)
        {
            if (page._pageDisposed || page._sourceOperation is not null || page._completionOperation is not null
                || page._assistDomModule is null || page._assistLastRead is not { } before || !insertion.IsCurrent()) return false;
            var now = page.ObserveAssistanceSynchronously();
            if (now is null || now.Composing || now.Sequence != before.Sequence
                || new SourceRange(now.Start, now.End) != before.Selection || !insertion.IsCurrent()
                || insertion.Before.File != before.Request.FileId || insertion.Before.Intent != before.Request.Intent) return false;
            // Synchronous final DOM check -> existing ordinary source-edit path.
            // No coordinator bypass, no await, no source rewrite, one file only.
            var owner = new object(); var ct = page._navCts?.Token ?? CancellationToken.None;
            page._sourceOperation = owner;
            try { return page.ApplyOrdinarySourceEdit(owner, insertion.Before, insertion.Edit, ct, "completion"); }
            finally { if (page._sourceSelection is not { } selection || !ReferenceEquals(selection.Owner, owner)) page.FinishSourceOperation(owner); }
        }
        public async Task<bool> NavigateAsync(AuthoringAssistNavigation navigation)
        {
            if (page._pageDisposed || page._sourceOperation is not null || page._completionOperation is not null
                || page._assistPendingNavigation is not null || page.Navigator.VerifyAuthoringNavigation(navigation) is not SpanMappingResult.Exact)
                return false; // NonSelectable remains a safe location-only result, never guessed.
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            page._assistPendingNavigation = (navigation, completion);
            // Owned target switch is read-only. Do NOT invoke the unrelated-tree
            // SelectFile handler: it intentionally invalidates all navigation.
            bool replacesEditor = page.Coordinator.ActiveFileId != navigation.File.FileId;
            if (!navigation.IsCurrent() || !page.JournalSelectFile(navigation.File.FileId))
            { page._assistPendingNavigation = null; return false; }
            page._assistObservedFile = navigation.File.FileId; // Preserve own intent while the target renders.
            if (replacesEditor) page._assistSequence = 0; // New keyed editor starts with an unmodified native observer.
            page.StateHasChanged();
            bool selected = await completion.Task; // Actual after-render selection lease.
            return !page._pageDisposed && selected && navigation.IsCurrent();
        }
    }
}
