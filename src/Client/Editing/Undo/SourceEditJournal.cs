using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Patching;

namespace Client.Editing.Undo;

/// <summary>Bounded per-file editor history, not storage/review history. All source
/// strings are borrowed immutable values; no encoding/copy/storage reads. Pending
/// recording preimages share the retained-byte budget with complete undo units.</summary>
public sealed class SourceEditJournal : IDisposable
{
    private readonly WorkspaceCoordinator _workspace;
    private readonly IWorkspaceLocks _locks;
    private readonly ISourceJournalEditor _editor;
    private readonly SourceJournalBudget _budget;
    private readonly TimeProvider _clock;
    private readonly Dictionary<FileId, Chain> _chains = [];
    private readonly LinkedList<Unit> _units = [];
    private Recording? _recording;
    private Mutation? _mutation;
    private CancellationTokenSource? _action;
    private Guid _session;
    private ProjectId? _project;
    private RevisionId? _head;
    private FileId? _active;
    private long _epoch, _version, _boundary, _retained, _reserved;
    private bool _disposed;

    public SourceEditJournal(WorkspaceCoordinator workspace, IWorkspaceLocks locks, ISourceJournalEditor editor,
        SourceJournalBudget? budget = null, TimeProvider? timeProvider = null)
    {
        _workspace = workspace; _locks = locks; _editor = editor;
        _budget = budget ?? new(); _clock = timeProvider ?? TimeProvider.System;
        if (!_budget.Valid) throw new ArgumentOutOfRangeException(nameof(budget));
        _session = workspace.SessionId; _project = workspace.Project?.Id; _head = workspace.CurrentRevision?.Id; _active = workspace.ActiveFileId;
        workspace.WorkspaceChanged += WorkspaceChanged;
        workspace.RevisionCommitted += Saved;
    }
    public event Action? Changed;
    public bool Busy => _action is not null || _mutation is not null;
    public int UnitCount => _units.Count;
    public long RetainedUtf8Bytes => _retained + _reserved;
    public long EvictedUnits { get; private set; }
    public long UnrecordedEdits { get; private set; }
    public string Notice { get; private set; } = "Editor undo is local to this session; saved history is separate.";
    public string? HistoryNotice => EvictedUnits > 0 && UnrecordedEdits > 0
        ? "Older editor undo was evicted and some edits could not be recorded; saved history is retained."
        : EvictedUnits > 0 ? "Older editor undo is no longer available; saved history is retained."
        : UnrecordedEdits > 0 ? "Some edits have no editor undo; current source and saved history are retained." : null;
    public bool CanUndo => CanAct(false);
    public bool CanRedo => CanAct(true);
    public SourceEditTransaction? UndoTransaction => Peek(false)?.Transaction;
    public SourceEditTransaction? RedoTransaction => Peek(true)?.Transaction;

    /// <summary>Begin observation, not mutation. A declined record never forbids
    /// the root's otherwise valid ordinary edit. No caller buffer is duplicated.</summary>
    public SourceEditLease? BeginEdit(string kind, SourceRange beforeSelection)
    {
        Refresh();
        if (_disposed || Busy || _recording is not null || !Kind(kind) || (_editor.IsComposing && kind != "composition")) return null;
        var stamp = Capture();
        if (stamp is null || !SourceTools.ValidRange(stamp.Text, beforeSelection)) return null;
        if (!SourceTools.ValidText(stamp.Text, out int bytes)) return null;
        long reserve = 2L * bytes;
        if (reserve > _budget.MaxRetainedUtf8Bytes) { UnavailableRecord(); return null; }
        Reserve(reserve);
        if (kind != "typing") _boundary++;
        var lease = new SourceEditLease(stamp.File.Id, kind);
        _recording = new(lease, stamp, beforeSelection, bytes, _clock.GetTimestamp());
        Notify();
        return lease;
    }

