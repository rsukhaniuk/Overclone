using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Client.Analysis;
using Client.Review;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Xunit;

namespace Workspace.Tests;

public sealed class KnuReviewAdmissionTests
{
    [Theory]
    [InlineData("geometry-fix", "1.1.0")]
    [InlineData("negative", "1.0.0")]
    public async Task Actual_Rust_indent_descriptor_previews_accepts_and_undoes_exact_bytes(string fixtureName, string version)
    {
        await using var h = await Context.Open(fixtureName);
        var before = h.Fixture.Bytes;
        var report = Assert.IsType<AnalysisState.Completed>(h.Analysis.State).Report;
        Assert.Equal(version, report.Snapshot.Profile.Version);
        var descriptor = Assert.Single(report.Result.Knu!.FixDescriptors);
        Assert.Equal(AnalysisCatalog.Indent, descriptor.RuleId);
        Assert.Equal("1.0.0", descriptor.RuleVersion);
        if (version == "1.1.0")
            Assert.Contains(AnalysisLimitation.UnsupportedGeometry, report.Result.Knu.Files[0].Limitations);
        Assert.Equal(ReviewCode.UnknownFix, (await h.Service.PreviewFixAsync(AnalysisCatalog.Margins, default)).Failure!.Code);
        var preview = await h.Service.PreviewFixAsync(AnalysisCatalog.Fix, default);
        Assert.Equal(ReviewStatus.Ready, preview.Status);
        var p = Assert.IsType<ReviewPreview>(preview.Preview);
        Assert.Equal(AnalysisCatalog.Indent, p.Citation!.RuleId);
        Assert.Equal("3.1", p.Citation.Section);
        Assert.Equal(before, p.Prepared.Files[0].Before.ToArray());
        Assert.Equal(0, h.Workspace.Repo.Commits);
        var expected = before[..(int)descriptor.Span.StartByte]
            .Concat(Encoding.UTF8.GetBytes(descriptor.Replacement))
            .Concat(before[(int)descriptor.Span.EndByte..]).ToArray();
        var apply = await h.Service.AcceptAsync(p.Id, default);
        Assert.Equal(ReviewStatus.AppliedSaved, apply.Status);
        Assert.True(apply.Changed);
        Assert.Equal(expected, await h.Workspace.C.GetFileBytesAsync(h.Workspace.C.Files[0], default));
        Assert.False(h.Workspace.C.IsDirty);
        var undo = await h.Service.UndoAsync(apply.ActionId!.Value, default);
        Assert.Equal(ReviewStatus.UndoSaved, undo.Status);
        Assert.True(undo.Changed);
        Assert.Equal(before, await h.Workspace.C.GetFileBytesAsync(h.Workspace.C.Files[0], default));
        Assert.Equal(2, h.Workspace.Repo.Commits);
        Assert.False((await h.Service.AcceptAsync(p.Id, default)).Changed);
    }

    [Theory]
    [InlineData(AnalysisCatalog.KnuId, "1.1.1")]
    [InlineData(AnalysisCatalog.KnuId, "1.5.0")]
    [InlineData(AnalysisCatalog.KnuId, "latest")]
    [InlineData("unknown", "1.1.0")]
    public async Task Invalid_selection_cannot_reuse_latest_preview_or_mutate_source(string id, string version)
    {
        await using var h = await Context.Open("geometry-fix");
        var preview = (await h.Service.PreviewFixAsync(AnalysisCatalog.Fix, default)).Preview!;
        h.Analysis.SetSelection(new(new(id, version), new(AnalysisWorkType.BachelorQualification)));
        Assert.IsType<AnalysisState.InvalidInput>(h.Analysis.State);
        Assert.Null(h.Service.State.Preview);
        Assert.Equal(ReviewCode.UnknownFix, (await h.Service.PreviewFixAsync(AnalysisCatalog.Fix, default)).Failure!.Code);
        Assert.False((await h.Service.AcceptAsync(preview.Id, default)).Changed);
        Assert.Equal(0, h.Workspace.Repo.Commits);
        Assert.Equal(h.Fixture.Bytes, await h.Workspace.C.GetFileBytesAsync(h.Workspace.C.Files[0], default));
    }

