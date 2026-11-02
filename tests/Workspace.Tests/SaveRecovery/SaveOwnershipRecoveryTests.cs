using Client.Analysis;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Client.Workspace.Patching;
using Xunit;

namespace Workspace.Tests.SaveRecovery;

// Root-owned PROPOSAL ONLY. Real coordinator/repository ports; controlled lock
// boundaries are managed contract evidence, not native-browser revocation proof.
public class SaveOwnershipRecoveryTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private const string Dirty = "retained Україна🙂 $1 \\literal\nsecond line\n";

    [Theory, InlineData(1), InlineData(2)]
    public async Task Ownership_loss_before_first_write_or_before_commit_retains_bytes_and_notifies_without_mutation(int loseAt)
    {
        var repo = new WorkspaceTextPatchTests.Repo(); var locks = new ControlledLocks { LoseAt = loseAt };
        await using var ws = new WorkspaceCoordinator(repo, locks, new HistoryService(repo)); await ws.OpenAsync(repo.Project.Id, default);
        var file = ws.Files.First(f => f.MediaKind == "text"); Assert.True(ws.EditText(file.Id, Dirty));
        var bytes = await ws.GetFileBytesAsync(file, default); var content = file.Content;
        var generation = ws.WorkingGeneration; var session = ws.SessionId; var head = ws.CurrentRevision!.Id;
        var count = repo.Content.Count; int changes = 0;
        await using var capture = new AnalysisSnapshotFactory(ws); var epoch = capture.CurrentWorkspaceEpoch;
        ws.WorkspaceChanged += () => { changes++; if (changes != 1) return; Assert.True(ws.IsReadOnly); Assert.Null(ws.LockId); Assert.True(ws.IsDirty); Assert.Equal(content, file.Content); };
        var result = await ws.SaveAsync(default);
        Assert.NotEqual(CommitStatus.Saved, result.Status); Assert.Equal(loseAt, locks.OwnsCalls);
        Assert.Equal(0, repo.Commits); Assert.Equal(count, repo.Content.Count); Assert.Equal(head, repo.Project.HeadRevisionId); Assert.Equal(head, ws.CurrentRevision.Id);
        Assert.Equal(generation, ws.WorkingGeneration); Assert.Equal(session, ws.SessionId); Assert.Same(file, ws.Files.Single(f => f.Id == file.Id));
        Assert.Equal(bytes, await ws.GetFileBytesAsync(file, default)); Assert.True(ws.IsDirty); Assert.True(ws.IsReadOnly); Assert.Null(ws.LockId);
        Assert.Equal(SavePhase.Failed, ws.SavePhase); Assert.Contains("Writer lock lost", ws.LastError!);
        Assert.Equal(1, changes); Assert.True(capture.CurrentWorkspaceEpoch > epoch); Assert.Equal(0, locks.DisposeCalls);
    }

    [Fact]
    public async Task An_old_save_ownership_reply_cannot_mark_a_new_session_readonly_or_notify_it()
    {
        var repo = new WorkspaceTextPatchTests.Repo(); var locks = new ControlledLocks { HoldAt = 1 };
        await using var ws = new WorkspaceCoordinator(repo, locks, new HistoryService(repo)); await ws.OpenAsync(repo.Project.Id, default);
        Assert.True(ws.EditText(ws.Files[0].Id, Dirty)); var oldSession = ws.SessionId;
        var pending = ws.SaveAsync(default); await locks.Reached.Task.WaitAsync(Bound);
        // Open a DIFFERENT project while the old real lock query is held.
        var newProjectId = ProjectId.New();
        repo.Revision = repo.Revision with { Id = RevisionId.New(), ProjectId = newProjectId, ParentRevisionId = null };
        repo.Project = new(newProjectId, "different project", repo.Revision.Id);
        await ws.OpenAsync(newProjectId, default); Assert.NotEqual(oldSession, ws.SessionId);
        var session = ws.SessionId; var generation = ws.WorkingGeneration; var lockId = ws.LockId; var head = ws.CurrentRevision!.Id;
        var values = ws.Files.Select(f => (f.Id, f.Content, f.DirtyContent)).ToArray(); int changes = 0; ws.WorkspaceChanged += () => changes++;
        locks.Release.TrySetResult(); Assert.NotEqual(CommitStatus.Saved, (await pending.WaitAsync(Bound)).Status);
        Assert.Equal(session, ws.SessionId); Assert.Equal(generation, ws.WorkingGeneration); Assert.Equal(lockId, ws.LockId); Assert.False(ws.IsReadOnly); Assert.Equal(newProjectId, ws.Project!.Id);
        Assert.Equal(head, ws.CurrentRevision.Id); Assert.Equal(values, ws.Files.Select(f => (f.Id, f.Content, f.DirtyContent)).ToArray()); Assert.Equal(0, changes); Assert.Equal(0, repo.Commits);
    }

    [Fact]
    public async Task Dirty_readonly_takeover_refuses_before_lock_wait_reload_or_RAM_replacement()
    {
        var repo = new WorkspaceTextPatchTests.Repo(); var locks = new ControlledLocks { LoseAt = 1 };
        await using var ws = new WorkspaceCoordinator(repo, locks, new HistoryService(repo)); await ws.OpenAsync(repo.Project.Id, default);
        var file = ws.Files[0]; ws.EditText(file.Id, Dirty); var bytes = await ws.GetFileBytesAsync(file, default); await ws.SaveAsync(default);
        Assert.True(ws.IsReadOnly); Assert.True(ws.IsDirty); var session = ws.SessionId; var generation = ws.WorkingGeneration; var head = ws.CurrentRevision!.Id;
        Assert.False(await ws.TakeOverAsync(default)); Assert.Equal(0, locks.WaitCalls); Assert.Equal(session, ws.SessionId); Assert.Equal(generation, ws.WorkingGeneration);
        Assert.Equal(head, ws.CurrentRevision.Id); Assert.Same(file, ws.Files.Single(f => f.Id == file.Id)); Assert.Equal(bytes, await ws.GetFileBytesAsync(file, default));
        Assert.Contains("retained unsaved", ws.LastError!); Assert.Null(ws.LockId); Assert.True(ws.IsReadOnly); Assert.True(ws.IsDirty); Assert.Equal(0, repo.Commits);
    }

    [Fact]
    public async Task Clean_visible_takeover_still_waits_cooperatively_then_reloads_latest_saved_head()
    {
        var repo = new WorkspaceTextPatchTests.Repo(); var broker = new Broker();
        await using var first = new WorkspaceCoordinator(repo, new CooperativeLocks(broker), new HistoryService(repo));
        await using var second = new WorkspaceCoordinator(repo, new CooperativeLocks(broker), new HistoryService(repo));
        await first.OpenAsync(repo.Project.Id, default); await second.OpenAsync(repo.Project.Id, default); Assert.True(second.IsReadOnly); Assert.False(first.IsReadOnly);
        var oldSession = second.SessionId; var taking = second.TakeOverAsync(default); await broker.WaitReached.Task.WaitAsync(Bound);
        Assert.False(taking.IsCompleted); Assert.False(first.IsReadOnly); Assert.True(second.IsReadOnly);
        // Ordinary edit/save by current owner is allowed WHILE takeover waits.
        first.EditText(first.Files[0].Id, "saved by original before cooperative release\n"); Assert.Equal(CommitStatus.Saved, (await first.SaveAsync(default)).Status);
        var saved = first.CurrentRevision!.Id; var bytes = await first.GetFileBytesAsync(first.Files[0], default);
        await first.CloseAsync(default); Assert.True(await taking.WaitAsync(Bound));
        Assert.False(second.IsReadOnly); Assert.False(second.IsDirty); Assert.NotNull(second.LockId); Assert.NotEqual(oldSession, second.SessionId);
        Assert.Equal(saved, second.CurrentRevision!.Id); Assert.Equal(bytes, await second.GetFileBytesAsync(second.Files[0], default)); Assert.Equal(1, repo.Commits);
    }

    private sealed class ControlledLocks : IWorkspaceLocks
    {
        internal int LoseAt, HoldAt, OwnsCalls, WaitCalls, DisposeCalls, Sequence; private string? _held;
        internal readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<WorkspaceCapabilities> GetCapabilitiesAsync(CancellationToken ct) => Task.FromResult(new WorkspaceCapabilities(true, true, true, true));
        public Task<string?> TryAcquireAsync(string name, CancellationToken ct) { _held = "test-lease-" + ++Sequence; return Task.FromResult<string?>(_held); }
        public Task<string> WaitForAsync(string name, CancellationToken ct) { WaitCalls++; throw new InvalidOperationException("Dirty takeover must not wait/acquire"); }
        public Task<bool> ReleaseAsync(string id, CancellationToken ct) { if (_held == id) _held = null; return Task.FromResult(true); }
        public Task<bool> IsHeldAsync(string name, CancellationToken ct) => Task.FromResult(_held is not null);
        public bool IsOwnedLocally(string id, string name) => _held == id;
        public async Task<bool> OwnsAsync(string id, string name, CancellationToken ct) { var call = ++OwnsCalls; if (call == HoldAt) { Reached.TrySetResult(); await Release.Task; } if (call == LoseAt) _held = null; return _held == id; }
        public ValueTask DisposeAsync() { DisposeCalls++; Release.TrySetResult(); return ValueTask.CompletedTask; }
    }
    private sealed class Broker
    {
        internal string? Held; internal int Sequence;
        internal readonly TaskCompletionSource WaitReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<string>? Waiting;
        internal string Acquire() => Held = "cooperative-" + ++Sequence;
    }
    private sealed class CooperativeLocks(Broker broker) : IWorkspaceLocks
    {
        private string? _mine;
        public Task<WorkspaceCapabilities> GetCapabilitiesAsync(CancellationToken ct) => Task.FromResult(new WorkspaceCapabilities(true, true, true, true));
        public Task<string?> TryAcquireAsync(string name, CancellationToken ct) { if (broker.Held is not null) return Task.FromResult<string?>(null); _mine = broker.Acquire(); return Task.FromResult<string?>(_mine); }
        public async Task<string> WaitForAsync(string name, CancellationToken ct) { if (broker.Held is null) return _mine = broker.Acquire(); var waiting = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); broker.Waiting = waiting; broker.WaitReached.TrySetResult(); return _mine = await waiting.Task.WaitAsync(ct); }
        public Task<bool> ReleaseAsync(string id, CancellationToken ct) { if (broker.Held == id) { broker.Held = null; _mine = null; if (broker.Waiting is { } w) { broker.Waiting = null; w.TrySetResult(broker.Acquire()); } } return Task.FromResult(true); }
        public Task<bool> IsHeldAsync(string name, CancellationToken ct) => Task.FromResult(broker.Held is not null);
        public bool IsOwnedLocally(string id, string name) => _mine == id && broker.Held == id;
        public Task<bool> OwnsAsync(string id, string name, CancellationToken ct) => Task.FromResult(IsOwnedLocally(id, name));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
