using System.Collections.Immutable;
using Client.Analysis;
using Client.Pages;
using Client.Review;
using Client.Workspace.Domain;
using Client.Workspace.Patching;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using static Workspace.Tests.TextPatchPreparationTests;

namespace Workspace.Tests.ReviewSelection;

public class RuleAndPanelTests
{
    [Fact] public async Task Canonical_rule_is_indivisible_generic_reuse_refuses_and_full_descriptor_path_still_works()
    {
        await using var f = await WorkspaceTextPatchTests.Fixture.Open();
        var bytes = KnuFixture.Load("negative").Bytes; var id = f.Repo.Revision.Files[0].Id;
        f.Repo.Content[Hash(bytes)] = bytes;
        f.Repo.Revision = f.Repo.Revision with { Files = ImmutableArray.Create(new ProjectFile(id, "main.tex", "text", Hash(bytes), bytes.Length, true)) };
        await f.C.OpenAsync(f.Repo.Project.Id, default);
        await using var port = new RulePort(); await using var analysis = new AnalysisCoordinator(f.C, port, delayAsync: (_, ct) => Task.Delay(Timeout.Infinite, ct));
        await using var service = new ChangeReviewService(f.C, analysis, new SourceNavigator(analysis, f.C, port));
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        analysis.Changed += s => { if (s is AnalysisState.Completed) done.TrySetResult(); };
        analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.BachelorQualification)); await done.Task.WaitAsync(SubsetHost.Bound);
        var p = (await service.PreviewFixAsync(AnalysisCatalog.Fix, default)).Preview!; Assert.Equal(TextPatchOrigin.Rule, p.Proposal.Origin);
        var full = new ReviewSubsetSelection(p.Proposal.Id, [new(id, 0)]);
        Assert.Equal(ReviewCode.UnknownFix, (await service.PreviewSubsetAsync(p.Id, full, default)).Failure!.Code);
        Assert.Equal(ReviewCode.UnknownFix, (await service.PreviewAsync(p.Proposal, default)).Failure!.Code);
        Assert.Null(TextChangeSet.Create(TextPatchOrigin.Rule, "not canonical", [new(id, "main.tex", Hash(bytes), bytes.Length, [new(0, 0, Array.Empty<byte>(), B("x"))])]).ChangeSet);
        Assert.Same(p, service.State.Preview); Assert.Equal(0, f.Repo.Commits);
        var fresh = (await service.PreviewFixAsync(AnalysisCatalog.Fix, default)).Preview!;
        analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.Coursework)); analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.BachelorQualification));
        Assert.False((await service.AcceptAsync(fresh.Id, default)).Changed); Assert.Equal(bytes, await f.C.GetFileBytesAsync(f.C.Files[0], default));
        Assert.NotNull((await service.PreviewFixAsync(AnalysisCatalog.Fix, default)).Preview);
    }
    private sealed class RulePort : ISourceAnalyzer
    {
        private AnalysisSnapshot? _snapshot;
        private readonly RustSourceAnalyzer _native;
        internal RulePort() => _native = new(new KnuProtocolTests.FixtureJs((_, rid) => KnuFixture.Load("negative").Reply(_snapshot!, rid)));
        public Task<AnalysisJobResult> AnalyzeAsync(AnalysisSnapshot s, CancellationToken ct) { _snapshot = s; return _native.AnalyzeAsync(s, ct); }
        public Task<bool> IsCurrentWorkerEpochAsync(long e, CancellationToken ct) => _native.IsCurrentWorkerEpochAsync(e, ct);
        public ValueTask DisposeAsync() => _native.DisposeAsync();
    }

    [Fact] public async Task Component_explicit_exact_checkboxes_no_auto_calls_and_readonly_disable()
    {
        await using var h = await SubsetHost.Open(); var p = await h.Preview();
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        int calls = 0;
        var callback = EventCallback.Factory.Create<ReviewSubsetRequest>(this, (ReviewSubsetRequest _) => { calls++; });
        async Task<string> Html(bool writable) => await renderer.Dispatcher.InvokeAsync(async () => {
            var r = await renderer.RenderComponentAsync<ReviewSubsetPanel>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["State"] = h.Service.State, ["Writable"] = writable, ["Preview"] = callback }));
            return r.ToHtmlString();
        });
        var before = h.F.State(); var html = await Html(true);
        Assert.Contains("type=\"checkbox\"", html); Assert.Contains("Preview selected edits", html); Assert.Contains("original edit 0", html);
        Assert.Contains("WHOLE exact replacement", html); Assert.DoesNotContain("checked=\"checked\"", html);
        Assert.Contains("disabled", await Html(false)); Assert.Equal(0, calls); Assert.Equal(before, h.F.State()); Assert.Equal(0, h.F.Repo.Commits); Assert.Same(p, h.Service.State.Preview);
    }

    [Theory][InlineData("head")][InlineData("ownership")]
    public async Task Actual_authority_queries_refuse_stale_head_or_lock_without_working_generation_changes(string change)
    {
        await using var h = await SubsetHost.Open(); var p = await h.Preview();
        if (change == "head") h.F.Repo.AdvanceHead(); else h.F.Locks.Owned = false;
        var result = await h.Service.PreviewSubsetAsync(p.Id, SubsetHost.Select(p, (0, 0)), default);
        Assert.Null(result.Preview); Assert.NotNull(result.Failure); Assert.Equal(0, h.F.Repo.Commits);
    }

    [Fact] public async Task External_affected_edit_conflicts_without_losing_subset_inverse()
    {
        await using var h = await SubsetHost.Open(); var p = await h.Preview();
        var q = (await h.Service.PreviewSubsetAsync(p.Id, SubsetHost.Select(p, (0, 0)), default)).Preview!;
        var result = await h.Service.AcceptAsync(q.Id, default); Assert.True(result.Changed);
        h.F.C.EditText(q.Prepared.Files[0].FileId, "external later edit");
        Assert.Equal(ReviewCode.Conflict, (await h.Service.UndoAsync(result.ActionId!.Value, default)).Failure!.Code);
        Assert.Equal(result.ActionId, h.Service.State.Action!.Id); Assert.True(h.Service.State.Action.CanUndo);
    }
}
