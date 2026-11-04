using System.Collections.Immutable;
using System.Text;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Client.Workspace.Patching;
using Client.Workspace.Transfer;
using Xunit;

namespace Workspace.Tests.SaveRecovery;

// Modeled repository/lock boundaries, NOT OPFS/native browser proof. No reflection.
public class CorruptHeadRecoveryTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("readonly")][InlineData("dirty")][InlineData("unlisted")][InlineData("foreign-checkpoint")][InlineData("unverified")][InlineData("foreign-target")][InlineData("local-lock")]
    public async Task Invalid_recovery_cannot_repair_or_drop_retained_state(string mode)
    {
        await using var h = await Host.Open(readonlyMode: mode == "readonly");
        if (mode == "dirty") h.W.Files.Add(new WorkingFile(FileId.New(), "retained.tex", "text", "retained🙂", null, false, true, false, false, true, false, null, 0, null));
        if (mode == "local-lock") h.L.Local = false;
        var target = mode == "unlisted" ? RevisionId.New() : h.R.B.Id;
        if (mode == "foreign-checkpoint") h.R.Choices[0] = h.R.Choices[0] with { ProjectId = h.R.Other.Id };
        if (mode == "unverified") h.R.Verified = false;
        if (mode == "foreign-target") h.R.ForeignTarget = true;
        var state = h.State(); Assert.Equal(0, h.R.Repairs); // open itself must not repair
        var result = await h.W.RecoverToAsync(target, default);
        Assert.NotEqual(CommitStatus.Saved, result.Status); Assert.Equal(0, h.R.Repairs); Assert.Equal(h.R.C.Id, h.R.Head);
        Assert.Equal(state, h.State());
        Assert.Equal(mode is "unverified" or "foreign-target" ? 1 : 0, h.R.Reads);
    }

    [Theory, InlineData(1), InlineData(2)]
    public async Task Native_ownership_loss_at_each_preinvoke_boundary_refuses_and_retains_recovery(int loseAt)
    {
        await using var h = await Host.Open(); h.L.LoseAt = loseAt; var report = h.W.Recovery; var generation = h.W.WorkingGeneration; var session = h.W.SessionId; int notifications = 0;
        h.W.WorkspaceChanged += () => notifications++;
        Assert.NotEqual(CommitStatus.Saved, (await h.W.RecoverToAsync(h.R.B.Id, default)).Status);
        Assert.Equal(0, h.R.Repairs); Assert.True(h.W.IsReadOnly); Assert.Null(h.W.LockId); Assert.Same(report, h.W.Recovery);
        Assert.Equal(generation, h.W.WorkingGeneration); Assert.Equal(session, h.W.SessionId); Assert.Equal(h.R.C.Id, h.W.Project!.HeadRevisionId);
        Assert.Equal(1, notifications); Assert.Empty(h.W.Files); Assert.Equal(0, h.L.Disposals);
    }

    [Theory, InlineData("target"), InlineData("repair"), InlineData("reload")]
    public async Task Held_old_result_cannot_change_error_readonly_files_or_reload_a_different_project(string stage)
    {
        await using var h = await Host.Open(); var gate = new Gate();
        if (stage == "target") h.R.TargetGate = gate;
        else if (stage == "repair") h.R.RepairGate = gate;
        else h.R.ReloadGate = gate;
        var pending = h.W.RecoverToAsync(h.R.B.Id, default); await gate.Reached.Task.WaitAsync(Bound);
        await h.W.OpenAsync(h.R.Other.Id, default); var state = h.State(); var error = h.W.LastError; var lockId = h.W.LockId;
        gate.Release.TrySetResult(); await pending.WaitAsync(Bound);
        Assert.Equal(state, h.State()); Assert.Equal(error, h.W.LastError); Assert.Equal(lockId, h.W.LockId); Assert.False(h.W.IsReadOnly);
        Assert.Equal(h.R.Other.Id, h.W.Project!.Id); Assert.Equal(Encoding.UTF8.GetBytes("unrelated🙂\r\n"), await h.W.GetFileBytesAsync(h.W.Files[0], default));
        Assert.Equal(stage == "target" ? 0 : 1, h.R.Repairs);
        Assert.Equal(1, h.R.Opens.Count(id => id == h.R.Other.Id)); // no late LoadSnapshot(Project.Id)
    }

    [Fact]
    public async Task Overlapping_recoveries_serialize_and_old_second_choice_cannot_repair_again()
    {
        await using var h = await Host.Open(); var gate = h.R.TargetGate = new();
        var first = h.W.RecoverToAsync(h.R.B.Id, default); await gate.Reached.Task.WaitAsync(Bound);
        var second = h.W.RecoverToAsync(h.R.A.Id, default); Assert.False(second.IsCompleted); Assert.True(h.W.RecoveryBusy); Assert.Equal(0, h.R.Repairs);
        gate.Release.TrySetResult(); Assert.Equal(CommitStatus.Saved, (await first.WaitAsync(Bound)).Status);
        Assert.NotEqual(CommitStatus.Saved, (await second.WaitAsync(Bound)).Status); Assert.Equal(1, h.R.Repairs); Assert.Equal(h.R.B.Id, h.R.Head); Assert.Null(h.W.Recovery);
        Assert.Null(h.W.LastError); Assert.False(h.W.RecoveryBusy);
    }

    [Fact]
    public async Task Newer_durable_head_wins_CAS_and_is_not_overwritten_by_stale_recovery_choice()
    {
        await using var h = await Host.Open(); var gate = h.R.TargetGate = new();
        var pending = h.W.RecoverToAsync(h.R.B.Id, default); await gate.Reached.Task.WaitAsync(Bound);
        h.R.Head = h.R.A.Id; gate.Release.TrySetResult(); var result = await pending.WaitAsync(Bound);
        Assert.Equal(CommitStatus.RevisionConflict, result.Status); Assert.Equal(h.R.A.Id, h.R.Head); Assert.NotNull(h.W.Recovery); Assert.Empty(h.W.Files);
    }

    [Fact]
    public async Task Explicit_clean_owner_recovers_existing_verified_checkpoint_without_new_child_and_keeps_binary_metadata()
    {
        await using var h = await Host.Open(); Assert.Equal(0, h.R.Reads); Assert.Equal(0, h.R.Repairs); Assert.Empty(h.W.Files);
        var project = h.W.Project!; var result = await h.W.RecoverToAsync(h.R.B.Id, default);
        Assert.Equal(CommitStatus.Saved, result.Status); Assert.Equal(h.R.C.Id, h.R.ExpectedRepairHead); Assert.Equal(h.R.B.Id, h.R.Head);
        Assert.Equal(h.R.B.Id, h.W.CurrentRevision!.Id); Assert.Equal(h.R.A.Id, h.W.CurrentRevision.ParentRevisionId); Assert.Null(h.W.Recovery);
        Assert.Equal(project.Id, h.W.Project!.Id); Assert.Equal(project.Name, h.W.Project.Name); Assert.Equal(SavePhase.Saved, h.W.SavePhase); Assert.False(h.W.IsDirty);
        Assert.Equal(h.R.BText, await h.W.GetFileBytesAsync(h.W.Files.Single(f => f.MediaKind == "text"), default));
        Assert.Equal(h.R.Binary, await h.W.GetFileBytesAsync(h.W.Files.Single(f => f.MediaKind == "binary"), default));
        Assert.True(h.R.Revisions.ContainsKey(h.R.C.Id)); Assert.True(h.R.Revisions.ContainsKey(h.R.A.Id)); Assert.Equal(1, h.R.Repairs);
    }

    private sealed class Gate
    {
        internal readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task Wait() { Reached.TrySetResult(); await Release.Task; } // actual noncooperative boundary
    }
    private sealed class Host : IAsyncDisposable
    {
        internal readonly Repo R = new(); internal readonly Locks L = new(); internal WorkspaceCoordinator W = null!;
        internal static async Task<Host> Open(bool readonlyMode = false) { var h = new Host(); h.L.Acquire = !readonlyMode; h.W = new(h.R, h.L, new HistoryService(h.R)); await h.W.OpenAsync(h.R.Project.Id, default); return h; }
        internal string State() => $"{W.Project?.Id}/{W.Project?.HeadRevisionId}/{W.SessionId}/{W.WorkingGeneration}/{W.IsReadOnly}/{W.LockId}/" + string.Join("|", W.Files.Select(f => $"{f.Id}:{f.Content}:{f.DirtyContent}:{f.HasBom}"));
        public async ValueTask DisposeAsync() { R.TargetGate?.Release.TrySetResult(); R.RepairGate?.Release.TrySetResult(); R.ReloadGate?.Release.TrySetResult(); await W.DisposeAsync(); }
    }
    private sealed class Locks : IWorkspaceLocks
    {
        internal bool Acquire = true, Local = true; internal int LoseAt, Calls, Disposals, Seq; private string? _id;
        public Task<WorkspaceCapabilities> GetCapabilitiesAsync(CancellationToken ct) => Task.FromResult(new WorkspaceCapabilities(true, true, true, true));
        public Task<string?> TryAcquireAsync(string name, CancellationToken ct) { Local = true; _id = Acquire ? "lock-" + ++Seq : null; return Task.FromResult(_id); }
        public Task<string> WaitForAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> ReleaseAsync(string id, CancellationToken ct) { if (_id == id) _id = null; return Task.FromResult(true); }
        public Task<bool> IsHeldAsync(string name, CancellationToken ct) => Task.FromResult(_id is not null);
        public bool IsOwnedLocally(string id, string name) => Local && id == _id;
        public Task<bool> OwnsAsync(string id, string name, CancellationToken ct) { Calls++; return Task.FromResult(Calls != LoseAt && IsOwnedLocally(id, name)); }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class Repo : IProjectRepository
    {
        internal readonly Project Project, Other; internal readonly ProjectRevision A, B, C, OtherRevision;
        internal RevisionId Head; internal readonly Dictionary<RevisionId, ProjectRevision> Revisions = new();
        internal readonly Dictionary<SHA256, byte[]> Content = new(); internal readonly List<HistoryCheckpoint> Choices = new();
        internal readonly RecoveryReport Report; internal readonly List<ProjectId> Opens = new();
        internal readonly byte[] AText = [239,187,191,..Encoding.UTF8.GetBytes("A Україна🙂\r\n")], BText = [239,187,191,..Encoding.UTF8.GetBytes("B Київ🙂\r\n")], Binary = [255,0,13,10];
        internal int Reads, Repairs; internal bool Verified = true, ForeignTarget; internal RevisionId? ExpectedRepairHead;
        internal Gate? TargetGate, RepairGate, ReloadGate;
        private static SHA256 Hash(byte[] b) => SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(b));
        internal Repo()
        {
            var pid = ProjectId.New(); var fid = FileId.New(); var bin = new ProjectFile(FileId.New(), "asset.bin", "binary", Hash(Binary), Binary.Length, false);
            ProjectRevision Rev(byte[] bytes, RevisionId? parent) => new(RevisionId.New(), pid, parent, ImmutableArray.Create(new ProjectFile(fid, "main.tex", "text", Hash(bytes), bytes.Length, true), bin), DateTimeOffset.UtcNow);
            A = Rev(AText, null); B = Rev(BText, A.Id); C = Rev(Encoding.UTF8.GetBytes("missing C"), B.Id); Head = C.Id; Project = new(pid, "damaged synthetic", C.Id);
            Other = new(ProjectId.New(), "unrelated", null); var otherBytes = Encoding.UTF8.GetBytes("unrelated🙂\r\n");
            OtherRevision = new(RevisionId.New(), Other.Id, null, ImmutableArray.Create(new ProjectFile(FileId.New(), "other.tex", "text", Hash(otherBytes), otherBytes.Length, false)), DateTimeOffset.UtcNow);
            Other = Other with { HeadRevisionId = OtherRevision.Id };
            foreach (var r in new[] { A, B, C, OtherRevision }) Revisions[r.Id] = r;
            Content[Hash(AText)] = AText; Content[Hash(BText)] = BText; Content[Hash(Binary)] = Binary; Content[Hash(otherBytes)] = otherBytes;
            Choices.Add(new(CheckpointId.New(), pid, B.Id, "save", DateTimeOffset.UtcNow)); Choices.Add(new(CheckpointId.New(), pid, A.Id, "import", DateTimeOffset.UtcNow));
            Report = new(C.Id, Choices);
        }
        public async Task<ProjectSnapshot> OpenAsync(ProjectId id, CancellationToken ct)
        {
            Opens.Add(id);
            if (id == Project.Id && Head == C.Id) return new(Project with { HeadRevisionId = Head }, null, new Dictionary<FileId,string>(), new Dictionary<FileId,byte[]>(), new Dictionary<FileId,bool>(), false, Report);
            var project = id == Other.Id ? Other : Project with { HeadRevisionId = Head }; var revision = Revisions[project.HeadRevisionId!.Value];
            var snapshot = new ProjectSnapshot(project, revision,
                revision.Files.Where(f => f.MediaKind == "text").ToDictionary(f => f.Id, f => TextContent.DecodeStrict(Content[f.ContentHash])),
                revision.Files.Where(f => f.MediaKind == "binary").ToDictionary(f => f.Id, f => Content[f.ContentHash]),
                revision.Files.Where(f => f.MediaKind == "text").ToDictionary(f => f.Id, f => f.HasBom), false);
            if (id == Project.Id && ReloadGate is { } g) await g.Wait(); return snapshot;
        }
        public async Task<RevisionManifest> ReadRevisionAsync(ProjectId id, RevisionId target, CancellationToken ct)
        { Reads++; if (TargetGate is { } g) await g.Wait(); var revision = Revisions[target]; return new(ForeignTarget ? revision with { ProjectId = Other.Id } : revision, Verified); }
        public async Task<CommitResult> RepairHeadAsync(ProjectId id, RevisionId expected, RevisionId target, CancellationToken ct)
        { Repairs++; ExpectedRepairHead = expected; if (RepairGate is { } g) await g.Wait(); if (Head != expected) return CommitResult.Conflict(Head); Head = target; return CommitResult.Saved(target); }
        public Task<byte[]> ReadContentAsync(SHA256 hash, CancellationToken ct) => Task.FromResult(Content[hash].ToArray());
        public Task<bool> VerifyHeadAsync(ProjectId id, RevisionId? head, CancellationToken ct) => Task.FromResult(id == Project.Id && Head == head);
        public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Project>>([Project with { HeadRevisionId = Head }, Other]);
        public Task<HistoryPage> ReadHistoryAsync(ProjectId id, HistoryCursor? cursor, CancellationToken ct) => Task.FromResult(new HistoryPage(Choices));
        public Task<CommitResult> CommitAsync(ProjectCommit b, RevisionId? head, CancellationToken ct) => throw new NotSupportedException();
        public Task<Project> CreateProjectAsync(string n, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteProjectAsync(ProjectId id, CancellationToken ct) => throw new NotSupportedException();
        public Task<ImportPublishResult> PublishImportAsync(StagedProject s, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommitResult> CreateRestoreRevisionAsync(ProjectId p, RevisionId h, ProjectRevision r, RevisionId s, string why, CancellationToken ct) => throw new NotSupportedException();
    }
}
