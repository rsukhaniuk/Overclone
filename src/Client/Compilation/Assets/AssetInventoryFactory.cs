using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Client.Workspace.Transfer;
using ContentHash = Client.Workspace.Domain.SHA256;

namespace Client.Compilation.Assets;

/// <summary>Creates a validated immutable declared-asset inventory from a
/// bounded untrusted caller list. Two-phase: Phase A validates every
/// ID/path/hash/version/length/metadata, canonical path + path-tree
/// boundaries, duplicates/conflicts and source/notice references to declared
/// entries, and aggregate limits — all BEFORE constructing any immutable
/// record or computing the digest. No byte reading, saving, locking, network,
/// analysis/review/native/worker calls. No public constructor lets a caller
/// manufacture a validated inventory or a verified receipt by setting a boolean.
///
/// The metadata budget is enforced BEFORE any entry indexer is touched: a cheap
/// conservative lower bound (domain + count + per-asset fixed minimum) rejects
/// an impossible budget up front, and per-asset source/notice COUNT preflights
/// (against the actual REMAINING aggregate budget) reject before those nested
/// indexers are touched. All metadata is measured incrementally into a single
/// length-only meter (length is order-independent) during validation, BEFORE
/// any owned source/notice arrays, immutable entries or canonical sorted arrays
/// are built. Malformed Unicode in path/reference fields is rejected as a typed
/// MalformedUnicode BEFORE NFC normalization/comparison (the shared
/// ProjectPathValidator is not modified to paper over this caller).</summary>
public static class AssetInventoryFactory
{
    // Conservative lower bounds on the canonical metadata, used to reject an
    // impossible budget BEFORE touching any entry indexer. Length is order-independent.
    private const long DomainMeta = 36L;        // 4-prefix + 28 domain bytes + Int32 count
    private const long AssetFixedMin = 129L;    // assetId(4+32)+path(4+0)+media(1)+length(8)+hash(4+64)+version(4+0)+srccount(4)+noticecount(4)

