using System.Collections.Immutable;
using Client.Compilation.Assets;
using Client.RuntimeAssets;

namespace Client.Compilation.Native;

/// <summary>Explicit trusted caller selection, not document/catalog-selected executable names.</summary>
public sealed record InstalledNativeResource(AssetId AssetId, string RelativePath, NativeResourcePin Pin);
public sealed record InstalledNativeRuntimeMapping(Guid InstallationId, string InventoryDigest, ImmutableArray<InstalledNativeResource> Resources,
    NativeRuntimeProfile Profile = NativeRuntimeProfile.Legacy);
public sealed record InstalledNativeRuntimeRead(RuntimeAssetResult Store, NativeRuntimePackage? Package, NativeRuntimeIssue? Issue);

/// <summary>Unregistered technical provider. Each load re-verifies a fresh owned
/// installed snapshot; package integrity is neither adoption nor source approval.
/// The existing nullable port loses detail; callers can use ReadPackageAsync.</summary>
public sealed class InstalledNativeRuntimeProvider(RuntimeAssetStore store, InstalledNativeRuntimeMapping mapping,
    RuntimeAssetBudget budget, NativeRuntimeLimits limits) : INativeRuntimeProvider
{
    public const int ResourceCount = 378;
    public async Task<InstalledNativeRuntimeRead> ReadPackageAsync(CancellationToken token = default)
    {
        if (!ValidMapping()) return new(new(RuntimeAssetStatus.InvalidInput), null, NativeRuntimeIssue.InvalidInventory);
        var read = await store.ReadAsync(mapping.InstallationId, mapping.InventoryDigest, budget, token).ConfigureAwait(false);
        if (read.Status != RuntimeAssetStatus.Ready || read.Snapshot is not { } snapshot)
            return new(read, null, NativeRuntimeIssue.Unavailable);
        if (snapshot.AssetCount != NativeRuntimeProfiles.InstalledCount(mapping.Profile)) return Corrupt();
        var indices = new Dictionary<AssetId, int>();
        for (int i = 0; i < snapshot.AssetCount; i++) indices.Add(snapshot.DeclaredAssets[i].AssetId, i);
        // ALL actual lengths/bindings before ANY package clone/hash.
        foreach (var resource in mapping.Resources)
            if (!indices.TryGetValue(resource.AssetId, out int index)
                || snapshot.DeclaredAssets[index].RelativePath != resource.RelativePath
                || snapshot.ActualLength(index) != resource.Pin.Length || snapshot.ActualHash(index).Hex != resource.Pin.Sha256)
                return Corrupt();
        var inputs = mapping.Resources.Select(r => new NativeResourceInput(r.Pin.Name, snapshot.ToArray(indices[r.AssetId]))).ToArray();
        var creation = NativeRuntimePackage.Create(inputs, mapping.Resources.Select(r => r.Pin).ToArray(), limits, mapping.Profile);
        return creation.Package is null ? Corrupt(creation.Issue) : new(read, creation.Package, null);
        InstalledNativeRuntimeRead Corrupt(NativeRuntimeIssue? issue = NativeRuntimeIssue.TechnicalHashMismatch)
            => new(read with { Status = RuntimeAssetStatus.Corrupt, Snapshot = null }, null, issue);
    }
    private bool ValidMapping()
    {
        if (store is null || mapping is null || budget is not { IsValid: true } || limits is not { IsValid: true }
            || !NativeRuntimeProfiles.Known(mapping.Profile) || mapping.Profile != limits.Profile
            || mapping.InstallationId == Guid.Empty || !Hex(mapping.InventoryDigest)
            || mapping.Resources.IsDefault || mapping.Resources.Length != NativeRuntimeProfiles.InstalledCount(mapping.Profile)
            || limits.MaxFiles < mapping.Resources.Length || budget.Inventory.MaxAssets < mapping.Resources.Length) return false;
        var ids = new HashSet<AssetId>(); var names = new HashSet<string>(StringComparer.Ordinal); var paths = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var resource in mapping.Resources)
        {
            if (resource is null || resource.AssetId.Value == Guid.Empty || !ids.Add(resource.AssetId) || resource.Pin is not { } p
                || p.Name is not { Length: > 0 and <= 200 } || !p.Name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
                || !names.Add(p.Name) || !paths.Add(resource.RelativePath) || resource.RelativePath != "runtime/" + p.Name
                || !Hex(p.Sha256) || p.Length < 0 || p.Length > limits.MaxFileBytes || p.Length > budget.Inventory.MaxAssetBytes
                || p.Length > limits.MaxTotalBytes - total || p.Length > budget.Inventory.MaxTotalBytes - total) return false;
            total += p.Length;
        }
        if (names.Contains(NativeRuntimePackage.NativeFormatName)) return false;
        return Fixed("engine.js", NativeRuntimeProfiles.CompilerLoaderLength(mapping.Profile), NativeRuntimeProfiles.CompilerLoaderHash(mapping.Profile))
            && Fixed("engine.wasm", NativeRuntimeProfiles.CompilerWasmLength(mapping.Profile), NativeRuntimeProfiles.CompilerWasmHash(mapping.Profile))
            && Fixed(NativeRuntimePackage.FormatResourceName, NativeRuntimeProfiles.FormatLength(mapping.Profile), NativeRuntimeProfiles.FormatHash(mapping.Profile))
            && (mapping.Profile == NativeRuntimeProfile.Legacy || NativeRuntimePackage.ResourceIdentity(mapping.Resources.Select(r => r.Pin)).Hex == NativeRuntimeProfiles.ClosureHash(mapping.Profile));
        bool Fixed(string name, int length, string hash) => mapping.Resources.Any(r => r.Pin.Name == name && r.Pin.Length == length && r.Pin.Sha256 == hash);
    }
    private static bool Hex(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public async Task<NativeRuntimePackage?> GetAsync() => (await ReadPackageAsync().ConfigureAwait(false)).Package;
}
