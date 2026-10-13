using System.Collections.Immutable;
using System.Security.Cryptography;
using ContentHash = Client.Workspace.Domain.SHA256;

namespace Client.Compilation.Assets;

/// <summary>Verifies that the declared bytes of an immutable inventory were
/// actually read (via the injected read-only port) and matched their declared
/// length/SHA-256. Whole-inventory, all-or-nothing: any missing, corrupt,
/// unavailable or over-budget asset yields no receipt. Two-phase: Phase A
/// re-preflights declared sizes/aggregate against the budget BEFORE any read;
/// Phase B reads via the injected reader only, copies returned mutable buffers
/// privately BEFORE validation (so later reader/caller mutation cannot change
/// the receipt), rejects oversized reads BEFORE the duplicate allocation/hash,
/// and checks actual length then hash. Byte content is opaque (no UTF-8/PDF/WASM
/// parse).
///
/// A declared-but-EMPTY inventory (zero assets) returns an explicit
/// <see cref="AssetVerificationResult.Empty"/> not-ready outcome — it never
/// becomes a meaningful <see cref="AssetVerificationResult.Verified"/> receipt
/// (invariant 8). A declared empty FILE remains valid and verifiable inside a
/// NONEMPTY inventory.
///
/// Cancellation settles even if the reader ignores its token
/// (<see cref="Task.WaitAsync(CancellationToken)"/>); the orphaned read is not
/// awaited. Cancellation is re-checked after the awaited reads and before the
/// receipt is published. This verifier owns no global event subscription, writer
/// lock or injected-service lifetime, is not disposable, and NEVER writes,
/// downloads, repairs or deletes assets. Existing editing/saving/analysis/review
/// are unaffected if verification fails or assets are absent.</summary>
public sealed class AssetInventoryVerifier
{
    /// <summary>Verify every declared asset's bytes against its declared
    /// length/hash using <paramref name="reader"/> under the explicit
    /// <paramref name="budget"/>. Returns <see cref="AssetVerificationResult.Verified"/>
    /// only when ALL declared bytes were read and matched;
    /// <see cref="AssetVerificationResult.Empty"/> for a zero-asset inventory;
    /// otherwise a typed failure. No partial success.</summary>
    public async Task<AssetVerificationResult> VerifyAsync(
        AssetInventory inventory, IAssetByteReader reader, AssetVerificationBudget budget, CancellationToken cancellationToken)
    {
        if (inventory is null) return new AssetVerificationResult.InvalidInput(AssetInventoryIssue.InvalidInventory);
        if (reader is null) return new AssetVerificationResult.InvalidInput(AssetInventoryIssue.InvalidInventory);
        if (budget is null || !budget.IsValid) return new AssetVerificationResult.InvalidInput(AssetInventoryIssue.InvalidBudget);
        if (cancellationToken.IsCancellationRequested) return new AssetVerificationResult.Cancelled();

        var assets = inventory.Assets;
        int count = assets.Length;

        // Invariant 8: an empty inventory is never a meaningful verified receipt.
        // (A declared empty FILE inside a NONEMPTY inventory is still verified below.)
        if (count == 0) return new AssetVerificationResult.Empty();

        // Phase A: declared-size/count preflight BEFORE any read/call. Every
        // field of AssetVerificationBudget is enforced (count, per-asset, aggregate),
        // so no supplied limit is silently ignored. Zero reads occur here on failure.
        if (count > budget.MaxAssets)
            return new AssetVerificationResult.QuotaExceeded(AssetInventoryIssue.AssetCountLimit);
        long declaredTotal = 0;
        for (int i = 0; i < count; i++)
        {
            var asset = assets[i];
            var file = new AssetReference(asset.AssetId, asset.RelativePath);
            if (asset.DeclaredLength > budget.MaxAssetBytes)
                return new AssetVerificationResult.QuotaExceeded(AssetInventoryIssue.AssetByteLimit, file);
            if (asset.DeclaredLength > budget.MaxTotalBytes - declaredTotal)
                return new AssetVerificationResult.QuotaExceeded(AssetInventoryIssue.TotalByteLimit, file);
            declaredTotal = checked(declaredTotal + asset.DeclaredLength);
        }

        // Phase B: read, own, verify. All-or-nothing: the first failure yields no receipt.
        var owned = new byte[count][];
        var hashes = ImmutableArray.CreateBuilder<ContentHash>(count);
        var lengths = ImmutableArray.CreateBuilder<long>(count);
        long actualTotal = 0;
        for (int i = 0; i < count; i++)
        {
            if (cancellationToken.IsCancellationRequested) return new AssetVerificationResult.Cancelled();
            var asset = assets[i];
            var file = new AssetReference(asset.AssetId, asset.RelativePath);

            byte[]? bytes;
            try
            {
                // Only the caller token is passed to the foreign port. WaitAsync(ct)
                // observes cancellation even if the reader ignores its token; the
                // orphaned read is not awaited (the reader owns its own cleanup).
                bytes = await reader.ReadAsync(asset, cancellationToken).WaitAsync(cancellationToken);
            }
            // The cancellation outcome must represent the CALLER token: if the
            // foreign reader canceled its own unrelated token while the caller token
            // is not canceled, that is a read failure, not a caller cancellation.
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { return new AssetVerificationResult.Cancelled(); }
            catch (OperationCanceledException)
            { return new AssetVerificationResult.Unavailable(AssetInventoryIssue.ReadFailure, file); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { return new AssetVerificationResult.Unavailable(AssetInventoryIssue.ReadFailure, file); }
            // Check caller cancellation immediately after the await, BEFORE examining
            // null/corrupt/read length/hash: a read that returned while the caller
            // token was canceled (narrow race) must still be Cancelled, never a
            // null/corrupt failure, and never publish a Verified receipt.
            if (cancellationToken.IsCancellationRequested) return new AssetVerificationResult.Cancelled();
            if (bytes is null) return new AssetVerificationResult.Unavailable(AssetInventoryIssue.UnavailableAsset, file);

            // Actual-size checks BEFORE the duplicate allocation/hash, so an
            // oversized hostile read is rejected without copying or hashing it.
            if (bytes.LongLength > budget.MaxAssetBytes)
                return new AssetVerificationResult.QuotaExceeded(AssetInventoryIssue.AssetByteLimit, file);
            if (bytes.LongLength > budget.MaxTotalBytes - actualTotal)
                return new AssetVerificationResult.QuotaExceeded(AssetInventoryIssue.TotalByteLimit, file);
            actualTotal = checked(actualTotal + bytes.LongLength);

            if (bytes.LongLength != asset.DeclaredLength)
                return new AssetVerificationResult.Unavailable(AssetInventoryIssue.CorruptBytes, file);

            // Copy BEFORE hashing: the returned array is an untrusted aliased buffer
            // the reader/caller may mutate after we return. The owned copy is the
            // only input to the hash and the only bytes retained in the receipt.
            var copy = bytes.ToArray();
            var hash = ContentHash.FromBytes(System.Security.Cryptography.SHA256.HashData(copy));
            if (hash != asset.DeclaredHash)
                return new AssetVerificationResult.Unavailable(AssetInventoryIssue.CorruptBytes, file);

            owned[i] = copy;
            hashes.Add(hash);
            lengths.Add(copy.LongLength);
        }

        // Re-check cancellation after the awaited reads and before publication: a
        // read may have completed while cancellation was requested mid-loop, and
        // a verified receipt must not be published after cancellation.
        if (cancellationToken.IsCancellationRequested) return new AssetVerificationResult.Cancelled();

        var verified = new VerifiedAssetInventory(inventory, owned, hashes.MoveToImmutable(), lengths.MoveToImmutable());
        return new AssetVerificationResult.Verified(verified);
    }
}
