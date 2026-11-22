using Client.Editing;
using Client.Editing.Undo;
using Client.Workspace.Domain;
using Microsoft.JSInterop;

namespace Client.Pages;

public partial class Workspace
{
    private SourceEditJournal? _journal;
    private DotNetObjectReference<Workspace>? _journalRef;
    private IJSInProcessObjectReference? _journalDom;
    private SourceEditLease? _journalCompositionLease;
    private bool _journalComposing, _journalProgrammatic;
    private long _journalViewEpoch;
    private JournalCompositionScope? _journalCompositionScope;
    private sealed record JournalCompositionScope(Client.Workspace.WorkingFile File, ProjectId? Project, Guid Session,
        string Path, long BindingEpoch, long CompositionEpoch);
    public sealed class JournalCompositionEvent
    {
        public bool Active { get; set; }
        public string FileId { get; set; } = "";
        public string Session { get; set; } = "";
        public string Path { get; set; } = "";
        public long BindingEpoch { get; set; }
        public long CompositionEpoch { get; set; }
        public long NativeEpoch { get; set; }
    }
    public sealed class JournalKeyEvent
    {
        public string Direction { get; set; } = "";
        public string FileId { get; set; } = "";
        public string Session { get; set; } = "";
        public string Path { get; set; } = "";
        public long BindingEpoch { get; set; }
        public long NativeEpoch { get; set; }
    }
    private string? _journalInputNotice;
    private (SourceJournalRead Read, SourceJournalDom Dom, long NativeEpoch)? _journalRead;
    private (SourceJournalSelection Selection, TaskCompletionSource<bool> Completion)? _journalSelection;

