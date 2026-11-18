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
    private IReadOnlyList<CompileWorkspacePorts> _engines = Array.Empty<CompileWorkspacePorts>();
    private Task? _configurationTask;
    private Guid _session;
    private bool _disposed, _refreshing, _previousCurrent;
    private AcceptedCompileOutput? _shown;
    private ValidatedCompileAttempt? _presented;
    public PdfPreviewController Preview { get; private set; } = new();
    public CompileReadiness Readiness { get; private set; } = new(false, "Unavailable", "No compiler configured.");
    public FileId? MainFile { get; private set; }
    public string EngineChoice { get; private set; } = "";
    public BibliographyToolKind BibliographyTool { get; private set; }
    public bool UseBibtex => BibliographyTool == BibliographyToolKind.Bibtex;
    public bool CanSelectBibtex => _ports is { SelectableBibliography: true } && AvailableBibtex is not null;
    public bool CanSelectBiber => _ports is { SelectableBibliography: true } && AvailableTool(BibliographyToolKind.Biber) is not null;
    private CompilationDescriptor? AvailableBibtex => AvailableTool(BibliographyToolKind.Bibtex);
    private CompilationDescriptor? AvailableTool(BibliographyToolKind kind)
    {
        var id = kind switch { BibliographyToolKind.Bibtex => "bibtex", BibliographyToolKind.Biber => "biber", _ => null };
        if (id is null) return null;
        var matches = _ports?.Tools.Where(tool => string.Equals(tool.Id, id, StringComparison.Ordinal)).Take(2).ToArray();
        return matches is { Length: 1 } ? matches[0] : null;
    }
    public string Notice { get; private set; } = "Choose a main file and supported engine. Settings are session-only.";
    public CompileLifecycleReason? LastReason { get; private set; }
    public CompileAttemptPresentation? Attempt { get; private set; }
    public CompileLifecycleTicket? LastTicket { get; private set; }
    public CompileWorkspacePorts? ConfiguredPorts => _ports;
    public IReadOnlyList<CompileWorkspacePorts> AvailableEngines => _engines;
    public AcceptedCompileOutput? DisplayedOutput => _shown;
    public bool ReadingTools => _configurationTask is { IsCompleted: false };
    public bool UsesBundledResources => configuration is LocalCompileWorkspaceConfiguration { UsesBundledResources: true };
    public bool Disposed => _disposed;
    public long SelectionVersion => _selection.Current.Version;
    public event Action? Changed;
    public CompileLifecycleView? State => _disposed || _owner is null ? null : _owner.Run(() => _lifecycle!.State);
    public CompileOwnerContext? Owner => _owner;
    public string PdfLabel => State is { LastSuccessful: not null } s && s.LastSuccessful.SnapshotIdentity.ProjectId == workspace.Project?.Id
        ? s.IsCurrent ? "PDF up to date" : "Last successful PDF — out of date" : "No compiled PDF";
    // Configured port options only. Actual runtime/engine admission stays in the factory.
    private static bool SuiteOptions(IEnumerable<CompilationOption> options) => NativeCompilerSuiteContract.MatchesOptions(options)
        || NativeCompilerSuiteContract.MatchesOptions(options, NativeNavigationContract.Profile)
        || NativeCompilerSuiteContract.MatchesOptions(options, NativeXeNavigationContract.Profile)
        || NativeCompilerSuiteContract.MatchesOptions(options, NativeGraphicsNavigationContract.Profile)
        || NativeCompilerSuiteContract.MatchesOptions(options, NativeUnicodeCompilerContract.Profile)
        || NativeCompilerSuiteContract.MatchesOptions(options, NativeBiberCompilerContract.Profile);
    private int ProjectFileLimit => NativeRuntimeProfiles.ProjectFileLimit(_ports?.NativeProfile ?? NativeRuntimeProfile.Legacy);
    private int ProjectFileBytes => NativeRuntimeProfiles.ProjectFileBytes(_ports?.NativeProfile ?? NativeRuntimeProfile.Legacy);
    private int ProjectTotalBytes => NativeRuntimeProfiles.ProjectTotalBytes(_ports?.NativeProfile ?? NativeRuntimeProfile.Legacy);
    private CompileLifecycleOptions Options => new(new(ProjectFileLimit, ProjectFileBytes, ProjectTotalBytes, 131072),
        new(8388608, 2097152, 64, 128, 4096, 1048576), 65536,
        _ports?.SelectionOptions is { } options && SuiteOptions(options)
            ? NativeCompilerSuiteContract.LifecycleDeadline : TimeSpan.FromSeconds(30), NativeRuntimePackage.Pdf,
        _ports?.OutputAdapter ?? NativeRuntimePackage.Adapter);
    private CompileAttemptPresentationBudget LogBudget => new(1048576, 2097152,
        new(NativeThesisCompilerContract.ProjectFiles, ProjectTotalBytes, 131072, 1048576, 8192, 10000, 128, 4096, 32, 200000, 8000000, 2097152));
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
                    var engines = new[] { ports }.Concat(result.AdditionalEngines ?? []).Take(5).ToArray();
                    if (engines.Length > 4 || engines.Any(p => p is null || string.IsNullOrEmpty(p.CapabilityId)
                            || !ReferenceEquals(p.Inspector, ports.Inspector) || !ReferenceEquals(p.Lifetime, ports.Lifetime))
                        || engines.Select(p => p.CapabilityId).Distinct(StringComparer.Ordinal).Count() != engines.Length)
                        throw new InvalidOperationException("Invalid configured compiler choices.");
                    _engines = Array.AsReadOnly(engines);
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
    public void SelectEngine(string id) => Run(() =>
    {
        EngineChoice = id;
        if (_engines.FirstOrDefault(p => p.CapabilityId == id) is { } selected)
        {
            _ports = selected;
        }
        ApplySelection();
    });
    public void SelectBibliography(bool enabled) => SelectBibliography(enabled ? BibliographyToolKind.Bibtex : BibliographyToolKind.None);
    public void SelectBibliography(BibliographyToolKind kind) => Run(() =>
    {
        if (_ports is not { SelectableBibliography: true }) return; // legacy declared tools remain unchanged
        if (kind is not (BibliographyToolKind.None or BibliographyToolKind.Bibtex or BibliographyToolKind.Biber)
            || kind != BibliographyToolKind.None && AvailableTool(kind) is null)
        { Notice = "The selected bibliography tool is unavailable for this configured adapter."; Refresh(); return; }
        BibliographyTool = kind; ApplySelection();
    });
    private void ApplySelection()
    {
        CompilationSelection? selection = null;
        var main = workspace.Files.FirstOrDefault(f => !f.Deleted && f.MediaKind == "text" && f.Id == MainFile);
        bool selectedEntry = _ports?.SelectionOptions is { } options
            && (NativeSelectedEntryContract.MatchesOptions(options) || NativeSyncTeXContract.MatchesOptions(options)
                || NativeLatexDviContract.MatchesOptions(options) || SuiteOptions(options));
        bool unicodeXe = _ports is { Engine: { } configuredEngine, SelectionOptions: { } configuredOptions }
            && (NativeUnicodeCompilerContract.MatchesXeSelection(configuredEngine, configuredOptions)
                || configuredEngine == new CompilationDescriptor("xelatex", NativeBiberCompilerContract.Component(NativeCompilerEngine.XeLaTeX).EngineVersion,
                    new(NativeCompilerUnicodePins.XeWasmHash))
                    && NativeCompilerBiberPins.Ready
                    && NativeCompilerSuiteContract.MatchesOptions(configuredOptions, NativeBiberCompilerContract.Profile, NativeCompilerEngine.XeLaTeX)
                || _ports.NativeProfile == NativeThesisCompilerContract.Profile && NativeCompilerThesisPins.Ready
                    && configuredEngine == new CompilationDescriptor("xelatex", NativeThesisCompilerContract.Component(NativeCompilerEngine.XeLaTeX).EngineVersion, new(NativeCompilerThesisPins.XeWasmHash))
                    && NativeCompilerSuiteContract.MatchesOptions(configuredOptions, NativeThesisCompilerContract.Profile, NativeCompilerEngine.XeLaTeX)
                || _ports.NativeProfile == NativeOriginCompilerContract.Profile && NativeCompilerOriginPins.Ready
                    && configuredEngine == new CompilationDescriptor("xelatex", NativeOriginCompilerContract.Component(NativeCompilerEngine.XeLaTeX).EngineVersion, new(NativeCompilerThesisPins.XeWasmHash))
                    && NativeCompilerSuiteContract.MatchesOptions(configuredOptions, NativeOriginCompilerContract.Profile, NativeCompilerEngine.XeLaTeX)
                || _ports.NativeProfile == NativeInputBudgetContract.Profile && NativeCompilerInputBudgetPins.Ready
                    && MatchesInputBudgetXe(configuredEngine, configuredOptions));
        var entryError = main is null ? null : selectedEntry ? unicodeXe
            ? NativeSelectedEntryContract.ValidateEntry(main.RelativePath, NativeUnicodeCompilerContract.Profile, NativeCompilerEngine.XeLaTeX)
            : NativeSelectedEntryContract.ValidateEntry(main.RelativePath)
            : main.RelativePath == "main.tex" ? null : "legacy-main-entry";
        if (_ports is { } ports && Readiness.Ready && EngineChoice == ports.CapabilityId && main is not null && entryError is null
            && (!ports.SelectableBibliography || BibliographyTool == BibliographyToolKind.None || AvailableTool(BibliographyTool) is not null))
        {
            CompilationDescriptor[] tools = ports.SelectableBibliography
                ? BibliographyTool == BibliographyToolKind.None ? [] : [AvailableTool(BibliographyTool)!]
                : ports.Tools;
            selection = CompilationSelection.Create(new(main.Id, ports.Engine, ports.Bundles, tools, ports.SelectionOptions ?? []), 65536).Selection;
        }
        _selection.SetSelection(selection); _lifecycle!.Cancel();
        Notice = entryError is not null ? selectedEntry
            ? "Unsupported selected root: use a bounded .tex path with letters, numbers, marks, dot, hyphen or underscore; internal ASCII spaces are allowed in directory and base names, but leading/trailing basename spaces and TeX command syntax are unsupported. No files are renamed."
            : "This legacy adapter requires main.tex; no files are renamed."
            : selection is null ? "Choose a supported .tex root and an available engine. Unsupported engines are not substituted."
            : "Ready to compile unsaved files from project CWD. Settings are session-only.";
        Refresh();
    }
    private static bool MatchesInputBudgetXe(CompilationDescriptor engine, IEnumerable<CompilationOption> options)
    {
        var component = NativeInputBudgetContract.Component(NativeCompilerEngine.XeLaTeX);
        return engine == new CompilationDescriptor("xelatex", component.EngineVersion, new(component.WasmHash))
            && NativeCompilerSuiteContract.MatchesOptions(options, NativeInputBudgetContract.Profile, NativeCompilerEngine.XeLaTeX);
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
        // Choices made while tools were unavailable remain explicit user
        // choices. Materialize them after readiness recovers, only after the
        // original session/generation/selection guard above has passed.
        if (Readiness.Ready && _ports is not null && _selection.Current.Selection is null)
        {
            var recoveredVersion = _selection.Current.Version + 1;
            ApplySelection();
            if (session != workspace.SessionId || generation != workspace.WorkingGeneration
                || recoveredVersion != _selection.Current.Version)
            { Notice = "Source or selection changed while checking tools. Compile again."; Refresh(); return; }
        }
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
            _session = workspace.SessionId; MainFile = null; EngineChoice = ""; BibliographyTool = BibliographyToolKind.None; _selection.SetSelection(null);
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
    private void PreviewChanged() => _owner?.ForwardWorkspaceEvent(() =>
    {
        if (!_disposed) Changed?.Invoke();
    });
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
