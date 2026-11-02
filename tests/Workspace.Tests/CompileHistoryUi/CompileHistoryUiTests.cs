using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using Client.Compilation;
using Client.Compilation.Download;
using Client.Compilation.History;
using Client.Compilation.History.Index;
using Client.Compilation.Lifecycle;
using Client.Compilation.Native;
using Client.Compilation.Presentation;
using Client.Pages;
using Client.Pdf;
using Client.Pdf.Preview;
using Client.RuntimeAssets;
using Client.Workspace;
using Client.Workspace.Domain;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.CompileHistoryUi;

// CONTRACT only: real workspace/B1/B3/compile presentation + SEALED N14 and N20
// stores, finite in-memory IO and synthetic accepted worker bytes, not native PDF proof.
public sealed class CompileHistoryUiTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(4);
    private static ArtifactHistoryBudget Artifacts(int deadline = 10000) => new(new(32, 1048576, 8388608, 65536), 1048576, 65536, new(new(2, 256, 1048576, 2097152, 65536, 256, 256), 16384, 16, 32, deadline));
    private static Func<Action, Task> Dispatcher()
    {
        var owner = SynchronizationContext.Current!;
        return action => { var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); owner.Post(_ => { try { action(); done.SetResult(); } catch (Exception e) { done.SetException(e); } }, null); return done.Task; };
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public CompilationTestWorkspace F = null!;
        public readonly Configuration Config = new();
        public readonly ArtifactIo Art = new(); public readonly IndexIo Keys = new();
        public RevisionReads Revisions = null!;
        public CompileWorkspaceSession Compile = null!; public CompileHistoryUiSession Ui = null!;
        public static async Task<Fixture> Create(int deadline = 10000, bool configure = true, bool headNull = false, bool independentPdf = false)
        {
            var h = new Fixture { F = await CompilationTestWorkspace.Open(headNull: headNull) };
            h.Revisions = new(h.F.Repo);
            h.Compile = new(h.F.Workspace, h.Config); h.Compile.AttachRenderer(Dispatcher());
            h.Ui = new(h.F.Workspace, h.Compile, new(h.Art, h.Revisions, NativeRuntimePackage.Pdf, NativeRuntimePackage.Adapter), new(h.Keys), Artifacts(deadline), new(64, 65536, 10000), independentPdf ? h.Config.Inspector : null);
            if (headNull) Assert.True(h.F.Workspace.CreateFile("main.tex", "Unsaved synthetic source."));
            else Assert.True(h.F.Workspace.RenameFile(h.F.Main, "main.tex"));
            if (configure) { await h.Compile.RefreshToolsAsync(); h.Compile.SelectMain(h.F.Main); h.Compile.SelectEngine("contract"); }
            return h;
        }
        public async Task CompileContract()
        {
            var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed() { if (Compile.DisplayedOutput is not null) shown.TrySetResult(); }
            Compile.Changed += Changed;
            try
            {
                await Compile.CompileAsync(); var worker = await Config.Workers.Next(); worker.Reply.SetResult(worker.Output());
                Assert.Equal(CompileLifecycleReason.Succeeded, (await Compile.LastTicket!.Completion.WaitAsync(Bound)).Reason);
                await shown.Task.WaitAsync(Bound); Assert.NotNull(Compile.DisplayedOutput);
            }
            finally { Compile.Changed -= Changed; }
        }
        public async Task Switch()
        {
            var project = new Project(ProjectId.New(), "other fixture", null);
            var revision = F.Repo.Revision! with { Id = RevisionId.New(), ProjectId = project.Id };
            project = project with { HeadRevisionId = revision.Id }; F.Repo.Projects[project.Id] = (project, revision); F.Repo.Revisions[revision.Id] = revision;
            await F.Workspace.CloseAsync(CancellationToken.None); await F.Workspace.OpenAsync(project.Id, CancellationToken.None);
            Assert.Null(Compile.MainFile);
        }
        public async ValueTask DisposeAsync()
        {
            Art.Release.TrySetResult(true); Keys.Release.TrySetResult(true); Revisions.Release.TrySetResult(true);
            await Ui.DisposeAsync(); await Ui.WhenSettled.WaitAsync(Bound);
            await Compile.DisposeAsync(); await Compile.Owner!.WhenSettled.WaitAsync(Bound); await F.DisposeAsync();
        }
    }
    [Fact] public void Explicit_save_uses_displayed_accepted_output_without_source_save_then_historical_open() => Run(async () =>
    {
        await using var h = await Fixture.Create(); Assert.False(h.Ui.CanSave); await h.CompileContract();
        var head = h.F.Workspace.Project!.HeadRevisionId; int commits = h.F.Repo.Commits; Assert.True(h.Ui.CanSave);
        await h.Ui.SaveDisplayedAsync(); await h.Ui.WhenSettled.WaitAsync(Bound);
        Assert.Equal(head, h.F.Workspace.Project.HeadRevisionId); Assert.Equal(commits, h.F.Repo.Commits);
        var key = Assert.Single(h.Ui.View.Entries); Assert.Equal(1, h.Art.Publishes); Assert.Null(h.Ui.RecoverableKey);
        await h.Ui.OpenAsync(key); await h.Ui.Preview.WhenSettled.WaitAsync(Bound);
        Assert.Equal("HistoricalOnly", h.Ui.View.Opened!.Classification); Assert.Equal("Saved PDF", h.Ui.Preview.State.DisplayName);
        Assert.Equal(PdfDisplayProvenance.CallerSaysHistorical, h.Ui.Preview.State.Provenance);
        Assert.Equal(PdfPreviewStatus.Busy, h.Ui.Preview.State.Status); Assert.True(h.Ui.Preview.Retry()); await h.Ui.Preview.WhenSettled.WaitAsync(Bound);
        Assert.Equal(0, h.Config.Inspector.Disposals); Assert.Equal(1, h.Art.Publishes);
    });
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Saved_PDF_is_retained_when_index_fails_and_explicit_retry_reconciles_without_second_persist(bool uncertain) => Run(async () =>
    {
        await using var h = await Fixture.Create(); await h.CompileContract(); h.Keys.Fail = !uncertain; h.Keys.UnknownCommit = uncertain;
        await h.Ui.SaveDisplayedAsync(); await h.Ui.WhenSettled.WaitAsync(Bound); Assert.NotNull(h.Ui.RecoverableKey); Assert.True(h.Ui.CanRetry);
        Assert.Contains("PDF was saved", h.Ui.View.Notice); Assert.Contains("listing update", h.Ui.View.Notice); Assert.Equal(1, h.Art.Publishes);
        h.Keys.Fail = false; h.Keys.UnknownCommit = false; await h.Ui.RetryPublicationAsync(); await h.Ui.WhenSettled.WaitAsync(Bound);
        Assert.Single(h.Ui.View.Entries); Assert.Null(h.Ui.RecoverableKey); Assert.Equal(1, h.Art.Publishes); Assert.Single(h.Keys.Rows);
    });
    [Fact] public void Uncertain_artifact_commit_requires_fresh_explicit_read_before_index_publication() => Run(async () =>
    {
        await using var h = await Fixture.Create(500); await h.CompileContract(); h.Art.HoldPublish = true;
        Exception? original = null;
        try
        {
            var saving = h.Ui.SaveDisplayedAsync(); await h.Art.Entered.Task.WaitAsync(Bound); await saving.WaitAsync(Bound);
            Assert.True(h.Ui.PersistenceUncertain); Assert.True(h.Ui.Busy); Assert.False(h.Art.Release.Task.IsCompleted);
            Assert.Empty(h.Keys.Rows); Assert.False(h.Ui.CanRetry);
        }
        catch (Exception error) { original = error; throw; }
        finally { h.Art.Release.TrySetResult(true); try { await h.Ui.WhenSettled.WaitAsync(Bound); } catch (Exception cleanup) when (original is not null) { throw new AggregateException(original, cleanup); } }
        h.Art.HoldPublish = false; await h.Ui.RetryPublicationAsync(); await h.Ui.WhenSettled.WaitAsync(Bound);
        Assert.Single(h.Ui.View.Entries); Assert.Equal(1, h.Art.Publishes); Assert.False(h.Ui.PersistenceUncertain);
    });
    [Fact] public void Wrong_project_and_corrupt_pointer_open_never_reuses_previous_PDF() => Run(async () =>
    {
        await using var h = await Fixture.Create(); await h.CompileContract(); await h.Ui.SaveDisplayedAsync(); await h.Ui.WhenSettled.WaitAsync(Bound); var key = Assert.Single(h.Ui.View.Entries);
        await h.Ui.OpenAsync(key); await h.Ui.Preview.WhenSettled.WaitAsync(Bound); Assert.NotNull(h.Ui.View.Opened);
        await h.Ui.OpenAsync(key with { ProjectId = ProjectId.New() }); await h.Ui.WhenSettled.WaitAsync(Bound); Assert.Null(h.Ui.View.Opened); Assert.Null(h.Ui.Preview.State.DocumentSha256);
        await h.Ui.OpenAsync(key with { ContentDigest = new(new string('f', 64)) }); await h.Ui.WhenSettled.WaitAsync(Bound); Assert.Null(h.Ui.View.Opened); Assert.Contains("Corrupt", h.Ui.View.Notice);
    });
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Old_project_list_or_open_cannot_publish_after_switch_and_retains_actual_admission(bool open) => Run(async () =>
    {
        await using var h = await Fixture.Create(); await h.CompileContract(); await h.Ui.SaveDisplayedAsync(); await h.Ui.WhenSettled.WaitAsync(Bound); var key = Assert.Single(h.Ui.View.Entries);
        h.Keys.HoldRead = !open; h.Art.HoldRead = open; Exception? original = null;
        try
        {
            var pending = open ? h.Ui.OpenAsync(key) : h.Ui.RefreshAsync(); await (open ? h.Art.Entered.Task : h.Keys.Entered.Task).WaitAsync(Bound);
            await h.Switch(); Assert.True(h.Ui.Busy); Assert.Empty(h.Ui.View.Entries); Assert.Null(h.Ui.View.Opened);
            Assert.False((open ? h.Art.Release.Task : h.Keys.Release.Task).IsCompleted);
            h.Art.Release.TrySetResult(true); h.Keys.Release.TrySetResult(true); await pending.WaitAsync(Bound); await h.Ui.WhenSettled.WaitAsync(Bound);
            Assert.Empty(h.Ui.View.Entries); Assert.Null(h.Ui.View.Opened); Assert.Null(h.Ui.Preview.State.DocumentSha256); Assert.False(h.Ui.CanSave);
        }
        catch (Exception error) { original = error; throw; }
        finally { h.Art.Release.TrySetResult(true); h.Keys.Release.TrySetResult(true); try { await h.Ui.WhenSettled.WaitAsync(Bound); } catch (Exception cleanup) when (original is not null) { throw new AggregateException(original, cleanup); } }
    });
    [Fact] public void Late_old_project_save_keeps_recoverable_key_hidden_from_new_project() => Run(async () =>
    {
        await using var h = await Fixture.Create(); await h.CompileContract(); var oldProject = h.F.Workspace.Project!.Id; h.Art.HoldPublish = true;
        try
        {
            var pending = h.Ui.SaveDisplayedAsync(); await h.Art.Entered.Task.WaitAsync(Bound); await h.Switch();
            h.Art.Release.TrySetResult(true); await pending.WaitAsync(Bound); await h.Ui.WhenSettled.WaitAsync(Bound);
            Assert.Empty(h.Ui.View.Entries); Assert.Null(h.Ui.View.Opened); Assert.Null(h.Ui.RecoverableKey); Assert.Empty(h.Keys.Rows);
            await h.F.Workspace.CloseAsync(CancellationToken.None); await h.F.Workspace.OpenAsync(oldProject, CancellationToken.None); Assert.Null(h.Compile.MainFile);
            Assert.NotNull(h.Ui.RecoverableKey); await h.Ui.RetryPublicationAsync(); await h.Ui.WhenSettled.WaitAsync(Bound); Assert.Single(h.Ui.View.Entries);
        }
        finally { h.Art.Release.TrySetResult(true); }
    });
    [Fact] public void Dispose_during_actual_write_is_prompt_and_does_not_dispose_borrowed_objects() => Run(async () =>
    {
        await using var h = await Fixture.Create(); await h.CompileContract(); h.Art.HoldWrite = true;
        try
        {
            var pending = h.Ui.SaveDisplayedAsync(); await h.Art.Entered.Task.WaitAsync(Bound); await h.Ui.DisposeAsync().AsTask().WaitAsync(Bound);
            await pending.WaitAsync(Bound); Assert.False(h.Art.Release.Task.IsCompleted); Assert.False(h.Ui.WhenSettled.IsCompleted); Assert.Equal(0, h.Art.Disposals);
            Assert.Equal(0, h.Config.Inspector.Disposals); Assert.False(h.Compile.Disposed); Assert.True(h.F.Workspace.EditText(h.F.Main, "borrowed editing remains"));
            h.Art.Release.TrySetResult(true); await h.Ui.WhenSettled.WaitAsync(Bound); Assert.Empty(h.Ui.View.Entries); Assert.Null(h.Ui.View.Opened); Assert.Equal(1, h.Art.Disposals); Assert.Equal(1, h.Keys.Disposals);
        }
        finally { h.Art.Release.TrySetResult(true); }
    });
    [Fact] public void Razor_panel_labels_separate_historical_display_and_explicit_recovery_actions() => Run(async () =>
    {
        await using var h = await Fixture.Create(); await h.CompileContract(); h.Keys.Fail = true; await h.Ui.SaveDisplayedAsync(); await h.Ui.WhenSettled.WaitAsync(Bound);
        string html = await Render(h.Ui); Assert.Contains("Save PDF to history", html); Assert.Contains("Refresh saved PDFs", html); Assert.Contains("Retry listing update", html); Assert.Contains("PDF was saved", html);
        h.Keys.Fail = false; await h.Ui.RetryPublicationAsync(); await h.Ui.WhenSettled.WaitAsync(Bound);
        var key = Assert.Single(h.Ui.View.Entries); html = await Render(h.Ui); Assert.Contains("Saved PDF 1", html); Assert.DoesNotContain(key.RecordId.ToString("N"), html);
        await h.Ui.OpenAsync(key); await h.Ui.Preview.WhenSettled.WaitAsync(Bound);
        html = await Render(h.Ui); Assert.Contains("data-testid=\"historical-pdf-label\">Saved PDF</h3>", html); Assert.Contains("may differ from the current source", html); Assert.Contains("Provenance: HistoricalOnly", html); Assert.Contains("<details data-testid=\"saved-pdf-details\">", html); Assert.Contains("durable compile settings are not available", html); Assert.Contains("Open saved PDF", html); Assert.DoesNotContain("PDF up to date", html);
    });
    [Fact] public void Late_configuration_is_used_by_Open_without_eager_preview_churn_or_borrowed_disposal() => Run(async () =>
    {
        await using var h = await Fixture.Create(configure: false);
        Assert.Null(h.Compile.ConfiguredPorts); var initialPreview = h.Ui.Preview;
        await h.Compile.RefreshToolsAsync(); h.Compile.SelectMain(h.F.Main); h.Compile.SelectEngine("contract");
        Assert.Same(initialPreview, h.Ui.Preview); // Same project: readiness alone does not replace an active view.
        Assert.Same(h.Config.Inspector, h.Compile.ConfiguredPorts!.Inspector);
        await h.CompileContract(); await h.Ui.SaveDisplayedAsync(); await h.Ui.WhenSettled.WaitAsync(Bound);
        int before = h.Config.Inspector.Calls; await h.Ui.OpenAsync(Assert.Single(h.Ui.View.Entries)); await h.Ui.Preview.WhenSettled.WaitAsync(Bound);
        Assert.NotSame(initialPreview, h.Ui.Preview); Assert.True(h.Config.Inspector.Calls > before); Assert.Equal(PdfPreviewStatus.Busy, h.Ui.Preview.State.Status);
        var openedPreview = h.Ui.Preview; await h.Compile.RefreshToolsAsync(); Assert.Same(openedPreview, h.Ui.Preview);
        Assert.Equal("HistoricalOnly", h.Ui.View.Opened!.Classification);
        await h.Ui.DisposeAsync(); Assert.Equal(0, h.Config.Inspector.Disposals); Assert.False(h.Compile.Disposed);
    });
    private static async Task<HistoricalArtifactKey> SavedPdf(Fixture h)
    {
        Assert.Equal(CommitStatus.Saved, (await h.F.Workspace.SaveAsync(CancellationToken.None)).Status);
        await h.CompileContract(); await h.Ui.SaveDisplayedAsync(); await h.Ui.WhenSettled.WaitAsync(Bound);
        var key = Assert.Single(h.Ui.View.Entries); await h.Ui.OpenAsync(key); await h.Ui.Preview.WhenSettled.WaitAsync(Bound);
        Assert.Equal(RevisionFileAssociationStatus.Unassociated, h.Ui.View.SourceAssociation!.Status);
        Assert.False(h.Ui.View.ComparisonRequested); Assert.Equal(0, h.Revisions.Reads); return key;
    }
    private static void RaiseWorkspace(WorkspaceCoordinator workspace) => typeof(WorkspaceCoordinator)
        .GetMethod("RaiseWorkspaceChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(workspace, null);
    private static void SetWorkspace(WorkspaceCoordinator workspace, string property, object? value) => typeof(WorkspaceCoordinator).GetProperty(property)!.SetValue(workspace, value);
    private static void RaiseSaved(WorkspaceCoordinator workspace, ProjectRevision revision) => typeof(WorkspaceCoordinator)
        .GetMethod("RaiseRevisionCommitted", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(workspace, [workspace.SessionId, revision]);

    [Fact] public void Explicit_comparison_verifies_saved_manifest_not_dirty_working_text_or_settings() => Run(async () =>
    {
        await using var h = await Fixture.Create(); var key = await SavedPdf(h);
        var opened = h.Ui.View.Opened!; var preview = h.Ui.Preview; var pdf = opened.CopyPdf(); var head = h.F.Workspace.Project!.HeadRevisionId;
        Assert.True(h.F.Workspace.EditText(h.F.Main, "different UNSAVED working text"));
        string before = h.F.State(); int commits = h.F.Repo.Commits, published = h.Art.Publishes;
        var writes = (h.Art.Acquires, h.Art.Stages, h.Art.Writes, h.Art.Publishes, h.Art.Discards, h.Keys.Exchanges);
        await h.Ui.CompareWithCurrentSavedSourceAsync(); await h.Ui.WhenSettled.WaitAsync(Bound);
        Assert.Equal(RevisionFileAssociationStatus.VerifiedFileManifest, h.Ui.View.SourceAssociation!.Status);
        Assert.Equal(head, h.Ui.View.SourceAssociation.RevisionId); Assert.Equal(head, h.Ui.View.ComparedSavedRevision);
        Assert.Equal(RuntimeAssetStatus.Ready, h.Ui.View.ComparisonReadStatus); Assert.True(h.Ui.View.WorkingTextWasDirty);
        Assert.True(h.Ui.HasUnsavedSourceChanges); Assert.False(h.Ui.View.AssociationStale); Assert.False(h.Ui.View.ComparingSavedSource);
        Assert.Equal(before, h.F.State()); Assert.Equal(commits, h.F.Repo.Commits); Assert.Equal(published, h.Art.Publishes);
        Assert.Equal(writes, (h.Art.Acquires, h.Art.Stages, h.Art.Writes, h.Art.Publishes, h.Art.Discards, h.Keys.Exchanges));
        Assert.Equal((key.ProjectId, head!.Value), Assert.Single(h.Revisions.Requests));
        Assert.Same(opened, h.Ui.View.Opened); Assert.Same(preview, h.Ui.Preview); Assert.Equal(pdf, opened.CopyPdf());
        Assert.Equal("HistoricalOnly", opened.Classification); Assert.Null(opened.OriginalRequestedRevision);
        Assert.Equal(PdfDisplayProvenance.CallerSaysHistorical, preview.State.Provenance);
        string html = await Render(h.Ui);
        Assert.Contains("data-association-status=\"VerifiedFileManifest\"", html); Assert.Contains("data-association-scope=\"RevisionFileManifestOnly\"", html);
        Assert.Contains("data-compared-revision=\"" + head.Value + "\"", html); Assert.Contains("data-dirty-working-source=\"true\"", html);
        Assert.Contains("Compile-setting equality is unavailable", html); Assert.Contains("not current working text", html);
        Assert.Contains(key.RecordId.ToString("N"), html); Assert.Contains(opened.ContentIdentity.OutputHash.Hex, html);
        Assert.Contains("Captured source base head", html); Assert.Contains("main.tex", html); Assert.Contains("synthetic-engine", html);
        Assert.DoesNotContain("PDF up to date", html);
    });

    [Fact] public void New_saved_source_marks_old_result_stale_then_explicit_comparison_reports_mismatch() => Run(async () =>
    {
        await using var h = await Fixture.Create(); await SavedPdf(h); await h.Ui.CompareWithCurrentSavedSourceAsync(); await h.Ui.WhenSettled.WaitAsync(Bound);
        var captured = h.Ui.View.Opened!; var preview = h.Ui.Preview; var oldHead = h.Ui.View.ComparedSavedRevision;
        Assert.True(h.F.Workspace.EditText(h.F.Main, "new SAVED source"));
        Assert.True(h.Ui.View.AssociationStale); Assert.Equal(RevisionFileAssociationStatus.VerifiedFileManifest, h.Ui.View.SourceAssociation!.Status);
        Assert.Equal(CommitStatus.Saved, (await h.F.Workspace.SaveAsync(CancellationToken.None)).Status);
        var newHead = h.F.Workspace.Project!.HeadRevisionId; Assert.NotEqual(oldHead, newHead); Assert.Equal(oldHead, h.Ui.View.ComparedSavedRevision);
        int commits = h.F.Repo.Commits; await h.Ui.CompareWithCurrentSavedSourceAsync(); await h.Ui.WhenSettled.WaitAsync(Bound);
        Assert.Equal(RevisionFileAssociationStatus.Mismatch, h.Ui.View.SourceAssociation!.Status); Assert.Equal(newHead, h.Ui.View.ComparedSavedRevision);
        Assert.False(h.Ui.View.AssociationStale); Assert.Equal(commits, h.F.Repo.Commits);
        Assert.Same(captured, h.Ui.View.Opened); Assert.Equal(oldHead, captured.OriginalSnapshot.BaseHeadRevisionId);
        Assert.Same(preview, h.Ui.Preview); Assert.Equal(PdfDisplayProvenance.CallerSaysHistorical, preview.State.Provenance);
    });

    [Theory] [InlineData("damaged", RevisionFileAssociationStatus.MissingOrCorrupt)]
    [InlineData("unavailable", RevisionFileAssociationStatus.Unavailable)] [InlineData("wrong-project", RevisionFileAssociationStatus.WrongProject)]
    public void Typed_revision_failures_are_not_relabelled_as_verified_or_mismatch(string mode, RevisionFileAssociationStatus expected) => Run(async () =>
    {
        await using var h = await Fixture.Create(); await SavedPdf(h); var preview = h.Ui.Preview;
        h.Revisions.Throw = mode == "unavailable";
        h.Revisions.Mutate = value => mode == "damaged" ? RevisionManifest.Damaged(value.Revision, "Synthetic missing content")
            : mode == "wrong-project" ? RevisionManifest.Ok(value.Revision with { ProjectId = ProjectId.New() }) : value;
        await h.Ui.CompareWithCurrentSavedSourceAsync(); await h.Ui.WhenSettled.WaitAsync(Bound);
        Assert.Equal(expected, h.Ui.View.SourceAssociation!.Status); Assert.Equal(RuntimeAssetStatus.Ready, h.Ui.View.ComparisonReadStatus);
        Assert.Equal(h.F.Workspace.Project!.HeadRevisionId, h.Ui.View.ComparedSavedRevision); Assert.Same(preview, h.Ui.Preview);
        Assert.Contains("data-association-status=\"" + expected + "\"", await Render(h.Ui));
    });

    [Fact] public void No_saved_head_is_explicitly_unassociated_and_does_not_save_or_guess_a_revision() => Run(async () =>
    {
        await using var h = await Fixture.Create(headNull: true); await h.CompileContract(); await h.Ui.SaveDisplayedAsync(); await h.Ui.WhenSettled.WaitAsync(Bound);
        await h.Ui.OpenAsync(Assert.Single(h.Ui.View.Entries)); await h.Ui.Preview.WhenSettled.WaitAsync(Bound);
        Assert.True(h.Ui.CanCompareSavedSource); await h.Ui.CompareWithCurrentSavedSourceAsync(); await h.Ui.WhenSettled.WaitAsync(Bound);
        Assert.Equal(RevisionFileAssociationStatus.Unassociated, h.Ui.View.SourceAssociation!.Status); Assert.Null(h.Ui.View.ComparedSavedRevision);
        Assert.Null(h.Ui.View.Opened!.OriginalSnapshot.BaseHeadRevisionId); Assert.Null(h.F.Workspace.Project!.HeadRevisionId);
        Assert.Equal(0, h.F.Repo.Commits); Assert.Equal(0, h.Revisions.Reads); Assert.True(h.Ui.View.ComparisonRequested);
        Assert.Contains("no current saved source revision", h.Ui.View.ComparisonNotice);
    });

    [Fact] public void Comparison_works_in_real_readonly_workspace_without_current_compile_readiness() => Run(async () =>
    {
        await using var h = await Fixture.Create(); var key = await SavedPdf(h); var bytes = h.Ui.View.Opened!.CopyPdf(); var project = key.ProjectId;
        await h.F.Workspace.CloseAsync(CancellationToken.None); h.F.Lock.AllowAcquire = false;
        await h.F.Workspace.OpenAsync(project, CancellationToken.None); Assert.True(h.F.Workspace.IsReadOnly); Assert.Null(h.F.Workspace.LockId);
        h.Config.Ready = false; await h.Compile.RefreshToolsAsync(); Assert.False(h.Compile.Readiness.Ready);
        await h.Ui.OpenAsync(key); await h.Ui.Preview.WhenSettled.WaitAsync(Bound); Assert.True(h.Ui.CanCompareSavedSource);
        string before = h.F.State(); int commits = h.F.Repo.Commits;
        await h.Ui.CompareWithCurrentSavedSourceAsync(); await h.Ui.WhenSettled.WaitAsync(Bound);
        Assert.Equal(RevisionFileAssociationStatus.VerifiedFileManifest, h.Ui.View.SourceAssociation!.Status);
        Assert.Equal(before, h.F.State()); Assert.Equal(commits, h.F.Repo.Commits); Assert.Equal(bytes, h.Ui.View.Opened!.CopyPdf());
        Assert.Equal("HistoricalOnly", h.Ui.View.Opened.Classification); Assert.False(h.Ui.CanSave);
    });

    [Fact] public void Failed_artifact_revalidation_keeps_opened_owned_PDF_but_reports_unavailable_association() => Run(async () =>
    {
        await using var h = await Fixture.Create(); var key = await SavedPdf(h); var opened = h.Ui.View.Opened!; var preview = h.Ui.Preview;
        h.Art.Catalog.Remove(key.RecordId); await h.Ui.CompareWithCurrentSavedSourceAsync(); await h.Ui.WhenSettled.WaitAsync(Bound);
        Assert.Equal(RuntimeAssetStatus.Incomplete, h.Ui.View.ComparisonReadStatus); Assert.Equal(RevisionFileAssociationStatus.Unavailable, h.Ui.View.SourceAssociation!.Status);
        Assert.Same(opened, h.Ui.View.Opened); Assert.Same(preview, h.Ui.Preview); Assert.NotEmpty(opened.CopyPdf());
        Assert.Contains("could not be revalidated", h.Ui.View.ComparisonNotice); Assert.Equal(0, h.Revisions.Reads);
    });

    [Theory] [InlineData("generation-aba")] [InlineData("tree-aba")] [InlineData("head-aba")]
    [InlineData("lock-aba")] [InlineData("same-files-new-head")]
    public void Actual_held_manifest_result_never_publishes_after_source_identity_change_or_ABA(string change) => Run(async () =>
    {
        await using var h = await Fixture.Create(); await SavedPdf(h); var artifact = h.Ui.View.Opened!; var preview = h.Ui.Preview;
        var project = h.F.Workspace.Project!; var revision = h.F.Workspace.CurrentRevision!; var head = project.HeadRevisionId;
        h.Revisions.Hold = true;
        try
        {
            var pending = h.Ui.CompareWithCurrentSavedSourceAsync(); await h.Revisions.Entered.Task.WaitAsync(Bound);
            Assert.True(h.Ui.Busy); Assert.True(h.Ui.View.ComparingSavedSource); Assert.Equal(head, h.Ui.View.ComparedSavedRevision);
            if (change == "generation-aba")
            {
                var text = h.F.Workspace.Files.Single(f => f.Id == h.F.Main).Content!;
                Assert.True(h.F.Workspace.EditText(h.F.Main, text + " changed")); Assert.True(h.F.Workspace.EditText(h.F.Main, text));
            }
            else if (change is "tree-aba" or "same-files-new-head")
            {
                var other = h.F.Workspace.Files.Single(f => f.RelativePath == "chapter.tex").Id;
                Assert.True(h.F.Workspace.RenameFile(other, "renamed.tex")); Assert.True(h.F.Workspace.RenameFile(other, "chapter.tex"));
                if (change == "same-files-new-head")
                {
                    Assert.Equal(CommitStatus.Saved, (await h.F.Workspace.SaveAsync(CancellationToken.None)).Status);
                    Assert.NotEqual(head, h.F.Workspace.Project!.HeadRevisionId);
                    Assert.Equal(revision.Files.OrderBy(f => f.Id.ToString()), h.F.Workspace.CurrentRevision!.Files.OrderBy(f => f.Id.ToString()));
                }
            }
            else if (change == "head-aba")
            {
                // Model verified external head notifications with otherwise
                // identical state; never add a rewind API to production.
                var next = revision with { Id = RevisionId.New(), ParentRevisionId = revision.Id };
                h.F.Repo.Revisions[next.Id] = next;
                SetWorkspace(h.F.Workspace, nameof(WorkspaceCoordinator.Project), project with { HeadRevisionId = next.Id });
                SetWorkspace(h.F.Workspace, nameof(WorkspaceCoordinator.CurrentRevision), next); RaiseSaved(h.F.Workspace, next);
                SetWorkspace(h.F.Workspace, nameof(WorkspaceCoordinator.Project), project);
                SetWorkspace(h.F.Workspace, nameof(WorkspaceCoordinator.CurrentRevision), revision); RaiseSaved(h.F.Workspace, revision);
                Assert.Equal(head, h.F.Workspace.Project!.HeadRevisionId);
            }
            else
            {
                var originalLock = h.F.Workspace.LockId;
                SetWorkspace(h.F.Workspace, nameof(WorkspaceCoordinator.LockId), "other-lock"); RaiseWorkspace(h.F.Workspace);
                SetWorkspace(h.F.Workspace, nameof(WorkspaceCoordinator.LockId), originalLock); RaiseWorkspace(h.F.Workspace);
                Assert.Equal(originalLock, h.F.Workspace.LockId);
            }
            Assert.True(h.Ui.View.AssociationStale); Assert.False(h.Ui.View.ComparingSavedSource); Assert.Same(preview, h.Ui.Preview);
            h.Revisions.Release.TrySetResult(true); await pending.WaitAsync(Bound); await h.Ui.WhenSettled.WaitAsync(Bound);
            Assert.True(h.Ui.View.AssociationStale); Assert.Null(h.Ui.View.SourceAssociation); Assert.Same(artifact, h.Ui.View.Opened);
            h.Revisions.Hold = false; await h.Ui.CompareWithCurrentSavedSourceAsync(); await h.Ui.WhenSettled.WaitAsync(Bound);
            Assert.Equal(RevisionFileAssociationStatus.VerifiedFileManifest, h.Ui.View.SourceAssociation!.Status);
            Assert.False(h.Ui.View.AssociationStale); Assert.Equal(h.F.Workspace.Project!.HeadRevisionId, h.Ui.View.ComparedSavedRevision);
        }
        finally { h.Revisions.Release.TrySetResult(true); }
    });

    [Fact] public void Capture_precedes_observer_notification_so_reentrant_source_change_does_not_retarget_comparison() => Run(async () =>
    {
        await using var h = await Fixture.Create(); await SavedPdf(h); bool changed = false;
        void Changed() { if (!changed && h.Ui.View.ComparingSavedSource) { changed = true; Assert.True(h.F.Workspace.EditText(h.F.Main, "reentrant mutation")); } }
        h.Ui.Changed += Changed;
        try { await h.Ui.CompareWithCurrentSavedSourceAsync(); await h.Ui.WhenSettled.WaitAsync(Bound); }
        finally { h.Ui.Changed -= Changed; }
        Assert.True(changed); Assert.True(h.Ui.View.AssociationStale); Assert.Equal(0, h.Revisions.Reads); Assert.NotNull(h.Ui.View.Opened);
    });

    [Theory] [InlineData("project")] [InlineData("project-aba")] [InlineData("dispose")]
    public void Project_switch_or_disposal_suppresses_held_comparison_and_retains_actual_IO_admission(string change) => Run(async () =>
    {
        await using var h = await Fixture.Create(); await SavedPdf(h); h.Revisions.Hold = true;
        var oldProject = h.F.Workspace.Project!.Id; var oldSession = h.F.Workspace.SessionId;
        try
        {
            var pending = h.Ui.CompareWithCurrentSavedSourceAsync(); await h.Revisions.Entered.Task.WaitAsync(Bound);
            if (change == "dispose") await h.Ui.DisposeAsync();
            else
            {
                await h.Switch();
                if (change == "project-aba")
                {
                    await h.F.Workspace.CloseAsync(CancellationToken.None); await h.F.Workspace.OpenAsync(oldProject, CancellationToken.None);
                    Assert.Equal(oldProject, h.F.Workspace.Project!.Id); Assert.NotEqual(oldSession, h.F.Workspace.SessionId);
                }
            }
            Assert.Null(h.Ui.View.Opened); Assert.Null(h.Ui.Preview.State.DocumentSha256); Assert.False(h.Ui.WhenSettled.IsCompleted);
            Assert.Equal(1, h.Revisions.Reads); Assert.False(h.Ui.CanCompareSavedSource);
            h.Revisions.Release.TrySetResult(true); await pending.WaitAsync(Bound); await h.Ui.WhenSettled.WaitAsync(Bound);
            Assert.Null(h.Ui.View.Opened); Assert.Null(h.Ui.View.SourceAssociation); Assert.Equal(1, h.Revisions.Reads);
            Assert.Equal(0, h.Config.Inspector.Disposals);
        }
        finally { h.Revisions.Release.TrySetResult(true); }
    });

    [Fact] public void Timed_out_comparison_requires_explicit_retry_after_actual_revision_read_settles() => Run(async () =>
    {
        await using var h = await Fixture.Create(500); await SavedPdf(h); h.Revisions.Hold = true;
        try
        {
            var pending = h.Ui.CompareWithCurrentSavedSourceAsync(); await h.Revisions.Entered.Task.WaitAsync(Bound); await pending.WaitAsync(Bound);
            Assert.Equal(RuntimeAssetStatus.TimedOut, h.Ui.View.ComparisonReadStatus); Assert.Equal(RevisionFileAssociationStatus.Unavailable, h.Ui.View.SourceAssociation!.Status);
            Assert.True(h.Ui.Busy); Assert.False(h.Ui.CanCompareSavedSource); Assert.False(h.Ui.WhenSettled.IsCompleted);
            await h.Ui.CompareWithCurrentSavedSourceAsync(); Assert.Equal(1, h.Revisions.Reads);
            h.Revisions.Release.TrySetResult(true); await h.Ui.WhenSettled.WaitAsync(Bound); h.Revisions.Hold = false;
            Assert.True(h.Ui.CanCompareSavedSource); await h.Ui.CompareWithCurrentSavedSourceAsync(); await h.Ui.WhenSettled.WaitAsync(Bound);
            Assert.Equal(2, h.Revisions.Reads); Assert.Equal(RevisionFileAssociationStatus.VerifiedFileManifest, h.Ui.View.SourceAssociation!.Status);
        }
        finally { h.Revisions.Release.TrySetResult(true); }
    });

    private static async Task<string> Render(CompileHistoryUiSession session)
    {
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(new NoJs()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => WebUtility.HtmlDecode((await renderer.RenderComponentAsync<CompileHistoryPanel>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(CompileHistoryPanel.Session)] = session }))).ToHtmlString()));
    }
    // The artifact store may read a verified revision, but this port MUST NOT
    // be used for source mutation, auto-save or live compile acceptance.
    private sealed class RevisionReads(IProjectRepository inner) : IProjectRepository
    {
        public int Reads;
        public bool Hold, Throw;
        public Func<RevisionManifest, RevisionManifest>? Mutate;
        public readonly List<(ProjectId Project, RevisionId Revision)> Requests = [];
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<RevisionManifest> ReadRevisionAsync(ProjectId project, RevisionId revision, CancellationToken ct)
        {
            Reads++; Requests.Add((project, revision));
            var result = await inner.ReadRevisionAsync(project, revision, CancellationToken.None);
            if (Hold) { Entered.TrySetResult(); await Release.Task; } // deliberately noncooperative
            if (Throw) throw new IOException("Synthetic unavailable revision read");
            return Mutate?.Invoke(result) ?? result;
        }
        private static Exception NoWrite() => new InvalidOperationException("Association must only read the requested verified revision.");
        public Task<ProjectSnapshot> OpenAsync(ProjectId p, CancellationToken c) => throw NoWrite();
        public Task<CommitResult> CommitAsync(ProjectCommit b, RevisionId? h, CancellationToken c) => throw NoWrite();
        public Task<HistoryPage> ReadHistoryAsync(ProjectId p, HistoryCursor? h, CancellationToken c) => throw NoWrite();
        public Task<Project> CreateProjectAsync(string n, CancellationToken c) => throw NoWrite();
        public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken c) => throw NoWrite();
        public Task DeleteProjectAsync(ProjectId p, CancellationToken c) => throw NoWrite();
        public Task<CommitResult> RepairHeadAsync(ProjectId p, RevisionId h, RevisionId n, CancellationToken c) => throw NoWrite();
        public Task<byte[]> ReadContentAsync(Client.Workspace.Domain.SHA256 h, CancellationToken c) => throw NoWrite();
        public Task<ImportPublishResult> PublishImportAsync(StagedProject s, CancellationToken c) => throw NoWrite();
        public Task<CommitResult> CreateRestoreRevisionAsync(ProjectId p, RevisionId h, ProjectRevision r, RevisionId s, string reason, CancellationToken c) => throw NoWrite();
    }
    private sealed class NoJs : IJSRuntime
    { public ValueTask<T> InvokeAsync<T>(string n, object?[]? a) => throw new NotSupportedException("Static render only"); public ValueTask<T> InvokeAsync<T>(string n, CancellationToken c, object?[]? a) => InvokeAsync<T>(n, a); }
    [Fact] public void Download_uses_opened_owned_historical_bytes_without_source_or_store_writes() => Run(async () =>
    {
        await using var h = await Fixture.Create(); var key = await SavedPdf(h); var artifact = h.Ui.View.Opened!;
        Assert.True(h.F.Workspace.EditText(h.F.Main, "later unsaved source"));
        var state = h.F.State(); var commits = h.F.Repo.Commits; var writes = h.Art.Writes; var exchanges = h.Keys.Exchanges;
        var browser = new DownloadPort(); await h.Ui.DownloadOpenedAsync(key, browser); await h.Ui.WhenSettled.WaitAsync(Bound);
        Assert.Equal(artifact.CopyPdf(), Assert.Single(browser.Files).Bytes); Assert.EndsWith(".pdf", browser.Files[0].Name);
        Assert.Equal(state, h.F.State()); Assert.Equal(commits, h.F.Repo.Commits); Assert.Equal(writes, h.Art.Writes); Assert.Equal(exchanges, h.Keys.Exchanges);
        Assert.Same(artifact, h.Ui.View.Opened); Assert.Equal(1, browser.Released); Assert.Equal("Saved PDF download started.", h.Ui.View.Notice);
    });
    [Fact] public void Historical_download_does_not_require_writer_or_current_compilation() => Run(async () =>
    {
        await using var h = await Fixture.Create(); var key = await SavedPdf(h);
        await h.F.Workspace.CloseAsync(CancellationToken.None); h.F.Lock.AllowAcquire = false;
        await h.F.Workspace.OpenAsync(key.ProjectId, CancellationToken.None); Assert.True(h.F.Workspace.IsReadOnly);
        await h.Ui.OpenAsync(key); await h.Ui.WhenSettled.WaitAsync(Bound); Assert.False(h.Ui.CanSave); Assert.True(h.Ui.CanDownloadOpened);
        var state = h.F.State(); var browser = new DownloadPort(); await h.Ui.DownloadOpenedAsync(key, browser); await h.Ui.WhenSettled.WaitAsync(Bound);
        Assert.Equal(h.Ui.View.Opened!.CopyPdf(), Assert.Single(browser.Files).Bytes); Assert.Equal(state, h.F.State());
        Assert.NotEqual(h.F.Workspace.SessionId, h.Compile.DisplayedOutput!.SnapshotIdentity.SessionId);
    });
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Late_download_preparation_after_project_switch_or_disposal_has_no_browser_commit(bool dispose) => Run(async () =>
    {
        await using var h = await Fixture.Create(); var key = await SavedPdf(h); var browser = new DownloadPort { Hold = true };
        var pending = h.Ui.DownloadOpenedAsync(key, browser); await browser.Entered.Task.WaitAsync(Bound);
        Assert.True(h.Ui.Busy); Assert.False(h.Ui.CanDownloadOpened);
        await h.Ui.DownloadOpenedAsync(key, browser); Assert.Equal(1, browser.Prepares);
        if (dispose) await h.Ui.DisposeAsync(); else await h.Switch();
        Assert.False(pending.IsCompleted); browser.Release.TrySetResult();
        await pending.WaitAsync(Bound); await h.Ui.WhenSettled.WaitAsync(Bound);
        Assert.Empty(browser.Files); Assert.Equal(1, browser.Released);
    });
    [Fact] public void Editing_source_during_download_preparation_keeps_the_historical_export_valid() => Run(async () =>
    {
        await using var h = await Fixture.Create(); var key = await SavedPdf(h); var artifact = h.Ui.View.Opened!; var browser = new DownloadPort { Hold = true };
        var pending = h.Ui.DownloadOpenedAsync(key, browser); await browser.Entered.Task.WaitAsync(Bound);
        Assert.True(h.F.Workspace.EditText(h.F.Main, "changed during historical export")); var state = h.F.State();
        browser.Release.TrySetResult(); await pending.WaitAsync(Bound); await h.Ui.WhenSettled.WaitAsync(Bound);
        Assert.Equal(artifact.CopyPdf(), Assert.Single(browser.Files).Bytes); Assert.Equal(state, h.F.State()); Assert.Same(artifact, h.Ui.View.Opened);
    });
    [Fact] public void An_old_record_button_cannot_export_a_different_opened_record() => Run(async () =>
    {
        await using var h = await Fixture.Create(); var key = await SavedPdf(h); var browser = new DownloadPort();
        await h.Ui.DownloadOpenedAsync(key with { RecordId = Guid.NewGuid() }, browser);
        Assert.Equal(0, browser.Prepares); Assert.Empty(browser.Files); Assert.True(h.Ui.CanDownloadOpened);
    });
    [Fact] public void Failed_download_preparation_releases_admission_for_an_explicit_retry() => Run(async () =>
    {
        await using var h = await Fixture.Create(); var key = await SavedPdf(h); var state = h.F.State(); var browser = new DownloadPort { FailPrepare = true };
        await h.Ui.DownloadOpenedAsync(key, browser); await h.Ui.WhenSettled.WaitAsync(Bound);
        Assert.Empty(browser.Files); Assert.Contains("could not be confirmed", h.Ui.View.Notice); Assert.True(h.Ui.CanDownloadOpened);
        browser.FailPrepare = false; await h.Ui.DownloadOpenedAsync(key, browser); await h.Ui.WhenSettled.WaitAsync(Bound);
        Assert.Single(browser.Files); Assert.Equal(2, browser.Prepares); Assert.Equal(1, browser.Released); Assert.Equal(state, h.F.State());
    });
    [Fact] public void Lease_disposal_failure_after_browser_commit_does_not_relabel_the_export_as_failed() => Run(async () =>
    {
        await using var h = await Fixture.Create(); var key = await SavedPdf(h); var browser = new DownloadPort { FailRelease = true };
        await h.Ui.DownloadOpenedAsync(key, browser); await h.Ui.WhenSettled.WaitAsync(Bound);
        Assert.Single(browser.Files); Assert.Equal(1, browser.Released); Assert.Equal("Saved PDF download started.", h.Ui.View.Notice);
    });
    [Fact] public void Historical_preview_borrows_the_existing_renderer_even_without_compiler_ports() => Run(async () =>
    {
        await using var h = await Fixture.Create(); var key = await SavedPdf(h);
        var io = new ArtifactIo();
        foreach (var row in h.Art.Files) io.Files.Add(row.Key, row.Value.ToDictionary(x => x.Key, x => x.Value.ToArray()));
        foreach (var row in h.Art.Catalog) io.Catalog.Add(row.Key, row.Value);
        await using var unavailable = new CompileWorkspaceSession(h.F.Workspace, new UnavailableCompileWorkspaceConfiguration());
        unavailable.AttachRenderer(Dispatcher()); await unavailable.RefreshToolsAsync();
        Assert.Null(unavailable.ConfiguredPorts); Assert.False(unavailable.Readiness.Ready);
        await using var history = new CompileHistoryUiSession(h.F.Workspace, unavailable,
            new(io, h.Revisions, NativeRuntimePackage.Pdf, NativeRuntimePackage.Adapter), new(new IndexIo()), Artifacts(), new(64, 65536, 10000), h.Config.Inspector);
        int calls = h.Config.Inspector.Calls;
        await history.OpenAsync(key); await history.Preview.WhenSettled.WaitAsync(Bound);
        Assert.True(h.Config.Inspector.Calls > calls); Assert.NotNull(history.View.Opened);
        Assert.True(history.CanDownloadOpened); Assert.False(history.CanSave);
    });
    private sealed class DownloadPort : IPdfDownloadBrowser
    {
        internal bool Hold, FailPrepare, FailRelease; internal int Prepares, Released;
        internal readonly List<(string Name, byte[] Bytes)> Files = [];
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<IPreparedPdfDownload> PrepareAsync(CancellationToken token)
        {
            Prepares++; Entered.TrySetResult();
            if (Hold) await Release.Task; // Deliberately noncooperative actual import.
            if (FailPrepare) throw new InvalidOperationException("synthetic preparation failure");
            return new Lease(this);
        }
        private sealed class Lease(DownloadPort owner) : IPreparedPdfDownload
        {
            public void Download(string name, byte[] bytes) => owner.Files.Add((name, bytes.ToArray()));
            public ValueTask DisposeAsync()
            { owner.Released++; if (owner.FailRelease) throw new InvalidOperationException("synthetic disposal failure"); return ValueTask.CompletedTask; }
        }
    }

    private sealed class BusyInspector : IPdfInspector
    {
        public int Disposals, Calls; private long _epoch;
        public Task<PdfInspectionOutcome> InspectAsync(PdfInspectRequest request, CancellationToken ct = default) { Calls++; return Task.FromResult(new PdfInspectionOutcome(PdfInspectionStatus.Busy, Guid.NewGuid().ToString("N"), request.Document.Sha256, ++_epoch, Code: "shared-busy")); }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class Configuration : ICompileWorkspaceConfiguration
    {
        public readonly Workers Workers = new(); public readonly BusyInspector Inspector = new(); public bool Ready = true;
        public Task<CompileWorkspaceConfigurationResult> OpenAsync() => Task.FromResult(new CompileWorkspaceConfigurationResult(new(Ready, Ready ? "Ready" : "Unavailable", "CONTRACT"), new("contract", "Contract only", CompilationTestWorkspace.Descriptor(), [], [], Workers, Inspector, () => Task.FromResult(new CompileReadiness(Ready, Ready ? "Ready" : "Unavailable", "CONTRACT")))));
    }
    private sealed class IndexIo : IHistoryIndexBackend
    {
        public readonly Dictionary<ProjectId, HistoryIndexRead> Rows = []; public bool Fail, UnknownCommit, HoldRead; public int Disposals, Exchanges;
        public readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<HistoryIndexRead> ReadAsync(ProjectId p, int max) { var r = Rows.GetValueOrDefault(p) ?? new("0", null); if (HoldRead) { Entered.TrySetResult(true); await Release.Task; } return r; }
        public Task<HistoryIndexCas> CompareExchangeAsync(ProjectId p, long expected, long next, string json, int max)
        { Exchanges++; if (Fail) return Task.FromResult(new HistoryIndexCas(HistoryIndexStatus.QuotaExceeded)); if ((Rows.GetValueOrDefault(p)?.Revision ?? "0") != expected.ToString(CultureInfo.InvariantCulture)) return Task.FromResult(new HistoryIndexCas(HistoryIndexStatus.Conflict)); Rows[p] = new(next.ToString(CultureInfo.InvariantCulture), json); if (UnknownCommit) throw new InvalidOperationException("CONTRACT unknown acknowledgement"); return Task.FromResult(new HistoryIndexCas(HistoryIndexStatus.Saved)); }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class ArtifactIo : IRuntimeAssetBackend
    {
        public readonly Dictionary<Guid, Dictionary<int, byte[]>> Files = []; public readonly Dictionary<Guid, string> Catalog = []; public bool HoldRead, HoldWrite, HoldPublish; public int Publishes, Disposals, Acquires, Stages, Writes, Discards;
        public readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private async Task Hold(bool active) { if (active) { Entered.TrySetResult(true); await Release.Task; } }
        public Task<IRuntimeAssetLease?> TryAcquireAsync() { Acquires++; return Task.FromResult<IRuntimeAssetLease?>(new Lease()); } public Task CreateStageAsync(IRuntimeAssetLease l, Guid id, int max) { Stages++; Files.Add(id, []); return Task.CompletedTask; }
        public async Task WriteAsync(IRuntimeAssetLease l, Guid id, int i, byte[] b) { Writes++; await Hold(HoldWrite); Files[id][i] = b.ToArray(); }
        public Task<StoredAssetBytes?> ReadAsync(Guid id, int i, long n, long max) => Task.FromResult(Files.TryGetValue(id, out var f) && f.TryGetValue(i, out var b) ? new StoredAssetBytes(b.Length, b.ToArray()) : null);
        public async Task<string?> CatalogAsync(Guid id, int max) { await Hold(HoldRead); return Catalog.GetValueOrDefault(id); } public Task<bool> StageExistsAsync(Guid id) => Task.FromResult(Files.ContainsKey(id));
        public async Task PublishAsync(IRuntimeAssetLease l, Guid id, string value, int max) { Publishes++; Catalog.Add(id, value); await Hold(HoldPublish); }
        public Task<bool> DiscardAsync(IRuntimeAssetLease l, Guid id) { Discards++; return Task.FromResult(false); } public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
        private sealed class Lease : IRuntimeAssetLease { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
}
