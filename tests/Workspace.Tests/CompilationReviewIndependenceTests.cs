using Client.Analysis;
using Client.Compilation;
using Client.Review;
using Client.Workspace.Patching;
using Xunit;
using static Workspace.Tests.CompilationTestWorkspace;

namespace Workspace.Tests;

public sealed class CompilationReviewIndependenceTests
{
    [Fact]
    public async Task Actual_reviewed_pending_bytes_and_unsaved_undo_recovery_are_unchanged_by_capture()
    {
        await using var f = await CompilationTestWorkspace.Open();
        await using var analyzer = new IdleAnalyzer();
        await using var analysis = new AnalysisCoordinator(f.Workspace, analyzer, delayAsync: (_, ct) => Task.Delay(Timeout.Infinite, ct));
        await using var review = new ChangeReviewService(f.Workspace, analysis, new SourceNavigator(analysis, f.Workspace, analyzer));
        var working = f.Workspace.Files.Single(w => w.Id == f.Main);
        var original = await f.Workspace.GetFileBytesAsync(working, None);
        var set = TextChangeSet.Create(TextPatchOrigin.User, "Synthetic generic reviewed exact-byte fixture",
            [new(working.Id, working.RelativePath, Hash(original), original.Length,
                [new(original.Length - 1, original.Length, original.AsMemory(original.Length - 1), Bytes("!"))])]).ChangeSet!;
        var preview = (await review.PreviewAsync(set, None)).Preview!; Assert.NotNull(preview);
        f.Repo.FailSave = true;
        var applied = await review.AcceptAsync(preview.Id, None); Assert.True(applied.Changed);
        var pending = Assert.IsType<ExactTextBytes>(working.PendingTextBytes);
        Assert.True(Client.Workspace.Transfer.TextContent.TryCountEditedBytes(working.Content, working.HasBom, out var manualLength));
        Assert.True(pending.Length > manualLength); // reviewed CRLF bytes must not be manually normalized
        var workspaceState = f.State(); var reviewState = review.State; var analysisState = analysis.State;
        var analysisSelection = analysis.CurrentSelection; var version = analysis.CurrentSelectionVersion;
        var commits = f.Repo.Commits; var queries = f.Lock.Queries; var acquisitions = f.Lock.Acquires; var analyzerCalls = analyzer.Calls;
        int reviewEvents = 0, analysisEvents = 0;
        review.Changed += _ => reviewEvents++; analysis.Changed += _ => analysisEvents++;
        var snapshot = await f.Snapshot();
        Assert.Equal(pending.ToArray(), snapshot.Files.Single(x => x.FileId == working.Id).ToArray());
        Assert.Same(pending, working.PendingTextBytes); Assert.Equal(workspaceState, f.State());
        Assert.Equal(reviewState, review.State); Assert.Equal(reviewState.Action, review.State.Action);
        Assert.Same(reviewState.Action!.Exact, review.State.Action!.Exact); // State getter creates view records, not new recovery
        Assert.Equal(0, reviewEvents); Assert.Equal(0, analysisEvents);
        Assert.Same(analysisState, analysis.State); Assert.Equal(analysisSelection, analysis.CurrentSelection); Assert.Equal(version, analysis.CurrentSelectionVersion);
        Assert.Equal(commits, f.Repo.Commits); Assert.Equal(queries, f.Lock.Queries); Assert.Equal(acquisitions, f.Lock.Acquires); Assert.Equal(analyzerCalls, analyzer.Calls);
        var reads = f.Repo.Reads;
        Assert.IsType<CompilationCaptureResult.QuotaExceeded>(await f.Capture.CaptureAsync(Budget with { MaxFileBytes = pending.Length - 1 }, None));
        Assert.Equal(reads, f.Repo.Reads); Assert.Equal(reviewState.Action, review.State.Action);
        Assert.Same(reviewState.Action!.Exact, review.State.Action!.Exact);
        await f.Capture.DisposeAsync(); // must not dispose the injected workspace/analysis/review objects
        var undo = await review.UndoAsync(applied.ActionId!.Value, None); Assert.True(undo.Changed);
        Assert.Equal(original, await f.Workspace.GetFileBytesAsync(working, None));
        Assert.Equal(pending.ToArray(), snapshot.Files.Single(x => x.FileId == working.Id).ToArray());
    }

    [Fact]
    public async Task Compilation_capture_does_not_depend_on_source_profile_or_add_to_its_explicit_runs()
    {
        await using var f = await CompilationTestWorkspace.Open();
        await using var analyzer = new IdleAnalyzer();
        await using var analysis = new AnalysisCoordinator(f.Workspace, analyzer, delayAsync: (_, ct) => Task.Delay(Timeout.Infinite, ct));
        var gate = f.Hold(); var pending = f.Capture.CaptureAsync(Budget, None); await gate.WaitReached();
        var compilerSelection = f.Selection.Current;
        Assert.Equal(0, analyzer.Calls); // compilation capture is already waiting, without source dispatch
        analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.BachelorQualification));
        analysis.SetSelection(AnalysisSelection.EngineStructural());
        await analyzer.TwoCalls.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var explicitCalls = analyzer.Calls;
        gate.Release.SetResult(f.Repo.Content[gate.Hash]);
        var snapshot = Assert.IsType<CompilationCaptureResult.Captured>(await pending).Snapshot;
        Assert.Equal(compilerSelection.Version, snapshot.Identity.SelectionVersion); Assert.Same(compilerSelection.Selection, snapshot.Selection);
        Assert.Equal(2, explicitCalls); Assert.Equal(explicitCalls, analyzer.Calls); Assert.Equal(0, f.Repo.Commits);
    }

    private sealed class IdleAnalyzer : ISourceAnalyzer
    {
        internal int Calls;
        internal readonly TaskCompletionSource TwoCalls = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AnalysisJobResult> AnalyzeAsync(AnalysisSnapshot snapshot, CancellationToken ct)
        { if (Interlocked.Increment(ref Calls) == 2) TwoCalls.TrySetResult(); return Task.FromResult<AnalysisJobResult>(new AnalysisJobResult.Cancelled("unused-source-analysis", 0)); }
        public Task<bool> IsCurrentWorkerEpochAsync(long epoch, CancellationToken ct) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
