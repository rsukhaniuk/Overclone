using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Client.Analysis;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Client.Workspace.Transfer;
using Xunit;

namespace Workspace.Tests;

public sealed class KnuWorkflowTests
{
    private static TaskCompletionSource<bool> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);
    private static async Task<AnalysisState> After(AnalysisCoordinator c, Action action, Func<AnalysisState, bool> accept)
    {
        var reached = new TaskCompletionSource<AnalysisState>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(AnalysisState s) { if (accept(s)) reached.TrySetResult(s); }
        c.Changed += Changed;
        try { action(); return await reached.Task.WaitAsync(HangGuard); }
        finally { c.Changed -= Changed; }
    }
    private static async Task<AnalysisReport> ReportAfter(AnalysisCoordinator c, Action action)
        => Assert.IsType<AnalysisState.Completed>(await After(c, action, s => s is AnalysisState.Completed)).Report;

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Knu_capture_remains_unsaved_or_readonly_with_binary_metadata_only(bool readOnly)
    {
        await using var h = await Harness.Create(readOnly, binary: true);
        if (!readOnly) Assert.True(h.Workspace.EditText(h.FileId, "user edit🙂\r\n"));
        var phase = h.Workspace.SavePhase; var head = h.Workspace.CurrentRevision!.Id;
        await using var factory = new AnalysisSnapshotFactory(h.Workspace);
        var selection = AnalysisSelection.Knu(AnalysisWorkType.Unspecified);
        var captured = Assert.IsType<CaptureResult.Captured>(await factory.CaptureSelectionAsync(selection, CancellationToken.None)).Snapshot;
        Assert.Equal(selection, captured.Selection);
        Assert.Equal(readOnly, h.Workspace.IsReadOnly);
        Assert.Single(captured.Files); Assert.Single(captured.Excluded);
        Assert.DoesNotContain(h.BinaryHash, h.Repo.Reads);
        if (readOnly) Assert.Equal(h.Fixture.Bytes, captured.Files[0].Bytes.ToArray());
        else Assert.Equal(new byte[] {239,187,191}.Concat(Encoding.UTF8.GetBytes("user edit🙂\n")), captured.Files[0].Bytes);
        Assert.Equal(phase, h.Workspace.SavePhase); Assert.Equal(head, h.Workspace.CurrentRevision.Id);
        Assert.Equal(0, h.Repo.Commits);
    }

    [Fact]
    public async Task Direct_capture_rechecks_selection_after_held_storage_read()
    {
        await using var h = await Harness.Create();
        var selected = AnalysisSelection.Knu(AnalysisWorkType.Unspecified);
        await using var factory = new AnalysisSnapshotFactory(h.Workspace, () => selected);
        h.Repo.ReadGate = Gate();
        var task = factory.CaptureSelectionAsync(selected, CancellationToken.None);
        await h.Repo.ReadReached.Task.WaitAsync(HangGuard);
        selected = AnalysisSelection.Knu(AnalysisWorkType.Coursework);
        h.Repo.ReadGate.TrySetResult(true);
        Assert.IsType<CaptureResult.Superseded>(await task.WaitAsync(HangGuard));
        var current = Assert.IsType<CaptureResult.Captured>(await factory.CaptureSelectionAsync(selected, CancellationToken.None));
        Assert.Equal(selected, current.Snapshot.Selection);
    }

    [Fact]
    public async Task Direct_capture_selection_version_rejects_away_and_back()
    {
        await using var h = await Harness.Create();
        var selected = AnalysisSelection.Knu(AnalysisWorkType.Unspecified);
        long version = 0;
        await using var factory = new AnalysisSnapshotFactory(h.Workspace, () => selected, () => version);
        h.Repo.ReadGate = Gate();
        var task = factory.CaptureSelectionAsync(selected, CancellationToken.None);
        await h.Repo.ReadReached.Task.WaitAsync(HangGuard);
        selected = AnalysisSelection.EngineStructural(); version++;
        selected = AnalysisSelection.Knu(AnalysisWorkType.Unspecified); version++;
        h.Repo.ReadGate.TrySetResult(true);
        Assert.IsType<CaptureResult.Superseded>(await task.WaitAsync(HangGuard));
        var fresh = Assert.IsType<CaptureResult.Captured>(await factory.CaptureSelectionAsync(selected, CancellationToken.None));
        Assert.Equal(2, fresh.Snapshot.SelectionVersion);
        Assert.Equal(selected, fresh.Snapshot.Selection);
    }

    [Fact]
    public async Task Direct_capture_rechecks_head_even_without_generation_or_epoch_change()
    {
        await using var h = await Harness.Create(extraText: true);
        Assert.True(h.Workspace.EditText(h.NotesId, "unsaved note"));
        await using var factory = new AnalysisSnapshotFactory(h.Workspace);
        var gen = h.Workspace.WorkingGeneration; var epoch = factory.CurrentWorkspaceEpoch;
        var oldHead = h.Workspace.CurrentRevision!.Id;
        h.Repo.ReadGate = Gate();
        var task = factory.CaptureSelectionAsync(AnalysisSelection.Knu(AnalysisWorkType.Unspecified), CancellationToken.None);
        await h.Repo.ReadReached.Task.WaitAsync(HangGuard);
        Assert.Equal(CommitStatus.Saved, (await h.Workspace.SaveAsync(CancellationToken.None)).Status);
        Assert.Equal(gen, h.Workspace.WorkingGeneration); Assert.Equal(epoch, factory.CurrentWorkspaceEpoch);
        Assert.NotEqual(oldHead, h.Workspace.CurrentRevision!.Id);
        h.Repo.ReadGate.TrySetResult(true);
        Assert.IsType<CaptureResult.Superseded>(await task.WaitAsync(HangGuard));
    }

    [Theory]
    [InlineData("capture",false,false)] [InlineData("capture",true,false)]
    [InlineData("import",false,false)] [InlineData("import",true,false)]
    [InlineData("initialize",false,false)] [InlineData("initialize",true,false)]
    [InlineData("response",false,false)] [InlineData("response",true,false)]
    [InlineData("capture",false,true)] [InlineData("capture",true,true)]
    [InlineData("import",false,true)] [InlineData("import",true,true)]
    [InlineData("initialize",false,true)] [InlineData("initialize",true,true)]
    [InlineData("response",false,true)] [InlineData("response",true,true)]
    public async Task Selection_switch_during_held_await_never_revives_old_job(string stage, bool awayAndBack, bool profileSwitch)
    {
        await using var h = await Harness.Create();
        switch (stage)
        {
            case "capture": h.Repo.ReadGate = Gate(); h.Js.ImportGate = Gate(); break;
            case "import": h.Js.ImportGate = Gate(); break;
            case "initialize": h.Js.InitializeGate = Gate(); break;
            case "response": h.Js.ResponseGate = Gate(); break;
        }
        var completed = new List<AnalysisReport>();
        h.Analysis.Changed += s => { if (s is AnalysisState.Completed c) completed.Add(c.Report); };
        var gen = h.Workspace.WorkingGeneration; var head = h.Workspace.CurrentRevision!.Id;
        var result = ReportAfter(h.Analysis, () => h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.Unspecified)));
        var reached = stage switch {
            "capture" => h.Repo.ReadReached.Task, "import" => h.Js.ImportReached.Task,
            "initialize" => h.Js.InitializeReached.Task, _ => h.Js.ResponseReached.Task,
        };
        await reached.WaitAsync(HangGuard);
        var old = h.Analyzer.Captured.FirstOrDefault();
        if (profileSwitch) h.Analysis.SetProfile(AnalysisProfile.EngineStructural());
        else h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.Coursework));
        if (awayAndBack) h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.Unspecified));
        Assert.False(h.Analysis.State is AnalysisState.Completed);
        h.Repo.ReadGate?.TrySetResult(true); h.Js.ImportGate?.TrySetResult(true);
        h.Js.InitializeGate?.TrySetResult(true); h.Js.ResponseGate?.TrySetResult(true);
        var report = await result.WaitAsync(HangGuard);
        var expected = profileSwitch && !awayAndBack ? AnalysisSelection.EngineStructural()
            : AnalysisSelection.Knu(awayAndBack ? AnalysisWorkType.Unspecified : AnalysisWorkType.Coursework);
        Assert.Equal(expected, report.Snapshot.Selection);
        Assert.Equal(report.Snapshot.Configuration, report.Result.Configuration);
        if (expected.Configuration is not null)
        {
            Assert.Equal(report.Snapshot.Configuration, Assert.Single(report.Result.Knu!.FixDescriptors).Configuration);
            Assert.Equal(report.Snapshot.Id.ToString(), Assert.Single(report.Result.Knu.FixDescriptors).SnapshotId);
        }
        else Assert.Null(report.Result.Knu);
        if (old is not null) Assert.NotEqual(old.Id, report.Snapshot.Id);
        Assert.Single(completed);
        Assert.Equal(gen, h.Workspace.WorkingGeneration); Assert.Equal(head, h.Workspace.CurrentRevision.Id);
        Assert.Equal(0, h.Repo.Commits);
        Assert.Equal(h.Fixture.Bytes, report.Snapshot.Files[0].Bytes.ToArray());
        foreach (var meta in h.Js.Metadata)
        {
            var p = JsonNode.Parse(meta)!["payload"]!;
            var sid = p["snapshotId"]!.GetValue<string>();
            var snapshot = h.Analyzer.Captured.Single(s => s.Id.ToString() == sid);
            Assert.Equal(snapshot.Configuration, p["configuration"] is { } cfg ? AnalysisJson.ParseConfiguration(cfg.ToJsonString()) : null);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Profile_switch_away_and_back_invalidates_reports_and_navigation(bool backToKnu)
    {
        await using var h = await Harness.Create();
        var old = await ReportAfter(h.Analysis, () => h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.Unspecified)));
        var oldFix = Assert.Single(old.Result.Knu!.FixDescriptors);
        var nav = new SourceNavigator(h.Analysis, h.Workspace, h.Analyzer);
        var engine = await ReportAfter(h.Analysis, () => h.Analysis.SetProfile(AnalysisProfile.EngineStructural()));
        Assert.Null(engine.Snapshot.Configuration); Assert.Null(engine.Result.Knu);
        if (backToKnu) await ReportAfter(h.Analysis, () => h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.Unspecified)));
        Assert.False(nav.IsStillCurrent(old));
        Assert.IsType<NavigationResult.Rejected>(await nav.NavigateAsync(old, old.Result.Diagnostics[0], CancellationToken.None));
        Assert.NotEqual(old.Snapshot.Id, Assert.IsType<AnalysisState.Completed>(h.Analysis.State).Report.Snapshot.Id);
        Assert.Equal(old.Snapshot.Id.ToString(), oldFix.SnapshotId); // remains old data, never current/applicable
        Assert.Equal(0, h.Repo.Commits);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Selection_change_during_held_navigation_rejects_old_report_and_fix_selection(bool profileSwitch)
    {
        await using var h = await Harness.Create();
        var old = await ReportAfter(h.Analysis, () => h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.Unspecified)));
        var nav = new SourceNavigator(h.Analysis, h.Workspace, h.Analyzer);
        var heading = old.Result.Diagnostics.Single(d => d.RuleId == AnalysisCatalog.Heading);
        var selected = Assert.IsType<NavigationResult.Selected>(await nav.NavigateAsync(old, heading, CancellationToken.None));
        var normalized = Encoding.UTF8.GetString(h.Fixture.Bytes).TrimStart('\ufeff').Replace("\r\n", "\n").Replace('\r','\n');
        Assert.Equal(normalized.LastIndexOf('.'), selected.TextareaStart);
        Assert.Equal(".", selected.ExpectedNormalizedSubstring);
        h.Js.EpochGate = Gate();
        h.Js.EpochReached = Gate();
        var task = nav.NavigateAsync(old, heading, CancellationToken.None);
        await h.Js.EpochReached.Task.WaitAsync(HangGuard);
        var fresh = ReportAfter(h.Analysis, () => {
            if (profileSwitch) h.Analysis.SetProfile(AnalysisProfile.EngineStructural());
            else h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.MasterQualification));
        });
        Assert.False(nav.IsStillCurrent(old));
        h.Js.EpochGate.TrySetResult(true);
        Assert.IsType<NavigationResult.Rejected>(await task.WaitAsync(HangGuard));
        var report = await fresh.WaitAsync(HangGuard);
        Assert.NotEqual(old.Snapshot.Id, report.Snapshot.Id);
        if (profileSwitch) Assert.Null(report.Result.Configuration);
        else Assert.Equal(AnalysisWorkType.MasterQualification, report.Result.Configuration!.WorkType);
        Assert.False(nav.IsStillCurrent(old)); Assert.Equal(0, h.Repo.Commits);
    }

    [Fact]
    public async Task Stale_worker_epoch_cannot_publish_a_Knu_report()
    {
        await using var h = await Harness.Create();
        h.Js.EpochGate = Gate();
        var result = After(h.Analysis, () => h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.Unspecified)), s => s is AnalysisState.Superseded);
        await h.Js.EpochReached.Task.WaitAsync(HangGuard);
        h.Js.CurrentEpoch = 8;
        h.Js.EpochGate.TrySetResult(true);
        Assert.IsType<AnalysisState.Superseded>(await result.WaitAsync(HangGuard));
        Assert.Equal(0, h.Repo.Commits);
    }

    [Theory]
    [InlineData("unknownProfile")][InlineData("unknownVersion")][InlineData("missingConfiguration")][InlineData("badWorkType")]
    public async Task Invalid_selection_invalidates_immediately_without_dispatch(string fault)
    {
        await using var h = await Harness.Create();
        var old = await ReportAfter(h.Analysis, () => h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.Unspecified)));
        int count = h.Analyzer.Captured.Count;
        var invalid = fault switch {
            "unknownProfile" => new AnalysisSelection(new("unknown","1"), null),
            "unknownVersion" => new(new(AnalysisCatalog.KnuId,"2"), new(AnalysisWorkType.Unspecified)),
            "missingConfiguration" => new(new(AnalysisCatalog.KnuId,AnalysisCatalog.Version), null),
            _ => AnalysisSelection.Knu((AnalysisWorkType)999),
        };
        h.Analysis.SetSelection(invalid);
        Assert.IsType<AnalysisState.InvalidInput>(h.Analysis.State);
        Assert.Equal(count, h.Analyzer.Captured.Count);
        Assert.False(new SourceNavigator(h.Analysis,h.Workspace,h.Analyzer).IsStillCurrent(old));
    }

    private sealed class RecordingAnalyzer(ISourceAnalyzer inner) : ISourceAnalyzer
    {
        public List<AnalysisSnapshot> Captured { get; } = new();
        public Task<AnalysisJobResult> AnalyzeAsync(AnalysisSnapshot s, CancellationToken ct)
        { Captured.Add(s); return inner.AnalyzeAsync(s, ct); }
        public Task<bool> IsCurrentWorkerEpochAsync(long epoch, CancellationToken ct) => inner.IsCurrentWorkerEpochAsync(epoch,ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private sealed class Harness : IAsyncDisposable
    {
        public KnuFixture Fixture = null!;
        public Repo Repo = null!;
        public WorkspaceCoordinator Workspace = null!;
        public AnalysisCoordinator Analysis = null!;
        public RecordingAnalyzer Analyzer = null!;
        public KnuProtocolTests.FixtureJs Js = null!;
        public FileId FileId, NotesId;
        public SHA256 BinaryHash;
        public static async Task<Harness> Create(bool readOnly = false, bool binary = false, bool extraText = false)
        {
            var h = new Harness { Fixture = KnuFixture.Load("negative"), Repo = new Repo() };
            var file = h.Fixture.Snapshot().Files[0]; h.FileId = file.FileId;
            h.Repo.Content[file.Sha256] = h.Fixture.Bytes;
            var files = new List<ProjectFile> { new(file.FileId,"main.tex","text",file.Sha256,file.ByteLength,true) };
            if (binary) { h.BinaryHash = new SHA256(new string('b',64)); files.Add(new(FileId.New(),"asset.bin","binary",h.BinaryHash,1000000)); }
            if (extraText)
            {
                var b = Encoding.UTF8.GetBytes("old note"); var hash = SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(b));
                h.NotesId = FileId.New(); files.Add(new(h.NotesId,"notes.txt","text",hash,b.Length)); h.Repo.Content[hash] = b;
            }
            var pid = ProjectId.New(); var rev = new ProjectRevision(RevisionId.New(),pid,null,files.ToImmutableArray(),DateTimeOffset.UtcNow);
            h.Repo.Current = new(new(pid,"synthetic",rev.Id),rev,
                files.Where(f=>f.MediaKind=="text").ToDictionary(f=>f.Id,f=>TextContent.DecodeStrict(h.Repo.Content[f.ContentHash])),
                new Dictionary<FileId,byte[]>(),files.ToDictionary(f=>f.Id,f=>f.HasBom),readOnly);
            h.Workspace = new(h.Repo, new Locks(readOnly), new HistoryService(h.Repo));
            await h.Workspace.OpenAsync(pid,CancellationToken.None);
            h.Js = new KnuProtocolTests.FixtureJs((_,rid) =>
            {
                var s = h.Analyzer.Captured[^1];
                var json = h.Fixture.Reply(s,rid);
                if (s.Configuration is null)
                {
                    // Scheduling-only engine projection of the preserved Rust
                    // engine facts/result; the standalone engine fixture also
                    // exercises its full actual wire shape in protocol tests.
                    var root = JsonNode.Parse(json)!; var p = root["payload"]!;
                    p["profile"] = new JsonObject { ["id"]="engine-structural",["version"]="0" };
                    p["aggregateOutcome"] = p["knu"]!["engineAggregateOutcome"]!.DeepClone();
                    p["ruleResults"] = new JsonArray(p["ruleResults"]![0]!.DeepClone());
                    p["diagnostics"] = new JsonArray();
                    p.AsObject().Remove("knu"); p.AsObject().Remove("configuration");
                    return root.ToJsonString();
                }
                return json;
            });
            h.Analyzer = new(new RustSourceAnalyzer(h.Js));
            h.Analysis = new(h.Workspace,h.Analyzer,delayAsync: (_,ct)=>Task.Delay(Timeout.Infinite,ct));
            return h;
        }
        public async ValueTask DisposeAsync()
        {
            Js.ImportGate?.TrySetResult(true); Js.InitializeGate?.TrySetResult(true); Js.ResponseGate?.TrySetResult(true); Js.EpochGate?.TrySetResult(true); Repo.ReadGate?.TrySetResult(true);
            await Analysis.DisposeAsync(); await Analyzer.DisposeAsync(); await Workspace.DisposeAsync();
        }
    }
    private sealed class Repo : IProjectRepository
    {
        public ProjectSnapshot Current = null!;
        public Dictionary<SHA256,byte[]> Content { get; } = new();
        public List<SHA256> Reads { get; } = new();
        public int Commits;
        public TaskCompletionSource<bool>? ReadGate { get; set; }
        public TaskCompletionSource<bool> ReadReached { get; } = Gate();
        private bool _held;
        public Task<ProjectSnapshot> OpenAsync(ProjectId id,CancellationToken ct) => Task.FromResult(Current);
        public async Task<byte[]> ReadContentAsync(SHA256 hash,CancellationToken ct)
        {
            Reads.Add(hash);
            if (!_held && ReadGate is { } g) { _held=true; ReadReached.TrySetResult(true); await g.Task; }
            return Content[hash]; // no binary object is provisioned: reading it fails
        }
        public Task<CommitResult> CommitAsync(ProjectCommit batch,RevisionId? head,CancellationToken ct)
        { Commits++; foreach(var c in batch.Content) Content[c.Hash]=c.Bytes; return Task.FromResult(CommitResult.Saved(batch.SelectedHead.Id)); }
        public Task<HistoryPage> ReadHistoryAsync(ProjectId p,HistoryCursor? cursor,CancellationToken ct) => Task.FromResult(new HistoryPage(Array.Empty<HistoryCheckpoint>()));
        public Task<RevisionManifest> ReadRevisionAsync(ProjectId p,RevisionId r,CancellationToken ct) => Task.FromResult(RevisionManifest.Ok(Current.Revision!));
        public Task<Project> CreateProjectAsync(string n,CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteProjectAsync(ProjectId p,CancellationToken ct) => throw new NotSupportedException();
        public Task<CommitResult> RepairHeadAsync(ProjectId p,RevisionId a,RevisionId b,CancellationToken ct) => throw new NotSupportedException();
        public Task<ImportPublishResult> PublishImportAsync(StagedProject p,CancellationToken ct) => throw new NotSupportedException();
        public Task<CommitResult> CreateRestoreRevisionAsync(ProjectId p,RevisionId h,ProjectRevision r,RevisionId s,string reason,CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Locks(bool readOnly) : IWorkspaceLocks
    {
        public Task<WorkspaceCapabilities> GetCapabilitiesAsync(CancellationToken ct) => Task.FromResult(new WorkspaceCapabilities(true,true,true,true));
        public Task<string?> TryAcquireAsync(string n,CancellationToken ct) => Task.FromResult<string?>(readOnly?null:"lock");
        public Task<string> WaitForAsync(string n,CancellationToken ct) => Task.FromResult("lock");
        public Task<bool> ReleaseAsync(string n,CancellationToken ct) => Task.FromResult(true);
        public Task<bool> IsHeldAsync(string n,CancellationToken ct) => Task.FromResult(!readOnly);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
