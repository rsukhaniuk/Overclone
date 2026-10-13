using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Client.Workspace.Domain;
using ContentHash = Client.Workspace.Domain.SHA256;

namespace Client.Compilation.Assets;

// Bounded typed failure for the asset canonical writer. Kept separate from B1's
// CompilationInputException and B2a's CompileArtifactException so this slice
// never edits accepted sources.
internal sealed class AssetInventoryException(AssetInventoryIssue issue) : Exception
{ internal AssetInventoryIssue Issue { get; } = issue; }

/// <summary>Versioned unambiguous encoding for the declared-asset inventory
/// identity (distinct domain from B1 input and B2a artifact identities). Same
/// primitive rules as B1's canonical writer (little-endian widths, length-prefixed
/// strict UTF-8, boolean byte, explicit counts) but a distinct domain. Does
/// not share state with B1/B2a. The set of assets and each per-asset
/// source/notice set are encoded in canonical (ordinal-sorted) order, so
/// equivalent inventories share identity regardless of input order. Ordered
/// pass/sequence metadata is intentionally NOT modeled: no completed
/// compile/install exists, so optional structures are omitted rather than invented.</summary>
internal sealed class AssetInventoryCanonical : IDisposable
{
    internal const string Domain = "overclone-asset-inventory-v1";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly long _limit;
    private readonly IncrementalHash? _hash;
    internal AssetInventoryCanonical(long limit, bool hash = false)
    { _limit = limit; if (hash) _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); }
    internal long Length { get; private set; }
    private long Remaining => _limit - Length;
    private void Reserve(long count)
    {
        if (count < 0 || count > Remaining) throw new AssetInventoryException(AssetInventoryIssue.MetadataLimit);
        Length += count;
    }
    internal void Int32(int value)
    {
        Reserve(4);
        Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        _hash?.AppendData(bytes);
    }
    internal void Int64(long value)
    {
        Reserve(8);
        Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        _hash?.AppendData(bytes);
    }
    internal void Boolean(bool value)
    {
        Reserve(1); Span<byte> bytes = stackalloc byte[1]; bytes[0] = value ? (byte)1 : (byte)0;
        _hash?.AppendData(bytes);
    }
    internal void Text(string value, int maxBytes, bool allowEmpty = false)
    {
        if (value is null || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
            throw new AssetInventoryException(AssetInventoryIssue.InvalidMetadata);
        if (value.Length > Remaining - 4) throw new AssetInventoryException(AssetInventoryIssue.MetadataLimit);
        // Cheap char-length lower bound before the UTF-8 byte scan: a field whose
        // char count already exceeds its byte budget cannot fit, so reject without
        // scanning (a one-byte cap cannot scan a huge field under a large aggregate cap).
        if (maxBytes >= 0 && value.Length > maxBytes) throw new AssetInventoryException(AssetInventoryIssue.MetadataLimit);
        int length;
        try { length = Utf8.GetByteCount(value); }
        catch (EncoderFallbackException) { throw new AssetInventoryException(AssetInventoryIssue.MalformedUnicode); }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        { throw new AssetInventoryException(AssetInventoryIssue.Overflow); }
        if (maxBytes >= 0 && length > maxBytes) throw new AssetInventoryException(AssetInventoryIssue.MetadataLimit);
        if (value.Any(char.IsControl)) throw new AssetInventoryException(AssetInventoryIssue.InvalidMetadata);
        Reserve(4L + length);
        if (_hash is not null)
        {
            Span<byte> prefix = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
            _hash.AppendData(prefix); _hash.AppendData(Utf8.GetBytes(value));
        }
    }
    private void Hash(ContentHash hash)
    {
        if (!CompilationCanonical.ValidHash(hash)) throw new AssetInventoryException(AssetInventoryIssue.InvalidHash);
        Text(hash.Hex, -1, allowEmpty: false);
    }
    private void Source(AssetSourceReference source, int maxRelationBytes, int maxKeyBytes)
    {
        if (source is null || source.Relation is null || source.TargetAssetKey is null)
            throw new AssetInventoryException(AssetInventoryIssue.InvalidSourceRef);
        Text(source.Relation, maxRelationBytes);
        Text(source.TargetAssetKey, maxKeyBytes);
    }
    private void Notice(AssetNotice notice, int maxKindBytes, int maxReferenceBytes, int maxTextBytes)
    {
        if (notice is null || notice.Kind is null || notice.ReferencePath is null)
            throw new AssetInventoryException(AssetInventoryIssue.InvalidNotice);
        Text(notice.Kind, maxKindBytes);
        Text(notice.ReferencePath, maxReferenceBytes);
        Boolean(notice.Text is not null);
        if (notice.Text is { } text) Text(text, maxTextBytes, allowEmpty: true);
    }
    // Encodes one declared asset. Caller MUST pass assets in canonical (sorted) order
    // and each asset's sources/notices already sorted, so the digest is order-independent.
    internal void Asset(DeclaredAsset asset, AssetInventoryBudget budget)
    {
        if (asset is null) throw new AssetInventoryException(AssetInventoryIssue.InvalidAsset);
        Text(asset.AssetId.ToString(), -1);
        Text(asset.RelativePath, budget.MaxPathBytes);
        Boolean(asset.MediaKind is not null);
        if (asset.MediaKind is { } media) Text(media, budget.MaxPathBytes);
        if (asset.DeclaredLength < 0) throw new AssetInventoryException(AssetInventoryIssue.InvalidAsset);
        Int64(asset.DeclaredLength);
        Hash(asset.DeclaredHash);
        Text(asset.Version, budget.MaxPathBytes);
        if (asset.Sources.IsDefault || asset.Sources.Length < 0 || asset.Sources.Length > Remaining / 8)
            throw new AssetInventoryException(AssetInventoryIssue.MetadataLimit);
        Int32(asset.Sources.Length);
        foreach (var source in asset.Sources)
            Source(source, budget.MaxSourceRefBytes, budget.MaxPathBytes);
        if (asset.Notices.IsDefault || asset.Notices.Length < 0 || asset.Notices.Length > Remaining / 8)
            throw new AssetInventoryException(AssetInventoryIssue.MetadataLimit);
        Int32(asset.Notices.Length);
        foreach (var notice in asset.Notices)
            Notice(notice, budget.MaxSourceRefBytes, budget.MaxPathBytes, budget.MaxNoticeBytes);
    }
    internal ContentHash Finish() => _hash is not null
        ? ContentHash.FromBytes(_hash.GetHashAndReset()) : throw new InvalidOperationException("Measurement has no digest.");
    public void Dispose() => _hash?.Dispose();
}
