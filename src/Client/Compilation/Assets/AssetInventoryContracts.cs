using System.Collections.Immutable;
using Client.Workspace.Domain;

namespace Client.Compilation.Assets;

/// <summary>Opaque stable identity for a declared provisioned asset. Distinct from
/// the workspace <see cref="FileId"/>: an asset is not necessarily a workspace
/// file, and no durable revision/workspace binding is implied.</summary>
public readonly record struct AssetId(Guid Value)
{
    public static AssetId New() => new(Guid.NewGuid());
    public static AssetId Parse(Guid value) => new(value);
    public override string ToString() => Value.ToString("N");
}

/// <summary>Bounded typed failures for the declared-asset boundary. Fixed
/// values; never used to echo raw exception text, source paths, credentials,
/// notice text or asset bytes.</summary>
public enum AssetInventoryIssue
{
    InvalidBudget, InvalidInventory, InvalidAsset, InvalidPath, DuplicateAsset, PathConflict,
    InvalidHash, InvalidMetadata, MalformedUnicode, Overflow, MetadataLimit, AssetCountLimit, AssetByteLimit,
    TotalByteLimit, InvalidSourceRef, InvalidNotice, ReadFailure, CorruptBytes, UnavailableAsset,
}

/// <summary>Explicit caller limits for declaring an inventory, not a product
/// provisioning policy or runtime readiness cap. Zero is never unlimited. All
/// fields must be positive and within <see cref="Array.MaxLength"/>. Both the
/// declared-length preflight and the actual-read verification reuse the same
/// per-asset/aggregate byte limits.</summary>
public sealed record AssetInventoryBudget(
    int MaxAssets, int MaxPathBytes, long MaxAssetBytes, long MaxTotalBytes,
    int MaxMetadataBytes, int MaxSourceRefBytes, int MaxNoticeBytes)
{
    // Per-single-array and count fields must fit their runtime representation
    // (one byte[]/int). MaxTotalBytes is an aggregate across SEPARATELY owned
    // per-asset byte arrays, so it is a positive long with no Array.MaxLength clamp
    // (the sum of many arrays may exceed a single array's bound), like B1. This is
    // not approval for huge runtime memory; no product caller/budget is selected here.
    public bool IsValid => MaxAssets > 0 && MaxAssets <= Array.MaxLength
        && MaxPathBytes > 0 && MaxPathBytes <= Array.MaxLength
        && MaxAssetBytes > 0 && MaxAssetBytes <= Array.MaxLength
        && MaxTotalBytes > 0
        && MaxMetadataBytes > 0 && MaxMetadataBytes <= Array.MaxLength
        && MaxSourceRefBytes > 0 && MaxSourceRefBytes <= Array.MaxLength
        && MaxNoticeBytes > 0 && MaxNoticeBytes <= Array.MaxLength;
}

/// <summary>Declarative provenance reference: this asset's source/notice is
/// another declared asset identified by its canonical relative path. Relation is
/// a bounded short label (e.g. "source", "notice", "license"). This is
/// declarative metadata only — NOT a legal clearance, an authoritative
/// dependency relationship, or proof the referenced source was fetched.</summary>
public sealed record AssetSourceReference(string Relation, string TargetAssetKey);

/// <summary>Declarative notice metadata. Kind is a bounded short label (e.g.
/// "license", "notice"). ReferencePath is the canonical relative path of
/// another declared asset (the notice file); Text is an optional bounded
/// description. Declarative only — never an approval or completeness claim.</summary>
public sealed record AssetNotice(string Kind, string ReferencePath, string? Text);

/// <summary>Untrusted declared-asset input as a caller would supply it. Lists
/// may alias caller storage; <see cref="AssetInventoryFactory.Create"/>
/// validates, bounds and privately owns every field before constructing an
/// immutable inventory.</summary>
public sealed record DeclaredAssetInput
{
    public required AssetId AssetId { get; init; }
    public required string RelativePath { get; init; }
    public required long DeclaredLength { get; init; }
    public required SHA256 DeclaredHash { get; init; }
    public required string Version { get; init; }
    public string? MediaKind { get; init; }
    public IReadOnlyList<AssetSourceReference>? Sources { get; init; }
    public IReadOnlyList<AssetNotice>? Notices { get; init; }
}

/// <summary>Untrusted inventory input: a bounded list of declared assets.</summary>
public sealed record AssetInventoryInput(IReadOnlyList<DeclaredAssetInput> Assets);

/// <summary>Validated immutable declared asset. Strings/records may be shared
/// (immutable); the source/notice arrays are privately owned immutable
/// arrays. A validated asset never carries raw untrusted mutable state.</summary>
public sealed record DeclaredAsset(
    AssetId AssetId, string RelativePath, long DeclaredLength, SHA256 DeclaredHash,
    string Version, string? MediaKind, ImmutableArray<AssetSourceReference> Sources,
    ImmutableArray<AssetNotice> Notices);