    public SourceJournalObservation ObserveEdit(SourceEditLease lease, SourceEdit planned, Func<bool> ordinaryApply)
    {
        ArgumentNullException.ThrowIfNull(ordinaryApply);
        Refresh();
        var record = _recording;
        if (_disposed || Busy || record is null || !ReferenceEquals(record.Lease, lease) || !Matches(record.Last, false)) return new(false, false, false);
        if (!SourceTools.ValidText(planned.Text, out int afterBytes) || !SourceTools.ValidRange(planned.Text, planned.Selection))
        { RejectRecord(record); return new(false, false, false); }
        long pairBytes = (long)record.BeforeBytes + afterBytes;
        if (pairBytes > _budget.MaxRetainedUtf8Bytes) { RejectRecord(record); return new(false, false, false); }
        Reserve(pairBytes);
        var mutation = new Mutation(record.Last, planned.Text, record);
        _mutation = mutation;
        bool accepted;
        try { accepted = ordinaryApply(); }
        finally { _mutation = null; }
        var after = Capture();
        bool tracked = accepted && mutation.Seen && !mutation.Invalid && ReferenceEquals(_recording, record)
            && AppliedModel(mutation) && after is not null && after.Lock == mutation.Before.Lock;
        if (tracked)
        {
            record.Last = after!;
            record.AfterSelection = planned.Selection;
            record.LastTime = _clock.GetTimestamp();
        }
        else if (ReferenceEquals(_recording, record)) RejectRecord(record);
        return new(accepted || mutation.Seen, true, tracked);
    }

    public SourceJournalRecordResult CompleteEdit(SourceEditLease lease, SourceRange afterSelection)
    {
        Refresh();
        var record = _recording;
        if (record is null || !ReferenceEquals(record.Lease, lease)) return new(false, false, Notice);
        if (!Matches(record.Last, false) || !SourceTools.ValidRange(record.Last.Text, afterSelection))
        { RejectRecord(record); return new(false, false, Notice); }
        _recording = null; _reserved = 0;
        if (record.Before.Text == record.Last.Text) { Notify(); return new(false, false, "No text change to record."); }
        if (!SourceTools.ValidText(record.Last.Text, out int afterBytes)) { DropFile(lease.FileId); UnavailableRecord(); return new(false, false, Notice); }
        var transaction = new SourceEditTransaction(lease.FileId, record.Before.Text, record.Last.Text,
            record.BeforeSelection, afterSelection, lease.Kind, record.Last.Generation);
        var chain = GetChain(record.Last);
        Unit? previous = chain.Cursor > 0 ? chain.Items[chain.Cursor - 1] : null;
        bool coalesce = previous is not null && ReferenceEquals(_units.Last?.Value, previous)
            && previous.Boundary == _boundary && previous.Transaction.Kind == "typing" && lease.Kind == "typing"
            && previous.Transaction.After == transaction.Before && previous.Transaction.AfterSelection == transaction.BeforeSelection
            && InsertTyping(previous.Transaction) && InsertTyping(transaction)
            && record.LastTime >= previous.Time && _clock.GetElapsedTime(previous.Time, record.LastTime) <= TimeSpan.FromMilliseconds(750);
        if (coalesce)
        {
            var merged = transaction with { Before = previous!.Transaction.Before, BeforeSelection = previous.Transaction.BeforeSelection };
            SourceTools.ValidText(merged.Before, out int beforeBytes);
            if ((long)beforeBytes + afterBytes <= _budget.MaxRetainedUtf8Bytes)
            { Remove(previous); transaction = merged; }
            else coalesce = false;
        }
        SourceTools.ValidText(transaction.Before, out int retainedBefore);
        long cost = (long)retainedBefore + afterBytes;
        EnsureCapacity(cost, true);
        chain = GetChain(record.Last);
        var unit = new Unit(transaction, cost, record.LastTime, _boundary, chain);
        unit.Node = _units.AddLast(unit); chain.Items.Add(unit); chain.Cursor = chain.Items.Count; _retained += cost;
        chain.Expected = FileState.Of(record.Last.File);
        Notice = coalesce ? "Typing recorded with the adjacent editor undo unit." : "Editor undo recorded.";
        Notify(); return new(true, coalesce, Notice);
    }
    public void AbortEdit(SourceEditLease lease)
    {
        if (_recording is not { } record || !ReferenceEquals(record.Lease, lease)) return;
        _recording = null; _reserved = 0; _boundary++;
        if (record.Before.Text != record.Last.Text) { DropFile(lease.FileId); UnavailableRecord(); }
    }
    public void NotifyFileSwitch()
    { Cancel(); _active = _workspace.ActiveFileId; _boundary++; }
    public void BreakCoalescing() { _boundary++; }
    public void InvalidateFile(FileId fileId)
    {
        if (_recording?.Lease.FileId == fileId) AbortEdit(_recording.Lease);
        DropFile(fileId); InvalidateAction(); _boundary++; Notice = "External source changes cleared affected editor undo; saved history is retained."; Notify();
    }
    public void Cancel()
    {
        InvalidateAction();
        if (_recording is { } record) AbortEdit(record.Lease);
        _boundary++; Notice = Busy ? "Editor history action cancelled; waiting for actual callback settlement." : "Editor history action cancelled."; Notify();
    }
    public Task<SourceJournalActionResult> UndoAsync(CancellationToken cancellationToken = default) => Traverse(false, cancellationToken);
    public Task<SourceJournalActionResult> RedoAsync(CancellationToken cancellationToken = default) => Traverse(true, cancellationToken);

