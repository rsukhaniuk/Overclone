using System.Collections.Immutable;
using Client.Workspace.Domain;
using ContentHash = Client.Workspace.Domain.SHA256;

namespace Client.Compilation.Assets;

/// <summary>Successful whole-inventory byte-verification receipt. Retains the
/// EXACT immutable declared inventory and a privately owned full byte set used
/// for verification, with per-asset actual hash/length and the deterministic
/// inventory identity. Backing byte arrays never escape this assembly:
/// <see cref="Bytes"/> returns a read-only span, <see cref="ToArray"/> copies.
///
/// Success means ONLY "all declared bytes verified for this owned capture". It is
/// NOT "compiler supported", "license approved", "dependency closure complete",
/// "stored forever" or "PWA offline-ready". Source storage mutation or
/// eviction after verification cannot retarget this receipt (bytes are owned
/// here); a future consumer still needs current installed-set lifecycle checks.
/// Receipt lifetime and storage readiness differ. No compiled artifact or
/// durable revision link is attached and no compiler execution is claimed.</summary>
public sealed class VerifiedAssetInventory
{
    private readonly AssetInventory _inventory;
    private readonly byte[][] _bytes;        // Owned, one per canonically-ordered asset. Never exposed.
    private readonly ImmutableArray<SHA256> _actualHashes;
    private readonly ImmutableArray<long> _actualLengths;

    internal VerifiedAssetInventory(AssetInventory inventory, byte[][] bytes,
        ImmutableArray<SHA256> actualHashes, ImmutableArray<long> actualLengths)
    {
        _inventory = inventory; _bytes = bytes;
        _actualHashes = actualHashes; _actualLengths = actualLengths;
    }

    /// <summary>The exact immutable declared inventory that was verified.</summary>
    public AssetInventory Inventory => _inventory;
    public AssetInventoryIdentity Identity => _inventory.Identity;
    public int AssetCount => _inventory.Assets.Length;
    public ImmutableArray<DeclaredAsset> DeclaredAssets => _inventory.Assets;

    /// <summary>Read-only view of the owned bytes for the asset at index
    /// <paramref name="index"/> (canonical order). Backing array never escapes.</summary>
    public ReadOnlySpan<byte> Bytes(int index) => _bytes[index];
    /// <summary>Defensive copy of the owned bytes for the asset at index
    /// <paramref name="index"/>. Never returns the backing array.</summary>
    public byte[] ToArray(int index) => _bytes[index].ToArray();
    public SHA256 ActualHash(int index) => _actualHashes[index];
    public long ActualLength(int index) => _actualLengths[index];
}
