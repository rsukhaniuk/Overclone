using Client.Compilation;
using Client.Compilation.Download;
using Client.Compilation.Lifecycle;
using Client.Compilation.Presentation;
using Client.Workspace.Domain;
using Microsoft.JSInterop;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.CompilationDownload;

// CONTRACT tests: real workspace/B1/B3/session/accepted output and export adapter;
// synthetic worker/storage/JS module only. Not native or actual browser evidence.
public sealed class PdfDownloadTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public void No_output_or_failed_output_never_imports(bool failed) => Run(async () =>
    {
        await using var f = await Fixture.Open();
        if (failed) await f.Compile(CompileOutputStatus.Failed);
        Assert.False(f.Download.CanDownload); await f.Download.DownloadAsync();
        Assert.Equal(0, f.Js.Imports); Assert.Equal(0, f.Js.Module.Calls);
    });

    [Fact] public void Exact_accepted_payload_export_does_not_require_renderer_or_ready_tools() => Run(async () =>
    {
        await using var f = await Fixture.Open(); await f.Compile();
        var output = f.Session.DisplayedOutput!; var bytes = output.ToPayloadArray()!;
        var state = f.Work.State(); var commits = f.Work.Repo.Commits; var created = f.Config.Workers.Created.Count;
        f.Config.Available = false; await f.Session.RefreshToolsAsync(); Assert.False(f.Session.Readiness.Ready);
        Assert.Null(f.Session.ConfiguredPorts!.Inspector); var notice = f.Session.Notice; var label = f.Session.PdfLabel;
        await f.Download.DownloadAsync();
        Assert.Equal(bytes, f.Js.Module.Bytes); Assert.Equal("downloadPdf", f.Js.Module.Identifier);
        Assert.Equal("compile-test.pdf", f.Js.Module.Filename); Assert.Equal(1, f.Js.Module.Disposals);
        Assert.Same(output, f.Session.DisplayedOutput); Assert.Equal(bytes, output.ToPayloadArray());
        Assert.Equal(notice, f.Session.Notice); Assert.Equal(label, f.Session.PdfLabel);
        Assert.Equal(state, f.Work.State()); Assert.Equal(commits, f.Work.Repo.Commits);
        Assert.Equal(created, f.Config.Workers.Created.Count); Assert.Equal("PDF download started.", f.Download.Notice);
    });

    [Fact] public void Source_edit_during_prepare_allows_same_displayed_stale_pdf_without_promoting() => Run(async () =>
    {
        await using var f = await Fixture.Open(); await f.Compile(); var output = f.Session.DisplayedOutput;
        f.Js.Hold = true; var downloading = f.Download.DownloadAsync(); await f.Js.Reached.Task;
        Assert.True(f.Work.Workspace.EditText(f.Work.Main, "changed source Україна 😀"));
        Assert.False(f.Session.State!.IsCurrent); Assert.Equal("Last successful PDF — out of date", f.Session.PdfLabel);
        var state = f.Work.State(); var notice = f.Session.Notice;
        f.Js.Release.TrySetResult(f.Js.Module); await downloading;
        Assert.Equal(1, f.Js.Module.Calls); Assert.Same(output, f.Session.DisplayedOutput);
        Assert.False(f.Session.State.IsCurrent); Assert.Equal("Last successful PDF — out of date", f.Session.PdfLabel);
        Assert.Equal(state, f.Work.State()); Assert.Equal(notice, f.Session.Notice);
    });

    [Theory]
    [InlineData("close")] [InlineData("switch")] [InlineData("reopen")]
    [InlineData("takeover")] [InlineData("replace")] [InlineData("dispose")] [InlineData("compile-dispose")]
    public void Held_prepare_cannot_download_after_identity_or_lifetime_change(string change) => Run(async () =>
    {
        await using var f = await Fixture.Open(); await f.Compile(); var old = f.Session.DisplayedOutput;
        f.Js.Hold = true; var downloading = f.Download.DownloadAsync(); await f.Js.Reached.Task;
        switch (change)
        {
            case "close": await f.Work.Workspace.CloseAsync(default); break;
            case "switch":
                var project = new Project(ProjectId.New(), "Other project", null);
                f.Work.Repo.Projects[project.Id] = (project, null);
                await f.Work.Workspace.OpenAsync(project.Id, default); break;
            case "reopen":
                var id = f.Work.Workspace.Project!.Id; await f.Work.Workspace.CloseAsync(default);
                await f.Work.Workspace.OpenAsync(id, default); break;
            case "takeover": Assert.True(await f.Work.Workspace.TakeOverAsync(default)); break;
            case "replace": await f.Compile(); Assert.NotSame(old, f.Session.DisplayedOutput); break;
            case "dispose": f.Download.Dispose(); break;
            case "compile-dispose": await f.Session.DisposeAsync(); break;
        }
        f.Js.Release.TrySetResult(f.Js.Module); await downloading;
        Assert.Equal(0, f.Js.Module.Calls); Assert.Equal(1, f.Js.Module.Disposals);
        Assert.False(f.Download.Busy);
        if (change != "replace") Assert.False(f.Download.CanDownload);
    });

    [Fact] public void Single_flight_rejects_second_click_during_import() => Run(async () =>
    {
        await using var f = await Fixture.Open(); await f.Compile(); f.Js.Hold = true;
        var first = f.Download.DownloadAsync(); await f.Js.Reached.Task;
        Assert.True(f.Download.Busy); Assert.False(f.Download.CanDownload);
        await f.Download.DownloadAsync(); Assert.Equal(1, f.Js.Imports);
        f.Js.Release.TrySetResult(f.Js.Module); await first;
        Assert.Equal(1, f.Js.Module.Calls); Assert.False(f.Download.Busy);
    });

    [Theory] [InlineData("import")] [InlineData("invoke")] [InlineData("dispose")]
    public void Browser_failures_are_safe_and_leave_compile_source_and_history_untouched(string failure) => Run(async () =>
    {
        await using var f = await Fixture.Open(); await f.Compile(); var output = f.Session.DisplayedOutput;
        var state = f.Work.State(); var notice = f.Session.Notice; var label = f.Session.PdfLabel;
        var commits = f.Work.Repo.Commits; var created = f.Config.Workers.Created.Count;
        f.Js.FailImport = failure == "import"; f.Js.Module.FailInvoke = failure == "invoke";
        f.Js.Module.FailDispose = failure == "dispose";
        await f.Download.DownloadAsync();
        Assert.DoesNotContain("SECRET", f.Download.Notice); Assert.Equal(failure == "dispose" ? "PDF download started." : "PDF download could not start. Try again.", f.Download.Notice);
        Assert.Same(output, f.Session.DisplayedOutput); Assert.Equal(state, f.Work.State());
        Assert.Equal(notice, f.Session.Notice); Assert.Equal(label, f.Session.PdfLabel);
        Assert.Equal(commits, f.Work.Repo.Commits); Assert.Equal(created, f.Config.Workers.Created.Count);
        Assert.False(f.Download.Busy);
    });

    private sealed class Configuration : ICompileWorkspaceConfiguration
    {
        internal readonly Workers Workers = new(); internal bool Available = true;
        private CompileReadiness Readiness() => new(Available, Available ? "Ready" : "Unavailable", "Contract only");
        public Task<CompileWorkspaceConfigurationResult> OpenAsync() => Task.FromResult(new CompileWorkspaceConfigurationResult(Readiness(),
            new("contract", "Contract only", CompilationTestWorkspace.Descriptor(), [], [], Workers, null, () => Task.FromResult(Readiness()))));
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal CompilationTestWorkspace Work = null!;
        internal Configuration Config = new(); internal CompileWorkspaceSession Session = null!;
        internal DownloadJs Js = new(); internal PdfDownloadCoordinator Download = null!;
        internal static async Task<Fixture> Open()
        {
            var f = new Fixture { Work = await CompilationTestWorkspace.Open() };
            var ui = SynchronizationContext.Current!;
            f.Session = new(f.Work.Workspace, f.Config);
            f.Session.AttachRenderer(action =>
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                ui.Post(_ => { try { action(); done.SetResult(); } catch (Exception error) { done.SetException(error); } }, null);
                return done.Task;
            });
            Assert.True(f.Work.Workspace.RenameFile(f.Work.Main, "main.tex"));
            await f.Session.RefreshToolsAsync(); f.Session.SelectMain(f.Work.Main); f.Session.SelectEngine("contract");
            f.Download = new(f.Work.Workspace, f.Session, new PdfDownloadBrowser(f.Js)); return f;
        }
        internal async Task Compile(CompileOutputStatus status = CompileOutputStatus.Succeeded)
        {
            var expected = status == CompileOutputStatus.Succeeded ? CompileLifecycleReason.Succeeded : CompileLifecycleReason.CompilerFailed;
            var previous = Session.LastTicket;
            var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed() { if (!ReferenceEquals(Session.LastTicket, previous) && Session.LastReason == expected) presented.TrySetResult(); }
            Session.Changed += Changed;
            try
            {
                await Session.CompileAsync(); var worker = await Config.Workers.Next();
                worker.Reply.SetResult(worker.Output(status, [37, 80, 68, 70, 45, 49, 0, 255, 128]));
                var result = await Session.LastTicket!.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(expected, result.Reason);
                // Ticket settlement and its owner/UI completion notification are
                // distinct; download preservation assertions need the latter.
                await presented.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { Session.Changed -= Changed; }
        }
        public async ValueTask DisposeAsync()
        {
            Download.Dispose(); Js.Release.TrySetResult(Js.Module);
            foreach (var worker in Config.Workers.Created) worker.Reply.TrySetResult(worker.Output());
            await Session.DisposeAsync(); if (Session.Owner is { } owner) await owner.WhenSettled.WaitAsync(TimeSpan.FromSeconds(5));
            await Work.DisposeAsync();
        }
    }
}

