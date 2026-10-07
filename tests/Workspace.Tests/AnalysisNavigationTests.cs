using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Client.Analysis;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Client.Workspace.Transfer;
using Xunit;

namespace Workspace.Tests;

/// <summary>
/// Guarded click-to-source navigation tests (M2a-2b-2b) for the public
/// <see cref="SourceNavigator"/> against the REAL <see cref="WorkspaceCoordinator"/>,
/// REAL <see cref="AnalysisSnapshotFactory"/> (via the REAL
/// <see cref="AnalysisCoordinator"/>) and a fake <see cref="ISourceAnalyzer"/>
/// for controlled races. Tests PUBLIC helper outcomes only — no reflection or
/// private-field assertions. The ACCEPTED analyzer/coordinator/factory behavior
/// is unchanged; only small additive navigation helpers are exercised.
///
/// Covers: current exact span incl. BOM+CRLF+Ukrainian+emoji; BOM/CRLF
/// NonSelectable (expanded display, no selection); canonical diagnostic
/// resolution (a passed clone with a matching Id but a forged span/file cannot
/// select — the canonical object wins); Unicode near-EOF / inside-CRLF / BOM
/// NonSelectable never throws and never treats byte offsets as char indices;
/// wrong-file / invalid span; stale report after edit/rename/delete/close/
/// profile-change; worker-epoch mismatch; edit/close DURING the awaited epoch
/// validation; an already-cancelled or now-cancelled navigation token rejects
/// even when a fake epoch query ignores the token; IsStillCurrent after a
/// mutation; multi-file selects the RIGHT file. No Task.Delay timing sleeps to
/// guess state — barriers (TaskCompletionSource) and a manually-controlled
/// debounce seam, plus bounded WaitAsync hang guards. No source bytes/text are
/// ever modified by navigation.
/// </summary>
public class AnalysisNavigationTests
{
    private static readonly ProjectId Pid = ProjectId.New();

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private static byte[] Bom(params byte[] b)
    {
        var r = new byte[TextContent.Bom.Length + b.Length];
        TextContent.Bom.CopyTo(r, 0);
        Buffer.BlockCopy(b, 0, r, TextContent.Bom.Length, b.Length);
        return r;
    }
    private static SHA256 Sha(byte[] b)
        => Client.Workspace.Domain.SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(b));
    private static ProjectFile TextFile(FileId id, string path, SHA256 hash, long len, bool hasBom)
        => new(id, path, "text", hash, len, hasBom);
    private static ProjectFile BinaryFile(FileId id, string path, SHA256 hash, long len)
        => new(id, path, "binary", hash, len, false);

    private static (NavRepo repo, WorkspaceCoordinator coord) NewCoord(bool readOnly = false)
    {
        var repo = new NavRepo();
        var locks = new NavLocks { ReadOnly = readOnly };
        var history = new HistoryService(repo);
        var coord = new WorkspaceCoordinator(repo, locks, history);
        return (repo, coord);
    }

    private static (FileId, SHA256, byte[]) OpenDurable(WorkspaceCoordinator coord, NavRepo repo, string path, byte[] bytes, bool hasBom)
    {
        var hash = Sha(bytes);
        repo.SetContent(hash, bytes);
        var fid = FileId.New();
        var text = hasBom ? TextContent.DecodeStrict(bytes) : TextContent.DecodeStrict(bytes);
        var rev = new ProjectRevision(RevisionId.New(), Pid, null,
            ImmutableArray.Create<ProjectFile>(TextFile(fid, path, hash, bytes.LongLength, hasBom)), DateTimeOffset.UtcNow);
        var project = new Project(Pid, "p", rev.Id);
        repo.Current = new ProjectSnapshot(project, rev,
            new Dictionary<FileId, string> { [fid] = text },
            new Dictionary<FileId, byte[]>(),
            new Dictionary<FileId, bool> { [fid] = hasBom },
            IsReadOnly: false);
        coord.OpenAsync(Pid, CancellationToken.None).GetAwaiter().GetResult();
        return (fid, hash, bytes);
    }

    private static ProjectSnapshot Snapshot(ProjectId pid, params ProjectFile[] files)
    {
        var rev = new ProjectRevision(RevisionId.New(), pid, null,
            ImmutableArray.CreateRange(files), DateTimeOffset.UtcNow);
        var project = new Project(pid, "p", rev.Id);
        return new ProjectSnapshot(project, rev, new Dictionary<FileId, string>(),
            new Dictionary<FileId, byte[]>(), new Dictionary<FileId, bool>(), IsReadOnly: false);
    }

    private static TaskCompletionSource<T> NewTcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Build a valid canonical AnalysisResultDto echoing the snapshot
    /// id/profile, with ONE diagnostic at the given byte span over the captured
    /// file, plus the typed PDF unavailable capabilities.</summary>
    private static AnalysisResultDto BuildResult(AnalysisSnapshot snap, FileId file, long startByte, long endByte, RuleOutcome outcome = RuleOutcome.Fail)
    {
        var cf = System.Linq.Enumerable.First(snap.Files, f => f.FileId == file);
        var diag = new AnalysisEngineDiagnosticDto(
            Id: file.ToString() + ":ENGINE-STRUCT-001:0",
            RuleId: "ENGINE-STRUCT-001",
            Origin: "engine",
            Severity: "warning",
            FileId: file.ToString(),
            Span: new AnalysisSpanDto(startByte, endByte),
            Message: "Unmatched opening brace.",
            PrimaryLocation: new AnalysisLocationDto(Line: 2, Column: 0));
        var fileFacts = new[]
        {
            new AnalysisSourceFileFactsDto(
                file.ToString(), ByteLength: cf.ByteLength, HasBom: cf.HasBom, NewlineKind.Crlf,
                Truncated: false, BraceDepthExceeded: false, TokenCount: 0,
                EngineOutcome: outcome, Array.Empty<AnalysisTokenDto>()),
        };
        var perFile = new[]
        {
            new AnalysisPerFileOutcomeDto(file.ToString(), outcome, new[] { diag.Id }),
        };
        return new AnalysisResultDto(
            snap.Id.ToString(),
            new AnalysisProfileDto(snap.Profile.Id, snap.Profile.Version),
            new AnalysisSourceFactsDto(fileFacts),
            new[] { diag },
            new[]
            {
                new AnalysisRuleResultDto("ENGINE-STRUCT-001", "engine", outcome, perFile, Array.Empty<string>()),
            },
            outcome,
            new AnalysisCoverageDto(
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                DiagnosticsTruncated: false, ResultTruncated: false),
            new[] { "pdfFacts", "pdfChecks" });
    }

    private sealed class FakeAnalyzer : ISourceAnalyzer
    {
        public Func<AnalysisSnapshot, AnalysisJobResult>? AnalyzeHandler;
        public TaskCompletionSource<bool>? EpochGate;
        public bool EpochResult = true;
        /// <summary>When true, IsCurrentWorkerEpochAsync IGNORES ct (awaits the
        /// gate without the token) so a now-cancelled token is rejected only by
        /// the navigator's own re-checks, not by the analyzer.</summary>
        public bool IgnoreCt;
        public int AnalyzeCalls, EpochCalls, DisposeCalls;
        public AnalysisSnapshot? LastSnapshot;
        private TaskCompletionSource<bool>? _analyzeReached, _epochReached;

        public TaskCompletionSource<bool> NewReached() => _analyzeReached = NewTcs<bool>();
        public void NewEpochReached() => _epochReached = NewTcs<bool>();
        public Task EpochReachedTask => _epochReached?.Task ?? Task.CompletedTask;

        public async Task<AnalysisJobResult> AnalyzeAsync(AnalysisSnapshot snapshot, CancellationToken ct)
        {
            AnalyzeCalls++;
            LastSnapshot = snapshot;
            _analyzeReached?.TrySetResult(true);
            if (ct.IsCancellationRequested) return new AnalysisJobResult.Cancelled("c", 0);
            return AnalyzeHandler is null
                ? new AnalysisJobResult.Completed("rid", 1, BuildResult(snapshot, snapshot.Files[0].FileId, 0, 1))
                : AnalyzeHandler(snapshot);
        }

        public async Task<bool> IsCurrentWorkerEpochAsync(long epoch, CancellationToken ct)
        {
            EpochCalls++;
            _epochReached?.TrySetResult(true);
            if (EpochGate is { } gate)
            {
                try { await (IgnoreCt ? gate.Task : gate.Task.WaitAsync(ct)).ConfigureAwait(false); }
                catch (OperationCanceledException) { return false; }
            }
            return EpochResult;
        }

        public ValueTask DisposeAsync() { DisposeCalls++; return ValueTask.CompletedTask; }
    }

    private sealed class DebounceController
    {
        private readonly List<TaskCompletionSource<bool>> _gates = new();
        private readonly object _g = new();
        public Task Delay(TimeSpan d, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_g) _gates.Add(tcs);
            return tcs.Task.WaitAsync(ct);
        }
        public void MatureLatest()
        {
            TaskCompletionSource<bool>? tcs = null;
            lock (_g) { if (_gates.Count > 0) tcs = _gates[^1]; _gates.Clear(); }
            tcs?.TrySetResult(true);
        }
    }

    private static async Task AwaitTerminalAsync(AnalysisCoordinator c)
    {
        static bool IsTerminal(AnalysisState s) => s is not (AnalysisState.Pending or AnalysisState.Running);
        if (IsTerminal(c.State)) return;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(AnalysisState s) { if (IsTerminal(s)) tcs.TrySetResult(true); }
        c.Changed += OnChanged;
        try { if (IsTerminal(c.State)) tcs.TrySetResult(true); await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { c.Changed -= OnChanged; }
    }

    // ---- 1. exact span over BOM+CRLF+Ukrainian+emoji ----

    [Fact]
    public async Task Exact_Span_Bom_Crlf_Ukrainian_Emoji_Selects_Correct_Range()
    {
        var (repo, coord) = NewCoord();
        // UTF-8 BOM + "Привіт 👩🏽\r\nРозділ {текст\r\n"
        var content = Bom(Encoding.UTF8.GetBytes("Привіт 👩🏽\r\nРозділ {текст\r\n"));
        var (fid, _, _) = OpenDurable(coord, repo, "main.tex", content, hasBom: true);

        var analyzer = new FakeAnalyzer { AnalyzeHandler = snap =>
            new AnalysisJobResult.Completed("rid", 1, BuildResult(snap, fid, FindOpenBraceByte(content), FindOpenBraceByte(content) + 1)) };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached(); debounce.MatureLatest(); await r.Task; await AwaitTerminalAsync(coord2);
        var report = Assert.IsType<AnalysisState.Completed>(coord2.State).Report;
        var diag = Assert.Single(report.Result.Diagnostics);

        var nav = new SourceNavigator(coord2, coord, analyzer);
        var result = await nav.NavigateAsync(report, diag, CancellationToken.None);

        var sel = Assert.IsType<NavigationResult.Selected>(result);
        Assert.Equal(fid, sel.FileId);
        // The expected normalized substring MUST be "{" (the unmatched brace).
        Assert.Equal("{", sel.ExpectedNormalizedSubstring);
        Assert.Equal(sel.TextareaStart + 1, sel.TextareaEnd);
        // Source bytes unchanged: navigation never mutated them.
        Assert.Equal(content, repo.LastReadContent);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- 2. BOM / inside-CRLF are NonSelectable (no selection) ----

    [Fact]
    public async Task Span_Covering_Bom_Is_NonSelectable()
    {
        var (repo, coord) = NewCoord();
        var content = Bom(Encoding.UTF8.GetBytes("a{b\r\n"));
        var (fid, _, _) = OpenDurable(coord, repo, "main.tex", content, hasBom: true);
        var analyzer = new FakeAnalyzer { AnalyzeHandler = snap =>
            new AnalysisJobResult.Completed("rid", 1, BuildResult(snap, fid, 0, 3)) }; // spans the BOM
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached(); debounce.MatureLatest(); await r.Task; await AwaitTerminalAsync(coord2);
        var report = Assert.IsType<AnalysisState.Completed>(coord2.State).Report;
        var nav = new SourceNavigator(coord2, coord, analyzer);
        var result = await nav.NavigateAsync(report, Assert.Single(report.Result.Diagnostics), CancellationToken.None);
        var ns = Assert.IsType<NavigationResult.NonSelectable>(result);
        Assert.Contains("BOM", ns.Reason);
        Assert.Contains("main.tex", ns.Display);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Boundary_Between_Cr_Lf_Is_NonSelectable()
    {
        var (repo, coord) = NewCoord();
        var content = B("x\r\n{"); // CRLF then brace; place a span boundary between \r and \n
        var (fid, _, _) = OpenDurable(coord, repo, "main.tex", content, hasBom: false);
        // Span [1,3) = "\r\n" — boundary at 2 is between CR and LF (startByte=1 is scalar? \r is byte1, \n byte2)
        // Use a zero-length caret at byte 2 (between \r and \n): startByte=endByte=2.
        var analyzer = new FakeAnalyzer { AnalyzeHandler = snap =>
            new AnalysisJobResult.Completed("rid", 1, BuildResult(snap, fid, 2, 2)) };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached(); debounce.MatureLatest(); await r.Task; await AwaitTerminalAsync(coord2);
        var report = Assert.IsType<AnalysisState.Completed>(coord2.State).Report;
        var nav = new SourceNavigator(coord2, coord, analyzer);
        var result = await nav.NavigateAsync(report, Assert.Single(report.Result.Diagnostics), CancellationToken.None);
        var ns = Assert.IsType<NavigationResult.NonSelectable>(result);
        Assert.Contains("CR", ns.Reason);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- 3. wrong file / invalid span -> Rejected ----

    [Fact]
    public async Task Wrong_File_Diagnostic_Is_Rejected()
    {
        var (repo, coord) = NewCoord();
        var content = B("a{b");
        var (fid, _, _) = OpenDurable(coord, repo, "main.tex", content, hasBom: false);
        var other = FileId.New();
        var analyzer = new FakeAnalyzer { AnalyzeHandler = snap =>
            new AnalysisJobResult.Completed("rid", 1, BuildResult(snap, fid, 1, 2)) };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached(); debounce.MatureLatest(); await r.Task; await AwaitTerminalAsync(coord2);
        var report = Assert.IsType<AnalysisState.Completed>(coord2.State).Report;
        // Fabricate a diagnostic with a foreign fileId not in the snapshot.
        var diag = new AnalysisEngineDiagnosticDto("x", "ENGINE-STRUCT-001", "engine", "warning",
            other.ToString(), new AnalysisSpanDto(0, 1), "m", null);
        var nav = new SourceNavigator(coord2, coord, analyzer);
        var result = await nav.NavigateAsync(report, diag, CancellationToken.None);
        Assert.IsType<NavigationResult.Rejected>(result);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Out_Of_Range_Span_Is_Rejected()
    {
        var (repo, coord) = NewCoord();
        var content = B("a{b");
        var (fid, _, _) = OpenDurable(coord, repo, "main.tex", content, hasBom: false);
        var analyzer = new FakeAnalyzer { AnalyzeHandler = snap =>
            new AnalysisJobResult.Completed("rid", 1, BuildResult(snap, fid, 100, 200)) };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached(); debounce.MatureLatest(); await r.Task; await AwaitTerminalAsync(coord2);
        var report = Assert.IsType<AnalysisState.Completed>(coord2.State).Report;
        var nav = new SourceNavigator(coord2, coord, analyzer);
        var result = await nav.NavigateAsync(report, Assert.Single(report.Result.Diagnostics), CancellationToken.None);
        Assert.IsType<NavigationResult.Rejected>(result);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- 4. stale report after edit/rename/delete ----

    [Theory]
    [InlineData("edit")]
    [InlineData("rename")]
    [InlineData("delete")]
    public async Task Stale_Report_After_Mutation_Is_Rejected(string kind)
    {
        var (repo, coord) = NewCoord();
        var content = B("a{b");
        var (fid, _, _) = OpenDurable(coord, repo, "main.tex", content, hasBom: false);
        var analyzer = new FakeAnalyzer { AnalyzeHandler = snap =>
            new AnalysisJobResult.Completed("rid", 1, BuildResult(snap, fid, 1, 2)) };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached(); debounce.MatureLatest(); await r.Task; await AwaitTerminalAsync(coord2);
        var report = Assert.IsType<AnalysisState.Completed>(coord2.State).Report;
        var diag = Assert.Single(report.Result.Diagnostics);

        switch (kind)
        {
            case "edit": Assert.True(coord.EditText(fid, "changed")); break;
            case "rename": Assert.True(coord.RenameFile(fid, "renamed.tex")); break;
            case "delete": Assert.True(coord.DeleteFile(fid)); break;
        }

        var nav = new SourceNavigator(coord2, coord, analyzer);
        var result = await nav.NavigateAsync(report, diag, CancellationToken.None);
        Assert.IsType<NavigationResult.Rejected>(result);
        // IsStillCurrent reflects the drop too.
        Assert.False(nav.IsStillCurrent(report));
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- 5. open/close (project change) stale ----

    [Fact]
    public async Task Stale_Report_After_Close_Is_Rejected()
    {
        var (repo, coord) = NewCoord();
        var content = B("a{b");
        var (fid, _, _) = OpenDurable(coord, repo, "main.tex", content, hasBom: false);
        var analyzer = new FakeAnalyzer { AnalyzeHandler = snap =>
            new AnalysisJobResult.Completed("rid", 1, BuildResult(snap, fid, 1, 2)) };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached(); debounce.MatureLatest(); await r.Task; await AwaitTerminalAsync(coord2);
        var report = Assert.IsType<AnalysisState.Completed>(coord2.State).Report;
        var diag = Assert.Single(report.Result.Diagnostics);

        await coord.CloseAsync(CancellationToken.None);

        var nav = new SourceNavigator(coord2, coord, analyzer);
        var result = await nav.NavigateAsync(report, diag, CancellationToken.None);
        Assert.IsType<NavigationResult.Rejected>(result);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- 6. worker epoch mismatch -> Rejected ----

    [Fact]
    public async Task Worker_Epoch_Mismatch_Is_Rejected()
    {
        var (repo, coord) = NewCoord();
        var content = B("a{b");
        var (fid, _, _) = OpenDurable(coord, repo, "main.tex", content, hasBom: false);
        // Publish succeeds with epoch CURRENT (Completed), then the worker is
        // reset (epoch no longer current); the navigator's click-time epoch check
        // rejects the now-stale report.
        var analyzer = new FakeAnalyzer { AnalyzeHandler = snap =>
            new AnalysisJobResult.Completed("rid", 1, BuildResult(snap, fid, 1, 2)), EpochResult = true };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached(); debounce.MatureLatest(); await r.Task; await AwaitTerminalAsync(coord2);
        var report = Assert.IsType<AnalysisState.Completed>(coord2.State).Report;
        // Simulate a worker reset after publish: the next epoch check returns false.
        analyzer.EpochResult = false;
        var nav = new SourceNavigator(coord2, coord, analyzer);
        var result = await nav.NavigateAsync(report, Assert.Single(report.Result.Diagnostics), CancellationToken.None);
        var rej = Assert.IsType<NavigationResult.Rejected>(result);
        Assert.Contains("reset", rej.Reason);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- 7. edit DURING the awaited epoch validation -> Rejected ----

    [Fact]
    public async Task Edit_During_Epoch_Await_Is_Rejected()
    {
        var (repo, coord) = NewCoord();
        var content = B("a{b");
        var (fid, _, _) = OpenDurable(coord, repo, "main.tex", content, hasBom: false);
        var analyzer = new FakeAnalyzer { AnalyzeHandler = snap =>
            new AnalysisJobResult.Completed("rid", 1, BuildResult(snap, fid, 1, 2)) };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached(); debounce.MatureLatest(); await r.Task; await AwaitTerminalAsync(coord2);
        var report = Assert.IsType<AnalysisState.Completed>(coord2.State).Report;
        var diag = Assert.Single(report.Result.Diagnostics);

        analyzer.EpochGate = NewTcs<bool>();
        analyzer.EpochResult = true;
        analyzer.NewEpochReached();
        var nav = new SourceNavigator(coord2, coord, analyzer);
        var navTask = nav.NavigateAsync(report, diag, CancellationToken.None);
        await analyzer.EpochReachedTask; // epoch check reached, now held
        // Edit during the await bumps the intent; the coordinator drops the report.
        Assert.True(coord.EditText(fid, "changed while verifying"));
        analyzer.EpochGate.TrySetResult(true);
        var result = await navTask;
        Assert.IsType<NavigationResult.Rejected>(result);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- 8. close DURING the awaited epoch validation -> Rejected ----

    [Fact]
    public async Task Close_During_Epoch_Await_Is_Rejected()
    {
        var (repo, coord) = NewCoord();
        var content = B("a{b");
        var (fid, _, _) = OpenDurable(coord, repo, "main.tex", content, hasBom: false);
        var analyzer = new FakeAnalyzer { AnalyzeHandler = snap =>
            new AnalysisJobResult.Completed("rid", 1, BuildResult(snap, fid, 1, 2)) };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached(); debounce.MatureLatest(); await r.Task; await AwaitTerminalAsync(coord2);
        var report = Assert.IsType<AnalysisState.Completed>(coord2.State).Report;
        var diag = Assert.Single(report.Result.Diagnostics);

        analyzer.EpochGate = NewTcs<bool>();
        analyzer.EpochResult = true;
        analyzer.NewEpochReached();
        var nav = new SourceNavigator(coord2, coord, analyzer);
        var navTask = nav.NavigateAsync(report, diag, CancellationToken.None);
        await analyzer.EpochReachedTask;
        await coord.CloseAsync(CancellationToken.None);
        analyzer.EpochGate.TrySetResult(true);
        var result = await navTask;
        Assert.IsType<NavigationResult.Rejected>(result);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- 9. multi-file: diagnostic from another file selects THAT file ----

    [Fact]
    public async Task Multi_File_Diagnostic_Selects_Target_File()
    {
        var (repo, coord) = NewCoord();
        var bytesA = B("same text\n");
        var bytesB = B("same text {unmatched\n"); // identical prefix + an unmatched brace
        var hashA = Sha(bytesA); var hashB = Sha(bytesB);
        repo.SetContent(hashA, bytesA); repo.SetContent(hashB, bytesB);
        var fa = FileId.New(); var fb = FileId.New();
        repo.Current = Snapshot(Pid,
            TextFile(fa, "a.tex", hashA, bytesA.LongLength, false),
            TextFile(fb, "b.tex", hashB, bytesB.LongLength, false));
        await coord.OpenAsync(Pid, CancellationToken.None);

        // Brace is at byte "same text ".length = 10 in b.tex.
        var braceByte = (long)Encoding.UTF8.GetByteCount("same text ");
        var analyzer = new FakeAnalyzer { AnalyzeHandler = snap =>
            new AnalysisJobResult.Completed("rid", 1, BuildResult(snap, fb, braceByte, braceByte + 1)) };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached(); debounce.MatureLatest(); await r.Task; await AwaitTerminalAsync(coord2);
        var report = Assert.IsType<AnalysisState.Completed>(coord2.State).Report;

        // Make file A the active file (same prefix text as B), then navigate to B.
        Assert.True(coord.SelectFile(fa));
        var nav = new SourceNavigator(coord2, coord, analyzer);
        var result = await nav.NavigateAsync(report, Assert.Single(report.Result.Diagnostics), CancellationToken.None);
        var sel = Assert.IsType<NavigationResult.Selected>(result);
        Assert.Equal(fb, sel.FileId);          // NOT fa despite identical text
        Assert.Equal("b.tex", sel.RelativePath);
        Assert.Equal("{", sel.ExpectedNormalizedSubstring);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- 10. Indeterminate aggregate keeps findings (no Pass/compliance) ----

    [Fact]
    public async Task Indeterminate_Report_Navigates_Findings_Remain()
    {
        var (repo, coord) = NewCoord();
        var content = B("a{b");
        var (fid, _, _) = OpenDurable(coord, repo, "main.tex", content, hasBom: false);
        var analyzer = new FakeAnalyzer { AnalyzeHandler = snap =>
            new AnalysisJobResult.Completed("rid", 1,
                BuildResult(snap, fid, 1, 2, outcome: RuleOutcome.Indeterminate)) };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached(); debounce.MatureLatest(); await r.Task; await AwaitTerminalAsync(coord2);
        var report = Assert.IsType<AnalysisState.Completed>(coord2.State).Report;
        Assert.Equal(RuleOutcome.Indeterminate, report.Result.AggregateOutcome);
        // The finding is still present and navigable.
        var nav = new SourceNavigator(coord2, coord, analyzer);
        var result = await nav.NavigateAsync(report, Assert.Single(report.Result.Diagnostics), CancellationToken.None);
        Assert.IsType<NavigationResult.Selected>(result);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    private static long FindOpenBraceByte(byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++) if (bytes[i] == (byte)'{') return i;
        throw new InvalidOperationException("no { in fixture");
    }

    // ---- canonical diagnostic resolution: a matching Id must not authorize ----
    // caller-supplied span/file fields. The canonical object in the report wins.

    private static async Task<(AnalysisReport report, AnalysisEngineDiagnosticDto diag, SourceNavigator nav, AnalysisCoordinator coord2, WorkspaceCoordinator coord)> SetupCanonicalAsync(
        byte[] content, bool hasBom, long startByte, long endByte, RuleOutcome outcome = RuleOutcome.Fail)
    {
        var (repo, coord) = NewCoord();
        var (fid, _, _) = OpenDurable(coord, repo, "main.tex", content, hasBom);
        var analyzer = new FakeAnalyzer { AnalyzeHandler = snap =>
            new AnalysisJobResult.Completed("rid", 1, BuildResult(snap, fid, startByte, endByte, outcome)) };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached(); debounce.MatureLatest(); await r.Task; await AwaitTerminalAsync(coord2);
        var report = Assert.IsType<AnalysisState.Completed>(coord2.State).Report;
        var diag = Assert.Single(report.Result.Diagnostics);
        var nav = new SourceNavigator(coord2, coord, analyzer);
        return (report, diag, nav, coord2, coord);
    }

    [Fact]
    public async Task Same_Id_Altered_Span_Uses_Canonical_Not_Caller_Fields()
    {
        // The real diagnostic is at [1,2) over "a{b" (selects "{"). A passed clone
        // with the SAME Id but a forged span [0,1) must NOT select "a"; the
        // canonical span wins and the original immutable diagnostic is unchanged.
        var (report, canonical, nav, coord2, coord) = await SetupCanonicalAsync(B("a{b"), false, 1, 2);
        var forged = new AnalysisEngineDiagnosticDto(
            Id: canonical.Id, RuleId: canonical.RuleId, Origin: canonical.Origin, Severity: canonical.Severity,
            FileId: canonical.FileId, Span: new AnalysisSpanDto(0, 1), Message: canonical.Message, PrimaryLocation: canonical.PrimaryLocation);
        var result = await nav.NavigateAsync(report, forged, CancellationToken.None);
        var sel = Assert.IsType<NavigationResult.Selected>(result);
        Assert.Equal("{", sel.ExpectedNormalizedSubstring);   // canonical span, NOT forged "a"
        Assert.Equal(sel.TextareaStart + 1, sel.TextareaEnd);
        // The canonical object in the report is unchanged.
        Assert.Equal(1, canonical.Span!.StartByte);
        Assert.Equal(2, canonical.Span!.EndByte);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Same_Id_Altered_File_Uses_Canonical_File()
    {
        // A clone with the same Id but a FOREIGN fileId must not forge a
        // wrong-file selection: the canonical file wins (selects its brace), and
        // the foreign file is never touched.
        var (report, canonical, nav, coord2, coord) = await SetupCanonicalAsync(B("a{b"), false, 1, 2);
        var foreign = FileId.New();
        var forged = new AnalysisEngineDiagnosticDto(
            Id: canonical.Id, RuleId: canonical.RuleId, Origin: canonical.Origin, Severity: canonical.Severity,
            FileId: foreign.ToString(), Span: new AnalysisSpanDto(0, 1), Message: canonical.Message, PrimaryLocation: canonical.PrimaryLocation);
        var result = await nav.NavigateAsync(report, forged, CancellationToken.None);
        var sel = Assert.IsType<NavigationResult.Selected>(result);
        Assert.Equal(canonical.FileId, sel.FileId.ToString());   // canonical file, NOT foreign
        Assert.Equal("{", sel.ExpectedNormalizedSubstring);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- Unicode near EOF / inside CRLF / BOM: bounded NonSelectable, no throw ----

    [Fact]
    public async Task Unicode_Near_Eof_Inside_Crlf_NonSelectable_No_Throw()
    {
        // 80 'я' (2 UTF-8 bytes each = 160 bytes) + CRLF + X. A diagnostic whose
        // boundary lands between CR and LF (byte 160..161) is NonSelectable. The
        // OLD excerpt code treated the byte offset as a UTF-16 index (160 > 83
        // chars) and threw ArgumentOutOfRangeException; the line-based excerpt
        // returns a bounded display without throwing or modifying source.
        var content = Encoding.UTF8.GetBytes(new string('я', 80) + "\r\nX");
        var (report, _, nav, coord2, coord) = await SetupCanonicalAsync(content, false, 160, 161);
        var result = await nav.NavigateAsync(report, Assert.Single(report.Result.Diagnostics), CancellationToken.None);
        var ns = Assert.IsType<NavigationResult.NonSelectable>(result);
        Assert.Contains("CR", ns.Reason);
        Assert.Contains("main.tex", ns.Display);
        // Bounded line excerpt of the first line (the 80 'я'); never empty, never throws.
        Assert.True(ns.Display.Length > 0);
        // Navigation never modifies source bytes: the captured snapshot bytes
        // equal the original content exactly.
        var captured = Assert.Single(report.Snapshot.Files);
        Assert.Equal(content.Length, captured.ByteLength);
        Assert.Equal(content, captured.Bytes.ToArray());
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Emoji_Near_Eof_Inside_Crlf_NonSelectable_No_Throw()
    {
        // 30 emoji (4 UTF-8 bytes, 2 UTF-16 units each = 120 bytes / 60 chars) +
        // CRLF + X. The byte offset of the CR (120) exceeds the char count (60)
        // by more than the excerpt window (24), the exact old throw shape: the
        // old code computed a=96 (>60) and Substring(96, -36) threw.
        var emoji = string.Concat(System.Linq.Enumerable.Repeat("\uD83D\uDE00", 30));
        var content = Encoding.UTF8.GetBytes(emoji + "\r\nX");
        var (report, _, nav, coord2, coord) = await SetupCanonicalAsync(content, false, 120, 121);
        var result = await nav.NavigateAsync(report, Assert.Single(report.Result.Diagnostics), CancellationToken.None);
        var ns = Assert.IsType<NavigationResult.NonSelectable>(result);
        Assert.Contains("CR", ns.Reason);
        Assert.True(ns.Display.Length > 0);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Bom_Span_Near_Eof_Unicode_NonSelectable_No_Throw()
    {
        // BOM + 80 'я' + CRLF + X. A span covering the BOM is NonSelectable; the
        // line-based excerpt of line 1 (the 80 'я') is bounded and never throws.
        var content = Bom(Encoding.UTF8.GetBytes(new string('я', 80) + "\r\nX"));
        var (report, _, nav, coord2, coord) = await SetupCanonicalAsync(content, true, 0, 3);
        var result = await nav.NavigateAsync(report, Assert.Single(report.Result.Diagnostics), CancellationToken.None);
        var ns = Assert.IsType<NavigationResult.NonSelectable>(result);
        Assert.Contains("BOM", ns.Reason);
        Assert.True(ns.Display.Length > 0);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- cancellation: reject even if a fake epoch query ignores the token ----

    [Fact]
    public async Task Already_Cancelled_Token_Rejected_Even_If_Epoch_Ignores_Ct()
    {
        var (report, diag, nav, coord2, coord) = await SetupCanonicalAsync(B("a{b"), false, 1, 2);
        // An analyzer that IGNORES ct for the epoch query.
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await nav.NavigateAsync(report, diag, cts.Token);
        var rej = Assert.IsType<NavigationResult.Rejected>(result);
        Assert.Contains("cancel", rej.Reason, StringComparison.OrdinalIgnoreCase);
        cts.Dispose();
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Now_Cancelled_During_Epoch_Rejected_Even_If_Epoch_Ignores_Ct()
    {
        var (repo, coord) = NewCoord();
        var content = B("a{b");
        var (fid, _, _) = OpenDurable(coord, repo, "main.tex", content, hasBom: false);
        var analyzer = new FakeAnalyzer { AnalyzeHandler = snap =>
            new AnalysisJobResult.Completed("rid", 1, BuildResult(snap, fid, 1, 2)), IgnoreCt = true };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached(); debounce.MatureLatest(); await r.Task; await AwaitTerminalAsync(coord2);
        var report = Assert.IsType<AnalysisState.Completed>(coord2.State).Report;
        var diag = Assert.Single(report.Result.Diagnostics);

        analyzer.EpochGate = NewTcs<bool>();
        analyzer.EpochResult = true;
        analyzer.NewEpochReached();
        var nav = new SourceNavigator(coord2, coord, analyzer);
        var cts = new CancellationTokenSource();
        var navTask = nav.NavigateAsync(report, diag, cts.Token);
        await analyzer.EpochReachedTask;           // epoch check reached, held (ignoring ct)
        cts.Cancel();                              // cancel the navigation intent
        analyzer.EpochGate.TrySetResult(true);     // release; analyzer ignores ct and returns true
        var result = await navTask;
        var rej = Assert.IsType<NavigationResult.Rejected>(result);
        Assert.Contains("cancel", rej.Reason, StringComparison.OrdinalIgnoreCase);
        cts.Dispose();
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- profile change makes a Completed report stale ----

    [Fact]
    public async Task Profile_Change_Rejects_Stale_Report()
    {
        var (report, diag, nav, coord2, coord) = await SetupCanonicalAsync(B("a{b"), false, 1, 2);
        // Changing the selected profile invalidates the identity match
        // (CurrentProfile != snap.Profile) even before the new analysis finishes.
        coord2.SetProfile(new AnalysisProfile("alt-profile-id", "v2"));
        var result = await nav.NavigateAsync(report, diag, CancellationToken.None);
        Assert.IsType<NavigationResult.Rejected>(result);
        Assert.False(nav.IsStillCurrent(report));
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- minimal fakes ----

    private sealed class NavRepo : IProjectRepository
    {
        public ProjectSnapshot? Current;
        public readonly Dictionary<string, byte[]> Content = new();
        public byte[]? LastReadContent;
        public void SetContent(SHA256 h, byte[] b) => Content[h.Hex] = b;
        public Task<ProjectSnapshot> OpenAsync(ProjectId id, CancellationToken ct)
            => Task.FromResult(Current ?? throw new InvalidOperationException("no snapshot"));
        public Task<byte[]> ReadContentAsync(SHA256 h, CancellationToken ct)
        { LastReadContent = Content.TryGetValue(h.Hex, out var b) ? b : Array.Empty<byte>(); return Task.FromResult(LastReadContent); }
        public Task<RevisionManifest> ReadRevisionAsync(ProjectId p, RevisionId r, CancellationToken ct)
            => Task.FromResult(RevisionManifest.Ok(Current!.Revision!));
        public Task<CommitResult> CommitAsync(ProjectCommit b, RevisionId? eh, CancellationToken ct)
            => Task.FromResult(CommitResult.Saved(b.SelectedHead.Id));
        public Task<HistoryPage> ReadHistoryAsync(ProjectId id, HistoryCursor? cursor, CancellationToken ct)
            => Task.FromResult(new HistoryPage(Array.Empty<HistoryCheckpoint>()));
        public Task<Project> CreateProjectAsync(string n, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteProjectAsync(ProjectId id, CancellationToken ct) => throw new NotImplementedException();
        public Task<CommitResult> RepairHeadAsync(ProjectId id, RevisionId e, RevisionId n, CancellationToken ct)
            => Task.FromResult(CommitResult.Saved(n));
        public Task<CommitResult> CreateRestoreRevisionAsync(ProjectId p, RevisionId eh, ProjectRevision r, RevisionId src, string reason, CancellationToken ct)
        { Current = new ProjectSnapshot(new Project(p, "p", r.Id), r, new Dictionary<FileId, string>(), new Dictionary<FileId, byte[]>(), new Dictionary<FileId, bool>(), false); return Task.FromResult(CommitResult.Saved(r.Id)); }
        public Task<ImportPublishResult> PublishImportAsync(StagedProject s, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class NavLocks : IWorkspaceLocks
    {
        public bool ReadOnly;
        public Task<WorkspaceCapabilities> GetCapabilitiesAsync(CancellationToken ct)
            => Task.FromResult(new WorkspaceCapabilities(true, true, true, true));
        public Task<string?> TryAcquireAsync(string name, CancellationToken ct)
            => Task.FromResult<string?>(ReadOnly ? null : "lock");
        public Task<string> WaitForAsync(string name, CancellationToken ct)
            => Task.FromResult(ReadOnly ? "" : "lock");
        public Task<bool> ReleaseAsync(string lockId, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> IsHeldAsync(string name, CancellationToken ct) => Task.FromResult(ReadOnly);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