    private async Task<SourceJournalActionResult> Traverse(bool redo, CancellationToken cancellationToken)
    {
        Refresh();
        string verb = redo ? "Redone" : "Undone";
        SourceJournalActionResult Refused(string notice) { Notice = notice; return new(false, false, notice); }
        if (_disposed || Busy || _recording is not null || cancellationToken.IsCancellationRequested || _editor.IsComposing)
            return Refused("Editor undo/redo is unavailable during this operation or composition.");
        if (!_editor.BeginHistoryAction()) return Refused("A competing editor action is still active.");
        Refresh();
        var unit = Peek(redo); var stamp = Capture();
        if (unit is null || stamp is null) return Refused("No current writable editor undo/redo unit is available; saved history is separate.");
        string expected = redo ? unit.Transaction.Before : unit.Transaction.After;
        string target = redo ? unit.Transaction.After : unit.Transaction.Before;
        SourceRange selection = redo ? unit.Transaction.AfterSelection : unit.Transaction.BeforeSelection;
        if (stamp.Text != expected || unit.Transaction.FileId != stamp.File.Id)
        { DropFile(stamp.File.Id); return Refused("The exact source preimage changed; editor history was cleared."); }
        _boundary++;
        long version = _version;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _action = stop;
        bool applied = false, caret = false, emission = false, historyMoved = false;
        bool Current() => !_disposed && ReferenceEquals(_action, stop) && !stop.IsCancellationRequested && version == _version
            && ReferenceEquals(PeekRaw(redo), unit) && Matches(stamp, true) && !_editor.IsComposing;
        try
        {
            if (!Current()) return Refused("Editor source changed before undo/redo.");
            var dom = await _editor.ReadAsync(new(stamp.File.Id, SourceTools.TextareaText(stamp.Text), stamp.Intent));
            if (!Current() || dom is null || dom.FileId != stamp.File.Id || dom.Intent != stamp.Intent
                || dom.Value != SourceTools.TextareaText(stamp.Text) || !SourceTools.TryFromTextarea(stamp.Text, dom.Selection, out _))
                return Refused("Editor source, caret or intent changed; no undo/redo was applied.");
            if (!SourceTools.ValidText(target, out _) || !SourceTools.ValidRange(target, selection) || !FitsProject(stamp.File, target))
                return Refused("Editor undo/redo would exceed current source limits.");
            var edit = new SourceEdit(target, selection);
            var grant = new SourceJournalMutation(new(stamp.Session, stamp.Generation, stamp.File.Id, stamp.Text, stamp.Intent), dom, edit,
                redo ? "redo" : "undo", () => emission && Current());
            if (!Current()) return Refused("Editor source changed; no undo/redo was applied.");
            var mutation = new Mutation(stamp, target, null);
            _mutation = mutation; emission = true;
            bool accepted;
            try { accepted = _editor.TryApply(grant); }
            finally { applied = mutation.Seen; emission = false; _mutation = null; }
            if (!accepted || !mutation.Seen || mutation.Invalid || !AppliedModel(mutation) || !_chains.TryGetValue(stamp.File.Id, out var chain)
                || !chain.Items.Contains(unit))
            {
                if (applied) { DropFile(stamp.File.Id); Notice = "Source changed while applying editor history; affected history was cleared."; return new(true, false, Notice); }
                return Refused("The ordinary editor guard refused undo/redo; no mutation was applied.");
            }
            chain.Cursor += redo ? 1 : -1; chain.Expected = FileState.Of(stamp.File); historyMoved = true;
            // Own WorkspaceChanged legitimately advanced the epoch. Capture model
            // authority after the accepted mutation, not the old preimage ticket.
            var after = CaptureAfter(stamp);
            bool SelectionCurrent() => !_disposed && ReferenceEquals(_action, stop) && !stop.IsCancellationRequested && version == _version
                && Matches(after, true) && !_editor.IsComposing;
            if (!stop.IsCancellationRequested && version == _version)
                caret = await _editor.SelectAsync(new(stamp.File.Id, target, selection, stamp.Intent, SelectionCurrent));
            caret = caret && SelectionCurrent();
            Notice = caret ? verb + " through ordinary source editing." : verb + "; the caret was not moved because its selection authority changed.";
            return new(true, caret, Notice);
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or Microsoft.JSInterop.JSException)
        {
            if (applied && !historyMoved) DropFile(stamp.File.Id);
            Notice = applied ? verb + "; source retained, caret placement unavailable." : "Editor history action stopped; no mutation applied.";
            return new(applied, false, Notice);
        }
        finally
        {
            emission = false;
            if (ReferenceEquals(_action, stop)) _action = null;
            Notify();
        }
    }

