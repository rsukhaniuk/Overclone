using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using Client.Compilation;
using Client.Compilation.History;
using Client.Compilation.History.Index;
using Client.Compilation.Lifecycle;
using Client.Compilation.Native;
using Client.Compilation.Presentation;
using Client.Pages;
using Client.Pdf;
using Client.Pdf.Preview;
using Client.RuntimeAssets;
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
        public CompileWorkspaceSession Compile = null!; public CompileHistoryUiSession Ui = null!;
        public static async Task<Fixture> Create(int deadline = 10000, bool configure = true)
        {
            var h = new Fixture { F = await CompilationTestWorkspace.Open() };
            h.Compile = new(h.F.Workspace, h.Config); h.Compile.AttachRenderer(Dispatcher());
            h.Ui = new(h.F.Workspace, h.Compile, new(h.Art, h.F.Repo, NativeRuntimePackage.Pdf, NativeRuntimePackage.Adapter), new(h.Keys), Artifacts(deadline), new(64, 65536, 10000));
            h.F.Workspace.RenameFile(h.F.Main, "main.tex");
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
            Art.Release.TrySetResult(true); Keys.Release.TrySetResult(true);
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
    private static async Task<string> Render(CompileHistoryUiSession session)
    {
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(new NoJs()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => WebUtility.HtmlDecode((await renderer.RenderComponentAsync<CompileHistoryPanel>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(CompileHistoryPanel.Session)] = session }))).ToHtmlString()));
    }
    private sealed class NoJs : IJSRuntime
    { public ValueTask<T> InvokeAsync<T>(string n, object?[]? a) => throw new NotSupportedException("Static render only"); public ValueTask<T> InvokeAsync<T>(string n, CancellationToken c, object?[]? a) => InvokeAsync<T>(n, a); }
    private sealed class BusyInspector : IPdfInspector
    {
        public int Disposals, Calls; private long _epoch;
        public Task<PdfInspectionOutcome> InspectAsync(PdfInspectRequest request, CancellationToken ct = default) { Calls++; return Task.FromResult(new PdfInspectionOutcome(PdfInspectionStatus.Busy, Guid.NewGuid().ToString("N"), request.Document.Sha256, ++_epoch, Code: "shared-busy")); }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class Configuration : ICompileWorkspaceConfiguration
    {
        public readonly Workers Workers = new(); public readonly BusyInspector Inspector = new();
        public Task<CompileWorkspaceConfigurationResult> OpenAsync() => Task.FromResult(new CompileWorkspaceConfigurationResult(new(true, "Ready", "CONTRACT"), new("contract", "Contract only", CompilationTestWorkspace.Descriptor(), [], [], Workers, Inspector, () => Task.FromResult(new CompileReadiness(true, "Ready", "CONTRACT")))));
    }
    private sealed class IndexIo : IHistoryIndexBackend
    {
        public readonly Dictionary<ProjectId, HistoryIndexRead> Rows = []; public bool Fail, UnknownCommit, HoldRead; public int Disposals;
        public readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<HistoryIndexRead> ReadAsync(ProjectId p, int max) { var r = Rows.GetValueOrDefault(p) ?? new("0", null); if (HoldRead) { Entered.TrySetResult(true); await Release.Task; } return r; }
        public Task<HistoryIndexCas> CompareExchangeAsync(ProjectId p, long expected, long next, string json, int max)
        { if (Fail) return Task.FromResult(new HistoryIndexCas(HistoryIndexStatus.QuotaExceeded)); if ((Rows.GetValueOrDefault(p)?.Revision ?? "0") != expected.ToString(CultureInfo.InvariantCulture)) return Task.FromResult(new HistoryIndexCas(HistoryIndexStatus.Conflict)); Rows[p] = new(next.ToString(CultureInfo.InvariantCulture), json); if (UnknownCommit) throw new InvalidOperationException("CONTRACT unknown acknowledgement"); return Task.FromResult(new HistoryIndexCas(HistoryIndexStatus.Saved)); }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class ArtifactIo : IRuntimeAssetBackend
    {
        public readonly Dictionary<Guid, Dictionary<int, byte[]>> Files = []; public readonly Dictionary<Guid, string> Catalog = []; public bool HoldRead, HoldWrite, HoldPublish; public int Publishes, Disposals;
        public readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private async Task Hold(bool active) { if (active) { Entered.TrySetResult(true); await Release.Task; } }
        public Task<IRuntimeAssetLease?> TryAcquireAsync() => Task.FromResult<IRuntimeAssetLease?>(new Lease()); public Task CreateStageAsync(IRuntimeAssetLease l, Guid id, int max) { Files.Add(id, []); return Task.CompletedTask; }
        public async Task WriteAsync(IRuntimeAssetLease l, Guid id, int i, byte[] b) { await Hold(HoldWrite); Files[id][i] = b.ToArray(); }
        public Task<StoredAssetBytes?> ReadAsync(Guid id, int i, long n, long max) => Task.FromResult(Files.TryGetValue(id, out var f) && f.TryGetValue(i, out var b) ? new StoredAssetBytes(b.Length, b.ToArray()) : null);
        public async Task<string?> CatalogAsync(Guid id, int max) { await Hold(HoldRead); return Catalog.GetValueOrDefault(id); } public Task<bool> StageExistsAsync(Guid id) => Task.FromResult(Files.ContainsKey(id));
        public async Task PublishAsync(IRuntimeAssetLease l, Guid id, string value, int max) { Publishes++; Catalog.Add(id, value); await Hold(HoldPublish); }
        public Task<bool> DiscardAsync(IRuntimeAssetLease l, Guid id) => Task.FromResult(false); public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
        private sealed class Lease : IRuntimeAssetLease { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
}