    public static AssetInventoryCreation Create(AssetInventoryInput input, AssetInventoryBudget budget)
    {
        if (budget is null || !budget.IsValid) return new(null, AssetInventoryIssue.InvalidBudget);
        if (input is null || input.Assets is null) return new(null, AssetInventoryIssue.InvalidInventory);

        int count;
        try { count = input.Assets.Count; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return new(null, AssetInventoryIssue.InvalidInventory); }
        if (count < 0 || count > budget.MaxAssets)
            return new(null, count <= 0 ? AssetInventoryIssue.InvalidInventory : AssetInventoryIssue.AssetCountLimit);

        // Cheap metadata preflight (NO entry access): conservative lower bound on the
        // total canonical metadata. If even this cannot fit MaxMetadataBytes, reject before
        // touching any entry indexer or allocating count-sized scaffolding.
        if (DomainMeta + (long)count * AssetFixedMin > budget.MaxMetadataBytes)
            return new(null, AssetInventoryIssue.MetadataLimit);

        if (count == 0)
        {
            // Empty declared state is allowed if its domain+count fits the metadata budget
            // (preflighted above). Route through the SAME bounded typed path: never throw
            // AssetInventoryException out of Create. VerifyAsync still returns Empty
            // (not Verified) for a zero-asset inventory.
            try
            {
                long emptyMetadataLength;
                ContentHash emptyDigest;
                using (var measure = new AssetInventoryCanonical(budget.MaxMetadataBytes, hash: false))
                { measure.Text(AssetInventoryCanonical.Domain, -1); measure.Int32(0); emptyMetadataLength = measure.Length; }
                using (var canonical = new AssetInventoryCanonical(budget.MaxMetadataBytes, hash: true))
                { canonical.Text(AssetInventoryCanonical.Domain, -1); canonical.Int32(0); emptyDigest = canonical.Finish(); }
                return new(AssetInventory.Create(ImmutableArray<DeclaredAsset>.Empty, new(emptyDigest, emptyMetadataLength, 0)), null);
            }
            catch (AssetInventoryException) { return new(null, AssetInventoryIssue.MetadataLimit); }
        }

        var byId = new HashSet<Guid>();
        var byPathKey = new Dictionary<string, AssetId>(StringComparer.Ordinal);
        var pending = new List<(DeclaredAssetInput Entry, string Path, string? Media)>(count);

        // Running length-only meter: measures the total canonical metadata incrementally
        // (length is order-independent) so over-budget metadata is rejected BEFORE any
        // owned source/notice arrays, immutable entries or sorted arrays are built. The
        // canonical Text enforces per-field byte bounds + control chars + surrogate safety
        // (GetByteCount) incrementally under the aggregate budget.
        using var meter = new AssetInventoryCanonical(budget.MaxMetadataBytes, hash: false);
        try
        {
            meter.Text(AssetInventoryCanonical.Domain, -1); // preflighted: fits
            meter.Int32(count);
        }
        catch (AssetInventoryException error) { return new(null, error.Issue); }

        long totalDeclared = 0;

        // Pass 1: validate identity/path/hash/version/length/budgets; build the full
        // path-key map; measure each asset's FIXED metadata. Source/notice entries are
        // NOT touched here (only their COUNT + a conservative minimum is measured); they
        // are validated/measured in Pass 2 once all paths are known (forward references).
        for (int i = 0; i < count; i++)
        {
            DeclaredAssetInput entry;
            try { entry = input.Assets[i]; }
            catch (Exception error) when (error is not OutOfMemoryException)
            { return new(null, AssetInventoryIssue.InvalidInventory); }
            if (entry is null) return new(null, AssetInventoryIssue.InvalidAsset);
            if (entry.AssetId.Value == Guid.Empty) return new(null, AssetInventoryIssue.InvalidAsset);
            if (entry.Version is null) return new(null, AssetInventoryIssue.InvalidAsset);
            if (entry.DeclaredLength < 0) return new(null, AssetInventoryIssue.InvalidAsset);
            if (!CompilationCanonical.ValidHash(entry.DeclaredHash)) return new(null, AssetInventoryIssue.InvalidHash);

            var pathIssue = CheckPathBudgetAndSurrogate(entry.RelativePath, budget.MaxPathBytes);
            if (pathIssue is { } pi) return new(null, pi);
            var normalized = ProjectPathValidator.Normalize(entry.RelativePath);
            if (normalized is null || normalized != entry.RelativePath
                || ProjectPathValidator.Validate(normalized) is not null)
                return new(null, AssetInventoryIssue.InvalidPath);
            if (OverFieldBudget(normalized, budget.MaxPathBytes)) return new(null, AssetInventoryIssue.MetadataLimit);
            var key = ProjectPathValidator.ComparisonKey(normalized); // safe: surrogate pre-checked
            if (byPathKey.ContainsKey(key)) return new(null, AssetInventoryIssue.DuplicateAsset);
            byPathKey[key] = entry.AssetId;
            if (!byId.Add(entry.AssetId.Value)) return new(null, AssetInventoryIssue.DuplicateAsset);

            if (entry.DeclaredLength > budget.MaxAssetBytes)
                return new(null, AssetInventoryIssue.AssetByteLimit);
            if (entry.DeclaredLength > budget.MaxTotalBytes - totalDeclared)
                return new(null, AssetInventoryIssue.TotalByteLimit);
            totalDeclared = checked(totalDeclared + entry.DeclaredLength);

            var media = entry.MediaKind;
            if (media is not null)
            {
                if (media.Length > budget.MaxPathBytes) return new(null, AssetInventoryIssue.MetadataLimit);
                if (HasUnmatchedSurrogate(media)) return new(null, AssetInventoryIssue.MalformedUnicode);
                if (string.IsNullOrWhiteSpace(media) || media.Any(char.IsControl))
                    return new(null, AssetInventoryIssue.InvalidAsset);
            }

            // Measure this asset's FIXED metadata into the running meter.
            try
            {
                meter.Text(entry.AssetId.ToString(), -1);
                meter.Text(normalized, budget.MaxPathBytes);
                meter.Boolean(media is not null);
                if (media is not null) meter.Text(media, budget.MaxPathBytes);
                meter.Int64(entry.DeclaredLength);
                meter.Text(entry.DeclaredHash.Hex, -1);
                meter.Text(entry.Version, budget.MaxPathBytes);
            }
            catch (AssetInventoryException error) { return new(null, error.Issue); }

            // Source/notice COUNT preflight (no source/notice entries touched): conservative
            // minimum for the counts against the ACTUAL REMAINING aggregate budget
            // (not budget.MaxMetadataBytes/8 afresh per asset). Per source min = 8
            // (relation+targetkey prefixes); per notice min = 9 (kind+ref+presence).
            int srcCount, ntcCount;
            try
            {
                srcCount = entry.Sources is null ? 0 : entry.Sources.Count;
                ntcCount = entry.Notices is null ? 0 : entry.Notices.Count;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { return new(null, AssetInventoryIssue.InvalidInventory); }
            if (srcCount < 0 || ntcCount < 0) return new(null, AssetInventoryIssue.InvalidInventory);
            long remaining = budget.MaxMetadataBytes - meter.Length;
            if (4L + (long)srcCount * 8L + 4L + (long)ntcCount * 9L > remaining)
                return new(null, AssetInventoryIssue.MetadataLimit);

            pending.Add((entry, normalized, media));
        }

        // Pass 2: all paths known. Validate + measure source/notice references (forward
        // references now resolve) incrementally into the SAME meter, then build owned
        // sorted arrays only after measurement passes.
        var validated = new List<DeclaredAsset>(count);
        var paths = new List<string>(count);
        foreach (var (entry, normalized, media) in pending)
        {
            var sources = ValidateAndMeasureRefs(entry.Sources, budget, byPathKey, meter, out var srcFail);
            if (!sources.ok) return new(null, srcFail);
            var notices = ValidateAndMeasureNotices(entry.Notices, budget, byPathKey, meter, out var ntcFail);
            if (!notices.ok) return new(null, ntcFail);
            paths.Add(normalized);
            validated.Add(new(entry.AssetId, normalized, entry.DeclaredLength, entry.DeclaredHash,
                entry.Version, media, sources.refs, notices.refs));
        }

        // Path-tree conflicts (file/directory prefix) among accepted paths.
        if (ProjectTreeValidator.FindPathConflicts(paths) is { Count: > 0 })
            return new(null, AssetInventoryIssue.PathConflict);

        // Canonical ordering for the set: sort by (relativePath ordinal, assetIdHex ordinal)
        // so equivalent inventories share identity regardless of input order.
        var ordered = validated
            .OrderBy(a => a.RelativePath, StringComparer.Ordinal)
            .ThenBy(a => a.AssetId.ToString(), StringComparer.Ordinal)
            .ToImmutableArray();

        // Total metadata length already measured by the meter (<= MaxMetadataBytes). Build
        // the digest in canonical (sorted) order; length is order-independent so this
        // cannot exceed the budget the meter already confirmed.
        long metadataLength = meter.Length;
        ContentHash digest;
        try
        {
            using var canonical = new AssetInventoryCanonical(budget.MaxMetadataBytes, hash: true);
            canonical.Text(AssetInventoryCanonical.Domain, -1);
            canonical.Int32(ordered.Length);
            foreach (var asset in ordered) canonical.Asset(asset, budget);
            digest = canonical.Finish();
        }
        catch (AssetInventoryException error) { return new(null, error.Issue); }

        return new(AssetInventory.Create(ordered, new(digest, metadataLength, ordered.Length)), null);
    }

    /// <summary>Reject a path/reference field that is over its per-field byte budget
    /// (cheap char-count lower bound) or contains an unmatched surrogate, BEFORE any
    /// NFC normalization/comparison (which would throw ArgumentException) and before
    /// scanning its bytes. The shared ProjectPathValidator is not modified.</summary>
    private static AssetInventoryIssue? CheckPathBudgetAndSurrogate(string? path, int maxPathBytes)
    {
        if (path is null) return AssetInventoryIssue.InvalidPath;
        if (path.Length > maxPathBytes) return AssetInventoryIssue.MetadataLimit; // cheap char bound
        if (HasUnmatchedSurrogate(path)) return AssetInventoryIssue.MalformedUnicode;
        return null;
    }

    /// <summary>Per-field UTF-8 byte bound for an already-normalized path, checked BEFORE
    /// the shared validator's own 1024-byte limit so a small caller budget fires first.
    /// Bounded by the cheap char preflight; the scan is on a bounded string.</summary>
    private static bool OverFieldBudget(string normalized, int maxPathBytes)
        => Encoding.UTF8.GetByteCount(normalized) > maxPathBytes;

    /// <summary>True if the string contains an unmatched high/low surrogate that would
    /// make UTF-8 encoding / NFC normalization fail. Single O(n) pass, no allocation.</summary>
    private static bool HasUnmatchedSurrogate(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return true;
                i++; // consume the pair
            }
            else if (char.IsLowSurrogate(c)) return true; // low without preceding high
        }
        return false;
    }

    private static (bool ok, ImmutableArray<AssetSourceReference> refs) ValidateAndMeasureRefs(
        IReadOnlyList<AssetSourceReference>? sources, AssetInventoryBudget budget,
        Dictionary<string, AssetId> byPathKey, AssetInventoryCanonical meter, out AssetInventoryIssue failure)
    {
        failure = AssetInventoryIssue.InvalidSourceRef;
        int n = sources is null ? 0 : sources.Count;
        if (n < 0) { failure = AssetInventoryIssue.MetadataLimit; return default; }
        // Pass-2 count RE-preflight against the ACTUAL remaining meter budget. Pass 1's preflight
        // used the then-remaining budget and does NOT replace this check: earlier assets' fixed
        // metadata (and this asset's) has since consumed bytes, so the nested count minimum may no
        // longer fit. Reject as MetadataLimit before any entry access or count-sized allocation.
        long remaining = budget.MaxMetadataBytes - meter.Length;
        if (4L + (long)n * 8L > remaining) { failure = AssetInventoryIssue.MetadataLimit; return default; }
        try { meter.Int32(n); } catch (AssetInventoryException error) { failure = error.Issue; return default; }
        if (n == 0) return (true, ImmutableArray<AssetSourceReference>.Empty);
        var builder = new List<AssetSourceReference>(n);
        var seen = new HashSet<(string, string)>();
        for (int i = 0; i < n; i++)
        {
            AssetSourceReference s;
            try { s = sources![i]; }
            catch (Exception error) when (error is not OutOfMemoryException)
            { failure = AssetInventoryIssue.InvalidSourceRef; return default; }
            if (s is null) { failure = AssetInventoryIssue.InvalidSourceRef; return default; }
            // Nullable raw fields BEFORE any .Length/HasUnmatchedSurrogate dereference.
            if (s.Relation is null || s.TargetAssetKey is null)
            { failure = AssetInventoryIssue.InvalidSourceRef; return default; }
            if (s.Relation.Length > budget.MaxSourceRefBytes) { failure = AssetInventoryIssue.MetadataLimit; return default; }
            if (HasUnmatchedSurrogate(s.Relation)) { failure = AssetInventoryIssue.MalformedUnicode; return default; }
            if (string.IsNullOrWhiteSpace(s.Relation) || s.Relation.Any(char.IsControl))
            { failure = AssetInventoryIssue.InvalidSourceRef; return default; }
            var keyIssue = CheckPathBudgetAndSurrogate(s.TargetAssetKey, budget.MaxPathBytes);
            if (keyIssue is { } ki) { failure = ki; return default; }
            var nkey = ProjectPathValidator.Normalize(s.TargetAssetKey);
            if (nkey is null || nkey != s.TargetAssetKey || ProjectPathValidator.Validate(nkey) is not null
                || OverFieldBudget(nkey, budget.MaxPathBytes)
                || !byPathKey.ContainsKey(ProjectPathValidator.ComparisonKey(nkey)))
            { failure = AssetInventoryIssue.InvalidSourceRef; return default; }
            if (!seen.Add((s.Relation, s.TargetAssetKey))) { failure = AssetInventoryIssue.InvalidSourceRef; return default; }
            try
            {
                meter.Text(s.Relation, budget.MaxSourceRefBytes);
                meter.Text(s.TargetAssetKey, budget.MaxPathBytes);
            }
            catch (AssetInventoryException error) { failure = error.Issue; return default; }
            builder.Add(s);
        }
        return (true, builder.OrderBy(x => x.Relation, StringComparer.Ordinal)
            .ThenBy(x => x.TargetAssetKey, StringComparer.Ordinal).ToImmutableArray());
    }

    private static (bool ok, ImmutableArray<AssetNotice> refs) ValidateAndMeasureNotices(
        IReadOnlyList<AssetNotice>? notices, AssetInventoryBudget budget,
        Dictionary<string, AssetId> byPathKey, AssetInventoryCanonical meter, out AssetInventoryIssue failure)
    {
        failure = AssetInventoryIssue.InvalidNotice;
        int n = notices is null ? 0 : notices.Count;
        if (n < 0) { failure = AssetInventoryIssue.MetadataLimit; return default; }
        // Pass-2 count RE-preflight against the ACTUAL remaining meter budget (per-notice minimum =
        // kind+ref prefixes + presence byte = 9). Pass 1's preflight used the then-remaining budget and
        // does NOT replace this check; earlier assets' fixed/source metadata has since consumed bytes.
        long remaining = budget.MaxMetadataBytes - meter.Length;
        if (4L + (long)n * 9L > remaining) { failure = AssetInventoryIssue.MetadataLimit; return default; }
        try { meter.Int32(n); } catch (AssetInventoryException error) { failure = error.Issue; return default; }
        if (n == 0) return (true, ImmutableArray<AssetNotice>.Empty);
        var builder = new List<AssetNotice>(n);
        var seen = new HashSet<(string, string, string?)>();
        for (int i = 0; i < n; i++)
        {
            AssetNotice nt;
            try { nt = notices![i]; }
            catch (Exception error) when (error is not OutOfMemoryException)
            { failure = AssetInventoryIssue.InvalidNotice; return default; }
            if (nt is null) { failure = AssetInventoryIssue.InvalidNotice; return default; }
            // Nullable raw fields BEFORE any .Length/HasUnmatchedSurrogate dereference (Text is nullable by design).
            if (nt.Kind is null || nt.ReferencePath is null)
            { failure = AssetInventoryIssue.InvalidNotice; return default; }
            if (nt.Kind.Length > budget.MaxSourceRefBytes) { failure = AssetInventoryIssue.MetadataLimit; return default; }
            if (HasUnmatchedSurrogate(nt.Kind)) { failure = AssetInventoryIssue.MalformedUnicode; return default; }
            if (string.IsNullOrWhiteSpace(nt.Kind) || nt.Kind.Any(char.IsControl))
            { failure = AssetInventoryIssue.InvalidNotice; return default; }
            var keyIssue = CheckPathBudgetAndSurrogate(nt.ReferencePath, budget.MaxPathBytes);
            if (keyIssue is { } ki) { failure = ki; return default; }
            var nkey = ProjectPathValidator.Normalize(nt.ReferencePath);
            if (nkey is null || nkey != nt.ReferencePath || ProjectPathValidator.Validate(nkey) is not null
                || OverFieldBudget(nkey, budget.MaxPathBytes)
                || !byPathKey.ContainsKey(ProjectPathValidator.ComparisonKey(nkey)))
            { failure = AssetInventoryIssue.InvalidNotice; return default; }
            if (nt.Text is { } text)
            {
                if (text.Length > budget.MaxNoticeBytes) { failure = AssetInventoryIssue.MetadataLimit; return default; }
                if (HasUnmatchedSurrogate(text)) { failure = AssetInventoryIssue.MalformedUnicode; return default; }
                if (text.Any(char.IsControl)) { failure = AssetInventoryIssue.InvalidNotice; return default; }
            }
            if (!seen.Add((nt.Kind, nt.ReferencePath, nt.Text))) { failure = AssetInventoryIssue.InvalidNotice; return default; }
            try
            {
                meter.Text(nt.Kind, budget.MaxSourceRefBytes);
                meter.Text(nt.ReferencePath, budget.MaxPathBytes);
                meter.Boolean(nt.Text is not null);
                if (nt.Text is { } ntText) meter.Text(ntText, budget.MaxNoticeBytes, allowEmpty: true);
            }
            catch (AssetInventoryException error) { failure = error.Issue; return default; }
            builder.Add(nt);
        }
        // Total ordering: presence (null vs present-empty) is part of the key, because the
        // canonical encoding distinguishes them via a presence byte. Input permutation
        // cannot change identity; the documented null-vs-present-empty distinction is preserved.
        return (true, builder
            .OrderBy(x => x.Kind, StringComparer.Ordinal)
            .ThenBy(x => x.ReferencePath, StringComparer.Ordinal)
            .ThenBy(x => x.Text is null ? 0 : 1)
            .ThenBy(x => x.Text ?? "", StringComparer.Ordinal)
            .ToImmutableArray());
    }
}
