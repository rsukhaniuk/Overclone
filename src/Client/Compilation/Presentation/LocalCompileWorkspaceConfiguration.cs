using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Client.Pdf;
using Client.RuntimeAssets;
using Microsoft.JSInterop;

namespace Client.Compilation.Presentation;

/// <summary>App-lifetime local-only composition. Trust is fixed in the application;
/// readiness and actual execution independently reverify the current installation.</summary>
public sealed class LocalCompileWorkspaceConfiguration : ICompileWorkspaceConfiguration, IAsyncDisposable
{
    public static TimeSpan JobDeadline => TimeSpan.FromSeconds(25);
    public bool UsesBundledResources => _resources.UsesBundledResources;
    private readonly LocalInstalledRuntimeProviders _resources;
    private readonly NativeBrowserModuleLoader _loader;
    private readonly IPdfInspector _inspector;
    // Same borrowed renderer, available independently of compiler readiness.
    public IPdfInspector PdfInspector => _inspector;
    private readonly NativeForeignOperations _checks = new(1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private NativeBrowserModuleLease? _module;
    private NativeCompilationWorkerFactory? _factory, _dviFactory, _xeFactory, _luaFactory;
    private CompileWorkspacePorts? _ports;
    private CompileWorkspacePorts[]? _additionalEngines;
    private NativeRuntimeProfile? _configuredProfile;
    private volatile bool _disposed, _moduleClosed;
    private Task? _closing;
    public LocalCompileWorkspaceConfiguration(IJSRuntime js) : this(js, new LocalInstalledRuntimeProviders(js)) { }
    public LocalCompileWorkspaceConfiguration(IJSRuntime js, LocalInstalledRuntimeProviders resources)
    { _resources = resources; _loader = new(js); _inspector = new LocalPdfInspector(js, resources); }
    public Task WhenSettled { get { lock (_gate) return _closing ?? Task.WhenAll(_checks.WhenIdle, _resources.WhenSettled); } }
    public Task<CompileWorkspaceConfigurationResult> OpenAsync() => OpenAsync(CancellationToken.None);
    public async Task<CompileReadiness> CheckAsync(CancellationToken token = default) => (await OpenAsync(token).ConfigureAwait(false)).Compiler;
    public async Task<CompileWorkspaceConfigurationResult> OpenAsync(CancellationToken caller)
    {
        // Initial static-asset download has its own bounded lifetime. It must not
        // consume the short per-operation readiness budget or require user setup.
        if (UsesBundledResources)
        {
            var preparation = await _resources.ReadCompilerAsync(caller).ConfigureAwait(false);
            if (preparation.Package is null) return Result(Failure(preparation.Store.Status));
        }
        CancellationTokenSource deadline, linked;
        lock (_gate)
        {
            if (_disposed || _moduleClosed) return Result(Failure(RuntimeAssetStatus.Disposed));
            deadline = new(TimeSpan.FromSeconds(30)); linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _shutdown.Token, deadline.Token);
        }
        bool started = false; var token = linked.Token;
        try { return await _checks.RunAsync(() => { started = true; return Actual(); }, token).ConfigureAwait(false); }
        catch (NativeResourceLimitException) { return Result(Failure(RuntimeAssetStatus.Busy)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return Result(Failure(_disposed ? RuntimeAssetStatus.Disposed : caller.IsCancellationRequested ? RuntimeAssetStatus.Cancelled : RuntimeAssetStatus.TimedOut)); }
        catch (JSException error) when (error.Message.Contains("disposed", StringComparison.OrdinalIgnoreCase))
        { _moduleClosed = true; return Result(Failure(RuntimeAssetStatus.Disposed)); }
        catch (ObjectDisposedException)
        { _moduleClosed = true; return Result(Failure(RuntimeAssetStatus.Disposed)); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return Result(new(false, "Unavailable", "Local tools could not be initialized. Check tools to retry; editing and saving remain available.")); }
        finally { if (!started) { linked.Dispose(); deadline.Dispose(); } }
        async Task<CompileWorkspaceConfigurationResult> Actual()
        {
            try
            {
                var read = await _resources.ReadCompilerAsync(token).ConfigureAwait(false);
                // Provider may have ended a public wait while actual foreign storage still owns it.
                // Retain this readiness admission too, but never use a timed-out result as success.
                if (read.Package is null) { await _resources.CompilerWhenSettled.ConfigureAwait(false); return Result(Failure(read.Store.Status)); }
                token.ThrowIfCancellationRequested();
                // Import with an uncancellable ACTUAL task. A cancelled readiness wait never
                // disposes/reimports a cached global module. Late success is retained until final app disposal.
                _module ??= await _loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                bool provisioned = await _module.ProvisionAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                bool suite = NativeRuntimeProfiles.IsSuite(read.Package.Profile);
                if (provisioned && (suite || read.Package.Profile == NativeRuntimeProfile.LocalLatex2023Dvi))
                {
                    provisioned = await _module.ProvisionDviAsync().ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
                if (provisioned && suite)
                {
                    provisioned = await _module.ProvisionXetexAsync().ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (provisioned) provisioned = await _module.ProvisionLuatexAsync().ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
                if (provisioned && read.Package.HasBiber)
                {
                    provisioned = await _module.ProvisionBiberAsync().ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
                if (!provisioned) return Result(new(false, "ControllerUpdate", "Local worker entry is not available under the current offline controller. Check the application update status, then Check tools to retry."));
                var current = await _resources.ConfirmCompilerSelectionAsync(read, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (current != RuntimeAssetStatus.Ready) return Result(Failure(current));
                if (_configuredProfile is { } configured && configured != read.Package.Profile)
                    return Result(new(false, "RuntimeChanged", "Compiler resource profile changed. Reopen the application to activate it; local documents are preserved."));
                if (_ports is null)
                {
                    bool mapped = suite || read.Package.Profile is NativeRuntimeProfile.LocalLatex2023SyncTeX or NativeRuntimeProfile.LocalLatex2023Dvi;
                    _configuredProfile = read.Package.Profile;
                    _factory = new(_resources, _module.Transport, suite ? NativeCompilerSuiteContract.CreatePlan(NativeCompilerEngine.PdfLaTeX, profile: read.Package.Profile) : mapped ? NativeSyncTeXContract.CreatePlan(deadline: JobDeadline) with { Profile = read.Package.Profile }
                        : NativeSelectedEntryContract.CreatePlan(deadline: JobDeadline));
                    // Fixed technical closures only; package integrity is not adoption approval.
                    var package = read.Package;
                    CompilationDescriptor[] tools = package.HasBiber ? [package.Bibtex, package.Biber] : [package.Bibtex];
                    _ports = new("local-authoring-v4", "Local pdfLaTeX authoring", package.Engine, [package.Bundle], tools,
                        _factory, _inspector, () => CheckAsync(), this, SelectableBibliography: true,
                        SelectionOptions: suite ? NativeCompilerSuiteContract.SelectionOptions(package.Profile, NativeCompilerEngine.PdfLaTeX)
                            : mapped ? NativeSyncTeXContract.SelectionOptions : NativeSelectedEntryContract.SelectionOptions,
                        OutputAdapter: package.OutputAdapter, NativeProfile: package.Profile,
                        CompiledPdfCapacity: _resources.HasSelectedPdfCapacity && NativeSelectedPdfContract.MatchesRuntime(package.Profile, package.Identity.Hex)
                            ? PdfCompiledInputCapacity.TwelveMiB : null);
                    if (suite || package.Profile == NativeRuntimeProfile.LocalLatex2023Dvi)
                    {
                        _dviFactory = new(_resources, _module.Transport, suite ? NativeCompilerSuiteContract.CreatePlan(NativeCompilerEngine.LaTeXDvi, profile: package.Profile)
                            : NativeLatexDviContract.CreatePlan(deadline: JobDeadline));
                        _additionalEngines = [new("latex-dvi", "LaTeX (DVI to PDF)", package.DviEngine, [package.Bundle], tools,
                            _dviFactory, _inspector, () => CheckAsync(), this, SelectableBibliography: true,
                            SelectionOptions: suite ? NativeCompilerSuiteContract.SelectionOptions(package.Profile, NativeCompilerEngine.LaTeXDvi) : NativeLatexDviContract.SelectionOptions, OutputAdapter: package.DviOutputAdapter, NativeProfile: package.Profile)];
                        if (suite)
                        {
                            _xeFactory = new(_resources, _module.Transport, NativeCompilerSuiteContract.CreatePlan(NativeCompilerEngine.XeLaTeX, profile: package.Profile));
                            _luaFactory = new(_resources, _module.Transport, NativeCompilerSuiteContract.CreatePlan(NativeCompilerEngine.LuaLaTeX, profile: package.Profile));
                            _additionalEngines = [.. _additionalEngines,
                                new("xelatex", "XeLaTeX (XDV to PDF)", package.EngineFor(NativeCompilerEngine.XeLaTeX), [package.Bundle], tools,
                                    _xeFactory, _inspector, () => CheckAsync(), this, SelectableBibliography: true,
                                    SelectionOptions: NativeCompilerSuiteContract.SelectionOptions(package.Profile, NativeCompilerEngine.XeLaTeX), OutputAdapter: package.AdapterFor(NativeCompilerEngine.XeLaTeX), NativeProfile: package.Profile),
                                new("lualatex", "LuaLaTeX", package.EngineFor(NativeCompilerEngine.LuaLaTeX), [package.Bundle], tools,
                                    _luaFactory, _inspector, () => CheckAsync(), this, SelectableBibliography: true,
                                    SelectionOptions: NativeCompilerSuiteContract.SelectionOptions(package.Profile, NativeCompilerEngine.LuaLaTeX), OutputAdapter: package.AdapterFor(NativeCompilerEngine.LuaLaTeX), NativeProfile: package.Profile)];
                        }
                    }
                }
                return Result(new(true, "Ready", "Local pdfLaTeX resources verified. Choose a supported .tex root (including nested paths) and the local engine; BibTeX is optional. PDF preview separately verifies its supported local renderer when used."));
            }
            catch (JSException error) when (error.Message.Contains("disposed", StringComparison.OrdinalIgnoreCase))
            { _moduleClosed = true; throw; }
            catch (ObjectDisposedException) { _moduleClosed = true; throw; }
            finally
            {
                // A failed/non-sync import may still be retiring its actual JS reference.
                // Never release readiness admission while that retirement owns the loader.
                if (_module is null) await _loader.WhenIdle.ConfigureAwait(false);
                linked.Dispose(); deadline.Dispose();
            }
        }
    }
    private CompileWorkspaceConfigurationResult Result(CompileReadiness readiness) => new(readiness, _ports, _additionalEngines);
    private CompileReadiness Failure(RuntimeAssetStatus status) => new(false, status.ToString(), UsesBundledResources ? status switch
    {
        RuntimeAssetStatus.Cancelled => "Bundled compiler preparation was cancelled. Check tools to retry.",
        RuntimeAssetStatus.Disposed => "Bundled tools have closed. Reopen the application to use them.",
        RuntimeAssetStatus.TimedOut => "The bundled compiler download timed out. Check your connection and retry Check tools.",
        RuntimeAssetStatus.Corrupt => "Bundled compiler files failed verification. Reconnect and reload the app to recover its complete release.",
        _ => "Bundled compiler files could not be loaded. Reconnect and reload the app; editing and saving remain available."
    } : status switch
    {
        RuntimeAssetStatus.Missing => "Local compiler resources are not installed. Install the supported local package, then Check tools. Editing, saving and source analysis remain available.",
        RuntimeAssetStatus.Incomplete => "Local compiler resources are incomplete. Recheck or reinstall your local package, then Check tools.",
        RuntimeAssetStatus.Corrupt or RuntimeAssetStatus.InvalidInput => "Local compiler resources or their selection failed verification. Recheck or reinstall the supported package; no other engine is substituted.",
        RuntimeAssetStatus.QuotaExceeded => "Browser storage quota prevents compiler verification. Check available storage, then retry explicitly.",
        RuntimeAssetStatus.PermissionDenied => "Browser storage permission prevents compiler verification. Check site permissions, then retry explicitly.",
        RuntimeAssetStatus.Busy => "Local tool verification or storage is still busy/finishing. Check tools again after it settles; no checks are queued.",
        RuntimeAssetStatus.Cancelled => "Local tool verification was cancelled. Actual unfinished work retains admission; retry when idle.",
        RuntimeAssetStatus.TimedOut => "Local tool verification timed out. Actual unfinished work retains admission; retry explicitly when idle.",
        RuntimeAssetStatus.Uncertain => "Local resource selection changed during verification or completion is uncertain. Check tools again; no earlier package is used as current readiness.",
        RuntimeAssetStatus.Disposed => "Local tools have closed for this application session. Reopen the application to use them.",
        _ => "Local tool storage is unavailable. Check browser OPFS/Web Locks support, site storage and update status, then retry explicitly."
    });
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true; _shutdown.Cancel();
            _closing = CloseAsync(); LocalInstalledRuntimeProviders.Observe(_closing); return ValueTask.CompletedTask;
        }
    }
    private async Task CloseAsync()
    {
        try
        {
            await _resources.DisposeAsync().ConfigureAwait(false);
            try { await _inspector.DisposeAsync().ConfigureAwait(false); } catch (Exception error) when (error is not OutOfMemoryException) { }
            await _checks.WhenIdle.ConfigureAwait(false);
            if (_factory is not null) await _factory.Operations.WhenIdle.ConfigureAwait(false);
            if (_dviFactory is not null) await _dviFactory.Operations.WhenIdle.ConfigureAwait(false);
            if (_xeFactory is not null) await _xeFactory.Operations.WhenIdle.ConfigureAwait(false);
            if (_luaFactory is not null) await _luaFactory.Operations.WhenIdle.ConfigureAwait(false);
            await _resources.WhenSettled.ConfigureAwait(false);
        }
        finally
        {
            try { if (_module is not null) await _module.DisposeAsync().ConfigureAwait(false); }
            finally { _shutdown.Dispose(); }
        }
    }
}
