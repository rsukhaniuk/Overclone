using Client.Compilation.Assets;

namespace Client.RuntimeAssets;

// Technical private storage state, not installation approval, permanent durability,
// compiler support, source/license clearance or PWA readiness. No defaults selected.
public enum RuntimeAssetStatus
{
    Installed, Ready, Missing, Incomplete, Corrupt, Unavailable, Busy, InvalidInput,
    QuotaExceeded, PermissionDenied, StorageFailure, Cancelled, TimedOut, Disposed,
    Uncertain, Discarded, NotOwned
}
// Explicit caller TEST/storage policy, not approved product defaults. Inventory
// and catalog-byte caps govern install/read; entry/stage-record caps govern new
// publication/allocation (including retained tombstones), deadline every call.
// Discard allocates/reads no content; it uses ownership/state + deadline only.
public sealed record RuntimeAssetBudget(AssetInventoryBudget Inventory, int MaxCatalogBytes,
    int MaxCatalogEntries, int MaxStageRecords, int DeadlineMs)
{
    public bool IsValid => Inventory is { IsValid: true } && MaxCatalogBytes is > 0 and <= 4194304
        && (MaxCatalogBytes <= 1048576 || Inventory.MaxAssets > 512)
        && MaxCatalogEntries is > 0 and <= 1024 && MaxStageRecords is > 0 and <= 1024 && DeadlineMs is > 0 and <= 30000;
    public AssetVerificationBudget Verification => new(Inventory.MaxAssets, Inventory.MaxAssetBytes, Inventory.MaxTotalBytes);
}
public sealed record RuntimeAssetResult(RuntimeAssetStatus Status, Guid? InstallationId = null,
    string? InventoryDigest = null, VerifiedAssetInventory? Snapshot = null, bool PublishMayHaveChanged = false, RuntimeAssetStatus? Cause = null);
public sealed record StoredAssetBytes(long Length, byte[] Bytes);
public sealed class RuntimeAssetIoException(RuntimeAssetStatus status) : Exception("Runtime asset IO failure.")
{ public RuntimeAssetStatus Status { get; } = status; }

// Backend calls represent ACTUAL operation settlement, not canceled proxy waits.
// Implementations check file/metadata lengths before returning full buffers. The
// store owns this backend and its leases; injected finite fixtures must settle.
public interface IRuntimeAssetLease : IAsyncDisposable { }
public interface IRuntimeAssetBackend : IAsyncDisposable
{
    Task<IRuntimeAssetLease?> TryAcquireAsync();
    Task CreateStageAsync(IRuntimeAssetLease lease, Guid stage, int maxStageRecords);
    Task WriteAsync(IRuntimeAssetLease lease, Guid stage, int index, byte[] bytes);
    Task<StoredAssetBytes?> ReadAsync(Guid stage, int index, long expectedLength, long maxBytes);
    Task<string?> CatalogAsync(Guid stage, int maxMetadataBytes);
    Task<bool> StageExistsAsync(Guid stage);
    Task PublishAsync(IRuntimeAssetLease lease, Guid stage, string metadata, int maxEntries);
    Task<bool> DiscardAsync(IRuntimeAssetLease lease, Guid stage);
}
