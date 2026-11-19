using System.Collections.Immutable;
using Client.Compilation.History.Index;
using Client.Compilation.Download;
using Client.Compilation.Presentation;
using Client.Pdf;
using Client.Pdf.Preview;
using Client.RuntimeAssets;
using Client.Workspace;
using Client.Workspace.Domain;

namespace Client.Compilation.History;

public sealed record CompileHistoryUiView(string Notice, ImmutableArray<HistoricalArtifactKey> Entries,
    HistoricalCompileArtifact? Opened = null, string? Association = null)
{
    // UI-only state. Neither an artifact/index schema nor live compile authority.
    public RevisionFileAssociation? SourceAssociation { get; init; }
    public RuntimeAssetStatus? ComparisonReadStatus { get; init; }
    public RevisionId? ComparedSavedRevision { get; init; }
    public bool ComparisonRequested { get; init; }
    public bool ComparingSavedSource { get; init; }
    public bool AssociationStale { get; init; }
    public bool WorkingTextWasDirty { get; init; }
    public string ComparisonNotice { get; init; } = "Not compared with the current saved source. Compare explicitly; captured base-head is provenance only.";
}

/// <summary>Registered explicit saved-PDF workflow. Borrows workspace/compile/
/// inspector; owns store/index and historical preview, not their borrowed ports.</summary>
public sealed class CompileHistoryUiSession : IAsyncDisposable
{
    private readonly WorkspaceCoordinator _workspace;
    private readonly CompileWorkspaceSession _compile;
    private readonly CompileArtifactHistoryStore _store;
    private readonly CompileHistoryIndex _index;
    private readonly ArtifactHistoryBudget _artifactBudget;
    private readonly HistoryIndexBudget _indexBudget;
    private readonly IPdfInspector? _historicalInspector;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _active = Task.CompletedTask, _retiringPreview = Task.CompletedTask;
    private PdfPreviewController _preview;
    private Stamp? _viewStamp;
    private SourceStamp? _sourceStamp;
    private Guid _sourceEpoch = Guid.NewGuid(), _comparisonAction;
    private Recovery? _recovery;
    private bool _disposed;
    private CompileHistoryUiView _view = new("Refresh to discover saved PDFs.", []);
    private sealed record Stamp(ProjectId Project, Guid Session);
    private sealed record SourceStamp(Stamp Owner, RevisionId? Head, long Generation, string? Lock, bool ReadOnly, bool Dirty);
    private sealed record ComparisonRequest(Guid Action, SourceStamp Source, Guid Epoch, HistoricalArtifactKey Key);
    // At most ONE recoverable unpublished/uncertain key. Never silently drop it
    // on a project switch; keep it hidden until that project is explicitly revisited.
    private sealed record Recovery(HistoricalArtifactKey Key, bool PersistenceUncertain);
    public CompileHistoryUiSession(WorkspaceCoordinator workspace, CompileWorkspaceSession compile,
        CompileArtifactHistoryStore store, CompileHistoryIndex index, ArtifactHistoryBudget artifactBudget, HistoryIndexBudget indexBudget,
        IPdfInspector? historicalInspector = null)
    {
        ArgumentNullException.ThrowIfNull(workspace); ArgumentNullException.ThrowIfNull(compile);
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(index);
        if (artifactBudget is not { IsValid: true } || indexBudget is not { IsValid: true }) throw new ArgumentException("Explicit history budgets required.");
        _workspace = workspace; _compile = compile; _store = store; _index = index; _artifactBudget = artifactBudget; _indexBudget = indexBudget;
        _historicalInspector = historicalInspector;
        _preview = CreatePreview(); _preview.Changed += PreviewChanged;
        _viewStamp = Capture(); _sourceStamp = CaptureSource();
        _workspace.WorkspaceChanged += WorkspaceChanged; _workspace.RevisionCommitted += RevisionCommitted; _compile.Changed += CompileChanged;
    }
    public event Action? Changed;
    public CompileHistoryUiView View { get { lock (_gate) return _view; } }
    public PdfPreviewController Preview { get { lock (_gate) return _preview; } }
    public bool Busy { get { lock (_gate) return !_active.IsCompleted; } }
    public bool CanSave { get { lock (_gate) return !_disposed && _active.IsCompleted && _recovery is null && OwnedOutput() is not null; } }
    public bool CanRetry { get { lock (_gate) return !_disposed && _active.IsCompleted && _recovery?.Key.ProjectId == _workspace.Project?.Id; } }
    public bool CanCompareSavedSource { get { lock (_gate) return !_disposed && _active.IsCompleted && Capture() is { } stamp && _view.Opened?.Key.ProjectId == stamp.Project; } }
    public bool CanDownloadOpened => CanCompareSavedSource;
    public RevisionId? CurrentSavedHead { get { lock (_gate) return _workspace.Project?.HeadRevisionId; } }
    public bool HasUnsavedSourceChanges { get { lock (_gate) return _workspace.IsDirty; } }
    public HistoricalArtifactKey? RecoverableKey { get { lock (_gate) return _recovery?.Key.ProjectId == _workspace.Project?.Id ? _recovery?.Key : null; } }
    public bool PersistenceUncertain { get { lock (_gate) return RecoverableKey is not null && _recovery!.PersistenceUncertain; } }
    public Task WhenSettled { get { lock (_gate) return Task.WhenAll(_active, _store.WhenSettled, _index.WhenSettled, _retiringPreview, _preview.WhenSettled); } }
    private Stamp? Capture() => _workspace.IsOpen && _workspace.Project is { } p ? new(p.Id, _workspace.SessionId) : null;
    private bool Current(Stamp stamp) => !_disposed && _workspace.Project?.Id == stamp.Project && _workspace.SessionId == stamp.Session;
    private SourceStamp? CaptureSource() => Capture() is { } owner
        ? new(owner, _workspace.Project?.HeadRevisionId, _workspace.WorkingGeneration, _workspace.LockId, _workspace.IsReadOnly, _workspace.IsDirty) : null;
    private bool ComparisonCurrent(ComparisonRequest request)
    {
        lock (_gate) return Current(request.Source.Owner) && _comparisonAction == request.Action && _sourceEpoch == request.Epoch
            && _view.Opened?.Key == request.Key && CaptureSource() == request.Source;
    }
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
    private void CompileChanged() { SynchronizeWorkspace(false); Notify(); }
    private PdfPreviewController CreatePreview() =>
        new(_historicalInspector ?? _compile.ConfiguredPorts?.Inspector,
            _artifactBudget.CompiledInputCapacity is { } capacity
                ? new PdfPreviewPolicy(InputBytes: capacity.MaxInputBytes) { CompiledInputCapacity = capacity }
                : null);
    private void PreviewChanged() => Notify();
    private void WorkspaceChanged() => SynchronizeWorkspace(true);
    private void RevisionCommitted(WorkspaceSaveReceipt receipt)
    {
        if (receipt.SessionId == _workspace.SessionId && receipt.Revision.ProjectId == _workspace.Project?.Id)
            SynchronizeWorkspace(true);
    }
    private static CompileHistoryUiView StaleComparison(CompileHistoryUiView view) => view with
    {
        AssociationStale = true, ComparingSavedSource = false,
        ComparisonNotice = "Previous comparison is stale: source, saved head, lock or session changed. Compare again; the historical PDF remains inspectable."
    };
    private void SynchronizeWorkspace(bool sourceEvent)
    {
        bool changed = false;
        lock (_gate)
        {
            if (_disposed) return;
            var stamp = Capture(); var source = CaptureSource();
            bool invalidate = sourceEvent || source != _sourceStamp;
            if (invalidate) { _sourceStamp = source; _sourceEpoch = Guid.NewGuid(); _comparisonAction = Guid.Empty; }
            if (stamp != _viewStamp)
            {
                _viewStamp = stamp; _view = new("Refresh to discover saved PDFs for this project.", []); ReplacePreview(); changed = true;
            }
            else if (invalidate && _view.Opened is not null)
            {
                if (_view.ComparisonRequested) _view = StaleComparison(_view);
                changed = true;
            }
        }
        if (changed) Notify();
    }
    private void PublishComparison(ComparisonRequest request, Func<CompileHistoryUiView, CompileHistoryUiView> change)
    {
        bool show;
        lock (_gate)
        {
            show = Current(request.Source.Owner) && _comparisonAction == request.Action && _view.Opened?.Key == request.Key;
            if (show)
            {
                if (_sourceEpoch != request.Epoch || CaptureSource() != request.Source)
                { _view = StaleComparison(_view); _comparisonAction = Guid.Empty; }
                else _view = change(_view);
            }
        }
        if (show) Notify();
    }
    private void ReplacePreview()
    {
        var previous = _preview; previous.Changed -= PreviewChanged; _ = previous.DisposeAsync();
        if (previous.ActualWorkPending) _retiringPreview = previous.WhenSettled;
        _preview = CreatePreview(); _preview.Changed += PreviewChanged;
    }
    private Task Start(Func<Stamp, Task> action, Func<Stamp, bool>? admit = null)
    {
        TaskCompletionSource actual; Stamp stamp;
        lock (_gate)
        {
            if (_disposed || !_active.IsCompleted || Capture() is not { } captured) return Task.CompletedTask;
            if (admit is not null && !admit(captured)) return Task.CompletedTask;
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
        lock (_gate) { if (Current(stamp)) { _comparisonAction = Guid.Empty; _view = new(_view.Notice, _view.Entries); ReplacePreview(); } }
        if (key.ProjectId != stamp.Project) { Publish(stamp, v => v with { Notice = "Saved PDF belongs to another project; not opened." }); return; }
        var read = await _store.ReadAsync(key, stamp.Project, null, _artifactBudget, _stop.Token);
        if (!Current(stamp)) return;
        if (read.Status != RuntimeAssetStatus.Ready || read.Artifact is null)
        { Publish(stamp, v => v with { Notice = "Saved PDF could not be opened: " + read.Status + ". Refresh or retry; no stale PDF shown." }); return; }
        lock (_gate)
        {
            if (!Current(stamp)) return;
            _view = _view with { Opened = read.Artifact, Association = read.Association?.Scope,
                SourceAssociation = read.Association, Notice = "Saved PDF opened. It may differ from the current source." };
            if (_retiringPreview.IsCompleted) _preview.Provide(new PdfDocument(read.Artifact.CopyPdf(), read.Artifact.PdfLength > PdfDocument.DefaultInputBytes
                ? _artifactBudget.CompiledInputCapacity : null), "Saved PDF", PdfDisplayProvenance.CallerSaysHistorical);
            else _view = _view with { Notice = "Saved PDF opened. Previous preview is finishing; retry Open when ready." };
        }
        Notify();
    });
    /// <summary>Read-only, explicit manifest comparison for the opened record.
    /// Captures the saved head before notifications/awaits; never saves source,
    /// compares dirty working text, rebinds provenance or constructs live authority.</summary>
    public Task CompareWithCurrentSavedSourceAsync()
    {
        ComparisonRequest? request = null;
        return Start(async _ =>
        {
            if (request is not { } captured) return;
            if (!ComparisonCurrent(captured)) { PublishComparison(captured, v => v); return; }
            ArtifactHistoryResult read;
            try { read = await _store.ReadAsync(captured.Key, captured.Source.Owner.Project, captured.Source.Head, _artifactBudget, _stop.Token); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { read = new(RuntimeAssetStatus.StorageFailure); }
            var association = read.Status == RuntimeAssetStatus.Ready && read.Artifact is not null && read.Association is { } result
                ? result : new RevisionFileAssociation(RevisionFileAssociationStatus.Unavailable, captured.Source.Head);
            PublishComparison(captured, v => v with
            {
                SourceAssociation = association, Association = association.Scope,
                ComparisonReadStatus = read.Status, ComparingSavedSource = false, AssociationStale = false,
                ComparisonNotice = read.Status != RuntimeAssetStatus.Ready || read.Artifact is null
                    ? "Saved PDF could not be revalidated: " + read.Status + ". Source-file association is unavailable; the already-opened historical PDF is unchanged. Retry comparison explicitly."
                    : AssociationMessage(association)
            });
        }, stamp =>
        {
            if (CaptureSource() is not { } source || source.Owner != stamp || _view.Opened is not { } artifact || artifact.Key.ProjectId != stamp.Project)
                return false;
            request = new(Guid.NewGuid(), source, _sourceEpoch, artifact.Key); _comparisonAction = request.Action;
            _view = _view with
            {
                SourceAssociation = null, Association = null, ComparisonReadStatus = null,
                ComparedSavedRevision = source.Head, ComparisonRequested = true, ComparingSavedSource = true,
                AssociationStale = false, WorkingTextWasDirty = source.Dirty,
                ComparisonNotice = "Comparing captured source files with the saved source head captured for this action. Working text and compile settings are not compared."
            };
            return true;
        });
    }
    /// <summary>Export only the revalidated historical bytes already opened.
    /// Source freshness is deliberately unrelated to this historical download.</summary>
    public Task DownloadOpenedAsync(HistoricalArtifactKey expectedKey, IPdfDownloadBrowser browser)
    {
        ArgumentNullException.ThrowIfNull(browser);
        HistoricalCompileArtifact? artifact = null;
        string filename = "saved-document.pdf";
        return Start(async stamp =>
        {
            bool CurrentArtifact() => Current(stamp) && ReferenceEquals(_view.Opened, artifact);
            bool committed = false;
            try
            {
                await using var prepared = await browser.PrepareAsync(_stop.Token);
                var bytes = artifact!.CopyPdf();
                lock (_gate)
                {
                    // No await/queued browser call after the final ownership guard.
                    if (!CurrentArtifact()) return;
                    prepared.Download(filename, bytes);
                    committed = true;
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // A disposal failure after the synchronous commit cannot undo it.
            }
            lock (_gate)
                if (CurrentArtifact()) _view = _view with { Notice = committed
                    ? "Saved PDF download started." : "Saved PDF download could not be confirmed. Try again." };
            Notify();
        }, stamp =>
        {
            if (_view.Opened is not { } opened || opened.Key != expectedKey || opened.Key.ProjectId != stamp.Project) return false;
            artifact = opened;
            filename = PdfDownloadFilename.Create(_workspace.Project?.Name + " saved " + opened.Key.RecordId.ToString("N")[..12]);
            _view = _view with { Notice = "Preparing saved PDF download…" };
            return true;
        });
    }
    private static string AssociationMessage(RevisionFileAssociation result) => result.Status switch
    {
        RevisionFileAssociationStatus.VerifiedFileManifest => "Captured files exactly match the compared saved revision's file manifest. Only the saved file manifest was verified; working text and compile settings were not compared.",
        RevisionFileAssociationStatus.Mismatch => "Captured files differ from the compared saved revision's file manifest. This historical PDF is not a verified match for that saved source.",
        RevisionFileAssociationStatus.MissingOrCorrupt => "The compared saved revision manifest is missing or corrupt. No source-file association was verified.",
        RevisionFileAssociationStatus.Unavailable => "Saved source-file comparison is unavailable. Retry explicitly; no association was verified.",
        RevisionFileAssociationStatus.Unassociated => "There is no current saved source revision to compare. The historical PDF remains unassociated.",
        RevisionFileAssociationStatus.WrongProject => "The returned saved revision does not belong to the requested project/revision. No source-file association was verified.",
        _ => "Source-file comparison is unavailable; no association was verified."
    };
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true; _comparisonAction = Guid.Empty; _sourceEpoch = Guid.NewGuid();
            _workspace.WorkspaceChanged -= WorkspaceChanged; _workspace.RevisionCommitted -= RevisionCommitted; _compile.Changed -= CompileChanged; _preview.Changed -= PreviewChanged;
            _view = new("Saved-PDF session closed.", []); Changed = null;
        }
        _stop.Cancel(); _ = _preview.DisposeAsync(); _ = _store.DisposeAsync(); _ = _index.DisposeAsync();
        // No borrowed workspace/compile/inspector disposal or foreign-task join.
        return ValueTask.CompletedTask;
    }
    private static void Observe(Task task) => _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
