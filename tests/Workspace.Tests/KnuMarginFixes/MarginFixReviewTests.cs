using System.Collections.Immutable;
using System.Text;
using Client.Analysis;
using Client.Pages;
using Client.Review;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Client.Workspace.Patching;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Workspace.Tests;

public sealed class MarginFixReviewTests
{
    [Theory]
    [InlineData("left-mm", AnalysisCatalog.MarginLeftFix, "25mm")]
    [InlineData("right-cm", AnalysisCatalog.MarginRightFix, "10mm")]
    [InlineData("top-cm", AnalysisCatalog.MarginTopFix, "20mm")]
    [InlineData("bottom-mm", AnalysisCatalog.MarginBottomFix, "20mm")]
    public async Task Real_review_preview_decline_accept_save_and_undo_preserve_exact_Unicode_BOM_CRLF_bytes(string name, string id, string replacement)
    {
        await using var h = await Context.Open(name); var before = h.Fixture.Bytes; var state = h.Workspace.State(); var head = h.Workspace.C.CurrentRevision!.Id;
        var preview = await h.Service.PreviewFixAsync(id, default); Assert.Equal(ReviewStatus.Ready, preview.Status);
        var p = preview.Preview!; Assert.Equal(state, h.Workspace.State()); Assert.Equal(0,h.Workspace.Repo.Commits);
        Assert.Equal(AnalysisCatalog.Margins, p.Citation!.RuleId); Assert.Equal("1.1.0", p.Citation.Version);
        Assert.Equal("3.1", p.Citation.Section); Assert.Equal(new[] {18}, p.Citation.PrintedPages.ToArray());
        var reviewState = h.Service.State; Assert.Same(p,reviewState.Preview);
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services,services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<ReviewPanel>(ParameterView.FromDictionary(
                new Dictionary<string,object?> { [nameof(ReviewPanel.State)] = reviewState, [nameof(ReviewPanel.Writable)] = true }))).ToHtmlString());
        Assert.Contains("KNU-SRC-MARGINS-001 v1.1.0; guideline §3.1; printed pages 18; PDF pages 18.",html);
        Assert.DoesNotContain("@cite.Version",html);
        var file = Assert.Single(p.Prepared.Files); Assert.Equal(before, file.Before.ToArray());
        var edit = Assert.Single(file.Edits); Assert.Equal(replacement, Encoding.UTF8.GetString(edit.Replacement.Span));
        Assert.Null(h.Service.Decline(p.Id).Failure); Assert.Equal(state,h.Workspace.State());
        p = (await h.Service.PreviewFixAsync(id, default)).Preview!;
        var apply = await h.Service.AcceptAsync(p.Id, default); Assert.True(apply.Changed); Assert.Equal(ReviewStatus.AppliedSaved,apply.Status);
        var after = await h.Workspace.C.GetFileBytesAsync(h.Workspace.C.Files[0], default);
        var expected = before.AsSpan(0,(int)edit.StartByte).ToArray().Concat(Encoding.UTF8.GetBytes(replacement)).Concat(before.AsSpan((int)edit.EndByte).ToArray()).ToArray();
        Assert.Equal(expected,after); Assert.NotEqual(head,h.Workspace.C.CurrentRevision!.Id); Assert.Equal(1,h.Workspace.Repo.Commits);
        Assert.True(h.Service.State.Action!.CanUndo);
        var undo = await h.Service.UndoAsync(apply.ActionId!.Value, default); Assert.Equal(ReviewStatus.UndoSaved,undo.Status); Assert.True(undo.Changed);
        Assert.Equal(before,await h.Workspace.C.GetFileBytesAsync(h.Workspace.C.Files[0],default)); Assert.Equal(2,h.Workspace.Repo.Commits);
        Assert.False(h.Service.State.Action!.CanUndo);
        Assert.Equal(ReviewCode.Consumed,(await h.Service.AcceptAsync(p.Id,default)).Failure!.Code);
    }
    [Fact]
    public async Task Fresh_reanalysis_removes_repaired_side_not_a_reused_descriptor()
    {
        await using var h = await Context.Open("left-mm");
        var p = (await h.Service.PreviewFixAsync(AnalysisCatalog.MarginLeftFix,default)).Preview!;
        Assert.True((await h.Service.AcceptAsync(p.Id,default)).Changed);
        await h.Reanalyze();
        var report = Assert.IsType<AnalysisState.Completed>(h.Analysis.State).Report;
        Assert.Empty(report.Result.Knu!.FixDescriptors);
        Assert.Equal(ReviewCode.UnknownFix,(await h.Service.PreviewFixAsync(AnalysisCatalog.MarginLeftFix,default)).Failure!.Code);
        Assert.Equal(MarginFixFixture.Load("compliant").Bytes,await h.Workspace.C.GetFileBytesAsync(h.Workspace.C.Files[0],default));
    }
    [Theory][InlineData("configuration")][InlineData("profile")][InlineData("edit")][InlineData("head")]
    public async Task Stale_preview_never_mutates_or_saves_new_authority(string change)
    {
        await using var h = await Context.Open("left-mm");
        var p = (await h.Service.PreviewFixAsync(AnalysisCatalog.MarginLeftFix,default)).Preview!;
        switch (change)
        {
            case "configuration": h.Analysis.SetSelection(AnalysisSelection.KnuWithMarginFixes(AnalysisWorkType.Coursework)); break;
            case "profile": h.Analysis.SetSelection(AnalysisSelection.KnuLatest(AnalysisWorkType.Unspecified)); break;
            case "edit": Assert.True(h.Workspace.C.EditText(h.Workspace.C.Files[0].Id,"later ordinary edit🙂\r\n")); break;
            case "head": h.Workspace.Repo.AdvanceHead(); await h.Workspace.C.OpenAsync(h.Workspace.Repo.Project.Id,default); break;
        }
        var state = h.Workspace.State(); var result = await h.Service.AcceptAsync(p.Id,default);
        Assert.False(result.Changed); Assert.NotNull(result.Failure); Assert.Equal(state,h.Workspace.State()); Assert.Equal(0,h.Workspace.Repo.Commits);
    }
    [Fact]
    public async Task Unknown_ID_and_overlapping_edits_are_refused_without_mutation()
    {
        await using var h = await Context.Open("left-mm"); var state = h.Workspace.State();
        Assert.Equal(ReviewCode.UnknownFix,(await h.Service.PreviewFixAsync("KNU-FIX-MARGIN-FORGED-001",default)).Failure!.Code);
        var fix = Assert.Single(Assert.IsType<AnalysisState.Completed>(h.Analysis.State).Report.Result.Knu!.FixDescriptors);
        var w = h.Workspace.C.Files[0]; var bytes = h.Fixture.Bytes;
        FilePatchInput[] patches = [new(w.Id,w.RelativePath,SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes)),bytes.Length,
            [new TextEditInput(fix.Span.StartByte,fix.Span.EndByte,Encoding.UTF8.GetBytes(fix.ExpectedOriginal),Encoding.UTF8.GetBytes(fix.Replacement)),
             new TextEditInput(fix.Span.StartByte,fix.Span.EndByte,Encoding.UTF8.GetBytes(fix.ExpectedOriginal),Encoding.UTF8.GetBytes("30mm"))])];
        var proposed = TextChangeSet.Create(TextPatchOrigin.User,"Overlapping margin proposals must not apply",patches);
        Assert.NotNull(proposed.ChangeSet);
        var prepared = PreparedTextBatch.Prepare(h.Workspace.C.CurrentTextPatchIdentity!,patches,
            [new PatchSourceInput(w.Id,w.RelativePath,w.MediaKind,bytes.Length,w.HasBom,bytes)]);
        Assert.Null(prepared.Batch); Assert.Equal(TextPatchError.Overlap,prepared.Failure!.Code);
        var rejected = await h.Service.PreviewAsync(proposed.ChangeSet!,default);
        Assert.Null(rejected.Preview); Assert.Equal(ReviewCode.InvalidInput,rejected.Failure!.Code);
        Assert.Equal(state,h.Workspace.State()); Assert.Equal(0,h.Workspace.Repo.Commits);
    }
    private sealed class Context : IAsyncDisposable
    {
        internal WorkspaceTextPatchTests.Fixture Workspace = null!;
        internal KnuFixture Fixture = null!;
        internal AnalysisCoordinator Analysis = null!;
        internal ChangeReviewService Service = null!;
        private Analyzer _analyzer = null!;
        internal static async Task<Context> Open(string name)
        {
            var c = new Context { Workspace = await WorkspaceTextPatchTests.Fixture.Open(), Fixture = MarginFixFixture.Load(name) };
            await c.Workspace.C.DisposeAsync(); var bytes = c.Fixture.Bytes;
            var file = new ProjectFile(c.Workspace.Repo.Revision.Files[0].Id,"main.tex","text",SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes)),bytes.Length,true);
            c.Workspace.Repo.Content[file.ContentHash] = bytes; c.Workspace.Repo.Revision = c.Workspace.Repo.Revision with { Files = ImmutableArray.Create(file) };
            c.Workspace.C = new(c.Workspace.Repo,c.Workspace.Locks,new HistoryService(c.Workspace.Repo)); await c.Workspace.C.OpenAsync(c.Workspace.Repo.Project.Id,default);
            c._analyzer = new(c.Fixture); c.Analysis = new(c.Workspace.C,c._analyzer,delayAsync:(_,ct)=>Task.Delay(Timeout.Infinite,ct));
            c.Service = new(c.Workspace.C,c.Analysis,new SourceNavigator(c.Analysis,c.Workspace.C,c._analyzer));
            var done = new TaskCompletionSource<AnalysisState>(TaskCreationOptions.RunContinuationsAsynchronously);
            c.Analysis.Changed += s => { if(s is AnalysisState.Completed or AnalysisState.Failed) done.TrySetResult(s); };
            c.Analysis.SetSelection(AnalysisSelection.KnuWithMarginFixes(AnalysisWorkType.Unspecified));
            Assert.IsType<AnalysisState.Completed>(await done.Task.WaitAsync(TimeSpan.FromSeconds(5))); return c;
        }
        internal async Task Reanalyze()
        {
            var done = new TaskCompletionSource<AnalysisState>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Finished(AnalysisState s) { if(s is AnalysisState.Completed or AnalysisState.Failed) done.TrySetResult(s); }
            Analysis.Changed += Finished; try { Analysis.RequestReanalyze(); Assert.IsType<AnalysisState.Completed>(await done.Task.WaitAsync(TimeSpan.FromSeconds(5))); }
            finally { Analysis.Changed -= Finished; }
        }
        public async ValueTask DisposeAsync() { await Service.DisposeAsync(); await Analysis.DisposeAsync(); await _analyzer.DisposeAsync(); await Workspace.DisposeAsync(); }
    }
    private sealed class Analyzer(KnuFixture initial) : ISourceAnalyzer
    {
        private AnalysisSnapshot? _snapshot;
        private RustSourceAnalyzer? _bound;
        public Task<AnalysisJobResult> AnalyzeAsync(AnalysisSnapshot snapshot,CancellationToken ct)
        {
            _snapshot = snapshot;
            _bound ??= new(new KnuProtocolTests.FixtureJs((_,rid)=>
            {
                var current = _snapshot!;
                var fixture = current.Files[0].Bytes.AsSpan().SequenceEqual(initial.Bytes) ? initial : MarginFixFixture.Load("compliant");
                return fixture.Reply(current,rid);
            }));
            return _bound.AnalyzeAsync(snapshot,ct);
        }
        public Task<bool> IsCurrentWorkerEpochAsync(long epoch,CancellationToken ct) => _bound!.IsCurrentWorkerEpochAsync(epoch,ct);
        public async ValueTask DisposeAsync() { if(_bound is not null) await _bound.DisposeAsync(); }
    }
}
