using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Client.Analysis;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Client.Workspace.Transfer;
using Xunit;

namespace Workspace.Tests;

/// <summary>
/// Deterministic lifecycle/immutability tests for analysis-snapshot capture (M2a-2a
/// correction) against the REAL WorkspaceCoordinator with a fake
/// IProjectRepository/IWorkspaceLocks. Uses TaskCompletionSource barriers
/// (RunContinuationsAsynchronously) with explicit reached signals — NO timing
/// sleeps. Covers the missing cases: rename/delete/create/close/switch/
/// takeover/restore during a blocked read; recovery reload bumps the factory
/// epoch; all entries freeze before the first await (an imported buffer is
/// mutated while a durable read is paused); caller-buffer mutation after a
/// successful capture cannot mutate immutable bytes; preflight allocation (a 4 MiB
/// import rejected against a tiny limit allocates no second 4 MiB, with
/// binary/deleted entries before text); cancellation on empty project; disposed
/// factory; negative limits; invalid profile; lone-surrogate edited text;
/// read-only capture. No source writes from capture.
/// </summary>
public class AnalysisLifecycleTests
{
    private static readonly ProjectId Pid = ProjectId.New();

    // ---- entry checks ----

    [Fact]
    public async Task Cancelled_Token_On_Empty_Project_Returns_Cancelled()
    {
        var (repo, coord) = NewCoord();
        OpenEmpty(coord, repo);
        var factory = new AnalysisSnapshotFactory(coord);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await factory.CaptureAsync(null, cts.Token);
        Assert.IsType<CaptureResult.Cancelled>(r);
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Cancelled_Token_During_Blocked_Read_Returns_Cancelled()
    {
        var (repo, coord) = NewCoord();
        var (fa, hashA, bytesA) = OpenDurable(coord, repo, "a.tex", "AAA");
        repo.SetContent(hashA, bytesA);
        var gate = repo.GateRead(hashA);
        var factory = new AnalysisSnapshotFactory(coord);
        using var cts = new CancellationTokenSource();
        var cap = factory.CaptureAsync(null, cts.Token);
        await gate.Reached.Task;
        // Cancel WHILE the read is still blocked. With WaitAsync(ct),
        // cancellation finishes WITHOUT the gate releasing (the underlying read may
        // complete later but cannot publish anything).
        cts.Cancel();
        var done = await Task.WhenAny(cap, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(cap, done); // bounded: capture must cancel in time, not hang
        Assert.IsType<CaptureResult.Cancelled>(await cap);
        Assert.Equal(0, repo.CommitCalls);
        gate.Release(bytesA); // finally release the orphaned blocked read for cleanup
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_During_Blocked_Read_Returns_Disposed()
    {
        var (repo, coord) = NewCoord();
        var (fa, hashA, bytesA) = OpenDurable(coord, repo, "a.tex", "AAA");
        repo.SetContent(hashA, bytesA);
        var gate = repo.GateRead(hashA);
        var factory = new AnalysisSnapshotFactory(coord);
        var cap = factory.CaptureAsync(null, CancellationToken.None);
        await gate.Reached.Task;
        // Dispose the factory WHILE the read is still blocked. Disposal does
        // not cancel the in-flight read (the read is blocked on the gate,
        // not cancellable by disposal), so the capture resolves Disposed
        // only after the read completes.
        await factory.DisposeAsync();
        gate.Release(bytesA); // release the orphaned blocked read
        var done = await Task.WhenAny(cap, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(cap, done); // after release, capture must resolve Disposed in time
        var r = await cap;
        Assert.IsType<CaptureResult.Disposed>(r); // not Captured (the round-3 bug)
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Disposed_Factory_Returns_Disposed()
    {
        var (repo, coord) = NewCoord();
        OpenEmpty(coord, repo);
        var factory = new AnalysisSnapshotFactory(coord);
        await factory.DisposeAsync();
        var r = await factory.CaptureAsync(null, CancellationToken.None);
        Assert.IsType<CaptureResult.Disposed>(r);
    }

    [Fact]
    public async Task Negative_Limits_Return_InvalidInput()
    {
        var (repo, coord) = NewCoord();
        OpenEmpty(coord, repo);
        var factory = new AnalysisSnapshotFactory(coord);
        var lim = new AnalysisLimitsDto(-1, null, null, null, null, null, null, null);
        var r = await factory.CaptureAsync(null, CancellationToken.None, lim);
        var inv = Assert.IsType<CaptureResult.InvalidInput>(r);
        Assert.Contains("nonnegative", inv.SafeMessage);
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Invalid_Profile_Returns_InvalidInput()
    {
        var (repo, coord) = NewCoord();
        OpenEmpty(coord, repo);
        var factory = new AnalysisSnapshotFactory(coord);
        // empty version
        var r = await factory.CaptureAsync(new AnalysisProfile("engine-structural", ""), CancellationToken.None);
        Assert.IsType<CaptureResult.InvalidInput>(r);
        // lone-surrogate id (not strict-valid UTF-8)
        r = await factory.CaptureAsync(new AnalysisProfile("a\ud800b", "0"), CancellationToken.None);
        Assert.IsType<CaptureResult.InvalidInput>(r);
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Lone_Surrogate_In_Edited_Text_Returns_InvalidInput()
    {
        var (repo, coord) = NewCoord();
        var (fa, _, _) = OpenDurable(coord, repo, "a.tex", "AAA");
        // Edit in a lone high surrogate; the strict encoder throws (no replacement).
        coord.EditText(fa, "x\ud800y");
        var factory = new AnalysisSnapshotFactory(coord);
        var r = await factory.CaptureAsync(null, CancellationToken.None);
        var inv = Assert.IsType<CaptureResult.InvalidInput>(r);
        Assert.Single(inv.AffectedFiles);
        Assert.Equal(fa, inv.AffectedFiles[0].FileId);
        Assert.Equal(0, repo.CommitCalls);
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Read_Only_Tab_Still_Captures()
    {
        var (repo, coord) = NewCoord(readOnly: true);
        var (fa, hashA, bytesA) = OpenDurable(coord, repo, "a.tex", "AAA");
        repo.SetContent(hashA, bytesA);
        Assert.True(coord.IsReadOnly); // opened read-only (no writer lock)
        var factory = new AnalysisSnapshotFactory(coord);
        var r = await factory.CaptureAsync(null, CancellationToken.None);
        var captured = Assert.IsType<CaptureResult.Captured>(r);
        Assert.Single(captured.Snapshot.Files);
        Assert.Equal(bytesA, captured.Snapshot.Files[0].Bytes.ToArray());
        await factory.DisposeAsync();
    }

    // ---- preflight allocation: quota-before-copy, binary-before-text ----

    [Fact]
    public async Task Preflight_Rejects_Big_Import_Without_Large_Clone_Or_Read()
    {
        var (repo, coord) = NewCoord();
        // Binary (excluded) and a deleted text entry BEFORE the big text import,
        // to catch the old parallel-array mismatch (declaredLengths indexed by the
        // raw Files list including binary/deleted but later indexed by the
        // compressed text list).
        var bin = FileId.New();
        var binBytes = new byte[] { 0xFF };
        var binHash = Sha(binBytes);
        repo.SetContent(binHash, binBytes);
        var deleted = FileId.New();
        var delBytes = B("gone");
        var delHash = Sha(delBytes);
        repo.SetContent(delHash, delBytes);
        OpenWithFiles(coord, repo,
            BinaryFile(bin, "asset.bin", binHash, binBytes.LongLength),
            TextFile(deleted, "del.tex", delHash, delBytes.LongLength, false));
        coord.DeleteFile(deleted); // mark deleted (skipped by capture)

        // Now IMPORT the 4 MiB text file (appended AFTER binary+deleted).
        var big = new byte[4 * 1024 * 1024];
        for (int i = 0; i < big.Length; i++) big[i] = (byte)(i & 0x7F);
        Assert.True(coord.CreateFileFromBytes("big.tex", big, isUserText: false));
        var fid = coord.Files.Single(f => f.Id != bin && f.Id != deleted).Id;

        // Lower the per-file limit so capture rejects the 4 MiB import.
        var lim = new AnalysisLimitsDto(MaxBytesPerFile: 16, null, null, null, null, null, null, null);
        var factory = new AnalysisSnapshotFactory(coord);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var r = await factory.CaptureAsync(null, CancellationToken.None, lim);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var q = Assert.IsType<CaptureResult.QuotaExceeded>(r);
        Assert.Contains("per-file", q.SafeMessage);
        // Rejected BEFORE any async read of the big text import.
        Assert.Equal(0, repo.ReadCalls);
        // Rejected BEFORE cloning the 4 MiB imported buffer: allow a small
        // metadata overhead but NOT another ~4 MiB.
        Assert.True(allocated < 1L * 1024 * 1024, $"allocated {allocated} bytes");
        // The big import buffer is untouched (not cloned into a packet; the
        // rejection happened before pass2 copied any imported buffer).
        Assert.Equal(big, coord.Files.Single(f => f.Id == fid).ImportedBytes);
        await factory.DisposeAsync();
    }

    // ---- mutations during a blocked read are all Superseded ----

    [Theory]
    [InlineData("rename")]
    [InlineData("delete")]
    [InlineData("create")]
    public async Task Tree_Mutation_During_Blocked_Read_Is_Superseded(string kind)
    {
        var (repo, coord) = NewCoord();
        var (fa, hashA, bytesA) = OpenDurable(coord, repo, "a.tex", "AAA");
        repo.SetContent(hashA, bytesA);
        var gate = repo.GateRead(hashA);
        var factory = new AnalysisSnapshotFactory(coord);
        var cap = factory.CaptureAsync(null, CancellationToken.None);
        await gate.Reached.Task;

        if (kind == "rename") Assert.True(coord.RenameFile(fa, "renamed.tex"));
        else if (kind == "delete") Assert.True(coord.DeleteFile(fa));
        else Assert.True(coord.CreateFile("new.tex", "new content"));

        gate.Release(bytesA);
        var r = await cap;
        Assert.IsType<CaptureResult.Superseded>(r);
        Assert.Equal(0, repo.CommitCalls);
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Close_During_Blocked_Read_Is_Superseded()
    {
        var (repo, coord) = NewCoord();
        var (fa, hashA, bytesA) = OpenDurable(coord, repo, "a.tex", "AAA");
        repo.SetContent(hashA, bytesA);
        var gate = repo.GateRead(hashA);
        var factory = new AnalysisSnapshotFactory(coord);
        var cap = factory.CaptureAsync(null, CancellationToken.None);
        await gate.Reached.Task;
        await coord.CloseAsync(CancellationToken.None); // close flushes (clean) + resets
        gate.Release(bytesA);
        var r = await cap;
        Assert.IsType<CaptureResult.Superseded>(r);
        Assert.Null(coord.Project);
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Switch_During_Blocked_Read_Is_Superseded()
    {
        var (repo, coord) = NewCoord();
        var (fa, hashA, bytesA) = OpenDurable(coord, repo, "a.tex", "AAA");
        repo.SetContent(hashA, bytesA);
        var gate = repo.GateRead(hashA);
        var factory = new AnalysisSnapshotFactory(coord);
        var cap = factory.CaptureAsync(null, CancellationToken.None);
        await gate.Reached.Task;
        // Switch to a different project.
        OpenEmptyAt(coord, repo, ProjectId.New());
        gate.Release(bytesA);
        var r = await cap;
        Assert.IsType<CaptureResult.Superseded>(r);
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task TakeOver_During_Blocked_Read_Is_Superseded()
    {
        var (repo, coord) = NewCoord();
        var (fa, hashA, bytesA) = OpenDurable(coord, repo, "a.tex", "AAA");
        repo.SetContent(hashA, bytesA);
        var gate = repo.GateRead(hashA);
        var factory = new AnalysisSnapshotFactory(coord);
        var cap = factory.CaptureAsync(null, CancellationToken.None);
        await gate.Reached.Task;
        Assert.True(await coord.TakeOverAsync(CancellationToken.None));
        gate.Release(bytesA);
        var r = await cap;
        Assert.IsType<CaptureResult.Superseded>(r);
        await factory.DisposeAsync();
    }

    // ---- all entries freeze before the first await ----

    [Fact]
    public async Task Imported_Buffer_Frozen_Before_First_Await_Mutation_Is_Superseded()
    {
        var (repo, coord) = NewCoord();
        // A is durable (blocks on its read); B is a new unchanged import with a
        // caller-owned buffer.
        var (fa, hashA, bytesA) = OpenDurable(coord, repo, "a.tex", "AAA");
        repo.SetContent(hashA, bytesA);
        var bBuffer = B("BOM-free import body");
        Assert.True(coord.CreateFileFromBytes("b.tex", bBuffer, isUserText: false));
        var fb = coord.Files[1];

        var gate = repo.GateRead(hashA);
        var factory = new AnalysisSnapshotFactory(coord);
        var cap = factory.CaptureAsync(null, CancellationToken.None);
        await gate.Reached.Task;

        // While A's read is paused, edit B (a real workspace command): clears the
        // live ImportedBytes, bumps generation + epoch.
        Assert.True(coord.EditText(fb.Id, "edited B content"));
        // Mutate the ORIGINAL caller buffer too — the frozen clone owns a copy.
        bBuffer[0] = (byte)'Z';

        gate.Release(bytesA);
        var r = await cap;
        Assert.IsType<CaptureResult.Superseded>(r);

        // A fresh capture is internally consistent: B is now edited text.
        var fresh = Assert.IsType<CaptureResult.Captured>(await factory.CaptureAsync(null, CancellationToken.None));
        var bCap = fresh.Snapshot.Files.Single(f => f.FileId == fb.Id);
        Assert.Equal(B("edited B content"), bCap.Bytes.ToArray());
        Assert.Equal(0, repo.CommitCalls);
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Caller_Buffer_Mutation_After_Capture_Cannot_Mutate_Bytes()
    {
        var (repo, coord) = NewCoord();
        OpenEmpty(coord, repo);
        var bBuffer = B("original import");
        Assert.True(coord.CreateFileFromBytes("b.tex", bBuffer, isUserText: false));
        var fb = coord.Files[0];
        var factory = new AnalysisSnapshotFactory(coord);
        var captured = Assert.IsType<CaptureResult.Captured>(await factory.CaptureAsync(null, CancellationToken.None));
        var before = captured.Snapshot.Files[0].Bytes.ToArray();
        // Mutate the original caller buffer AFTER capture.
        bBuffer[0] = (byte)'Z';
        Assert.Equal(before, captured.Snapshot.Files[0].Bytes.ToArray());
        Assert.Equal(B("original import"), captured.Snapshot.Files[0].Bytes.ToArray());
        await factory.DisposeAsync();
    }

    // ---- restore during a blocked read replaces bytes without bumping generation ----

    [Fact]
    public async Task Restore_During_Blocked_Read_Is_Superseded_Without_Generation_Bump()
    {
        var (repo, coord) = NewCoord();
        var bytesA = B("AAA");
        var hashA = Sha(bytesA);
        var bytesB = B("BBB");
        var hashB = Sha(bytesB);
        var fa = FileId.New();
        var fb = FileId.New();
        // Open BOTH durable files in one snapshot (two OpenDurable calls would re-open
        // and discard the first file).
        repo.SetContent(hashA, bytesA);
        repo.SetContent(hashB, bytesB);
        OpenWithFiles(coord, repo,
            TextFile(fa, "a.tex", hashA, bytesA.LongLength, false),
            TextFile(fb, "b.tex", hashB, bytesB.LongLength, false));

        // Historical revision restores B to different content (same IDs).
        var restoredB = B("RESTORED B");
        var restoredBHash = Sha(restoredB);
        repo.SetContent(restoredBHash, restoredB);
        var hist = new ProjectRevision(RevisionId.New(), coord.Project!.Id, null,
            ImmutableArray.Create(
                new ProjectFile(fa, "a.tex", "text", hashA, bytesA.LongLength, false),
                new ProjectFile(fb, "b.tex", "text", restoredBHash, restoredB.LongLength, false)),
            DateTimeOffset.UtcNow);
        repo.SeedRevision(hist);
        // Precompute the restore preview BEFORE arming the capture barrier.
        var preview = await coord.CaptureRestorePreviewAsync(hist.Id, CancellationToken.None);

        var gate = repo.GateRead(hashA);
        var factory = new AnalysisSnapshotFactory(coord);
        long genBefore = coord.WorkingGeneration;
        long epochBefore = factory.CurrentWorkspaceEpoch;
        var cap = factory.CaptureAsync(null, CancellationToken.None);
        await gate.Reached.Task;

        // Accept the restore while A's read is paused. Restore replaces B's bytes,
        // advances the durable head, raises WorkspaceChanged (epoch++) but does NOT
        // bump WorkingGeneration.
        var result = await coord.AcceptRestoreAsync(preview, CancellationToken.None);
        Assert.Equal(CommitStatus.Saved, result.Status);
        Assert.Equal(genBefore, coord.WorkingGeneration); // restore does not bump generation
        Assert.True(factory.CurrentWorkspaceEpoch > epochBefore); // but the local epoch moved
        Assert.NotEqual(hashB, coord.Files.Single(f => f.Id == fb).DurableHash);

        gate.Release(bytesA);
        var r = await cap;
        Assert.IsType<CaptureResult.Superseded>(r);
        // Capture never writes; restore used CreateRestoreRevisionAsync, not CommitAsync.
        Assert.Equal(0, repo.CommitCalls);
        await factory.DisposeAsync();
    }

    // ---- recovery reload emits WorkspaceChanged after loaded state and bumps epoch ----

    [Fact]
    public async Task Recovery_Reload_Bumps_Factory_Epoch_And_Fresh_Capture_Works()
    {
        var (repo, coord) = NewCoord();
        var (fa, hashA, bytesA) = OpenDurable(coord, repo, "a.tex", "AAA");
        repo.SetContent(hashA, bytesA);

        // A legitimate damaged-head recovery: the durable head is damaged and a
        // complete checkpoint points to a healthy target revision.
        var target = new ProjectRevision(RevisionId.New(), coord.Project!.Id, null,
            ImmutableArray.Create(new ProjectFile(fa, "a.tex", "text", hashA, bytesA.LongLength, false)),
            DateTimeOffset.UtcNow);
        repo.SeedRevision(target);
        var damagedHead = coord.CurrentRevision!.Id;
        // Replace the loaded snapshot with a damaged one (Recovery set).
        repo.Current = new ProjectSnapshot(
            coord.Project with { }, coord.CurrentRevision!,
            new Dictionary<FileId, string>(), new Dictionary<FileId, byte[]>(),
            new Dictionary<FileId, bool>(), IsReadOnly: false,
            Recovery: new RecoveryReport(damagedHead, new[]
            {
                new HistoryCheckpoint(CheckpointId.New(), coord.Project.Id, target.Id, "auto", DateTimeOffset.UtcNow),
            }));
        await coord.OpenAsync(Pid, CancellationToken.None);
        Assert.NotNull(coord.Recovery);

        var factory = new AnalysisSnapshotFactory(coord);
        long epochBefore = factory.CurrentWorkspaceEpoch;
        var recover = await coord.RecoverToAsync(target.Id, CancellationToken.None);
        Assert.Equal(CommitStatus.Saved, recover.Status);
        // Recovery reload emits WorkspaceChanged AFTER the loaded state.
        Assert.Null(coord.Recovery);
        Assert.True(factory.CurrentWorkspaceEpoch > epochBefore);

        // A fresh capture of the recovered state works.
        var captured = Assert.IsType<CaptureResult.Captured>(await factory.CaptureAsync(null, CancellationToken.None));
        Assert.Equal("AAA", System.Text.Encoding.UTF8.GetString(captured.Snapshot.Files[0].Bytes.ToArray()));
        Assert.Equal(0, repo.CommitCalls);
        await factory.DisposeAsync();
    }

    // ---- storage failures and oversized returns are typed, never leaked ----

    [Fact]
    public async Task Storage_Read_Exception_Returns_Unavailable_No_Leak()
    {
        var (repo, coord) = NewCoord();
        var (fa, hashA, bytesA) = OpenDurable(coord, repo, "a.tex", "AAA");
        repo.SetContent(hashA, bytesA);
        repo.ThrowOnRead = new FileNotFoundException("/opaque/path/xyz");
        var factory = new AnalysisSnapshotFactory(coord);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var r = await factory.CaptureAsync(null, CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var u = Assert.IsType<CaptureResult.Unavailable>(r);
        Assert.Single(u.AffectedFiles);
        Assert.Equal(fa, u.AffectedFiles[0].FileId);
        // Safe message: never leaks the raw storage exception text.
        Assert.DoesNotContain("FileNotFoundException", u.SafeMessage);
        Assert.DoesNotContain("opaque", u.SafeMessage);
        // No decode/hash/copy of the file content happened on the failure path.
        Assert.True(allocated < 64 * 1024, $"allocated {allocated}");
        Assert.Equal(0, repo.CommitCalls);
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Oversized_Returned_Content_Rejected_Before_Decode()
    {
        var (repo, coord) = NewCoord();
        // Durable metadata claims 10 bytes, but the repo returns a 1 MiB buffer.
        var big = new byte[1024 * 1024];
        for (int i = 0; i < big.Length; i++) big[i] = (byte)'a';
        var bigHash = Sha(big);
        var fid = FileId.New();
        repo.SetContent(bigHash, big);
        OpenWithFiles(coord, repo, TextFile(fid, "a.tex", bigHash, 10, false));
        var factory = new AnalysisSnapshotFactory(coord);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var r = await factory.CaptureAsync(null, CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // The length mismatch (actual 1 MiB != declared 10) is caught BEFORE a
        // full UTF-8 decode/hash/immutable copy of the 1 MiB buffer.
        Assert.IsType<CaptureResult.Unavailable>(r);
        Assert.True(allocated < 64 * 1024, $"allocated {allocated} bytes");
        Assert.Equal(0, repo.CommitCalls);
        await factory.DisposeAsync();
    }

    // ---- helpers + fakes ----

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private static SHA256 Sha(byte[] b) => Client.Workspace.Domain.SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(b));
    private static ProjectFile TextFile(FileId id, string path, SHA256 hash, long len, bool hasBom)
        => new(id, path, "text", hash, len, hasBom);
    private static ProjectFile BinaryFile(FileId id, string path, SHA256 hash, long len)
        => new(id, path, "binary", hash, len, false);

    private static (LifecycleRepo repo, WorkspaceCoordinator coord) NewCoord(bool readOnly = false)
    {
        var repo = new LifecycleRepo();
        var locks = new FakeLocks { ReadOnly = readOnly };
        var history = new HistoryService(repo);
        var coord = new WorkspaceCoordinator(repo, locks, history);
        return (repo, coord);
    }

    private static void OpenEmpty(WorkspaceCoordinator coord, LifecycleRepo repo)
        => OpenEmptyAt(coord, repo, Pid);

    private static void OpenEmptyAt(WorkspaceCoordinator coord, LifecycleRepo repo, ProjectId pid)
    {
        repo.Current = Snapshot(pid, Array.Empty<ProjectFile>(),
            new Dictionary<FileId, string>(), new Dictionary<FileId, byte[]>());
        coord.OpenAsync(pid, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static (FileId fa, SHA256 hash, byte[] bytes) OpenDurable(
        WorkspaceCoordinator coord, LifecycleRepo repo, string path, string content)
    {
        var bytes = B(content);
        var hash = Sha(bytes);
        var fid = FileId.New();
        OpenWithFiles(coord, repo, TextFile(fid, path, hash, bytes.LongLength, false));
        return (fid, hash, bytes);
    }

    private static void OpenWithFiles(WorkspaceCoordinator coord, LifecycleRepo repo, params ProjectFile[] files)
    {
        repo.Current = Snapshot(Pid, files, new Dictionary<FileId, string>(), new Dictionary<FileId, byte[]>());
        coord.OpenAsync(Pid, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static ProjectSnapshot Snapshot(
        ProjectId pid, ProjectFile[] files,
        Dictionary<FileId, string> content, Dictionary<FileId, byte[]> bin)
    {
        var rev = new ProjectRevision(RevisionId.New(), pid, null,
            ImmutableArray.CreateRange(files), DateTimeOffset.UtcNow);
        var project = new Project(pid, "p", rev.Id);
        return new ProjectSnapshot(project, rev, content, bin, new Dictionary<FileId, bool>(), IsReadOnly: false);
    }

    private sealed class ReadGate
    {
        public readonly TaskCompletionSource<byte[]> Block =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Reached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Released;
        public void Release(byte[] result)
        {
            Released = true;
            Block.SetResult(result);
        }
        // Await the signal that the capture's read has REACHED the barrier
        // (set by ReadContentAsync before awaiting Block). Do NOT re-set it.
        public Task ReachedTask => Reached.Task;
    }

    private sealed class LifecycleRepo : IProjectRepository
    {
        public ProjectSnapshot? Current;
        public readonly Dictionary<string, byte[]> Content = new();
        public readonly Dictionary<RevisionId, ProjectRevision> Revisions = new();
        public readonly HashSet<SHA256> ReadHashes = new();
        public int ReadCalls;
        public int CommitCalls;
        public Exception? ThrowOnRead;
        private readonly Dictionary<SHA256, ReadGate> _gates = new();

        public bool WasRead(SHA256 h) => ReadHashes.Contains(h);
        public void SetContent(SHA256 h, byte[] b) => Content[h.Hex] = b;
        public void SeedRevision(ProjectRevision r) => Revisions[r.Id] = r;

        public ReadGate GateRead(SHA256 h)
        {
            var g = new ReadGate();
            _gates[h] = g;
            return g;
        }

        public Task<ProjectSnapshot> OpenAsync(ProjectId id, CancellationToken ct)
            => Task.FromResult(Current ?? throw new InvalidOperationException("no snapshot"));

        public async Task<byte[]> ReadContentAsync(SHA256 h, CancellationToken ct)
        {
            ReadCalls++;
            ReadHashes.Add(h);
            if (_gates.TryGetValue(h, out var g))
            {
                _gates.Remove(h); // one-shot gate: a re-read is not blocked
                g.Reached.SetResult(true);
                await g.Block.Task;
            }
            if (ThrowOnRead is { } ex) throw ex;
            return Content.TryGetValue(h.Hex, out var b) ? b : Array.Empty<byte>();
        }

        public Task<RevisionManifest> ReadRevisionAsync(ProjectId p, RevisionId r, CancellationToken ct)
        {
            if (!Revisions.TryGetValue(r, out var rev) || rev.ProjectId != p)
                return Task.FromResult(RevisionManifest.Damaged(
                    new ProjectRevision(r, p, null, ImmutableArray<ProjectFile>.Empty, DateTimeOffset.UtcNow), "missing"));
            foreach (var f in rev.Files)
                if (!Content.ContainsKey(f.ContentHash.Hex))
                    return Task.FromResult(RevisionManifest.Damaged(rev, $"missing {f.RelativePath}"));
            return Task.FromResult(RevisionManifest.Ok(rev));
        }

        public Task<CommitResult> CommitAsync(ProjectCommit b, RevisionId? eh, CancellationToken ct)
        {
            CommitCalls++;
            foreach (var c in b.Content) Content[c.Hash.Hex] = c.Bytes;
            return Task.FromResult(CommitResult.Saved(b.SelectedHead.Id));
        }

        public Task<CommitResult> CreateRestoreRevisionAsync(
            ProjectId p, RevisionId eh, ProjectRevision r, RevisionId src, string reason, CancellationToken ct)
        {
            Revisions[r.Id] = r;
            // Reflect the restored file set in the snapshot used by the re-read.
            Current = new ProjectSnapshot(
                new Project(p, "p", r.Id), r, new Dictionary<FileId, string>(),
                new Dictionary<FileId, byte[]>(), new Dictionary<FileId, bool>(), IsReadOnly: false);
            return Task.FromResult(CommitResult.Saved(r.Id));
        }

        public Task<CommitResult> RepairHeadAsync(ProjectId p, RevisionId e, RevisionId n, CancellationToken ct)
        {
            // Recovery repair: replace the damaged head with the target revision.
            var rev = Revisions[n];
            Current = new ProjectSnapshot(
                new Project(p, "p", n), rev, new Dictionary<FileId, string>(),
                new Dictionary<FileId, byte[]>(), new Dictionary<FileId, bool>(), IsReadOnly: false);
            return Task.FromResult(CommitResult.Saved(n));
        }

        public Task<HistoryPage> ReadHistoryAsync(ProjectId id, HistoryCursor? cursor, CancellationToken ct)
            => Task.FromResult(new HistoryPage(Array.Empty<HistoryCheckpoint>()));
        public Task<Project> CreateProjectAsync(string n, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteProjectAsync(ProjectId id, CancellationToken ct) => throw new NotImplementedException();
        public Task<ImportPublishResult> PublishImportAsync(StagedProject s, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FakeLocks : IWorkspaceLocks
    {
        public bool ReadOnly;
        private readonly HashSet<string> _held = new();
        public Task<WorkspaceCapabilities> GetCapabilitiesAsync(CancellationToken ct)
            => Task.FromResult(new WorkspaceCapabilities(true, true, true, true));
        public Task<string?> TryAcquireAsync(string name, CancellationToken ct)
        { if (ReadOnly) return Task.FromResult<string?>(null); var id = "lock:" + name; _held.Add(id); return Task.FromResult<string?>(id); }
        public Task<string> WaitForAsync(string name, CancellationToken ct)
        { var id = "lock:" + name; _held.Add(id); return Task.FromResult(id); }
        public Task<bool> ReleaseAsync(string lockId, CancellationToken ct) => Task.FromResult(_held.Remove(lockId));
        public Task<bool> IsHeldAsync(string name, CancellationToken ct) => Task.FromResult(_held.Contains("lock:" + name));
        public bool IsOwnedLocally(string id, string name) => id == "lock:" + name && _held.Contains(id);
        public Task<bool> OwnsAsync(string id, string name, CancellationToken ct) => Task.FromResult(IsOwnedLocally(id, name));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
