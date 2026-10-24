using System.Collections.Immutable;
using Client.Analysis;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Client.Workspace.Transfer;
using Xunit;

namespace Workspace.Tests.SourceReferences;

public sealed class ReferenceLifecycleTests
{
    private static TaskCompletionSource<bool> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static async Task<AnalysisReport> Fresh(AnalysisCoordinator coordinator, Action change)
    {
        var ready = new TaskCompletionSource<AnalysisReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = (coordinator.State as AnalysisState.Completed)?.Report.Snapshot.Id;
        void Changed(AnalysisState state) { if (state is AnalysisState.Completed c && c.Report.Snapshot.Id != previous) ready.TrySetResult(c.Report); }
        coordinator.Changed += Changed;
        try { change(); return await ready.Task.WaitAsync(Guard); }
        finally { coordinator.Changed -= Changed; }
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Captured_selection_and_readonly_exact_key_navigation_never_save(bool readOnly)
    {
        await using var h = await Harness.Create(readOnly);
        var report = await Fresh(h.Analysis, () => h.Analysis.SetSelection(AnalysisSelection.SourceReferences(h.A.ToString())));
        Assert.Equal(h.A.ToString(), report.Snapshot.ReferenceConfiguration!.RootFileId);
        Assert.Equal(report.Snapshot.ReferenceConfiguration, report.Result.ReferenceConfiguration);
        var phase = h.Workspace.SavePhase; var head = h.Workspace.CurrentRevision!.Id; var gen = h.Workspace.WorkingGeneration;
        h.Workspace.SelectFile(h.B);
        var navigator = new SourceNavigator(h.Analysis, h.Workspace, h.Analyzer);
        var diagnostic = Assert.Single(report.Result.Diagnostics);
        var selected = Assert.IsType<NavigationResult.Selected>(await navigator.NavigateAsync(report, diagnostic, CancellationToken.None));
        Assert.Equal(h.A, selected.FileId); Assert.Equal(SyntheticReferenceFixture.Key, selected.ExpectedNormalizedSubstring);
        Assert.Equal(SyntheticReferenceFixture.Key.Length, selected.TextareaEnd - selected.TextareaStart);
        Assert.Equal(gen, h.Workspace.WorkingGeneration); Assert.Equal(phase, h.Workspace.SavePhase);
        Assert.Equal(head, h.Workspace.CurrentRevision!.Id); Assert.Equal(0, h.Repo.Commits); Assert.Equal(readOnly, h.Workspace.IsReadOnly);
        Assert.All(report.Snapshot.Files, f => Assert.Equal(SyntheticReferenceFixture.Bytes, f.Bytes.ToArray()));
    }

    [Theory]
    [InlineData("capture")][InlineData("import")][InlineData("response")][InlineData("epoch")]
    public async Task Automatic_root_A_B_A_reanalysis_does_not_revive_held_work(string stage)
    {
        await using var h = await Harness.Create();
        if (stage == "capture") { h.Repo.ReadGate = Gate(); h.Js.ImportGate = Gate(); }
        if (stage == "import") h.Js.ImportGate = Gate();
        if (stage == "response") h.Js.ResponseGate = Gate();
        if (stage == "epoch") h.Js.EpochGate = Gate();
        var published = new List<AnalysisReport>(); h.Analysis.Changed += s => { if (s is AnalysisState.Completed c) published.Add(c.Report); };
        var fresh = Fresh(h.Analysis, () => h.Analysis.SetSelection(AnalysisSelection.SourceReferences(h.A.ToString())));
        await (stage switch { "capture" => h.Repo.ReadReached.Task, "import" => h.Js.ImportReached.Task,
            "response" => h.Js.ResponseReached.Task, _ => h.Js.EpochReached.Task }).WaitAsync(Guard);
        var old = h.Analyzer.Captured.FirstOrDefault(); var generation = h.Workspace.WorkingGeneration;
        h.Analysis.SetSelection(AnalysisSelection.SourceReferences(h.B.ToString()));
        h.Analysis.SetSelection(AnalysisSelection.SourceReferences(h.A.ToString()));
        Assert.False(h.Analysis.State is AnalysisState.Completed);
        h.Release(); var report = await fresh.WaitAsync(Guard);
        Assert.Equal(h.A.ToString(), report.Snapshot.ReferenceConfiguration!.RootFileId);
        Assert.Equal(h.Analysis.CurrentSelectionVersion, report.Snapshot.SelectionVersion);
        Assert.Equal(3, report.Snapshot.SelectionVersion);
        if (old is not null) Assert.NotEqual(old.Id, report.Snapshot.Id);
        Assert.Single(published); Assert.Equal(generation, h.Workspace.WorkingGeneration); Assert.Equal(0, h.Repo.Commits);
    }

    [Fact]
    public async Task Held_navigation_and_identical_bytes_cannot_override_root_selection_version()
    {
        await using var h = await Harness.Create();
        var old = await Fresh(h.Analysis, () => h.Analysis.SetSelection(AnalysisSelection.SourceReferences(h.A.ToString())));
        var navigator = new SourceNavigator(h.Analysis, h.Workspace, h.Analyzer);
        h.Js.EpochGate = Gate(); h.Js.EpochReached = Gate();
        var navigation = navigator.NavigateAsync(old, Assert.Single(old.Result.Diagnostics), CancellationToken.None);
        await h.Js.EpochReached.Task.WaitAsync(Guard);
        var fresh = Fresh(h.Analysis, () => {
            h.Analysis.SetSelection(AnalysisSelection.SourceReferences(h.B.ToString()));
            h.Analysis.SetSelection(AnalysisSelection.SourceReferences(h.A.ToString()));
        });
        Assert.False(navigator.IsStillCurrent(old)); h.Release();
        Assert.IsType<NavigationResult.Rejected>(await navigation.WaitAsync(Guard));
        var current = await fresh.WaitAsync(Guard); Assert.NotEqual(old.Snapshot.Id, current.Snapshot.Id);
        Assert.Equal(old.Snapshot.Files[0].Bytes.ToArray(), current.Snapshot.Files[0].Bytes.ToArray());
        Assert.Equal(Assert.Single(old.Result.Diagnostics).Id, Assert.Single(current.Result.Diagnostics).Id);
        Assert.IsType<NavigationResult.Rejected>(await navigator.NavigateAsync(old, Assert.Single(old.Result.Diagnostics), CancellationToken.None));
        Assert.Equal(0, h.Repo.Commits);
    }

    [Theory]
    [InlineData("rename")][InlineData("delete")][InlineData("source")][InlineData("session")][InlineData("project")]
    public async Task Root_source_or_workspace_authority_loss_refuses_old_navigation(string change)
    {
        await using var h = await Harness.Create();
        var old = await Fresh(h.Analysis, () => h.Analysis.SetSelection(AnalysisSelection.SourceReferences(h.A.ToString())));
        var navigator = new SourceNavigator(h.Analysis, h.Workspace, h.Analyzer);
        switch (change)
        {
            case "rename": Assert.True(h.Workspace.RenameFile(h.A, "renamed.tex")); break;
            case "delete": Assert.True(h.Workspace.DeleteFile(h.A)); break;
            case "source": Assert.True(h.Workspace.EditText(h.A, SyntheticReferenceFixture.Text + "% changed")); break;
            case "session": await h.Workspace.CloseAsync(CancellationToken.None); await h.Workspace.OpenAsync(h.Repo.Current.Project.Id, CancellationToken.None); break;
            case "project": await h.Workspace.CloseAsync(CancellationToken.None); break;
        }
        Assert.False(navigator.IsStillCurrent(old));
        Assert.IsType<NavigationResult.Rejected>(await navigator.NavigateAsync(old, Assert.Single(old.Result.Diagnostics), CancellationToken.None));
        Assert.Equal(0, h.Repo.Commits);
    }

    [Theory]
    [InlineData("unknown")][InlineData("binary")][InlineData("nontex")][InlineData("deleted")]
    public async Task Invalid_root_capture_is_not_dispatched_or_retargeted(string kind)
    {
        await using var h = await Harness.Create();
        var id = kind switch { "binary" => h.Binary.ToString(), "nontex" => h.Notes.ToString(), "unknown" => "missing", _ => h.A.ToString() };
        if (kind == "deleted") Assert.True(h.Workspace.DeleteFile(h.A));
        await using var factory = new AnalysisSnapshotFactory(h.Workspace);
        Assert.IsType<CaptureResult.InvalidInput>(await factory.CaptureSelectionAsync(AnalysisSelection.SourceReferences(id), CancellationToken.None));
        Assert.Empty(h.Js.Calls); Assert.Equal(0, h.Repo.Commits);
    }

    [Fact]
    public async Task Missing_root_keeps_explicit_profile_and_a_held_capture_rechecks_version()
    {
        await using var h = await Harness.Create();
        h.Analysis.SetSelection(AnalysisSelection.SourceReferences(null));
        Assert.Equal(AnalysisCatalog.ReferenceId, h.Analysis.CurrentProfile.Id);
        Assert.IsType<AnalysisState.InvalidInput>(h.Analysis.State); Assert.Empty(h.Js.Calls);
        var selection = AnalysisSelection.SourceReferences(h.A.ToString()); long version = 1;
        await using var factory = new AnalysisSnapshotFactory(h.Workspace, () => selection, () => version);
        h.Repo.ReadGate = Gate(); var task = factory.CaptureSelectionAsync(selection, CancellationToken.None);
        await h.Repo.ReadReached.Task.WaitAsync(Guard);
        selection = AnalysisSelection.SourceReferences(h.B.ToString()); version++;
        selection = AnalysisSelection.SourceReferences(h.A.ToString()); version++;
        h.Release(); Assert.IsType<CaptureResult.Superseded>(await task.WaitAsync(Guard));
    }

    [Theory]
    [InlineData("rename")][InlineData("delete")][InlineData("source")]
    public async Task Held_capture_rechecks_actual_root_after_storage_await(string change)
    {
        await using var h = await Harness.Create();
        await using var factory = new AnalysisSnapshotFactory(h.Workspace);
        h.Repo.ReadGate = Gate();
        var pending = factory.CaptureSelectionAsync(AnalysisSelection.SourceReferences(h.A.ToString()), CancellationToken.None);
        await h.Repo.ReadReached.Task.WaitAsync(Guard);
        if (change == "rename") Assert.True(h.Workspace.RenameFile(h.A, "renamed.tex"));
        else if (change == "delete") Assert.True(h.Workspace.DeleteFile(h.A));
        else Assert.True(h.Workspace.EditText(h.A, "new source"));
        h.Release(); Assert.IsType<CaptureResult.Superseded>(await pending.WaitAsync(Guard));
        Assert.Empty(h.Js.Calls); Assert.Equal(0, h.Repo.Commits);
    }

    private sealed class RecordingAnalyzer(ISourceAnalyzer inner) : ISourceAnalyzer
    {
        public List<AnalysisSnapshot> Captured { get; } = new();
        public Task<AnalysisJobResult> AnalyzeAsync(AnalysisSnapshot snapshot, CancellationToken ct)
        { Captured.Add(snapshot); return inner.AnalyzeAsync(snapshot, ct); }
        public Task<bool> IsCurrentWorkerEpochAsync(long epoch, CancellationToken ct) => inner.IsCurrentWorkerEpochAsync(epoch, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private sealed class Harness : IAsyncDisposable
    {
        public Repo Repo = null!; public WorkspaceCoordinator Workspace = null!; public AnalysisCoordinator Analysis = null!;
        public RecordingAnalyzer Analyzer = null!; public KnuProtocolTests.FixtureJs Js = null!;
        public FileId A, B, Binary, Notes;
        public static async Task<Harness> Create(bool readOnly = false)
        {
            var h = new Harness { Repo = new() }; var snapshot = SyntheticReferenceFixture.Snapshot();
            h.A = snapshot.Files[0].FileId; h.B = snapshot.Files[1].FileId; h.Binary = FileId.New(); h.Notes = FileId.New();
            var files = snapshot.Files.Select(f => new ProjectFile(f.FileId, f.RelativePath, "text", f.Sha256, f.ByteLength, true)).ToList();
            foreach (var f in snapshot.Files) h.Repo.Content[f.Sha256] = f.Bytes.ToArray();
            var hash = snapshot.Files[0].Sha256;
            files.Add(new(h.Notes, "notes.txt", "text", hash, SyntheticReferenceFixture.Bytes.Length, true));
            files.Add(new(h.Binary, "asset.bin", "binary", new SHA256(new string('b', 64)), 100000));
            var project = ProjectId.New(); var revision = new ProjectRevision(RevisionId.New(), project, null, files.ToImmutableArray(), DateTimeOffset.UtcNow);
            h.Repo.Current = new(new(project, "synthetic", revision.Id), revision,
                files.Where(f => f.MediaKind == "text").ToDictionary(f => f.Id, _ => SyntheticReferenceFixture.Text),
                new Dictionary<FileId, byte[]>(), files.ToDictionary(f => f.Id, f => f.HasBom), readOnly);
            h.Workspace = new(h.Repo, new Locks(readOnly), new HistoryService(h.Repo)); await h.Workspace.OpenAsync(project, CancellationToken.None);
            h.Js = new KnuProtocolTests.FixtureJs((_, rid) => SyntheticReferenceFixture.Reply(h.Analyzer.Captured[^1], rid));
            h.Analyzer = new(new RustSourceAnalyzer(h.Js));
            h.Analysis = new(h.Workspace, h.Analyzer, delayAsync: (_, ct) => Task.Delay(Timeout.Infinite, ct)); return h;
        }
        public void Release()
        { Repo.ReadGate?.TrySetResult(true); Js.ImportGate?.TrySetResult(true); Js.ResponseGate?.TrySetResult(true); Js.EpochGate?.TrySetResult(true); }
        public async ValueTask DisposeAsync()
        { Release(); await Analysis.DisposeAsync(); await Analyzer.DisposeAsync(); await Workspace.DisposeAsync(); }
    }
    private sealed class Repo : IProjectRepository
    {
        public ProjectSnapshot Current = null!; public Dictionary<SHA256, byte[]> Content { get; } = new(); public int Commits;
        public TaskCompletionSource<bool>? ReadGate; public TaskCompletionSource<bool> ReadReached { get; } = Gate(); private bool _held;
        public Task<ProjectSnapshot> OpenAsync(ProjectId id, CancellationToken ct) => Task.FromResult(Current);
        public async Task<byte[]> ReadContentAsync(SHA256 hash, CancellationToken ct)
        { if (!_held && ReadGate is { } gate) { _held = true; ReadReached.TrySetResult(true); await gate.Task; } return Content[hash]; }
        public Task<CommitResult> CommitAsync(ProjectCommit batch, RevisionId? head, CancellationToken ct)
        { Commits++; foreach (var c in batch.Content) Content[c.Hash] = c.Bytes; return Task.FromResult(CommitResult.Saved(batch.SelectedHead.Id)); }
        public Task<HistoryPage> ReadHistoryAsync(ProjectId p, HistoryCursor? cursor, CancellationToken ct) => Task.FromResult(new HistoryPage(Array.Empty<HistoryCheckpoint>()));
        public Task<RevisionManifest> ReadRevisionAsync(ProjectId p, RevisionId r, CancellationToken ct) => Task.FromResult(RevisionManifest.Ok(Current.Revision!));
        public Task<Project> CreateProjectAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteProjectAsync(ProjectId id, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommitResult> RepairHeadAsync(ProjectId p, RevisionId a, RevisionId b, CancellationToken ct) => throw new NotSupportedException();
        public Task<ImportPublishResult> PublishImportAsync(StagedProject p, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommitResult> CreateRestoreRevisionAsync(ProjectId p, RevisionId h, ProjectRevision r, RevisionId s, string reason, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Locks(bool readOnly) : IWorkspaceLocks
    {
        public Task<WorkspaceCapabilities> GetCapabilitiesAsync(CancellationToken ct) => Task.FromResult(new WorkspaceCapabilities(true, true, true, true));
        public Task<string?> TryAcquireAsync(string name, CancellationToken ct) => Task.FromResult<string?>(readOnly ? null : "lock");
        public Task<string> WaitForAsync(string name, CancellationToken ct) => Task.FromResult("lock");
        public Task<bool> ReleaseAsync(string name, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> IsHeldAsync(string name, CancellationToken ct) => Task.FromResult(!readOnly);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