    private bool CanAct(bool redo)
    {
        Refresh();
        if (_disposed || Busy || _recording is not null || _editor.IsComposing) return false;
        var unit = PeekRaw(redo); var stamp = Capture();
        return unit is not null && stamp is not null && stamp.Text == (redo ? unit.Transaction.Before : unit.Transaction.After);
    }
    private Unit? Peek(bool redo) { Refresh(); return PeekRaw(redo); }
    private Unit? PeekRaw(bool redo)
    {
        if (_workspace.ActiveFileId is not { } id || !_chains.TryGetValue(id, out var chain)) return null;
        int index = redo ? chain.Cursor : chain.Cursor - 1;
        return index >= 0 && index < chain.Items.Count ? chain.Items[index] : null;
    }
    private Stamp? Capture()
    {
        if (_disposed || _workspace.Project is not { } project || _workspace.ActiveFile is not { Deleted: false, MediaKind: "text" } file
            || _workspace.IsReadOnly || _workspace.Recovery is not null || _workspace.LockId is not { } lockId
            || !Owned(project.Id, lockId) || !SourceTools.ValidText(file.Content, out _)) return null;
        return new(project.Id, _workspace.SessionId, file, file.RelativePath, file.Content, file.HasBom, _workspace.WorkingGeneration,
            _workspace.CurrentRevision?.Id, lockId, _epoch, _editor.Intent);
    }
    private Stamp CaptureAfter(Stamp before) => before with { Text = before.File.Content, Generation = _workspace.WorkingGeneration,
        Head = _workspace.CurrentRevision?.Id, Epoch = _epoch };
    private bool Matches(Stamp stamp, bool intent) => !_disposed && _workspace.Project?.Id == stamp.Project && _workspace.SessionId == stamp.Session
        && ReferenceEquals(_workspace.ActiveFile, stamp.File) && _workspace.Files.Any(f => ReferenceEquals(f, stamp.File))
        && !stamp.File.Deleted && stamp.File.MediaKind == "text" && stamp.File.RelativePath == stamp.Path && stamp.File.Content == stamp.Text
        && stamp.File.HasBom == stamp.HasBom && _workspace.WorkingGeneration == stamp.Generation && _workspace.CurrentRevision?.Id == stamp.Head
        && _workspace.LockId == stamp.Lock && !_workspace.IsReadOnly && _workspace.Recovery is null && Owned(stamp.Project, stamp.Lock)
        && _epoch == stamp.Epoch && (!intent || _editor.Intent == stamp.Intent);
    private bool Owned(ProjectId project, string lockId)
    { try { return _locks.IsOwnedLocally(lockId, "oop-project:" + project); } catch (Exception error) when (error is not OutOfMemoryException) { return false; } }
    private bool AppliedModel(Mutation mutation) => _workspace.Project?.Id == mutation.Before.Project && _workspace.SessionId == mutation.Before.Session
        && ReferenceEquals(_workspace.ActiveFile, mutation.Before.File) && !mutation.Before.File.Deleted
        && mutation.Before.File.RelativePath == mutation.Before.Path && mutation.Before.File.MediaKind == "text"
        && mutation.Before.File.HasBom == mutation.Before.HasBom && mutation.Before.File.Content == mutation.After
        && _workspace.WorkingGeneration == mutation.Before.Generation + 1;

    // Earlier coordinator subscribers can render history properties after the
    // model write but before our WorkspaceChanged callback runs. Reconcile only
    // that exact in-scope mutation, without consuming its notification or moving
    // a cursor. Other source/identity/generation changes still invalidate normally.
    private bool OwnMutationCurrent(Mutation own) => !own.Invalid && AppliedModel(own)
        && _workspace.Files.Any(f => ReferenceEquals(f, own.Before.File))
        && _workspace.CurrentRevision?.Id == own.Before.Head && _workspace.LockId == own.Before.Lock
        && !_workspace.IsReadOnly && _workspace.Recovery is null && Owned(own.Before.Project, own.Before.Lock)
        && _epoch >= own.Before.Epoch && _epoch <= own.Before.Epoch + 1;

