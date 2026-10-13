using System.Collections.Immutable;
using Client.Compilation.Assets;
using Client.Workspace.Domain;
using Xunit;

namespace Workspace.Tests;

// Real public creation/verification APIs, a deterministic fake read-only byte-reader,
// and synthetic assets only. No native/PDF/browser evidence; no compiler/worker/
// storage is exercised. The fake reader never saves, locks, downloads or subscribes.
public sealed class AssetInventoryVerifierTests
{
    private static readonly AssetInventoryBudget InvBudget =
        new(MaxAssets: 32, MaxPathBytes: 4096, MaxAssetBytes: 1024 * 1024, MaxTotalBytes: 8 * 1024 * 1024,
            MaxMetadataBytes: 65536, MaxSourceRefBytes: 256, MaxNoticeBytes: 4096);
    private static readonly AssetVerificationBudget VBudget = new(32, 1024 * 1024, 8 * 1024 * 1024);
    internal static readonly CancellationToken None = CancellationToken.None;

    internal static SHA256 H(byte[] b) => SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(b));
    internal static AssetId Id(string seed)
    {
        var s = string.IsNullOrEmpty(seed) ? "0" : seed;
        var sb = new System.Text.StringBuilder(32);
        while (sb.Length < 32) sb.Append(s);
        return AssetId.Parse(Guid.Parse(sb.ToString(0, 32)));
    }
    internal static DeclaredAssetInput Asset(string id, string path, byte[] content, string version)
        => new()
        {
            AssetId = Id(id),
            RelativePath = path,
            DeclaredLength = content.LongLength,
            DeclaredHash = H(content),
            Version = version
        };
    internal static AssetInventoryInput Input(params DeclaredAssetInput[] assets) => new(assets);
    internal static AssetInventory Inventory(params DeclaredAssetInput[] assets)
        => Assert.IsType<AssetInventoryCreation>(AssetInventoryFactory.Create(Input(assets), InvBudget)).Inventory!;

    private static readonly byte[] EmptyBytes = Array.Empty<byte>();
    private static readonly SHA256 EmptyHash = H(EmptyBytes);

    [Fact]
    public async Task Verified_receipt_owns_bytes_and_exposes_span_and_copy()
    {
        var a = Asset("a", "fonts/serif.otf", new byte[] { 1, 2, 3 }, "v1");
        var b = Asset("b", "notices/license.txt", new byte[] { 9, 9 }, "v2");
        var inv = Inventory(a, b);
        var reader = new FakeAssetReader();
        reader.Content[a.AssetId] = new byte[] { 1, 2, 3 };
        reader.Content[b.AssetId] = new byte[] { 9, 9 };
        var result = await new AssetInventoryVerifier().VerifyAsync(inv, reader, VBudget, None);
        var verified = Assert.IsType<AssetVerificationResult.Verified>(result).Inventory;
        Assert.Equal(2, verified.AssetCount);
        Assert.Equal(inv.Identity.Digest, verified.Identity.Digest); // EXACT immutable inventory retained
        Assert.Equal(H(new byte[] { 1, 2, 3 }), verified.ActualHash(0));
        Assert.Equal(3, verified.ActualLength(0));
        Assert.True(verified.Bytes(0).SequenceEqual(new byte[] { 1, 2, 3 }));
        var copy = verified.ToArray(0);
        copy[0] = 99;
        Assert.Equal(1, verified.Bytes(0)[0]); // ToArray is defensive; receipt unchanged
    }

    [Fact]
    public async Task Preflight_over_budget_causes_zero_reads()
    {
        var a = Asset("a", "fonts/serif.otf", new byte[] { 1, 2, 3 }, "v1");
        var inv = Inventory(a);
        var reader = new FakeAssetReader();
        reader.Barriers[a.AssetId] = new(); // would prove a read happened; must NOT
        // Declared length 3 is fine, but use a verification budget with MaxAssetBytes=1.
        var tiny = new AssetVerificationBudget(MaxAssets: 32, MaxAssetBytes: 1, MaxTotalBytes: 8 * 1024 * 1024);
        var result = await new AssetInventoryVerifier().VerifyAsync(inv, reader, tiny, None);
        Assert.IsType<AssetVerificationResult.QuotaExceeded>(result);
        Assert.False(reader.Barriers[a.AssetId].Used); // 0 reads
        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task MaxAssets_enforced_before_reads()
    {
        var inv = Inventory(Asset("a", "p1", new byte[] { 1 }, "v1"), Asset("b", "p2", new byte[] { 2 }, "v2"),
            Asset("c", "p3", new byte[] { 3 }, "v3"));
        var reader = new FakeAssetReader();
        var small = new AssetVerificationBudget(MaxAssets: 2, MaxAssetBytes: 1024, MaxTotalBytes: 8 * 1024 * 1024);
        var result = await new AssetInventoryVerifier().VerifyAsync(inv, reader, small, None);
        var q = Assert.IsType<AssetVerificationResult.QuotaExceeded>(result);
        Assert.Equal(AssetInventoryIssue.AssetCountLimit, q.Issue);
        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task Missing_asset_rejects_whole_capture()
    {
        var a = Asset("a", "p1", new byte[] { 1 }, "v1");
        var b = Asset("b", "p2", new byte[] { 2 }, "v2");
        var c = Asset("c", "p3", new byte[] { 3 }, "v3");
        var reader = new FakeAssetReader();
        reader.Content[a.AssetId] = new byte[] { 1 };
        reader.Content[c.AssetId] = new byte[] { 3 };
        reader.ReturnNull = b.AssetId; // asset 2 (canonical order: a,b,c) unavailable
        var result = await new AssetInventoryVerifier().VerifyAsync(Inventory(a, b, c), reader, VBudget, None);
        var u = Assert.IsType<AssetVerificationResult.Unavailable>(result);
        Assert.Equal(AssetInventoryIssue.UnavailableAsset, u.Issue);
        Assert.Equal(b.AssetId, u.Asset!.AssetId);
        Assert.False(reader.Barriers.ContainsKey(c.AssetId)); // c never reached (fail-fast, all-or-nothing)
    }

    [Fact]
    public async Task Corrupt_bytes_rejects_whole_capture()
    {
        var a = Asset("a", "p1", new byte[] { 1, 2 }, "v1");
        var b = Asset("b", "p2", new byte[] { 2 }, "v2");
        var reader = new FakeAssetReader();
        reader.Content[a.AssetId] = new byte[] { 1, 2 };
        reader.Content[b.AssetId] = new byte[] { 9 }; // wrong content -> hash mismatch
        var result = await new AssetInventoryVerifier().VerifyAsync(Inventory(a, b), reader, VBudget, None);
        var u = Assert.IsType<AssetVerificationResult.Unavailable>(result);
        Assert.Equal(AssetInventoryIssue.CorruptBytes, u.Issue);
    }

    [Fact]
    public async Task Oversized_read_rejected_before_duplicate_allocation()
    {
        // Declared length is small (passes preflight); the reader returns a huge buffer.
        // The verifier's actual-size check fires BEFORE the copy/hash, so this is
        // QuotaExceeded(AssetByteLimit), not Unavailable(CorruptBytes) (which a copy-then-hash would yield).
        var a = Asset("a", "p1", new byte[] { 1, 2, 3 }, "v1");
        var reader = new FakeAssetReader();
        reader.OversizedAsset = a.AssetId;
        var result = await new AssetInventoryVerifier().VerifyAsync(Inventory(a), reader, VBudget, None);
        var q = Assert.IsType<AssetVerificationResult.QuotaExceeded>(result);
        Assert.Equal(AssetInventoryIssue.AssetByteLimit, q.Issue);
    }

    [Fact]
    public async Task Owned_buffer_mutation_resistance()
    {
        var a = Asset("a", "p1", new byte[] { 1, 2, 3 }, "v1");
        var reader = new FakeAssetReader();
        var live = new byte[] { 1, 2, 3 };
        reader.Content[a.AssetId] = live; // shared mutable aliased buffer
        var result = await new AssetInventoryVerifier().VerifyAsync(Inventory(a), reader, VBudget, None);
        var verified = Assert.IsType<AssetVerificationResult.Verified>(result).Inventory;
        live[0] = 99; // mutate the reader's returned buffer AFTER verification
        Assert.Equal(1, verified.Bytes(0)[0]); // receipt owns its own copy
        Assert.Equal(H(new byte[] { 1, 2, 3 }), verified.ActualHash(0));
    }

    [Fact]
    public async Task Empty_file_valid_in_nonempty_inventory()
    {
        var empty = Asset("a", "empty.txt", EmptyBytes, "v1");
        var b = Asset("b", "p2", new byte[] { 7 }, "v2");
        var reader = new FakeAssetReader();
        reader.Content[empty.AssetId] = EmptyBytes;
        reader.Content[b.AssetId] = new byte[] { 7 };
        var result = await new AssetInventoryVerifier().VerifyAsync(Inventory(empty, b), reader, VBudget, None);
        var verified = Assert.IsType<AssetVerificationResult.Verified>(result).Inventory;
        Assert.Equal(0, verified.ActualLength(0));
        Assert.Equal(EmptyHash, verified.ActualHash(0));
        Assert.True(verified.Bytes(0).IsEmpty);
    }

    [Fact]
    public async Task Empty_inventory_returns_not_ready_not_verified()
    {
        var inv = Inventory(); // zero assets
        var result = await new AssetInventoryVerifier().VerifyAsync(inv, new FakeAssetReader(), VBudget, None);
        Assert.IsType<AssetVerificationResult.Empty>(result);
        Assert.False(result is AssetVerificationResult.Verified); // never a meaningful receipt (invariant 8)
    }

    [Fact]
    public async Task Invalid_inputs_rejected()
    {
        var v = new AssetInventoryVerifier();
        Assert.IsType<AssetVerificationResult.InvalidInput>(await v.VerifyAsync(null!, new FakeAssetReader(), VBudget, None));
        Assert.IsType<AssetVerificationResult.InvalidInput>(await v.VerifyAsync(Inventory(Asset("a", "p", new byte[] { 1 }, "v1")), null!, VBudget, None));
        Assert.IsType<AssetVerificationResult.InvalidInput>(await v.VerifyAsync(Inventory(Asset("a", "p", new byte[] { 1 }, "v1")), new FakeAssetReader(), null!, None));
        Assert.IsType<AssetVerificationResult.InvalidInput>(await v.VerifyAsync(Inventory(Asset("a", "p", new byte[] { 1 }, "v1")), new FakeAssetReader(),
            new AssetVerificationBudget(MaxAssets: 0, 1, 1), None)); // invalid budget (zero)
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.IsType<AssetVerificationResult.Cancelled>(await v.VerifyAsync(Inventory(Asset("a", "p", new byte[] { 1 }, "v1")), new FakeAssetReader(), VBudget, cts.Token));
    }

    [Fact]
    public async Task Cancellation_with_noncooperative_read()
    {
        var a = Asset("a", "p1", new byte[] { 1, 2, 3 }, "v1");
        var gate = new ReadBarrier();
        var reader = new FakeAssetReader { Content = { [a.AssetId] = new byte[] { 1, 2, 3 } } };
        reader.Barriers[a.AssetId] = gate;
        using var cts = new CancellationTokenSource();
        var v = new AssetInventoryVerifier();
        var task = v.VerifyAsync(Inventory(a), reader, VBudget, cts.Token);
        await gate.WaitReached();          // read is in flight, blocked on Release (ignores ct)
        cts.Cancel();                      // WaitAsync(ct) observes cancellation even though the reader ignores it
        var result = await task;
        Assert.IsType<AssetVerificationResult.Cancelled>(result);
        gate.Release.TrySetResult(new byte[] { 1, 2, 3 }); // unblock orphaned read for cleanup
    }

    [Fact]
    public async Task Cancellation_after_read_prevents_verified_publication()
    {
        // The last asset's read cancels ct before returning. The verifier must NOT
        // publish a Verified receipt after the awaited reads (re-check before publication).
        var a = Asset("a", "p1", new byte[] { 1 }, "v1");
        var b = Asset("b", "p2", new byte[] { 2 }, "v2");
        using var cts = new CancellationTokenSource();
        var reader = new FakeAssetReader();
        reader.Content[a.AssetId] = new byte[] { 1 };
        reader.Content[b.AssetId] = new byte[] { 2 };
        reader.CancelOnReadAsset = b.AssetId;
        reader.CancelOnRead = cts;
        var result = await new AssetInventoryVerifier().VerifyAsync(Inventory(a, b), reader, VBudget, cts.Token);
        Assert.IsType<AssetVerificationResult.Cancelled>(result);
        Assert.False(result is AssetVerificationResult.Verified);
    }

    [Fact]
    public async Task Read_failure_rejected_as_unavailable()
    {
        var a = Asset("a", "p1", new byte[] { 1 }, "v1");
        var reader = new FakeAssetReader { FailRead = a.AssetId };
        var result = await new AssetInventoryVerifier().VerifyAsync(Inventory(a), reader, VBudget, None);
        var u = Assert.IsType<AssetVerificationResult.Unavailable>(result);
        Assert.Equal(AssetInventoryIssue.ReadFailure, u.Issue);
    }

    [Fact]
    public async Task Immutable_receipt_unchanged_when_source_data_changes()
    {
        var a = Asset("a", "p1", new byte[] { 1, 2, 3 }, "v1");
        var reader = new FakeAssetReader();
        reader.Content[a.AssetId] = new byte[] { 1, 2, 3 };
        var result = await new AssetInventoryVerifier().VerifyAsync(Inventory(a), reader, VBudget, None);
        var verified = Assert.IsType<AssetVerificationResult.Verified>(result).Inventory;
        reader.Content[a.AssetId] = new byte[] { 99 }; // source storage mutation after success
        Assert.Equal(1, verified.Bytes(0)[0]); // receipt bytes owned; cannot be retargeted
        Assert.Equal(H(new byte[] { 1, 2, 3 }), verified.ActualHash(0));
    }

    [Fact]
    public async Task Reader_OCE_with_uncanceled_caller_is_read_failure()
    {
        // The cancellation outcome must represent the CALLER token: a foreign reader
        // that cancels its own unrelated token while the caller token is not canceled is
        // a read failure, not a caller cancellation.
        var a = Asset("a", "p1", new byte[] { 1, 2, 3 }, "v1");
        var reader = new FakeAssetReader { ForeignCancel = a.AssetId };
        var result = await new AssetInventoryVerifier().VerifyAsync(Inventory(a), reader, VBudget, None);
        var u = Assert.IsType<AssetVerificationResult.Unavailable>(result);
        Assert.Equal(AssetInventoryIssue.ReadFailure, u.Issue);
    }

    [Fact]
    public async Task Canceled_caller_null_bytes_still_cancelled()
    {
        // Caller token canceled and the read returns null: the post-await caller-cancel
        // check must fire BEFORE the null check, so this is Cancelled (not Unavailable).
        var a = Asset("a", "p1", new byte[] { 1 }, "v1");
        using var cts = new CancellationTokenSource();
        var reader = new FakeAssetReader { ReturnNull = a.AssetId };
        reader.CancelOnReadAsset = a.AssetId; reader.CancelOnRead = cts;
        var result = await new AssetInventoryVerifier().VerifyAsync(Inventory(a), reader, VBudget, cts.Token);
        Assert.IsType<AssetVerificationResult.Cancelled>(result);
        Assert.False(result is AssetVerificationResult.Unavailable);
    }

    [Fact]
    public async Task Canceled_caller_corrupt_bytes_still_cancelled()
    {
        // Caller token canceled and the read returns CORRUPT bytes: the post-await
        // caller-cancel check must fire BEFORE the length/hash check.
        var a = Asset("a", "p1", new byte[] { 1, 2, 3 }, "v1");
        using var cts = new CancellationTokenSource();
        var reader = new FakeAssetReader { Content = { [a.AssetId] = new byte[] { 9, 9, 9 } } };
        reader.CancelOnReadAsset = a.AssetId; reader.CancelOnRead = cts;
        var result = await new AssetInventoryVerifier().VerifyAsync(Inventory(a), reader, VBudget, cts.Token);
        Assert.IsType<AssetVerificationResult.Cancelled>(result);
        Assert.False(result is AssetVerificationResult.Unavailable);
    }

    // Deterministic reached/release barrier: no sleeps. The reader blocks on
    // Release (deliberately ignores the cancellation token) to prove cancellation
    // settles even when the foreign port is noncooperative.
    internal sealed class ReadBarrier
    {
        internal readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<byte[]?> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Used;
        internal Task WaitReached() => Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    internal sealed class FakeAssetReader : IAssetByteReader
    {
        internal readonly Dictionary<AssetId, byte[]> Content = new();
        internal readonly Dictionary<AssetId, ReadBarrier> Barriers = new();
        internal AssetId? FailRead;       // throw IOException for this asset
        internal AssetId? ReturnNull;     // return null (unavailable)
        internal AssetId? OversizedAsset;  // return an oversized buffer (hostile/odd)
        internal AssetId? ForeignCancel;   // throw OCE on the reader's OWN token (caller not canceled)
        internal AssetId? CancelOnReadAsset;
        internal CancellationTokenSource? CancelOnRead;
        internal int Reads;
        public Task<byte[]?> ReadAsync(DeclaredAsset asset, CancellationToken cancellationToken)
        {
            Reads++;
            if (Barriers.TryGetValue(asset.AssetId, out var barrier))
            {
                barrier.Used = true; barrier.Reached.TrySetResult();
                return barrier.Release.Task; // deliberately ignores the token
            }
            if (FailRead is { } f && f == asset.AssetId) throw new IOException("synthetic read failure");
            if (ForeignCancel is { } fc && fc == asset.AssetId)
                throw new OperationCanceledException(); // reader's own token; caller token may be uncanceled
            if (CancelOnReadAsset is { } ca && ca == asset.AssetId && CancelOnRead is { } src)
                src.Cancel(); // cancel the CALLER token before returning, so the post-await/catch sees it
            if (ReturnNull is { } n && n == asset.AssetId) return Task.FromResult<byte[]?>(null);
            byte[] bytes;
            if (OversizedAsset is { } o && o == asset.AssetId)
                bytes = new byte[VBudget.MaxAssetBytes + 10]; // hostile oversized read
            else
                bytes = Content[asset.AssetId]; // shared mutable aliased buffer
            return Task.FromResult<byte[]?>(bytes);
        }
    }
}
