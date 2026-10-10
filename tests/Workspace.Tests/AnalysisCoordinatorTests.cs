using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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
/// Deterministic AnalysisCoordinator race/lifecycle tests (M2a-2b-2) against the
/// REAL WorkspaceCoordinator + REAL AnalysisSnapshotFactory with a fake
/// IProjectRepository/IWorkspaceLocks and a fake ISourceAnalyzer. Uses
/// TaskCompletionSource reached/release barriers (RunContinuationsAsynchronously)
/// and a manually-controlled debounce seam — NO Task.Delay sleeps or fixed
/// Task.Yield loops. Only bounded WaitAsync timeouts as test hang guards. Test
/// response builders use the serializer, not raw string concatenation. Each
/// regression is shown capable of detecting the bug. No diagnostics UI/DI/offline
/// analysis is wired here (M2a not complete). Covers: healthy analysis from
/// unsaved edited bytes; zero-eligible/binary excluded metadata; typed capture
/// failures with analyzerCallCount 0; failed-save then successful analysis with
/// no implicit Save; immediate invalidation on every EditText/Rename/Delete/
/// Create/CreateFromBytes/ReplaceFileBytes BEFORE debounce; open/close/switch/
/// takeover/restore(without generation bump)/recovery stale-rejection; burst
/// coalescing with a non-cooperative analyzer; profile-change-during-capture/
/// analyze; late wrong-snapshot/profile and worker-epoch-mismatch rejection;
/// critical TOCTOU around IsCurrentWorkerEpochAsync; capture-await race; cancel
/// while debouncing/capturing/analyzing; dispose while each await pending;
/// synchronous completion and reentrant Changed handlers; typed failure
/// differentiation. Existing 304 C# tests are untouched.
/// </summary>
public class AnalysisCoordinatorTests
{
    private static readonly ProjectId Pid = ProjectId.New();

    // ---- 1. basic healthy analysis from unsaved edited bytes ----

