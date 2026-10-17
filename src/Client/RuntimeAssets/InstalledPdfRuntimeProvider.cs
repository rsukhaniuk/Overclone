using Client.Compilation.Assets;
using Client.Pdf;

namespace Client.RuntimeAssets;

// Explicit trusted caller mapping, NEVER selected by PDF/document/worker metadata.
public sealed record InstalledPdfRuntimeMapping(Guid InstallationId, string InventoryDigest,
    AssetId LoaderAssetId, long LoaderLength, string LoaderHash,
    AssetId WasmAssetId, long WasmLength, string WasmHash);
public sealed record InstalledPdfRuntimeRead(RuntimeAssetResult Store, PdfRuntimePackage? Package);

public sealed class InstalledPdfRuntimeProvider(RuntimeAssetStore store, InstalledPdfRuntimeMapping mapping, RuntimeAssetBudget budget) : IPdfRuntimeProvider
{
    public async Task<InstalledPdfRuntimeRead> ReadPackageAsync(CancellationToken token = default)
    {
        // BOTH lengths, IDs and hashes checked before any store work/owned clone.
        if (mapping is null || mapping.InstallationId == Guid.Empty || !RuntimeAssetCatalog.HashValid(mapping.InventoryDigest)
            || mapping.LoaderAssetId.Value == Guid.Empty || mapping.WasmAssetId.Value == Guid.Empty || mapping.LoaderAssetId == mapping.WasmAssetId
            || mapping.LoaderLength is < 1 or > 1048576 || mapping.WasmLength is < 8 or > 16777216
            || !RuntimeAssetCatalog.HashValid(mapping.LoaderHash) || !RuntimeAssetCatalog.HashValid(mapping.WasmHash))
            return new(new(RuntimeAssetStatus.InvalidInput), null);
        var read = await store.ReadAsync(mapping.InstallationId, mapping.InventoryDigest, budget, token).ConfigureAwait(false);
        if (read.Status != RuntimeAssetStatus.Ready || read.Snapshot is null) return new(read, null);
        var snapshot = read.Snapshot; int loader = -1, wasm = -1;
        for (int i = 0; i < snapshot.AssetCount; i++)
        {
            if (snapshot.DeclaredAssets[i].AssetId == mapping.LoaderAssetId) loader = i;
            if (snapshot.DeclaredAssets[i].AssetId == mapping.WasmAssetId) wasm = i;
        }
        if (loader < 0 || wasm < 0 || snapshot.ActualLength(loader) != mapping.LoaderLength || snapshot.ActualLength(wasm) != mapping.WasmLength
            || snapshot.ActualHash(loader).Hex != mapping.LoaderHash || snapshot.ActualHash(wasm).Hex != mapping.WasmHash)
            return new(read with { Status = RuntimeAssetStatus.Corrupt, Snapshot = null }, null);
        // This fresh successfully reverified OWNED snapshot is the package source.
        // Package/hash agreement is technical identity, not approval/adoption.
        var package = new PdfRuntimePackage(snapshot.ToArray(loader), snapshot.ToArray(wasm), mapping.LoaderHash, mapping.WasmHash);
        if (!package.IsValid) return new(read with { Status = RuntimeAssetStatus.Corrupt, Snapshot = null }, null);
        return new(read, package);
    }
    public async ValueTask<PdfRuntimePackage?> GetAsync(CancellationToken cancellationToken)
        => (await ReadPackageAsync(cancellationToken).ConfigureAwait(false)).Package;
    // Existing PDF API maps null to RuntimeAbsent even for known corruption.
    // Consumers MUST use ReadPackageAsync for richer Missing/Incomplete/Corrupt/
    // permission/quota/unavailable diagnostics; do not label these literally absent.
}
