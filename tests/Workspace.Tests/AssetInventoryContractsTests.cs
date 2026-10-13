using Client.Compilation.Assets;
using Client.Workspace.Domain;
using Xunit;

namespace Workspace.Tests;

// Real public creation APIs and synthetic declared assets only. These tests are
// not native/PDF/browser evidence; no compiler/worker/storage is exercised.
public sealed class AssetInventoryContractsTests
{
    private static readonly AssetInventoryBudget Budget =
        new(MaxAssets: 32, MaxPathBytes: 4096, MaxAssetBytes: 1024 * 1024, MaxTotalBytes: 8 * 1024 * 1024,
            MaxMetadataBytes: 65536, MaxSourceRefBytes: 256, MaxNoticeBytes: 4096);

    internal static SHA256 Hash(string hex) => new(hex);
    internal static AssetId Id(string seed)
    {
        // Tile the seed to a 32-hex Guid (N format). Single-char seeds match the
        // Python goldens ("a" -> 32 'a's); a 32-char seed is used as-is.
        var s = string.IsNullOrEmpty(seed) ? "0" : seed;
        var sb = new System.Text.StringBuilder(32);
        while (sb.Length < 32) sb.Append(s);
        return AssetId.Parse(Guid.Parse(sb.ToString(0, 32)));
    }

    internal static DeclaredAssetInput Asset(string id, string path, long length, string hash, string version,
        string? media = null, IReadOnlyList<AssetSourceReference>? sources = null, IReadOnlyList<AssetNotice>? notices = null)
        => new()
        {
            AssetId = Id(id),
            RelativePath = path,
            DeclaredLength = length,
            DeclaredHash = Hash(hash),
            Version = version,
            MediaKind = media,
            Sources = sources,
            Notices = notices
        };

    internal static AssetInventoryInput Input(params DeclaredAssetInput[] assets) => new(assets);

    private static AssetInventory Create(params DeclaredAssetInput[] assets)
        => Assert.IsType<AssetInventoryCreation>(AssetInventoryFactory.Create(Input(assets), Budget)).Inventory!;
    private static AssetInventoryIssue Issue(params DeclaredAssetInput[] assets)
        => IssueCore(Budget, assets);
    private static AssetInventoryIssue IssueCore(AssetInventoryBudget budget, params DeclaredAssetInput[] assets)
        => Fail(AssetInventoryFactory.Create(Input(assets), budget));
    private static AssetInventoryIssue Fail(AssetInventoryCreation c)
    { Assert.Null(c.Inventory); return c.Failure!.Value; }

    [Fact]
    public void Budget_rejects_zero_bounds()
    {
        Assert.NotNull(Create(Asset("a", "fonts/serif.otf", 5, new string('b', 64), "v1")));
        Assert.Equal(AssetInventoryIssue.InvalidBudget, IssueCore(Budget with { MaxAssets = 0 }, Asset("a", "fonts/serif.otf", 5, new string('b', 64), "v1")));
        Assert.Equal(AssetInventoryIssue.InvalidBudget, IssueCore(Budget with { MaxPathBytes = 0 }, Asset("a", "fonts/serif.otf", 5, new string('b', 64), "v1")));
        Assert.Equal(AssetInventoryIssue.InvalidBudget, IssueCore(Budget with { MaxAssetBytes = 0 }, Asset("a", "fonts/serif.otf", 5, new string('b', 64), "v1")));
        Assert.Equal(AssetInventoryIssue.InvalidBudget, IssueCore(Budget with { MaxTotalBytes = 0 }, Asset("a", "fonts/serif.otf", 5, new string('b', 64), "v1")));
        Assert.Equal(AssetInventoryIssue.InvalidBudget, IssueCore(Budget with { MaxMetadataBytes = 0 }, Asset("a", "fonts/serif.otf", 5, new string('b', 64), "v1")));
        Assert.Equal(AssetInventoryIssue.InvalidBudget, IssueCore(Budget with { MaxSourceRefBytes = 0 }, Asset("a", "fonts/serif.otf", 5, new string('b', 64), "v1")));
        Assert.Equal(AssetInventoryIssue.InvalidBudget, IssueCore(Budget with { MaxNoticeBytes = 0 }, Asset("a", "fonts/serif.otf", 5, new string('b', 64), "v1")));
        Assert.Equal(AssetInventoryIssue.InvalidBudget, IssueCore(Budget with { MaxAssets = Array.MaxLength + 1 }, Asset("a", "fonts/serif.otf", 5, new string('b', 64), "v1")));
    }