    [Fact]
    public async Task Healthy_Analysis_From_Unsaved_Edited_Bytes_Publishes_Completed_No_Save()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);

        // Edit the file (unsaved) and re-analyze: the snapshot must capture the
        // dirty editor bytes, not durable.
        var fid = coord.Files[0].Id;
        Assert.True(coord.EditText(fid, "edited body"));
        var r = analyzer.NewReached();           // expect a new analyze call
        coord2.RequestReanalyze();
        await r.Task;
        await AwaitTerminalAsync(coord2);

        var completed = Assert.IsType<AnalysisState.Completed>(coord2.State);
        Assert.Equal(B("edited body"), completed.Report.Snapshot.Files[0].Bytes.ToArray());
        Assert.Equal(coord.SessionId, completed.Report.Snapshot.SessionId);
        Assert.Equal(coord.WorkingGeneration, completed.Report.Snapshot.WorkingGeneration);
        Assert.Contains("pdfFacts", completed.Report.Result.UnavailableCapabilities);
        Assert.Contains("pdfChecks", completed.Report.Result.UnavailableCapabilities);
        // No commit/flush triggered by analysis; save state unchanged (dirty).
        Assert.Equal(0, repo.CommitCalls);
        Assert.Equal(SavePhase.Dirty, coord.SavePhase);
        Assert.True(coord.IsDirty);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- 2. zero eligible + binary excluded metadata retained; no binary read ----

    [Fact]
    public async Task Zero_Eligible_And_Binary_Excluded_Metadata_Retained_No_Binary_Read()
    {
        var (repo, coord) = NewCoord();
        var bin = FileId.New();
        var binBytes = new byte[] { 0xFF, 0xFE };
        var binHash = Sha(binBytes);
        repo.SetContent(binHash, binBytes);
        OpenWithFiles(coord, repo, BinaryFile(bin, "asset.bin", binHash, binBytes.LongLength));
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached();
        debounce.MatureLatest();
        await r.Task;
        await AwaitTerminalAsync(coord2);

        var completed = Assert.IsType<AnalysisState.Completed>(coord2.State);
        var excl = Assert.Single(completed.Report.Snapshot.Excluded);
        Assert.Equal(bin, excl.FileId);
        Assert.Equal("binary", excl.MediaKind);
        Assert.DoesNotContain(binHash, repo.ReadHashes);
        Assert.Equal(0, repo.CommitCalls);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Quota_Capture_Failure_Does_Not_Dispatch_Analyzer()
    {
        var (repo, coord) = NewCoord();
        var fid = FileId.New();
        // Declared durable length OVER the default 8 MiB per-file cap, with a
        // bogus hash (no content seeded). The REAL factory preflight rejects
        // before any read, so the coordinator's state is QuotaExceeded with
        // analyzerCallCount 0 and readCalls 0 (no unbounded allocation).
        var overMax = AnalysisLimits.DMaxBytesPerFile + 1;
        var bogusHash = Sha(new byte[] { 1, 2, 3 }); // declared but never read
        OpenWithFiles(coord, repo, TextFile(fid, "main.tex", bogusHash, overMax, false));

        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached();
        debounce.MatureLatest();
        // The capture fails (QuotaExceeded) before dispatch; no analyzer call.
        // Wait deterministically for the terminal QuotaExceeded via the event.
        await AwaitTerminalAsync(coord2);
        Assert.IsType<AnalysisState.QuotaExceeded>(coord2.State);
        Assert.Equal(0, analyzer.AnalyzeCalls);
        Assert.Equal(0, repo.ReadCalls); // rejected before reading
        Assert.Equal(0, repo.CommitCalls);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Invalid_Utf16_Capture_Failure_Does_Not_Dispatch_Analyzer()
    {
        var (repo, coord) = NewCoord();
        var fid = FileId.New();
        var bytes = B("hello");
        var hash = Sha(bytes);
        repo.SetContent(hash, bytes);
        OpenWithFiles(coord, repo, TextFile(fid, "main.tex", hash, bytes.LongLength, false));
        Assert.True(coord.EditText(fid, "x\ud800y")); // lone surrogate

        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        debounce.MatureLatest();
        await AwaitTerminalAsync(coord2);
        var inv = Assert.IsType<AnalysisState.InvalidInput>(coord2.State);
        Assert.Contains("malformed UTF-16", inv.SafeMessage);
        Assert.Equal(0, analyzer.AnalyzeCalls);
        Assert.Equal(0, repo.CommitCalls);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Unavailable_Storage_Capture_Failure_Does_Not_Dispatch_Analyzer()
    {
        var (repo, coord) = NewCoord();
        var fid = FileId.New();
        var bytes = B("hello");
        var hash = Sha(bytes);
        repo.SetContent(hash, bytes);
        OpenWithFiles(coord, repo, TextFile(fid, "main.tex", hash, bytes.LongLength, false));
        repo.ThrowOnRead = new FileNotFoundException("/opaque/xyz");

        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        debounce.MatureLatest();
        await AwaitTerminalAsync(coord2);
        var u = Assert.IsType<AnalysisState.Unavailable>(coord2.State);
        Assert.DoesNotContain("FileNotFoundException", u.SafeMessage);
        Assert.DoesNotContain("opaque", u.SafeMessage);
        Assert.Equal(0, analyzer.AnalyzeCalls);
        Assert.Equal(0, repo.CommitCalls);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- 3. failed local save then successful analysis (no implicit Save) ----

    [Fact]
    public async Task Failed_Save_Then_Successful_Analysis_No_Implicit_Save()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var fid = coord.Files[0].Id;
        Assert.True(coord.EditText(fid, "dirty body"));
        var headBefore = coord.Project!.HeadRevisionId;

        repo.CommitResult = CommitResult.Failure("DiskFull", "disk full");
        var save = await coord.SaveAsync(CancellationToken.None);
        Assert.Equal(CommitStatus.StorageFailure, save.Status);
        Assert.Equal(SavePhase.Failed, coord.SavePhase);
        Assert.Equal(headBefore, coord.Project.HeadRevisionId);
        Assert.True(coord.IsDirty);

        // Run analysis; it must succeed on the dirty snapshot and NOT call Save.
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached();
        debounce.MatureLatest();
        await r.Task;
        await AwaitTerminalAsync(coord2);

        var completed = Assert.IsType<AnalysisState.Completed>(coord2.State);
        Assert.Equal(B("dirty body"), completed.Report.Snapshot.Files[0].Bytes.ToArray());
        Assert.Equal(SavePhase.Failed, coord.SavePhase);
        Assert.Equal(headBefore, coord.Project.HeadRevisionId);
        Assert.True(coord.IsDirty);
        // Analysis added no commit attempt beyond the one failed save above.
        Assert.Equal(1, repo.CommitCalls);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- 4. immediate invalidation on every mutation BEFORE debounce ----

    [Theory]
    [InlineData("edit")]
    [InlineData("rename")]
    [InlineData("delete")]
    [InlineData("create")]
    [InlineData("createFromBytes")]
    [InlineData("replaceBytes")]
    public async Task Each_Mutation_Invalidates_Report_Immediately_Before_Debounce(string kind)
    {
        var (repo, coord) = NewCoord();
        var bytes = B("AAA");
        var hash = Sha(bytes);
        var fid = FileId.New();
        var bin = FileId.New();
        var binBytes = new byte[] { 9, 9 };
        var binHash = Sha(binBytes);
        repo.SetContent(hash, bytes);
        repo.SetContent(binHash, binBytes);
        OpenWithFiles(coord, repo,
            TextFile(fid, "main.tex", hash, bytes.LongLength, false),
            BinaryFile(bin, "asset.bin", binHash, binBytes.LongLength));

        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r0 = analyzer.NewReached();
        debounce.MatureLatest();
        await r0.Task;
        await AwaitTerminalAsync(coord2);
        Assert.IsType<AnalysisState.Completed>(coord2.State);

        switch (kind)
        {
            case "edit": Assert.True(coord.EditText(fid, "changed")); break;
            case "rename": Assert.True(coord.RenameFile(fid, "renamed.tex")); break;
            case "delete": Assert.True(coord.DeleteFile(fid)); break;
            case "create": Assert.True(coord.CreateFile("new.tex", "n")); break;
            case "createFromBytes": Assert.True(coord.CreateFileFromBytes("imp.tex", B("import"), isUserText: false)); break;
            case "replaceBytes": Assert.True(coord.ReplaceFileBytes(bin, new byte[] { 1, 2, 3 })); break;
        }

        // Immediately (synchronously) after the mutation: no usable Completed,
        // and the debounce has NOT matured.
        Assert.False(coord2.State is AnalysisState.Completed, $"stale report still visible after {kind}");
        Assert.True(coord2.State is AnalysisState.Pending or AnalysisState.NoProject,
            $"state after {kind} was {coord2.State.GetType().Name}");
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Close_Shows_NoProject_Immediately_And_Cancels_Queued()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached();
        debounce.MatureLatest();
        await r.Task;
        await AwaitTerminalAsync(coord2);
        Assert.IsType<AnalysisState.Completed>(coord2.State);

        await coord.CloseAsync(CancellationToken.None);
        Assert.IsType<AnalysisState.NoProject>(coord2.State);
        Assert.Null(coord.Project);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Open_Switch_Takeover_Schedule_Fresh_Analysis()
    {
        var (repo, coord) = NewCoord();
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho };
        var debounce = new DebounceController();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached();
        debounce.MatureLatest();
        await r.Task;
        await AwaitTerminalAsync(coord2);
        Assert.IsType<AnalysisState.Completed>(coord2.State);

        // Switch to a different project (close reset + load).
        var pid2 = ProjectId.New();
        OpenDurableAt(coord, repo, pid2, "other.tex", "BBB");
        r = analyzer.NewReached();
        debounce.MatureLatest();
        await r.Task;
        await AwaitTerminalAsync(coord2);
        var completed = Assert.IsType<AnalysisState.Completed>(coord2.State);
        Assert.Equal(B("BBB"), completed.Report.Snapshot.Files[0].Bytes.ToArray());
        Assert.Equal(pid2, completed.Report.Snapshot.ProjectId);

        // Takeover resets session + loads (fresh analysis).
        r = analyzer.NewReached();
        Assert.True(await coord.TakeOverAsync(CancellationToken.None));
        debounce.MatureLatest();
        await r.Task;
        await AwaitTerminalAsync(coord2);
        Assert.IsType<AnalysisState.Completed>(coord2.State);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Restore_Without_Generation_Bump_Invalidates_And_Reanalyzes()
    {
        var (repo, coord) = NewCoord();
        var bytesA = B("AAA");
        var hashA = Sha(bytesA);
        var bytesR = B("RESTORED");
        var hashR = Sha(bytesR);
        repo.SetContent(hashA, bytesA);
        repo.SetContent(hashR, bytesR);
        var fid = FileId.New();
        OpenWithFiles(coord, repo, TextFile(fid, "main.tex", hashA, bytesA.LongLength, false));

        var hist = new ProjectRevision(RevisionId.New(), coord.Project!.Id, null,
            ImmutableArray.Create(new ProjectFile(fid, "main.tex", "text", hashR, bytesR.LongLength, false)),
            DateTimeOffset.UtcNow);
        repo.SeedRevision(hist);
        var preview = await coord.CaptureRestorePreviewAsync(hist.Id, CancellationToken.None);

        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached();
        debounce.MatureLatest();
        await r.Task;
        await AwaitTerminalAsync(coord2);
        var firstGen = coord.WorkingGeneration;
        var firstEpoch = coord2.CurrentWorkspaceEpoch;
        Assert.IsType<AnalysisState.Completed>(coord2.State);

        // Accept the restore: replaces bytes, raises WorkspaceChanged (epoch++),
        // does NOT bump WorkingGeneration. The visible Completed must be
        // invalidated IMMEDIATELY (before any debounce), so no stale report.
        var result = await coord.AcceptRestoreAsync(preview, CancellationToken.None);
        Assert.Equal(CommitStatus.Saved, result.Status);
        Assert.Equal(firstGen, coord.WorkingGeneration); // no generation bump
        Assert.True(coord2.CurrentWorkspaceEpoch > firstEpoch); // epoch moved
        Assert.False(coord2.State is AnalysisState.Completed); // no stale report

        // A fresh analysis of the restored state shows RESTORED.
        var r2 = analyzer.NewReached();
        debounce.MatureLatest();
        await r2.Task;
        await AwaitTerminalAsync(coord2);
        var completed = Assert.IsType<AnalysisState.Completed>(coord2.State);
        Assert.Equal(B("RESTORED"), completed.Report.Snapshot.Files[0].Bytes.ToArray());
        Assert.Equal(0, repo.CommitCalls); // restore uses CreateRestoreRevisionAsync
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- 5. burst coalescing with a non-cooperative analyzer ----

    [Fact]
    public async Task Burst_Coalesces_At_Most_One_Active_NonCooperative_Latest_Wins()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var fid = coord.Files[0].Id;
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho, Cooperative = false };
        analyzer.AnalyzeGate = NewTcs<AnalysisJobResult>(); // hold the first call
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r0 = analyzer.NewReached();
        debounce.MatureLatest();
        await r0.Task; // first call reached, now held (non-cooperative)

        // Many edits while the old job is held; mature each debounce.
        for (int i = 0; i < 5; i++)
        {
            Assert.True(coord.EditText(fid, "v" + i));
            debounce.MatureLatest();
        }
        // The old job is still held (non-cooperative ignores cancel); queued ready
        // waits. Exactly one analyzer call so far.
        Assert.Equal(1, analyzer.AnalyzeCalls);

        // Release the old job echoing the OLD snapshot -> discarded.
        var staleSnapshot = analyzer.LastSnapshot!;
        var rNext = analyzer.NewReached(); // expect the queued latest to dispatch
        analyzer.AnalyzeGate!.TrySetResult(CompletedEcho(staleSnapshot, CancellationToken.None));
        analyzer.AnalyzeGate = null;
        await rNext.Task;
        await AwaitTerminalAsync(coord2);

        Assert.Equal(2, analyzer.AnalyzeCalls); // exactly one new (latest) call
        var completed = Assert.IsType<AnalysisState.Completed>(coord2.State);
        Assert.Equal(B("v4"), completed.Report.Snapshot.Files[0].Bytes.ToArray());
        Assert.NotEqual(staleSnapshot.Id, completed.Report.Snapshot.Id);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Failed_Old_Response_Cannot_Repaint_After_Supersession()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var fid = coord.Files[0].Id;
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho, Cooperative = false };
        analyzer.AnalyzeGate = NewTcs<AnalysisJobResult>(); // hold the first call
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r0 = analyzer.NewReached();
        debounce.MatureLatest();
        await r0.Task; // held (non-cooperative)

        Assert.True(coord.EditText(fid, "newer"));
        debounce.MatureLatest();
        var rNext = analyzer.NewReached();
        analyzer.AnalyzeGate!.TrySetResult(new AnalysisJobResult.Failed("old",
            0, new AnalysisErrorDto("WorkerCrashed", "old crashed", false)));
        analyzer.AnalyzeGate = null;
        await rNext.Task;
        await AwaitTerminalAsync(coord2);

        // The stale Failed must NOT repaint; the latest intent's Completed shows.
        var completed = Assert.IsType<AnalysisState.Completed>(coord2.State);
        Assert.Equal(B("newer"), completed.Report.Snapshot.Files[0].Bytes.ToArray());
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- 6. profile change during capture/analyze; late wrong-snapshot & epoch mismatch ----

    [Fact]
    public async Task Profile_Change_During_Analyze_Only_Latest_Profile_Publishes()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho, Cooperative = false };
        analyzer.AnalyzeGate = NewTcs<AnalysisJobResult>(); // hold the first call
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r0 = analyzer.NewReached();
        debounce.MatureLatest();
        await r0.Task; // held with engine-structural

        var prof = AnalysisSelection.Knu(AnalysisWorkType.Unspecified);
        var rNext = analyzer.NewReached();
        coord2.SetSelection(prof); // invalidate + immediate
        // Release the old job echoing the OLD profile -> discarded.
        analyzer.AnalyzeGate!.TrySetResult(CompletedEcho(analyzer.LastSnapshot!, CancellationToken.None));
        analyzer.AnalyzeGate = null;
        await rNext.Task;
        await AwaitTerminalAsync(coord2);

        var completed = Assert.IsType<AnalysisState.Completed>(coord2.State);
        Assert.Equal(prof, completed.Report.Snapshot.Selection);
        Assert.Equal(prof.Profile.Id, completed.Report.Result.Profile.Id);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Late_Response_With_Wrong_SnapshotId_Never_Accepted()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var foreign = AnalysisSnapshotId.New();
        var analyzer = new FakeAnalyzer
        {
            AnalyzeHandler = (snap, ct) => new AnalysisJobResult.Completed("rid", 0,
                BuildResult(foreign, snap.Profile, snap.Files[0].FileId))
        };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached();
        debounce.MatureLatest();
        await r.Task;
        await AwaitTerminalAsync(coord2);
        // Forged snapshotId mismatch yields Superseded (no usable report),
        // never a Completed with a foreign report.
        Assert.False(coord2.State is AnalysisState.Completed);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Worker_Epoch_Mismatch_Is_Superseded_Not_Completed()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho, EpochResult = false };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached();
        debounce.MatureLatest();
        await r.Task;
        await AwaitTerminalAsync(coord2);
        Assert.IsType<AnalysisState.Superseded>(coord2.State);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- 7. critical TOCTOU around IsCurrentWorkerEpochAsync ----

    [Theory]
    [InlineData("edit")]
    [InlineData("close")]
    [InlineData("profile")]
    public async Task Mutation_During_Epoch_Await_Prevents_Publication(string kind)
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var fid = coord.Files[0].Id;
        var analyzer = new FakeAnalyzer
        {
            AnalyzeHandler = CompletedEcho,
            EpochResult = true,
            EpochIgnoreCt = true, // prove the post-await RECHECK blocks it
        };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached();
        analyzer.NewEpochReached(); // arm BEFORE maturing so the epoch
        analyzer.EpochGate = NewTcs<bool>(); // check signals reached
        debounce.MatureLatest();
        await r.Task; // analyze completed synchronously
        await analyzer.EpochReachedTask; // now blocked in the epoch check

        var settle = analyzer.AwaitEpochSettledAsync(); // arm BEFORE mutation
        switch (kind)
        {
            case "edit": Assert.True(coord.EditText(fid, "during-epoch")); break;
            case "close": await coord.CloseAsync(CancellationToken.None); break;
            case "profile": coord2.SetProfile(new AnalysisProfile("p2", "1")); break;
        }
        analyzer.EpochGate!.TrySetResult(true); // release with true AFTER mutation
        await settle; // epoch check returns -> post-await recheck discards
        Assert.False(coord2.State is AnalysisState.Completed,
            $"old report published after {kind} during epoch await");
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Mutation_During_Cooperative_Epoch_Await_Cancels_Promptly()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var fid = coord.Files[0].Id;
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho, EpochResult = true };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached();
        analyzer.NewEpochReached();
        analyzer.EpochGate = NewTcs<bool>();
        debounce.MatureLatest();
        await r.Task;
        await analyzer.EpochReachedTask;

        var settle = analyzer.AwaitEpochSettledAsync(); // arm BEFORE mutation
        Assert.True(coord.EditText(fid, "during-epoch"));
        analyzer.EpochGate!.TrySetResult(true); // no-op (await already threw OCE)
        await settle; // cooperative epoch check returns (OCE) -> no publish
        Assert.False(coord2.State is AnalysisState.Completed);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- 8. capture-await race ----

    [Fact]
    public async Task Capture_Await_Race_Old_Capture_Never_Dispatched_Latest_Works()
    {
        var (repo, coord) = NewCoord();
        var (fid, hashA, bytesA) = OpenDurableAt(coord, repo, Pid, "main.tex", "AAA");
        var gate = repo.GateRead(hashA); // block the durable read
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        debounce.MatureLatest();
        await gate.Reached.Task; // capture's read reached the barrier

        // Close the project while the read is blocked.
        await coord.CloseAsync(CancellationToken.None);
        Assert.Null(coord.Project);
        gate.Release(bytesA); // release the orphaned read

        // The old capture is never dispatched. Reopen and analyze: latest works.
        OpenDurableAt(coord, repo, Pid, "main.tex", "BBB");
        var r = analyzer.NewReached();
        debounce.MatureLatest();
        await r.Task;
        await AwaitTerminalAsync(coord2);
        var completed = Assert.IsType<AnalysisState.Completed>(coord2.State);
        Assert.Equal(B("BBB"), completed.Report.Snapshot.Files[0].Bytes.ToArray());
        Assert.Equal(0, repo.CommitCalls);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- 9. cancel & dispose while pending; no implicit restart / late repaint ----

    [Fact]
    public async Task Cancel_While_Debouncing_No_Implicit_Restart()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        Assert.True(coord2.State is AnalysisState.Pending); // debounce pending

        coord2.Cancel();
        Assert.IsType<AnalysisState.Cancelled>(coord2.State);
        debounce.MatureLatest(); // cancelled debounce must NOT restart
        await AwaitTerminalAsync(coord2);
        Assert.IsType<AnalysisState.Cancelled>(coord2.State);
        Assert.Equal(0, analyzer.AnalyzeCalls);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Cancel_While_Capturing_No_Implicit_Restart()
    {
        var (repo, coord) = NewCoord();
        var (fid, hashA, bytesA) = OpenDurableAt(coord, repo, Pid, "main.tex", "AAA");
        var gate = repo.GateRead(hashA); // block capture's read
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        debounce.MatureLatest();
        await gate.Reached.Task; // capture blocked

        coord2.Cancel();
        Assert.IsType<AnalysisState.Cancelled>(coord2.State);
        gate.Release(bytesA); // release the orphaned read
        await AwaitTerminalAsync(coord2);
        // The cancelled capture settles; no analyzer dispatch, no restart.
        Assert.IsType<AnalysisState.Cancelled>(coord2.State);
        Assert.Equal(0, analyzer.AnalyzeCalls);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Cancel_While_Analyzing_No_Late_Repaint()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho, Cooperative = false };
        analyzer.AnalyzeGate = NewTcs<AnalysisJobResult>(); // hold the call
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached();
        debounce.MatureLatest();
        await r.Task; // held (non-cooperative)

        coord2.Cancel();
        Assert.IsType<AnalysisState.Cancelled>(coord2.State);
        analyzer.AnalyzeGate!.TrySetResult(CompletedEcho(analyzer.LastSnapshot!, CancellationToken.None));
        await AwaitTerminalAsync(coord2);
        Assert.IsType<AnalysisState.Cancelled>(coord2.State); // late Completed cannot replace
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Explicit_Request_After_Cancel_Works_Once_Old_Settles()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho, Cooperative = false };
        analyzer.AnalyzeGate = NewTcs<AnalysisJobResult>(); // hold the first call
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached();
        debounce.MatureLatest();
        await r.Task; // held (non-cooperative)

        coord2.Cancel();
        Assert.IsType<AnalysisState.Cancelled>(coord2.State);

        // A new explicit request after Cancel: the old non-cooperative job is
        // still held, so the new request queues and waits.
        var rNext = analyzer.NewReached();
        coord2.RequestReanalyze();
        Assert.True(coord2.State is AnalysisState.Pending or AnalysisState.Cancelled);

        // Release the old job; the queued new request dispatches and publishes.
        analyzer.AnalyzeGate!.TrySetResult(CompletedEcho(analyzer.LastSnapshot!, CancellationToken.None));
        analyzer.AnalyzeGate = null;
        await rNext.Task;
        await AwaitTerminalAsync(coord2);
        var completed = Assert.IsType<AnalysisState.Completed>(coord2.State);
        Assert.Equal(B("AAA"), completed.Report.Snapshot.Files[0].Bytes.ToArray());
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Theory]
    [InlineData("capturing")]
    [InlineData("analyzing")]
    [InlineData("epoch")]
    public async Task Dispose_While_Await_Pending_Settles_Promptly_No_Late_Work(string phase)
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho, Cooperative = false };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);

        if (phase == "capturing")
        {
            var gate = repo.GateRead(Sha(B("AAA")));
            debounce.MatureLatest();
            await gate.Reached.Task; // capture blocked
        }
        else if (phase == "analyzing")
        {
            analyzer.AnalyzeGate = NewTcs<AnalysisJobResult>(); // hold the analyzer
            var r = analyzer.NewReached();
            debounce.MatureLatest();
            await r.Task; // reached the analyzer, now held (non-cooperative)
        }
        else // epoch
        {
            var r = analyzer.NewReached();
            analyzer.NewEpochReached(); // arm BEFORE maturing so the
            analyzer.EpochGate = NewTcs<bool>(); // epoch check signals reached
            debounce.MatureLatest();
            await r.Task; // analyze completed synchronously
            await analyzer.EpochReachedTask; // now blocked in the epoch check
        }

        var dispose = coord2.DisposeAsync();
        var done = await Task.WhenAny(dispose.AsTask(), Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(dispose.AsTask(), done); // disposal does not hang
        await dispose;
        Assert.IsType<AnalysisState.Disposed>(coord2.State);
        Assert.Equal(0, analyzer.DisposeCalls); // coordinator never disposes analyzer

        coord2.RequestReanalyze(); // method after dispose does not revive
        Assert.IsType<AnalysisState.Disposed>(coord2.State);
        await coord2.DisposeAsync(); // repeated dispose harmless
        await coord.DisposeAsync();
    }

    // ---- 10. synchronous completion & reentrant Changed handlers ----

    [Fact]
    public async Task Synchronously_Completing_Analyzer_No_Stuck_Active()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        // Immediate request with a synchronously-completing analyzer: the
        // workflow runs to completion synchronously inside TryStart.
        var r = analyzer.NewReached();
        coord2.RequestReanalyze();
        await r.Task;
        await AwaitTerminalAsync(coord2);
        Assert.IsType<AnalysisState.Completed>(coord2.State);

        // A second immediate request still works (active flag was cleared).
        var fid = coord.Files[0].Id;
        Assert.True(coord.EditText(fid, "second"));
        var r2 = analyzer.NewReached();
        coord2.RequestReanalyze();
        await r2.Task;
        await AwaitTerminalAsync(coord2);
        var completed = Assert.IsType<AnalysisState.Completed>(coord2.State);
        Assert.Equal(B("second"), completed.Report.Snapshot.Files[0].Bytes.ToArray());
        Assert.Equal(2, analyzer.AnalyzeCalls);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Reentrant_Changed_Handler_Does_Not_Deadlock_Or_Lose_Latest()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        int raiseCount = 0;
        coord2.Changed += s =>
        {
            raiseCount++;
            // Reentrant: a subscriber that requests a reanalyze from inside the
            // event. Must not deadlock (lock is not held during raise) and must
            // not loop forever (guard on raiseCount).
            if (raiseCount == 1)
                coord2.RequestReanalyze();
        };
        var r = analyzer.NewReached();
        debounce.MatureLatest();
        await r.Task;
        // The reentrant RequestReanalyze (fired from inside the first Changed)
        // scheduled a second analysis that runs synchronously after the first
        // settles; AwaitTerminal observes the final Completed.
        await AwaitTerminalAsync(coord2);
        Assert.IsType<AnalysisState.Completed>(coord2.State);
        Assert.True(raiseCount >= 2); // reentrant handler observed multiple transitions
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- 11. typed failure differentiation; no empty-success substitution ----

    [Fact]
    public async Task Analyzer_Failed_Maps_To_Failed_Not_Empty_Completed()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var analyzer = new FakeAnalyzer
        {
            AnalyzeHandler = (_, _) => new AnalysisJobResult.Failed("r",
                0, new AnalysisErrorDto("Timeout", "deadline exceeded", false))
        };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached();
        debounce.MatureLatest();
        await r.Task;
        await AwaitTerminalAsync(coord2);
        var failed = Assert.IsType<AnalysisState.Failed>(coord2.State);
        Assert.Equal("Timeout", failed.Error.Code);
        Assert.Equal("deadline exceeded", failed.Error.SafeMessage);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Invalid_Profile_Is_Typed_InvalidInput_Not_Dispatched()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        coord2.SetProfile(new AnalysisProfile("", "0")); // invalid id
        var inv = Assert.IsType<AnalysisState.InvalidInput>(coord2.State);
        Assert.Contains("profile id/version", inv.SafeMessage);
        Assert.Equal(0, analyzer.AnalyzeCalls);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Identical_Profile_Change_Does_Not_Schedule_Redundant_Work()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached();
        debounce.MatureLatest();
        await r.Task;
        await AwaitTerminalAsync(coord2);
        Assert.IsType<AnalysisState.Completed>(coord2.State);
        var callsBefore = analyzer.AnalyzeCalls;
        coord2.SetProfile(AnalysisProfile.EngineStructural()); // identical
        Assert.Equal(callsBefore, analyzer.AnalyzeCalls);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- review round: 1. close/NoProject MUST notify ----

    [Fact]
    public async Task Close_Raises_NoProject_Notification_And_Drops_Usable_Report()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached();
        debounce.MatureLatest();
        await r.Task;
        await AwaitTerminalAsync(coord2);
        Assert.IsType<AnalysisState.Completed>(coord2.State); // baseline usable report

        var recorder = new EventRecorder(coord2);
        var callsBeforeClose = analyzer.AnalyzeCalls;
        await coord.CloseAsync(CancellationToken.None);
        // Close MUST raise a NoProject notification synchronously; the recorder's
        // last event is NoProject and no usable Completed remains.
        Assert.IsType<AnalysisState.NoProject>(coord2.State);
        Assert.NotEmpty(recorder.Events);
        Assert.IsType<AnalysisState.NoProject>(recorder.Events[^1]);
        Assert.DoesNotContain(recorder.Events, e => e is AnalysisState.Completed);
        Assert.Equal(callsBeforeClose, analyzer.AnalyzeCalls); // close cancelled queued, no new dispatch

        // A completed open after the reset still schedules correctly.
        OpenDurableAt(coord, repo, Pid, "main.tex", "BBB");
        var r2 = analyzer.NewReached();
        debounce.MatureLatest();
        await r2.Task;
        await AwaitTerminalAsync(coord2);
        var completed = Assert.IsType<AnalysisState.Completed>(coord2.State);
        Assert.Equal(B("BBB"), completed.Report.Snapshot.Files[0].Bytes.ToArray());
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- review round: 2. every current terminal job reaches a terminal state ----

    [Fact]
    public async Task Adapter_Cancelled_With_Uncanceled_Token_Publishes_Cancelled()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        // The adapter returns Cancelled (e.g. a JS bridge worker reset/dispose)
        // WITHOUT this coordinator calling Cancel (token uncanceled).
        var analyzer = new FakeAnalyzer
        {
            AnalyzeHandler = (_, _) => new AnalysisJobResult.Cancelled("rid", 0)
        };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached();
        debounce.MatureLatest();
        await r.Task;
        await AwaitTerminalAsync(coord2);
        // The still-latest job must reach a terminal Cancelled, not stay Running.
        Assert.IsType<AnalysisState.Cancelled>(coord2.State);
        Assert.Equal(1, analyzer.AnalyzeCalls);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Analyzer_Throws_OCE_Publishes_Terminal_Cancelled()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var analyzer = new FakeAnalyzer
        {
            AnalyzeHandler = (_, _) => throw new OperationCanceledException()
        };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached();
        debounce.MatureLatest();
        await r.Task;
        await AwaitTerminalAsync(coord2);
        Assert.False(coord2.State is AnalysisState.Running or AnalysisState.Pending);
        Assert.IsType<AnalysisState.Cancelled>(coord2.State);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Old_Cancelled_Released_After_New_Queued_Does_Not_Overwrite()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var fid = coord.Files[0].Id;
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho, Cooperative = false };
        analyzer.AnalyzeGate = NewTcs<AnalysisJobResult>(); // hold the first call
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r0 = analyzer.NewReached();
        debounce.MatureLatest();
        await r0.Task; // held (non-cooperative)

        // A newer intent queues (edit) while the old job is held.
        Assert.True(coord.EditText(fid, "newer"));
        debounce.MatureLatest();
        // Release the old job with a Cancelled result -> must NOT overwrite the
        // newer Pending (intent mismatch discards it).
        var rNext = analyzer.NewReached();
        analyzer.AnalyzeGate!.TrySetResult(new AnalysisJobResult.Cancelled("old", 0));
        analyzer.AnalyzeGate = null;
        await rNext.Task;
        await AwaitTerminalAsync(coord2);
        var completed = Assert.IsType<AnalysisState.Completed>(coord2.State);
        Assert.Equal(B("newer"), completed.Report.Snapshot.Files[0].Bytes.ToArray());
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Explicit_Request_After_Adapter_Cancel_Works()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        bool firstCall = true;
        var analyzer = new FakeAnalyzer
        {
            AnalyzeHandler = (snap, ct) =>
            {
                if (firstCall) { firstCall = false; return new AnalysisJobResult.Cancelled("rid", 0); }
                return CompletedEcho(snap, ct); // retry: healthy completion
            }
        };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached();
        debounce.MatureLatest();
        await r.Task;
        await AwaitTerminalAsync(coord2);
        Assert.IsType<AnalysisState.Cancelled>(coord2.State);

        // A new explicit request after the adapter cancel works (no implicit
        // restart happened on the cancel).
        var r2 = analyzer.NewReached();
        coord2.RequestReanalyze();
        debounce.MatureLatest();
        await r2.Task;
        await AwaitTerminalAsync(coord2);
        var completed = Assert.IsType<AnalysisState.Completed>(coord2.State);
        Assert.Equal(B("AAA"), completed.Report.Snapshot.Files[0].Bytes.ToArray());
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- review round: 3. reentrant event + debounce ownership ----

    [Fact]
    public async Task Reentrant_Cancel_On_Pending_Delivers_Cancelled_To_Later_Subscriber()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var fid = coord.Files[0].Id;
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        // First subscriber cancels on Pending; second (recorder) is subscribed AFTER so
        // it is delivered the CURRENT state after the reentrant mutation, never the
        // obsolete Pending.
        coord2.Changed += s =>
        {
            if (s is AnalysisState.Pending)
                coord2.Cancel();
        };
        var second = new EventRecorder(coord2);
        // An edit raises Pending; the reentrant Cancel raises Cancelled. The
        // second subscriber must receive Cancelled (the CURRENT state), not the
        // obsolete Pending, and no uncancelled delay may survive the cancel.
        Assert.True(coord.EditText(fid, "edit"));
        Assert.IsType<AnalysisState.Cancelled>(coord2.State);
        Assert.NotEmpty(second.Events);
        Assert.IsType<AnalysisState.Cancelled>(second.Events[^1]);
        Assert.DoesNotContain(second.Events, e => e is AnalysisState.Pending);
        // No uncancelled debounce created afterward: a mature does not dispatch.
        debounce.MatureLatest();
        await Task.Yield();
        Assert.Equal(0, analyzer.AnalyzeCalls);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Reentrant_Edit_On_Pending_Only_Latest_Debounce_Survives()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var fid = coord.Files[0].Id;
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        int reentrantEdits = 0;
        coord2.Changed += s =>
        {
            if (s is AnalysisState.Pending && reentrantEdits == 0)
            {
                reentrantEdits++;
                coord.EditText(fid, "reentrant edit"); // a newer edit on Pending (once)
            }
        };
        // The initial edit raises Pending; the reentrant edit raises a newer
        // Pending and replaces the debounce. Only the LATEST debounce survives and
        // exactly the latest bytes dispatch.
        Assert.True(coord.EditText(fid, "first"));
        var r = analyzer.NewReached();
        debounce.MatureLatest();
        await r.Task;
        await AwaitTerminalAsync(coord2);
        var completed = Assert.IsType<AnalysisState.Completed>(coord2.State);
        Assert.Equal(B("reentrant edit"), completed.Report.Snapshot.Files[0].Bytes.ToArray());
        Assert.Equal(1, analyzer.AnalyzeCalls); // exactly one (latest) dispatch
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Reentrant_Dispose_On_Pending_Stops_All_Subsequent_Work()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var fid = coord.Files[0].Id;
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        coord2.Changed += s =>
        {
            if (s is AnalysisState.Pending)
                coord2.DisposeAsync().AsTask().Wait(); // dispose during Pending
        };
        var second = new EventRecorder(coord2);
        Assert.True(coord.EditText(fid, "edit"));
        Assert.IsType<AnalysisState.Disposed>(coord2.State);
        // No subsequent delay/worker call/event: a mature does not dispatch.
        debounce.MatureLatest();
        await Task.Yield();
        Assert.Equal(0, analyzer.AnalyzeCalls);
        Assert.DoesNotContain(second.Events, e => e is AnalysisState.Pending);
        // Methods after dispose do not revive work.
        coord2.RequestReanalyze();
        Assert.IsType<AnalysisState.Disposed>(coord2.State);
        await coord2.DisposeAsync(); // idempotent
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Faulting_Subscriber_Does_Not_Suppress_Later_Subscribers()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var fid = coord.Files[0].Id;
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        coord2.Changed += s =>
        {
            if (s is AnalysisState.Pending)
                throw new InvalidOperationException("subscriber boom");
        };
        var second = new EventRecorder(coord2);
        // The faulting first subscriber is isolated; the second subscriber still
        // receives the CURRENT state (Pending).
        Assert.True(coord.EditText(fid, "edit"));
        Assert.IsType<AnalysisState.Pending>(coord2.State);
        Assert.NotEmpty(second.Events);
        Assert.IsType<AnalysisState.Pending>(second.Events[^1]);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    // ---- review round: 4. honest failure coverage + recovery/restore/profile ----

    [Fact]
    public async Task Faulting_Delay_Does_Not_Leave_Pending_Forever()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var fid = coord.Files[0].Id;
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho };
        var delay = new FaultyDelay();
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: delay.Delay);
        // First analysis is healthy (no fault).
        var r0 = analyzer.NewReached();
        delay.Inner.MatureLatest();
        await r0.Task;
        await AwaitTerminalAsync(coord2);
        Assert.IsType<AnalysisState.Completed>(coord2.State);

        // An edit schedules a debounce whose delay seam FAULTS (non-OCE). The
        // current intent must reach an honest terminal Failed, not Pending forever.
        delay.FaultNext = true;
        Assert.True(coord.EditText(fid, "edit"));
        await AwaitTerminalAsync(coord2);
        Assert.False(coord2.State is AnalysisState.Pending or AnalysisState.Running);
        Assert.IsType<AnalysisState.Failed>(coord2.State);
        Assert.Equal(1, analyzer.AnalyzeCalls); // the faulty debounce never dispatched (count is the prior healthy call)

        // A healthy explicit retry afterward works.
        delay.FaultNext = false;
        var r1 = analyzer.NewReached();
        coord2.RequestReanalyze();
        delay.Inner.MatureLatest();
        await r1.Task;
        await AwaitTerminalAsync(coord2);
        var completed = Assert.IsType<AnalysisState.Completed>(coord2.State);
        Assert.Equal(B("edit"), completed.Report.Snapshot.Files[0].Bytes.ToArray());
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Wrong_Result_Profile_Is_Superseded_Not_Completed()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var otherProfile = new AnalysisProfile("other-profile", "7");
        var analyzer = new FakeAnalyzer
        {
            AnalyzeHandler = (snap, ct) => new AnalysisJobResult.Completed("rid", 0,
                BuildResult(snap.Id, otherProfile, snap.Files[0].FileId))
        };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached();
        debounce.MatureLatest();
        await r.Task;
        await AwaitTerminalAsync(coord2);
        // The result profile differs from the captured snapshot profile ->
        // Superseded (no usable report), never a foreign Completed.
        Assert.False(coord2.State is AnalysisState.Completed);
        Assert.IsType<AnalysisState.Superseded>(coord2.State);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Profile_Change_During_Held_Capture_Only_Latest_Profile_Publishes()
    {
        var (repo, coord) = NewCoord();
        var (fid, hashA, bytesA) = OpenDurableAt(coord, repo, Pid, "main.tex", "AAA");
        var gate = repo.GateRead(hashA); // hold the capture's read
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        debounce.MatureLatest();
        await gate.Reached.Task; // capture blocked in the read

        // Change the profile while the capture is held; then release the read.
        var newProfile = AnalysisSelection.Knu(AnalysisWorkType.Coursework);
        // Arm the reached signal BEFORE the profile change so the NEW workflow's
        // AnalyzeAsync sets it (the OLD capture is cancelled via ct by SetProfile and
        // never reaches AnalyzeAsync).
        var r = analyzer.NewReached();
        coord2.SetSelection(newProfile);
        gate.Release(bytesA); // orphan cleanup (OLD capture already OCE-settled)
        await r.Task; // NEW workflow (new profile) reaches the analyzer
        await AwaitTerminalAsync(coord2);
        var completed = Assert.IsType<AnalysisState.Completed>(coord2.State);
        Assert.Equal(newProfile, completed.Report.Snapshot.Selection);
        Assert.Equal(newProfile.Profile.Id, completed.Report.Result.Profile.Id);
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Recovery_During_Held_Epoch_No_Old_Publication_Only_Latest_Publishes()
    {
        var (repo, coord) = NewCoord();
        var bytesA = B("AAA");
        var hashA = Sha(bytesA);
        var bytesR = B("RECOVERED");
        var hashR = Sha(bytesR);
        repo.SetContent(hashA, bytesA);
        repo.SetContent(hashR, bytesR);
        var fid = FileId.New();
        OpenWithFiles(coord, repo, TextFile(fid, "main.tex", hashA, bytesA.LongLength, false));

        // A damaged-head recovery: target revision restores the file to RECOVERED.
        var target = new ProjectRevision(RevisionId.New(), coord.Project!.Id, null,
            ImmutableArray.Create(new ProjectFile(fid, "main.tex", "text", hashR, bytesR.LongLength, false)),
            DateTimeOffset.UtcNow);
        repo.SeedRevision(target);
        var damagedHead = coord.CurrentRevision!.Id;
        repo.Current = new ProjectSnapshot(
            coord.Project with { }, coord.CurrentRevision!,
            new Dictionary<FileId, string>(), new Dictionary<FileId, byte[]>(),
            new Dictionary<FileId, bool>(), IsReadOnly: false,
            Recovery: new RecoveryReport(damagedHead, new[]
            {
                new HistoryCheckpoint(CheckpointId.New(), coord.Project.Id, target.Id, "auto", DateTimeOffset.UtcNow),
            }));
        await coord.OpenAsync(Pid, CancellationToken.None); // reload with Recovery set
        Assert.NotNull(coord.Recovery);

        var analyzer = new FakeAnalyzer
        {
            AnalyzeHandler = CompletedEcho,
            EpochResult = true,
            EpochIgnoreCt = true, // prove the post-await recheck discards
        };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached();
        analyzer.NewEpochReached();
        analyzer.EpochGate = NewTcs<bool>();
        debounce.MatureLatest();
        await r.Task;
        await analyzer.EpochReachedTask; // blocked in the epoch check (AAA)

        // Recover while the epoch check is held: bumps the factory epoch and the
        // intent (WorkspaceChanged) WITHOUT bumping WorkingGeneration.
        var genBefore = coord.WorkingGeneration;
        var epochBefore = coord2.CurrentWorkspaceEpoch;
        var recover = await coord.RecoverToAsync(target.Id, CancellationToken.None);
        Assert.Equal(CommitStatus.Saved, recover.Status);
        Assert.Equal(genBefore, coord.WorkingGeneration); // no generation bump
        Assert.True(coord2.CurrentWorkspaceEpoch > epochBefore); // epoch moved

        // Release the held epoch with TRUE; the OLD (AAA) report must NOT publish.
        var settle = analyzer.AwaitEpochSettledAsync();
        analyzer.EpochGate!.TrySetResult(true);
        await settle;
        Assert.False(coord2.State is AnalysisState.Completed c && c.Report.Snapshot.Files[0].Bytes.ToArray().SequenceEqual(B("AAA")));

        // Only the LATEST (RECOVERED) snapshot can publish.
        var r2 = analyzer.NewReached();
        debounce.MatureLatest();
        await r2.Task;
        await AwaitTerminalAsync(coord2);
        var completed = Assert.IsType<AnalysisState.Completed>(coord2.State);
        Assert.Equal(B("RECOVERED"), completed.Report.Snapshot.Files[0].Bytes.ToArray());
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Restore_During_Held_Epoch_No_Old_Publication_Only_Latest_Publishes()
    {
        var (repo, coord) = NewCoord();
        var bytesA = B("AAA");
        var hashA = Sha(bytesA);
        var bytesR = B("RESTORED");
        var hashR = Sha(bytesR);
        repo.SetContent(hashA, bytesA);
        repo.SetContent(hashR, bytesR);
        var fid = FileId.New();
        OpenWithFiles(coord, repo, TextFile(fid, "main.tex", hashA, bytesA.LongLength, false));
        var hist = new ProjectRevision(RevisionId.New(), coord.Project!.Id, null,
            ImmutableArray.Create(new ProjectFile(fid, "main.tex", "text", hashR, bytesR.LongLength, false)),
            DateTimeOffset.UtcNow);
        repo.SeedRevision(hist);
        var preview = await coord.CaptureRestorePreviewAsync(hist.Id, CancellationToken.None);

        var analyzer = new FakeAnalyzer
        {
            AnalyzeHandler = CompletedEcho, EpochResult = true, EpochIgnoreCt = true,
        };
        var debounce = new DebounceController();
        var coord2 = new AnalysisCoordinator(coord, analyzer, delayAsync: debounce.Delay);
        var r = analyzer.NewReached();
        analyzer.NewEpochReached();
        analyzer.EpochGate = NewTcs<bool>();
        debounce.MatureLatest();
        await r.Task;
        await analyzer.EpochReachedTask; // blocked in the epoch check (AAA)

        var genBefore = coord.WorkingGeneration;
        var epochBefore = coord2.CurrentWorkspaceEpoch;
        var result = await coord.AcceptRestoreAsync(preview, CancellationToken.None);
        Assert.Equal(CommitStatus.Saved, result.Status);
        Assert.Equal(genBefore, coord.WorkingGeneration); // restore does not bump gen
        Assert.True(coord2.CurrentWorkspaceEpoch > epochBefore); // epoch moved

        var settle = analyzer.AwaitEpochSettledAsync();
        analyzer.EpochGate!.TrySetResult(true);
        await settle;
        Assert.False(coord2.State is AnalysisState.Completed c && c.Report.Snapshot.Files[0].Bytes.ToArray().SequenceEqual(B("AAA")));

        var r2 = analyzer.NewReached();
        debounce.MatureLatest();
        await r2.Task;
        await AwaitTerminalAsync(coord2);
        var completed = Assert.IsType<AnalysisState.Completed>(coord2.State);
        Assert.Equal(B("RESTORED"), completed.Report.Snapshot.Files[0].Bytes.ToArray());
        await coord2.DisposeAsync();
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Constructor_Rejects_Negative_Debounce()
    {
        var (repo, coord) = NewCoord();
        OpenDurable(coord, repo, "main.tex", "AAA");
        var analyzer = new FakeAnalyzer { AnalyzeHandler = CompletedEcho };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AnalysisCoordinator(coord, analyzer, debounce: TimeSpan.FromMilliseconds(-1)));
        await coord.DisposeAsync();
    }

    // ---- helpers + fakes ----

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private static SHA256 Sha(byte[] b) => Client.Workspace.Domain.SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(b));
    private static ProjectFile TextFile(FileId id, string path, SHA256 hash, long len, bool hasBom)
        => new(id, path, "text", hash, len, hasBom);
    private static ProjectFile BinaryFile(FileId id, string path, SHA256 hash, long len)
        => new(id, path, "binary", hash, len, false);

    private static (CoordRepo repo, WorkspaceCoordinator coord) NewCoord(bool readOnly = false)
    {
        var repo = new CoordRepo();
        var locks = new FakeLocks { ReadOnly = readOnly };
        var history = new HistoryService(repo);
        var coord = new WorkspaceCoordinator(repo, locks, history);
        return (repo, coord);
    }

    private static void OpenDurable(WorkspaceCoordinator coord, CoordRepo repo, string path, string content)
        => OpenDurableAt(coord, repo, Pid, path, content);

    private static (FileId, SHA256, byte[]) OpenDurableAt(WorkspaceCoordinator coord, CoordRepo repo, ProjectId pid, string path, string content)
    {
        var bytes = B(content);
        var hash = Sha(bytes);
        repo.SetContent(hash, bytes); // seed durable bytes so capture can read them
        var fid = FileId.New();
        OpenWithFilesAt(coord, repo, pid, TextFile(fid, path, hash, bytes.LongLength, false));
        return (fid, hash, bytes);
    }

    private static void OpenWithFiles(WorkspaceCoordinator coord, CoordRepo repo, params ProjectFile[] files)
        => OpenWithFilesAt(coord, repo, Pid, files);

    private static void OpenWithFilesAt(WorkspaceCoordinator coord, CoordRepo repo, ProjectId pid, params ProjectFile[] files)
    {
        repo.Current = Snapshot(pid, files);
        coord.OpenAsync(pid, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static ProjectSnapshot Snapshot(ProjectId pid, ProjectFile[] files)
    {
        var rev = new ProjectRevision(RevisionId.New(), pid, null,
            ImmutableArray.CreateRange(files), DateTimeOffset.UtcNow);
        var project = new Project(pid, "p", rev.Id);
        return new ProjectSnapshot(project, rev, new Dictionary<FileId, string>(),
            new Dictionary<FileId, byte[]>(), new Dictionary<FileId, bool>(), IsReadOnly: false);
    }

    private static AnalysisJobResult CompletedEcho(AnalysisSnapshot snap, CancellationToken ct)
        => new AnalysisJobResult.Completed("rid", 0, BuildResult(snap.Id, snap.Profile,
            snap.Files.Length > 0 ? snap.Files[0].FileId : (FileId?)null) with { Configuration = snap.Configuration });

    /// <summary>Build a valid canonical AnalysisResultDto echoing the given
    /// snapshot id and profile, with one Pass file facts entry per captured
    /// file, a single Pass ENGINE-STRUCT-001 rule result, empty coverage, and
    /// the typed PDF unavailable capabilities.</summary>
    private static AnalysisResultDto BuildResult(AnalysisSnapshotId snapshotId, AnalysisProfile profile, FileId? primaryFile)
    {
        var fileFacts = primaryFile.HasValue
            ? new[] { new AnalysisSourceFileFactsDto(
                primaryFile.Value.ToString(), ByteLength: 0, HasBom: false, NewlineKind.Lf,
                Truncated: false, BraceDepthExceeded: false, TokenCount: 0,
                EngineOutcome: RuleOutcome.Pass, Array.Empty<AnalysisTokenDto>()) }
            : Array.Empty<AnalysisSourceFileFactsDto>();
        var perFile = primaryFile.HasValue
            ? new[] { new AnalysisPerFileOutcomeDto(primaryFile.Value.ToString(), RuleOutcome.Pass, Array.Empty<string>()) }
            : Array.Empty<AnalysisPerFileOutcomeDto>();
        return new AnalysisResultDto(
            snapshotId.ToString(),
            new AnalysisProfileDto(profile.Id, profile.Version),
            new AnalysisSourceFactsDto(fileFacts),
            Array.Empty<AnalysisEngineDiagnosticDto>(),
            new[]
            {
                new AnalysisRuleResultDto(
                    "ENGINE-STRUCT-001", "engine", RuleOutcome.Pass,
                    perFile,
                    Array.Empty<string>()),
            },
            RuleOutcome.Pass,
            new AnalysisCoverageDto(
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                DiagnosticsTruncated: false, ResultTruncated: false),
            new[] { "pdfFacts", "pdfChecks" });
    }

    private static TaskCompletionSource<T> NewTcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A manually-controlled debounce seam: each call creates a fresh
    /// TCS awaited with the cancellation token. MatureLatest completes the most
    /// recently created gate (the current debounce) and clears orphans; a cancel
    /// (reset) makes the awaited WaitAsync(ct) throw OCE.</summary>
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

    /// <summary>Fake ISourceAnalyzer with controllable reached/release
    /// barriers. AnalyzeGate holds AnalyzeAsync; Cooperative toggles whether the
    /// wait honors ct (false = non-cooperative, ignores ct). EpochGate holds
    /// IsCurrentWorkerEpochAsync; EpochIgnoreCt makes it ignore ct. The
    /// coordinator must NEVER dispose this analyzer (DisposeCalls stays 0 unless
    /// the test itself disposes). A "reached" signal is a fresh TCS the test
    /// arms via NewReached()/NewEpochReached() and awaits to know a call has
    /// started — deterministic, no timing sleeps.</summary>
    private sealed class FakeAnalyzer : ISourceAnalyzer
    {
        public TaskCompletionSource<AnalysisJobResult>? AnalyzeGate;
        public Func<AnalysisSnapshot, CancellationToken, AnalysisJobResult>? AnalyzeHandler;
        public bool Cooperative = true;
        public TaskCompletionSource<bool>? EpochGate;
        public bool EpochResult = true;
        public bool EpochIgnoreCt;
        public int AnalyzeCalls, EpochCalls, DisposeCalls;
        public AnalysisSnapshot? LastSnapshot;

        private TaskCompletionSource<bool>? _analyzeReached;
        private TaskCompletionSource<bool>? _epochReached;

        public TaskCompletionSource<bool> NewReached() => _analyzeReached = NewTcs<bool>();
        public void NewEpochReached() => _epochReached = NewTcs<bool>();
        public Task EpochReachedTask => _epochReached?.Task ?? Task.CompletedTask;

        public async Task<AnalysisJobResult> AnalyzeAsync(AnalysisSnapshot snapshot, CancellationToken ct)
        {
            AnalyzeCalls++;
            LastSnapshot = snapshot;
            _analyzeReached?.TrySetResult(true);
            if (ct.IsCancellationRequested) return new AnalysisJobResult.Cancelled("c", 0);
            if (AnalyzeGate is { } gate)
            {
                try { await (Cooperative ? gate.Task.WaitAsync(ct) : gate.Task).ConfigureAwait(false); }
                catch (OperationCanceledException) { return new AnalysisJobResult.Cancelled("c", 0); }
            }
            return AnalyzeHandler is null
                ? new AnalysisJobResult.Completed("rid", 0, BuildResult(snapshot.Id, snapshot.Profile,
                    snapshot.Files.Length > 0 ? snapshot.Files[0].FileId : (FileId?)null))
                : AnalyzeHandler(snapshot, ct);
        }

        public async Task<bool> IsCurrentWorkerEpochAsync(long epoch, CancellationToken ct)
        {
            EpochCalls++;
            _epochReached?.TrySetResult(true);
            try
            {
                if (EpochGate is { } gate)
                {
                    try { await (EpochIgnoreCt ? gate.Task : gate.Task.WaitAsync(ct)).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return false; }
                }
                return EpochResult;
            }
            finally { EpochSettled?.TrySetResult(true); }
        }

        public TaskCompletionSource<bool>? EpochSettled;
        public Task AwaitEpochSettledAsync()
        {
            var tcs = EpochSettled = NewTcs<bool>();
            return tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public ValueTask DisposeAsync() { DisposeCalls++; return ValueTask.CompletedTask; }


    }

    /// <summary>Deterministically wait for the coordinator to reach a terminal
    /// state (anything other than Pending/Running) via the Changed event, with
    /// a bounded hang guard. No Task.Delay sleeps; the terminal transition is
    /// observed through the same Changed seam the UI will use.</summary>
    private static async Task AwaitTerminalAsync(AnalysisCoordinator c)
    {
        static bool IsTerminal(AnalysisState s) =>
            s is not (AnalysisState.Pending or AnalysisState.Running);
        if (IsTerminal(c.State)) return;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(AnalysisState s) { if (IsTerminal(s)) tcs.TrySetResult(true); }
        c.Changed += OnChanged;
        try
        {
            if (IsTerminal(c.State)) tcs.TrySetResult(true);
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { c.Changed -= OnChanged; }
    }

    /// <summary>Records every AnalysisState delivered to this subscriber's
    /// Changed handler, in delivery order. Used to assert reentrant-notification
    /// correctness (a later subscriber must not receive an obsolete payload
    /// after an earlier subscriber mutated state).</summary>
    private sealed class EventRecorder
    {
        private readonly List<AnalysisState> _events = new();
        private readonly Action<AnalysisState> _handler;
        public EventRecorder(AnalysisCoordinator c)
        {
            _handler = s => _events.Add(s);
            c.Changed += _handler;
        }
        public IReadOnlyList<AnalysisState> Events => _events;
    }

    /// <summary>A delay seam whose next call can be made to FAULT with a
    /// non-OCE exception (to verify the scheduler surfaces an honest terminal
    /// Failed instead of leaving the intent Pending forever). Subsequent
    /// calls delegate to an inner DebounceController for a healthy retry.</summary>
    private sealed class FaultyDelay
    {
        public bool FaultNext;
        public readonly DebounceController Inner = new();
        public Task Delay(TimeSpan d, CancellationToken ct)
        {
            if (FaultNext) { FaultNext = false; throw new InvalidOperationException("delay seam fault"); }
            return Inner.Delay(d, ct);
        }
    }


    private sealed class CoordRepo : IProjectRepository
    {
        public ProjectSnapshot? Current;
        public readonly Dictionary<string, byte[]> Content = new();
        public readonly Dictionary<RevisionId, ProjectRevision> Revisions = new();
        public readonly HashSet<SHA256> ReadHashes = new();
        public int ReadCalls;
        public int CommitCalls;
        public Exception? ThrowOnRead;
        public CommitResult? CommitResult;
        private readonly Dictionary<SHA256, ReadGate> _gates = new();

        public void SetContent(SHA256 h, byte[] b) => Content[h.Hex] = b;
        public void SeedRevision(ProjectRevision r) => Revisions[r.Id] = r;

        public ReadGate GateRead(SHA256 h)
        {
            var g = new ReadGate();
            _gates[h] = g;
            return g;
        }

        public Task<ProjectSnapshot> OpenAsync(ProjectId id, CancellationToken ct)
            => Task.FromResult(Current ?? throw new InvalidOperationException("no snapshot"));

        public async Task<byte[]> ReadContentAsync(SHA256 h, CancellationToken ct)
        {
            ReadCalls++;
            ReadHashes.Add(h);
            if (_gates.TryGetValue(h, out var g))
            {
                _gates.Remove(h);
                g.Reached.SetResult(true);
                await g.Block.Task;
            }
            if (ThrowOnRead is { } ex) throw ex;
            return Content.TryGetValue(h.Hex, out var b) ? b : Array.Empty<byte>();
        }

        public Task<RevisionManifest> ReadRevisionAsync(ProjectId p, RevisionId r, CancellationToken ct)
        {
            if (!Revisions.TryGetValue(r, out var rev) || rev.ProjectId != p)
                return Task.FromResult(RevisionManifest.Damaged(
                    new ProjectRevision(r, p, null, ImmutableArray<ProjectFile>.Empty, DateTimeOffset.UtcNow), "missing"));
            foreach (var f in rev.Files)
                if (!Content.ContainsKey(f.ContentHash.Hex))
                    return Task.FromResult(RevisionManifest.Damaged(rev, $"missing {f.RelativePath}"));
            return Task.FromResult(RevisionManifest.Ok(rev));
        }

        public Task<CommitResult> CommitAsync(ProjectCommit b, RevisionId? eh, CancellationToken ct)
        {
            CommitCalls++;
            if (CommitResult is { } fr) return Task.FromResult(fr);
            foreach (var c in b.Content) Content[c.Hash.Hex] = c.Bytes;
            return Task.FromResult(CommitResult.Saved(b.SelectedHead.Id));
        }

        public Task<CommitResult> CreateRestoreRevisionAsync(
            ProjectId p, RevisionId eh, ProjectRevision r, RevisionId src, string reason, CancellationToken ct)
        {
            Revisions[r.Id] = r;
            Current = new ProjectSnapshot(new Project(p, "p", r.Id), r,
                new Dictionary<FileId, string>(), new Dictionary<FileId, byte[]>(),
                new Dictionary<FileId, bool>(), IsReadOnly: false);
            return Task.FromResult(CommitResult.Saved(r.Id));
        }

        public Task<CommitResult> RepairHeadAsync(ProjectId p, RevisionId e, RevisionId n, CancellationToken ct)
        {
            // Recovery repair: replace the damaged head with the target revision.
            var rev = Revisions[n];
            Current = new ProjectSnapshot(new Project(p, "p", n), rev,
                new Dictionary<FileId, string>(), new Dictionary<FileId, byte[]>(),
                new Dictionary<FileId, bool>(), IsReadOnly: false);
            return Task.FromResult(CommitResult.Saved(n));
        }
        public Task<HistoryPage> ReadHistoryAsync(ProjectId id, HistoryCursor? cursor, CancellationToken ct)
            => Task.FromResult(new HistoryPage(Array.Empty<HistoryCheckpoint>()));
        public Task<Project> CreateProjectAsync(string n, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteProjectAsync(ProjectId id, CancellationToken ct) => throw new NotImplementedException();
        public Task<ImportPublishResult> PublishImportAsync(StagedProject s, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class ReadGate
    {
        public readonly TaskCompletionSource<byte[]> Block = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release(byte[] result) => Block.SetResult(result);
    }

    private sealed class FakeLocks : IWorkspaceLocks
    {
        public bool ReadOnly;
        public Task<WorkspaceCapabilities> GetCapabilitiesAsync(CancellationToken ct)
            => Task.FromResult(new WorkspaceCapabilities(true, true, true, true));
        public Task<string?> TryAcquireAsync(string name, CancellationToken ct)
            => Task.FromResult<string?>(ReadOnly ? null : "lock:" + name);
        public Task<string> WaitForAsync(string name, CancellationToken ct)
            => Task.FromResult("lock:" + name);
        public Task<bool> ReleaseAsync(string lockId, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> IsHeldAsync(string name, CancellationToken ct) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
