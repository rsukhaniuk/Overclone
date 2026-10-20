using System.Text.Json;
using Client.Compilation.Assets;
using Client.RuntimeAssets;
using Client.Pdf;
using Microsoft.JSInterop;
using Hash = Client.Workspace.Domain.SHA256;

namespace AssetBrowserHarness;

public static class HarnessApi
{
    public static string Last { get; private set; } = "";
    private static IJSRuntime _js = null!; private static HttpClient _http = null!; private static IJSObjectReference _test = null!;
    private static RuntimeAssetStore _store = null!; private static VerifiedAssetInventory? _receipt;
    private static JsRuntimeAssetBackend? _holder; private static IRuntimeAssetLease? _hold;
    private static readonly AssetId Loader = new(new Guid("11111111-1111-1111-1111-111111111111")), Wasm = new(new Guid("22222222-2222-2222-2222-222222222222"));
    private const string LoaderHash = "040bdfac50656b3993f622614b73feaaba31d8253d077ff50f925f316c2f5f6b", WasmHash = "ea5a9a81d6e200fb59ccaced8ecd510d84999c648acf4a13e4ebbef07b1e2e8b";
    private static RuntimeAssetBudget Budget => new(new(4, 256, 8388608, 16777216, 16384, 256, 256), 32768, 8, 8, 15000);
    private static AssetInventory Declaration()
    {
        var value = AssetInventoryFactory.Create(new(new[]
        {
            new DeclaredAssetInput { AssetId = Loader, RelativePath = "runtime/e1.js", DeclaredLength = 13562, DeclaredHash = new Hash(LoaderHash), Version = "private-n10c1-test" },
            new DeclaredAssetInput { AssetId = Wasm, RelativePath = "runtime/e1.wasm", DeclaredLength = 3434938, DeclaredHash = new Hash(WasmHash), Version = "private-n10c1-test" }
        }), Budget.Inventory);
        return value.Inventory ?? throw new InvalidOperationException("Trusted TEST declaration rejected.");
    }
    public static async Task Initialize(IJSRuntime js, HttpClient http)
    {
        _js = js; _http = http; _test = await js.InvokeAsync<IJSObjectReference>("import", "./test-host.js");
        _store = new(new JsRuntimeAssetBackend(js));
        Last = JsonSerializer.Serialize(new { ready = true, onlineReceiptPresent = _receipt is not null, inventoryDigest = Declaration().Identity.Digest.Hex });
    }
    private static async Task<VerifiedAssetInventory> ProvisionReceipt()
    {
        if (_receipt is not null) return _receipt;
        var loader = await _http.GetByteArrayAsync("private-inputs/e1.js.bin"); var wasm = await _http.GetByteArrayAsync("private-inputs/e1.wasm.bin");
        var result = await new AssetInventoryVerifier().VerifyAsync(Declaration(), new BytesReader(loader, wasm), Budget.Verification, CancellationToken.None);
        _receipt = (result as AssetVerificationResult.Verified)?.Inventory ?? throw new InvalidOperationException("Private runtime input verification failed.");
        return _receipt;
    }
    private static object Summary(RuntimeAssetResult result) => new { status = result.Status.ToString(), installationId = result.InstallationId?.ToString("N"), result.InventoryDigest, result.PublishMayHaveChanged, cause = result.Cause?.ToString() };
    private static InstalledPdfRuntimeMapping Mapping(Guid id) => new(id, Declaration().Identity.Digest.Hex, Loader, 13562, LoaderHash, Wasm, 3434938, WasmHash);
    private static async Task<Guid> Selected()
    { string value = await _test.InvokeAsync<string>("selected"); return Guid.ParseExact(value, "N"); }
    private static async Task<object> Show(object value) { Last = JsonSerializer.Serialize(value); await _test.InvokeVoidAsync("show", Last); return value; }
    [JSInvokable]
    public static async Task<object> InstallPrivate()
    {
        var receipt = await ProvisionReceipt(); var result = await _store.InstallAsync(receipt, Budget);
        if (result.Status == RuntimeAssetStatus.Installed)
        {
            var pdf = await _http.GetByteArrayAsync("private-inputs/regular.pdf");
            await _test.InvokeVoidAsync("saveDocument", pdf, "local document sentinel Привіт");
            await _test.InvokeVoidAsync("select", result.InstallationId!.Value.ToString("N"));
        }
        return await Show(new { operation = "explicit-install", result = Summary(result), sentinel = await _test.InvokeAsync<string>("sentinel") });
    }
    [JSInvokable]
    public static async Task<object> ReadAndPdf()
    {
        var id = await Selected(); var provider = new InstalledPdfRuntimeProvider(_store, Mapping(id), Budget);
        var read = await provider.ReadPackageAsync(); var before = await _test.InvokeAsync<WorkerState>("workers");
        var counter = new Counter(_js); await using var inspector = new JsPdfInspector(counter, provider);
        var bytes = await _test.InvokeAsync<byte[]>("pdf"); var result = await inspector.InspectAsync(new(new PdfDocument(bytes), 0, new(), "Україно", true, 240, 340));
        var after = await _test.InvokeAsync<WorkerState>("workers"); var pixels = result.Page?.CopyBgra() ?? [];
        return await Show(new
        {
            operation = "stored-read-native",
            onlineReceiptPresent = _receipt is not null,
            read = Summary(read.Store),
            packageLoaderHash = read.Package?.LoaderSha256,
            packageWasmHash = read.Package?.WasmSha256,
            pdfStatus = result.Status.ToString(),
            result.Code,
            result.NativeStatus,
            result.RequestId,
            result.DocumentSha256,
            result.WorkerEpoch,
            result.Runtime,
            text = result.Page?.Text,
            utf16 = result.Page?.Utf16Units,
            rasterSha256 = pixels.Length == 0 ? null : PdfHashes.Of(pixels),
            rasterBytes = pixels.Length,
            nativeCalls = result.NativeCalls,
            imports = counter.Imports,
            before,
            after,
            sentinel = await _test.InvokeAsync<string>("sentinel"),
            cacheUrls = await _test.InvokeAsync<string[]>("cachedUrls")
        });
    }
    [JSInvokable]
    public static async Task<object> HoldLock()
    { _holder = new(_js); _hold = await _holder.TryAcquireAsync(); return await Show(new { held = _hold is not null }); }
    [JSInvokable]
    public static async Task ReleaseLock()
    { if (_hold is not null) await _hold.DisposeAsync(); if (_holder is not null) await _holder.DisposeAsync(); _hold = null; _holder = null; }
    [JSInvokable]
    public static async Task<object> InjectPartialQuota()
    {
        var receipt = await ProvisionReceipt(); await using var store = new RuntimeAssetStore(new FaultBackend(new JsRuntimeAssetBackend(_js)));
        var result = await store.InstallAsync(receipt, Budget); var id = result.InstallationId!.Value;
        var incomplete = await _store.ReadAsync(id, receipt.Identity.Digest.Hex, Budget);
        var previous = await _store.ReadAsync(await Selected(), receipt.Identity.Digest.Hex, Budget);
        var beforeDiscard = await _test.InvokeAsync<JsonElement>("inspectAsset", id.ToString("N"));
        var discard = await store.DiscardAsync(id, Budget); var stale = await store.DiscardAsync(id, Budget);
        return await Show(new { operation = "INJECTED-write-quota-after-real-first-write", result = Summary(result), incomplete = Summary(incomplete), previous = Summary(previous), beforeDiscard, discard = Summary(discard), staleDiscard = Summary(stale), sentinel = await _test.InvokeAsync<string>("sentinel") });
    }
    [JSInvokable]
    public static async Task<object> InjectPublishUncertainty()
    {
        var backend = new DelayBackend(new JsRuntimeAssetBackend(_js)); var store = new RuntimeAssetStore(backend); using var caller = new CancellationTokenSource();
        var first = store.InstallAsync(await ProvisionReceipt(), Budget, caller.Token);
        try
        {
            await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel(); var result = await first.WaitAsync(TimeSpan.FromSeconds(3));
            var blocked = await store.DiscardAsync(result.InstallationId!.Value, Budget);
            var reconciled = await _store.ReadAsync(result.InstallationId.Value, Declaration().Identity.Digest.Hex, Budget);
            bool pending = !backend.Release.Task.IsCompleted && !store.WhenSettled.IsCompleted;
            backend.Release.TrySetResult(true); await store.WhenSettled.WaitAsync(TimeSpan.FromSeconds(5));
            var protectedSet = await store.DiscardAsync(result.InstallationId.Value, Budget);
            return await Show(new { operation = "INJECTED-post-commit-completion-delay", result = Summary(result), blocked = Summary(blocked), reconciled = Summary(reconciled), actualStillPendingBeforeRelease = pending, protectedSet = Summary(protectedSet), sentinel = await _test.InvokeAsync<string>("sentinel") });
        }
        finally { backend.Release.TrySetResult(true); await store.WhenSettled.WaitAsync(TimeSpan.FromSeconds(5)); await store.DisposeAsync(); }
    }
    [JSInvokable]
    public static async Task<object> Mutate(string mode)
    {
        var id = await Selected(); var declaration = Declaration(); int index = Enumerable.Range(0, declaration.AssetCount).Single(i => declaration.Assets[i].AssetId == Wasm);
        await _test.InvokeVoidAsync("mutate", id.ToString("N"), index, mode);
        return await ReadAndPdf();
    }
    [JSInvokable] public static async Task<object> InspectSelected() => await _test.InvokeAsync<JsonElement>("inspectAsset", (await Selected()).ToString("N"));
    [JSInvokable] public static async Task<object> Sentinel() => new { sentinel = await _test.InvokeAsync<string>("sentinel") };
    private sealed class BytesReader(byte[] loader, byte[] wasm) : IAssetByteReader
    { public Task<byte[]?> ReadAsync(DeclaredAsset a, CancellationToken ct) => Task.FromResult<byte[]?>(a.AssetId == Loader ? loader : wasm); }
    private sealed class Counter(IJSRuntime inner) : IJSRuntime
    {
        public int Imports;
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => InvokeAsync<T>(id, CancellationToken.None, args);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args) { if (id == "import") Imports++; return inner.InvokeAsync<T>(id, ct, args); }
    }
    public sealed class WorkerState { public int Created { get; set; } public int Terminated { get; set; } public int Active { get; set; } }
    private class ForwardBackend(IRuntimeAssetBackend inner) : IRuntimeAssetBackend
    {
        public Task<IRuntimeAssetLease?> TryAcquireAsync() => inner.TryAcquireAsync();
        public Task CreateStageAsync(IRuntimeAssetLease lease, Guid id, int maxStageRecords) => inner.CreateStageAsync(lease, id, maxStageRecords);
        public virtual Task WriteAsync(IRuntimeAssetLease lease, Guid id, int index, byte[] bytes) => inner.WriteAsync(lease, id, index, bytes);
        public Task<StoredAssetBytes?> ReadAsync(Guid id, int index, long length, long max) => inner.ReadAsync(id, index, length, max);
        public Task<string?> CatalogAsync(Guid id, int max) => inner.CatalogAsync(id, max);
        public Task<bool> StageExistsAsync(Guid id) => inner.StageExistsAsync(id);
        public virtual Task PublishAsync(IRuntimeAssetLease lease, Guid id, string metadata, int max) => inner.PublishAsync(lease, id, metadata, max);
        public Task<bool> DiscardAsync(IRuntimeAssetLease lease, Guid id) => inner.DiscardAsync(lease, id);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private sealed class FaultBackend(IRuntimeAssetBackend inner) : ForwardBackend(inner)
    { public override async Task WriteAsync(IRuntimeAssetLease lease, Guid id, int index, byte[] bytes) { await base.WriteAsync(lease, id, index, bytes); throw new RuntimeAssetIoException(RuntimeAssetStatus.QuotaExceeded); } }
    private sealed class DelayBackend(IRuntimeAssetBackend inner) : ForwardBackend(inner)
    {
        public readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task PublishAsync(IRuntimeAssetLease lease, Guid id, string metadata, int max) { await base.PublishAsync(lease, id, metadata, max); Entered.TrySetResult(true); await Release.Task; }
    }
}