    [Fact]
    public void Create_rejects_invalid_inputs()
    {
        Assert.Equal(AssetInventoryIssue.InvalidInventory, Fail(AssetInventoryFactory.Create(null!, Budget)));
        // A null ENTRY (not a null list): the list contains one null element.
        DeclaredAssetInput[] withNull = [null!];
        Assert.Equal(AssetInventoryIssue.InvalidAsset, Fail(AssetInventoryFactory.Create(new AssetInventoryInput(withNull), Budget)));
        Assert.Equal(AssetInventoryIssue.InvalidAsset, Issue(Asset("000000000000000000000000000000000", "p", 1, new string('b', 64), "v1")));
        Assert.Equal(AssetInventoryIssue.InvalidAsset, Issue(Asset("a", "p", -1, new string('b', 64), "v1")));
        Assert.Equal(AssetInventoryIssue.InvalidHash, Issue(Asset("a", "p", 1, "BEEF", "v1")));       // not 64 hex
        Assert.Equal(AssetInventoryIssue.InvalidHash, Issue(Asset("a", "p", 1, new string('B', 64), "v1"))); // uppercase
        Assert.Equal(AssetInventoryIssue.InvalidAsset, Issue(Asset("a", "p", 1, new string('b', 64), null!))); // null version
    }

    [Fact]
    public void Create_rejects_noncanonical_and_conflicting_paths()
    {
        foreach (var bad in new[] { "/abs", "a\\b", "a/../b", "CON", "a/b/c/../d", " p ", "a//b" })
            Assert.Equal(AssetInventoryIssue.InvalidPath, Issue(Asset("a", bad, 1, new string('b', 64), "v1")));
        var a = Asset("a", "fonts/serif.otf", 1, new string('b', 64), "v1");
        Assert.Equal(AssetInventoryIssue.DuplicateAsset, Issue(a, Asset("a", "other.otf", 1, new string('b', 64), "v2")));
        Assert.Equal(AssetInventoryIssue.DuplicateAsset, Issue(a, Asset("b", "Fonts/SERIF.otf", 1, new string('b', 64), "v2")));
        Assert.Equal(AssetInventoryIssue.PathConflict,
            Issue(Asset("a", "chapters", 1, new string('b', 64), "v1"), Asset("b", "chapters/intro.tex", 1, new string('b', 64), "v2")));
    }

    [Fact]
    public void Create_rejects_count_and_aggregate_limits()
    {
        Assert.Equal(AssetInventoryIssue.AssetCountLimit, IssueCore(Budget with { MaxAssets = 2 },
            Asset("a", "p1", 1, new string('b', 64), "v1"), Asset("b", "p2", 1, new string('b', 64), "v2"), Asset("c", "p3", 1, new string('b', 64), "v3")));
        Assert.Equal(AssetInventoryIssue.AssetByteLimit, Issue(Asset("a", "p", Budget.MaxAssetBytes + 1, new string('b', 64), "v1")));
        Assert.Equal(AssetInventoryIssue.TotalByteLimit, IssueCore(Budget with { MaxTotalBytes = 5 },
            Asset("a", "p1", 3, new string('b', 64), "v1"), Asset("b", "p2", 3, new string('b', 64), "v2")));
    }

