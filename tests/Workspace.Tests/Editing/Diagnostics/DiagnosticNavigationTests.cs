using Client.Analysis;
using Client.Editing.Diagnostics;
using Client.Compilation;
using Client.Compilation.Diagnostics;
using Client.Pdf.Preview;
using Xunit;
using static global::Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.Editing.Diagnostics;

public sealed class DiagnosticNavigationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Canonical_source_and_failed_compiler_requests_use_existing_mapping_and_leave_bytes_untouched(bool readOnly) => Run(async () =>
    {
        await using var h = await DiagnosticTestHost.Open(readOnly); var report = await h.Analyze(); await h.CompileReply();
        string state = h.Work.State(); int commits = h.Work.Repo.Commits, calls = h.Analyzer.Calls;
        var source = h.List.Snapshot().Rows.First(r => r.Origin == DiagnosticOrigin.Source && r.HasSourceAction);
        DiagnosticSourceRequest? saved = null;
        var result = await h.List.OpenSourceAsync(source, async request =>
        {
            saved = request; Assert.True(request.IsCurrent); Assert.Same(report, request.Report);
            Assert.Same(report.Result.Diagnostics[request.DiagnosticIndex], request.Diagnostic);
            var selected = Assert.IsType<NavigationResult.Selected>(await request.PrepareAsync());
            Assert.Equal("🙂", selected.ExpectedNormalizedSubstring); Assert.Equal((7, 9), (selected.TextareaStart, selected.TextareaEnd));
        });
        Assert.Equal(DiagnosticActionCode.Presented, result.Code); Assert.False(saved!.IsCurrent);
        Assert.IsType<NavigationResult.Rejected>(await saved.PrepareAsync());
        var compiler = h.List.Snapshot().Rows.First(r => r.Origin == DiagnosticOrigin.Compiler && r.HasSourceAction && r.RelativePath == "main.tex");
        Assert.Null(h.Compile.DisplayedOutput); // failed attempts have independent source authority
        result = await h.List.OpenCompilerAsync(compiler, async request =>
        {
            Assert.Same(h.Compile.Attempt!.Attempt!.Snapshot, request.Canonical.Snapshot);
            Assert.Same(((CompilerLogParseResult.Parsed)h.Compile.Attempt.ParserResult!).Report.Diagnostics[request.DiagnosticIndex], request.Canonical.Diagnostic);
            var selected = await request.PrepareAsync(); Assert.NotNull(selected);
            Assert.Equal("main", selected.ExpectedTextareaText[selected.TextareaRange.Start..selected.TextareaRange.End]);
        });
        Assert.Equal(DiagnosticActionCode.Presented, result.Code); Assert.Equal(state, h.Work.State()); Assert.Equal(commits, h.Work.Repo.Commits); Assert.Equal(calls, h.Analyzer.Calls);
    });

    [Fact] public void Foreign_rows_cloned_canonical_diagnostics_and_spanless_unknown_locations_do_not_mint_actions() => Run(async () =>
    {
        await using var h = await DiagnosticTestHost.Open(); var report = await h.Analyze();
        using var other = new DiagnosticListSession(h.Workspace, h.Analysis, h.Navigator, h.Compile);
        var first = h.List.Snapshot().Rows.First(r => r.HasSourceAction); var foreign = other.Snapshot().Rows.First(r => r.HasSourceAction);
        Assert.Equal(first.Id, foreign.Id); Assert.NotSame(first, foreign);
        int callbacks = 0;
        Assert.Equal(DiagnosticActionCode.Stale, (await h.List.OpenSourceAsync(foreign, _ => { callbacks++; return Task.CompletedTask; })).Code);
        var diagnostics = Assert.IsType<List<AnalysisEngineDiagnosticDto>>(report.Result.Diagnostics);
        diagnostics[0] = diagnostics[0] with { Span = new(0, 1), FileId = "forged" }; // same ID, different canonical member
        Assert.Equal(DiagnosticActionCode.Stale, (await h.List.OpenSourceAsync(first, _ => { callbacks++; return Task.CompletedTask; })).Code);
        foreach (var row in h.List.Snapshot().Rows.Where(r => !r.HasSourceAction))
            Assert.NotEqual(DiagnosticActionCode.Presented, (await h.List.OpenSourceAsync(row, _ => { callbacks++; return Task.CompletedTask; })).Code);
        Assert.Equal(0, callbacks);
    });

    [Theory] [InlineData(0, 3)] [InlineData(17, 18)]
    public void Canonical_nonselectable_or_invalid_spans_are_not_relocated_by_the_projection(int start, int end) => Run(async () =>
    {
        await using var h = await DiagnosticTestHost.Open();
        h.Analyzer.Transform = snapshot =>
        {
            var result = DiagnosticTestHost.SourcePort.Result(snapshot, 1);
            return result with { Diagnostics = new[] { result.Diagnostics[0] with { Span = new(start, end) } } };
        };
        await h.Analyze(); var row = h.List.Snapshot().Rows.First(r => r.HasSourceAction); NavigationResult? mapped = null;
        await h.List.OpenSourceAsync(row, async request => mapped = await request.PrepareAsync());
        if (start == 0) Assert.IsType<NavigationResult.NonSelectable>(mapped); else Assert.IsType<NavigationResult.Rejected>(mapped);
    });

    [Theory] [InlineData("cancel")] [InlineData("filter-aba")] [InlineData("edit-aba")] [InlineData("worktype-aba")]
    [InlineData("root-aba")] [InlineData("reopen")] [InlineData("project")] [InlineData("restore")] [InlineData("dispose")]
    public void Actual_held_callback_retains_admission_and_rejects_obsolete_requests(string change) => Run(async () =>
    {
        await using var h = await DiagnosticTestHost.Open(); await h.Analyze();
        var row = h.List.Snapshot().Rows.First(r => r.HasSourceAction);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DiagnosticSourceRequest? held = null;
        var pending = h.List.OpenSourceAsync(row, async request => { held = request; reached.TrySetResult(); await release.Task; Assert.False(request.IsCurrent); });
        await reached.Task.WaitAsync(DiagnosticTestHost.Bound);
        switch (change)
        {
            case "cancel": h.List.Cancel(); break;
            case "filter-aba": h.List.SetFilter(new(Severity: DiagnosticSeverity.Error)); h.List.ResetFilters(); break;
            case "edit-aba": var file = h.Workspace.Files.First(f => f.Id == h.Work.Main); var old = file.Content; h.Workspace.EditText(file.Id, "away"); h.Workspace.EditText(file.Id, old); break;
            case "worktype-aba": h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.BachelorQualification)); h.Analysis.SetSelection(AnalysisSelection.EngineStructural()); break;
            case "root-aba": h.Analysis.SetSelection(AnalysisSelection.SourceReferences(h.Work.Main.ToString())); h.Analysis.SetSelection(AnalysisSelection.EngineStructural()); break;
            case "reopen": await h.Workspace.OpenAsync(h.Work.Repo.Project.Id, default); break;
            case "project": var project = new Client.Workspace.Domain.Project(Client.Workspace.Domain.ProjectId.New(), "other", null); h.Work.Repo.Projects[project.Id] = (project, null); await h.Workspace.OpenAsync(project.Id, default); break;
            case "restore": var historical = h.Workspace.CurrentRevision! with { Id = Client.Workspace.Domain.RevisionId.New() }; h.Work.Repo.Revisions[historical.Id] = historical;
                var restore = await h.Workspace.CaptureRestorePreviewAsync(historical.Id, default); await h.Workspace.AcceptRestoreAsync(restore, default); break;
            case "dispose": h.List.Dispose(); break;
        }
        Assert.True(h.List.Busy); Assert.False(pending.IsCompleted); Assert.False(held!.IsCurrent);
        var second = await h.List.OpenSourceAsync(row, _ => throw new InvalidOperationException("must not run"));
        Assert.Equal(change == "dispose" ? DiagnosticActionCode.Disposed : DiagnosticActionCode.Busy, second.Code);
        release.TrySetResult(); var result = await pending.WaitAsync(DiagnosticTestHost.Bound);
        Assert.Contains(result.Code, new[] { DiagnosticActionCode.Stale, DiagnosticActionCode.Cancelled, DiagnosticActionCode.Disposed }); Assert.False(h.List.Busy);
    });

    [Fact] public void Noncooperative_epoch_query_retains_admission_and_worker_reset_revokes_known_findings_without_dispatch() => Run(async () =>
    {
        await using var h = await DiagnosticTestHost.Open(); await h.Analyze(); var row = h.List.Snapshot().Rows.First(r => r.HasSourceAction);
        h.Analyzer.HoldEpoch = true; int callbacks = 0;
        var pending = h.List.OpenSourceAsync(row, _ => { callbacks++; return Task.CompletedTask; }); await h.Analyzer.Reached.Task.WaitAsync(DiagnosticTestHost.Bound);
        h.Analyzer.Epoch++; h.List.Cancel(); Assert.True(h.List.Busy); Assert.False(pending.IsCompleted);
        h.Analyzer.Release.TrySetResult(); await pending.WaitAsync(DiagnosticTestHost.Bound);
        Assert.Equal(0, callbacks); Assert.Contains(h.List.Snapshot().Rows, r => r.Id == row.Id && !r.IsCurrent && !r.HasSourceAction);
        Assert.Equal(1, h.Analyzer.Calls);
    });

    [Fact] public void Compiler_selection_new_attempt_and_held_actual_materialization_cannot_reuse_old_authority() => Run(async () =>
    {
        await using var h = await DiagnosticTestHost.Open(); await h.CompileReply();
        var row = h.List.Snapshot().Rows.First(r => r.Origin == DiagnosticOrigin.Compiler && r.HasSourceAction && r.RelativePath == "main.tex");
        var gate = h.Work.Hold(h.Work.Main); Client.Compilation.Navigation.CompilerSourceNavigationPolicy.Selection? selection = null;
        var pending = h.List.OpenCompilerAsync(row, async request => selection = await request.PrepareAsync()); await gate.WaitReached();
        h.Compile.SelectMain(h.Workspace.Files.First(f => f.RelativePath == "chapter.tex").Id); h.Compile.SelectMain(h.Work.Main);
        Assert.True(h.List.Busy); Assert.False(pending.IsCompleted); gate.Release.TrySetResult(h.Work.Repo.Content[gate.Hash]);
        var result = await pending.WaitAsync(DiagnosticTestHost.Bound); Assert.NotEqual(DiagnosticActionCode.Presented, result.Code); Assert.Null(selection);
        await h.CompileReply(); var fresh = h.List.Snapshot().Rows.First(r => r.Origin == DiagnosticOrigin.Compiler && r.HasSourceAction);
        await h.Compile.CompileAsync(); var worker = await h.Config.Workers.Next();
        Assert.False(h.List.Snapshot().Rows.First(r => r.Id == fresh.Id).HasSourceAction);
        worker.Reply.TrySetResult(worker.Output(CompileOutputStatus.Failed)); await h.Compile.LastTicket!.Completion.WaitAsync(DiagnosticTestHost.Bound);
    });

    [Fact] public void Narrow_rendered_rows_are_page_only_and_same_hash_historical_ABA_never_restores_the_old_action() => Run(async () =>
    {
        await using var h = await DiagnosticTestHost.Open(); await h.NarrowSections();
        var rows = h.List.Snapshot().Rows.Where(r => r.Origin == DiagnosticOrigin.Rendered).ToArray(); Assert.Equal(4, rows.Length);
        var total = rows.Single(r => r.PageIndex is null); Assert.False(total.HasPageAction); Assert.False(total.HasSourceAction);
        var page = rows.First(r => r.HasPageAction); Assert.False(page.HasSourceAction); int reads = h.Work.Repo.Reads, checks = h.Evaluator.Calls;
        var result = await h.List.OpenPageAsync(page, async request => { Assert.Equal(page.PageIndex, request.PageIndex); Assert.True(await request.GoToPageAsync()); });
        Assert.Equal(DiagnosticActionCode.Presented, result.Code); Assert.Equal(page.PageIndex, h.Compile.Preview.State.PageIndex); Assert.Equal(reads, h.Work.Repo.Reads); Assert.Equal(checks, h.Evaluator.Calls);
        var bytes = h.Compile.DisplayedOutput!.ToPayloadArray()!;
        h.Compile.Preview.ProvideBytes(bytes, "historical", PdfDisplayProvenance.CallerSaysHistorical); await h.Compile.Preview.WhenSettled.WaitAsync(DiagnosticTestHost.Bound);
        h.Compile.Preview.ProvideBytes(bytes, "same hash caller current", PdfDisplayProvenance.CallerSaysCurrent); await h.Compile.Preview.WhenSettled.WaitAsync(DiagnosticTestHost.Bound);
        Assert.All(h.List.Snapshot().Rows.Where(r => r.Origin == DiagnosticOrigin.Rendered), r => Assert.False(r.HasPageAction));
        Assert.NotEqual(DiagnosticActionCode.Presented, (await h.List.OpenPageAsync(page, _ => throw new InvalidOperationException("old page action"))).Code);
        h.Extent.SelectWorkType("masterQualification"); h.Extent.SelectWorkType("bachelorQualification");
        Assert.All(h.List.Snapshot().Rows.Where(r => r.Origin == DiagnosticOrigin.Rendered), r => Assert.False(r.IsCurrent));
    });

    [Fact] public void Cancelling_an_owned_page_stops_only_that_generation_and_waits_for_actual_inspection() => Run(async () =>
    {
        await using var h = await DiagnosticTestHost.Open(); await h.NarrowSections();
        var row = h.List.Snapshot().Rows.First(r => r.HasPageAction);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Inspector.Handler = async (request, epoch) => { reached.TrySetResult(); await release.Task; return global::Workspace.Tests.PdfPreview.PreviewInspector.Page(request, epoch, h.Inspector.PageCountOverride); };
        var pending = h.List.OpenPageAsync(row, async request => Assert.False(await request.GoToPageAsync()));
        await reached.Task.WaitAsync(DiagnosticTestHost.Bound); h.List.Cancel();
        Assert.True(h.List.Busy); Assert.False(pending.IsCompleted); Assert.Equal(PdfPreviewStatus.Cancelled, h.Compile.Preview.State.Status);
        release.TrySetResult(); var result = await pending.WaitAsync(DiagnosticTestHost.Bound);
        Assert.Equal(0, h.ForeignCompileNotifications);
        Assert.Equal(DiagnosticActionCode.Cancelled, result.Code); Assert.Null(h.Compile.Preview.State.Frame);
    });

    [Fact] public void A_newer_shared_PDF_selection_is_not_cancelled_by_the_old_diagnostic_request() => Run(async () =>
    {
        await using var h = await DiagnosticTestHost.Open(); await h.NarrowSections(); var row = h.List.Snapshot().Rows.First(r => r.HasPageAction);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Inspector.Handler = async (request, epoch) => { reached.TrySetResult(); await release.Task; return global::Workspace.Tests.PdfPreview.PreviewInspector.Page(request, epoch, h.Inspector.PageCountOverride); };
        var pending = h.List.OpenPageAsync(row, async request => Assert.False(await request.GoToPageAsync())); await reached.Task.WaitAsync(DiagnosticTestHost.Bound);
        Assert.True(h.Compile.Preview.GoToPage(2)); long newer = h.Compile.Preview.State.Generation;
        h.List.Cancel(); Assert.Equal(newer, h.Compile.Preview.State.Generation); Assert.NotEqual(PdfPreviewStatus.Cancelled, h.Compile.Preview.State.Status);
        Assert.True(h.List.Busy); release.TrySetResult(); await pending.WaitAsync(DiagnosticTestHost.Bound);
        Assert.Equal(0, h.ForeignCompileNotifications);
        Assert.Equal(1, h.Compile.Preview.State.PageIndex); Assert.Equal(PdfPreviewStatus.Available, h.Compile.Preview.State.Status);
    });

    [Fact] public void A_foreign_callback_fault_after_revocation_does_not_replace_the_recorded_cancellation() => Run(async () =>
    {
        await using var h = await DiagnosticTestHost.Open(); await h.Analyze(); var row = h.List.Snapshot().Rows.First(r => r.HasSourceAction);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = h.List.OpenSourceAsync(row, async _ => { reached.TrySetResult(); await release.Task; throw new IOException("untrusted foreign failure after cancellation"); });
        await reached.Task.WaitAsync(DiagnosticTestHost.Bound); h.List.Cancel(); Assert.True(h.List.Busy); Assert.False(pending.IsCompleted);
        release.TrySetResult(); var result = await pending.WaitAsync(DiagnosticTestHost.Bound);
        Assert.Equal(DiagnosticActionCode.Cancelled, result.Code); Assert.DoesNotContain("untrusted", result.Notice); Assert.False(h.List.Busy);
    });

    [Fact] public void Foreign_notifications_are_dispatched_and_actual_dispatch_settlement_is_retained() => Run(async () =>
    {
        await using var h = await DiagnosticTestHost.Open(); await h.Analyze();
        var dispatchReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var dispatchRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var list = new DiagnosticListSession(h.Workspace, h.Analysis, h.Navigator, h.Compile, dispatch: async action =>
        { dispatchReached.TrySetResult(); await dispatchRelease.Task; await h.Dispatch(action); });
        var row = list.Snapshot().Rows.First(r => r.HasSourceAction);
        var callbackReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var callbackRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = list.OpenSourceAsync(row, async _ => { callbackReached.TrySetResult(); await callbackRelease.Task; callbackDone.TrySetResult(); });
        await callbackReached.Task.WaitAsync(DiagnosticTestHost.Bound);
        await Task.Run(h.Analysis.Cancel); await dispatchReached.Task.WaitAsync(DiagnosticTestHost.Bound);
        callbackRelease.TrySetResult(); await callbackDone.Task.WaitAsync(DiagnosticTestHost.Bound);
        Assert.True(list.Busy); Assert.False(pending.IsCompleted);
        dispatchRelease.TrySetResult(); Assert.Equal(DiagnosticActionCode.Stale, (await pending.WaitAsync(DiagnosticTestHost.Bound)).Code);
        Assert.False(list.Busy); Assert.All(list.Snapshot().Rows, r => Assert.False(r.IsCurrent));
    });

    [Fact] public void Disposing_view_during_actual_callback_suppresses_late_notifications_and_does_not_dispose_owners() => Run(async () =>
    {
        await using var h = await DiagnosticTestHost.Open(); await h.Analyze(); var row = h.List.Snapshot().Rows.First(r => r.HasSourceAction);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int notifications = 0; h.List.Changed += () => notifications++;
        var pending = h.List.OpenSourceAsync(row, async _ => { reached.TrySetResult(); await release.Task; }); await reached.Task.WaitAsync(DiagnosticTestHost.Bound);
        h.List.Dispose(); int before = notifications; Assert.True(h.List.Busy); release.TrySetResult(); await pending.WaitAsync(DiagnosticTestHost.Bound);
        Assert.Equal(before, notifications); Assert.Empty(h.List.Snapshot().Rows); Assert.True(h.Workspace.IsOpen); Assert.False(h.Compile.Disposed); Assert.IsType<AnalysisState.Completed>(h.Analysis.State);
    });
}