/// <summary>Deterministic inventory content identity, separate from any engine/
/// bundle/tool/format/adapter hash (it is NOT a reinterpreted B1
/// <c>CompilationDescriptor.Hash</c>). The digest is SHA-256 of the
/// <c>overclone-asset-inventory-v1</c> encoding of every modeled semantic field
/// with canonical ordering for the set of assets and the per-asset
/// source/notice sets. Equal declared inventories share identity regardless of
/// input order; this says nothing about compiler capability, license approval,
/// dependency closure, storage readiness or PWA offline state.</summary>
public sealed record AssetInventoryIdentity(SHA256 Digest, long MetadataByteLength, int AssetCount);

/// <summary>Validated immutable declared inventory. Privately owns the
/// canonically-ordered asset set and its deterministic identity. No public
/// constructor manufactures this; only <see cref="AssetInventoryFactory.Create"/>
/// produces one after full validation. A caller's manifest is NOT proof it lists
/// everything — omitted provenance is visibly absent, never silently approved.</summary>
public sealed class AssetInventory
{
    private AssetInventory(ImmutableArray<DeclaredAsset> assets, AssetInventoryIdentity identity)
    { Assets = assets; Identity = identity; }
    public ImmutableArray<DeclaredAsset> Assets { get; }
    public AssetInventoryIdentity Identity { get; }
    public int AssetCount => Assets.Length;

    internal static AssetInventory Create(ImmutableArray<DeclaredAsset> assets, AssetInventoryIdentity identity)
        => new(assets, identity);
}

public sealed record AssetInventoryCreation(AssetInventory? Inventory, AssetInventoryIssue? Failure);

/// <summary>Bounded reference only; invalid paths/asset ids are not echoed into
/// error messages with raw content.</summary>
public sealed record AssetReference(AssetId AssetId, string? RelativePath);

/// <summary>Read-only injected byte-reader port. The verifier uses only this
/// seam; it never writes, downloads, repairs, deletes, acquires a lock, or
/// subscribes to a global event. Returned arrays are untrusted aliased buffers:
/// the verifier copies before validation so later reader/caller mutation cannot
/// change the receipt. Byte content is opaque (no UTF-8/PDF/WASM parse).</summary>
public interface IAssetByteReader
{
    Task<byte[]?> ReadAsync(DeclaredAsset asset, CancellationToken cancellationToken);
}

/// <summary>Explicit caller limits for the runtime byte-verification pass only. This
/// is a DISTINCT, smaller budget from <see cref="AssetInventoryBudget"/>: it
/// omits metadata/path/source/notice fields, which were already enforced at
/// inventory creation and are irrelevant to reading bytes. Every field here IS
/// used by the verifier (count, per-asset and aggregate byte limits), so no
/// supplied limit is silently ignored. Zero is never unlimited.</summary>
public sealed record AssetVerificationBudget(int MaxAssets, long MaxAssetBytes, long MaxTotalBytes)
{
    // MaxAssetBytes is a per-single-array bound; MaxTotalBytes is an aggregate across
    // separately owned per-asset byte arrays, so it is a positive long with no
    // Array.MaxLength clamp (same rule as AssetInventoryBudget.MaxTotalBytes).
    public bool IsValid => MaxAssets > 0 && MaxAssets <= Array.MaxLength
        && MaxAssetBytes > 0 && MaxAssetBytes <= Array.MaxLength
        && MaxTotalBytes > 0;
}

public abstract record AssetVerificationResult
{
    private AssetVerificationResult() { }
    public sealed record Verified(VerifiedAssetInventory Inventory) : AssetVerificationResult;
    /// <summary>Explicit not-ready outcome for a declared-but-EMPTY inventory
    /// (zero assets). An empty inventory cannot silently become a meaningful
    /// verified receipt or readiness (invariant 8); a declared empty FILE
    /// remains valid and verifiable inside a NONEMPTY inventory.</summary>
    public sealed record Empty : AssetVerificationResult;
    public sealed record InvalidInput(AssetInventoryIssue Issue) : AssetVerificationResult;
    public sealed record QuotaExceeded(AssetInventoryIssue Issue, AssetReference? Asset = null) : AssetVerificationResult
    { public string SafeMessage => "A declared or actual asset size exceeds the explicit budget; no receipt was produced."; }
    public sealed record Unavailable(AssetInventoryIssue Issue, AssetReference? Asset = null) : AssetVerificationResult
    { public string SafeMessage => "Declared asset bytes could not be read or verified; no receipt was produced."; }
    public sealed record Cancelled : AssetVerificationResult;
}
