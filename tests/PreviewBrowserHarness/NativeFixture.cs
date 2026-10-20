using System.Text.Json;
using Client.Compilation.Assets;
using Client.Pdf;
using Client.RuntimeAssets;
using Client.Pdf.Preview;
using Microsoft.JSInterop;
using Hash = Client.Workspace.Domain.SHA256;

namespace PreviewBrowserHarness;

// Explicit TEST provisioning of accepted bytes, never product default adoption.
public sealed class NativeFixture : IAsyncDisposable
{
    public const string LoaderHash = "040bdfac50656b3993f622614b73feaaba31d8253d077ff50f925f316c2f5f6b";
    public const string WasmHash = "ea5a9a81d6e200fb59ccaced8ecd510d84999c648acf4a13e4ebbef07b1e2e8b";
    public const string N11Hash = "fa243d9af9fd8911853ef98c344edd555d614740e8a73fac73cd34fc430c4f3a";
    public const string RegularHash = "44a48c241ff55bd29197767d746b732d5e1e86cf32c6389ad7a56c2e0735318f";
    public const string ScanHash = "328a122095cad9117ad91f69e44d57679f0039938aa8c012e2c1396cb4145d39";
    public const string CropHash = "3431a64ebfe1c612decf07e83bc2a010bef4dad03e955116d329c77e73e5be45";
    private readonly RuntimeAssetStore _store;
    private readonly JsPdfInspector _native;
    public RecordingInspector Inspector { get; }
    public IReadOnlyDictionary<string, PdfDocument> Documents { get; }
    public sealed record VerifiedInput(string Name, int Length, string Sha256);
    public IReadOnlyList<VerifiedInput> VerifiedInputs { get; }
    public bool Disposed { get; private set; }
    public RuntimeAssetStatus Installation { get; }
    public string InventoryDigest { get; }
    private NativeFixture(RuntimeAssetStore store, JsPdfInspector native, IReadOnlyDictionary<string, PdfDocument> documents, RuntimeAssetStatus installed, string digest, List<VerifiedInput> inputs)
    { _store = store; _native = native; Documents = documents; Inspector = new(native); Installation = installed; InventoryDigest = digest; VerifiedInputs = inputs.AsReadOnly(); }
    public static async Task<NativeFixture> Create(IJSRuntime js, HttpClient http)
    {
        var loader = await http.GetByteArrayAsync("private-inputs/e1.js.bin"); var wasm = await http.GetByteArrayAsync("private-inputs/e1.wasm.bin");
        var loaderId = new AssetId(new Guid("11111111-1111-1111-1111-111111111111")); var wasmId = new AssetId(new Guid("22222222-2222-2222-2222-222222222222"));
        var budget = new RuntimeAssetBudget(new(4, 256, 8388608, 16777216, 16384, 256, 256), 32768, 8, 8, 15000);
        var declaration = AssetInventoryFactory.Create(new(new[] {
            new DeclaredAssetInput { AssetId = loaderId, RelativePath = "runtime/e1.js", DeclaredLength = 13562, DeclaredHash = new Hash(LoaderHash), Version = "private-n10c1-test" },
            new DeclaredAssetInput { AssetId = wasmId, RelativePath = "runtime/e1.wasm", DeclaredLength = 3434938, DeclaredHash = new Hash(WasmHash), Version = "private-n10c1-test" }
        }), budget.Inventory).Inventory ?? throw new InvalidOperationException("Trusted test inventory");
        var verified = await new AssetInventoryVerifier().VerifyAsync(declaration, new Bytes(loaderId, loader, wasm), budget.Verification, CancellationToken.None);
        var receipt = (verified as AssetVerificationResult.Verified)?.Inventory ?? throw new InvalidOperationException("Accepted runtime identity");
        var completedInputs = new List<VerifiedInput> { new("e1.js.bin", loader.Length, PdfHashes.Of(loader)), new("e1.wasm.bin", wasm.Length, PdfHashes.Of(wasm)) };
        var store = new RuntimeAssetStore(new JsRuntimeAssetBackend(js));
        try
        {
            var installed = await store.InstallAsync(receipt, budget); if (installed.Status != RuntimeAssetStatus.Installed) throw new InvalidOperationException("Explicit install: " + installed.Status);
            var mapping = new InstalledPdfRuntimeMapping(installed.InstallationId!.Value, declaration.Identity.Digest.Hex, loaderId, 13562, LoaderHash, wasmId, 3434938, WasmHash);
            var provider = new InstalledPdfRuntimeProvider(store, mapping, budget);
            var docs = new Dictionary<string, PdfDocument>();
            foreach (var p in new[] { ("n11", "revision-a.pdf", 63856, N11Hash), ("regular", "regular.pdf", 40246, RegularHash), ("scan", "scan.pdf", 824, ScanHash), ("crop", "crop270.pdf", 513, CropHash) })
            {
                var bytes = await http.GetByteArrayAsync("private-inputs/" + p.Item2);
                if (bytes.Length != p.Item3 || PdfHashes.Of(bytes) != p.Item4) throw new InvalidOperationException("Accepted PDF identity");
                docs.Add(p.Item1, new(bytes)); completedInputs.Add(new(p.Item2, bytes.Length, PdfHashes.Of(bytes)));
            }
            // Explicit WARM TEST provisioning of ES modules before host stop.
            // Importing does not create a PDF port, worker, native job or canvas.
            // Module-map caching is not service-worker/PWA cold-start evidence.
            foreach (string path in new[] { "./js/pdf-port-bridge.js", "./js/pdf-preview-canvas.js" })
            { var module = await js.InvokeAsync<IJSObjectReference>("import", path); await module.DisposeAsync(); }
            return new(store, new JsPdfInspector(js, provider), docs, installed.Status, declaration.Identity.Digest.Hex, completedInputs);
        }
        catch { await store.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        if (Disposed) return; Disposed = true; Inspector.Release(); await _native.DisposeAsync(); await _store.DisposeAsync();
    }
    private sealed class Bytes(AssetId loader, byte[] js, byte[] wasm) : IAssetByteReader
    { public Task<byte[]?> ReadAsync(DeclaredAsset a, CancellationToken ct) => Task.FromResult<byte[]?>(a.AssetId == loader ? js : wasm); }
}

// Explicit, finite TEST-only delays/failure AFTER real native completion. Positive
// calls are unchanged real JsPdfInspector outcomes. No duplicate renderer/worker.
public sealed class RecordingInspector(IPdfInspector actual) : IPdfInspector
{
    private TaskCompletionSource<bool>? _release;
    public bool HoldNext { get; set; }
    public bool FailNext { get; set; }
    public bool Held { get; private set; }
    public int Active { get; private set; }
    public int Peak { get; private set; }
    public int CallsStarted { get; private set; }
    public List<object> Calls { get; } = [];
    public event Action? Changed;
    public async Task<PdfInspectionOutcome> InspectAsync(PdfInspectRequest request, CancellationToken token = default)
    {
        if (CallsStarted >= 160) throw new InvalidOperationException("Finite harness call cap");
        Active++; Peak = Math.Max(Peak, Active); CallsStarted++; Changed?.Invoke();
        try
        {
            var outcome = await actual.InspectAsync(request, token);
            bool fail = FailNext; FailNext = false;
            Calls.Add(new { request.Document.Sha256, request.PageIndex, request.Search, request.Raster, request.RasterWidth, request.RasterHeight,
                status = outcome.Status.ToString(), outcome.RequestId, outcome.WorkerEpoch, outcome.NativeStatus, outcome.EnteredNative, outcome.Runtime, outcome.NativeCalls,
                injection = fail ? "INJECTED-failure-after-real-native-completion" : "none", page = outcome.Page is not { } page ? null : new {
                    page.PageCount, page.PageIndex, page.Text, page.Utf16Units, page.NativeDisplaySize, page.EffectiveBox, page.Rotation,
                    page.SearchHits, glyphs = request.Search is null ? null : page.Glyphs, bgraSha256 = request.Raster ? PdfHashes.Of(page.CopyBgra()) : null } });
            if (HoldNext)
            {
                HoldNext = false; _release = new(TaskCreationOptions.RunContinuationsAsynchronously); Held = true; Changed?.Invoke();
                await _release.Task; Held = false; Changed?.Invoke();
            }
            return fail ? outcome with { Status = PdfInspectionStatus.TransportError, Page = null, Code = "INJECTED-after-native-completion" } : outcome;
        }
        finally { Active--; Changed?.Invoke(); }
    }
    public void Release() => _release?.TrySetResult(true);
    public ValueTask DisposeAsync() { Release(); Changed = null; return ValueTask.CompletedTask; } // borrows actual
}
