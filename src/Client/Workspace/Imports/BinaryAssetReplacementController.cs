using System.Security.Cryptography;
using Client.Workspace.Domain;
using Client.Workspace.Transfer;

namespace Client.Workspace.Imports;

public enum BinaryReplacementState { Idle, Reading, Preview, Applied, Undone, Stale, Cancelled, Busy, Failed, Disposed }
public sealed record BinaryReplacementPreview(FileId TargetFileId, string TargetPath, string PickedFilename,
    long OldBytes, long NewBytes, string OldSha256, string NewSha256);

/// <summary>UI-owned conservative preview/confirm and one session-local Undo.
/// Borrows model/locks. Foreign IO/hash/cleanup settle before releasing the shared
/// read lease. Never saves, selects, renames, decodes or launches downstream jobs.</summary>
public sealed class BinaryAssetReplacementController : IDisposable
{
    private readonly WorkspaceCoordinator _workspace;
    private readonly IWorkspaceLocks _locks;
    private readonly Func<Action, Task> _dispatch;
    private readonly Func<CancellationToken, Task> _yield;
    private readonly LocalFileReadAdmission _admission;
    private WorkingFile? _target;
    private long _targetVersion, _intent;
    private bool _disposed, _mutating;
    private Operation? _active;
    private Prepared? _preview;
    private UndoEntry? _undo;
    private BinaryReplacementState _state;
    public event Action? Changed;
    public bool Busy => _admission.Busy;
    public BinaryReplacementState State => _disposed ? BinaryReplacementState.Disposed
        : _preview is { } p && !Strict(p.Stamp) || _undo is { } u && !UndoCurrent(u) ? BinaryReplacementState.Stale : _state;
    public BinaryReplacementPreview? Preview => _preview is { } p && Strict(p.Stamp) ? p.Info : null;
    public bool CanConfirm => !_mutating && !Busy && Preview is not null;
    public bool CanUndo => !_mutating && !Busy && _preview is null && _undo is { } u && UndoCurrent(u);
    public bool CanCancel => !_disposed && !_mutating && (_active is { Invalid: 0 } || _preview is not null);
    public string Notice => State switch
    {
        BinaryReplacementState.Reading => "Reading and hashing local replacement bytes…",
        BinaryReplacementState.Preview => "Preview only. Confirm to replace this asset; nothing has been saved.",
        BinaryReplacementState.Applied => "Replacement applied. Save normally. Undo is available only while current.",
        BinaryReplacementState.Undone => "Replacement undone in this session. Save normally.",
        BinaryReplacementState.Stale => "Preview or Undo is no longer current. Choose the file again to retry.",
        BinaryReplacementState.Cancelled => Busy ? "Cancelled. Actual local work is still settling." : "Replacement cancelled.",
        BinaryReplacementState.Busy => "A local file read is still settling. Try again when it finishes.",
        BinaryReplacementState.Failed => "Replacement could not be completed. Check file size and current write access.",
        BinaryReplacementState.Disposed => "Asset replacement closed.",
        _ => "Choose local bytes to replace the selected binary asset."
    };