    private sealed class JournalFrame
    {
        public JournalFrame() { }
        public string FileId { get; set; } = "";
        public string Session { get; set; } = "";
        public string Path { get; set; } = "";
        public long BindingEpoch { get; set; }
        public long CompositionEpoch { get; set; }
        public long NativeEpoch { get; set; }
        public string BeforeValue { get; set; } = "";
        public string AfterValue { get; set; } = "";
        public int BeforeStart { get; set; }
        public int BeforeEnd { get; set; }
        public int Start { get; set; }
        public int End { get; set; }
        public bool Composing { get; set; }
        public bool Overrun { get; set; }
    }
    private sealed class JournalState
    {
        public JournalState() { }
        public long Epoch { get; set; }
        public string FileId { get; set; } = "";
        public string Session { get; set; } = "";
        public string Path { get; set; } = "";
        public long BindingEpoch { get; set; }
        public long CompositionEpoch { get; set; }
        public long CompositionEndEpoch { get; set; }
        public bool Composing { get; set; }
    }
    private JournalState? CurrentJournalState()
    {
        try { return _journalDom?.Invoke<JournalState?>("sourceJournalState", _editorRef); }
        catch (Exception error) when (error is JSException or ObjectDisposedException) { return null; }
    }
    private bool JournalComposing => _journalComposing || CurrentJournalState()?.Composing == true;
    private void InitializeJournal()
    {
        _journalRef = DotNetObjectReference.Create(this);
        _journal = new(Coordinator, StarterLocks, new JournalEditor(this));
        _journal.Changed += JournalChanged;
        Coordinator.WorkspaceChanged += JournalWorkspaceChanged;
    }
    private bool JournalWritable() => !_pageDisposed && Coordinator.Project is { } project && Coordinator.LockId is { } id
        && !Coordinator.IsReadOnly && Coordinator.Recovery is null && StarterLocks.IsOwnedLocally(id, "oop-project:" + project.Id);
    private bool JournalIdentity(string file, string session, string path, long binding, long native, JournalState? state)
        => !_pageDisposed && state is not null && binding > 0 && binding <= 9007199254740991 && native >= 0 && native <= state.Epoch
        && state.BindingEpoch == binding && Coordinator.ActiveFile is { Deleted: false, MediaKind: "text" } active
        && active.Id.ToString() == file && Coordinator.SessionId.ToString("N") == session && active.RelativePath == path
        && state.FileId == file && state.Session == session && state.Path == path;
    private void AbortRootJournalComposition(bool resetNative)
    {
        if (_journalCompositionLease is { } lease) _journal?.AbortEdit(lease);
        bool hadComposition = _journalCompositionScope is not null || JournalComposing;
        _journalCompositionLease = null; _journalCompositionScope = null; _journalComposing = false;
        if (resetNative)
        {
            try { _journalDom?.Invoke<object?>("resetSourceJournal", _editorRef); } catch (Exception e) when (e is JSException or ObjectDisposedException) { }
            if (hadComposition) _journalViewEpoch++; // Recreate only the retired composing DOM, never reuse it for a new file/session.
        }
    }
    private void JournalWorkspaceChanged()
    {
        var scope = _journalCompositionScope;
        bool different = scope is not null && (scope.Session != Coordinator.SessionId || scope.Project != Coordinator.Project?.Id
            || !ReferenceEquals(Coordinator.ActiveFile, scope.File) || scope.File.Deleted || scope.File.MediaKind != "text" || scope.File.RelativePath != scope.Path);
        if (!_journalProgrammatic || different) AbortRootJournalComposition(true); // restore/review/tree/session boundaries, never own observed input.
    }
    private void JournalFileSwitch()
    {
        AbortRootJournalComposition(true); _journal?.NotifyFileSwitch();
        if (_journalSelection is { } pending) pending.Completion.TrySetResult(false); // Deferred only; started render already claimed its task.
        _journalSelection = null;
    }
    private bool JournalSelectFile(FileId id) { JournalFileSwitch(); return Coordinator.SelectFile(id); }
    private void JournalAwareSourceSelectionChanged()
    {
        if (_pageDisposed) return;
        if (_journalDom?.Invoke<bool>("sourceJournalOwnsSelection", _editorRef) == true)
        { ObserveAssistanceSynchronously(false); return; } // Exact owned value/range only, never a blanket user-event exemption.
        AssistanceEditorIntentChanged();
        if (_journalDom?.Invoke<bool>("sourceJournalInputOwnsSelection", _editorRef) != true) _journal?.BreakCoalescing();
    }
    private void JournalSourcePointerChanged() { _journal?.BreakCoalescing(); AssistanceEditorIntentChanged(); }
    private bool _journalRenderQueued;
    private void JournalChanged()
    {
        if (_pageDisposed || _journalRenderQueued) return;
        _journalRenderQueued = true;
        _ = InvokeAsync(async () =>
        {
            // Coordinator subscribers and owned file switches must finish before
            // a render can claim their deferred selection callbacks.
            await Task.Yield();
            _journalRenderQueued = false;
            if (!_pageDisposed && !JournalComposing) StateHasChanged();
        });
    }
    private async Task<bool> RunEditorHistory(bool redo)
    {
        if (_pageDisposed || _journal is null || JournalComposing) return false;
        var result = redo ? await _journal.RedoAsync() : await _journal.UndoAsync();
        return result.Applied; // No fallback to review Undo/native browser history.
    }
    private bool JournalOrdinaryInput(string value)
    {
        bool previous = _journalProgrammatic; _journalProgrammatic = true;
        try
        {
            if (Coordinator.ActiveFile is not { } file || !Coordinator.EditText(file.Id, value)) return false;
            if (!JournalComposing) StateHasChanged(); return true;
        }
        finally { _journalProgrammatic = previous; }
    }
    // A native insertion can briefly expose raw CR/CRLF in the textarea.
    // Interpret offsets in the representation the browser actually captured.
    private static bool JournalSourceRange(string source, string observed, SourceRange range, out SourceRange mapped)
    {
        mapped = range;
        if (source == observed) return SourceTools.ValidRange(source, range);
        return SourceTools.TextareaText(source) == observed && SourceTools.TryFromTextarea(source, range, out mapped);
    }

