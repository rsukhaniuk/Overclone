using System.Collections.Immutable;
using Client.Analysis;
using Client.Review;
using Client.Workspace.Domain;
using Client.Workspace.Patching;
using Xunit;
using static Workspace.Tests.TextPatchPreparationTests;
using static Workspace.Tests.ReviewSelection.SubsetHost;

namespace Workspace.Tests.ReviewSelection;

/// <summary>Real coordinator/service, controlled actual capture and worker tasks.
/// No timers/reflection/event spoofing. Worker Started and Completed are observed
/// while the SAME independent review read is still held noncooperatively.</summary>
public class AnalysisCurrentnessTests
{
    public static IEnumerable<object[]> PositiveCases() =>
        from origin in new[] { TextPatchOrigin.User, TextPatchOrigin.Ai }
        from pending in new[] { false, true }
        from subset in new[] { false, true }
        select new object[] { origin, pending, subset };

    [Theory, MemberData(nameof(PositiveCases))]
    public async Task Unrelated_started_and_completed_analysis_preserves_exact_generic_pending_and_ready_authority(TextPatchOrigin origin, bool pending, bool subset)
    {
        var port = new ControlledPort(); await using var h = await Open(port);
        var set = await h.Proposal(origin);
        ReviewPreview? original = subset ? (await h.Service.PreviewAsync(set, default)).Preview : null;
        if (subset) Assert.NotNull(original);
        if (pending) { h.Repo.HoldsRemaining = 1; h.Repo.Hold = true; }
        var operation = subset ? h.Service.PreviewSubsetAsync(original!.Id, Select(original, (0, 0), (1, 1)), default)
            : h.Service.PreviewAsync(set, default);
        if (pending) await h.Repo.Reached.Task.WaitAsync(Bound);
        ReviewPreview? ready = pending ? null : (await operation.WaitAsync(Bound)).Preview;
        if (!pending) Assert.NotNull(ready);
        var before = h.F.State(); var epoch = h.Analysis.CurrentWorkspaceEpoch; var selection = h.Analysis.CurrentSelectionVersion;
        var completed = Completed(h.Analysis); var call = port.Expect();
        h.Analysis.RequestReanalyze(); // actual Pending -> capture -> Running
        await call.Reached.Task.WaitAsync(Bound);
        Assert.IsType<AnalysisState.Running>(h.Analysis.State);
        if (pending) { Assert.False(operation.IsCompleted); Assert.True(h.Service.State.Busy); }
        else Assert.Same(ready, h.Service.State.Preview);
        call.Release.TrySetResult(); var report = await completed.WaitAsync(Bound);
        Assert.Same(report, Assert.IsType<AnalysisState.Completed>(h.Analysis.State).Report);
        Assert.Equal(epoch, h.Analysis.CurrentWorkspaceEpoch); Assert.Equal(selection, h.Analysis.CurrentSelectionVersion);
        if (pending)
        {
            // Analysis Completed must not cancel the independent held read or
            // release review admission early. Without the product fix this fails.
            Assert.False(operation.IsCompleted); Assert.True(h.Service.State.Busy);
            h.Repo.Hold = false; h.Repo.Release.TrySetResult();
            var result = await operation.WaitAsync(Bound); Assert.Null(result.Failure); ready = Assert.IsType<ReviewPreview>(result.Preview);
        }
        Assert.Same(ready, h.Service.State.Preview); Assert.Equal(origin, ready!.Proposal.Origin);
        Assert.Equal(before, h.F.State()); Assert.Equal(0, h.F.Repo.Commits);
        if (subset) { Assert.NotEqual(original!.Id, ready.Id); Assert.Equal(original.Binding, ready.Binding); }
        // It remains real usable authority, not merely an unaccepted stale view.
        var accepted = await h.Service.AcceptAsync(ready.Id, default); Assert.True(accepted.Changed); Assert.Null(accepted.Failure);
        Assert.Equal(ReviewStatus.UndoSaved, (await h.Service.UndoAsync(accepted.ActionId!.Value, default)).Status);
    }

