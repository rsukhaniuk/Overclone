using Client.Workspace.Domain;
using Client.Workspace.Transfer;

namespace Client.Workspace.Imports;

public enum SingleFileImportState { Idle, Reading, Imported, Stale, Cancelled, Busy, Failed, Disposed }

/// <summary>Page-owned additive import. Public commands and workspace callbacks
/// run on the UI owner. Foreign reads/cleanup are context-free; model access is
/// dispatched. Borrows workspace/locks, never saves or starts downstream jobs.</summary>
public sealed class SingleFileImportController : IDisposable
{
    private readonly LocalFileReadAdmission _admission;
    private readonly WorkspaceCoordinator _workspace;
    private readonly IWorkspaceLocks _locks;
    private readonly Func<Action, Task> _dispatch;
    private readonly Action<FileId>? _selected;
    private Operation? _active;
    private long _intent;
    private bool _disposed;
    private SingleFileImportState _state;
    private ProjectId? _viewProject;
    private Guid _viewSession;
    public event Action? Changed;
    public bool Busy => _admission.Busy;
    public SingleFileImportState State => Volatile.Read(ref _disposed) ? SingleFileImportState.Disposed : _state;
    public string Notice => State switch
    {
        SingleFileImportState.Reading => "Reading a local file for this project…",
        SingleFileImportState.Imported => "File imported. Save normally to keep it on this device.",
        SingleFileImportState.Stale => "Import discarded: the writable project session changed.",
        SingleFileImportState.Cancelled => Busy ? "Import cancelled. The pending read is still settling." : "Import cancelled.",
        SingleFileImportState.Busy => "An import is still settling. Wait before choosing another file.",
        SingleFileImportState.Failed => "File could not be imported. Check its path, size and project write access.",
        SingleFileImportState.Disposed => "File import closed.",
        _ => "Choose a local file to add to this project."
    };

    public SingleFileImportController(WorkspaceCoordinator workspace, IWorkspaceLocks locks, Func<Action, Task> dispatch,
        Action<FileId>? selected = null)
    {
        _workspace = workspace; _locks = locks; _dispatch = dispatch; _selected = selected;
        _admission = LocalFileReadAdmission.For(workspace);
        _admission.Changed += AdmissionReleased;
        _viewProject = workspace.Project?.Id; _viewSession = workspace.SessionId;
        workspace.WorkspaceChanged += SourceChanged;
    }