    // Called at the top of the existing ActiveContent setter. No captured native
    // frame means startup/programmatic input uses the existing ordinary setter.
    private bool TryJournalNativeInput(string value)
    {
        if (_journalProgrammatic || _journalDom is null || _journal is null || Coordinator.ActiveFile is not { } file) return false;
        JournalFrame? frame;
        try { frame = _journalDom.Invoke<JournalFrame?>("takeSourceJournalInput", _editorRef, value); }
        catch (Exception error) when (error is JSException or ObjectDisposedException) { return false; }
        if (frame is null) return false;
        var native = CurrentJournalState();
        if (!JournalWritable() || !JournalIdentity(frame.FileId, frame.Session, frame.Path, frame.BindingEpoch, frame.NativeEpoch, native)
            || frame.CompositionEpoch != native!.CompositionEpoch || (frame.Composing && frame.CompositionEpoch <= 0)) return true;
        var after = new SourceRange(frame.Start, frame.End);
        if (frame.FileId != file.Id.ToString() || frame.Session != Coordinator.SessionId.ToString("N") || frame.Path != file.RelativePath
            || !SourceTools.ValidText(value, out _)
            || !JournalSourceRange(file.Content, frame.BeforeValue, new(frame.BeforeStart, frame.BeforeEnd), out var before)
            || !SourceTools.ValidRange(value, after))
        { _journalInputNotice = "Source input preimage changed; no stale input was applied."; return true; }
        bool composing = _journalComposing || frame.Composing;
        if (composing && (_journalCompositionScope is not { } old || old.BindingEpoch != frame.BindingEpoch || old.CompositionEpoch != frame.CompositionEpoch))
        {
            AbortRootJournalComposition(false); // Old token cannot be completed while a NEW native composition is active.
            _journalCompositionScope = new(file, Coordinator.Project?.Id, Coordinator.SessionId, file.RelativePath, frame.BindingEpoch, frame.CompositionEpoch);
        }
        var lease = _journalCompositionLease ?? (!frame.Overrun ? _journal.BeginEdit(composing ? "composition" : "typing", before) : null);
        if (composing) { _journalComposing = true; _journalCompositionLease = lease; }
        var observation = lease is null ? null : _journal.ObserveEdit(lease, new(value, after), () => JournalOrdinaryInput(value));
        if (observation is null || !observation.CallbackInvoked)
        {
            // Recording is optional; the root's ordinary source/lock/recovery
            // and actual DOM preimage guards remain mandatory here.
            if (Coordinator.ActiveFile == file && (file.Content == frame.BeforeValue || SourceTools.TextareaText(file.Content) == frame.BeforeValue) && !Coordinator.IsReadOnly && Coordinator.Recovery is null)
                JournalOrdinaryInput(value);
            _journalInputNotice = "This edit was not recorded for editor undo; current source remains usable.";
        }
        if (!composing && lease is not null) _journal.CompleteEdit(lease, after);
        return true;
    }
    [JSInvokable]
    public Task SourceJournalComposition(JournalCompositionEvent e)
    {
        var native = CurrentJournalState();
        if (e is null || native is null || !JournalIdentity(e.FileId, e.Session, e.Path, e.BindingEpoch, e.NativeEpoch, native)
            || e.CompositionEpoch <= 0 || native.CompositionEpoch != e.CompositionEpoch) return Task.CompletedTask;
        if (e.Active)
        {
            if (!native.Composing) return Task.CompletedTask; // Queued old start after a completed/new composition.
            if (_journalCompositionScope is { } existing && existing.BindingEpoch == e.BindingEpoch && existing.CompositionEpoch == e.CompositionEpoch)
                return Task.CompletedTask;
            AbortRootJournalComposition(false);
            _journalComposing = true;
            _journalCompositionScope = new(Coordinator.ActiveFile!, Coordinator.Project?.Id, Coordinator.SessionId, e.Path, e.BindingEpoch, e.CompositionEpoch);
            return Task.CompletedTask; // Begin lease from the first exact beforeinput frame, before its model mutation.
        }
        var scope = _journalCompositionScope;
        if (native.Composing || e.NativeEpoch != native.CompositionEndEpoch || scope is null || scope.BindingEpoch != e.BindingEpoch
            || scope.CompositionEpoch != e.CompositionEpoch || scope.Session != Coordinator.SessionId || scope.Project != Coordinator.Project?.Id
            || !ReferenceEquals(scope.File, Coordinator.ActiveFile) || scope.Path != e.Path) return Task.CompletedTask;
        // Never complete a lease while CURRENT native state still reports composing.
        // Keep root composing=true while flushing the final exact input so that
        // TryJournalNativeInput cannot turn it into a separate typing transaction.
        _journalComposing = true;
        var text = _journalDom?.Invoke<string?>("readValue", _editorRef);
        if (JournalWritable() && text is not null && SourceTools.TextareaText(scope.File.Content) != text) TryJournalNativeInput(text);
        if (!ReferenceEquals(scope, _journalCompositionScope) || CurrentJournalState()?.Composing != false) return Task.CompletedTask;
        var lease = _journalCompositionLease;
        var source = scope.File.Content;
        var dom = _journalDom?.Invoke<SourceDomRange?>("readSelection", _editorRef, scope.File.Id.ToString(), SourceTools.TextareaText(source));
        if (lease is not null && dom is { Start: { } start, End: { } end } && SourceTools.TryFromTextarea(source, new(start, end), out var selection))
            _journal?.CompleteEdit(lease, selection);
        else if (lease is not null) _journal?.AbortEdit(lease);
        _journalCompositionLease = null; _journalCompositionScope = null; _journalComposing = false; StateHasChanged();
        return Task.CompletedTask;
    }
    [JSInvokable]
    public async Task SourceJournalKey(JournalKeyEvent e)
    {
        var native = CurrentJournalState();
        if (e is null || native is null || JournalComposing || !JournalIdentity(e.FileId, e.Session, e.Path, e.BindingEpoch, e.NativeEpoch, native)
            || native.Epoch != e.NativeEpoch || e.Direction is not ("undo" or "redo")) return;
        ObserveAssistanceSynchronously(false); // Acknowledge this key before fresh command capture, not during/after mutation.
        await RunEditorHistory(e.Direction == "redo");
    }
    private async Task JournalAfterRenderAsync()
    {
        var pending = _journalSelection; _journalSelection = null; // Claim before first foreign await.
        bool selected = false;
        try
        {
            if (_pageDisposed || Coordinator.ActiveFile is not { MediaKind: "text" } file) return;
            var module = await GetEditorSelectAsync(CancellationToken.None); // Actual shared import.
            if (_pageDisposed || !ReferenceEquals(Coordinator.ActiveFile, file)) return;
            _journalDom = module as IJSInProcessObjectReference;
            if (_journalDom is null) return;
            _journalDom.Invoke<object?>("enableSourceJournal", _editorRef, _journalRef);
            _journalDom.InvokeVoid("enableWorkspaceFocus", _editorRef);
            if (pending is { } action && action.Selection.IsCurrent() && !JournalComposing
                && SourceTools.ToTextarea(action.Selection.Text, action.Selection.Selection) is { } range)
                selected = _journalDom.Invoke<bool>("selectSourceJournal", _editorRef, action.Selection.FileId.ToString(),
                    SourceTools.TextareaText(action.Selection.Text), range.Start, range.End);
        }
        catch (Exception error) when (error is JSException or ObjectDisposedException) { }
        finally { pending?.Completion.TrySetResult(selected); }
    }
    private bool JournalExplicitEdit(string kind, SourceEdit planned, Func<bool> ordinaryCore)
    {
        if (_journal is null) return ordinaryCore();
        if (JournalComposing || Coordinator.ActiveFile is not { } file) return false;
        var dom = _journalDom?.Invoke<SourceDomRange?>("readSelection", _editorRef, file.Id.ToString(), SourceTools.TextareaText(file.Content));
        if (dom is not { Start: { } start, End: { } end } || !SourceTools.TryFromTextarea(file.Content, new(start, end), out var before)) return false;
        var lease = _journal.BeginEdit(kind, before);
        bool previous = _journalProgrammatic; _journalProgrammatic = true;
        try
        {
            var observation = lease is null ? null : _journal.ObserveEdit(lease, planned, ordinaryCore);
            bool applied = observation is null || !observation.CallbackInvoked ? ordinaryCore() : observation.Applied;
            if (applied && lease is not null) _journal.CompleteEdit(lease, planned.Selection);
            return applied;
        }
        finally { _journalProgrammatic = previous; if (lease is not null) _journal.AbortEdit(lease); }
    }
    private void DisposeJournal()
    {
        Coordinator.WorkspaceChanged -= JournalWorkspaceChanged;
        AbortRootJournalComposition(true);
        if (_journal is { } journal) { journal.Changed -= JournalChanged; journal.Dispose(); }
        if (_journalSelection is { } pending) pending.Completion.TrySetResult(false);
        _journalSelection = null;
        try { _journalDom?.Invoke<object?>("disableSourceJournal", _editorRef); } catch (Exception error) when (error is JSException or ObjectDisposedException) { }
        _journalRef?.Dispose(); _journalRef = null;
    }
    private sealed class JournalEditor(Workspace page) : ISourceJournalEditor
    {
        public long Intent => page._sourceIntent;
        public bool IsComposing => page.JournalComposing;
        public bool BeginHistoryAction()
        { if (page._pageDisposed || page._sourceOperation is not null || page._completionOperation is not null || page._assistance?.Busy == true) return false; page.ObserveAssistanceSynchronously(); page.NavCancelAndStart(); return true; }
        public async Task<SourceJournalDom?> ReadAsync(SourceJournalRead request)
        {
            var module = await page.GetEditorSelectAsync(CancellationToken.None);
            page.ObserveAssistanceSynchronously();
            if (page._pageDisposed || Intent != request.Intent || page.Coordinator.ActiveFile?.Id != request.FileId || module is not IJSInProcessObjectReference sync) return null;
            var range = sync.Invoke<SourceDomRange?>("readSelection", page._editorRef, request.FileId.ToString(), request.ExpectedTextareaText);
            var state = sync.Invoke<JournalState?>("sourceJournalState", page._editorRef);
            if (range is not { Start: { } start, End: { } end } || state is null || state.Composing) return null;
            var dom = new SourceJournalDom(request.FileId, request.ExpectedTextareaText, new(start, end), request.Intent);
            page._journalRead = (request, dom, state.Epoch); return dom;
        }
        public bool TryApply(SourceJournalMutation mutation)
        {
            if (!mutation.IsCurrent() || page._journalRead is not { } read || page._journalDom is null || page.JournalComposing) return false;
            var dom = page._journalDom.Invoke<SourceDomRange?>("readSelection", page._editorRef, read.Read.FileId.ToString(), read.Dom.Value);
            var state = page.CurrentJournalState();
            if (dom is not { Start: { } start, End: { } end } || new SourceRange(start, end) != read.Dom.Selection
                || state?.Epoch != read.NativeEpoch || !mutation.IsCurrent()) return false;
            if (SourceTools.ToTextarea(mutation.Edit.Text, mutation.Edit.Selection) is not { } after
                || !page._journalDom.Invoke<bool>("armSourceJournalRender", page._editorRef, mutation.Dom.Value,
                    SourceTools.TextareaText(mutation.Edit.Text), after.Start, after.End) || !mutation.IsCurrent()) return false;
            // Plain EditText/autosave: NEVER exact PendingTextBytes or review Undo.
            return page.JournalOrdinaryInput(mutation.Edit.Text);
        }
        public Task<bool> SelectAsync(SourceJournalSelection selection)
        {
            if (page._pageDisposed || page._journalSelection is not null) return Task.FromResult(false);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            page._journalSelection = (selection, completion); page.StateHasChanged();
            return completion.Task; // Actual after-render callback; not a cancelled waiter.
        }
    }
}