    public static IEnumerable<object[]> NegativeCases() =>
        from origin in new[] { TextPatchOrigin.User, TextPatchOrigin.Ai }
        from pending in new[] { false, true }
        from mutation in new[] { "source-epoch-aba", "head", "profile-aba", "root-aba", "worktype-aba", "lock", "session", "cancel" }
        select new object[] { origin, pending, mutation };

    [Theory, MemberData(nameof(NegativeCases))]
    public async Task Generic_preservation_never_resurrects_changed_binding_or_revoked_intent(TextPatchOrigin origin, bool pending, string mutation)
    {
        var port = new ControlledPort(); await using var h = await Open(port);
        if (mutation == "head") h.F.C.EditText(h.F.C.Files[0].Id, "dirty current bytes before proposal\r\n");
        var set = await h.Proposal(origin);
        if (pending) { h.Repo.HoldsRemaining = 1; h.Repo.Hold = true; }
        var operation = h.Service.PreviewAsync(set, default);
        if (pending) await h.Repo.Reached.Task.WaitAsync(Bound);
        var preview = pending ? null : (await operation.WaitAsync(Bound)).Preview;
        if (!pending) Assert.NotNull(preview);
        var generation = h.F.C.WorkingGeneration; var epoch = h.Analysis.CurrentWorkspaceEpoch; var file = h.F.C.Files[0];
        var oldHead = h.F.C.CurrentRevision!.Id;
        switch (mutation)
        {
            case "source-epoch-aba":
                var content = file.Content; h.F.C.EditText(file.Id, "intervening"); h.F.C.EditText(file.Id, content);
                Assert.True(h.F.C.WorkingGeneration > generation); Assert.True(h.Analysis.CurrentWorkspaceEpoch > epoch); break;
            case "head":
                await h.F.C.SaveAsync(default); Assert.NotEqual(oldHead, h.F.C.CurrentRevision!.Id); Assert.Equal(generation, h.F.C.WorkingGeneration); break;
            case "profile-aba": h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.BachelorQualification)); h.Analysis.SetSelection(AnalysisSelection.EngineStructural()); break;
            case "root-aba": h.Analysis.SetSelection(AnalysisSelection.SourceReferences(file.Id.ToString())); h.Analysis.SetSelection(AnalysisSelection.SourceReferences(h.F.C.Files[1].Id.ToString())); h.Analysis.SetSelection(AnalysisSelection.EngineStructural()); break;
            case "worktype-aba": h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.Coursework)); h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.MasterQualification)); h.Analysis.SetSelection(AnalysisSelection.EngineStructural()); break;
            case "lock": h.F.Locks.Owned = false; break;
            case "session": await h.F.C.OpenAsync(h.F.Repo.Project.Id, default); break;
            default: h.Service.Cancel(); break;
        }
        // A fresh, genuinely published report must not resurrect old authority.
        var done = Completed(h.Analysis); h.Analysis.RequestReanalyze(); await done.WaitAsync(Bound);
        if (pending)
        {
            Assert.True(h.Service.State.Busy); Assert.False(operation.IsCompleted);
            h.Repo.Hold = false; h.Repo.Release.TrySetResult();
            var rejected = await operation.WaitAsync(Bound); Assert.Null(rejected.Preview); Assert.NotNull(rejected.Failure);
        }
        Assert.Null(h.Service.State.Preview);
        if (preview is not null) Assert.False((await h.Service.AcceptAsync(preview.Id, default)).Changed);
        Assert.Null(h.Service.State.Action);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Rule_pending_and_ready_previews_remain_strictly_report_bound(bool pending)
    {
        var port = new ControlledPort(canonical: true); await using var h = await Open(port);
        var bytes = KnuFixture.Load("negative").Bytes; var file = h.F.Repo.Revision.Files[0];
        h.F.Repo.Content[Hash(bytes)] = bytes;
        h.F.Repo.Revision = h.F.Repo.Revision with { Files = ImmutableArray.Create(new ProjectFile(file.Id, "main.tex", "text", Hash(bytes), bytes.Length, true)) };
        await h.F.C.OpenAsync(h.F.Repo.Project.Id, default);
        var first = Completed(h.Analysis); h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.BachelorQualification));
        var oldReport = await first.WaitAsync(Bound);
        if (pending) { h.Repo.HoldsRemaining = 1; h.Repo.Hold = true; }
        var operation = h.Service.PreviewFixAsync(AnalysisCatalog.Fix, default);
        if (pending) await h.Repo.Reached.Task.WaitAsync(Bound);
        var oldPreview = pending ? null : (await operation.WaitAsync(Bound)).Preview;
        if (!pending) { Assert.NotNull(oldPreview); Assert.Equal(TextPatchOrigin.Rule, oldPreview.Proposal.Origin); }
        var next = Completed(h.Analysis); var call = port.Expect(); h.Analysis.RequestReanalyze();
        await call.Reached.Task.WaitAsync(Bound); Assert.IsType<AnalysisState.Running>(h.Analysis.State);
        Assert.Null(h.Service.State.Preview); call.Release.TrySetResult(); var newReport = await next.WaitAsync(Bound);
        Assert.NotSame(oldReport, newReport); Assert.NotEqual(oldReport.Snapshot.Id, newReport.Snapshot.Id);
        if (pending)
        {
            Assert.True(h.Service.State.Busy); Assert.False(operation.IsCompleted);
            h.Repo.Hold = false; h.Repo.Release.TrySetResult(); var rejected = await operation.WaitAsync(Bound);
            Assert.Null(rejected.Preview); Assert.NotNull(rejected.Failure);
        }
        else Assert.False((await h.Service.AcceptAsync(oldPreview!.Id, default)).Changed);
        Assert.Null(h.Service.State.Action); Assert.Equal(0, h.F.Repo.Commits);
        // No weakening of canonical creation or its current fresh-report route.
        Assert.NotNull((await h.Service.PreviewFixAsync(AnalysisCatalog.Fix, default)).Preview);
    }

    private static Task<AnalysisReport> Completed(AnalysisCoordinator coordinator)
    {
        var done = new TaskCompletionSource<AnalysisReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(AnalysisState state) { if (state is AnalysisState.Completed completed) { coordinator.Changed -= Changed; done.TrySetResult(completed.Report); } }
        coordinator.Changed += Changed; return done.Task;
    }

    private sealed class ControlledPort : ISourceAnalyzer
    {
        internal sealed class Call
        {
            internal readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private readonly Queue<Call> _queued = new(); private readonly List<Call> _all = new();
        private readonly RustSourceAnalyzer? _native; private AnalysisSnapshot? _snapshot; private bool _disposed;
        internal ControlledPort(bool canonical = false)
        {
            if (canonical) _native = new(new KnuProtocolTests.FixtureJs((_, rid) => KnuFixture.Load("negative").Reply(_snapshot!, rid)));
        }
        internal Call Expect() { var c = new Call(); lock (_queued) { _queued.Enqueue(c); _all.Add(c); } return c; }
        public async Task<AnalysisJobResult> AnalyzeAsync(AnalysisSnapshot snapshot, CancellationToken ct)
        {
            Call? call; lock (_queued) call = _queued.Count > 0 ? _queued.Dequeue() : null;
            if (call is not null) { call.Reached.TrySetResult(); await call.Release.Task; }
            if (_native is not null) { _snapshot = snapshot; return await _native.AnalyzeAsync(snapshot, ct); }
            var result = new AnalysisResultDto(snapshot.Id.ToString(), new(snapshot.Profile.Id, snapshot.Profile.Version),
                new(snapshot.Files.Select(f => new AnalysisSourceFileFactsDto(f.FileId.ToString(), f.ByteLength, f.HasBom, NewlineKind.Mixed, false, false, 0, RuleOutcome.Indeterminate, [])).ToArray()),
                [], [], RuleOutcome.Indeterminate, new([], ["synthetic-controlled-analysis"], [], false, false), ["pdfFacts", "pdfChecks"], snapshot.Configuration, ReferenceConfiguration: snapshot.ReferenceConfiguration);
            return new AnalysisJobResult.Completed("controlled-review-progress", 7, result);
        }
        public Task<bool> IsCurrentWorkerEpochAsync(long epoch, CancellationToken ct) => _native?.IsCurrentWorkerEpochAsync(epoch, ct) ?? Task.FromResult(epoch == 7);
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return; _disposed = true; lock (_queued) foreach (var c in _all) c.Release.TrySetResult();
            if (_native is not null) await _native.DisposeAsync();
        }
    }
}
