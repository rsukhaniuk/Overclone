using Client.Compilation.Diagnostics;
using Client.Compilation.Lifecycle;
using Client.Compilation.Native;
using Client.Pdf;
using Client.Pdf.Preview;
using Client.Workspace;
using Client.Workspace.Domain;

namespace Client.Compilation.Presentation;

/// <summary>App-scoped composition, not another lifecycle. Borrow workspace/config;
/// own selection/B3/preview/designated ports. Pages only attach/unsubscribe. Actual
/// unfinished operations retain the original B3/adapter admission across remounts.</summary>
public sealed class CompileWorkspaceSession(WorkspaceCoordinator workspace, ICompileWorkspaceConfiguration configuration, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private CompileOwnerContext? _owner;
    private CompilationLifecycleService? _lifecycle;
    private readonly CompilationSelectionState _selection = new();
    private CompileWorkspacePorts? _ports;
    private Task? _configurationTask;
    private Guid _session;
    private bool _disposed, _refreshing, _previousCurrent;
    private AcceptedCompileOutput? _shown;
    private ValidatedCompileAttempt? _presented;
    public PdfPreviewController Preview { get; private set; } = new();
    public CompileReadiness Readiness { get; private set; } = new(false, "Unavailable", "No compiler configured.");
    public FileId? MainFile { get; private set; }
    public string EngineChoice { get; private set; } = "";
    public bool UseBibtex { get; private set; }
    public bool CanSelectBibtex => _ports is { SelectableBibliography: true } && AvailableBibtex is not null;
    private CompilationDescriptor? AvailableBibtex
    {
        get
        {
            var matches = _ports?.Tools.Where(tool => string.Equals(tool.Id, "bibtex", StringComparison.Ordinal)).Take(2).ToArray();
            return matches is { Length: 1 } ? matches[0] : null;
        }
    }
    public string Notice { get; private set; } = "Choose a main file and supported engine. Settings are session-only.";
    public CompileLifecycleReason? LastReason { get; private set; }
    public CompileAttemptPresentation? Attempt { get; private set; }
    public CompileLifecycleTicket? LastTicket { get; private set; }
    public CompileWorkspacePorts? ConfiguredPorts => _ports;
    public AcceptedCompileOutput? DisplayedOutput => _shown;
    public bool ReadingTools => _configurationTask is { IsCompleted: false };
    public bool Disposed => _disposed;
    public long SelectionVersion => _selection.Current.Version;
    public event Action? Changed;
    public CompileLifecycleView? State => _disposed || _owner is null ? null : _owner.Run(() => _lifecycle!.State);
    public CompileOwnerContext? Owner => _owner;
    public string PdfLabel => State is { LastSuccessful: not null } s && s.LastSuccessful.SnapshotIdentity.ProjectId == workspace.Project?.Id
        ? s.IsCurrent ? "PDF up to date" : "Last successful PDF — out of date" : "No compiled PDF";
    private static CompileLifecycleOptions Options => new(new(64, 1048576, 8388608, 131072),
        new(8388608, 2097152, 64, 128, 4096, 1048576), 65536, TimeSpan.FromSeconds(30), NativeRuntimePackage.Pdf, NativeRuntimePackage.Adapter);
    private static CompileAttemptPresentationBudget LogBudget => new(1048576, 2097152,
        new(64, 8388608, 131072, 1048576, 8192, 10000, 128, 4096, 32, 200000, 8000000, 2097152));
    public void AttachRenderer(Func<Action, Task> renderer)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CompileWorkspaceSession));
        if (_owner is not null) return; // same app owner/ports survive page remount
        _owner = new(renderer);
        _owner.Run(() =>
        {
            _lifecycle = new(workspace, _selection, new SelectedFactory(this), new(3, long.MaxValue), timeProvider ?? TimeProvider.System, _owner.ForwardWorkspaceEvent);
            _lifecycle.Changed += Refresh;
            _session = workspace.SessionId; workspace.WorkspaceChanged += WorkspaceChanged;
            workspace.RevisionCommitted += RevisionCommitted;
        });
    }
    public Task RefreshToolsAsync()
    {
        if (_disposed || _owner is null || ReadingTools) return Task.CompletedTask;
        _configurationTask = _owner.Run(ReadTools);
        _owner.ObserveOwnedOperation(_configurationTask, CompilationOwnedOperationKind.Workflow); return _configurationTask;
    }
    private async Task ReadTools()
    {
        try
        {
            if (_ports is null)
            {
                var result = await configuration.OpenAsync();
                if (_disposed) { if (result.Ports?.Lifetime is { } late) _ = late.DisposeAsync(); return; }
                Readiness = result.Compiler;
                if (result.Ports is { } ports)
                {
                    _ports = ports; await Preview.DisposeAsync(); Preview = new(ports.Inspector); Preview.Changed += PreviewChanged;
                }
            }
            else Readiness = await _ports.Readiness();
        }
        catch (OperationCanceledException) { if (!_disposed) Readiness = new(false, "Unavailable", "Tool readiness was cancelled; retry explicitly."); }
        catch (Exception error) when (error is not OutOfMemoryException) { if (!_disposed) Readiness = new(false, "Unavailable", "Tools could not be checked. Retry explicitly."); }
        if (!_disposed) { Notice = Readiness.Message; Refresh(); }
    }
    public void SelectMain(FileId? id) => Run(() => { MainFile = id; ApplySelection(); });
    public void SelectEngine(string id) => Run(() => { EngineChoice = id; ApplySelection(); });
    public void SelectBibliography(bool enabled) => Run(() =>
    {
        if (_ports is not { SelectableBibliography: true }) return; // legacy declared tools remain unchanged
        if (enabled && !CanSelectBibtex)
        { Notice = "BibTeX is unavailable for this configured adapter."; Refresh(); return; }
        UseBibtex = enabled; ApplySelection();
    });
    private void ApplySelection()
    {
        CompilationSelection? selection = null;
        var main = workspace.Files.FirstOrDefault(f => !f.Deleted && f.MediaKind == "text" && f.Id == MainFile);
        if (_ports is { } ports && Readiness.Ready && EngineChoice == ports.CapabilityId && main?.RelativePath == "main.tex"
            && (!ports.SelectableBibliography || !UseBibtex || AvailableBibtex is not null))
        {
            CompilationDescriptor[] tools = ports.SelectableBibliography
                ? UseBibtex ? [AvailableBibtex!] : []
                : ports.Tools;
            selection = CompilationSelection.Create(new(main.Id, ports.Engine, ports.Bundles, tools, []), 65536).Selection;
        }
        _selection.SetSelection(selection); _lifecycle!.Cancel();
        Notice = main is not null && main.RelativePath != "main.tex" ? "This adapter requires main.tex; no files are renamed." : selection is null ? "Choose main.tex and an available engine. Unsupported engines are not substituted." : "Ready to compile unsaved files. Settings are session-only.";
        Refresh();
    }
    public Task CompileAsync()
    {
        if (_disposed || _owner is null || ReadingTools) return Task.CompletedTask;
        return _owner.Run(CompileCore);
    }
    private async Task CompileCore()
    {
        var stamp = _selection.Current; var session = workspace.SessionId; var generation = workspace.WorkingGeneration;
        await RefreshToolsAsync();
        if (_disposed) return;
        if (session != workspace.SessionId || generation != workspace.WorkingGeneration || stamp.Version != _selection.Current.Version)
        { Notice = "Source or selection changed while checking tools. Compile again."; Refresh(); return; }
        if (!Readiness.Ready || _ports is null || _selection.Current.Selection is null)
        { Notice = Readiness.Ready ? "Select a supported main file and engine." : Readiness.Message; Refresh(); return; }
        if (_owner!.PendingOwned >= 16) { Notice = "Compile work is still finishing. Try again when idle."; Refresh(); return; }
        var ticket = _lifecycle!.Request(Options); LastTicket = ticket; LastReason = null; Notice = "Compiling unsaved project files…"; Refresh();
        _owner.ObserveOwnedOperation(ObserveCompletion(ticket), CompilationOwnedOperationKind.Workflow);
    }
    private async Task ObserveCompletion(CompileLifecycleTicket ticket)
    {
        var result = await ticket.Completion;
        if (_disposed) return;
        if (ReferenceEquals(LastTicket, ticket)) { LastReason = result.Reason; Notice = result.Reason == CompileLifecycleReason.Succeeded ? "Compilation finished." : "No new PDF: " + result.Reason + "."; }
        Refresh();
    }
    public void Cancel() => Run(() => { _lifecycle!.Cancel(); Notice = "Compilation cancelled; unfinished work retains admission."; Refresh(); });
    private void WorkspaceChanged() => _owner?.ForwardWorkspaceEvent(() =>
    {
        if (_disposed) return;
        if (_session != workspace.SessionId)
        {
            _session = workspace.SessionId; MainFile = null; EngineChoice = ""; UseBibtex = false; _selection.SetSelection(null);
            Preview.Changed -= PreviewChanged; _ = Preview.DisposeAsync(); Preview = new(_ports?.Inspector); Preview.Changed += PreviewChanged;
            _shown = null; _previousCurrent = false; _presented = null; Attempt = null; LastTicket = null; LastReason = null;
        }
        Refresh();
    });
    private void RevisionCommitted(WorkspaceSaveReceipt receipt) => _owner?.ForwardWorkspaceEvent(() =>
    {
        // Save/review/autosave can advance the durable head without a source
        // generation or WorkspaceChanged event. Re-read B3 on its owner; never
        // let a late old-session/project commit repaint the current workspace.
        if (_disposed || receipt.SessionId != _session || receipt.SessionId != workspace.SessionId
            || receipt.Revision.ProjectId != workspace.Project?.Id) return;
        Refresh();
    });
    private void Refresh()
    {
        if (_disposed || _refreshing || _lifecycle is null) return;
        _refreshing = true;
        try
        {
            var state = _lifecycle.State;
            var output = state.LastSuccessful?.SnapshotIdentity.ProjectId == workspace.Project?.Id ? state.LastSuccessful : null;
            if (output is not null && (!ReferenceEquals(output, _shown) || _previousCurrent && !state.IsCurrent))
            {
                _shown = output; _previousCurrent = state.IsCurrent;
                Preview.Cancel(); Preview.Provide(new PdfDocument(output.ToPayloadArray()!), "Compiled PDF", state.IsCurrent ? PdfDisplayProvenance.ProvidedPdf : PdfDisplayProvenance.CallerSaysHistorical);
            }
            _previousCurrent = output is not null && state.IsCurrent;
            if (state.LastAttempt is { } attempt && attempt.Snapshot.Identity.ProjectId == workspace.Project?.Id && !ReferenceEquals(attempt, _presented))
            { _presented = attempt; Attempt = CompileAttemptPresenter.Present(attempt, LogBudget); }
        }
        finally { _refreshing = false; }
        Changed?.Invoke();
    }
    private void PreviewChanged() { if (!_disposed) Changed?.Invoke(); }
    private void Run(Action action) { if (!_disposed && _owner is not null) _owner.Run(action); }
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        if (_owner is null) { _disposed = true; Changed = null; return ValueTask.CompletedTask; }
        Run(() => { _disposed = true; workspace.WorkspaceChanged -= WorkspaceChanged; workspace.RevisionCommitted -= RevisionCommitted; _lifecycle!.Changed -= Refresh; _ = _lifecycle.DisposeAsync(); Preview.Changed -= PreviewChanged; _ = Preview.DisposeAsync(); Changed = null; });
        if (_ports?.Lifetime is { } lifetime) _ = lifetime.DisposeAsync();
        _owner?.RequestClose(); return ValueTask.CompletedTask;
    }
    private sealed class SelectedFactory(CompileWorkspaceSession session) : ICompilationWorkerFactory
    { public ICompilationWorker? Create(CompileJobId id, long epoch) => session._ports?.Factory.Create(id, epoch); }
}
