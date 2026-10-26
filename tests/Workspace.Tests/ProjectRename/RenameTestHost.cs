using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Client.Workspace.Renaming;

namespace Workspace.Tests.ProjectRename;

// Fake browser IO only; real coordinator/session/name policy and immutable source
// records. Fake transaction models a boundary, NOT actual IndexedDB proof.
internal sealed class RenameTestHost : IAsyncDisposable
{
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    internal readonly CompilationTestWorkspace.Repository Data = new();
    internal readonly LockNetwork Network;
    internal readonly Locks Lock;
    internal readonly Repository Repo;
    internal readonly WorkspaceCoordinator Workspace;
    internal readonly ProjectRenameSession Session;
    private readonly SynchronizationContext _ui = SynchronizationContext.Current!;
    private readonly List<Task> _tasks = [];
    internal RenameTestHost(LockNetwork? network = null)
    {
        Network = network ?? new(); Lock = new(Network); Repo = new(Data, Lock);
        Workspace = new(Repo, Lock, new HistoryService(Repo));
        Session = new(Workspace, Dispatch);
    }
    internal Project Project => Data.Project;
    internal Task Dispatch(Action action)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(_ => { try { action(); signal.TrySetResult(); } catch (Exception e) { signal.TrySetException(e); } }, null);
        var task = signal.Task; _tasks.Add(task); return task;
    }
    internal Task<ProjectRenameResult> Confirm(string name)
    {
        var task = Session.ConfirmAsync(Session.CurrentProposal!, name); _tasks.Add(task); return task;
    }
    public async ValueTask DisposeAsync()
    {
        Session.Dispose(); Lock.AcquireHold?.TrySetResult(); Repo.BeforeWrite?.TrySetResult(); Repo.AfterWrite?.TrySetResult(); Repo.OpenHold?.TrySetResult(); Lock.ReleaseHold?.TrySetResult();
        await Task.WhenAll(_tasks.ToArray()).WaitAsync(Bound);
        await Workspace.DisposeAsync(); await Lock.DisposeAsync();
    }
    internal sealed class LockNetwork
    {
        internal readonly Dictionary<string, (Locks Owner, string Id)> Held = new();
        internal int Sequence;
    }
    internal sealed class Locks(LockNetwork network) : IWorkspaceLocks
    {
        private readonly Dictionary<string, string> _owned = new();
        internal TaskCompletionSource? AcquireHold, ReleaseHold;
        internal readonly TaskCompletionSource AcquireReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Supported = true, ForeignId, LoseOwnership, FailRelease;
        internal int Acquires, Releases, Waits;
        public Task<WorkspaceCapabilities> GetCapabilitiesAsync(CancellationToken ct) => Task.FromResult(new WorkspaceCapabilities(true, true, Supported, true));
        public async Task<string?> TryAcquireAsync(string name, CancellationToken ct)
        {
            _ = ct; Acquires++; AcquireReached.TrySetResult(); if (AcquireHold is { } held) await held.Task;
            if (ForeignId) return "foreign-id";
            if (network.Held.ContainsKey(name)) return null;
            var id = "own-" + ++network.Sequence; network.Held[name] = (this, id); _owned[id] = name; return id;
        }
        public Task<string> WaitForAsync(string name, CancellationToken ct) { Waits++; throw new NotSupportedException("Never take over for rename"); }
        public async Task<bool> ReleaseAsync(string id, CancellationToken ct)
        {
            _ = ct; Releases++; ReleaseReached.TrySetResult(); if (ReleaseHold is { } held) await held.Task;
            if (FailRelease) throw new IOException("private release detail");
            if (!_owned.Remove(id, out string? name)) return false;
            if (network.Held.TryGetValue(name, out var entry) && ReferenceEquals(entry.Owner, this) && entry.Id == id)
                return network.Held.Remove(name);
            return false;
        }
        public bool IsOwnedLocally(string id, string name) => _owned.TryGetValue(id, out var held) && held == name;
        public Task<bool> OwnsAsync(string id, string name, CancellationToken ct) => Task.FromResult(!LoseOwnership && Owns(id, name));
        internal bool Owns(string id, string name) => IsOwnedLocally(id, name)
            && network.Held.TryGetValue(name, out var entry) && ReferenceEquals(entry.Owner, this) && entry.Id == id;
        public Task<bool> IsHeldAsync(string name, CancellationToken ct) => Task.FromResult(network.Held.ContainsKey(name));
        public ValueTask DisposeAsync()
        {
            foreach (var pair in _owned)
                if (network.Held.TryGetValue(pair.Value, out var entry) && ReferenceEquals(entry.Owner, this)) network.Held.Remove(pair.Value);
            _owned.Clear(); return ValueTask.CompletedTask;
        }
    }
    internal sealed class Repository(CompilationTestWorkspace.Repository data, Locks locks) : IProjectRepository
    {
        internal TaskCompletionSource? BeforeWrite, AfterWrite, OpenHold;
        internal readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Wrote = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource OpenReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool FailWrite, Missing, Unsupported;
        internal int Renames, NameWrites, OpenCalls, Creates, Deletes;
        public async Task<ProjectRenameResult> RenameProjectAsync(ProjectRenameSnapshot expected, string normalizedName, string writerLockId, CancellationToken ct)
        {
            Renames++; Reached.TrySetResult(); if (BeforeWrite is { } hold) await hold.Task;
            if (Unsupported) return new(ProjectRenameCode.Unsupported);
            if (ct.IsCancellationRequested) return new(ProjectRenameCode.Cancelled);
            if (FailWrite) throw new IOException("private simulated transaction error");
            if (!locks.Owns(writerLockId, "oop-project:" + expected.Id)) return new(ProjectRenameCode.WriterUnavailable);
            if (Missing || !data.Projects.TryGetValue(expected.Id, out var stored)) return new(ProjectRenameCode.Missing);
            var project = stored.Item1;
            if (project.Name != expected.Name || project.HeadRevisionId != expected.Head || project.SchemaVersion != expected.SchemaVersion || project.Id != expected.Id)
                return new(ProjectRenameCode.Stale);
            var code = project.Name == normalizedName ? ProjectRenameCode.Unchanged : ProjectRenameCode.Applied;
            if (code == ProjectRenameCode.Applied)
            {
                NameWrites++; var changed = project with { Name = normalizedName };
                data.Projects[expected.Id] = (changed, stored.Item2); if (data.Project.Id == expected.Id) data.Project = changed;
            }
            Wrote.TrySetResult(); if (AfterWrite is { } done) await done.Task;
            return new(code); // Models actual commit reply, not cancellation proxy.
        }
        public async Task<ProjectSnapshot> OpenAsync(ProjectId id, CancellationToken ct)
        { OpenCalls++; OpenReached.TrySetResult(); if (OpenHold is { } h) await h.Task; return await data.OpenAsync(id, ct); }
        public Task<Project> CreateProjectAsync(string name, CancellationToken ct)
        { Creates++; var p = new Project(ProjectId.New(), name, null); data.Projects[p.Id] = (p, null); return Task.FromResult(p); }
        public Task DeleteProjectAsync(ProjectId id, CancellationToken ct) { Deletes++; data.Projects.Remove(id); return Task.CompletedTask; }
        public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct) => data.ListProjectsAsync(ct);
        public Task<CommitResult> CommitAsync(ProjectCommit batch, RevisionId? head, CancellationToken ct) => data.CommitAsync(batch, head, ct);
        public Task<HistoryPage> ReadHistoryAsync(ProjectId id, HistoryCursor? cursor, CancellationToken ct) => data.ReadHistoryAsync(id, cursor, ct);
        public Task<CommitResult> RepairHeadAsync(ProjectId id, RevisionId head, RevisionId next, CancellationToken ct) => data.RepairHeadAsync(id, head, next, ct);
        public Task<RevisionManifest> ReadRevisionAsync(ProjectId id, RevisionId revision, CancellationToken ct) => data.ReadRevisionAsync(id, revision, ct);
        public Task<byte[]> ReadContentAsync(SHA256 hash, CancellationToken ct) => data.ReadContentAsync(hash, ct);
        public Task<ImportPublishResult> PublishImportAsync(StagedProject stage, CancellationToken ct) => data.PublishImportAsync(stage, ct);
        public Task<CommitResult> CreateRestoreRevisionAsync(ProjectId id, RevisionId expected, ProjectRevision revision, RevisionId source, string reason, CancellationToken ct)
            => data.CreateRestoreRevisionAsync(id, expected, revision, source, reason, ct);
    }
}
