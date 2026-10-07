using System.Collections.Generic;
using System.Collections.Immutable;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Client.Workspace.Transfer;
using Xunit;

namespace Workspace.Tests;

/// <summary>
/// The single WorkspaceChanged invalidation seam (M2a-2a): it is raised
/// synchronously and immediately for edits/tree mutations (via BumpGeneration),
/// for a session reset, for a takeover reset, AFTER a completed load
/// (open/takeover/recover), and ONCE at the end of a successful restore AFTER
/// synchronous reconciliation — without bumping WorkingGeneration for the
/// restore. M1b generation semantics are unchanged (no data-loss behavior
/// change). Uses the REAL WorkspaceCoordinator with a fake repo/locks.
/// </summary>
public class WorkspaceChangedTests
{
    private static readonly ProjectId Pid = ProjectId.New();

    [Fact]
    public async Task Edit_Raises_Immediately_And_Bumps_Generation()
    {
        var (repo, coord) = NewCoordinator();
        OpenWithFiles(coord, repo, out var f1, out _);
        int count = 0;
        long gen = coord.WorkingGeneration;
        coord.WorkspaceChanged += () => count++;

        Assert.True(coord.EditText(f1, "changed"));
        Assert.Equal(1, count);
        Assert.True(coord.WorkingGeneration > gen);
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Tree_Mutations_Raise_And_Bump_Generation()
    {
        var (repo, coord) = NewCoordinator();
        OpenWithFiles(coord, repo, out var f1, out var bin);
        int count = 0;
        coord.WorkspaceChanged += () => count++;
        long gen0 = coord.WorkingGeneration;

        Assert.True(coord.CreateFile("new.tex", "x")); Assert.Equal(1, count);
        Assert.True(coord.RenameFile(f1, "renamed.tex")); Assert.Equal(2, count);
        Assert.True(coord.DeleteFile(f1)); Assert.Equal(3, count);
        Assert.True(coord.ReplaceFileBytes(bin, new byte[] { 1, 2, 3 })); Assert.Equal(4, count);
        Assert.True(coord.WorkingGeneration > gen0);
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Close_Resets_And_Raises()
    {
        var (repo, coord) = NewCoordinator();
        OpenWithFiles(coord, repo, out _, out _);
        int count = 0;
        coord.WorkspaceChanged += () => count++;
        await coord.CloseAsync(CancellationToken.None);
        Assert.Equal(1, count);
        Assert.Null(coord.Project);
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Open_Raises_On_Reset_Then_After_Load()
    {
        var (repo, coord) = NewCoordinator();
        int count = 0;
        coord.WorkspaceChanged += () => count++;
        OpenWithFiles(coord, repo, out _, out _);
        // ResetSession (immediate) + completed LoadSnapshotAsync (after load).
        Assert.Equal(2, count);
        Assert.NotNull(coord.Project);
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task TakeOver_Raises_On_Reset_Then_After_Load()
    {
        var (repo, coord) = NewCoordinator();
        OpenWithFiles(coord, repo, out _, out _);
        int count = 0;
        coord.WorkspaceChanged += () => count++;
        Assert.True(await coord.TakeOverAsync(CancellationToken.None));
        // TakeOver resets session (immediate) then loads (after load).
        Assert.Equal(2, count);
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Restore_Raises_Once_At_End_Without_Generation_Bump()
    {
        var (repo, coord) = NewCoordinator();
        OpenWithFiles(coord, repo, out _, out _);
        // Seed a historical revision (the file renamed, same content hash) and
        // capture a restore preview against it.
        var hist = repo.SeedHistorical(Pid, RenamedFile(coord));
        var preview = await coord.CaptureRestorePreviewAsync(hist, CancellationToken.None);

        int count = 0;
        long genBefore = coord.WorkingGeneration;
        coord.WorkspaceChanged += () => count++;

        var result = await coord.AcceptRestoreAsync(preview, CancellationToken.None);
        Assert.Equal(CommitStatus.Saved, result.Status);
        // The restore's OWN raise is exactly one, at the end; the restore path does
        // NOT bump WorkingGeneration to notify.
        Assert.Equal(1, count);
        Assert.Equal(genBefore, coord.WorkingGeneration);
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Restore_With_Concurrent_Edit_Raises_Twice_And_Preserves_Edit()
    {
        var (repo, coord) = NewCoordinator();
        OpenWithFiles(coord, repo, out var f1, out _);
        var hist = repo.SeedHistorical(Pid, RenamedFile(coord));
        var preview = await coord.CaptureRestorePreviewAsync(hist, CancellationToken.None);

        int count = 0;
        long genBefore = coord.WorkingGeneration;
        coord.WorkspaceChanged += () => count++;

        // Pause the restore's re-read (OpenAsync) so an edit lands during the I/O.
        repo.OpenGate = new TaskCompletionSource<object>();
        var accept = coord.AcceptRestoreAsync(preview, CancellationToken.None);
        Assert.True(coord.EditText(f1, "during-restore edit")); // bumps gen + raises
        repo.OpenGate.SetResult(true); // release the re-read

        var result = await accept;
        Assert.Equal(CommitStatus.Saved, result.Status);
        // One raise from the concurrent edit (BumpGeneration) + one from the
        // restore's end. The during-I/O edit is superimposed (kept dirty),
        // not silently overwritten.
        Assert.Equal(2, count);
        Assert.True(coord.WorkingGeneration > genBefore);
        Assert.Equal(SavePhase.Dirty, coord.SavePhase); // the edit stays unsaved
        await coord.DisposeAsync();
    }

    // ---- helpers + fakes ----

    private static (RestoreRepo repo, WorkspaceCoordinator coord) NewCoordinator()
    {
        var repo = new RestoreRepo();
        var locks = new FakeLocks();
        var history = new HistoryService(repo);
        var coord = new WorkspaceCoordinator(repo, locks, history);
        return (repo, coord);
    }

    private static void OpenWithFiles(WorkspaceCoordinator coord, RestoreRepo repo, out FileId f1, out FileId bin)
    {
        var bytes = B("hello");
        var hash = Sha(bytes);
        f1 = FileId.New();
        bin = FileId.New();
        var binBytes = new byte[] { 9, 9 };
        var binHash = Sha(binBytes);
        repo.SetContent(hash, bytes);
        repo.SetContent(binHash, binBytes);
        var files = new[] {
            new ProjectFile(f1, "main.tex", "text", hash, bytes.LongLength, false),
            new ProjectFile(bin, "asset.bin", "binary", binHash, binBytes.LongLength, false),
        };
        repo.Current = Snapshot(Pid, files);
        coord.OpenAsync(Pid, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void OpenWithBinary(WorkspaceCoordinator coord, RestoreRepo repo, FileId bin)
    {
        var bytes = new byte[] { 9, 9 };
        var hash = Sha(bytes);
        repo.SetContent(hash, bytes);
        var pf = new ProjectFile(bin, "asset.bin", "binary", hash, bytes.LongLength, false);
        repo.Current = Snapshot(Pid, new[] { pf });
        coord.OpenAsync(Pid, CancellationToken.None).GetAwaiter().GetResult();
    }

    // A historical revision: rename main.tex -> old/main.tex, same content hash.
    private static ProjectRevision RenamedFile(WorkspaceCoordinator coord)
    {
        var file = coord.Files[0];
        return new ProjectRevision(
            RevisionId.New(), coord.Project!.Id, null,
            ImmutableArray.Create(new ProjectFile(file.Id, "old/main.tex", "text", file.DurableHash!.Value, file.DurableLength, file.HasBom)),
            DateTimeOffset.UtcNow);
    }

    private static ProjectSnapshot Snapshot(ProjectId pid, ProjectFile[] files)
    {
        var rev = new ProjectRevision(RevisionId.New(), pid, null,
            ImmutableArray.CreateRange(files), DateTimeOffset.UtcNow);
        var project = new Project(pid, "p", rev.Id);
        return new ProjectSnapshot(project, rev, new Dictionary<FileId, string>(), new Dictionary<FileId, byte[]>(),
            new Dictionary<FileId, bool>(), IsReadOnly: false);
    }

    private static byte[] B(string s) => System.Text.Encoding.UTF8.GetBytes(s);
    private static SHA256 Sha(byte[] b) => Client.Workspace.Domain.SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(b));

    private sealed class RestoreRepo : IProjectRepository
    {
        public ProjectSnapshot? Current;
        public readonly Dictionary<string, byte[]> Content = new();
        public readonly Dictionary<RevisionId, ProjectRevision> Revisions = new();
        public TaskCompletionSource<object>? OpenGate;
        public int CommitCalls;

        public void SetContent(SHA256 h, byte[] b) => Content[h.Hex] = b;
        public RevisionId SeedHistorical(ProjectId pid, ProjectRevision rev)
        {
            Revisions[rev.Id] = rev;
            return rev.Id;
        }

        public async Task<ProjectSnapshot> OpenAsync(ProjectId id, CancellationToken ct)
        {
            if (OpenGate is { } g) await g.Task;
            return Current ?? throw new InvalidOperationException("no snapshot");
        }

        public Task<byte[]> ReadContentAsync(SHA256 h, CancellationToken ct)
            => Task.FromResult(Content.TryGetValue(h.Hex, out var b) ? b : Array.Empty<byte>());

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

        public Task<CommitResult> CreateRestoreRevisionAsync(ProjectId p, RevisionId eh, ProjectRevision r, RevisionId src, string reason, CancellationToken ct)
        {
            Revisions[r.Id] = r;
            return Task.FromResult(CommitResult.Saved(r.Id));
        }

        public Task<HistoryPage> ReadHistoryAsync(ProjectId id, HistoryCursor? cursor, CancellationToken ct)
            => Task.FromResult(new HistoryPage(Array.Empty<HistoryCheckpoint>()));
        public Task<Project> CreateProjectAsync(string n, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteProjectAsync(ProjectId id, CancellationToken ct) => throw new NotImplementedException();
        public Task<CommitResult> RepairHeadAsync(ProjectId p, RevisionId e, RevisionId n, CancellationToken ct) => throw new NotImplementedException();
        public Task<ImportPublishResult> PublishImportAsync(StagedProject s, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FakeLocks : IWorkspaceLocks
    {
        public Task<WorkspaceCapabilities> GetCapabilitiesAsync(CancellationToken ct)
            => Task.FromResult(new WorkspaceCapabilities(true, true, true, true));
        public Task<string?> TryAcquireAsync(string name, CancellationToken ct)
            => Task.FromResult<string?>("lock:" + name);
        public Task<string> WaitForAsync(string name, CancellationToken ct)
            => Task.FromResult("lock:" + name);
        public Task<bool> ReleaseAsync(string lockId, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> IsHeldAsync(string name, CancellationToken ct) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
