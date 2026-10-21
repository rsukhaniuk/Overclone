using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Client.Compilation;
using Client.Compilation.Assets;
using Client.Compilation.Native;
using Client.Compilation.Presentation;
using Client.Pdf;
using Client.RuntimeAssets;
using Microsoft.JSInterop;
using Hash = Client.Workspace.Domain.SHA256;

namespace AuthoringBrowserHarness;

// Private trusted TEST provisioning only. No project seed/edit/compile shortcuts.
public sealed class PrivateTools(IJSRuntime js, HttpClient http) : ICompileWorkspaceConfiguration, IAsyncDisposable
{
    private RuntimeAssetStore? _store;
    private InstalledNativeRuntimeProvider? _installed;
    private NativeBrowserModuleLease? _module;
    private JsPdfInspector? _pdf;
    private CompileWorkspacePorts? _ports;
    private InstalledNativeRuntimeMapping? _mapping;
    private VerifiedAssetInventory? _trustedInventory;
    public string Status { get; private set; } = "No private tools provisioned";
    public CompileReadiness LatestRead { get; private set; } = new(false, "Unavailable", "Private TEST tools not provisioned.");
    public List<object> Phases { get; } = [];
    public List<object> Inspections { get; } = [];
    public bool HoldNext { get; set; }
    public bool Held { get; private set; }
    private TaskCompletionSource? _release;
    public int ActiveForeign => (_ports?.Factory as NativeCompilationWorkerFactory)?.Operations.Active ?? 0;
    public event Action? Changed;
    private static RuntimeAssetBudget Budget => new(new(378, 256, 16777216, 33554432, 1048576, 256, 256), 262144, 8, 16, 30000);
    private static ImmutableArray<InstalledNativeResource> Resources => TrustedCompilerPins.All.Select(p => new InstalledNativeResource(
        new(new Guid(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("N19:" + p.Name)).AsSpan(0, 16))), "runtime/" + p.Name, p)).ToImmutableArray();
    public Task<CompileWorkspaceConfigurationResult> OpenAsync() => Task.FromResult(new CompileWorkspaceConfigurationResult(LatestRead, _ports));
    public async Task Provision()
    {
        if (_ports is not null) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _store = new(new JsRuntimeAssetBackend(js));
        var rows = Resources; var declaration = AssetInventoryFactory.Create(new(rows.Select(r => new DeclaredAssetInput
        { AssetId = r.AssetId, RelativePath = r.RelativePath, DeclaredLength = r.Pin.Length, DeclaredHash = new(r.Pin.Sha256), Version = "private-N19-trusted" }).ToArray()), Budget.Inventory).Inventory ?? throw new InvalidOperationException("Trusted declaration");
        var inputs = new Dictionary<AssetId, byte[]>();
        foreach (var r in rows)
        {
            var b = await http.GetByteArrayAsync("private-inputs/compiler/" + r.Pin.Name, deadline.Token);
            if (b.Length != r.Pin.Length || NativeRuntimePackage.Digest(b).Hex != r.Pin.Sha256) throw new InvalidOperationException("Original compiler pin");
            inputs.Add(r.AssetId, b);
        }
        var verified = (await new AssetInventoryVerifier().VerifyAsync(declaration, new Reader(inputs), Budget.Verification, deadline.Token) as AssetVerificationResult.Verified)?.Inventory ?? throw new InvalidOperationException("Full378 verification");
        _trustedInventory = verified;
        var installed = await _store.InstallAsync(verified, Budget, deadline.Token);
        if (installed.Status != RuntimeAssetStatus.Installed) throw new InvalidOperationException("Compiler install " + installed.Status);
        _mapping = new(installed.InstallationId!.Value, declaration.Identity.Digest.Hex, rows);
        _installed = new(_store, _mapping, Budget, new(512, 16777216, 33554432));
        var read = await _installed.ReadPackageAsync(deadline.Token); var package = read.Package ?? throw new InvalidOperationException("Fresh installed package");
        var loaderId = new AssetId(Guid.Parse("11111111-1111-1111-1111-111111111111")); var wasmId = new AssetId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        const string lh = "040bdfac50656b3993f622614b73feaaba31d8253d077ff50f925f316c2f5f6b", wh = "ea5a9a81d6e200fb59ccaced8ecd510d84999c648acf4a13e4ebbef07b1e2e8b";
        var loader = await http.GetByteArrayAsync("private-inputs/e1.js.bin", deadline.Token); var wasm = await http.GetByteArrayAsync("private-inputs/e1.wasm.bin", deadline.Token);
        var pdfDecl = AssetInventoryFactory.Create(new(new[] { new DeclaredAssetInput { AssetId = loaderId, RelativePath = "runtime/e1.js", DeclaredLength = 13562, DeclaredHash = new(lh), Version = "private-n10c1" }, new DeclaredAssetInput { AssetId = wasmId, RelativePath = "runtime/e1.wasm", DeclaredLength = 3434938, DeclaredHash = new(wh), Version = "private-n10c1" } }), Budget.Inventory).Inventory!;
        var pdfVerified = (await new AssetInventoryVerifier().VerifyAsync(pdfDecl, new Reader(new() { [loaderId] = loader, [wasmId] = wasm }), Budget.Verification, deadline.Token) as AssetVerificationResult.Verified)?.Inventory ?? throw new InvalidOperationException("PDF pins");
        var pdfInstall = await _store.InstallAsync(pdfVerified, Budget, deadline.Token);
        if (pdfInstall.Status != RuntimeAssetStatus.Installed) throw new InvalidOperationException("PDF install");
        var provider = new InstalledPdfRuntimeProvider(_store, new(pdfInstall.InstallationId!.Value, pdfDecl.Identity.Digest.Hex, loaderId, 13562, lh, wasmId, 3434938, wh), Budget);
        _pdf = new(js, provider);
        _module = await new NativeBrowserModuleLoader(js).LoadAsync(deadline.Token);
        if (!await _module.ProvisionAsync()) throw new InvalidOperationException("Explicit worker module provisioning");
        var factory = new NativeCompilationWorkerFactory(new CurrentProvider(this), new HeldTransport(this, _module.Transport), new(true, 4, TimeSpan.FromSeconds(25), UseSelectionTools: true), new(2));
        factory.PhaseObserved += phase =>
        {
            if (Phases.Count >= 96) throw new InvalidOperationException("Phase trace bound");
            Phases.Add(new { phase.Tag, phase.Sent, phase.Reply.Outcome, phase.Reply.NativeReturn, phase.Reply.Entered, phase.Reply.LoaderSha256, phase.Reply.WasmSha256,
                log = phase.Reply.Log, phase.Reply.Inputs, files = phase.Reply.Files.Select(f => new { f.Name, length = f.Bytes.Length, hash = NativeRuntimePackage.Digest(f.Bytes).Hex, generated = f.Name.EndsWith(".aux", StringComparison.Ordinal) || f.Name == "main.bbl" ? Convert.ToBase64String(f.Bytes) : null }) }); Changed?.Invoke();
        };
        _ports = new("historical-pdftex", "Historical pdfLaTeX + BibTeX (private TEST)", package.Engine, [package.Bundle], [package.Bibtex], factory, new RecordingPdf(this, _pdf), Check, this, SelectableBibliography: true);
        LatestRead = new(true, "Ready", "Explicit private TEST installation; main.tex/historical package limits."); Status = "Private378 compiler and PDF installed";
        foreach (var path in new[] { "./js/pdf-port-bridge.js", "./js/pdf-preview-canvas.js", "./test-hooks.js" }) { var module = await js.InvokeAsync<IJSObjectReference>("import", path); await module.DisposeAsync(); }
        Changed?.Invoke();
    }
    public async Task Recover()
    {
        if (_store is null || _trustedInventory is null || _mapping is null) throw new InvalidOperationException("Explicit initial provision required");
        var previous = _mapping.InstallationId; var installed = await _store.InstallAsync(_trustedInventory, Budget);
        if (installed.Status != RuntimeAssetStatus.Installed || installed.InstallationId == previous) throw new InvalidOperationException("New installation recovery");
        _mapping = _mapping with { InstallationId = installed.InstallationId!.Value };
        _installed = new(_store, _mapping, Budget, new(512, 16777216, 33554432));
        await Check(); Status = "Explicit new private compiler installation recovered"; Changed?.Invoke();
    }
    private sealed class CurrentProvider(PrivateTools parent) : INativeRuntimeProvider
    { public Task<NativeRuntimePackage?> GetAsync() => parent._installed!.GetAsync(); }
    private async Task<CompileReadiness> Check()
    {
        var r = await _installed!.ReadPackageAsync();
        LatestRead = new(r.Package is not null, r.Store.Status.ToString(), "Installed compiler: " + r.Store.Status + ". No automatic repair/download."); Changed?.Invoke(); return LatestRead;
    }
    public void Release() { _release?.TrySetResult(); }
    public object Observations() => new { Status, LatestRead, ActiveForeign, Held, Phases, Inspections, module = _disposed ? null : _module?.Observations(), mapping = _mapping };
    private sealed class Reader(Dictionary<AssetId, byte[]> data) : IAssetByteReader
    { public Task<byte[]?> ReadAsync(DeclaredAsset asset, CancellationToken ct) => Task.FromResult<byte[]?>(data.GetValueOrDefault(asset.AssetId)); }
    private sealed class HeldTransport(PrivateTools parent, INativePhaseTransport actual) : INativePhaseTransport
    {
        public async Task<NativePhaseReply> ExecuteAsync(NativePhaseRequest request)
        {
            var result = await actual.ExecuteAsync(request);
            if (parent.HoldNext)
            {
                parent.HoldNext = false; parent.Held = true; parent._release = new(TaskCreationOptions.RunContinuationsAsynchronously); parent.Changed?.Invoke();
                await parent._release.Task; parent.Held = false; parent.Changed?.Invoke();
            }
            return result;
        }
        public void RequestTermination(string job, long epoch) => actual.RequestTermination(job, epoch);
    }
    private sealed class RecordingPdf(PrivateTools parent, IPdfInspector actual) : IPdfInspector
    {
        public async Task<PdfInspectionOutcome> InspectAsync(PdfInspectRequest request, CancellationToken token = default)
        {
            var r = await actual.InspectAsync(request, token);
            if (parent.Inspections.Count >= 96) throw new InvalidOperationException("PDF trace bound");
            parent.Inspections.Add(new { r.RequestId, r.DocumentSha256, r.WorkerEpoch, status = r.Status.ToString(), r.NativeStatus, r.EnteredNative, r.Runtime, request.PageIndex, request.Search, request.Raster, page = r.Page is { } p ? new { p.Text, p.Utf16Units, p.Glyphs, p.SearchHits, p.RasterWidth, p.RasterHeight } : null }); parent.Changed?.Invoke(); return r;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask; // borrowed actual inspector
    }
    private bool _disposed;
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true; Release(); Changed = null;
        if (_pdf is not null) await _pdf.DisposeAsync();
        if (_module is not null) await _module.DisposeAsync();
        if (_store is not null) await _store.DisposeAsync();
    }
}
