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
    private readonly LocalInstalledRuntimeProviders _resources;
    private readonly NativeBrowserModuleLoader _loader;
    private readonly IPdfInspector _inspector;
    private readonly NativeForeignOperations _checks = new(1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private NativeBrowserModuleLease? _module;
    private NativeCompilationWorkerFactory? _factory, _dviFactory;
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
                if (provisioned && read.Package.Profile == NativeRuntimeProfile.LocalLatex2023Dvi)
                {
                    provisioned = await _module.ProvisionDviAsync().ConfigureAwait(false);
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
                    bool mapped = read.Package.Profile is NativeRuntimeProfile.LocalLatex2023SyncTeX or NativeRuntimeProfile.LocalLatex2023Dvi;
                    _configuredProfile = read.Package.Profile;
                    _factory = new(_resources, _module.Transport, mapped ? NativeSyncTeXContract.CreatePlan(deadline: JobDeadline) with { Profile = read.Package.Profile }
                        : NativeSelectedEntryContract.CreatePlan(deadline: JobDeadline));
                    // Fixed technical closures only; package integrity is not adoption approval.
                    var package = read.Package;
                    _ports = new("local-authoring-v4", "Local pdfLaTeX authoring", package.Engine, [package.Bundle], [package.Bibtex],
                        _factory, _inspector, () => CheckAsync(), this, SelectableBibliography: true,
                        SelectionOptions: mapped ? NativeSyncTeXContract.SelectionOptions : NativeSelectedEntryContract.SelectionOptions,
                        OutputAdapter: package.OutputAdapter);
                    if (package.Profile == NativeRuntimeProfile.LocalLatex2023Dvi)
                    {
                        _dviFactory = new(_resources, _module.Transport, NativeLatexDviContract.CreatePlan(deadline: JobDeadline));
                        _additionalEngines = [new("latex-dvi", "LaTeX (DVI to PDF)", package.DviEngine, [package.Bundle], [package.Bibtex],
                            _dviFactory, _inspector, () => CheckAsync(), this, SelectableBibliography: true,
                            SelectionOptions: NativeLatexDviContract.SelectionOptions, OutputAdapter: package.DviOutputAdapter)];
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
    private static CompileReadiness Failure(RuntimeAssetStatus status) => new(false, status.ToString(), status switch
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
            await _resources.WhenSettled.ConfigureAwait(false);
        }
        finally
        {
            try { if (_module is not null) await _module.DisposeAsync().ConfigureAwait(false); }
            finally { _shutdown.Dispose(); }
        }
    }
}
