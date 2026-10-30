using Client.Compilation.Native;
using Client.Compilation.SyncTeX;
using Client.Pdf;
using Client.RuntimeAssets;
using Microsoft.JSInterop;

namespace Client.Compilation.Provisioning;

/// <summary>Application trust is fixed here, independently of user-declared installation integrity.
/// Manual installations are reverified on every read. Bundled application assets
/// are verified once and retained as privately owned immutable packages.</summary>
public sealed class LocalInstalledRuntimeProviders : INativeRuntimeProvider, IPdfRuntimeProvider, ISyncTeXRuntimeProvider, IAsyncDisposable
{
    public const string PdfLoaderHash = "040bdfac50656b3993f622614b73feaaba31d8253d077ff50f925f316c2f5f6b";
    public const string PdfWasmHash = "1637c1a7798cab443334e559045d88f33864d89e0ff02eeb57b3da4f08f37c20";
    private readonly RuntimeAssetStore _compilerStore, _pdfStore;
    private readonly ILocalRuntimeSelectionStore _compilerSelection;
    private readonly IPdfRuntimeSelectionStore _pdfSelection;
    private readonly NativeForeignOperations _compilerReads = new(1), _pdfReads = new(1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private bool _disposed;
    private Task? _closing;
    private readonly BundledRuntimeAssets? _bundled;
    public bool UsesBundledResources => _bundled is not null;
    public LocalInstalledRuntimeProviders(IJSRuntime js) : this(new(new JsRuntimeAssetBackend(js)), new JsLocalRuntimeSelectionStore(js),
        new(new JsRuntimeAssetBackend(js)), new JsPdfRuntimeSelectionStore(js)) { }
    public LocalInstalledRuntimeProviders(IJSRuntime js, BundledRuntimeAssets bundled) : this(js) { _bundled = bundled; }
    public LocalInstalledRuntimeProviders(RuntimeAssetStore compilerStore, ILocalRuntimeSelectionStore compilerSelection,
        RuntimeAssetStore pdfStore, IPdfRuntimeSelectionStore pdfSelection)
    { _compilerStore = compilerStore; _compilerSelection = compilerSelection; _pdfStore = pdfStore; _pdfSelection = pdfSelection; }
    public Task CompilerWhenSettled => Task.WhenAll(_compilerReads.WhenIdle, _compilerStore.WhenSettled, _bundled?.WhenSettled ?? Task.CompletedTask);
    public Task WhenSettled => Task.WhenAll(CompilerWhenSettled, _pdfReads.WhenIdle, _pdfStore.WhenSettled);
    public Task<RuntimeAssetStatus> ConfirmCompilerSelectionAsync(InstalledNativeRuntimeRead read, CancellationToken token = default)
        => _bundled is not null ? Task.FromResult(token.IsCancellationRequested ? RuntimeAssetStatus.Cancelled
            : _bundled.Owns(read.Package) ? RuntimeAssetStatus.Ready : RuntimeAssetStatus.Uncertain) : RunAsync(_compilerReads, async owned =>
        {
            var text = await _compilerSelection.ReadAsync().ConfigureAwait(false); owned.ThrowIfCancellationRequested();
            if (text is null) return RuntimeAssetStatus.Missing;
            var mapping = LocalRuntimeSelection.Decode(text);
            return read.Package is not null && mapping.Profile == read.Package.Profile
                && mapping.InstallationId == read.Store.InstallationId && mapping.InventoryDigest == read.Store.InventoryDigest
                ? RuntimeAssetStatus.Ready : RuntimeAssetStatus.Uncertain;
        }, status => status, token);
    public Task<InstalledNativeRuntimeRead> ReadCompilerAsync(CancellationToken token = default) => _bundled?.ReadCompilerAsync(token) ?? RunAsync(_compilerReads, async owned =>
    {
        try
        {
            var before = await _compilerSelection.ReadAsync().ConfigureAwait(false); owned.ThrowIfCancellationRequested();
            if (before is null) return CompilerFailure(RuntimeAssetStatus.Missing);
            var mapping = LocalRuntimeSelection.Decode(before);
            var result = await new InstalledNativeRuntimeProvider(_compilerStore, mapping, LocalRuntimeSelection.BudgetFor(mapping.Profile), LocalRuntimeSelection.LimitsFor(mapping.Profile)).ReadPackageAsync(owned).ConfigureAwait(false);
            await _compilerStore.WhenSettled.ConfigureAwait(false); owned.ThrowIfCancellationRequested();
            var after = await _compilerSelection.ReadAsync().ConfigureAwait(false); owned.ThrowIfCancellationRequested();
            return before == after ? result : CompilerFailure(RuntimeAssetStatus.Uncertain);
        }
        finally { await _compilerStore.WhenSettled.ConfigureAwait(false); }
    }, CompilerFailure, token);
    public Task<InstalledPdfRuntimeRead> ReadPdfAsync(CancellationToken token = default) => _bundled?.ReadPdfAsync(token) ?? RunAsync(_pdfReads, async owned =>
    {
        try
        {
            var before = await _pdfSelection.ReadAsync().ConfigureAwait(false); owned.ThrowIfCancellationRequested();
            if (before is null) return PdfFailure(RuntimeAssetStatus.Missing);
            var selection = PdfRuntimeSelection.Decode(before);
            // COMPLETE exact trust preflight BEFORE any byte store IO or PDF port import.
            if (!TrustedPdf(selection)) return PdfFailure(RuntimeAssetStatus.Corrupt);
            var result = await new InstalledPdfRuntimeProvider(_pdfStore, selection.TechnicalMapping(), PdfRuntimeSelection.Budget).ReadPackageAsync(owned).ConfigureAwait(false);
            await _pdfStore.WhenSettled.ConfigureAwait(false); owned.ThrowIfCancellationRequested();
            var after = await _pdfSelection.ReadAsync().ConfigureAwait(false); owned.ThrowIfCancellationRequested();
            return before == after ? result : PdfFailure(RuntimeAssetStatus.Uncertain);
        }
        finally { await _pdfStore.WhenSettled.ConfigureAwait(false); }
    }, PdfFailure, token);
    private static bool TrustedPdf(PdfResourceInstallation selection) => selection.Resources.Length == 2
        && selection.Resources.Any(r => r.Role == "loader" && r.Name == "e1.js" && r.Length == 13562 && r.Sha256 == PdfLoaderHash)
        && selection.Resources.Any(r => r.Role == "wasm" && r.Name == "e1.wasm" && r.Length == 3434939 && r.Sha256 == PdfWasmHash);
    private async Task<T> RunAsync<T>(NativeForeignOperations operations, Func<CancellationToken, Task<T>> read,
        Func<RuntimeAssetStatus, T> failure, CancellationToken caller)
    {
        CancellationTokenSource deadline, linked;
        lock (_gate)
        {
            if (_disposed) return failure(RuntimeAssetStatus.Disposed);
            deadline = new(TimeSpan.FromSeconds(30)); linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _shutdown.Token, deadline.Token);
        }
        var token = linked.Token; bool started = false;
        try
        {
            return await operations.RunAsync(() => { started = true; return Actual(); }, token).ConfigureAwait(false);
        }
        catch (NativeResourceLimitException) { return failure(RuntimeAssetStatus.Busy); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return failure(_disposed ? RuntimeAssetStatus.Disposed : caller.IsCancellationRequested ? RuntimeAssetStatus.Cancelled : RuntimeAssetStatus.TimedOut); }
        catch (RuntimeAssetIoException error) { return failure(error.Status); }
        catch (Exception error) when (error is System.Text.Json.JsonException or FormatException or InvalidOperationException or ArgumentException or OverflowException)
        { return failure(RuntimeAssetStatus.Corrupt); }
        catch (Exception error) when (error is not OutOfMemoryException) { return failure(RuntimeAssetStatus.StorageFailure); }
        finally { if (!started) { linked.Dispose(); deadline.Dispose(); } }
        async Task<T> Actual()
        {
            try { return await read(token).ConfigureAwait(false); }
            finally { linked.Dispose(); deadline.Dispose(); }
        }
    }
    public async Task<NativeRuntimePackage?> GetAsync() => (await ReadCompilerAsync().ConfigureAwait(false)).Package;
    async ValueTask<PdfRuntimePackage?> IPdfRuntimeProvider.GetAsync(CancellationToken token) => (await ReadPdfAsync(token).ConfigureAwait(false)).Package;
    async ValueTask<SyncTeXRuntimePackage?> ISyncTeXRuntimeProvider.GetAsync(CancellationToken token)
    {
        // Reuse the actual compiler read: same single admission, strict selection
        // before/after and fresh full-closure byte verification. No query cache/store.
        var read = await ReadCompilerAsync(token).ConfigureAwait(false);
        if (read.Package is not { } package
            || !NativeRuntimeProfiles.IsSuite(package.Profile) && package.Profile is not (NativeRuntimeProfile.LocalLatex2023SyncTeX or NativeRuntimeProfile.LocalLatex2023Dvi)) return null;
        token.ThrowIfCancellationRequested();
        return new(package.Copy("synctex-query.js"), package.Copy("synctex-query.wasm"),
            NativeRuntimeProfiles.SyncTeXQueryLoaderHash, NativeRuntimeProfiles.SyncTeXQueryWasmHash);
    }
    private static InstalledNativeRuntimeRead CompilerFailure(RuntimeAssetStatus status) => new(new(status), null, NativeRuntimeIssue.Unavailable);
    private static InstalledPdfRuntimeRead PdfFailure(RuntimeAssetStatus status) => new(new(status), null);
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true; _shutdown.Cancel();
            _closing = CloseAsync(); Observe(_closing); return ValueTask.CompletedTask;
        }
    }
    private async Task CloseAsync()
    {
        if (_bundled is not null) await _bundled.DisposeAsync().ConfigureAwait(false);
        await _compilerStore.DisposeAsync().ConfigureAwait(false); await _pdfStore.DisposeAsync().ConfigureAwait(false);
        await WhenSettled.ConfigureAwait(false); _shutdown.Dispose();
    }
    internal static void Observe(Task task) => _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}

