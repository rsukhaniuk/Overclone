using System.Collections.Immutable;
using System.Text;
using Client.Compilation;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Client.Workspace.Transfer;
using Xunit;

namespace Workspace.Tests;

// Synthetic storage/engine descriptors only. REAL coordinator and public capture
// APIs; these tests are not compiler, capability, WASM or browser evidence.
internal sealed class CompilationTestWorkspace : IAsyncDisposable
{
    internal static readonly CancellationToken None = CancellationToken.None;
    internal static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    internal static SHA256 Hash(byte[] value) => SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(value));
    internal static readonly CompilationCaptureBudget Budget = new(32, 1024 * 1024, 8 * 1024 * 1024, 65536);
    internal readonly Repository Repo = new();
    internal readonly Locks Lock = new();
    internal WorkspaceCoordinator Workspace = null!;
    internal readonly CompilationSelectionState Selection = new();
    internal CompilationSnapshotFactory Capture = null!;
    internal FileId Main => Workspace.Files.First(f => !f.Deleted && f.MediaKind == "text").Id;
    internal static CompilationDescriptor Descriptor(string id = "synthetic-engine", string version = "test-v1", string? hash = null)
        => new(id, version, new SHA256(hash ?? new string('a', 64)));
    internal static CompilationSelection Select(FileId main, string engine = "synthetic-engine",
        IReadOnlyList<CompilationDescriptor>? bundles = null, IReadOnlyList<CompilationDescriptor>? tools = null,
        IReadOnlyList<CompilationOption>? options = null)
        => Assert.IsType<CompilationSelection>(CompilationSelection.Create(new(main, Descriptor(engine),
            bundles ?? [], tools ?? [], options ?? []), Budget.MaxMetadataBytes).Selection);

    internal static async Task<CompilationTestWorkspace> Open(bool readOnly = false, bool headNull = false)
    {
        var fixture = new CompilationTestWorkspace();
        fixture.Lock.AllowAcquire = !readOnly;
        if (headNull) fixture.Repo.HeadNull();
        fixture.Workspace = new(fixture.Repo, fixture.Lock, new HistoryService(fixture.Repo));
        await fixture.Workspace.OpenAsync(fixture.Repo.Project.Id, None);
        fixture.Capture = new(fixture.Workspace, fixture.Selection);
        if (!headNull) fixture.Selection.SetSelection(Select(fixture.Main));
        return fixture;
    }
    internal async Task<CompilationInputSnapshot> Snapshot(CompilationCaptureBudget? budget = null)
        => Assert.IsType<CompilationCaptureResult.Captured>(await Capture.CaptureAsync(budget ?? Budget, None)).Snapshot;
    internal ReadBarrier Hold(FileId? id = null)
    {
        var file = Workspace.Files.Single(f => f.Id == (id ?? Main));
        var gate = new ReadBarrier(file.DurableHash!.Value);
        Repo.Gates.Add(gate); return gate;
    }
    internal string State() => $"{Workspace.Project}/{Workspace.CurrentRevision?.Id}/{Workspace.SessionId}/{Workspace.WorkingGeneration}/{Workspace.SavePhase}/{Workspace.LastError}/{Workspace.ActiveFileId}/{Workspace.IsReadOnly}/{Workspace.LockId}/{Workspace.IsDirty}/"
        + string.Join("|", Workspace.Files.Select(f => $"{f.Id}:{f.RelativePath}:{f.MediaKind}:{f.Content}:{f.HasBom}:{f.DirtyContent}:{f.DirtyBinary}:{f.TreeChanged}:{f.IsNew}:{f.Deleted}:{f.DurableHash}:{f.DurableLength}:{f.PendingTextBytes?.Hash}"));
    public async ValueTask DisposeAsync()
    {
        foreach (var gate in Repo.Gates) gate.Release.TrySetResult(Repo.Content[gate.Hash]);
        Repo.CommitGate?.Release.TrySetResult();
        await Capture.DisposeAsync();
        // Only fixture-issued finite reads; their gates were released above.
        // Settlement notifications preserve the original read result/exception
        // for its consumer, without rethrowing an already handled port failure.
        await Task.WhenAll(Repo.PendingReadSettlements);
        await Workspace.DisposeAsync();
    }

    internal sealed class ReadBarrier(SHA256 hash)
    {
        internal SHA256 Hash = hash;
        internal readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<byte[]> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Used;
        internal Task WaitReached() => Reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); // failure bound, not a timing guess
    }
    internal sealed class CommitBarrier
    {
        internal readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task WaitReached() => Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    internal sealed class Locks : IWorkspaceLocks
    {
        internal bool AllowAcquire = true;
        internal int Acquires, Releases, Queries;
        public Task<WorkspaceCapabilities> GetCapabilitiesAsync(CancellationToken ct) => Task.FromResult(new WorkspaceCapabilities(true, true, true, true));
        public Task<string?> TryAcquireAsync(string name, CancellationToken ct) { Acquires++; return Task.FromResult<string?>(AllowAcquire ? "test-lock" : null); }
        public Task<string> WaitForAsync(string name, CancellationToken ct) { Acquires++; return Task.FromResult("test-lock"); }
        public Task<bool> ReleaseAsync(string id, CancellationToken ct) { Releases++; return Task.FromResult(true); }
        public Task<bool> IsHeldAsync(string name, CancellationToken ct) { Queries++; return Task.FromResult(true); }
        public bool IsOwnedLocally(string id, string name) => AllowAcquire && id == "test-lock";
        public Task<bool> OwnsAsync(string id, string name, CancellationToken ct) { Queries++; return Task.FromResult(IsOwnedLocally(id, name)); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    internal sealed class Repository : IProjectRepository
    {
        internal Project Project = new(ProjectId.New(), "compile-test", null);
        internal ProjectRevision? Revision;
        internal readonly Dictionary<SHA256, byte[]> Content = new();
        internal readonly Dictionary<RevisionId, ProjectRevision> Revisions = new();
        internal readonly Dictionary<ProjectId, (Project, ProjectRevision?)> Projects = new();
        internal readonly List<ReadBarrier> Gates = [];
        internal readonly List<SHA256> ReadHashes = [];
        internal readonly List<Task> PendingReadSettlements = [];
        internal Action<Task>? ReadOperationObserved;
        internal int Reads, Commits, HeadChecks, Publishes, Opens, Restores;
        internal bool FailSave;
        internal CommitBarrier? CommitGate;
        internal Func<SHA256, byte[]>? ReadOverride;
        internal Repository()
        {
            byte[] main = [239, 187, 191, .. Bytes("Україна🙂\r\nmain\rlast\n")];
            var files = new[] { ("entry", main, "text"), ("chapter.tex", Bytes("chapter\r\n"), "text"), ("asset.bin", new byte[] { 255, 254, 0, 128 }, "binary"), ("empty.txt", Array.Empty<byte>(), "text") };
            Revision = new(RevisionId.New(), Project.Id, null,
                files.Select(item => new ProjectFile(FileId.New(), item.Item1, item.Item3, Hash(item.Item2), item.Item2.Length,
                    item.Item3 == "text" && TextContent.HasBom(item.Item2))).ToImmutableArray(), DateTimeOffset.UtcNow);
            foreach (var file in files) Content[Hash(file.Item2)] = file.Item2;
            Project = Project with { HeadRevisionId = Revision.Id }; Remember();
        }
        internal void Remember()
        { Projects[Project.Id] = (Project, Revision); if (Revision is not null) Revisions[Revision.Id] = Revision; }
        internal void HeadNull() { Revision = null; Project = Project with { HeadRevisionId = null }; Remember(); }
        public Task<ProjectSnapshot> OpenAsync(ProjectId id, CancellationToken ct)
        {
            Opens++;
            if (id != Project.Id) { var stored = Projects[id]; Project = stored.Item1; Revision = stored.Item2; }
            var files = Revision?.Files ?? ImmutableArray<ProjectFile>.Empty;
            return Task.FromResult(new ProjectSnapshot(Project, Revision,
                files.Where(f => f.MediaKind == "text").ToDictionary(f => f.Id, f => TextContent.DecodeStrict(Content[f.ContentHash])),
                files.Where(f => f.MediaKind == "binary").ToDictionary(f => f.Id, f => Content[f.ContentHash]),
                files.Where(f => f.MediaKind == "text").ToDictionary(f => f.Id, f => f.HasBom), false));
        }
        public Task<byte[]> ReadContentAsync(SHA256 hash, CancellationToken ct)
        {
            var operation = ReadCoreAsync(hash, ct);
            ReadOperationObserved?.Invoke(operation);
            if (!operation.IsCompleted)
            {
                var settled = operation.ContinueWith(static completed => { _ = completed.Exception; },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                PendingReadSettlements.Add(settled);
                (SynchronizationContext.Current as CompilationLifecycle.LifecycleTestHost.OwnerContext)?.ObserveFixtureOperation(settled);
            }
            return operation;
        }
        private async Task<byte[]> ReadCoreAsync(SHA256 hash, CancellationToken ct)
        {
            Reads++; ReadHashes.Add(hash);
            var gate = Gates.FirstOrDefault(g => !g.Used && g.Hash == hash);
            if (gate is not null)
            {
                gate.Used = true; gate.Reached.TrySetResult();
                return await gate.Release.Task; // deliberately noncooperative token behavior
            }
            return ReadOverride is { } handler ? handler(hash) : Content[hash]; // deliberate shared mutable array
        }
        public Task<bool> VerifyHeadAsync(ProjectId id, RevisionId? head, CancellationToken ct)
        { HeadChecks++; return Task.FromResult(Project.Id == id && Project.HeadRevisionId == head); }
        public async Task<CommitResult> CommitAsync(ProjectCommit batch, RevisionId? expected, CancellationToken ct)
        {
            Commits++;
            if (CommitGate is { } gate) { gate.Reached.TrySetResult(); await gate.Release.Task.WaitAsync(ct); }
            if (FailSave) return CommitResult.Failure("SyntheticStorageFailure", "Synthetic local save failed.");
            if (expected != Project.HeadRevisionId) return CommitResult.Conflict(Project.HeadRevisionId);
            foreach (var item in batch.Content) Content[item.Hash] = item.Bytes.ToArray();
            Revision = batch.SelectedHead; Project = Project with { HeadRevisionId = Revision.Id }; Remember();
            return CommitResult.Saved(Revision.Id);
        }
        public Task<RevisionManifest> ReadRevisionAsync(ProjectId id, RevisionId revision, CancellationToken ct)
            => Task.FromResult(RevisionManifest.Ok(Revisions[revision]));
        public Task<CommitResult> CreateRestoreRevisionAsync(ProjectId id, RevisionId expected, ProjectRevision restored,
            RevisionId source, string reason, CancellationToken ct)
        { Restores++; return CommitAsync(ProjectCommit.Of(restored), expected, ct); }
        public Task<ImportPublishResult> PublishImportAsync(StagedProject staged, CancellationToken ct)
        {
            Publishes++; var project = new Project(ProjectId.New(), staged.Name, null);
            var revision = new ProjectRevision(RevisionId.New(), project.Id, null,
                staged.Files.Select(f => new ProjectFile(FileId.New(), f.RelativePath, f.MediaKind, f.Hash, f.ByteLength, f.HasBom)).ToImmutableArray(), DateTimeOffset.UtcNow);
            foreach (var file in staged.Files) Content[file.Hash] = file.Bytes.ToArray();
            project = project with { HeadRevisionId = revision.Id }; Projects[project.Id] = (project, revision); Revisions[revision.Id] = revision;
            return Task.FromResult(new ImportPublishResult(true, project, revision.Id));
        }
        public Task<HistoryPage> ReadHistoryAsync(ProjectId id, HistoryCursor? cursor, CancellationToken ct) => Task.FromResult(new HistoryPage([]));
        public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Project>>(Projects.Values.Select(p => p.Item1).ToArray());
        public Task<Project> CreateProjectAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteProjectAsync(ProjectId id, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommitResult> RepairHeadAsync(ProjectId id, RevisionId expected, RevisionId replacement, CancellationToken ct) => throw new NotSupportedException();
    }
}
