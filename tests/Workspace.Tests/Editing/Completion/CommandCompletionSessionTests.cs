using Client.Editing;
using Client.Editing.Completion;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.Editing.Completion;

// Real coordinator/source snapshots and authority; IO/storage fixtures only.
// These do not simulate or claim real textarea/renderer/browser acceptance.
public sealed class CommandCompletionSessionTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private const long Intent = 42;
    private static readonly string Text = "Україна🙂\r\n\\se{keep}\r\n";
    private static SourceRange Caret(string source)
    {
        int end = source.IndexOf("\\se", StringComparison.Ordinal) + 3;
        return SourceTools.ToTextarea(source, new(end, end))!.Value;
    }
    private static CommandCompletionSession.Proposal Propose(CommandCompletionSession session)
    {
        var request = Assert.IsType<CommandCompletionSession.Request>(session.Begin(Intent));
        var proposal = session.Propose(request, Caret(request.Snapshot.Text), Intent, out var state);
        Assert.Equal(CompletionMatchState.Ready, state); return Assert.IsType<CommandCompletionSession.Proposal>(proposal);
    }

    [Fact] public void Proposal_and_dismiss_are_nonmutating_and_explicit_consume_is_one_shot() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); Assert.True(f.Workspace.EditText(f.Main, Text));
        using var session = new CommandCompletionSession(f.Workspace, f.Lock);
        string before = f.State(); var proposal = Propose(session); Assert.Equal(before, f.State());
        Assert.True(session.Dismiss(proposal)); Assert.False(session.Dismiss(proposal)); Assert.Equal(before, f.State());
        Assert.Null(session.Consume(proposal, "section", proposal.DomCaret, Intent, 1000));
        proposal = Propose(session);
        var edit = Assert.IsType<SourceEdit>(session.Consume(proposal, "section", proposal.DomCaret, Intent, 1000));
        Assert.Equal(before, f.State()); // Proposal authority never edits the model itself.
        long generation = f.Workspace.WorkingGeneration; int commits = f.Repo.Commits;
        var others = f.Workspace.Files.Where(file => file.Id != f.Main).Select(file => (file.Id, file.Content, file.BinaryContent)).ToArray();
        Assert.True(f.Workspace.EditText(f.Main, edit.Text));
        Assert.Equal(generation + 1, f.Workspace.WorkingGeneration); Assert.Equal(commits, f.Repo.Commits);
        Assert.Equal("Україна🙂\r\n\\section{}{keep}\r\n", f.Workspace.ActiveFile!.Content);
        Assert.Equal(others, f.Workspace.Files.Where(file => file.Id != f.Main).Select(file => (file.Id, file.Content, file.BinaryContent)).ToArray());
        Assert.Null(session.Consume(proposal, "section", proposal.DomCaret, Intent, 1000));
        var after = Assert.IsType<SourceToolSnapshot>(SourceToolSnapshot.Capture(f.Workspace, Intent));
        Assert.True(session.IsPostEditCurrent(proposal, after, Intent));
        await f.Workspace.SaveAsync(default); Assert.True(session.IsPostEditCurrent(proposal, after, Intent));
        f.Lock.AllowAcquire = false; Assert.False(session.IsPostEditCurrent(proposal, after, Intent));
    });

    [Fact] public void Save_alone_preserves_exact_current_proposal() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, Text);
        using var session = new CommandCompletionSession(f.Workspace, f.Lock); var proposal = Propose(session);
        var oldHead = f.Workspace.CurrentRevision!.Id;
        Assert.Equal(CommitStatus.Saved, (await f.Workspace.SaveAsync(default)).Status);
        Assert.NotEqual(oldHead, f.Workspace.CurrentRevision!.Id);
        Assert.True(session.IsCurrent(proposal, Intent));
        Assert.NotNull(session.Consume(proposal, "section", proposal.DomCaret, Intent, 1000));
    });

    [Theory][InlineData("text-ABA")][InlineData("file-ABA")][InlineData("tree")][InlineData("session-ABA")][InlineData("project-ABA")]
    [InlineData("writer")][InlineData("intent")][InlineData("disposed")][InlineData("superseded")]
    public void Delayed_dom_read_cannot_adopt_later_source_or_session(string change) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, Text);
        using var session = new CommandCompletionSession(f.Workspace, f.Lock);
        var request = Assert.IsType<CommandCompletionSession.Request>(session.Begin(Intent));
        var dom = new TaskCompletionSource<SourceRange>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<CommandCompletionSession.Proposal?> AfterRead()
        {
            var range = await dom.Task;
            return session.Propose(request, range, change == "intent" ? Intent + 1 : Intent, out _);
        }
        var pending = AfterRead();
        try
        {
            switch (change)
            {
                case "text-ABA": f.Workspace.EditText(f.Main, "different"); f.Workspace.EditText(f.Main, Text); break;
                case "file-ABA": f.Workspace.SelectFile(f.Workspace.Files.Single(file => file.RelativePath == "chapter.tex").Id); f.Workspace.SelectFile(f.Main); session.Clear(); break;
                case "tree": f.Workspace.CreateFile("later.tex", "unrelated"); f.Workspace.SelectFile(f.Main); break;
                case "session-ABA":
                    var project = f.Workspace.Project!.Id; await f.Workspace.CloseAsync(default); await f.Workspace.OpenAsync(project, default); break;
                case "project-ABA":
                    var a = f.Workspace.Project!.Id; await f.Workspace.CloseAsync(default);
                    var b = new Project(ProjectId.New(), "B", null); f.Repo.Projects[b.Id] = (b, null);
                    await f.Workspace.OpenAsync(b.Id, default); await f.Workspace.CloseAsync(default); await f.Workspace.OpenAsync(a, default); break;
                case "writer": f.Lock.AllowAcquire = false; break;
                case "disposed": session.Dispose(); break;
                case "superseded": session.Begin(Intent); break;
            }
            string before = f.State(); dom.TrySetResult(Caret(Text)); Assert.Null(await pending.WaitAsync(Bound)); Assert.Equal(before, f.State());
        }
        finally { dom.TrySetResult(Caret(Text)); await pending.WaitAsync(Bound); }
    });

    [Theory][InlineData("caret")][InlineData("text")][InlineData("writer")][InlineData("intent")][InlineData("dismiss")][InlineData("new-proposal")]
    public void Delayed_insert_revalidates_actual_caret_and_consumed_button_authority(string change) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, Text);
        using var session = new CommandCompletionSession(f.Workspace, f.Lock); var proposal = Propose(session);
        var dom = new TaskCompletionSource<SourceRange>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<SourceEdit?> AfterRead()
        {
            var range = await dom.Task;
            return session.Consume(proposal, "section", range, change == "intent" ? Intent + 1 : Intent, 1000);
        }
        var pending = AfterRead();
        try
        {
            if (change == "text") f.Workspace.EditText(f.Main, "different source");
            if (change == "writer") f.Lock.AllowAcquire = false;
            if (change == "dismiss") Assert.True(session.Dismiss(proposal));
            CommandCompletionSession.Proposal? newer = change == "new-proposal" ? Propose(session) : null;
            string before = f.State(); var range = change == "caret" ? new SourceRange(proposal.DomCaret.Start - 1, proposal.DomCaret.End - 1) : proposal.DomCaret;
            dom.TrySetResult(range); Assert.Null(await pending.WaitAsync(Bound)); Assert.Equal(before, f.State());
            if (newer is not null)
            {
                Assert.False(session.Dismiss(proposal)); Assert.True(session.IsCurrent(newer, Intent));
                Assert.NotNull(session.Consume(newer, "section", newer.DomCaret, Intent, 1000));
            }
        }
        finally { dom.TrySetResult(proposal.DomCaret); await pending.WaitAsync(Bound); }
    });

    [Fact] public void Unknown_ID_or_growth_refusal_never_mutates_or_consumes_valid_proposal() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, Text);
        using var session = new CommandCompletionSession(f.Workspace, f.Lock); var proposal = Propose(session); string before = f.State();
        Assert.Null(session.Consume(proposal, "input", proposal.DomCaret, Intent, 1000));
        Assert.Null(session.Consume(proposal, "section", proposal.DomCaret, Intent, 1));
        Assert.Equal(before, f.State()); Assert.True(session.IsCurrent(proposal, Intent));
    });
    [Fact] public void Remount_cannot_use_an_old_component_proposal() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, Text);
        var old = new CommandCompletionSession(f.Workspace, f.Lock); var proposal = Propose(old); old.Dispose();
        using var next = new CommandCompletionSession(f.Workspace, f.Lock); Propose(next);
        Assert.Null(next.Consume(proposal, "section", proposal.DomCaret, Intent, 1000)); Assert.False(next.Dismiss(proposal));
    });
    [Fact] public void Readonly_or_binary_target_cannot_begin_a_request() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(readOnly: true);
        using var session = new CommandCompletionSession(f.Workspace, f.Lock); string before = f.State();
        Assert.Null(session.Begin(Intent)); Assert.Equal(before, f.State());
        f.Workspace.SelectFile(f.Workspace.Files.Single(file => file.MediaKind == "binary").Id);
        Assert.Null(session.Begin(Intent));
    });
    [Fact] public void Recovery_text_is_not_completion_edit_authority() => Run(async () =>
    {
        var id = ProjectId.New(); var damaged = RevisionId.New(); var fileId = FileId.New(); byte[] bytes = System.Text.Encoding.UTF8.GetBytes(Text);
        var file = new ProjectFile(fileId, "main.tex", "text", CompilationTestWorkspace.Hash(bytes), bytes.Length, false);
        var revision = new ProjectRevision(RevisionId.New(), id, null, [file], DateTimeOffset.UtcNow);
        var recovery = new RecoveryReport(damaged, []);
        var snapshot = new ProjectSnapshot(new Project(id, "recovery", damaged), revision,
            new Dictionary<FileId, string> { [fileId] = Text }, new Dictionary<FileId, byte[]>(), new Dictionary<FileId, bool> { [fileId] = false }, false, recovery);
        var repo = new RecoveryRepository(snapshot); var locks = new CompilationTestWorkspace.Locks();
        await using var workspace = new WorkspaceCoordinator(repo, locks, new HistoryService(repo)); await workspace.OpenAsync(id, default);
        using var session = new CommandCompletionSession(workspace, locks);
        Assert.Null(session.Begin(Intent)); Assert.Same(recovery, workspace.Recovery); Assert.Equal(Text, workspace.ActiveFile!.Content);
    });
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
