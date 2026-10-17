using System.Text.Json.Nodes;
using Client.Compilation.Assets;
using Client.RuntimeAssets;
using Xunit;
using Hash = Client.Workspace.Domain.SHA256;

namespace Workspace.Tests.RuntimeAssets;

// Managed contract/lifetime tests ONLY; real storage/cold evidence is separate.
public sealed class RuntimeAssetStoreTests
{
    private static RuntimeAssetBudget Budget(int deadline = 30000) => new(new(4, 256, 8388608, 16777216, 16384, 256, 256), 32768, 8, 8, deadline);
    private static readonly AssetId LoaderId = new(new Guid("11111111-1111-1111-1111-111111111111")), WasmId = new(new Guid("22222222-2222-2222-2222-222222222222"));
    private static async Task<VerifiedAssetInventory> Receipt()
    {
        byte[] loader = [47, 47, 32, 120], wasm = [0, 97, 115, 109, 1, 0, 0, 0];
        var inventory = AssetInventoryFactory.Create(new(new[]
        {
            new DeclaredAssetInput { AssetId = LoaderId, RelativePath = "runtime/loader.js", DeclaredLength = loader.Length, DeclaredHash = Hash.FromBytes(System.Security.Cryptography.SHA256.HashData(loader)), Version = "test1" },
            new DeclaredAssetInput { AssetId = WasmId, RelativePath = "runtime/engine.wasm", DeclaredLength = wasm.Length, DeclaredHash = Hash.FromBytes(System.Security.Cryptography.SHA256.HashData(wasm)), Version = "test1" }
        }), Budget().Inventory).Inventory!;
        var result = await new AssetInventoryVerifier().VerifyAsync(inventory, new Reader(asset => asset.AssetId == LoaderId ? loader : wasm), Budget().Verification, CancellationToken.None);
        return Assert.IsType<AssetVerificationResult.Verified>(result).Inventory;
    }
    private sealed class Reader(Func<DeclaredAsset, byte[]> read) : IAssetByteReader
    { public Task<byte[]?> ReadAsync(DeclaredAsset a, CancellationToken token) => Task.FromResult<byte[]?>(read(a)); }
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Content_first_publish_fresh_revalidation_and_owned_snapshot()
    {
        var receipt = await Receipt(); var io = new Backend(); await using var store = new RuntimeAssetStore(io);
        var installed = await store.InstallAsync(receipt, Budget()); Assert.Equal(RuntimeAssetStatus.Installed, installed.Status);
        Assert.Equal(new[] { "acquire", "create", "write0", "write1", "read0", "read1", "publish", "release" }, io.Events);
        await using var reopen = new RuntimeAssetStore(new Backend(io.Data));
        var fresh = await reopen.ReadAsync(installed.InstallationId!.Value, receipt.Identity.Digest.Hex, Budget()); Assert.Equal(RuntimeAssetStatus.Ready, fresh.Status);
        Assert.NotSame(receipt, fresh.Snapshot); var copy = fresh.Snapshot!.ToArray(0); copy[0] ^= 1;
        Assert.Equal(receipt.Bytes(0).ToArray(), fresh.Snapshot.ToArray(0)); Assert.NotEqual(receipt.Identity.Digest.Hex, installed.InstallationId.Value.ToString("N"));
    }
    [Theory]
    [InlineData("write")]
    [InlineData("read")]
    [InlineData("publish")]
    public async Task New_failure_never_invalidates_previous_installed_set(string failure)
    {
        var receipt = await Receipt(); var io = new Backend(); await using var store = new RuntimeAssetStore(io); var old = await store.InstallAsync(receipt, Budget());
        io.Failure = failure; var failed = await store.InstallAsync(receipt, Budget()); Assert.NotEqual(RuntimeAssetStatus.Installed, failed.Status); Assert.Null(failed.Snapshot);
        io.Failure = ""; Assert.Equal(RuntimeAssetStatus.Ready, (await store.ReadAsync(old.InstallationId!.Value, receipt.Identity.Digest.Hex, Budget())).Status);
        Assert.Equal(RuntimeAssetStatus.Incomplete, (await store.ReadAsync(failed.InstallationId!.Value, receipt.Identity.Digest.Hex, Budget())).Status);
        Assert.Equal(RuntimeAssetStatus.Discarded, (await store.DiscardAsync(failed.InstallationId.Value, Budget())).Status);
        Assert.Equal(RuntimeAssetStatus.NotOwned, (await store.DiscardAsync(old.InstallationId.Value, Budget())).Status);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Catalog_metadata_is_untrusted_and_bounded_before_asset_reads(int kind)
    {
        var receipt = await Receipt(); var io = new Backend(); await using var store = new RuntimeAssetStore(io); var installed = await store.InstallAsync(receipt, Budget()); var id = installed.InstallationId!.Value;
        var json = JsonNode.Parse(io.Data.Catalog[id])!;
        switch (kind) { case 0: json["version"] = 2; break; case 1: json["inventoryDigest"] = new string('0', 64); break; case 2: json["assets"]![0]!["declaredLength"] = long.MaxValue; break; case 3: json["assets"]![0]!["relativePath"] = "../outside"; break; case 4: json["unexpected"] = true; break; }
        io.Data.Catalog[id] = kind == 5 ? new string('x', 32769) : kind == 6 ? io.Data.Catalog[id].Replace("\"version\":1", "\"version\":1,\"version\":1") : json.ToJsonString(); int before = io.Reads;
        Assert.Equal(RuntimeAssetStatus.Corrupt, (await store.ReadAsync(id, receipt.Identity.Digest.Hex, Budget())).Status); Assert.Equal(before, io.Reads);
    }
    [Fact]
    public async Task Missing_incomplete_wrong_expected_identity_and_corrupt_content_remain_distinct()
    {
        var receipt = await Receipt(); var io = new Backend(); await using var store = new RuntimeAssetStore(io);
        Assert.Equal(RuntimeAssetStatus.Missing, (await store.ReadAsync(Guid.NewGuid(), receipt.Identity.Digest.Hex, Budget())).Status);
        var installed = await store.InstallAsync(receipt, Budget()); var id = installed.InstallationId!.Value;
        Assert.Equal(RuntimeAssetStatus.Corrupt, (await store.ReadAsync(id, new string('0', 64), Budget())).Status);
        io.Data.Files[id][0][0] ^= 1; Assert.Equal(RuntimeAssetStatus.Corrupt, (await store.ReadAsync(id, receipt.Identity.Digest.Hex, Budget())).Status);
        io.Data.Files[id].Remove(0); Assert.Equal(RuntimeAssetStatus.Incomplete, (await store.ReadAsync(id, receipt.Identity.Digest.Hex, Budget())).Status);
        io.Data.Catalog.Remove(id); Assert.Equal(RuntimeAssetStatus.Incomplete, (await store.ReadAsync(id, receipt.Identity.Digest.Hex, Budget())).Status);
    }
    [Theory]
    [InlineData("write")]
    [InlineData("read")]
    public async Task Logical_timeout_retains_actual_IO_and_lease_until_settlement(string phase)
    {
        var receipt = await Receipt(); var io = new Backend { HoldPhase = phase }; var store = new RuntimeAssetStore(io);
        var task = store.InstallAsync(receipt, Budget(1000));
        try
        {
            await io.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); var timed = await task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(RuntimeAssetStatus.TimedOut, timed.Status);
            Assert.False(io.Hold.Task.IsCompleted); Assert.True(io.Data.Locked); Assert.False(store.WhenSettled.IsCompleted);
            Assert.Equal(RuntimeAssetStatus.Busy, (await store.InstallAsync(receipt, Budget())).Status); Assert.Equal(1, io.Acquires);
            Assert.Equal(RuntimeAssetStatus.Busy, (await store.DiscardAsync(timed.InstallationId!.Value, Budget())).Status);
            await store.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)); Assert.True(io.Data.Locked); Assert.False(io.Hold.Task.IsCompleted);
            Assert.Equal(RuntimeAssetStatus.Disposed, (await store.InstallAsync(receipt, Budget())).Status);
        }
        finally { io.Hold.TrySetResult(true); await store.WhenSettled.WaitAsync(TimeSpan.FromSeconds(3)); await store.DisposeAsync(); }
        Assert.False(io.Data.Locked); Assert.Empty(io.Data.Catalog); Assert.Equal(1, io.Disposals);
    }
    [Fact]
    public async Task Late_acquired_lease_is_released_after_caller_cancel_without_new_work()
    {
        var receipt = await Receipt(); var io = new Backend { HoldPhase = "acquire" }; var store = new RuntimeAssetStore(io); using var caller = new CancellationTokenSource();
        var first = store.InstallAsync(receipt, Budget(), caller.Token);
        try
        {
            await io.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); caller.Cancel(); Assert.Equal(RuntimeAssetStatus.Cancelled, (await first.WaitAsync(TimeSpan.FromSeconds(3))).Status);
            Assert.False(io.Hold.Task.IsCompleted); Assert.Equal(RuntimeAssetStatus.Busy, (await store.InstallAsync(receipt, Budget())).Status); Assert.Equal(1, io.Acquires);
        }
        finally { io.Hold.TrySetResult(true); await store.WhenSettled.WaitAsync(TimeSpan.FromSeconds(3)); await store.DisposeAsync(); }
        Assert.Contains("release", io.Events); Assert.DoesNotContain("create", io.Events); Assert.False(io.Data.Locked);
    }
    [Fact]
    public async Task Publish_after_cancel_is_uncertain_until_fresh_read_and_discard_cannot_revoke()
    {
        var receipt = await Receipt(); var io = new Backend { HoldPhase = "publish" }; var store = new RuntimeAssetStore(io); using var caller = new CancellationTokenSource();
        var first = store.InstallAsync(receipt, Budget(), caller.Token);
        try
        {
            await io.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); caller.Cancel(); var uncertain = await first.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(RuntimeAssetStatus.Uncertain, uncertain.Status); Assert.True(uncertain.PublishMayHaveChanged); Assert.False(io.Hold.Task.IsCompleted); Assert.True(io.Data.Locked);
            Assert.Equal(RuntimeAssetStatus.Busy, (await store.DiscardAsync(uncertain.InstallationId!.Value, Budget())).Status);
            await using var independent = new RuntimeAssetStore(new Backend(io.Data)); Assert.Equal(RuntimeAssetStatus.Ready, (await independent.ReadAsync(uncertain.InstallationId.Value, receipt.Identity.Digest.Hex, Budget())).Status);
            io.Hold.TrySetResult(true); await store.WhenSettled.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(RuntimeAssetStatus.NotOwned, (await store.DiscardAsync(uncertain.InstallationId.Value, Budget())).Status);
        }
        finally { io.Hold.TrySetResult(true); await store.WhenSettled.WaitAsync(TimeSpan.FromSeconds(3)); await store.DisposeAsync(); }
    }
    [Theory]
    [InlineData("acquire")]
    [InlineData("read")]
    public async Task Foreign_OCE_with_unexpired_deadline_is_provider_failure(string phase)
    {
        var receipt = await Receipt(); var io = new Backend { Foreign = phase }; await using var store = new RuntimeAssetStore(io); using var caller = new CancellationTokenSource();
        var result = await store.InstallAsync(receipt, Budget(), caller.Token).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(RuntimeAssetStatus.StorageFailure, result.Status); Assert.False(caller.IsCancellationRequested); Assert.Null(result.Snapshot);
    }
    [Fact]
    public async Task Invalid_budgets_and_receipt_lengths_reject_before_IO_or_large_clones()
    {
        var receipt = await Receipt(); var io = new Backend(); await using var store = new RuntimeAssetStore(io);
        Assert.Equal(RuntimeAssetStatus.InvalidInput, (await store.InstallAsync(receipt, Budget() with { DeadlineMs = 0 })).Status);
        Assert.Equal(RuntimeAssetStatus.QuotaExceeded, (await store.InstallAsync(receipt, Budget() with { Inventory = Budget().Inventory with { MaxAssetBytes = 1 } })).Status);
        Assert.Empty(io.Events);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Global_storage_and_metadata_caps_preserve_existing_sets(int mode)
    {
        var receipt = await Receipt(); var io = new Backend(); await using var store = new RuntimeAssetStore(io);
        var old = await store.InstallAsync(receipt, Budget());
        var budget = mode == 0 ? Budget() with { MaxStageRecords = 1 } : mode == 1 ? Budget() with { MaxCatalogEntries = 1 } : Budget() with { MaxCatalogBytes = 1 };
        var result = await store.InstallAsync(receipt, budget); Assert.NotEqual(RuntimeAssetStatus.Installed, result.Status);
        Assert.True(result.Status == RuntimeAssetStatus.QuotaExceeded || result.Status == RuntimeAssetStatus.Uncertain && result.Cause == RuntimeAssetStatus.QuotaExceeded);
        Assert.Equal(RuntimeAssetStatus.Ready, (await store.ReadAsync(old.InstallationId!.Value, receipt.Identity.Digest.Hex, Budget())).Status);
        Assert.Single(io.Data.Catalog);
    }
    [Fact]
    public async Task Trusted_runtime_mapping_revalidates_and_preserves_defensive_ownership()
    {
        var receipt = await Receipt(); var io = new Backend(); await using var store = new RuntimeAssetStore(io); var install = await store.InstallAsync(receipt, Budget());
        var loader = receipt.DeclaredAssets.Single(a => a.AssetId == LoaderId); var wasm = receipt.DeclaredAssets.Single(a => a.AssetId == WasmId);
        var mapping = new InstalledPdfRuntimeMapping(install.InstallationId!.Value, receipt.Identity.Digest.Hex, LoaderId, loader.DeclaredLength, loader.DeclaredHash.Hex, WasmId, wasm.DeclaredLength, wasm.DeclaredHash.Hex);
        var provider = new InstalledPdfRuntimeProvider(store, mapping, Budget()); var result = await provider.ReadPackageAsync(); Assert.Equal(RuntimeAssetStatus.Ready, result.Store.Status); Assert.True(result.Package!.IsValid);
        byte[] copy = result.Package.CopyWasm(); copy[0] ^= 1; Assert.True(result.Package.IsValid);
        var wrong = await new InstalledPdfRuntimeProvider(store, mapping with { LoaderHash = new string('0', 64) }, Budget()).ReadPackageAsync(); Assert.Equal(RuntimeAssetStatus.Corrupt, wrong.Store.Status); Assert.Null(wrong.Package);
        io.Data.Files[mapping.InstallationId][0][0] ^= 1; Assert.Null(await provider.GetAsync(CancellationToken.None)); Assert.Equal(RuntimeAssetStatus.Corrupt, (await provider.ReadPackageAsync()).Store.Status);
    }

    private sealed class State
    { public readonly Dictionary<Guid, Dictionary<int, byte[]>> Files = []; public readonly Dictionary<Guid, string> Catalog = []; public readonly HashSet<Guid> StageRecords = []; public bool Locked; }
    private sealed class Backend(State? state = null) : IRuntimeAssetBackend
    {
        public State Data { get; } = state ?? new(); public List<string> Events = []; public int Acquires, Reads, Disposals;
        public string Failure = "", Foreign = "", HoldPhase = ""; public readonly TaskCompletionSource<bool> Entered = Signal<bool>(), Hold = Signal<bool>();
        private bool _held;
        private async Task Before(string phase)
        {
            if (Foreign == phase) throw new OperationCanceledException("foreign TEST token", new CancellationToken(true));
            if (Failure == phase) throw new RuntimeAssetIoException(phase == "read" ? RuntimeAssetStatus.Corrupt : RuntimeAssetStatus.QuotaExceeded);
            if (HoldPhase == phase && !_held) { _held = true; Entered.TrySetResult(true); await Hold.Task; }
        }
        public async Task<IRuntimeAssetLease?> TryAcquireAsync()
        { Acquires++; Events.Add("acquire"); await Before("acquire"); if (Data.Locked) return null; Data.Locked = true; return new Lease(this); }
        public Task CreateStageAsync(IRuntimeAssetLease lease, Guid id, int maxStageRecords) { Events.Add("create"); if (Data.StageRecords.Count >= maxStageRecords) throw new RuntimeAssetIoException(RuntimeAssetStatus.QuotaExceeded); Data.StageRecords.Add(id); Data.Files.Add(id, []); return Task.CompletedTask; }
        public async Task WriteAsync(IRuntimeAssetLease lease, Guid id, int index, byte[] bytes) { Events.Add("write" + index); await Before("write"); Data.Files[id][index] = bytes.ToArray(); }
        public async Task<StoredAssetBytes?> ReadAsync(Guid id, int index, long expectedLength, long maxBytes)
        { Reads++; Events.Add("read" + index); await Before("read"); return Data.Files.TryGetValue(id, out var files) && files.TryGetValue(index, out var bytes) ? new(bytes.LongLength, bytes.ToArray()) : null; }
        public Task<string?> CatalogAsync(Guid id, int maximum)
        { if (Data.Catalog.TryGetValue(id, out var value)) { if (value.Length > maximum) throw new RuntimeAssetIoException(RuntimeAssetStatus.Corrupt); return Task.FromResult<string?>(value); } return Task.FromResult<string?>(null); }
        public Task<bool> StageExistsAsync(Guid id) => Task.FromResult(Data.Files.ContainsKey(id));
        public async Task PublishAsync(IRuntimeAssetLease lease, Guid id, string value, int maximum)
        { Events.Add("publish"); if (Failure == "publish") throw new RuntimeAssetIoException(RuntimeAssetStatus.QuotaExceeded); if (Data.Catalog.Count >= maximum) throw new RuntimeAssetIoException(RuntimeAssetStatus.QuotaExceeded); Data.Catalog.Add(id, value); await Before("publish"); }
        public Task<bool> DiscardAsync(IRuntimeAssetLease lease, Guid id) => Task.FromResult(!Data.Catalog.ContainsKey(id) && Data.Files.Remove(id));
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
        private sealed class Lease(Backend owner) : IRuntimeAssetLease
        { public ValueTask DisposeAsync() { owner.Events.Add("release"); owner.Data.Locked = false; return ValueTask.CompletedTask; } }
    }
}
