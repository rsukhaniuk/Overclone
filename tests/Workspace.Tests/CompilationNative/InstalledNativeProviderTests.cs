using System.Collections.Immutable;
using System.Text.Json;
using Client.Compilation.Assets;
using Client.Compilation.Native;
using Client.RuntimeAssets;
using Xunit;
namespace Workspace.Tests.CompilationNative;

public sealed class InstalledNativeProviderTests
{
    private static readonly NativeResourcePin[] Pins = LoadPins();
    private static readonly Lazy<NativeResourceInput[]> Inputs = new(() => { var root = Environment.GetEnvironmentVariable("N11_RUNTIME_DIR") ?? throw new InvalidOperationException("Explicit cached test inputs"); return Pins.Select(p => new NativeResourceInput(p.Name, File.ReadAllBytes(Path.Combine(root, "files", p.Name)))).ToArray(); });
    private static NativeResourcePin[] LoadPins() { var root = Environment.GetEnvironmentVariable("N11_RUNTIME_DIR") ?? throw new InvalidOperationException("Explicit cached test inputs"); using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "pins.json"))); return json.RootElement.GetProperty("resources").EnumerateArray().Select(p => new NativeResourcePin(p.GetProperty("name").GetString()!, p.GetProperty("length").GetInt32(), p.GetProperty("sha256").GetString()!)).ToArray(); }
    private static RuntimeAssetBudget Budget(int deadline = 30000) => new(new(378, 256, 16777216, 33554432, 1048576, 256, 256), 262144, 8, 16, deadline);
    private static readonly NativeRuntimeLimits Limits = new(512, 16777216, 33554432);
    private static ImmutableArray<InstalledNativeResource> Mapping() => Pins.Select((p, i) => new InstalledNativeResource(new(new Guid(i + 1, 0, 0, new byte[8])), "runtime/" + p.Name, p)).ToImmutableArray();
    private static AssetInventory Declaration(ImmutableArray<InstalledNativeResource> resources) => AssetInventoryFactory.Create(new(resources.Select(r => new DeclaredAssetInput { AssetId = r.AssetId, RelativePath = r.RelativePath, DeclaredLength = r.Pin.Length, DeclaredHash = new(r.Pin.Sha256), Version = "provider-test" }).ToArray()), Budget().Inventory).Inventory!;
    private static async Task<(InstalledNativeRuntimeMapping Mapping, RuntimeAssetStore Store, Backend Io)> Installed()
    { var resources = Mapping(); var declared = Declaration(resources); var owned = Assert.IsType<AssetVerificationResult.Verified>(await new AssetInventoryVerifier().VerifyAsync(declared, new Reader(resources), Budget().Verification, CancellationToken.None)).Inventory; var io = new Backend(); var store = new RuntimeAssetStore(io); var result = await store.InstallAsync(owned, Budget()); Assert.Equal(RuntimeAssetStatus.Installed, result.Status); return (new(result.InstallationId!.Value, declared.Identity.Digest.Hex, resources), store, io); }
    [Fact]
    public async Task Fresh_owned_full378_package_defensive_copy_and_absence_are_distinct()
    {
        var f = await Installed(); await using var store = f.Store; var provider = new InstalledNativeRuntimeProvider(store, f.Mapping, Budget(), Limits); var first = await provider.ReadPackageAsync(); Assert.Equal(RuntimeAssetStatus.Ready, first.Store.Status); Assert.NotNull(first.Package); var copy = first.Package.Copy("pdflatex.fmt"); copy[0] ^= 1; Assert.Equal(NativeRuntimePackage.FormatHash, NativeRuntimePackage.Digest(first.Package.Copy("pdflatex.fmt")).Hex); int reads = f.Io.Reads; var second = await provider.ReadPackageAsync(); Assert.Equal(378, f.Io.Reads - reads); Assert.NotSame(first.Package, second.Package); Assert.Equal(first.Package.Identity, second.Package!.Identity); Assert.Equal(first.Package.Engine, second.Package.Engine);
        f.Io.Catalog.Clear(); var missing = await provider.ReadPackageAsync(); Assert.Equal(RuntimeAssetStatus.Missing, missing.Store.Status); Assert.Null(missing.Package); Assert.Null(await provider.GetAsync());
    }
    [Theory]
    [InlineData("count")]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("path")]
    [InlineData("hash")]
    [InlineData("length")]
    [InlineData("critical")]
    [InlineData("destination")]
    public async Task Mapping_preflight_rejects_all_entries_before_any_catalog_or_clone(string mode)
    { var resources = Mapping(); var a = resources.ToArray(); a[^1] = mode switch { "id" => a[^1] with { AssetId = a[0].AssetId }, "name" => a[^1] with { Pin = a[^1].Pin with { Name = a[0].Pin.Name } }, "path" => a[^1] with { RelativePath = "foreign/asset" }, "hash" => a[^1] with { Pin = a[^1].Pin with { Sha256 = "BAD" } }, "length" => a[^1] with { Pin = a[^1].Pin with { Length = 16777217 } }, "destination" => a[^1] with { Pin = a[^1].Pin with { Name = NativeRuntimePackage.NativeFormatName }, RelativePath = "runtime/" + NativeRuntimePackage.NativeFormatName }, _ => a[^1] }; if (mode == "critical") { int i = Array.FindIndex(a, r => r.Pin.Name == "pdflatex.fmt"); a[i] = a[i] with { Pin = a[i].Pin with { Sha256 = new string('a', 64) } }; } var io = new Backend(); await using var store = new RuntimeAssetStore(io); var mapping = new InstalledNativeRuntimeMapping(Guid.NewGuid(), new string('a', 64), mode == "count" ? a.Skip(1).ToImmutableArray() : a.ToImmutableArray()); var result = await new InstalledNativeRuntimeProvider(store, mapping, Budget(), Limits).ReadPackageAsync(); Assert.Equal(RuntimeAssetStatus.InvalidInput, result.Store.Status); Assert.Equal(0, io.CatalogReads); Assert.Equal(0, io.Reads); }
    [Theory]
    [InlineData("corrupt")]
    [InlineData("delete")]
    [InlineData("stale-digest")]
    [InlineData("foreign-id")]
    public async Task Corrupt_missing_and_foreign_bound_resources_never_become_package(string mode)
    { var f = await Installed(); await using var store = f.Store; var mapping = f.Mapping; if (mode == "stale-digest") mapping = mapping with { InventoryDigest = new string('a', 64) }; else if (mode == "foreign-id") mapping = mapping with { Resources = mapping.Resources.SetItem(0, mapping.Resources[0] with { AssetId = new(Guid.NewGuid()) }) }; else { int i = Array.FindIndex(Pins, p => p.Name == "pdflatex.fmt"); if (mode == "delete") f.Io.Files.Remove((mapping.InstallationId, i)); else f.Io.Files[(mapping.InstallationId, i)][0] ^= 1; } var result = await new InstalledNativeRuntimeProvider(store, mapping, Budget(), Limits).ReadPackageAsync(); Assert.Null(result.Package); Assert.NotEqual(RuntimeAssetStatus.Ready, result.Store.Status); }
    [Fact]
    public async Task Logical_timeout_does_not_release_actual_pending_installed_read()
    {
        var resources = Mapping(); var io = new Backend { Hold = true }; var store = new RuntimeAssetStore(io); var provider = new InstalledNativeRuntimeProvider(store, new(Guid.NewGuid(), Declaration(resources).Identity.Digest.Hex, resources), Budget(1000), Limits); Exception? primary = null;
        try { var first = provider.ReadPackageAsync(); await io.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); var result = await first.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(RuntimeAssetStatus.TimedOut, result.Store.Status); Assert.False(io.Release.Task.IsCompleted); Assert.False(store.WhenSettled.IsCompleted); Assert.Equal(RuntimeAssetStatus.Busy, (await provider.ReadPackageAsync()).Store.Status); Assert.Equal(1, io.CatalogReads); await store.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (Exception e) { primary = e; throw; }
        finally { io.Release.TrySetResult(true); try { await store.WhenSettled.WaitAsync(TimeSpan.FromSeconds(3)); await store.DisposeAsync(); } catch (Exception cleanup) when (primary is not null) { throw new AggregateException(primary, cleanup); } }
    }
    [Fact]
    public async Task Foreign_store_cancellation_is_not_deadline_or_caller()
    { var resources = Mapping(); var io = new Backend { Foreign = true }; await using var store = new RuntimeAssetStore(io); var result = await new InstalledNativeRuntimeProvider(store, new(Guid.NewGuid(), Declaration(resources).Identity.Digest.Hex, resources), Budget(), Limits).ReadPackageAsync(); Assert.Equal(RuntimeAssetStatus.StorageFailure, result.Store.Status); Assert.Null(result.Package); }
    private sealed class Reader(ImmutableArray<InstalledNativeResource> resources) : IAssetByteReader { public Task<byte[]?> ReadAsync(DeclaredAsset a, CancellationToken ct) => Task.FromResult<byte[]?>(Inputs.Value[Array.FindIndex(resources.ToArray(), r => r.AssetId == a.AssetId)].Bytes); }
    private sealed class Backend : IRuntimeAssetBackend
    {
        public readonly Dictionary<(Guid, int), byte[]> Files = []; public readonly Dictionary<Guid, string> Catalog = []; public int Reads, CatalogReads; public bool Hold, Foreign; public readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IRuntimeAssetLease?> TryAcquireAsync() => Task.FromResult<IRuntimeAssetLease?>(new Lease()); public Task CreateStageAsync(IRuntimeAssetLease l, Guid id, int max) => Task.CompletedTask; public Task WriteAsync(IRuntimeAssetLease l, Guid id, int i, byte[] b) { Files[(id, i)] = b.ToArray(); return Task.CompletedTask; }
        public Task<StoredAssetBytes?> ReadAsync(Guid id, int i, long n, long max) { Reads++; return Task.FromResult(Files.TryGetValue((id, i), out var b) ? new StoredAssetBytes(b.Length, b.ToArray()) : null); }
        public async Task<string?> CatalogAsync(Guid id, int max) { CatalogReads++; if (Foreign) throw new OperationCanceledException("foreign TEST", new CancellationToken(true)); if (Hold) { Entered.TrySetResult(true); await Release.Task; } return Catalog.GetValueOrDefault(id); }
        public Task<bool> StageExistsAsync(Guid id) => Task.FromResult(false); public Task PublishAsync(IRuntimeAssetLease l, Guid id, string json, int max) { Catalog.Add(id, json); return Task.CompletedTask; }
        public Task<bool> DiscardAsync(IRuntimeAssetLease l, Guid id) => Task.FromResult(false); public ValueTask DisposeAsync() => ValueTask.CompletedTask; private sealed class Lease : IRuntimeAssetLease { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
}
