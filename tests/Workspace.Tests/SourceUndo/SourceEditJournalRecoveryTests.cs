using System.Text;
using Client.Editing.Undo;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.SourceUndo;

public sealed partial class SourceEditJournalTests
{
    [Fact] public void Recovery_or_readonly_source_never_receives_editor_history_mutations() => Run(async () =>
    {
        await using (var readOnly = await CompilationTestWorkspace.Open(readOnly: true))
        {
            var editor = new Editor(readOnly.Workspace); using var journal = new SourceEditJournal(readOnly.Workspace, readOnly.Lock, editor);
            string before = readOnly.State(); Assert.Null(journal.BeginEdit("typing", new(0, 0))); Assert.False(journal.CanUndo);
            Assert.False((await journal.UndoAsync()).Applied); Assert.False((await journal.RedoAsync()).Applied); Assert.Equal(before, readOnly.State()); Assert.Equal(0, editor.Applies);
        }
        var id = ProjectId.New(); var fileId = FileId.New(); byte[] bytes = Encoding.UTF8.GetBytes("Recovery text");
        var file = new ProjectFile(fileId, "main.tex", "text", CompilationTestWorkspace.Hash(bytes), bytes.Length, false);
        var revision = new ProjectRevision(RevisionId.New(), id, null, [file], DateTimeOffset.UtcNow);
        var recovery = new RecoveryReport(RevisionId.New(), []);
        var snapshot = new ProjectSnapshot(new Project(id, "recovery", revision.Id), revision,
            new Dictionary<FileId, string> { [fileId] = "Recovery text" }, new Dictionary<FileId, byte[]>(), new Dictionary<FileId, bool> { [fileId] = false }, false, recovery);
        var repository = new RecoveryRepository(snapshot, bytes); var locks = new CompilationTestWorkspace.Locks();
        await using var workspace = new WorkspaceCoordinator(repository, locks, new HistoryService(repository)); await workspace.OpenAsync(id, default);
        var recoveryEditor = new Editor(workspace); using var recoveryJournal = new SourceEditJournal(workspace, locks, recoveryEditor);
        Assert.Null(recoveryJournal.BeginEdit("typing", new(0, 0))); Assert.False((await recoveryJournal.UndoAsync()).Applied);
        Assert.Equal("Recovery text", workspace.ActiveFile!.Content); Assert.Same(recovery, workspace.Recovery); Assert.Equal(0, recoveryEditor.Applies);
    });
    private sealed class RecoveryRepository(ProjectSnapshot snapshot, byte[] bytes) : IProjectRepository
    {
        public Task<ProjectSnapshot> OpenAsync(ProjectId id, CancellationToken ct) => Task.FromResult(snapshot);
        public Task<byte[]> ReadContentAsync(SHA256 hash, CancellationToken ct) => Task.FromResult(bytes);
        public Task<CommitResult> CommitAsync(ProjectCommit batch, RevisionId? expectedHead, CancellationToken ct) => throw new NotSupportedException();
        public Task<HistoryPage> ReadHistoryAsync(ProjectId id, HistoryCursor? cursor, CancellationToken ct) => throw new NotSupportedException();
        public Task<Project> CreateProjectAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteProjectAsync(ProjectId id, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommitResult> RepairHeadAsync(ProjectId id, RevisionId expected, RevisionId newHead, CancellationToken ct) => throw new NotSupportedException();
        public Task<RevisionManifest> ReadRevisionAsync(ProjectId id, RevisionId revision, CancellationToken ct) => throw new NotSupportedException();
        public Task<ImportPublishResult> PublishImportAsync(StagedProject staged, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommitResult> CreateRestoreRevisionAsync(ProjectId id, RevisionId expected, ProjectRevision revision, RevisionId source, string reason, CancellationToken ct) => throw new NotSupportedException();
    }
}