/// <summary>One app-lifetime inspector, borrowed by project preview controllers. Status probe is
/// per admitted inspection; neither a cached package nor a positive readiness flag.</summary>
internal sealed class LocalPdfInspector : IPdfInspector
{
    private sealed class Attempt { public RuntimeAssetStatus Status = RuntimeAssetStatus.Missing; }
    private sealed class Probe(LocalInstalledRuntimeProviders resources) : IPdfRuntimeProvider
    {
        public AsyncLocal<Attempt?> Current { get; } = new();
        public async ValueTask<PdfRuntimePackage?> GetAsync(CancellationToken token)
        {
            var attempt = Current.Value;
            var read = await resources.ReadPdfAsync(token).ConfigureAwait(false);
            if (attempt is not null) attempt.Status = read.Store.Status;
            return read.Package;
        }
    }
    private readonly Probe _probe;
    private readonly JsPdfInspector _inner;
    private int _disposed;
    public LocalPdfInspector(IJSRuntime js, LocalInstalledRuntimeProviders resources) { _probe = new(resources); _inner = new(js, _probe); }
    public async Task<PdfInspectionOutcome> InspectAsync(PdfInspectRequest request, CancellationToken token = default)
    {
        // Per-async-call status preserves inner request/epoch/Busy/cancellation ownership.
        // A late provider may update only its own abandoned attempt, never a newer one.
        var attempt = new Attempt(); _probe.Current.Value = attempt;
        try
        {
            var result = await _inner.InspectAsync(request, token).ConfigureAwait(false);
            if (result is not { Status: PdfInspectionStatus.Unavailable, Code: "RuntimeAbsent" }) return result;
            var status = attempt.Status switch
            {
                RuntimeAssetStatus.Busy => PdfInspectionStatus.Busy,
                RuntimeAssetStatus.Cancelled => PdfInspectionStatus.Cancelled,
                RuntimeAssetStatus.TimedOut => PdfInspectionStatus.TimedOut,
                RuntimeAssetStatus.Disposed => PdfInspectionStatus.Disposed,
                _ => PdfInspectionStatus.Unavailable
            };
            return result with { Status = status, Code = "Runtime" + attempt.Status };
        }
        finally { _probe.Current.Value = null; }
    }
    public ValueTask DisposeAsync() => Interlocked.Exchange(ref _disposed, 1) == 0 ? _inner.DisposeAsync() : ValueTask.CompletedTask;
}
