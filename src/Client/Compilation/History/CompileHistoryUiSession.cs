using System.Collections.Immutable;
using Client.Compilation.History.Index;
using Client.Compilation.Presentation;
using Client.Pdf;
using Client.Pdf.Preview;
using Client.RuntimeAssets;
using Client.Workspace;
using Client.Workspace.Domain;

namespace Client.Compilation.History;

public sealed record CompileHistoryUiView(string Notice, ImmutableArray<HistoricalArtifactKey> Entries,
    HistoricalCompileArtifact? Opened = null, string? Association = null);

/// <summary>Explicit saved-PDF workflow. Borrows workspace/compile/inspector;
/// owns the supplied store/index and historical preview, not their borrowed ports.
/// Unregistered: composition/UI wiring is deliberately left to the caller.</summary>
public sealed class CompileHistoryUiSession : IAsyncDisposable
{
    private readonly WorkspaceCoordinator _workspace;
    private readonly CompileWorkspaceSession _compile;
    private readonly CompileArtifactHistoryStore _store;
    private readonly CompileHistoryIndex _index;
    private readonly ArtifactHistoryBudget _artifactBudget;
    private readonly HistoryIndexBudget _indexBudget;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _active = Task.CompletedTask, _retiringPreview = Task.CompletedTask;
    private PdfPreviewController _preview;
    private Stamp? _viewStamp;
    private Recovery? _recovery;
    private bool _disposed;
    private CompileHistoryUiView _view = new("Refresh to discover saved PDFs.", []);
    private sealed record Stamp(ProjectId Project, Guid Session);
    // At most ONE recoverable unpublished/uncertain key. Never silently drop it
    // on a project switch; keep it hidden until that project is explicitly revisited.
    private sealed record Recovery(HistoricalArtifactKey Key, bool PersistenceUncertain);
    public CompileHistoryUiSession(WorkspaceCoordinator workspace, CompileWorkspaceSession compile,
        CompileArtifactHistoryStore store, CompileHistoryIndex index, ArtifactHistoryBudget artifactBudget, HistoryIndexBudget indexBudget)
    {
        ArgumentNullException.ThrowIfNull(workspace); ArgumentNullException.ThrowIfNull(compile);
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(index);
        if (artifactBudget is not { IsValid: true } || indexBudget is not { IsValid: true }) throw new ArgumentException("Explicit history budgets required.");
        _workspace = workspace; _compile = compile; _store = store; _index = index; _artifactBudget = artifactBudget; _indexBudget = indexBudget;
        _preview = new(compile.ConfiguredPorts?.Inspector); _preview.Changed += PreviewChanged;
        _viewStamp = Capture(); _workspace.WorkspaceChanged += WorkspaceChanged; _compile.Changed += CompileChanged;
    }
    public event Action? Changed;
    public CompileHistoryUiView View { get { lock (_gate) return _view; } }
    public PdfPreviewController Preview { get { lock (_gate) return _preview; } }
    public bool Busy { get { lock (_gate) return !_active.IsCompleted; } }
    public bool CanSave { get { lock (_gate) return !_disposed && _active.IsCompleted && _recovery is null && OwnedOutput() is not null; } }
    public bool CanRetry { get { lock (_gate) return !_disposed && _active.IsCompleted && _recovery?.Key.ProjectId == _workspace.Project?.Id; } }
    public HistoricalArtifactKey? RecoverableKey { get { lock (_gate) return _recovery?.Key.ProjectId == _workspace.Project?.Id ? _recovery?.Key : null; } }
    public bool PersistenceUncertain { get { lock (_gate) return RecoverableKey is not null && _recovery!.PersistenceUncertain; } }
    public Task WhenSettled { get { lock (_gate) return Task.WhenAll(_active, _store.WhenSettled, _index.WhenSettled, _retiringPreview, _preview.WhenSettled); } }
    private Stamp? Capture() => _workspace.IsOpen && _workspace.Project is { } p ? new(p.Id, _workspace.SessionId) : null;
    private bool Current(Stamp stamp) => !_disposed && _workspace.Project?.Id == stamp.Project && _workspace.SessionId == stamp.Session;
    private AcceptedCompileOutput? OwnedOutput()
    {
        var output = _compile.DisplayedOutput; var stamp = Capture();
        return stamp is not null && output is { Status: CompileOutputStatus.Succeeded, ContentIdentity: not null }
            && output.SnapshotIdentity.ProjectId == stamp.Project && output.SnapshotIdentity.SessionId == stamp.Session ? output : null;
    }
    private void Publish(Stamp stamp, Func<CompileHistoryUiView, CompileHistoryUiView> change)
    { bool show; lock (_gate) { show = Current(stamp); if (show) _view = change(_view); } if (show) Notify(); }
    private void Notify()
    {
        if (_disposed) return;
        try { Changed?.Invoke(); }
        catch (Exception error) when (error is not OutOfMemoryException) { /* A view observer cannot release or strand operation ownership. */ }
    }
    private void CompileChanged() { WorkspaceChanged(); Notify(); }
    private void PreviewChanged() => Notify();
    private void WorkspaceChanged()
    {
        bool changed = false;
        lock (_gate)
        {
            var stamp = Capture();
            if (!_disposed && stamp != _viewStamp)
            {
                _viewStamp = stamp; _view = new("Refresh to discover saved PDFs for this project.", []); ReplacePreview(); changed = true;
            }
        }
        if (changed) Notify();
    }
    private void ReplacePreview()
    {
        var previous = _preview; previous.Changed -= PreviewChanged; _ = previous.DisposeAsync();
        if (previous.ActualWorkPending) _retiringPreview = previous.WhenSettled;
        _preview = new(_compile.ConfiguredPorts?.Inspector); _preview.Changed += PreviewChanged;
    }
    private Task Start(Func<Stamp, Task> action)
    {
        TaskCompletionSource actual; Stamp stamp;
        lock (_gate)
        {
            if (_disposed || !_active.IsCompleted || Capture() is not { } captured) return Task.CompletedTask;
            stamp = captured; actual = new(TaskCreationOptions.RunContinuationsAsynchronously); _active = actual.Task;
        }
        Notify(); return Execute();
        async Task Execute()
        {
            try { if (Current(stamp)) await action(stamp); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { Publish(stamp, v => v with { Notice = "History operation could not finish. Refresh or reconcile explicitly." }); }
            finally { Observe(Settle()); }
        }
        async Task Settle()
        {
            try { await Task.WhenAll(_store.WhenSettled, _index.WhenSettled); }
            finally { actual.TrySetResult(); Notify(); }
        }
    }
    public Task RefreshAsync() => Start(async stamp =>
    {
        var result = await _index.ListAsync(stamp.Project, _indexBudget, _stop.Token);
        if (!Current(stamp)) return;
        Publish(stamp, v => result.Status is HistoryIndexStatus.Ready or HistoryIndexStatus.Missing && result.Snapshot is not null
            ? v with { Entries = result.Snapshot.Entries, Notice = result.Snapshot.Entries.IsEmpty ? "No saved PDFs listed." : "Saved PDFs refreshed. Opening revalidates each artifact." }
            : v with { Entries = [], Notice = "Saved-PDF list unavailable: " + result.Status + ". Retry Refresh." });
    });
    public Task SaveDisplayedAsync() => Start(async stamp =>
    {
        AcceptedCompileOutput? output; lock (_gate) output = Current(stamp) && _recovery is null ? OwnedOutput() : null;
        if (output is null) { Publish(stamp, v => v with { Notice = "No displayed PDF from this project/session, or a previous save needs reconciliation." }); return; }
        Publish(stamp, v => v with { Notice = "Saving displayed PDF to history; source files are not saved." });
        if (!Current(stamp)) return;
        var persisted = await _store.PersistAsync(output, null, _artifactBudget, _stop.Token);
        if (!Current(stamp)) { RememberLate(stamp, persisted); return; }
        if (persisted.Status == RuntimeAssetStatus.Installed && persisted.Key is { } saved)
        {
            lock (_gate) _recovery = new(saved, false);
            await PublishIndex(stamp, saved); return;
        }
        if (persisted.Status == RuntimeAssetStatus.Uncertain && persisted.Key is { } uncertain)
        {
            lock (_gate) _recovery = new(uncertain, true);
            Publish(stamp, v => v with { Notice = "PDF save outcome uncertain. Reconcile saved PDF explicitly before updating the list." }); return;
        }
        Publish(stamp, v => v with { Notice = "PDF was not confirmed saved: " + persisted.Status + ". No listing was added." });
    });
    private void RememberLate(Stamp stamp, ArtifactHistoryResult result)
    {
        lock (_gate)
            if (!_disposed && result.Key is { } key && key.ProjectId == stamp.Project && result.Status is RuntimeAssetStatus.Installed or RuntimeAssetStatus.Uncertain)
                _recovery ??= new(key, result.Status == RuntimeAssetStatus.Uncertain);
        // No old-project notice/key/list/PDF publication. Revisiting that project
        // exposes only a recoverable pointer; explicit Retry freshly validates it.
    }
    public Task RetryPublicationAsync() => Start(async stamp =>
    {
        Recovery? recovery; lock (_gate) recovery = _recovery?.Key.ProjectId == stamp.Project ? _recovery : null;
        if (recovery is null) return;
        var fresh = await _store.ReadAsync(recovery.Key, stamp.Project, null, _artifactBudget, _stop.Token);
        if (!Current(stamp)) return;
        if (fresh.Status != RuntimeAssetStatus.Ready || fresh.Artifact is null)
        { Publish(stamp, v => v with { Notice = "Saved PDF reconciliation: " + fresh.Status + ". Key retained; no listing overwrite or automatic retry." }); return; }
        lock (_gate) _recovery = new(recovery.Key, false);
        await PublishIndex(stamp, recovery.Key);
    });
    private async Task PublishIndex(Stamp stamp, HistoricalArtifactKey key)
    {
        if (!Current(stamp)) return;
        var list = await _index.ListAsync(stamp.Project, _indexBudget, _stop.Token);
        if (!Current(stamp)) return;
        if (list.Status is not (HistoryIndexStatus.Ready or HistoryIndexStatus.Missing) || list.Snapshot is null)
        { Publish(stamp, v => v with { Notice = "PDF was saved, but listing update failed: " + list.Status + ". Retry listing update." }); return; }
        var append = await _index.AppendPersistedAsync(stamp.Project, key, list.Snapshot.Revision, _indexBudget, _stop.Token);
        if (!Current(stamp)) return;
        if (append.Status is HistoryIndexStatus.Saved or HistoryIndexStatus.AlreadyIndexed && append.Snapshot is not null)
        {
            lock (_gate) _recovery = null;
            Publish(stamp, v => v with { Entries = append.Snapshot.Entries, Notice = "PDF saved to history. It may differ from the current source." });
        }
        else Publish(stamp, v => v with { Notice = "PDF was saved, but listing update " + (append.Status == HistoryIndexStatus.Uncertain ? "is uncertain" : "failed: " + append.Status) + ". Retry listing update using a fresh read." });
    }
    public Task OpenAsync(HistoricalArtifactKey key) => Start(async stamp =>
    {
        lock (_gate) { if (Current(stamp)) { _view = _view with { Opened = null, Association = null }; ReplacePreview(); } }
        if (key.ProjectId != stamp.Project) { Publish(stamp, v => v with { Notice = "Saved PDF belongs to another project; not opened." }); return; }
        var read = await _store.ReadAsync(key, stamp.Project, null, _artifactBudget, _stop.Token);
        if (!Current(stamp)) return;
        if (read.Status != RuntimeAssetStatus.Ready || read.Artifact is null)
        { Publish(stamp, v => v with { Notice = "Saved PDF could not be opened: " + read.Status + ". Refresh or retry; no stale PDF shown." }); return; }
        lock (_gate)
        {
            if (!Current(stamp)) return;
            _view = _view with { Opened = read.Artifact, Association = read.Association?.Scope, Notice = "Saved PDF opened. It may differ from the current source." };
            if (_retiringPreview.IsCompleted) _preview.Provide(new PdfDocument(read.Artifact.CopyPdf()), "Saved PDF", PdfDisplayProvenance.CallerSaysHistorical);
            else _view = _view with { Notice = "Saved PDF opened. Previous preview is finishing; retry Open when ready." };
        }
        Notify();
    });
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true; _workspace.WorkspaceChanged -= WorkspaceChanged; _compile.Changed -= CompileChanged; _preview.Changed -= PreviewChanged;
            _view = new("Saved-PDF session closed.", []); Changed = null;
        }
        _stop.Cancel(); _ = _preview.DisposeAsync(); _ = _store.DisposeAsync(); _ = _index.DisposeAsync();
        // No borrowed workspace/compile/inspector disposal or foreign-task join.
        return ValueTask.CompletedTask;
    }
    private static void Observe(Task task) => _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