    private void WorkspaceChanged()
    {
        _epoch++;
        if (_mutation is { } own && !own.Seen && OwnMutationCurrent(own))
        {
            own.Seen = true;
            if (own.Recording is { } record && ReferenceEquals(_recording, record))
            {
                if (own.Before.Text != own.After) ClearRedo();
                record.Last = CaptureAfter(own.Before);
            }
            if (_chains.TryGetValue(own.Before.File.Id, out var chain)) chain.Expected = FileState.Of(own.Before.File);
            return; // Do not cancel or erase our own synchronous source mutation.
        }
        if (_mutation is { } interrupted) interrupted.Invalid = true;
        if (_recording is { } pending) AbortEdit(pending.Lease);
        InvalidateAction(); _boundary++;
        Refresh(); ClearRedo();
        Notify();
    }
    private void Saved(WorkspaceSaveReceipt receipt)
    {
        if (receipt.SessionId != _workspace.SessionId) return;
        _epoch++; _boundary++; InvalidateAction();
        // Save changes the head, not the source preimage. Keep completed chains.
        if (_recording is { } record && SameFile(record.Last.File, record.Last.Path, record.Last.Text, record.Last.HasBom)
            && _workspace.WorkingGeneration == record.Last.Generation) record.Last = CaptureAfter(record.Last);
        _head = _workspace.CurrentRevision?.Id;
        Refresh(); Notify();
    }
    private void Refresh()
    {
        if (_disposed) return;
        if (_session != _workspace.SessionId || _project != _workspace.Project?.Id)
        {
            _session = _workspace.SessionId; _project = _workspace.Project?.Id;
            ClearAll(); InvalidateAction(); _boundary++; _epoch++;
            Notice = "Editor history cleared for a different project/session; saved history is separate.";
        }
        if (_head != _workspace.CurrentRevision?.Id)
        { _head = _workspace.CurrentRevision?.Id; _boundary++; _epoch++; InvalidateAction(); }
        if (_active != _workspace.ActiveFileId)
        { _active = _workspace.ActiveFileId; _boundary++; InvalidateAction(); }
        if (_mutation is { } own && OwnMutationCurrent(own) && _chains.TryGetValue(own.Before.File.Id, out var nativeChain))
            nativeChain.Expected = FileState.Of(own.Before.File);
        foreach (var pair in _chains.ToArray())
            if (!pair.Value.Expected.Current(_workspace)) { DropFile(pair.Key); Notice = "External source changes cleared affected editor undo; saved history is retained."; }
    }
    private static bool SameFile(WorkingFile file, string path, string text, bool bom) => !file.Deleted && file.MediaKind == "text"
        && file.RelativePath == path && file.Content == text && file.HasBom == bom;
    private void InvalidateAction() { _version++; _action?.Cancel(); }
    private void Reserve(long bytes)
    { _reserved = bytes; EnsureCapacity(0, false); }
    private void EnsureCapacity(long bytes, bool addUnit)
    {
        while (_units.First is { } first && (_retained + _reserved + bytes > _budget.MaxRetainedUtf8Bytes
            || addUnit && _units.Count >= _budget.MaxUnits)) { Remove(first.Value); EvictedUnits++; }
    }
    private void RejectRecord(Recording record)
    {
        if (!ReferenceEquals(_recording, record)) return;
        _recording = null; _reserved = 0; _boundary++;
        if (record.Before.Text != record.Last.Text) DropFile(record.Lease.FileId);
        UnavailableRecord();
    }
    private void UnavailableRecord()
    { UnrecordedEdits++; Notice = "Editor undo could not record this edit; valid source edits remain usable and saved history is retained."; Notify(); }
    private Chain GetChain(Stamp stamp)
    {
        if (!_chains.TryGetValue(stamp.File.Id, out var chain)) _chains[stamp.File.Id] = chain = new(FileState.Of(stamp.File));
        return chain;
    }
    private void ClearRedo()
    { foreach (var chain in _chains.Values.ToArray()) while (chain.Items.Count > chain.Cursor) Remove(chain.Items[^1]); }
    private void DropFile(FileId fileId)
    { if (_chains.TryGetValue(fileId, out var chain)) foreach (var unit in chain.Items.ToArray()) Remove(unit); _chains.Remove(fileId); }
    private void Remove(Unit unit)
    {
        var chain = unit.Chain; int index = chain.Items.IndexOf(unit);
        if (index < 0) return;
        chain.Items.RemoveAt(index); if (index < chain.Cursor) chain.Cursor--;
        if (unit.Node is not null) _units.Remove(unit.Node);
        _retained -= unit.Cost;
        if (chain.Items.Count == 0) _chains.Remove(unit.Transaction.FileId);
    }
    private void ClearAll()
    { _chains.Clear(); _units.Clear(); _retained = 0; _reserved = 0; _recording = null; EvictedUnits = 0; UnrecordedEdits = 0; }
    private static bool Kind(string kind) => kind is "typing" or "composition" or "lineTool" or "completion" or "replace";
    private static bool InsertTyping(SourceEditTransaction t)
    {
        var before = t.BeforeSelection; var after = t.AfterSelection;
        int growth = t.After.Length - t.Before.Length;
        return before.Start == before.End && after.Start == after.End && growth > 0 && after.Start == before.Start + growth
            && t.Before.AsSpan(0, before.Start).SequenceEqual(t.After.AsSpan(0, before.Start))
            && t.Before.AsSpan(before.Start).SequenceEqual(t.After.AsSpan(after.Start));
    }
    private bool FitsProject(WorkingFile target, string text)
    {
        if (_workspace.Files.Count(f => !f.Deleted) > WorkspaceTextLimits.FileCount) return false;
        long total = 0;
        foreach (var file in _workspace.Files.Where(f => !f.Deleted))
        {
            if (file.MediaKind == "text")
            {
                if (!SourceTools.ValidText(ReferenceEquals(file, target) ? text : file.Content, out int bytes)) return false;
                long size = bytes + (file.HasBom ? 3L : 0);
                if (size > WorkspaceTextLimits.FileBytes) return false;
                total += size;
            }
            else { long size = file.BinaryContent?.LongLength ?? file.DurableLength; if (size < 0 || size > WorkspaceTextLimits.ProjectBytes) return false; total += size; }
            if (total > WorkspaceTextLimits.ProjectBytes) return false;
        }
        return true;
    }
    private void Notify()
    { if (_disposed) return; try { Changed?.Invoke(); } catch (Exception error) when (error is not OutOfMemoryException) { /* UI observers cannot undo an accepted source mutation. */ } }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _workspace.WorkspaceChanged -= WorkspaceChanged; _workspace.RevisionCommitted -= Saved;
        InvalidateAction(); ClearAll(); // Actual action callback keeps its own slot until finally.
    }
    private sealed record Stamp(ProjectId Project, Guid Session, WorkingFile File, string Path, string Text, bool HasBom,
        long Generation, RevisionId? Head, string Lock, long Epoch, long Intent);
    private sealed record FileState(WorkingFile File, string Path, string Text, bool HasBom)
    {
        internal static FileState Of(WorkingFile file) => new(file, file.RelativePath, file.Content, file.HasBom);
        internal bool Current(WorkspaceCoordinator workspace) => workspace.Files.Any(f => ReferenceEquals(f, File)) && SameFile(File, Path, Text, HasBom);
    }
    private sealed class Chain(FileState expected)
    { internal readonly List<Unit> Items = []; internal int Cursor; internal FileState Expected = expected; }
    private sealed class Unit(SourceEditTransaction transaction, long cost, long time, long boundary, Chain chain)
    {
        internal SourceEditTransaction Transaction { get; } = transaction;
        internal long Cost { get; } = cost; internal long Time { get; } = time; internal long Boundary { get; } = boundary;
        internal Chain Chain { get; } = chain; internal LinkedListNode<Unit>? Node;
    }
    private sealed class Recording(SourceEditLease lease, Stamp before, SourceRange selection, int bytes, long time)
    {
        internal SourceEditLease Lease { get; } = lease; internal Stamp Before { get; } = before;
        internal Stamp Last = before; internal SourceRange BeforeSelection { get; } = selection;
        internal SourceRange AfterSelection = selection; internal int BeforeBytes { get; } = bytes; internal long LastTime = time;
    }
    private sealed class Mutation(Stamp before, string after, Recording? recording)
    { internal Stamp Before { get; } = before; internal string After { get; } = after; internal Recording? Recording { get; } = recording; internal bool Seen, Invalid; }
}