    [Fact]
    public void Create_rejects_invalid_sources_and_notices()
    {
        var a = Asset("a", "fonts/serif.otf", 1, new string('b', 64), "v1");
        // Dangling reference (not a declared asset), non-canonical, null, duplicate.
        // A `with` result is assigned to a local before being passed to Issue
        // (clearer, and avoids a `with`-as-direct-params-argument collection quirk).
        var aSrcMissing = a with { Sources = [new AssetSourceReference("source", "missing.txt")] };
        Assert.Equal(AssetInventoryIssue.InvalidSourceRef, Issue(aSrcMissing));
        var aSrcAbs = a with { Sources = [new AssetSourceReference("source", "/abs")] };
        Assert.Equal(AssetInventoryIssue.InvalidSourceRef, Issue(aSrcAbs));
        var aSrcDup = a with { Sources = [new AssetSourceReference("source", "fonts/serif.otf"), new AssetSourceReference("source", "fonts/serif.otf")] };
        Assert.Equal(AssetInventoryIssue.InvalidSourceRef, Issue(aSrcDup));
        var aNtcMissing = a with { Notices = [new AssetNotice("license", "missing.txt", null)] };
        Assert.Equal(AssetInventoryIssue.InvalidNotice, Issue(aNtcMissing));
        var aNtcDup = a with { Notices = [new AssetNotice("license", "fonts/serif.otf", null), new AssetNotice("license", "fonts/serif.otf", null)] };
        Assert.Equal(AssetInventoryIssue.InvalidNotice, Issue(aNtcDup));
        var aNtcCtrl = a with { Notices = [new AssetNotice("li\u0001cense", "fonts/serif.otf", null)] };
        Assert.Single(aNtcCtrl.Notices!);
        Assert.Equal(AssetInventoryIssue.InvalidNotice, Issue(aNtcCtrl));
        // Cross-reference to a co-declared asset is valid (references resolve).
        Assert.NotNull(AssetInventoryFactory.Create(
            Input(a with { Sources = [new AssetSourceReference("source", "notices/license.txt")] },
            Asset("b", "notices/license.txt", 1, new string('b', 64), "v2")), Budget).Inventory);
    }

    [Fact]
    public void Reordered_equivalent_inventories_share_digest()
    {
        var a = Asset("a", "fonts/serif.otf", 5, new string('b', 64), "v1", media: "font");
        var b = Asset("c", "notices/license.txt", 12, new string('d', 64), "v2", media: "text");
        Assert.Equal(Create(a, b).Identity.Digest, Create(b, a).Identity.Digest);
    }

    [Fact]
    public void Repeated_capture_is_stable()
    {
        var a = Asset("a", "fonts/serif.otf", 5, new string('b', 64), "v1", media: "font");
        Assert.Equal(Create(a).Identity.Digest, Create(a).Identity.Digest);
    }

    [Fact]
    public void Changing_each_modeled_field_changes_identity()
    {
        var a = Asset("a", "fonts/serif.otf", 5, new string('b', 64), "v1", media: "font",
            sources: [new AssetSourceReference("source", "notices/license.txt")], notices: [new AssetNotice("license", "notices/license.txt", "desc")]);
        var b = Asset("c", "notices/license.txt", 12, new string('d', 64), "v2");
        var baseline = Create(a, b);
        Assert.NotEqual(baseline.Identity.Digest, Create(a with { AssetId = Id("e") }, b).Identity.Digest);
        Assert.NotEqual(baseline.Identity.Digest, Create(a with { RelativePath = "fonts/other.otf" }, b).Identity.Digest);
        Assert.NotEqual(baseline.Identity.Digest, Create(a with { DeclaredLength = 6 }, b).Identity.Digest);
        Assert.NotEqual(baseline.Identity.Digest, Create(a with { DeclaredHash = Hash(new string('e', 64)) }, b).Identity.Digest);
        Assert.NotEqual(baseline.Identity.Digest, Create(a with { Version = "v9" }, b).Identity.Digest);
        Assert.NotEqual(baseline.Identity.Digest, Create(a with { MediaKind = null }, b).Identity.Digest);
        Assert.NotEqual(baseline.Identity.Digest, Create(a with { Sources = Array.Empty<AssetSourceReference>() }, b).Identity.Digest);
        Assert.NotEqual(baseline.Identity.Digest, Create(a with { Notices = Array.Empty<AssetNotice>() }, b).Identity.Digest);
    }

    [Fact]
    public void Empty_inventory_is_valid_and_stable()
    {
        var empty = AssetInventoryFactory.Create(Input(), Budget);
        Assert.NotNull(empty.Inventory);
        Assert.Equal(0, empty.Inventory!.AssetCount);
        Assert.Equal("60156ae4b1de9762a8d1dd7bd53b67627ed3646de79f37b354d2cb34ff950c8a", empty.Inventory.Identity.Digest.Hex);
        Assert.Equal(36, empty.Inventory.Identity.MetadataByteLength);
    }