    [Theory]
    [InlineData(AnalysisCatalog.KnuId, "1.1.1")]
    [InlineData("other", "1.1.0")]
    [InlineData("engine-structural", "0")]
    [InlineData(AnalysisCatalog.KnuId, "1.0.0")]
    public void Canonical_admission_rejects_foreign_unknown_or_cross_version_snapshot(string id, string version)
    {
        var fixture = KnuFixture.Load("geometry-fix");
        var result = Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(fixture.Json)).Result;
        var report = new AnalysisReport(fixture.Snapshot() with { Profile = new(id, version) }, result, "fixture-request", 7);
        // Rejection-only test of the internal admission boundary. Valid preview,
        // apply and undo above use the real service/workspace and typed adapter.
        var method = typeof(ChangeReviewService).Assembly.GetType("Client.Review.ReviewPreparation")!
            .GetMethod("CanonicalF1", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Null(method.Invoke(null, [report, AnalysisCatalog.Fix, null, null]));
    }

    [Fact]
    public void Canonical_admission_rejects_mismatched_result_snapshot_identity()
    {
        var fixture = KnuFixture.Load("geometry-fix");
        var result = Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(fixture.Json)).Result;
        var report = new AnalysisReport(fixture.Snapshot(), result with { SnapshotId = new string('6', 32) }, "fixture-request", 7);
        var method = typeof(ChangeReviewService).Assembly.GetType("Client.Review.ReviewPreparation")!
            .GetMethod("CanonicalF1", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Null(method.Invoke(null, [report, AnalysisCatalog.Fix, null, null]));
    }

    // Reuses the repository/locks/real WorkspaceCoordinator from existing review
    // tests. The only analyzer transport is actual Rust fixture JSON rebound by
    // the existing KnuFixture helper; no pre-accepted review result is modeled.
    private sealed class Context : IAsyncDisposable
    {
        public WorkspaceTextPatchTests.Fixture Workspace = null!;
        public KnuFixture Fixture = null!;
        public AnalysisCoordinator Analysis = null!;
        public ChangeReviewService Service = null!;
        private CapturedAnalyzer _analyzer = null!;
        public static async Task<Context> Open(string fixtureName)
        {
            var c = new Context { Workspace = await WorkspaceTextPatchTests.Fixture.Open(), Fixture = KnuFixture.Load(fixtureName) };
            await c.Workspace.C.DisposeAsync();
            var bytes = c.Fixture.Bytes;
            var file = new ProjectFile(c.Workspace.Repo.Revision.Files[0].Id, "main.tex", "text",
                SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes)), bytes.Length,
                bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }));
            c.Workspace.Repo.Content[file.ContentHash] = bytes;
            c.Workspace.Repo.Revision = c.Workspace.Repo.Revision with { Files = ImmutableArray.Create(file) };
            c.Workspace.C = new(c.Workspace.Repo, c.Workspace.Locks, new HistoryService(c.Workspace.Repo));
            await c.Workspace.C.OpenAsync(c.Workspace.Repo.Project.Id, default);
            c._analyzer = new(c.Fixture);
            c.Analysis = new(c.Workspace.C, c._analyzer, delayAsync: (_, ct) => Task.Delay(Timeout.Infinite, ct));
            c.Service = new(c.Workspace.C, c.Analysis, new SourceNavigator(c.Analysis, c.Workspace.C, c._analyzer));
            var done = new TaskCompletionSource<AnalysisState>(TaskCreationOptions.RunContinuationsAsynchronously);
            c.Analysis.Changed += state => { if (state is AnalysisState.Completed or AnalysisState.Failed) done.TrySetResult(state); };
            c.Analysis.SetSelection(fixtureName == "geometry-fix"
                ? AnalysisSelection.KnuLatest(AnalysisWorkType.BachelorQualification)
                : AnalysisSelection.Knu(AnalysisWorkType.BachelorQualification));
            Assert.IsType<AnalysisState.Completed>(await done.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            return c;
        }
        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync(); await Analysis.DisposeAsync(); await _analyzer.DisposeAsync(); await Workspace.DisposeAsync();
        }
    }
    private sealed class CapturedAnalyzer : ISourceAnalyzer
    {
        private AnalysisSnapshot? _snapshot;
        private readonly RustSourceAnalyzer _native;
        public CapturedAnalyzer(KnuFixture fixture)
            => _native = new(new KnuProtocolTests.FixtureJs((_, requestId) => fixture.Reply(_snapshot!, requestId)));
        public Task<AnalysisJobResult> AnalyzeAsync(AnalysisSnapshot snapshot, CancellationToken ct)
        { _snapshot = snapshot; return _native.AnalyzeAsync(snapshot, ct); }
        public Task<bool> IsCurrentWorkerEpochAsync(long epoch, CancellationToken ct) => _native.IsCurrentWorkerEpochAsync(epoch, ct);
        public ValueTask DisposeAsync() => _native.DisposeAsync();
    }
}
