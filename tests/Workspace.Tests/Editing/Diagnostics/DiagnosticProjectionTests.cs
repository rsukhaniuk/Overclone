using Client.Analysis;
using Client.Editing.Diagnostics;
using Client.Workspace.Domain;
using Xunit;
using static global::Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.Editing.Diagnostics;

public sealed class DiagnosticProjectionTests
{
    [Fact] public void No_data_filters_do_not_dispatch_or_touch_workspace_or_binary_PDF_bytes() => Run(async () =>
    {
        await using var h = await DiagnosticTestHost.Open(readOnly: true);
        string before = h.Work.State(); int reads = h.Work.Repo.Reads, commits = h.Work.Repo.Commits, opens = h.Config.Opens;
        var empty = h.List.Snapshot(); Assert.Empty(empty.Rows); Assert.All(empty.Origins, s => Assert.False(s.HasReport));
        Assert.True(h.List.SetFilter(new(DiagnosticOrigin.Source, DiagnosticSeverity.Warning, Currentness: DiagnosticCurrentness.Stale)));
        h.List.Next(); h.List.Previous(); h.List.ResetFilters(); h.List.Refresh();
        Assert.Equal(before, h.Work.State()); Assert.Equal(reads, h.Work.Repo.Reads); Assert.Equal(commits, h.Work.Repo.Commits);
        Assert.Equal(opens, h.Config.Opens); Assert.Equal(0, h.Analyzer.Calls); Assert.Empty(h.Config.Workers.Created); Assert.Empty(h.Inspector.Calls); Assert.Equal(0, h.Evaluator.Calls);
    });

    [Fact] public void Origin_severity_file_rule_and_currentness_filter_actual_owner_reports_without_mutation() => Run(async () =>
    {
        await using var h = await DiagnosticTestHost.Open(); var source = await h.Analyze(); await h.CompileReply();
        string state = h.Work.State(); int reads = h.Work.Repo.Reads, dispatches = h.Analyzer.Calls, workers = h.Config.Workers.Created.Count;
        var all = h.List.Snapshot(); Assert.Equal(6, all.ProjectedCount); Assert.True(all.Origins.Single(s => s.Origin == DiagnosticOrigin.Compiler).IsCurrent);
        Assert.True(h.List.SetFilter(new(DiagnosticOrigin.Source, DiagnosticSeverity.Warning, h.Work.Main.ToString(), "ENGINE-STRUCT-001", DiagnosticCurrentness.Current)));
        var one = Assert.Single(h.List.Snapshot().Rows); Assert.Equal(source.Snapshot.Files[0].RelativePath, one.RelativePath); Assert.True(one.HasSourceAction);
        Assert.Contains("synthetic Україна🙂", one.Message); Assert.Equal(DiagnosticRowKind.Finding, one.Kind);
        Assert.True(h.List.SetFilter(new(DiagnosticOrigin.Source, DiagnosticSeverity.Information)));
        var spanless = Assert.Single(h.List.Snapshot().Rows); Assert.False(spanless.HasSourceAction);
        Assert.Equal(DiagnosticActionCode.Unavailable, (await h.List.OpenSourceAsync(spanless, _ => throw new InvalidOperationException("must not navigate"))).Code);
        Assert.True(h.List.SetFilter(new(DiagnosticOrigin.Source, DiagnosticSeverity.Error, "none")));
        var unknown = Assert.Single(h.List.Snapshot().Rows); Assert.Null(unknown.FileId); Assert.Null(unknown.RelativePath); Assert.False(unknown.HasSourceAction);
        Assert.True(h.List.SetFilter(new(DiagnosticOrigin.Compiler, DiagnosticSeverity.Error, h.Work.Main.ToString(), "TEX005")));
        var compiler = Assert.Single(h.List.Snapshot().Rows); Assert.True(compiler.HasSourceAction); Assert.Contains("Captured line", compiler.Message);
        Assert.False(h.List.SetFilter(new((DiagnosticOrigin)900))); Assert.False(h.List.SetFilter(new(File: FileId.New().ToString())));
        h.List.ResetFilters(); Assert.Equal(all.Rows.Select(r => r.Id), h.List.Snapshot().Rows.Select(r => r.Id));
        Assert.Equal(state, h.Work.State()); Assert.Equal(reads, h.Work.Repo.Reads); Assert.Equal(dispatches, h.Analyzer.Calls); Assert.Equal(workers, h.Config.Workers.Created.Count);
    });

