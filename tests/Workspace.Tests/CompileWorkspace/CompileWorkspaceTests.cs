using Client.Compilation;
using Client.Compilation.Lifecycle;
using Client.Compilation.Native;
using Client.Compilation.Presentation;
using Client.Workspace;
using Client.Workspace.Domain;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.CompileWorkspace;

// CONTRACT synthetic storage/worker/output; real Workspace/B1/B3/presentation.
// Actual Rust/native/PDF/canvas evidence is the separate published application.
public sealed class CompileWorkspaceTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static Func<Action, Task> Dispatcher()
    {
        var ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Fixture UI context");
        return action =>
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.Post(_ => { try { action(); signal.SetResult(); } catch (Exception error) { signal.SetException(error); } }, null);
            return signal.Task;
        };
    }
    private static async Task Drain(CompileWorkspaceSession session)
    { await session.DisposeAsync(); if (session.Owner is { } owner) await owner.WhenSettled.WaitAsync(Bound); }
    private static CompileWorkspaceSession Attach(CompilationTestWorkspace f, ICompileWorkspaceConfiguration configuration)
    {
        var session = new CompileWorkspaceSession(f.Workspace, configuration); session.AttachRenderer(Dispatcher()); return session;
    }
    private static async Task Select(CompilationTestWorkspace f, CompileWorkspaceSession session)
    {
        Assert.True(f.Workspace.RenameFile(f.Main, "main.tex")); await session.RefreshToolsAsync(); session.SelectMain(f.Main); session.SelectEngine("contract");
    }
    [Fact] public void Default_unavailable_does_not_open_native_or_block_workspace() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var config = new Configuration(false); var session = Attach(f, config);
        try { await session.RefreshToolsAsync(); Assert.False(session.Readiness.Ready); await session.CompileAsync(); Assert.Null(session.LastTicket); Assert.True(f.Workspace.EditText(f.Main, "local Україна 😀")); Assert.Empty(config.Workers.Created); }
        finally { await Drain(session); }
    });
    [Fact] public void Settled_readiness_notification_enables_the_compiler_without_another_workspace_event() => Run(async () =>
    {
        await using var fixture = await CompilationTestWorkspace.Open();
        var configuration = new DeferredConfiguration();
        var session = Attach(fixture, configuration);
        var updates = new List<(bool Reading, bool Ready)>();
        session.Changed += () => updates.Add((session.ReadingTools, session.Readiness.Ready));
        try
        {
            var checking = session.RefreshToolsAsync();
            Assert.True(session.ReadingTools);
            await session.RefreshToolsAsync();
            Assert.Equal(1, configuration.Opens);
            configuration.Permit.SetResult();
            await checking.WaitAsync(Bound);
            Assert.Equal((false, true), updates[^1]);
            Assert.Single(session.AvailableEngines);
        }
        finally
        {
            configuration.Permit.TrySetResult();
            await Drain(session);
        }
    });
    [Fact] public void Owner_entry_restores_ambient_context_and_defers_post() => Run(async () =>
    {
        var original = SynchronizationContext.Current; var owner = new CompileOwnerContext(Dispatcher()); bool called = false;
        try
        {
            owner.Run(() => { Assert.Same(owner, SynchronizationContext.Current); owner.Post(_ => called = true, null); Assert.False(called); });
            Assert.Same(original, SynchronizationContext.Current); owner.RequestClose(); await owner.WhenSettled.WaitAsync(Bound); Assert.True(called);
        }
        finally { owner.RequestClose(); await owner.WhenSettled.WaitAsync(Bound); }
    });
    [Fact] public void Explicit_selection_advances_A_B_A_and_unsupported_never_substitutes() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var c = new Configuration(); var session = Attach(f, c);
        try
        {
            await Select(f, session); long first = session.SelectionVersion; session.SelectEngine("xelatex"); session.SelectEngine("contract"); Assert.Equal(first + 2, session.SelectionVersion);
            session.SelectMain(f.Workspace.Files.First(x => x.RelativePath == "chapter.tex").Id); await session.CompileAsync(); Assert.Null(session.LastTicket); Assert.Contains("supported", session.Notice);
        }
        finally { await Drain(session); }
    });
    [Fact] public void Changing_configured_engine_dispatches_its_descriptor_and_rejects_previous_reply() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var c = new MultipleConfiguration(); var session = Attach(f, c);
        try
        {
            await Select(f, session);
            // The app retains its choices even if the composition's source list changes.
            c.Additional.Clear(); Assert.Equal(2, session.AvailableEngines.Count);
            await session.CompileAsync(); var old = await c.First.Next(); var oldTicket = session.LastTicket!;
            session.SelectEngine("xelatex");
            Assert.Equal("xetex", session.ConfiguredPorts!.Engine.Id);
            await session.CompileAsync(); var current = await c.Second.Next(); var currentTicket = session.LastTicket!;
            Assert.Equal("xetex", current.Context.Snapshot.Selection.Engine.Id);
            Assert.Contains(current.Context.Snapshot.Selection.Options, option => option.Key == "output-mode" && option.Value == "xdv");
            old.Reply.SetResult(old.Output());
            Assert.NotEqual(CompileLifecycleReason.Succeeded, (await oldTicket.Completion.WaitAsync(Bound)).Reason);
            Assert.Null(session.DisplayedOutput);
            current.Reply.SetResult(current.Output());
            Assert.Equal(CompileLifecycleReason.Succeeded, (await currentTicket.Completion.WaitAsync(Bound)).Reason);
            Assert.True(session.State!.IsCurrent);
            session.SelectEngine("contract"); Assert.False(session.State.IsCurrent);
            Assert.Equal("contract", session.ConfiguredPorts.CapabilityId);
        }
        finally
        {
            foreach (var worker in c.First.Created.Concat(c.Second.Created)) worker.Reply.TrySetResult(worker.Output());
            await Drain(session);
        }
    });
    [Fact] public void An_unconfigured_engine_never_uses_an_available_factory() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var c = new MultipleConfiguration(); var session = Attach(f, c);
        try
        {
            await Select(f, session); session.SelectEngine("lualatex"); await session.CompileAsync();
            Assert.Null(session.LastTicket); Assert.Empty(c.First.Created); Assert.Empty(c.Second.Created);
            Assert.True(f.Workspace.EditText(f.Main, "local Україна 😀"));
        }
        finally { await Drain(session); }
    });
    [Theory] [InlineData("duplicate")] [InlineData("lifetime")] [InlineData("count")]
    public void Invalid_configured_engine_sets_remain_unavailable(string invalid) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var c = new MultipleConfiguration(invalid); var session = Attach(f, c);
        try
        {
            await session.RefreshToolsAsync(); Assert.False(session.Readiness.Ready); Assert.Null(session.ConfiguredPorts);
            Assert.Empty(session.AvailableEngines); await session.CompileAsync(); Assert.Empty(c.First.Created); Assert.Empty(c.Second.Created);
        }
        finally { await Drain(session); }
    });
    [Fact] public void Dirty_capture_uses_original_B3_context_no_save_or_extra_lock() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var c = new Configuration(); var session = Attach(f, c);
        try
        {
            await Select(f, session); f.Workspace.EditText(f.Main, "dirty І😀x\r\n"); int commits = f.Repo.Commits, locks = f.Lock.Acquires;
            await session.CompileAsync(); var worker = await c.Workers.Next(); Assert.Contains(worker.Context.Snapshot.Files, file => file.RelativePath == "main.tex" && System.Text.Encoding.UTF8.GetString(file.ToArray()).Contains("dirty І😀x"));
            worker.Reply.SetResult(worker.Output()); var ticket = session.LastTicket!; Assert.Equal(CompileLifecycleReason.Succeeded, (await ticket.Completion.WaitAsync(Bound)).Reason);
            Assert.Equal(commits, f.Repo.Commits); Assert.Equal(locks, f.Lock.Acquires); Assert.Same(worker.Context.Snapshot, ticket.Snapshot);
        }
        finally { await Drain(session); }
    });
    [Fact] public void Foreign_ambient_edit_invalidates_before_any_old_reply_and_guards_remain() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var c = new Configuration(); var session = Attach(f, c);
        try
        {
            await Select(f, session); await session.CompileAsync(); var worker = await c.Workers.Next(); var ticket = session.LastTicket!;
            var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(null);
            try { Assert.True(f.Workspace.EditText(f.Main, "outside compile owner")); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            worker.Reply.TrySetResult(worker.Output()); Assert.Equal(CompileLifecycleReason.Superseded, (await ticket.Completion.WaitAsync(Bound)).Reason); Assert.Null(session.DisplayedOutput);
        }
        finally { foreach (var w in c.Workers.Created) w.Reply.TrySetResult(w.Output()); await Drain(session); }
    });
    [Fact] public void Queued_event_seam_does_not_delay_B1_freshness_or_promote_old_output() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var pending = new Queue<Action>(); var workers = new Workers();
        await using var life = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(2, 100), TimeProvider.System, pending.Enqueue);
        var ticket = life.Request(Options); var worker = await workers.Next(); var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(null);
        try { f.Workspace.EditText(f.Main, "epoch invalidated synchronously"); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        Assert.Single(pending); worker.Reply.SetResult(worker.Output()); Assert.Equal(CompileLifecycleReason.Superseded, (await ticket.Completion.WaitAsync(Bound)).Reason);
        while (pending.TryDequeue(out var action)) action(); await life.WhenIdle; Assert.False(life.State.IsCurrent); Assert.Null(life.State.LastSuccessful);
    });
    [Fact] public void Cancel_is_prompt_actual_work_retains_admission_until_release() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var c = new Configuration(); var session = Attach(f, c);
        try
        {
            await Select(f, session); await session.CompileAsync(); var worker = await c.Workers.Next(); var ticket = session.LastTicket!;
            session.Cancel(); Assert.Equal(CompileLifecycleReason.Cancelled, (await ticket.Completion.WaitAsync(Bound)).Reason); Assert.Null(session.DisplayedOutput); worker.Reply.SetResult(worker.Output());
        }
        finally { foreach (var w in c.Workers.Created) w.Reply.TrySetResult(w.Output()); await Drain(session); }
    });
    [Theory] [InlineData(true)] [InlineData(false)]
    public void Failure_or_foreign_OCE_never_promotes_previous_pdf(bool foreign) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var c = new Configuration(); var session = Attach(f, c);
        try
        {
            await Select(f, session); await session.CompileAsync(); var first = await c.Workers.Next(); first.Reply.SetResult(first.Output()); await session.LastTicket!.Completion.WaitAsync(Bound);
            // Fresh public B3 view is read through the actual owner bridge.
            Assert.True(session.State!.IsCurrent);
            f.Workspace.EditText(f.Main, "new unsaved failure"); var old = session.State!.LastSuccessful;
            await session.CompileAsync(); var next = await c.Workers.Next(); if (foreign) next.Reply.SetException(new OperationCanceledException()); else next.Reply.SetResult(next.Output(CompileOutputStatus.Failed));
            var result = await session.LastTicket!.Completion.WaitAsync(Bound); Assert.Null(result.Output); Assert.False(session.State!.IsCurrent); Assert.Same(old, session.State.LastSuccessful);
        }
        finally { foreach (var w in c.Workers.Created) w.Reply.TrySetResult(w.Output()); await Drain(session); }
    });
    [Theory] [InlineData("Corrupt")] [InlineData("Busy")] [InlineData("Uncertain")] [InlineData("Incomplete")]
    public void Rich_readiness_refuses_dispatch_without_fake_absence(string status) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var c = new Configuration(); var session = Attach(f, c);
        try { await Select(f, session); c.Readiness = new(false, status, "Actual provider refusal"); await session.CompileAsync(); Assert.Equal(status, session.Readiness.Status); Assert.Empty(c.Workers.Created); }
        finally { await Drain(session); }
    });
    [Fact] public void Remount_attach_keeps_same_owner_and_does_not_dispose_borrowed_workspace() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var session = Attach(f, new Configuration()); var owner = session.Owner;
        session.AttachRenderer(_ => throw new InvalidOperationException("No second owner")); Assert.Same(owner, session.Owner); await Drain(session);
        Assert.True(f.Workspace.EditText(f.Main, "still editable")); var saved = await f.Workspace.SaveAsync(CancellationToken.None); Assert.Equal(CommitStatus.Saved, saved.Status);
    });
    [Fact] public void Project_close_clears_selection_and_preview_but_keeps_app_session_reusable() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var session = Attach(f, new Configuration());
        try { await Select(f, session); await f.Workspace.CloseAsync(CancellationToken.None); Assert.Null(session.MainFile); Assert.Equal("No compiled PDF", session.PdfLabel); Assert.Null(session.Preview.State.DocumentSha256); await f.Workspace.OpenAsync(f.Repo.Project.Id, CancellationToken.None); Assert.False(session.Disposed); Assert.Null(session.MainFile); }
        finally { await Drain(session); }
    });
    [Fact] public void Explicit_port_lifetime_is_owned_once_but_configuration_and_workspace_are_borrowed() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var c = new Configuration(); var session = Attach(f, c);
        await session.RefreshToolsAsync(); await Drain(session); await session.DisposeAsync(); Assert.Equal(1, c.Lifetime.Disposals); Assert.True(f.Workspace.EditText(f.Main, "borrowed survives"));
    });
    [Fact] public void Owner_deadline_settles_without_promoting_held_worker_and_cleanup_drains() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var c = new Configuration(); var clock = new Clock();
        var session = new CompileWorkspaceSession(f.Workspace, c, clock); session.AttachRenderer(Dispatcher());
        try { await Select(f, session); await session.CompileAsync(); var worker = await c.Workers.Next(); var ticket = session.LastTicket!; clock.Advance(TimeSpan.FromSeconds(31)); Assert.Equal(CompileLifecycleReason.TimedOut, (await ticket.Completion.WaitAsync(Bound)).Reason); Assert.Null(session.DisplayedOutput); worker.Reply.SetResult(worker.Output()); }
        finally { foreach (var worker in c.Workers.Created) worker.Reply.TrySetResult(worker.Output()); await Drain(session); }
    });
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Durable_head_commit_notifies_fresh_owner_view_without_content_generation_change(bool reviewSave) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var c = new Configuration(); var session = Attach(f, c);
        try
        {
            await Select(f, session); Assert.True(f.Workspace.EditText(f.Main, "dirty revision to compile then save"));
            await session.CompileAsync(); var worker = await c.Workers.Next(); worker.Reply.SetResult(worker.Output()); await session.LastTicket!.Completion.WaitAsync(Bound);
            var output = session.State!.LastSuccessful; Assert.True(session.State.IsCurrent); var generation = f.Workspace.WorkingGeneration;
            var oldHead = f.Workspace.CurrentRevision!.Id; var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Changed += () =>
            {
                // This is the render notification, not a polling getter that
                // hides the missing event. B3 must already be freshly stale.
                if (f.Workspace.CurrentRevision!.Id == oldHead) return;
                Assert.Same(session.Owner, SynchronizationContext.Current); Assert.False(session.State!.IsCurrent);
                Assert.Same(output, session.State.LastSuccessful); Assert.Same(output, session.DisplayedOutput);
                Assert.Equal("Last successful PDF — out of date", session.PdfLabel); committed.TrySetResult();
            };
            var gate = f.Repo.CommitGate = new();
            var saving = reviewSave ? f.Workspace.SaveSessionAsync(f.Workspace.Project!.Id, f.Workspace.SessionId, CancellationToken.None) : f.Workspace.SaveAsync(CancellationToken.None);
            await gate.WaitReached(); gate.Release.TrySetResult(); Assert.Equal(CommitStatus.Saved, (await saving.WaitAsync(Bound)).Status);
            await committed.Task.WaitAsync(Bound); Assert.NotEqual(oldHead, f.Workspace.CurrentRevision!.Id); Assert.Equal(generation, f.Workspace.WorkingGeneration); Assert.False(f.Workspace.IsDirty);
        }
        finally { f.Repo.CommitGate?.Release.TrySetResult(); await Drain(session); }
    });
    [Fact] public void Late_commit_from_previous_session_cannot_notify_new_session() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var session = Attach(f, new Configuration());
        try
        {
            await Select(f, session); var oldSession = f.Workspace.SessionId; var gate = f.Repo.CommitGate = new(); var saving = f.Workspace.SaveAsync(CancellationToken.None); await gate.WaitReached();
            // Actual finite commit still pending, while a takeover creates a new
            // session for the SAME project. Project-only filtering is insufficient.
            Assert.True(await f.Workspace.TakeOverAsync(CancellationToken.None)); Assert.NotEqual(oldSession, f.Workspace.SessionId);
            int notifications = 0; session.Changed += () => notifications++;
            gate.Release.TrySetResult(); Assert.Equal(CommitStatus.Saved, (await saving.WaitAsync(Bound)).Status); Assert.Equal(0, notifications); Assert.Null(session.MainFile);
        }
        finally { f.Repo.CommitGate?.Release.TrySetResult(); await Drain(session); }
    });
    [Fact] public void Disposal_removes_commit_subscription_and_borrowed_workspace_still_saves() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var session = Attach(f, new Configuration());
        var field = typeof(WorkspaceCoordinator).GetField("RevisionCommitted", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Contains(((Delegate)field.GetValue(f.Workspace)!).GetInvocationList(), d => ReferenceEquals(d.Target, session));
        await Drain(session); var remaining = (Delegate?)field.GetValue(f.Workspace);
        Assert.DoesNotContain(remaining?.GetInvocationList() ?? [], d => ReferenceEquals(d.Target, session));
        Assert.True(f.Workspace.EditText(f.Main, "save after compile owner disposed")); Assert.Equal(CommitStatus.Saved, (await f.Workspace.SaveAsync(CancellationToken.None)).Status);
    });
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Bibliography_opt_in_defaults_plain_while_legacy_keeps_all_declared_tools(bool selectable) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open();
        var bibtex = CompilationTestWorkspace.Descriptor("bibtex", "available-version");
        CompilationDescriptor[] declared = [bibtex, CompilationTestWorkspace.Descriptor("other-tool")];
        var c = new Configuration(selectableBibliography: selectable, tools: declared); var session = Attach(f, c);
        try
        {
            await Select(f, session); Assert.False(session.UseBibtex); Assert.Equal(selectable, session.CanSelectBibtex);
            // Bibliography-looking source is not an automatic tool-selection signal.
            Assert.True(f.Workspace.EditText(f.Main, @"\citation{entry}\bibdata{refs}\bibstyle{plain}"));
            await session.CompileAsync(); var worker = await c.Workers.Next();
            Assert.Equal(selectable ? Array.Empty<CompilationDescriptor>() : declared, worker.Context.Snapshot.Selection.Tools.ToArray());
            worker.Reply.TrySetResult(worker.Output()); await session.LastTicket!.Completion.WaitAsync(Bound);
            if (!selectable)
            {
                var version = session.SelectionVersion; session.SelectBibliography(true);
                Assert.False(session.UseBibtex); Assert.Equal(version, session.SelectionVersion);
            }
        }
        finally { foreach (var worker in c.Workers.Created) worker.Reply.TrySetResult(worker.Output()); await Drain(session); }
    });
    [Fact] public void Bibliography_A_B_A_uses_exact_available_descriptor_and_stales_old_output_without_save_or_implicit_compile() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open();
        var bibtex = CompilationTestWorkspace.Descriptor("bibtex", "exact-available-version", new string('b', 64));
        var c = new Configuration(selectableBibliography: true, tools: [CompilationTestWorkspace.Descriptor("unknown-tool"), bibtex]); var session = Attach(f, c);
        try
        {
            await Select(f, session); await session.CompileAsync(); var first = await c.Workers.Next();
            Assert.Empty(first.Context.Snapshot.Selection.Tools); first.Reply.TrySetResult(first.Output()); await session.LastTicket!.Completion.WaitAsync(Bound);
            Assert.True(session.State!.IsCurrent); var oldOutput = session.State.LastSuccessful; var version = session.SelectionVersion; int commits = f.Repo.Commits;
            session.SelectBibliography(true); Assert.True(session.UseBibtex); Assert.Equal(version + 1, session.SelectionVersion);
            Assert.False(session.State!.IsCurrent); Assert.Same(oldOutput, session.State.LastSuccessful); Assert.Single(c.Workers.Created);
            await session.CompileAsync(); var held = await c.Workers.Next(); var oldTicket = session.LastTicket!;
            Assert.Equal(bibtex, Assert.Single(held.Context.Snapshot.Selection.Tools));
            session.SelectBibliography(false); Assert.False(session.UseBibtex); Assert.Equal(version + 2, session.SelectionVersion);
            Assert.False(session.State!.IsCurrent); Assert.Same(oldOutput, session.State.LastSuccessful); Assert.Equal(2, c.Workers.Created.Count);
            held.Reply.TrySetResult(held.Output()); var rejected = await oldTicket.Completion.WaitAsync(Bound); Assert.NotEqual(CompileLifecycleReason.Succeeded, rejected.Reason); Assert.Null(rejected.Output);
            Assert.Equal(commits, f.Repo.Commits); Assert.False(session.State!.IsCurrent);
            await session.CompileAsync(); var plain = await c.Workers.Next(); Assert.Empty(plain.Context.Snapshot.Selection.Tools);
            plain.Reply.TrySetResult(plain.Output()); await session.LastTicket!.Completion.WaitAsync(Bound); Assert.True(session.State!.IsCurrent);
        }
        finally { foreach (var worker in c.Workers.Created) worker.Reply.TrySetResult(worker.Output()); await Drain(session); }
    });
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Bibliography_resets_with_main_and_engine_on_project_or_same_project_session_change(bool takeover) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var c = new Configuration(selectableBibliography: true, tools: [CompilationTestWorkspace.Descriptor("bibtex")]); var session = Attach(f, c);
        try
        {
            await Select(f, session); session.SelectBibliography(true); Assert.True(session.UseBibtex);
            if (takeover) Assert.True(await f.Workspace.TakeOverAsync(CancellationToken.None));
            else { await f.Workspace.CloseAsync(CancellationToken.None); await f.Workspace.OpenAsync(f.Repo.Project.Id, CancellationToken.None); }
            Assert.False(session.UseBibtex); Assert.Null(session.MainFile); Assert.Empty(session.EngineChoice); Assert.Empty(c.Workers.Created);
            await Select(f, session); await session.CompileAsync(); var worker = await c.Workers.Next(); Assert.Empty(worker.Context.Snapshot.Selection.Tools);
            worker.Reply.TrySetResult(worker.Output()); await session.LastTicket!.Completion.WaitAsync(Bound);
        }
        finally { foreach (var worker in c.Workers.Created) worker.Reply.TrySetResult(worker.Output()); await Drain(session); }
    });
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Bibliography_cannot_substitute_unknown_or_ambiguous_declared_tools(bool ambiguous) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open();
        CompilationDescriptor[] tools = ambiguous ? [CompilationTestWorkspace.Descriptor("bibtex", "a"), CompilationTestWorkspace.Descriptor("bibtex", "b")] : [CompilationTestWorkspace.Descriptor("BibTeX"), CompilationTestWorkspace.Descriptor("unknown")];
        var c = new Configuration(selectableBibliography: true, tools: tools); var session = Attach(f, c);
        try
        {
            await Select(f, session); var version = session.SelectionVersion; Assert.False(session.CanSelectBibtex);
            session.SelectBibliography(true); Assert.False(session.UseBibtex); Assert.Equal(version, session.SelectionVersion); Assert.Contains("unavailable", session.Notice); Assert.Empty(c.Workers.Created);
        }
        finally { await Drain(session); }
    });
    [Fact] public void Actual_backend_handler_passes_explicit_selection_without_compiling() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var c = new Configuration(selectableBibliography: true, tools: [CompilationTestWorkspace.Descriptor("bibtex")]); var session = Attach(f, c);
        try
        {
            await Select(f, session); var panel = new Client.Pages.CompileControls();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(Client.Pages.CompileControls).GetProperty("Session", flags)!.SetValue(panel, session);
            var handler = typeof(Client.Pages.CompileControls).GetMethod("BibliographyChanged", flags)!;
            handler.Invoke(panel, [new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "Bibtex" }]); Assert.True(session.UseBibtex);
            handler.Invoke(panel, [new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "None" }]); Assert.False(session.UseBibtex);
            var version = session.SelectionVersion;
            handler.Invoke(panel, [new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "Biber" }]); Assert.Equal(version, session.SelectionVersion);
            handler.Invoke(panel, [new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "unknown" }]); Assert.Equal(version, session.SelectionVersion);
            Assert.Empty(c.Workers.Created);
        }
        finally { await Drain(session); }
    });
    [Fact] public void Biber_is_explicit_and_uses_exact_descriptor_with_stale_backend_rejection() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open();
        var biber = CompilationTestWorkspace.Descriptor("biber", "2.19", new string('b',64));
        var bibtex = CompilationTestWorkspace.Descriptor("bibtex", "0.99d", new string('c',64));
        var c = new Configuration(selectableBibliography:true, tools:[bibtex,biber]);var session=Attach(f,c);
        try
        {
            await Select(f,session);Assert.Equal(BibliographyToolKind.None,session.BibliographyTool);Assert.True(session.CanSelectBiber);
            Assert.True(f.Workspace.EditText(f.Main,@"\usepackage[backend=biber]{biblatex}"));
            await session.CompileAsync();var plain=await c.Workers.Next();Assert.Empty(plain.Context.Snapshot.Selection.Tools);
            plain.Reply.TrySetResult(plain.Output());await session.LastTicket!.Completion.WaitAsync(Bound);
            var version=session.SelectionVersion;var commits=f.Repo.Commits;
            session.SelectBibliography(BibliographyToolKind.Biber);Assert.Equal(version+1,session.SelectionVersion);Assert.False(session.UseBibtex);
            await session.CompileAsync();var held=await c.Workers.Next();var old=session.LastTicket!;
            Assert.Equal(biber,Assert.Single(held.Context.Snapshot.Selection.Tools));
            session.SelectBibliography(BibliographyToolKind.Bibtex);Assert.True(session.UseBibtex);
            held.Reply.TrySetResult(held.Output());Assert.NotEqual(CompileLifecycleReason.Succeeded,(await old.Completion.WaitAsync(Bound)).Reason);
            Assert.False(session.State!.IsCurrent);Assert.Equal(commits,f.Repo.Commits);
            await session.CompileAsync();var next=await c.Workers.Next();Assert.Equal(bibtex,Assert.Single(next.Context.Snapshot.Selection.Tools));
            next.Reply.TrySetResult(next.Output());await session.LastTicket!.Completion.WaitAsync(Bound);
            session.SelectBibliography(BibliographyToolKind.None);Assert.False(session.UseBibtex);Assert.Equal(3,c.Workers.Created.Count);
        }
        finally { foreach(var worker in c.Workers.Created)worker.Reply.TrySetResult(worker.Output());await Drain(session); }
    });
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Biber_unknown_or_ambiguous_tools_never_fall_back_to_Bibtex(bool duplicate) => Run(async () =>
    {
        await using var f=await CompilationTestWorkspace.Open();
        var tools=duplicate ? new[]{CompilationTestWorkspace.Descriptor("biber","a"),CompilationTestWorkspace.Descriptor("biber","b"),CompilationTestWorkspace.Descriptor("bibtex")}
            : new[]{CompilationTestWorkspace.Descriptor("Biber"),CompilationTestWorkspace.Descriptor("bibtex")};
        var c=new Configuration(selectableBibliography:true,tools:tools);var session=Attach(f,c);
        try
        {
            await Select(f,session);Assert.False(session.CanSelectBiber);var version=session.SelectionVersion;
            session.SelectBibliography(BibliographyToolKind.Biber);Assert.Equal(version,session.SelectionVersion);
            session.SelectBibliography((BibliographyToolKind)int.MaxValue);Assert.Equal(version,session.SelectionVersion);
            Assert.Equal(BibliographyToolKind.None,session.BibliographyTool);Assert.False(session.UseBibtex);Assert.Empty(c.Workers.Created);
        }
        finally {await Drain(session);}
    });
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Suite_jobs_survive_the_old_deadline_but_still_time_out_without_accepting_late_output(bool suite) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open();
        var c = new Configuration(selectionOptions: suite ? NativeCompilerSuiteContract.SelectionOptions(NativeCompilerEngine.LuaLaTeX) : null);
        var clock = new Clock(); var session = new CompileWorkspaceSession(f.Workspace, c, clock);
        session.AttachRenderer(Dispatcher());
        try
        {
            await Select(f, session); await session.CompileAsync();
            var worker = await c.Workers.Next(); var ticket = session.LastTicket!;
            clock.Advance(TimeSpan.FromSeconds(31)); await Task.Yield();
            if (suite)
            {
                Assert.False(ticket.Completion.IsCompleted);
                clock.Advance(TimeSpan.FromSeconds(90));
            }
            Assert.Equal(CompileLifecycleReason.TimedOut, (await ticket.Completion.WaitAsync(Bound)).Reason);
            worker.Reply.SetResult(worker.Output()); await Task.Yield();
            Assert.Null(session.DisplayedOutput);
        }
        finally
        {
            foreach (var worker in c.Workers.Created) worker.Reply.TrySetResult(worker.Output());
            await Drain(session);
        }
    });
    [Theory]
    [InlineData(NativeCompilerEngine.PdfLaTeX)] [InlineData(NativeCompilerEngine.LaTeXDvi)]
    [InlineData(NativeCompilerEngine.XeLaTeX)] [InlineData(NativeCompilerEngine.LuaLaTeX)]
    public void Graphics_profile_options_dispatch_from_app_selection_and_keep_suite_deadline(NativeCompilerEngine engine) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open();
        var options = NativeCompilerSuiteContract.SelectionOptions(NativeGraphicsNavigationContract.Profile, engine);
        var c = new Configuration(selectionOptions: options); var clock = new Clock();
        var session = new CompileWorkspaceSession(f.Workspace, c, clock); session.AttachRenderer(Dispatcher());
        try
        {
            Assert.True(f.Workspace.RenameFile(f.Main, "тези/my thesis.tex"));
            await session.RefreshToolsAsync(); session.SelectMain(f.Main); session.SelectEngine("contract");
            await session.CompileAsync();
            var worker = await c.Workers.Next(); var ticket = session.LastTicket!;
            Assert.Equal(options, worker.Context.Snapshot.Selection.Options);
            clock.Advance(TimeSpan.FromSeconds(31)); await Task.Yield();
            Assert.False(ticket.Completion.IsCompleted); // Native suite jobs retain their existing two-minute lifecycle.
            worker.Reply.SetResult(worker.Output());
            Assert.Equal(CompileLifecycleReason.Succeeded, (await ticket.Completion.WaitAsync(Bound)).Reason);
            Assert.True(session.State!.IsCurrent);
        }
        finally
        {
            foreach (var worker in c.Workers.Created) worker.Reply.TrySetResult(worker.Output());
            await Drain(session);
        }
    });
    [Theory] [InlineData(10)] [InlineData(11)] [InlineData(18)] [InlineData(99)]
    public void Reserved_or_unknown_native_options_do_not_enable_nested_native_roots(int phase) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open();
        var c = new Configuration(selectionOptions: [new("overclone-native-phase-contract", phase.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
        var session = Attach(f, c);
        try
        {
            Assert.True(f.Workspace.RenameFile(f.Main, "тези/my thesis.tex"));
            await session.RefreshToolsAsync(); session.SelectMain(f.Main); session.SelectEngine("contract");
            await session.CompileAsync();
            Assert.Null(session.LastTicket); Assert.Empty(c.Workers.Created); Assert.Null(session.DisplayedOutput);
        }
        finally { await Drain(session); }
    });
    private sealed class Lifetime : IAsyncDisposable
    { internal int Disposals; public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; } }
    private sealed class MultipleConfiguration : ICompileWorkspaceConfiguration
    {
        internal readonly Workers First = new(), Second = new();
        internal readonly List<CompileWorkspacePorts> Additional = [];
        private readonly CompileWorkspacePorts _first;
        public MultipleConfiguration(string? invalid = null)
        {
            var lifetime = new Lifetime();
            Task<CompileReadiness> Ready() => Task.FromResult(new CompileReadiness(true, "Ready", "Modeled engines"));
            _first = new("contract", "First engine", CompilationTestWorkspace.Descriptor(), [], [], First, null, Ready, lifetime);
            Additional.Add(new(invalid == "duplicate" ? "contract" : "xelatex", "Second engine", CompilationTestWorkspace.Descriptor("xetex"),
                [], [], Second, null, Ready, invalid == "lifetime" ? new Lifetime() : lifetime,
                SelectionOptions: [new("output-mode", "xdv")]));
            if (invalid == "count")
                for (int i = 0; i < 3; i++) Additional.Add(_first with { CapabilityId = "extra-" + i });
        }
        public Task<CompileWorkspaceConfigurationResult> OpenAsync() => Task.FromResult(new CompileWorkspaceConfigurationResult(
            new(true, "Ready", "Modeled engines"), _first, Additional));
    }
    private sealed class DeferredConfiguration : ICompileWorkspaceConfiguration
    {
        internal readonly TaskCompletionSource Permit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Opens { get; private set; }
        public async Task<CompileWorkspaceConfigurationResult> OpenAsync()
        {
            Opens++;
            await Permit.Task;
            return await new Configuration().OpenAsync();
        }
    }
    private sealed class Configuration(bool available = true, bool selectableBibliography = false, CompilationDescriptor[]? tools = null,
        IReadOnlyList<CompilationOption>? selectionOptions = null) : ICompileWorkspaceConfiguration
    {
        internal readonly Workers Workers = new(); internal readonly Lifetime Lifetime = new(); internal CompileReadiness Readiness = new(true, "Ready", "CONTRACT");
        public Task<CompileWorkspaceConfigurationResult> OpenAsync() => Task.FromResult(new CompileWorkspaceConfigurationResult(available ? Readiness : new(false, "Unavailable", "No runtime"), available ? new("contract", "Contract only", CompilationTestWorkspace.Descriptor(), [], tools ?? [], Workers, null, () => Task.FromResult(Readiness), Lifetime, SelectableBibliography: selectableBibliography, SelectionOptions: selectionOptions) : null));
    }
}
