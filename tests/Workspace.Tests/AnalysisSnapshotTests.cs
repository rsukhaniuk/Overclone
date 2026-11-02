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
/// Immutable analysis-snapshot capture (M2a-2a) against the REAL
/// WorkspaceCoordinator with a fake IProjectRepository/IWorkspaceLocks. Covers:
/// successful capture of durable/edited/imported text; binary excluded without
/// reads; lower-limit preflight before any read; missing/corrupt durable
/// bytes => Unavailable (no empty substitution); cancellation => Cancelled;
/// empty project => captured empty; no project => NoProject; failed local save
/// still captures the dirty snapshot with SavePhase/head/dirty unchanged; and
/// the WorkspaceChanged epoch invalidates a capture during a blocked read.
/// Deterministic barriers, not timing sleeps.
/// </summary>
public class AnalysisSnapshotTests
{
    private static readonly ProjectId Pid = ProjectId.New();

    [Fact]
    public async Task No_Project_Is_NoProject()
    {
        var (repo, coord) = NewCoordinator();
        var factory = new AnalysisSnapshotFactory(coord);
        var r = await factory.CaptureAsync(null, CancellationToken.None);
        Assert.IsType<CaptureResult.NoProject>(r);
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Empty_Project_Captures_Empty_Snapshot()
    {
        var (repo, coord) = NewCoordinator();
        OpenEmpty(coord, repo);
        var factory = new AnalysisSnapshotFactory(coord);
        var r = await factory.CaptureAsync(null, CancellationToken.None);
        var captured = Assert.IsType<CaptureResult.Captured>(r);
        Assert.Empty(captured.Snapshot.Files);
        Assert.Empty(captured.Snapshot.Excluded);
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Durable_Text_Captures_Exact_Bytes_And_Hash()
    {
        var (repo, coord) = NewCoordinator();
        var bytes = B("Привіт 🇺🇦 e\u0301\n");
        var hash = Sha(bytes);
        var fid = FileId.New();
        OpenWithFiles(coord, repo, TextFile(fid, "main.tex", hash, bytes.LongLength, hasBom: false));
        repo.SetContent(hash, bytes);
        var factory = new AnalysisSnapshotFactory(coord);
        var r = await factory.CaptureAsync(null, CancellationToken.None);
        var captured = Assert.IsType<CaptureResult.Captured>(r);
        Assert.Single(captured.Snapshot.Files);
        var f = captured.Snapshot.Files[0];
        Assert.Equal(fid, f.FileId);
        Assert.Equal("main.tex", f.RelativePath);
        Assert.Equal(bytes.LongLength, f.ByteLength);
        Assert.Equal(hash, f.Sha256);
        Assert.Equal(bytes, f.Bytes.ToArray());
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Binary_Files_Are_Excluded_Without_Reading_Content()
    {
        var (repo, coord) = NewCoordinator();
        var binBytes = new byte[] { 0xFF, 0xFE, 0x00, 0x01 };
        var binHash = Sha(binBytes);
        var bin = FileId.New();
        var textBytes = B("hello");
        var textHash = Sha(textBytes);
        var text = FileId.New();
        OpenWithFiles(coord, repo,
            TextFile(text, "a.tex", textHash, textBytes.LongLength, false),
            BinaryFile(bin, "img.png", binHash, binBytes.LongLength));
        repo.SetContent(textHash, textBytes);
        // Binary hash is intentionally NOT stored: any read would return empty + count.
        var factory = new AnalysisSnapshotFactory(coord);
        var r = await factory.CaptureAsync(null, CancellationToken.None);
        var captured = Assert.IsType<CaptureResult.Captured>(r);
        Assert.Single(captured.Snapshot.Files);          // only the text file
        Assert.Single(captured.Snapshot.Excluded);      // binary metadata-only
        var ex = captured.Snapshot.Excluded[0];
        Assert.Equal(bin, ex.FileId);
        Assert.Equal("binary", ex.MediaKind);
        Assert.False(repo.WasRead(binHash));           // binary content never read
        Assert.True(repo.WasRead(textHash));           // text content read
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Lower_Limit_Preflight_Rejects_Before_Any_Read()
    {
        var (repo, coord) = NewCoordinator();
        var bytes = B("this is more than ten bytes");
        var hash = Sha(bytes);
        var fid = FileId.New();
        OpenWithFiles(coord, repo, TextFile(fid, "big.tex", hash, bytes.LongLength, false));
        var factory = new AnalysisSnapshotFactory(coord);
        var limits = new AnalysisLimitsDto(
            MaxBytesPerFile: 10, MaxFiles: null, MaxTotalBytes: null, MaxResultBytes: null,
            null, null, null, null);
        var r = await factory.CaptureAsync(null, CancellationToken.None, limits);
        var q = Assert.IsType<CaptureResult.QuotaExceeded>(r);
        Assert.Contains("per-file", q.SafeMessage);
        Assert.Equal(0, repo.TotalReadCalls); // rejected before any async read
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Missing_Durable_Bytes_Is_Unavailable_No_Empty_Substitute()
    {
        var (repo, coord) = NewCoordinator();
        var bytes = B("chapter one");
        var hash = Sha(bytes);
        var fid = FileId.New();
        OpenWithFiles(coord, repo, TextFile(fid, "a.tex", hash, bytes.LongLength, false));
        // repo content NOT stored -> ReadContentAsync returns empty (length mismatch).
        var factory = new AnalysisSnapshotFactory(coord);
        var r = await factory.CaptureAsync(null, CancellationToken.None);
        var u = Assert.IsType<CaptureResult.Unavailable>(r);
        Assert.Single(u.AffectedFiles);
        Assert.Equal(fid, u.AffectedFiles[0].FileId);
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Corrupt_Durable_Bytes_Wrong_Hash_Is_Unavailable()
    {
        var (repo, coord) = NewCoordinator();
        var realBytes = B("chapter one");
        var realHash = Sha(realBytes);
        var corruptBytes = B("chapter TWO"); // same length, valid UTF-8, wrong hash
        var fid = FileId.New();
        OpenWithFiles(coord, repo, TextFile(fid, "a.tex", realHash, realBytes.LongLength, false));
        repo.SetContent(realHash, corruptBytes);
        var factory = new AnalysisSnapshotFactory(coord);
        var r = await factory.CaptureAsync(null, CancellationToken.None);
        Assert.IsType<CaptureResult.Unavailable>(r);
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Cancellation_Returns_Cancelled()
    {
        var (repo, coord) = NewCoordinator();
        var bytes = B("hello");
        var hash = Sha(bytes);
        var fid = FileId.New();
        OpenWithFiles(coord, repo, TextFile(fid, "a.tex", hash, bytes.LongLength, false));
        repo.SetContent(hash, bytes);
        var factory = new AnalysisSnapshotFactory(coord);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await factory.CaptureAsync(null, cts.Token);
        Assert.IsType<CaptureResult.Cancelled>(r);
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Edit_During_Blocked_Read_Is_Superseded()
    {
        var (repo, coord) = NewCoordinator();
        var bytesA = B("file A content");
        var hashA = Sha(bytesA);
        var fa = FileId.New();
        OpenWithFiles(coord, repo, TextFile(fa, "a.tex", hashA, bytesA.LongLength, false));
        repo.SetContent(hashA, bytesA);
        repo.ReadGate = new TaskCompletionSource<byte[]>(); // block the durable read

        var factory = new AnalysisSnapshotFactory(coord);
        var cap = factory.CaptureAsync(null, CancellationToken.None);
        // While the read is blocked, edit the SAME file (bumps generation +
        // raises WorkspaceChanged -> local epoch++).
        Assert.True(coord.EditText(fa, "mutated during I/O"));
        repo.ReadGate.SetResult(bytesA); // release the read
        var r = await cap;
        Assert.IsType<CaptureResult.Superseded>(r);
        Assert.Equal(0, repo.CommitCalls); // analysis never writes
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Snapshot_Bytes_Immune_To_Later_Live_Mutation()
    {
        var (repo, coord) = NewCoordinator();
        var bytes = B("original content");
        var hash = Sha(bytes);
        var fid = FileId.New();
        OpenWithFiles(coord, repo, TextFile(fid, "a.tex", hash, bytes.LongLength, false));
        repo.SetContent(hash, bytes);
        var factory = new AnalysisSnapshotFactory(coord);
        var captured = Assert.IsType<CaptureResult.Captured>(await factory.CaptureAsync(null, CancellationToken.None));
        var snap = captured.Snapshot.Files[0];
        var snapBytes = snap.Bytes.ToArray();
        // Mutate the live file AFTER capture (the snapshot is frozen + owned).
        coord.EditText(fid, "totally different content");
        Assert.Equal(snapBytes, snap.Bytes.ToArray());
        Assert.Equal(B("original content"), snap.Bytes.ToArray());
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Imported_Bytes_Are_Frozen()
    {
        var (repo, coord) = NewCoordinator();
        OpenEmpty(coord, repo);
        var imported = B("BOM-free CRLF\r\nbody");
        Assert.True(coord.CreateFileFromBytes("imp.tex", imported, isUserText: false));
        var factory = new AnalysisSnapshotFactory(coord);
        var captured = Assert.IsType<CaptureResult.Captured>(await factory.CaptureAsync(null, CancellationToken.None));
        var f = captured.Snapshot.Files[0];
        Assert.Equal(imported, f.Bytes.ToArray());
        Assert.Equal(imported.LongLength, f.ByteLength);
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Failed_Save_Still_Captures_Dirty_And_Leaves_Save_Unchanged()
    {
        var (repo, coord) = NewCoordinator();
        var durable = B("durable");
        var durableHash = Sha(durable);
        var fid = FileId.New();
        OpenWithFiles(coord, repo, TextFile(fid, "a.tex", durableHash, durable.LongLength, false));
        repo.SetContent(durableHash, durable);
        Assert.True(coord.EditText(fid, "edited dirty text"));
        repo.NextCommitFails = true;
        var save = await coord.SaveAsync(CancellationToken.None);
        Assert.Equal(CommitStatus.StorageFailure, save.Status);
        Assert.Equal(SavePhase.Failed, coord.SavePhase);
        RevisionId? headBefore = coord.Project!.HeadRevisionId;
        Assert.True(coord.IsDirty);

        var factory = new AnalysisSnapshotFactory(coord);
        var captured = Assert.IsType<CaptureResult.Captured>(await factory.CaptureAsync(null, CancellationToken.None));
        var f = captured.Snapshot.Files[0];
        // Edited text serializes LF + BOM-off; captured bytes are the encoded
        // dirty content, not the durable bytes.
        Assert.Equal(TextContent.EncodeEdited("edited dirty text", false), f.Bytes.ToArray());
        Assert.Equal(SavePhase.Failed, coord.SavePhase);
        Assert.Equal(headBefore, coord.Project.HeadRevisionId);
        Assert.True(coord.IsDirty);
        Assert.Equal(1, repo.CommitCalls); // only the failed save; capture did not write
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task File_Count_Over_Limit_Is_QuotaExceeded()
    {
        var (repo, coord) = NewCoordinator();
        var files = new List<ProjectFile>();
        for (int i = 0; i < 3; i++)
        {
            var b = B("c" + i);
            files.Add(TextFile(FileId.New(), "f" + i + ".tex", Sha(b), b.LongLength, false));
        }
        OpenWithFiles(coord, repo, files.ToArray());
        var factory = new AnalysisSnapshotFactory(coord);
        var limits = new AnalysisLimitsDto(null, MaxFiles: 2, null, null, null, null, null, null);
        var r = await factory.CaptureAsync(null, CancellationToken.None, limits);
        Assert.IsType<CaptureResult.QuotaExceeded>(r);
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Total_Bytes_Over_Limit_Is_QuotaExceeded()
    {
        var (repo, coord) = NewCoordinator();
        var a = B("0123456789"); // 10 bytes
        var b = B("9876543210"); // 10 bytes
        var fa = FileId.New(); var fb = FileId.New();
        OpenWithFiles(coord, repo,
            TextFile(fa, "a.tex", Sha(a), a.LongLength, false),
            TextFile(fb, "b.tex", Sha(b), b.LongLength, false));
        var factory = new AnalysisSnapshotFactory(coord);
        var limits = new AnalysisLimitsDto(null, null, MaxTotalBytes: 15, null, null, null, null, null);
        var r = await factory.CaptureAsync(null, CancellationToken.None, limits);
        Assert.IsType<CaptureResult.QuotaExceeded>(r);
        await factory.DisposeAsync();
    }

    // ---- helpers + fakes ----

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private static SHA256 Sha(byte[] b) => Client.Workspace.Domain.SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(b));

    private static ProjectFile TextFile(FileId id, string path, SHA256 hash, long len, bool hasBom)
        => new(id, path, "text", hash, len, hasBom);

    private static ProjectFile BinaryFile(FileId id, string path, SHA256 hash, long len)
        => new(id, path, "binary", hash, len, false);

    private static (FakeRepo repo, WorkspaceCoordinator coord) NewCoordinator()
    {
        var repo = new FakeRepo();
        var locks = new FakeLocks();
        var history = new HistoryService(repo);
        var coord = new WorkspaceCoordinator(repo, locks, history);
        return (repo, coord);
    }

    private static void OpenEmpty(WorkspaceCoordinator coord, FakeRepo repo)
    {
        repo.Current = Snapshot(Pid, Array.Empty<ProjectFile>(), new Dictionary<FileId, string>(), new Dictionary<FileId, byte[]>());
        coord.OpenAsync(Pid, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void OpenWithFiles(WorkspaceCoordinator coord, FakeRepo repo, params ProjectFile[] files)
    {
        repo.Current = Snapshot(Pid, files, new Dictionary<FileId, string>(), new Dictionary<FileId, byte[]>());
        coord.OpenAsync(Pid, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static ProjectSnapshot Snapshot(
        ProjectId pid, ProjectFile[] files,
        Dictionary<FileId, string> content, Dictionary<FileId, byte[]> bin,
        Dictionary<FileId, bool>? bom = null)
    {
        var project = new Project(pid, "p", null);
        var rev = new ProjectRevision(RevisionId.New(), pid, null,
            ImmutableArray.CreateRange(files), DateTimeOffset.UtcNow);
        return new ProjectSnapshot(project, rev, content, bin, bom ?? new(), IsReadOnly: false);
    }

    private sealed class FakeRepo : IProjectRepository
    {
        public ProjectSnapshot? Current;
        public readonly Dictionary<string, byte[]> Content = new();   // content by hash hex
        public readonly HashSet<string> ReadHashes = new();
        public TaskCompletionSource<byte[]>? ReadGate;
        public int TotalReadCalls;
        public int CommitCalls;
        public bool NextCommitFails;

        public bool WasRead(SHA256 h) => ReadHashes.Contains(h.Hex);
        public void SetContent(SHA256 h, byte[] b) => Content[h.Hex] = b;

        public Task<ProjectSnapshot> OpenAsync(ProjectId id, CancellationToken ct)
            => Task.FromResult(Current ?? throw new InvalidOperationException("no snapshot"));
        public async Task<byte[]> ReadContentAsync(SHA256 h, CancellationToken ct)
        {
            TotalReadCalls++;
            ReadHashes.Add(h.Hex);
            if (ReadGate is { } g) await g.Task;
            return Content.TryGetValue(h.Hex, out var b) ? b : Array.Empty<byte>();
        }
        public Task<CommitResult> CommitAsync(ProjectCommit b, RevisionId? eh, CancellationToken ct)
        {
            CommitCalls++;
            if (NextCommitFails) return Task.FromResult(CommitResult.Failure("StorageFailure", "save failed"));
            foreach (var c in b.Content) Content[c.Hash.Hex] = c.Bytes;
            return Task.FromResult(CommitResult.Saved(b.SelectedHead.Id));
        }
        public Task<HistoryPage> ReadHistoryAsync(ProjectId id, HistoryCursor? cursor, CancellationToken ct)
            => Task.FromResult(new HistoryPage(Array.Empty<HistoryCheckpoint>()));
        public Task<Project> CreateProjectAsync(string n, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteProjectAsync(ProjectId id, CancellationToken ct) => throw new NotImplementedException();
        public Task<CommitResult> RepairHeadAsync(ProjectId p, RevisionId e, RevisionId n, CancellationToken ct) => throw new NotImplementedException();
        public Task<RevisionManifest> ReadRevisionAsync(ProjectId p, RevisionId r, CancellationToken ct) => throw new NotImplementedException();
        public Task<ImportPublishResult> PublishImportAsync(StagedProject s, CancellationToken ct) => throw new NotImplementedException();
        public Task<CommitResult> CreateRestoreRevisionAsync(ProjectId p, RevisionId eh, ProjectRevision r, RevisionId s, string reason, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class FakeLocks : IWorkspaceLocks
    {
        private readonly HashSet<string> _held = new();
        public Task<WorkspaceCapabilities> GetCapabilitiesAsync(CancellationToken ct)
            => Task.FromResult(new WorkspaceCapabilities(true, true, true, true));
        public Task<string?> TryAcquireAsync(string name, CancellationToken ct)
        { var id = "lock:" + name; _held.Add(id); return Task.FromResult<string?>(id); }
        public Task<string> WaitForAsync(string name, CancellationToken ct)
        { var id = "lock:" + name; _held.Add(id); return Task.FromResult(id); }
        public Task<bool> ReleaseAsync(string lockId, CancellationToken ct) => Task.FromResult(_held.Remove(lockId));
        public Task<bool> IsHeldAsync(string name, CancellationToken ct) => Task.FromResult(_held.Contains("lock:" + name));
        public bool IsOwnedLocally(string id, string name) => id == "lock:" + name && _held.Contains(id);
        public Task<bool> OwnsAsync(string id, string name, CancellationToken ct) => Task.FromResult(IsOwnedLocally(id, name));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