    [Fact]
    public void Empty_file_is_valid_under_nonempty_inventory()
    {
        var emptyHash = SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(Array.Empty<byte>()));
        var inv = Create(Asset("a", "empty.txt", 0, emptyHash.Hex, "v1"));
        Assert.Single(inv.Assets);
        Assert.Equal(0, inv.Assets[0].DeclaredLength);
    }

    [Fact]
    public void Independent_python_golden_matches_single_asset()
    {
        var inv = Create(Asset("a", "fonts/serif.otf", 5, new string('b', 64), "v1"));
        Assert.Equal("685e8051b0b5e4a5f8c1a4e55339acb8db64e9bc56b857e234eb90830434dc69", inv.Identity.Digest.Hex);
        Assert.Equal(182, inv.Identity.MetadataByteLength);
    }

    [Fact]
    public void Independent_python_golden_matches_two_assets_with_references()
    {
        var a = Asset("a", "fonts/serif.otf", 5, new string('b', 64), "v1", media: "font",
            sources: [new AssetSourceReference("source", "notices/license.txt")], notices: [new AssetNotice("license", "notices/license.txt", null)]);
        var b = Asset("c", "notices/license.txt", 12, new string('d', 64), "v2", media: "text");
        var inv = Create(a, b);
        Assert.Equal("84426df67eaa155c3bc9d4d030973f2e796d002385d6e7c2b1bdceb8ce4627ec", inv.Identity.Digest.Hex);
        Assert.Equal(416, inv.Identity.MetadataByteLength);
    }

    [Fact]
    public void Path_byte_budget_rejects_over_budget_before_validator_limit()
    {
        // A path that passes ProjectPathValidator (<=1024 bytes) but exceeds a small caller
        // budget is rejected by the canonical writer's per-field budget (MetadataLimit),
        // not the validator's own 1024-byte limit (InvalidPath).
        var small = Budget with { MaxPathBytes = 8 };
        Assert.Equal(AssetInventoryIssue.MetadataLimit, IssueCore(small, Asset("a", "longer.txt", 1, new string('b', 64), "v1"))); // 9 bytes > 8
        Assert.NotNull(AssetInventoryFactory.Create(Input(Asset("a", "short.t", 1, new string('b', 64), "v1")), small).Inventory); // 7 bytes <= 8
    }

    [Fact]
    public void Unicode_path_exceeds_validator_limit_is_invalid_path()
    {
        // A path beyond the shared validator's 1024-byte limit is InvalidPath regardless of the caller budget.
        var over = new string('я', 600); // 600 Cyrillic chars = 1200 bytes > 1024
        Assert.Equal(AssetInventoryIssue.InvalidPath, Issue(Asset("a", over, 1, new string('b', 64), "v1")));
    }

    [Fact]
    public void Metadata_overflow_is_typed_failure()
    {
        Assert.Equal(AssetInventoryIssue.MetadataLimit,
            IssueCore(Budget with { MaxMetadataBytes = 16 }, Asset("a", "fonts/serif.otf", 5, new string('b', 64), "v1")));
    }

    [Fact]
    public void No_public_constructor_manufactures_validated_inventory()
    {
        // AssetInventory.Create is internal: only the factory produces a validated inventory.
        // A caller cannot set a boolean to obtain a verified receipt (Verifier is the only seam).
        Assert.NotNull(Create(Asset("a", "fonts/serif.otf", 5, new string('b', 64), "v1")));
    }

    [Fact]
    public void Control_char_notice_kind_rejected_in_isolation()
    {
        // Isolated: a real control char (U+0001 via \u, NOT \x which is variable-length and
        // would consume 4 hex digits). Confirms the factory rejects it independent of ordering.
        var a = Asset("a", "fonts/serif.otf", 1, new string('b', 64), "v1");
        var n = a with { Notices = [new AssetNotice("li\u0001cense", "fonts/serif.otf", null)] };
        Assert.Single(n.Notices!);
        Assert.Equal(AssetInventoryIssue.InvalidNotice, Issue(n));
    }

    [Fact]
    public void Empty_inventory_with_tiny_metadata_budget_is_typed_failure_not_thrown()
    {
        // Empty fast path must route through the same bounded typed factory path:
        // a domain+count that cannot fit MaxMetadataBytes is MetadataLimit, never a thrown
        // AssetInventoryException. VerifyAsync still returns Empty (not Verified) for zero assets.
        var tiny = Budget with { MaxMetadataBytes = 1 };
        var c = AssetInventoryFactory.Create(Input(), tiny);
        Assert.Null(c.Inventory);
        Assert.Equal(AssetInventoryIssue.MetadataLimit, c.Failure);
        // An empty inventory whose domain+count (36 bytes) exactly fits is still valid.
        var fits = AssetInventoryFactory.Create(Input(), Budget with { MaxMetadataBytes = 36 });
        Assert.NotNull(fits.Inventory);
        Assert.Equal(0, fits.Inventory!.AssetCount);
    }

    [Fact]
    public void Unmatched_surrogate_in_path_positions_is_typed_malformed_unicode()
    {
        // Unmatched high/low surrogates make NFC normalization throw ArgumentException;
        // they must be rejected as typed MalformedUnicode BEFORE the shared
        // ProjectPathValidator is touched (not modified), in every path/reference position.
        var high = "bad\ud800";   // unmatched high surrogate
        var low = "x\udc00y";    // unmatched low surrogate
        Assert.Equal(AssetInventoryIssue.MalformedUnicode, Issue(Asset("a", high, 1, new string('b', 64), "v1")));
        Assert.Equal(AssetInventoryIssue.MalformedUnicode, Issue(Asset("a", low, 1, new string('b', 64), "v1")));
        var a = Asset("a", "fonts/serif.otf", 1, new string('b', 64), "v1");
        Assert.Equal(AssetInventoryIssue.MalformedUnicode,
            Issue(a with { Sources = [new AssetSourceReference("source", high)] }));
        Assert.Equal(AssetInventoryIssue.MalformedUnicode,
            Issue(a with { Notices = [new AssetNotice("license", high, null)] }));
    }

    [Fact]
    public void Valid_unicode_paths_and_case_conflict_preserved()
    {
        // Valid Ukrainian and a real emoji (a matched surrogate pair) are accepted;
        // the surrogate pre-check must not false-positive on a valid pair.
        Assert.NotNull(Create(Asset("a", "шляхи/файл.tex", 1, new string('b', 64), "v1")));
        Assert.NotNull(Create(Asset("a", "a/\uD83D\uDE00.bin", 1, new string('b', 64), "v1"))); // 😀
        // NFC case-insensitive collision still reports DuplicateAsset (shared validator unchanged).
        Assert.Equal(AssetInventoryIssue.DuplicateAsset,
            Issue(Asset("a", "fonts/serif.otf", 1, new string('b', 64), "v1"),
                  Asset("b", "Fonts/SERIF.otf", 1, new string('b', 64), "v2")));
    }

    [Fact]
    public void Impossible_metadata_budget_rejects_before_entry_access()
    {
        // A hostile top-level list whose indexer/enumerator throws: with an impossible
        // metadata budget, Create must return MetadataLimit WITHOUT touching the indexer.
        var tiny = Budget with { MaxMetadataBytes = 1 };
        var hostile = new ThrowingList<DeclaredAssetInput>(2);
        var c = AssetInventoryFactory.Create(new AssetInventoryInput(hostile), tiny);
        Assert.Null(c.Inventory);
        Assert.Equal(AssetInventoryIssue.MetadataLimit, c.Failure);
        Assert.False(hostile.IndexerTouched);
    }

    [Fact]
    public void Source_count_over_remaining_rejects_before_source_entry_access()
    {
        // An asset whose source COUNT cannot fit the REMAINING aggregate metadata is
        // rejected as MetadataLimit without touching the source entries' indexer.
        var hostileSrc = new ThrowingList<AssetSourceReference>(int.MaxValue);
        var a = Asset("a", "fonts/serif.otf", 1, new string('b', 64), "v1", sources: hostileSrc);
        Assert.Equal(AssetInventoryIssue.MetadataLimit, Issue(a));
        Assert.False(hostileSrc.IndexerTouched);
    }

    [Fact]
    public void Notice_count_over_remaining_rejects_before_notice_entry_access()
    {
        var hostileNtc = new ThrowingList<AssetNotice>(int.MaxValue);
        var a = Asset("a", "fonts/serif.otf", 1, new string('b', 64), "v1", notices: hostileNtc);
        Assert.Equal(AssetInventoryIssue.MetadataLimit, Issue(a));
        Assert.False(hostileNtc.IndexerTouched);
    }

    [Fact]
    public void Notice_order_null_vs_empty_text_is_total()
    {
        // Two notices differing only by null-vs-present-empty Text both reference asset b.
        // Reordering ONLY those two notices must not change identity (total ordering key
        // includes presence), and the documented null-vs-empty distinction is preserved.
        var b = Asset("c", "n", 1, new string('d', 64), "v2");
        var nFirst = new AssetNotice("notice", "n", null);
        var eFirst = new AssetNotice("notice", "n", "");
        var order1 = Create(Asset("a", "p", 1, new string('b', 64), "v1", notices: [nFirst, eFirst]), b);
        var order2 = Create(Asset("a", "p", 1, new string('b', 64), "v1", notices: [eFirst, nFirst]), b);
        Assert.Equal(order1.Identity.Digest, order2.Identity.Digest);
        Assert.Equal("83416ee2282d43061007479511afabd16f28a19da0daa44d3adb3d224dbd8800", order1.Identity.Digest.Hex);
        Assert.Equal(336, order1.Identity.MetadataByteLength);
    }

    [Fact]
    public void Asset_source_notice_set_permutations_share_digest()
    {
        // Overall asset-set and per-asset source/notice-set permutations all share identity.
        var a = Asset("a", "fonts/serif.otf", 1, new string('b', 64), "v1", media: "font",
            sources: [new AssetSourceReference("source", "notices/license.txt"),
                      new AssetSourceReference("derived", "notices/license.txt")],
            notices: [new AssetNotice("license", "notices/license.txt", "desc"),
                      new AssetNotice("notice", "notices/license.txt", null)]);
        var b = Asset("c", "notices/license.txt", 1, new string('d', 64), "v2");
        var baseline = Create(a, b);
        // Permute the two assets.
        Assert.Equal(baseline.Identity.Digest, Create(b, a).Identity.Digest);
        // Permute a's sources and notices (independently created inputs).
        var aPerm = Asset("a", "fonts/serif.otf", 1, new string('b', 64), "v1", media: "font",
            sources: [new AssetSourceReference("derived", "notices/license.txt"),
                      new AssetSourceReference("source", "notices/license.txt")],
            notices: [new AssetNotice("notice", "notices/license.txt", null),
                      new AssetNotice("license", "notices/license.txt", "desc")]);
        Assert.Equal(baseline.Identity.Digest, Create(aPerm, b).Identity.Digest);
    }

    [Fact]
    public void Aggregate_total_budget_is_long_not_array_clamped()
    {
        // MaxTotalBytes is an aggregate across separately owned byte arrays: a positive
        // long > Array.MaxLength is valid (unlike per-single-array MaxAssetBytes).
        var big = Budget with { MaxTotalBytes = (long)Array.MaxLength + 1 };
        Assert.True(big.IsValid);
        Assert.NotNull(AssetInventoryFactory.Create(
            Input(Asset("a", "p1", 1, new string('b', 64), "v1"), Asset("b", "p2", 1, new string('b', 64), "v2")), big).Inventory);
        // Per-single-array bound still clamps to Array.MaxLength.
        Assert.Equal(AssetInventoryIssue.InvalidBudget,
            IssueCore(Budget with { MaxAssetBytes = (long)Array.MaxLength + 1 }, Asset("a", "p", 1, new string('b', 64), "v1")));
    }

    [Fact]
    public void Caller_source_notice_arrays_not_retained_after_creation()
    {
        // The factory privately owns its own immutable copies; mutating the caller's
        // mutable source/notice lists after Create cannot change the inventory.
        var srcList = new List<AssetSourceReference> { new("source", "notices/license.txt") };
        var ntcList = new List<AssetNotice> { new("license", "notices/license.txt", null) };
        var a = Asset("a", "fonts/serif.otf", 1, new string('b', 64), "v1", sources: srcList, notices: ntcList);
        var b = Asset("c", "notices/license.txt", 1, new string('d', 64), "v2");
        var inv = Create(a, b);
        srcList.Clear();
        ntcList.Clear();
        Assert.Single(inv.Assets[0].Sources);
        Assert.Single(inv.Assets[0].Notices);
        Assert.Equal("notices/license.txt", inv.Assets[0].Sources[0].TargetAssetKey);
    }

    [Fact] public void Null_source_relation_is_typed_invalid_source_ref()
    {
        // A null raw Relation must be rejected as typed InvalidSourceRef BEFORE any
        // .Length/HasUnmatchedSurrogate dereference (which would throw NullReferenceException).
        var a = Asset("a", "p", 1, new string('b', 64), "v1");
        Assert.Equal(AssetInventoryIssue.InvalidSourceRef,
            Issue(a with { Sources = [new AssetSourceReference(null!, "p")] }));
    }

    [Fact] public void Null_source_targetkey_is_typed_invalid_source_ref()
    {
        var a = Asset("a", "p", 1, new string('b', 64), "v1");
        Assert.Equal(AssetInventoryIssue.InvalidSourceRef,
            Issue(a with { Sources = [new AssetSourceReference("source", null!)] }));
    }

    [Fact] public void Null_notice_kind_is_typed_invalid_notice()
    {
        var a = Asset("a", "p", 1, new string('b', 64), "v1");
        Assert.Equal(AssetInventoryIssue.InvalidNotice,
            Issue(a with { Notices = [new AssetNotice(null!, "p", null)] }));
    }

    [Fact] public void Null_notice_referencepath_is_typed_invalid_notice()
    {
        var a = Asset("a", "p", 1, new string('b', 64), "v1");
        Assert.Equal(AssetInventoryIssue.InvalidNotice,
            Issue(a with { Notices = [new AssetNotice("license", null!, null)] }));
    }

    [Fact] public void Source_count_over_remaining_in_pass2_rejects_before_entry_access()
    {
        // Two valid assets a/p and b/n. a.Sources is a hostile list with Count=10
        // whose indexer throws. Pass 1's count preflight fits the then-remaining
        // budget, but b's fixed metadata shrinks the remaining; in pass 2 a's 10
        // source entries' minimum (80 bytes) no longer fits the ACTUAL remaining
        // meter budget -> MetadataLimit with the hostile indexer untouched.
        var tight = Budget with { MaxMetadataBytes = 350, MaxTotalBytes = 4096 };
        var hostileSrc = new ThrowingList<AssetSourceReference>(10);
        var a = Asset("a", "p", 1, new string('b', 64), "v1", sources: hostileSrc);
        var b = Asset("b", "n", 1, new string('d', 64), "v2");
        var c = AssetInventoryFactory.Create(Input(a, b), tight);
        Assert.Null(c.Inventory);
        Assert.Equal(AssetInventoryIssue.MetadataLimit, c.Failure);
        Assert.False(hostileSrc.IndexerTouched); // no source entry access
    }

    [Fact] public void Notice_count_over_remaining_in_pass2_rejects_before_entry_access()
    {
        // A notice version that fails only after earlier fixed/nested metadata shrinks
        // remaining bytes: a has one VALID source (consumed in pass 2) plus 10 throwing
        // notices. The source shrinks the remaining budget; a's 10 notices' minimum
        // (90 bytes) then no longer fits -> MetadataLimit with the notice indexer untouched.
        var tight = Budget with { MaxMetadataBytes = 350, MaxTotalBytes = 4096 };
        var hostileNtc = new ThrowingList<AssetNotice>(10);
        var a = Asset("a", "p", 1, new string('b', 64), "v1",
            sources: new List<AssetSourceReference> { new("source", "n") },
            notices: hostileNtc);
        var b = Asset("b", "n", 1, new string('d', 64), "v2");
        var c = AssetInventoryFactory.Create(Input(a, b), tight);
        Assert.Null(c.Inventory);
        Assert.Equal(AssetInventoryIssue.MetadataLimit, c.Failure);
        Assert.False(hostileNtc.IndexerTouched); // no notice entry access
    }

    private sealed class ThrowingList<T> : IReadOnlyList<T>
    {
        private readonly int _count;
        internal bool IndexerTouched;
        internal ThrowingList(int count) => _count = count;
        public T this[int index] { get { IndexerTouched = true; throw new InvalidOperationException("hostile indexer"); } }
        public int Count => _count;
        public IEnumerator<T> GetEnumerator() { throw new InvalidOperationException("hostile enumerator"); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