internal sealed class DownloadJs : IJSRuntime
{
    internal readonly DownloadModule Module = new();
    internal readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource<IJSObjectReference> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool Hold, FailImport; internal int Imports;
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
    public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        Assert.Equal("import", identifier); Assert.Equal("./js/download.js", Assert.Single(args!)); Imports++; Reached.TrySetResult();
        if (FailImport) throw new JSException("SECRET import path");
        var module = Hold ? await Release.Task : Module;
        return (TValue)(object)module;
    }
}
internal sealed class DownloadModule : IJSInProcessObjectReference
{
    internal int Calls, Disposals; internal bool FailInvoke, FailDispose;
    internal string? Identifier, Filename; internal byte[]? Bytes;
    public TValue Invoke<TValue>(string identifier, params object?[]? args)
    {
        Identifier = identifier; Filename = Assert.IsType<string>(args![0]);
        var bytes = Assert.IsType<byte[]>(args[1]); Bytes = bytes.ToArray(); bytes[0] = 0; // JS receives an owned copy, never accepted storage
        if (FailInvoke) throw new JSException("SECRET browser path");
        Calls++; return default!;
    }
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("A queued click is forbidden.");
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new InvalidOperationException("A queued click is forbidden.");
    public void Dispose() => throw new InvalidOperationException("Use owned async module disposal.");
    public ValueTask DisposeAsync()
    {
        Disposals++;
        if (FailDispose) throw new JSException("SECRET cleanup path");
        return ValueTask.CompletedTask;
    }
}