    public BinaryAssetReplacementController(WorkspaceCoordinator workspace, IWorkspaceLocks locks, Func<Action, Task> dispatch,
        Func<CancellationToken, Task>? yield = null)
    {
        _workspace = workspace; _locks = locks; _dispatch = dispatch;
        _yield = yield ?? (ct => Task.Delay(1, ct));
        _admission = LocalFileReadAdmission.For(workspace); _admission.Changed += Released;
        workspace.WorkspaceChanged += SourceChanged;
        workspace.RevisionCommitted += Saved;
    }
    public void SetTarget(WorkingFile? target)
    {
        if (_disposed || ReferenceEquals(_target, target)) return;
        _undo = null; // Parameter/selection intent changes consume the old target's Undo, even on A-B-A.
        Invalidate(BinaryReplacementState.Stale); _target = target; ++_targetVersion;
        if (_active is null) _state = BinaryReplacementState.Idle;
        Notify();
    }
    private bool Editable()
    {
        if (_disposed || !_workspace.IsOpen || _workspace.IsReadOnly || _workspace.Recovery is not null
            || _workspace.LockId is not { Length: > 0 } id) return false;
        try { return _locks.IsOwnedLocally(id, "oop-project:" + _workspace.Project!.Id); }
        catch (Exception e) when (e is not OutOfMemoryException) { return false; }
    }
    private bool Base(Stamp s) => Editable() && _workspace.Project?.Id == s.Project && _workspace.SessionId == s.Session
        && _workspace.LockId == s.Lock && _targetVersion == s.TargetVersion && ReferenceEquals(_target, s.Target)
        && _workspace.ActiveFileId == s.Target.Id && _workspace.Files.Any(f => ReferenceEquals(f, s.Target))
        && !s.Target.Deleted && s.Target.MediaKind == "binary" && s.Target.RelativePath == s.Path;
    private bool Strict(Stamp s) => Base(s) && _workspace.WorkingGeneration == s.Generation
        && _workspace.CurrentRevision?.Id == s.Head && _workspace.Project?.HeadRevisionId == s.ProjectHead
        && s.Target.DirtyBinary == s.Dirty && s.Target.IsNew == s.New
        && ReferenceEquals(s.Target.BinaryContent, s.Buffer) && s.Target.DurableHash == s.Hash && s.Target.DurableLength == s.Length;
    private Stamp? Capture()
    {
        if (!Editable() || _target is not { Deleted: false, MediaKind: "binary" } t || _workspace.ActiveFileId != t.Id
            || !_workspace.Files.Any(f => ReferenceEquals(f, t))) return null;
        if ((t.DirtyBinary || t.IsNew ? t.BinaryContent?.LongLength ?? 0 : t.DurableLength) > ArchiveLimits.SingleFile) return null;
        return new(_workspace.Project!.Id, _workspace.SessionId, _workspace.LockId!, _targetVersion, t, t.RelativePath,
            _workspace.WorkingGeneration, _workspace.CurrentRevision?.Id, _workspace.Project.HeadRevisionId,
            t.BinaryContent, t.DurableHash, t.DurableLength, t.DirtyBinary, t.IsNew);
    }
    private bool UndoCurrent(UndoEntry u)
    {
        if (!Base(u.Preview.Stamp) || _workspace.WorkingGeneration != u.Generation) return false;
        var t = u.Preview.Stamp.Target;
        // Save may clear transient buffers. Durable identity, not stale buffer
        // reference, is the effective authority once the bytes have been saved.
        return t.DirtyBinary || t.IsNew
            ? ReferenceEquals(t.BinaryContent, u.Preview.New) && t.BinaryContent?.LongLength == u.Preview.Info.NewBytes
            : t.DurableHash?.Hex == u.Preview.Info.NewSha256 && t.DurableLength == u.Preview.Info.NewBytes;
    }
    private void Saved(WorkspaceSaveReceipt receipt) { _ = receipt; SourceChanged(); }
    private void SourceChanged()
    {
        if (_disposed || _mutating) return;
        if (_active is { Invalid: 0 } op && !Current(op) || _preview is { } p && !Strict(p.Stamp) || _undo is { } u && !UndoCurrent(u))
            Invalidate(BinaryReplacementState.Stale);
    }
    private void Invalidate(BinaryReplacementState state)
    {
        ++_intent; _preview = null;
        DiscardStaleUndo(); // A strict preview can expire on Save while the previous successful Undo remains valid.
        if (_active is { } op)
        {
            Volatile.Write(ref op.Invalid, 1);
            try { Transport(() => { op.Stop.Cancel(); return true; }); }
            catch (Exception e) when (e is not OutOfMemoryException) { }
        }
        _state = state; Notify();
    }
    private void DiscardStaleUndo()
    {
        if (_undo is { } u && !UndoCurrent(u)) _undo = null;
    }
    public void Cancel() { if (!_disposed && CanCancel) Invalidate(BinaryReplacementState.Cancelled); }
    private bool Current(Operation op) => !_disposed && ReferenceEquals(_active, op) && op.Invalid == 0 && _intent == op.Intent
        && (op.Undo is { } u ? ReferenceEquals(_undo, u) && UndoCurrent(u) : Strict(op.Stamp));
    private async Task<bool> Validate(Operation op)
    {
        if (Volatile.Read(ref _disposed) || Volatile.Read(ref op.Invalid) != 0) return false;
        bool valid = false;
        try
        {
            await _dispatch(() =>
            {
                if (_disposed || op.Invalid != 0) return;
                valid = Current(op); if (!valid) Invalidate(BinaryReplacementState.Stale);
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (_disposed) { return false; }
        return valid && !Volatile.Read(ref _disposed) && Volatile.Read(ref op.Invalid) == 0;
    }
    public Task StageAsync(string? pickedName, long advertisedSize, Func<CancellationToken, ValueTask<Stream>> open)
    {
        if (_disposed || _mutating) return Task.CompletedTask;
        if (Busy)
        {
            if (_active is null or { Invalid: 0 }) _state = BinaryReplacementState.Busy;
            Notify(); return Task.CompletedTask;
        }
        _preview = null; DiscardStaleUndo(); ++_intent;
        var stamp = Capture();
        if (stamp is null || advertisedSize < 0 || advertisedSize > ArchiveLimits.SingleFile)
        { _state = BinaryReplacementState.Failed; Notify(); return Task.CompletedTask; }
        var op = new Operation(_intent, stamp, null, pickedName ?? "");
        return Start(op, () => Run(op, open));
    }
    public Task UndoAsync()
    {
        if (_disposed || !CanUndo || _undo is not { } u) return Task.CompletedTask;
        var op = new Operation(++_intent, u.Preview.Stamp, u, "");
        return Start(op, () => Run(op, null));
    }
    private Task Start(Operation op, Func<Task> run)
    {
        if (!_admission.Enter(op)) { op.Stop.Dispose(); _state = BinaryReplacementState.Busy; Notify(); return Task.CompletedTask; }
        _active = op; _state = BinaryReplacementState.Reading; Notify();
        var task = run(); Observe(task); return task;
    }
    private async Task<byte[]> ReadTarget(Operation op)
    {
        Task<byte[]>? read = null;
        await _dispatch(() =>
        {
            if (Current(op)) read = Transport(() => _workspace.GetFileBytesAsync(op.Stamp.Target, op.Stop.Token));
            else if (!_disposed && op.Invalid == 0) Invalidate(BinaryReplacementState.Stale);
        }).ConfigureAwait(false);
        if (read is null) throw new OperationCanceledException();
        var bytes = await read.ConfigureAwait(false);
        if (!await Validate(op).ConfigureAwait(false)) throw new OperationCanceledException();
        if (bytes.LongLength > ArchiveLimits.SingleFile) throw new TargetDataMismatchException();
        return bytes;
    }
    private async Task<(byte[] Bytes, string Hash)> Hash(Operation op, byte[] bytes, bool copy)
    {
        if (bytes.LongLength > ArchiveLimits.SingleFile) throw new IOException();
        var owned = copy ? new byte[bytes.Length] : bytes;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        const int chunk = 256 * 1024; // Work quantum, not a smaller file cap.
        for (int offset = 0; offset < bytes.Length; offset += chunk)
        {
            int count = Math.Min(chunk, bytes.Length - offset);
            if (copy) bytes.AsSpan(offset, count).CopyTo(owned.AsSpan(offset, count));
            hash.AppendData(owned.AsSpan(offset, count));
            await Transport(() => _yield(op.Stop.Token)).ConfigureAwait(false);
            if (!await Validate(op).ConfigureAwait(false)) throw new OperationCanceledException();
        }
        return (owned, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }
    private async Task<byte[]> ReadNew(Operation op, Func<CancellationToken, ValueTask<Stream>> open)
    {
        Stream? stream = null;
        try
        {
            stream = await Transport(() => open(op.Stop.Token)).ConfigureAwait(false);
            if (!await Validate(op).ConfigureAwait(false)) throw new OperationCanceledException();
            if (stream is null) throw new IOException();
            using var content = new MemoryStream(); byte[] scratch = new byte[64 * 1024];
            while (true)
            {
                int wanted = (int)Math.Min(scratch.Length, ArchiveLimits.SingleFile - content.Length + 1);
                int count = await Transport(() => stream.ReadAsync(scratch.AsMemory(0, wanted), op.Stop.Token)).ConfigureAwait(false);
                if (!await Validate(op).ConfigureAwait(false)) throw new OperationCanceledException();
                if (count < 0 || count > wanted || count > ArchiveLimits.SingleFile - content.Length) throw new IOException();
                if (count == 0) return content.ToArray();
                int required = checked((int)content.Length + count);
                if (required > content.Capacity) content.Capacity = (int)Math.Min(ArchiveLimits.SingleFile, Math.Max(required, (long)content.Capacity * 2));
                content.Write(scratch, 0, count);
            }
        }
        finally { if (stream is not null) await Transport(stream.DisposeAsync).ConfigureAwait(false); }
    }
    private async Task Run(Operation op, Func<CancellationToken, ValueTask<Stream>>? open)
    {
        Prepared? prepared = null; string? currentHash = null; bool failed = false, targetMismatch = false;
        try
        {
            if (!await Validate(op).ConfigureAwait(false)) return;
            var raw = await ReadTarget(op).ConfigureAwait(false);
            if (!await Validate(op).ConfigureAwait(false)) return;
            var old = await Hash(op, raw, copy: op.Undo is null).ConfigureAwait(false);
            if (!await Validate(op).ConfigureAwait(false)) return;
            if (op.Undo is not null) currentHash = old.Hash;
            else
            {
                if (!op.Stamp.Dirty && !op.Stamp.New && (op.Stamp.Hash?.Hex != old.Hash || op.Stamp.Length != old.Bytes.LongLength)) throw new TargetDataMismatchException();
                var bytes = await ReadNew(op, open!).ConfigureAwait(false);
                if (!await Validate(op).ConfigureAwait(false)) return;
                var next = await Hash(op, bytes, copy: false).ConfigureAwait(false);
                if (!await Validate(op).ConfigureAwait(false)) return;
                prepared = new(op.Stamp, old.Bytes, next.Bytes, new(op.Stamp.Target.Id, op.Stamp.Path, op.Name,
                    old.Bytes.LongLength, next.Bytes.LongLength, old.Hash, next.Hash));
            }
        }
        catch (TargetDataMismatchException) { failed = true; targetMismatch = true; }
        catch (Exception e) when (e is not OutOfMemoryException) { failed = true; }
        finally
        {
            try
            {
                if (!Volatile.Read(ref _disposed))
                {
                    try { await _dispatch(() => Finish(op, prepared, currentHash, failed, targetMismatch)).ConfigureAwait(false); }
                    catch (ObjectDisposedException) when (_disposed) { }
                }
            }
            finally
            {
                Interlocked.CompareExchange(ref _active, null, op); _admission.Release(op); op.Stop.Dispose();
            }
        }
    }
    private void Finish(Operation op, Prepared? prepared, string? hash, bool failed, bool targetMismatch)
    {
        if (_disposed || !ReferenceEquals(_active, op)) return;
        // Observed target-data loss overrides metadata-only UndoCurrent. Unlike
        // a failed new-file choice or Save-only stage expiry it is not reversible
        // authority; no other inverse can be created while this lease is held.
        if (targetMismatch) _undo = null;
        if (op.Invalid == 0)
        {
            if (!Current(op)) Invalidate(BinaryReplacementState.Stale);
            else if (failed) _state = BinaryReplacementState.Failed;
            else if (op.Undo is { } u)
            {
                if (hash != u.Preview.Info.NewSha256)
                {
                    _undo = null; // Actual effective bytes disproved the expected preimage; never retry this inverse.
                    Invalidate(BinaryReplacementState.Stale);
                }
                else
                {
                    _undo = null; _mutating = true;
                    bool changed;
                    try { changed = _workspace.ReplaceFileBytes(u.Preview.Stamp.Target.Id, u.Preview.Old); }
                    finally { _mutating = false; }
                    _state = changed ? BinaryReplacementState.Undone : BinaryReplacementState.Failed;
                }
            }
            else if (prepared is not null) { _preview = prepared; _state = BinaryReplacementState.Preview; }
            else _state = BinaryReplacementState.Failed;
        }
        Interlocked.CompareExchange(ref _active, null, op); _admission.Release(op); Notify();
    }
    public bool Confirm()
    {
        if (!CanConfirm || _preview is not { } p) return false;
        _preview = null; _mutating = true;
        bool changed;
        try { changed = _workspace.ReplaceFileBytes(p.Stamp.Target.Id, p.New); }
        finally { _mutating = false; }
        // A successful command is a real mutation even if a synchronous observer
        // edits/selects/saves afterwards. Never roll it back or report no change.
        if (changed)
        {
            var undo = new UndoEntry(p, p.Stamp.Generation + 1);
            _undo = UndoCurrent(undo) ? undo : null; // Exactly one inverse for THIS successful mutation, not a stack.
        }
        else DiscardStaleUndo();
        _state = changed ? BinaryReplacementState.Applied : BinaryReplacementState.Failed;
        Notify(); return changed;
    }
    private void Released()
    {
        if (Volatile.Read(ref _disposed)) return;
        try
        {
            var task = _dispatch(() =>
            {
                if (_disposed) return;
                if (!Busy && _active is null && _state == BinaryReplacementState.Busy)
                    _state = _preview is not null ? BinaryReplacementState.Preview : _undo is not null ? BinaryReplacementState.Applied : BinaryReplacementState.Idle;
                Notify();
            });
            Observe(task);
        }
        catch (ObjectDisposedException) when (_disposed) { }
    }
    private void Notify()
    {
        if (_disposed) return;
        try { Changed?.Invoke(); } catch (Exception e) when (e is not OutOfMemoryException) { /* Observer cannot strand actual work. */ }
    }
    private static void Observe(Task task) => _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private static T Transport<T>(Func<T> start)
    {
        var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(null);
        try { return start(); } finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        Volatile.Write(ref _disposed, true); _workspace.WorkspaceChanged -= SourceChanged; _workspace.RevisionCommitted -= Saved;
        _admission.Changed -= Released; _preview = null; _undo = null; ++_intent;
        if (_active is { } op)
        {
            Volatile.Write(ref op.Invalid, 1);
            try { Transport(() => { op.Stop.Cancel(); return true; }); } catch (Exception e) when (e is not OutOfMemoryException) { }
        }
        Changed = null;
    }
    private sealed class TargetDataMismatchException : IOException { }
    private sealed record Stamp(ProjectId Project, Guid Session, string Lock, long TargetVersion, WorkingFile Target, string Path,
        long Generation, RevisionId? Head, RevisionId? ProjectHead, byte[]? Buffer, Client.Workspace.Domain.SHA256? Hash,
        long Length, bool Dirty, bool New);
    private sealed record Prepared(Stamp Stamp, byte[] Old, byte[] New, BinaryReplacementPreview Info);
    private sealed record UndoEntry(Prepared Preview, long Generation);
    private sealed class Operation(long intent, Stamp stamp, UndoEntry? undo, string name)
    {
        internal readonly long Intent = intent;
        internal readonly Stamp Stamp = stamp;
        internal readonly UndoEntry? Undo = undo;
        internal readonly string Name = name;
        internal readonly CancellationTokenSource Stop = new();
        internal int Invalid;
    }
}