    /// <summary>Capture before invoking ANY foreign opener. Busy refuses without
    /// opening, invalidating or queuing another read. Advertised length is only
    /// preflight; actual consumed bytes are independently bounded.</summary>
    public Task ImportAsync(string? filename, long advertisedSize, Func<CancellationToken, ValueTask<Stream>> open)
    {
        if (_disposed) return Task.CompletedTask;
        if (Busy)
        {
            if (_active is null or { Invalid: 0 }) _state = SingleFileImportState.Busy;
            Notify(); return Task.CompletedTask;
        }
        long intent = ++_intent;
        if (!Editable() || advertisedSize < 0 || advertisedSize > ArchiveLimits.SingleFile)
        { _state = SingleFileImportState.Failed; Notify(); return Task.CompletedTask; }
        var name = string.IsNullOrWhiteSpace(filename) ? "imported.tex" : filename.Trim();
        var op = new Operation(intent, _workspace.Project!.Id, _workspace.SessionId, _workspace.LockId!, name);
        if (!_admission.Enter(op))
        { op.Stop.Dispose(); _state = SingleFileImportState.Busy; Notify(); return Task.CompletedTask; }
        _active = op; _state = SingleFileImportState.Reading; Notify();
        var task = RunAsync(op, open);
        // Caller awaits actual settlement too. Observe even if its view unmounts.
        _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private bool Editable()
    {
        if (_disposed || !_workspace.IsOpen || _workspace.IsReadOnly || _workspace.Recovery is not null
            || _workspace.SessionId == Guid.Empty || _workspace.LockId is not { Length: > 0 } id) return false;
        try { return _locks.IsOwnedLocally(id, "oop-project:" + _workspace.Project!.Id); }
        catch (Exception e) when (e is not OutOfMemoryException) { return false; }
    }
    private bool Current(Operation op) => ReferenceEquals(_active, op) && op.Invalid == 0 && _intent == op.Intent
        && Editable() && _workspace.Project?.Id == op.Project && _workspace.SessionId == op.Session && _workspace.LockId == op.Lock;

    private void SourceChanged()
    {
        if (_disposed) return;
        bool newView = _viewProject != _workspace.Project?.Id || _viewSession != _workspace.SessionId;
        _viewProject = _workspace.Project?.Id; _viewSession = _workspace.SessionId;
        if (_active is { Invalid: 0 } op && !Current(op)) Invalidate(op, SingleFileImportState.Stale);
        else if (newView && !Busy) { _state = SingleFileImportState.Idle; Notify(); }
        // No generation/head binding: ordinary edits and saves in THIS session
        // are allowed. Current coordinator path collision guards remain final.
    }
    public void Cancel()
    {
        if (!_disposed && _active is { Invalid: 0 } op) Invalidate(op, SingleFileImportState.Cancelled);
    }
    private void Invalidate(Operation op, SingleFileImportState state)
    {
        Volatile.Write(ref op.Invalid, 1); ++_intent; _state = state;
        try { Transport(() => { op.Stop.Cancel(); return true; }); }
        catch (Exception e) when (e is not OutOfMemoryException) { /* Foreign cancellation may fail; actual work is still owned. */ }
        Notify();
    }
    private async Task<bool> ValidateAsync(Operation op)
    {
        if (Volatile.Read(ref _disposed) || Volatile.Read(ref op.Invalid) != 0) return false;
        bool valid = false;
        try
        {
            await _dispatch(() =>
            {
                if (_disposed || op.Invalid != 0) return;
                valid = Current(op);
                if (!valid) Invalidate(op, SingleFileImportState.Stale);
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed)) { return false; }
        return valid && !Volatile.Read(ref _disposed) && Volatile.Read(ref op.Invalid) == 0;
    }

    private async Task RunAsync(Operation op, Func<CancellationToken, ValueTask<Stream>> open)
    {
        Stream? stream = null;
        byte[]? bytes = null;
        bool failed = false;
        try
        {
            if (!await ValidateAsync(op).ConfigureAwait(false)) return;
            stream = await Transport(() => open(op.Stop.Token)).ConfigureAwait(false);
            if (!await ValidateAsync(op).ConfigureAwait(false)) return;
            if (stream is null) throw new IOException();
            using var content = new MemoryStream();
            // IO chunk size, NOT a smaller document cap. Even a lying stream can
            // consume only SingleFile+1 bytes before refusal, with bounded storage.
            byte[] chunk = new byte[64 * 1024];
            while (true)
            {
                int wanted = (int)Math.Min(chunk.Length, ArchiveLimits.SingleFile - content.Length + 1);
                int count = await Transport(() => stream.ReadAsync(chunk.AsMemory(0, wanted), op.Stop.Token)).ConfigureAwait(false);
                if (!await ValidateAsync(op).ConfigureAwait(false)) return;
                if (count < 0 || count > wanted || count > ArchiveLimits.SingleFile - content.Length) throw new IOException();
                if (count == 0) break;
                int required = checked((int)content.Length + count);
                if (required > content.Capacity)
                    content.Capacity = (int)Math.Min(ArchiveLimits.SingleFile, Math.Max(required, (long)content.Capacity * 2));
                content.Write(chunk, 0, count);
            }
            bytes = content.ToArray();
        }
        catch (Exception e) when (e is not OutOfMemoryException) { failed = true; }
        finally
        {
            try
            {
                if (stream is not null)
                {
                    try { await Transport(stream.DisposeAsync).ConfigureAwait(false); }
                    catch (Exception e) when (e is not OutOfMemoryException) { failed = true; }
                }
                // Disposal is part of actual admission and BEFORE any mutation.
                // Invalidated work can only retire Busy, never publish old result.
                if (!Volatile.Read(ref _disposed))
                {
                    try { await _dispatch(() => Finish(op, bytes, failed)).ConfigureAwait(false); }
                    catch (ObjectDisposedException) when (Volatile.Read(ref _disposed)) { }
                }
            }
            finally
            {
                Interlocked.CompareExchange(ref _active, null, op);
                _admission.Release(op);
                op.Stop.Dispose();
            }
        }
    }
    private void Finish(Operation op, byte[]? bytes, bool failed)
    {
        if (_disposed || !ReferenceEquals(_active, op)) return;
        if (op.Invalid == 0)
        {
            if (!Current(op)) Invalidate(op, SingleFileImportState.Stale);
            else if (failed || bytes is null) _state = SingleFileImportState.Failed;
            else
            {
                // Sole content mutation seam. Retain the exact new ID by path
                // and identity difference, NOT ActiveFile/Files.Last: a synchronous
                // subscriber may append/select another file during BumpGeneration.
                var before = _workspace.Files.Select(f => f.Id).ToHashSet();
                string? path = ProjectPathValidator.Normalize(op.Name);
                bool created = _workspace.CreateFileFromBytes(op.Name, bytes);
                if (!Current(op)) { if (op.Invalid == 0) Invalidate(op, SingleFileImportState.Stale); }
                else if (!created) _state = SingleFileImportState.Failed;
                else
                {
                    var file = _workspace.Files.SingleOrDefault(f => !before.Contains(f.Id) && !f.Deleted && f.RelativePath == path);
                    if (file is not null)
                    {
                        _workspace.SelectFile(file.Id);
                        _selected?.Invoke(file.Id); // Existing page selection/intent cancellation, on the same UI owner.
                    }
                    _state = SingleFileImportState.Imported;
                }
            }
        }
        Interlocked.CompareExchange(ref _active, null, op);
        _admission.Release(op);
        Notify(); // Live-view admission update only after stale/cancelled settlement.
    }
    private void AdmissionReleased()
    {
        if (Volatile.Read(ref _disposed)) return;
        try
        {
            var view = _dispatch(() =>
            {
                if (_disposed) return;
                if (!Busy && _active is null && _state == SingleFileImportState.Busy) _state = SingleFileImportState.Idle;
                Notify();
            });
            _ = view.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed)) { }
    }
    private void Notify()
    {
        if (_disposed) return;
        try { Changed?.Invoke(); }
        catch (Exception e) when (e is not OutOfMemoryException) { /* View observer must not strand an admitted read. */ }
    }
    // SDK/Stream implementations may have INTERNAL capturing awaits. Start only
    // foreign IO/cleanup same-thread context-free, restore caller immediately.
    private static T Transport<T>(Func<T> start)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { return start(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        Volatile.Write(ref _disposed, true);
        _workspace.WorkspaceChanged -= SourceChanged;
        _admission.Changed -= AdmissionReleased;
        if (_active is { } op)
        {
            Volatile.Write(ref op.Invalid, 1); ++_intent;
            try { Transport(() => { op.Stop.Cancel(); return true; }); }
            catch (Exception e) when (e is not OutOfMemoryException) { }
        }
        Changed = null; // Actual operation keeps its admission/resources until finally.
    }
    private sealed class Operation(long intent, ProjectId project, Guid session, string lockId, string name)
    {
        internal readonly long Intent = intent;
        internal readonly ProjectId Project = project;
        internal readonly Guid Session = session;
        internal readonly string Lock = lockId, Name = name;
        internal readonly CancellationTokenSource Stop = new();
        internal int Invalid;
    }
}