    [Fact] public void Known_findings_survive_staleness_filters_persist_and_refresh_never_rebases_old_row_objects() => Run(async () =>
    {
        await using var h = await DiagnosticTestHost.Open(); await h.Analyze();
        h.List.SetFilter(new(DiagnosticOrigin.Source, DiagnosticSeverity.Warning, h.Work.Main.ToString(), "ENGINE-STRUCT-001"));
        var old = Assert.Single(h.List.Snapshot().Rows); var id = old.Id;
        // SetSelection intentionally reanalyzes immediately and this synthetic
        // analyzer may publish a NEW snapshot synchronously. Explicit Cancel
        // supplies the deterministic stale-without-new-report state instead.
        h.Analysis.Cancel();
        var stale = Assert.Single(h.List.Snapshot().Rows); Assert.Equal(id, stale.Id); Assert.False(stale.IsCurrent); Assert.False(stale.HasSourceAction);
        Assert.Equal(h.Work.Main.ToString(), h.List.Snapshot().Filter.File); Assert.Equal("ENGINE-STRUCT-001", h.List.Snapshot().Filter.Rule);
        h.List.SetFilter(h.List.Snapshot().Filter with { Currentness = DiagnosticCurrentness.Current }); Assert.Empty(h.List.Snapshot().Rows);
        h.List.SetFilter(h.List.Snapshot().Filter with { Currentness = DiagnosticCurrentness.All });
        await h.Analyze();
        var fresh = Assert.Single(h.List.Snapshot().Rows); Assert.True(fresh.IsCurrent); Assert.NotEqual(id, fresh.Id); Assert.NotSame(old, fresh);
        Assert.Equal(DiagnosticActionCode.Stale, (await h.List.OpenSourceAsync(old, _ => Task.CompletedTask)).Code);
        await h.Workspace.CloseAsync(default); Assert.Empty(h.List.Snapshot().Rows); Assert.Null(h.List.Snapshot().Filter.File); Assert.Null(h.List.Snapshot().Filter.Rule);
        Assert.Equal(DiagnosticOrigin.Source, h.List.Snapshot().Filter.Origin); Assert.Equal(DiagnosticSeverity.Warning, h.List.Snapshot().Filter.Severity);
    });

    [Fact] public void Display_limits_continuation_and_no_findings_are_honest_not_a_complete_clean_verdict() => Run(async () =>
    {
        await using var h = await DiagnosticTestHost.Open(); h.Analyzer.Count = 1200; await h.Analyze();
        var first = h.List.Snapshot(); Assert.Equal(50, first.Rows.Length); Assert.Equal(1001, first.ProjectedCount);
        var source = first.Origins.Single(s => s.Origin == DiagnosticOrigin.Source); Assert.True(source.Incomplete); Assert.Equal(1201, source.AvailableRows);
        var ids = new HashSet<string>();
        while (true)
        {
            var page = h.List.Snapshot(); Assert.InRange(page.Rows.Length, 1, 50); foreach (var row in page.Rows) Assert.True(ids.Add(row.Id));
            if (!page.HasNext) break; h.List.Next();
        }
        Assert.Equal(1001, ids.Count); h.List.Previous(); Assert.True(h.List.Snapshot().HasNext);
        h.Analyzer.Count = 0; await h.Analyze(); h.List.ResetFilters();
        var noFindings = h.List.Snapshot(); Assert.Single(noFindings.Rows); Assert.Equal(DiagnosticRowKind.CheckStatus, noFindings.Rows[0].Kind);
        Assert.True(noFindings.Origins[0].HasReport); Assert.Equal(0, noFindings.Origins[0].FindingOccurrences);
        Assert.Equal(RuleOutcome.Indeterminate.ToString(), noFindings.Rows[0].Outcome);
    });

    [Fact] public void Committed_actual_thesis_fixture_projects_cited_statuses_without_fabricating_context_locations() => Run(async () =>
    {
        await using var h = await global::Workspace.Tests.ThesisSource.ThesisTestHost.Open();
        var report = await h.Fresh(() => h.Analysis.SetSelection(h.Selection(type: AnalysisWorkType.BachelorQualification)));
        var config = new NeverOpen(); await using var compile = new Client.Compilation.Presentation.CompileWorkspaceSession(h.Workspace, config);
        using var list = new DiagnosticListSession(h.Workspace, h.Analysis, new SourceNavigator(h.Analysis, h.Workspace, h.Analyzer), compile);
        var view = list.Snapshot(); Assert.True(view.Origins[0].HasReport); Assert.Equal(0, config.Opens);
        var thesisRule = view.Rows.First(r => r.Outcome is not null && r.RuleId == report.Result.Thesis!.RuleResults[0].RuleId);
        Assert.NotNull(thesisRule.Citation); Assert.False(thesisRule.HasSourceAction); Assert.False(thesisRule.HasPageAction);
        Assert.Contains("canonical context", thesisRule.NavigationReason); Assert.Contains(view.Rows, r => r.Message.Contains("capability unavailable", StringComparison.Ordinal));
    });
    private sealed class NeverOpen : Client.Compilation.Presentation.ICompileWorkspaceConfiguration
    {
        internal int Opens;
        public Task<Client.Compilation.Presentation.CompileWorkspaceConfigurationResult> OpenAsync()
        { Opens++; throw new InvalidOperationException("A projection must not configure tools."); }
    }
}
