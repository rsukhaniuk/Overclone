using System.Text;
using System.Collections.Immutable;
using Client.Compilation;
using Client.Compilation.Diagnostics;
using Client.Compilation.Lifecycle;
using Client.Compilation.Navigation;
using Client.Compilation.Presentation;
using Client.Workspace;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.CompilerNavigation;

// Managed policy contracts: REAL workspace/capture/lifecycle/presenter/parser/session;
// synthetic storage/worker ports, no native execution, renderer or DOM-success claim.
public sealed class CompilerSourceNavigationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    internal sealed class Config : ICompileWorkspaceConfiguration
    {
        internal readonly Workers Workers = new();
        public Task<CompileWorkspaceConfigurationResult> OpenAsync() => Task.FromResult(new CompileWorkspaceConfigurationResult(
            new(true, "Ready", "Contract only"), new("contract", "Contract only", CompilationTestWorkspace.Descriptor(), [], [],
                Workers, null, () => Task.FromResult(new CompileReadiness(true, "Ready", "Contract only")))));
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        internal CompilationTestWorkspace Storage = null!;
        internal CompileWorkspaceSession Session = null!;
        internal readonly Config Config = new();
        internal CompilerSourceNavigationPolicy Policy = null!;
        internal WorkspaceCoordinator Workspace => Storage.Workspace;
        internal CompilerLogReport Report => Assert.IsType<CompilerLogParseResult.Parsed>(Session.Attempt!.ParserResult).Report;
        internal static async Task<Fixture> Open(bool readOnly = false)
        {
            var f = new Fixture { Storage = await CompilationTestWorkspace.Open(readOnly: readOnly) };
            if (readOnly)
            {
                // Synthetic repository initial manifest, not an edit/unlock of
                // the read-only workspace. Reopen through its real public API.
                var revision = f.Storage.Repo.Revision!;
                f.Storage.Repo.Revision = revision with { Files = revision.Files.Select(file =>
                    file.RelativePath == "entry" ? file with { RelativePath = "main.tex" } : file).ToImmutableArray() };
                f.Storage.Repo.Remember();
                await f.Workspace.CloseAsync(CancellationToken.None);
                await f.Workspace.OpenAsync(f.Storage.Repo.Project.Id, CancellationToken.None);
            }
            var ui = SynchronizationContext.Current!;
            f.Session = new(f.Workspace, f.Config);
            f.Session.AttachRenderer(action =>
            {
                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                ui.Post(_ => { try { action(); signal.SetResult(); } catch (Exception e) { signal.SetException(e); } }, null);
                return signal.Task;
            });
            if (!readOnly) Assert.True(f.Workspace.RenameFile(f.Storage.Main, "main.tex"));
            await f.Session.RefreshToolsAsync();
            f.Session.SelectMain(f.Storage.Main); f.Session.SelectEngine("contract");
            f.Policy = new(f.Workspace, f.Session);
            return f;
        }
        internal async Task Fail(string log = "main.tex:2: Undefined control sequence.\n")
        {
            var previous = Session.Attempt;
            var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed() { if (Session.Attempt is { } attempt && !ReferenceEquals(previous, attempt)) presented.TrySetResult(); }
            Session.Changed += Changed;
            try
            {
                await Session.CompileAsync();
                var worker = await Config.Workers.Next();
                worker.Reply.TrySetResult(worker.Output(CompileOutputStatus.Failed) with { LogText = log });
                Assert.Equal(CompileLifecycleReason.CompilerFailed, (await Session.LastTicket!.Completion.WaitAsync(Bound)).Reason);
                await presented.Task.WaitAsync(Bound);
                Assert.True(Session.State!.IsAttemptCurrent);
            }
            finally { Session.Changed -= Changed; }
        }
        internal CompilerSourceNavigationRequest Request(int index = 0) => Assert.IsType<CompilerSourceNavigationRequest>(
            CompilerSourceNavigationRequest.TryCreate(Session, Report, Report.Diagnostics[index]));
        public async ValueTask DisposeAsync()
        {
            Policy.Dispose();
            foreach (var w in Config.Workers.Created) w.Reply.TrySetResult(w.Output(CompileOutputStatus.Failed));
            await Session.DisposeAsync();
            if (Session.Owner is { } owner) await owner.WhenSettled.WaitAsync(Bound);
            await Storage.DisposeAsync();
        }
    }

    [Fact] public void Current_failed_attempt_navigates_multiple_files_without_pdf_or_mutation() => Run(async () =>
    {
        await using var f = await Fixture.Open();
        await f.Fail("main.tex:1: Undefined control sequence.\nchapter.tex:1: Undefined control sequence.\n");
        Assert.Null(f.Session.DisplayedOutput);
        var report = f.Report; Assert.Equal(2, report.Diagnostics.Length);
        var before = f.Storage.State(); int commits = f.Storage.Repo.Commits, workers = f.Config.Workers.Created.Count;
        foreach (int i in new[] { 0, 1 })
        {
            var request = f.Request(i);
            Assert.Same(report, request.Report); Assert.Same(report.Snapshot, request.Snapshot);
            Assert.Same(report.Diagnostics[i], request.Diagnostic);
            var selected = Assert.IsType<CompilerSourceNavigationPolicy.Selection>(await f.Policy.PrepareAsync(request));
            Assert.True(f.Policy.IsCurrent(selected));
            Assert.Equal(i == 0 ? "Україна🙂" : "chapter", selected.ExpectedTextareaText[selected.TextareaRange.Start..selected.TextareaRange.End]);
        }
        Assert.Equal(before, f.Storage.State()); Assert.Equal(commits, f.Storage.Repo.Commits);
        Assert.Equal(workers, f.Config.Workers.Created.Count);
        // Active-file switching itself is read-only and does not stale source.
        var chapter = f.Request(1); var target = (await f.Policy.PrepareAsync(chapter))!;
        Assert.True(f.Workspace.SelectFile(chapter.File.FileId));
        Assert.True(f.Policy.IsCurrent(target, requireActiveFile: true));
        Assert.Same(report, f.Report); Assert.True(f.Workspace.IsDirty); // Rename is still unsaved.
    });

    [Theory]
    [InlineData("one\nsecond\n", 2, "second", 4, 10)]
    [InlineData("Привіт😀\r\nУкраїна🙂\r\n", 2, "Україна🙂", 9, 18)]
    [InlineData("first\r\n\r\nlast", 2, "", 6, 6)]
    [InlineData("a\rb\r", 3, "", 4, 4)]
    public void Unicode_BOM_CRLF_and_empty_line_ranges_are_DOM_normalized(string source, int line, string expected, int start, int end) => Run(async () =>
    {
        await using var f = await Fixture.Open();
        var bytes = new byte[] { 239, 187, 191 }.Concat(Encoding.UTF8.GetBytes(source)).ToArray();
        Assert.True(f.Workspace.CreateFileFromBytes("unicode.tex", bytes));
        await f.Fail($"unicode.tex:{line}: Undefined control sequence.\n");
        var request = f.Request(); Assert.Equal(line, request.Location.Line);
        var selection = Assert.IsType<CompilerSourceNavigationPolicy.Selection>(await f.Policy.PrepareAsync(request));
        Assert.Equal(expected, selection.ExpectedTextareaText[selection.TextareaRange.Start..selection.TextareaRange.End]);
        Assert.Equal(start, selection.TextareaRange.Start); Assert.Equal(end, selection.TextareaRange.End);
        Assert.DoesNotContain('\r', selection.ExpectedTextareaText);
        Assert.Equal(source, selection.Source);
    });

    [Theory]
    [InlineData("missing.tex:2: Undefined control sequence.\n")]
    [InlineData("asset.bin:1: Undefined control sequence.\n")]
    [InlineData("main.tex:999: Undefined control sequence.\n")]
    [InlineData("! Undefined control sequence.\n")]
    [InlineData("(unknown.tex\n! Undefined control sequence.\nl.2 x\n")]
    public void Unknown_binary_missing_or_uncertain_locations_never_become_actions(string log) => Run(async () =>
    {
        await using var f = await Fixture.Open(); await f.Fail(log);
        Assert.NotEmpty(f.Report.Diagnostics);
        foreach (var diagnostic in f.Report.Diagnostics)
            Assert.Null(CompilerSourceNavigationRequest.TryCreate(f.Session, f.Report, diagnostic));
    });

    [Fact] public void Read_only_find_does_not_unlock_or_edit_workspace() => Run(async () =>
    {
        await using var f = await Fixture.Open(readOnly: true); await f.Fail();
        var before = f.Storage.State();
        var request = f.Request();
        Assert.NotNull(await f.Policy.PrepareAsync(request));
        Assert.True(f.Workspace.IsReadOnly);
        Assert.Equal(before, f.Storage.State());
        Assert.Equal(0, f.Storage.Repo.Commits);
    });

    [Fact] public void Separate_parser_report_or_nonmember_diagnostic_is_not_presented_authority() => Run(async () =>
    {
        await using var f = await Fixture.Open(); await f.Fail();
        var report = f.Report;
        var budget = new CompilerLogBudget(32, 8388608, 65536, 65536, 8192, 1000, 32, 1024, 32, 10000, 1000000, 1048576);
        var other = Assert.IsType<CompilerLogParseResult.Parsed>(CompilerLogParser.Parse(report.Snapshot,
            Encoding.UTF8.GetBytes("main.tex:1: Undefined control sequence.\n"), budget, logIsComplete: false)).Report;
        Assert.Null(CompilerSourceNavigationRequest.TryCreate(f.Session, report, other.Diagnostics[0]));
        Assert.Null(CompilerSourceNavigationRequest.TryCreate(f.Session, other, other.Diagnostics[0]));
        Assert.NotNull(f.Request());
    });

    [Theory]
    [InlineData("edit")][InlineData("ABA")][InlineData("other-file-edit")][InlineData("rename")][InlineData("delete")]
    [InlineData("selection")][InlineData("selection-ABA")][InlineData("close")][InlineData("reopen")]
    [InlineData("takeover")][InlineData("switch-project")][InlineData("save")][InlineData("dispose-policy")][InlineData("dispose-session")]
    public void Every_strict_stamp_and_lifecycle_change_invalidates_prepared_navigation(string change) => Run(async () =>
    {
        await using var f = await Fixture.Open(); await f.Fail();
        var request = f.Request();
        var selection = Assert.IsType<CompilerSourceNavigationPolicy.Selection>(await f.Policy.PrepareAsync(request));
        var file = f.Workspace.Files.Single(x => x.Id == request.File.FileId);
        var original = file.Content;
        switch (change)
        {
            case "edit": Assert.True(f.Workspace.EditText(file.Id, "edited")); break;
            case "ABA": Assert.True(f.Workspace.EditText(file.Id, "edited")); Assert.True(f.Workspace.EditText(file.Id, original)); break;
            case "other-file-edit": Assert.True(f.Workspace.EditText(f.Workspace.Files.Single(x => x.RelativePath == "chapter.tex").Id, "changed elsewhere")); break;
            case "rename": Assert.True(f.Workspace.RenameFile(file.Id, "renamed.tex")); break;
            case "delete": Assert.True(f.Workspace.DeleteFile(file.Id)); break;
            case "selection": f.Session.SelectEngine("xelatex"); break;
            case "selection-ABA": f.Session.SelectEngine("xelatex"); f.Session.SelectEngine("contract"); break;
            case "close": await f.Workspace.CloseAsync(CancellationToken.None); break;
            case "reopen": await f.Workspace.CloseAsync(CancellationToken.None); await f.Workspace.OpenAsync(f.Storage.Repo.Project.Id, CancellationToken.None); break;
            case "takeover": Assert.True(await f.Workspace.TakeOverAsync(CancellationToken.None)); break;
            case "switch-project":
                var project = new Client.Workspace.Domain.Project(Client.Workspace.Domain.ProjectId.New(), "other-synthetic", null);
                f.Storage.Repo.Projects[project.Id] = (project, null);
                await f.Workspace.CloseAsync(CancellationToken.None); await f.Workspace.OpenAsync(project.Id, CancellationToken.None); break;
            case "save": Assert.Equal(Client.Workspace.Domain.CommitStatus.Saved, (await f.Workspace.SaveAsync(CancellationToken.None)).Status); break;
            case "dispose-policy": f.Policy.Dispose(); break;
            case "dispose-session": await f.Session.DisposeAsync(); break;
        }
        Assert.False(f.Policy.IsCurrent(selection));
        Assert.Null(await f.Policy.PrepareAsync(request));
    });

    [Fact] public void Byte_identical_edit_ABA_still_invalidates_strict_generation() => Run(async () =>
    {
        await using var f = await Fixture.Open();
        var bytes = Encoding.UTF8.GetBytes("same Україна 😀\n");
        Assert.True(f.Workspace.CreateFileFromBytes("strict.tex", bytes));
        await f.Fail("strict.tex:1: Undefined control sequence.\n");
        var request = f.Request(); var selected = (await f.Policy.PrepareAsync(request))!;
        Assert.True(f.Workspace.EditText(request.File.FileId, "other"));
        Assert.True(f.Workspace.EditText(request.File.FileId, Encoding.UTF8.GetString(bytes)));
        var file = f.Workspace.Files.Single(x => x.Id == request.File.FileId);
        Assert.Equal(request.File.ToArray(), await f.Workspace.GetFileBytesAsync(file, CancellationToken.None));
        Assert.NotEqual(request.Snapshot.Identity.WorkingGeneration, f.Workspace.WorkingGeneration);
        Assert.False(f.Policy.IsCurrent(selected));
        Assert.Null(await f.Policy.PrepareAsync(request));
    });

    [Fact] public void New_attempt_invalidates_old_request_even_before_new_reply() => Run(async () =>
    {
        await using var f = await Fixture.Open(); await f.Fail();
        var request = f.Request(); var selection = (await f.Policy.PrepareAsync(request))!;
        await f.Session.CompileAsync(); var held = await f.Config.Workers.Next();
        Assert.False(f.Policy.IsCurrent(selection));
        Assert.Null(CompilerSourceNavigationRequest.TryCreate(f.Session, request.Report, request.Diagnostic));
        held.Reply.TrySetResult(held.Output(CompileOutputStatus.Failed) with { LogText = "main.tex:1: Undefined control sequence.\n" });
        await f.Session.LastTicket!.Completion.WaitAsync(Bound);
    });

    [Fact] public void Held_real_byte_read_rechecks_after_await_and_observes_cancellation() => Run(async () =>
    {
        await using var f = await Fixture.Open(); await f.Fail();
        var request = f.Request(); using var stop = new CancellationTokenSource();
        var gate = f.Storage.Hold();
        var preparing = f.Policy.PrepareAsync(request, stop.Token);
        await gate.WaitReached(); stop.Cancel();
        // The actual synthetic port deliberately ignores cancellation; release
        // it and assert that no prepared action escapes after the await.
        gate.Release.TrySetResult(f.Storage.Repo.Content[gate.Hash]);
        Assert.Null(await preparing.WaitAsync(Bound));
        Assert.Null(await f.Policy.PrepareAsync(request, stop.Token));
        Assert.True(f.Policy.IsCurrent(request));
    });

    [Theory][InlineData("edit")][InlineData("selection")][InlineData("dispose")]
    public void Changes_during_foreign_byte_IO_do_not_retire_into_a_valid_selection(string change) => Run(async () =>
    {
        await using var f = await Fixture.Open(); await f.Fail(); var request = f.Request();
        var gate = f.Storage.Hold(); var preparing = f.Policy.PrepareAsync(request);
        await gate.WaitReached();
        if (change == "edit") f.Workspace.EditText(request.File.FileId, "changed during IO");
        if (change == "selection") f.Session.SelectEngine("xelatex");
        if (change == "dispose") f.Policy.Dispose();
        gate.Release.TrySetResult(f.Storage.Repo.Content[gate.Hash]);
        Assert.Null(await preparing.WaitAsync(Bound));
    });

    [Fact] public void Actual_bytes_are_checked_not_just_durable_hash_metadata_or_same_line() => Run(async () =>
    {
        await using var f = await Fixture.Open(); await f.Fail(); var request = f.Request();
        f.Storage.Repo.ReadOverride = _ => Encoding.UTF8.GetBytes("unrelated bytes with the same alleged metadata");
        Assert.Null(await f.Policy.PrepareAsync(request));
        f.Storage.Repo.ReadOverride = _ => [0xff, 0xfe];
        Assert.Null(await f.Policy.PrepareAsync(request));
    });

    [Fact] public void DOM_only_success_is_never_inferred_by_policy_and_active_file_is_explicit() => Run(async () =>
    {
        await using var f = await Fixture.Open(); await f.Fail("chapter.tex:1: Undefined control sequence.\n");
        var request = f.Request(); var selected = (await f.Policy.PrepareAsync(request))!;
        Assert.False(f.Policy.IsCurrent(selected, requireActiveFile: true));
        Assert.True(f.Workspace.SelectFile(request.File.FileId));
        Assert.True(f.Policy.IsCurrent(selected, requireActiveFile: true));
        // The policy returns only a range/preimage. It does not claim actual DOM
        // success (real expected-file/value/selection tested by published UI).
        Assert.Equal("chapter\n", selected.ExpectedTextareaText);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.False(f.Policy.IsCurrent(selected, stop.Token, requireActiveFile: true));
    });
}
