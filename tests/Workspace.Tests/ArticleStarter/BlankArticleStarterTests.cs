using System.Text;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Client.Workspace.Starters;
using Client.Workspace.Transfer;
using Xunit;

namespace Workspace.Tests.ArticleStarter;

// Real WorkspaceCoordinator + existing synthetic repository/locks. No browser,
// native/compiler execution or actual DOM acceptance is claimed by these tests.
public sealed class BlankArticleStarterTests
{
    private const string ExpectedHash = "300b5d5eb317e27407e40414d9f7289855c866aa27e892b16a9e2865b617bddc";

    [Fact] public async Task Untouched_empty_project_creates_one_selected_exact_article_without_saving()
    {
        await using var f = await CompilationTestWorkspace.Open(headNull: true);
        using var starter = new BlankArticleStarter(f.Workspace, f.Lock);
        var project = f.Workspace.Project!; var session = f.Workspace.SessionId;
        var selection = f.Selection.Current;
        int contentObjects = f.Repo.Content.Count, reads = f.Repo.Reads;
        var request = Assert.IsType<BlankArticleStarter.Request>(starter.Capture());
        Assert.Equal(project.Id, request.ProjectId); Assert.Equal(session, request.SessionId);
        Assert.Equal(0, request.Generation); Assert.Null(request.Head);
        int changes = 0;
        f.Workspace.WorkspaceChanged += () =>
        {
            changes++;
            Assert.Equal(1, f.Workspace.WorkingGeneration);
            Assert.Equal(Assert.Single(f.Workspace.Files).Id, f.Workspace.ActiveFileId);
        };
        Assert.True(starter.TryStart(request));
        Assert.Equal(1, changes); // Ordinary CreateFile selects once before its one change event.
        var file = Assert.Single(f.Workspace.Files);
        Assert.Equal("main.tex", file.RelativePath); Assert.Equal("text", file.MediaKind);
        Assert.Same(file, f.Workspace.ActiveFile); Assert.False(file.HasBom); Assert.True(file.IsNew);
        Assert.True(file.TreeChanged); Assert.Null(file.ImportedBytes);
        Assert.Equal(SavePhase.Dirty, f.Workspace.SavePhase); Assert.True(f.Workspace.IsDirty);
        Assert.Same(project, f.Workspace.Project); Assert.Null(f.Workspace.CurrentRevision);
        Assert.Null(f.Workspace.Project!.HeadRevisionId); Assert.Equal(session, f.Workspace.SessionId);
        Assert.Equal(selection, f.Selection.Current); Assert.Equal(0, f.Repo.Commits);
        Assert.Equal(contentObjects, f.Repo.Content.Count); Assert.Equal(reads, f.Repo.Reads);
        Assert.Equal(0, f.Repo.HeadChecks); Assert.Equal(0, f.Repo.Publishes); Assert.Equal(0, f.Repo.Restores);
        var bytes = await f.Workspace.GetFileBytesAsync(file, CancellationToken.None);
        Assert.Equal(98, bytes.Length); Assert.Equal(ExpectedHash, CompilationTestWorkspace.Hash(bytes).Hex);
        Assert.False(TextContent.HasBom(bytes)); Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[^1]); Assert.Equal(4, bytes.Count(b => b == (byte)'\n'));
        Assert.Equal(file.Content, new UTF8Encoding(false, true).GetString(bytes));
        var after = f.State();
        Assert.False(starter.TryStart(request)); Assert.False(starter.TryStart(starter.Capture()));
        Assert.Equal(after, f.State()); Assert.Equal(1, changes); Assert.Equal(0, f.Repo.Commits);
    }

    [Fact] public async Task Ordinary_save_and_reopen_persist_exact_bytes_and_retire_starter()
    {
        await using var f = await CompilationTestWorkspace.Open(headNull: true);
        using var starter = new BlankArticleStarter(f.Workspace, f.Lock);
        Assert.True(starter.TryStart(starter.Capture()));
        Assert.Null(f.Repo.Project.HeadRevisionId); Assert.Equal(0, f.Repo.Commits);
        var projectId = f.Workspace.Project!.Id;
        var saved = await f.Workspace.SaveAsync(CancellationToken.None);
        Assert.Equal(CommitStatus.Saved, saved.Status); Assert.Equal(1, f.Repo.Commits);
        var revision = Assert.IsType<ProjectRevision>(f.Workspace.CurrentRevision);
        Assert.Null(revision.ParentRevisionId); Assert.Equal(projectId, revision.ProjectId);
        var manifestFile = Assert.Single(revision.Files);
        Assert.Equal("main.tex", manifestFile.RelativePath); Assert.Equal(98, manifestFile.ByteLength);
        Assert.Equal(ExpectedHash, manifestFile.ContentHash.Hex); Assert.False(manifestFile.HasBom);
        Assert.Equal(ExpectedHash, CompilationTestWorkspace.Hash(f.Repo.Content[manifestFile.ContentHash]).Hex);
        Assert.Null(starter.Capture());
        await f.Workspace.CloseAsync(CancellationToken.None);
        await f.Workspace.OpenAsync(projectId, CancellationToken.None);
        var file = Assert.Single(f.Workspace.Files);
        Assert.Equal(manifestFile.Id, file.Id); Assert.Equal(file.Id, f.Workspace.ActiveFileId);
        Assert.Equal(ExpectedHash, CompilationTestWorkspace.Hash(await f.Workspace.GetFileBytesAsync(file, CancellationToken.None)).Hex);
        Assert.False(f.Workspace.IsDirty); Assert.Null(starter.Capture()); Assert.Equal(1, f.Repo.Commits);
    }

    [Theory][InlineData("empty")][InlineData("text")][InlineData("binary")][InlineData("deleted")]
    public async Task Existing_or_deleted_but_retained_file_rejects_without_touching_unrelated_data(string kind)
    {
        await using var f = await CompilationTestWorkspace.Open(headNull: true);
        using var starter = new BlankArticleStarter(f.Workspace, f.Lock);
        var old = starter.Capture();
        if (kind == "binary") Assert.True(f.Workspace.CreateFileFromBytes("asset.bin", [0xff, 0xfe, 0]));
        else Assert.True(f.Workspace.CreateFile("notes.tex", kind == "text" ? "Україна 😀\r\n" : ""));
        if (kind == "deleted") Assert.True(f.Workspace.DeleteFile(Assert.Single(f.Workspace.Files).Id));
        Assert.Single(f.Workspace.Files); // Includes the retained tombstone.
        var before = f.State(); var selection = f.Selection.Current;
        Assert.Null(starter.Capture()); Assert.False(starter.TryStart(old));
        Assert.Equal(before, f.State()); Assert.Equal(selection, f.Selection.Current); Assert.Equal(0, f.Repo.Commits);
    }

    [Theory][InlineData("close")][InlineData("reopen")][InlineData("takeover")][InlineData("switch")]
    public async Task Rendered_action_cannot_target_another_project_or_session(string change)
    {
        await using var f = await CompilationTestWorkspace.Open(headNull: true);
        using var starter = new BlankArticleStarter(f.Workspace, f.Lock);
        var old = Assert.IsType<BlankArticleStarter.Request>(starter.Capture());
        var firstProject = f.Workspace.Project!;
        if (change == "takeover") Assert.True(await f.Workspace.TakeOverAsync(CancellationToken.None));
        else
        {
            await f.Workspace.CloseAsync(CancellationToken.None);
            if (change == "reopen") await f.Workspace.OpenAsync(firstProject.Id, CancellationToken.None);
            if (change == "switch")
            {
                var other = new Project(ProjectId.New(), firstProject.Name, null); // Same name is not authority.
                f.Repo.Projects[other.Id] = (other, null);
                await f.Workspace.OpenAsync(other.Id, CancellationToken.None);
            }
        }
        Assert.NotEqual(old.SessionId, f.Workspace.SessionId);
        var before = f.State();
        Assert.False(starter.TryStart(old)); Assert.Equal(before, f.State()); Assert.Equal(0, f.Repo.Commits);
        if (change != "close")
        {
            var current = Assert.IsType<BlankArticleStarter.Request>(starter.Capture());
            Assert.True(starter.TryStart(current)); // A fresh explicit action in the new empty session is allowed.
        }
    }

    [Fact] public async Task Generation_alone_prevents_reuse_even_if_retained_list_is_now_empty()
    {
        await using var f = await CompilationTestWorkspace.Open(headNull: true);
        using var starter = new BlankArticleStarter(f.Workspace, f.Lock);
        var old = starter.Capture(); Assert.True(f.Workspace.CreateFile("notes.tex"));
        Assert.True(f.Workspace.DeleteFile(Assert.Single(f.Workspace.Files).Id));
        // Adversarial model mutation through its public list, not the UI. Even
        // erasing the tombstone cannot make a nonzero-generation session virgin.
        f.Workspace.Files.Clear(); Assert.NotEqual(0, f.Workspace.WorkingGeneration);
        var before = f.State();
        Assert.Null(starter.Capture()); Assert.False(starter.TryStart(old)); Assert.Equal(before, f.State());
    }

    [Fact] public async Task Durable_empty_revision_is_not_an_untouched_project()
    {
        await using var f = await CompilationTestWorkspace.Open(headNull: true);
        using var starter = new BlankArticleStarter(f.Workspace, f.Lock);
        var old = starter.Capture();
        var revision = ProjectRevision.Root(f.Repo.Project.Id);
        f.Repo.Revision = revision; f.Repo.Project = f.Repo.Project with { HeadRevisionId = revision.Id }; f.Repo.Remember();
        await f.Workspace.CloseAsync(CancellationToken.None);
        await f.Workspace.OpenAsync(f.Repo.Project.Id, CancellationToken.None);
        Assert.Empty(f.Workspace.Files); Assert.Equal(0, f.Workspace.WorkingGeneration);
        var before = f.State();
        Assert.Null(starter.Capture()); Assert.False(starter.TryStart(old)); Assert.Equal(before, f.State());
    }

    [Fact] public async Task Read_only_and_lost_local_writer_lock_refuse_without_mutations()
    {
        await using var readOnly = await CompilationTestWorkspace.Open(readOnly: true, headNull: true);
        using var blocked = new BlankArticleStarter(readOnly.Workspace, readOnly.Lock);
        var before = readOnly.State(); Assert.True(readOnly.Workspace.IsReadOnly); Assert.Null(readOnly.Workspace.LockId);
        Assert.Null(blocked.Capture()); Assert.False(blocked.TryStart(null)); Assert.Equal(before, readOnly.State());
        await using var f = await CompilationTestWorkspace.Open(headNull: true);
        using var starter = new BlankArticleStarter(f.Workspace, f.Lock);
        var old = starter.Capture(); f.Lock.AllowAcquire = false;
        Assert.False(f.Workspace.IsReadOnly); Assert.NotNull(f.Workspace.LockId); // Stale local writer allegation.
        before = f.State();
        Assert.Null(starter.Capture()); Assert.False(starter.TryStart(old)); Assert.Equal(before, f.State());
        Assert.Equal(0, f.Repo.Commits);
    }

    [Fact] public async Task Helper_disposal_and_foreign_helper_request_are_not_authority()
    {
        await using var f = await CompilationTestWorkspace.Open(headNull: true);
        using var starter = new BlankArticleStarter(f.Workspace, f.Lock);
        using var another = new BlankArticleStarter(f.Workspace, f.Lock);
        var old = starter.Capture(); var before = f.State();
        Assert.False(another.TryStart(old)); Assert.Equal(before, f.State());
        starter.Dispose(); Assert.Null(starter.Capture()); Assert.False(starter.TryStart(old)); Assert.Equal(before, f.State());
    }

    [Fact] public async Task Disposed_coordinator_loses_writer_lock_and_cannot_be_started()
    {
        await using var f = await CompilationTestWorkspace.Open(headNull: true);
        using var starter = new BlankArticleStarter(f.Workspace, f.Lock);
        var old = starter.Capture(); await f.Workspace.DisposeAsync();
        var before = f.State(); Assert.Null(f.Workspace.LockId);
        Assert.Null(starter.Capture()); Assert.False(starter.TryStart(old)); Assert.Equal(before, f.State());
    }

    [Fact] public async Task Recovery_is_never_cleared_repaired_or_overwritten()
    {
        var id = ProjectId.New(); var damaged = RevisionId.New();
        var snapshot = new ProjectSnapshot(new Project(id, "recovery-synthetic", damaged), null,
            new Dictionary<FileId, string>(), new Dictionary<FileId, byte[]>(), new Dictionary<FileId, bool>(), false,
            new RecoveryReport(damaged, []));
        var repo = new RecoveryRepository(snapshot); var locks = new CompilationTestWorkspace.Locks();
        await using var workspace = new WorkspaceCoordinator(repo, locks, new HistoryService(repo));
        await workspace.OpenAsync(id, CancellationToken.None);
        using var starter = new BlankArticleStarter(workspace, locks);
        var recovery = workspace.Recovery; Assert.NotNull(recovery); Assert.Empty(workspace.Files);
        Assert.Null(starter.Capture()); Assert.False(starter.TryStart(null));
        Assert.Same(recovery, workspace.Recovery); Assert.Equal(damaged, workspace.Project!.HeadRevisionId);
        Assert.Empty(workspace.Files); Assert.Null(workspace.ActiveFileId); Assert.Equal(0, workspace.WorkingGeneration);
    }

    // Only Open is permitted in this recovery test; any hidden save/read/repair
    // is an immediate failure, not a mock-only eligibility predicate.
    private sealed class RecoveryRepository(ProjectSnapshot snapshot) : IProjectRepository
    {
        public Task<ProjectSnapshot> OpenAsync(ProjectId id, CancellationToken ct) => Task.FromResult(snapshot);
        public Task<CommitResult> CommitAsync(ProjectCommit batch, RevisionId? expectedHead, CancellationToken ct) => throw new NotSupportedException();
        public Task<HistoryPage> ReadHistoryAsync(ProjectId id, HistoryCursor? cursor, CancellationToken ct) => throw new NotSupportedException();
        public Task<Project> CreateProjectAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteProjectAsync(ProjectId id, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommitResult> RepairHeadAsync(ProjectId id, RevisionId oldHead, RevisionId newHead, CancellationToken ct) => throw new NotSupportedException();
        public Task<RevisionManifest> ReadRevisionAsync(ProjectId id, RevisionId revision, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> ReadContentAsync(SHA256 hash, CancellationToken ct) => throw new NotSupportedException();
        public Task<ImportPublishResult> PublishImportAsync(StagedProject staged, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommitResult> CreateRestoreRevisionAsync(ProjectId id, RevisionId expected, ProjectRevision revision,
            RevisionId source, string reason, CancellationToken ct) => throw new NotSupportedException();
    }
}
