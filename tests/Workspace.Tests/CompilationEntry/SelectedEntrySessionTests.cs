using Client.Compilation;
using Client.Compilation.Lifecycle;
using Client.Compilation.Native;
using Client.Compilation.Presentation;
using Workspace.Tests.CompilationLifecycle;
using Xunit;

namespace Workspace.Tests.CompilationEntry;

// Registered selection/session seam with modeled workers. No native or browser claim.
public sealed class SelectedEntrySessionTests
{
    private sealed class Configuration(CompileWorkspacePorts ports) : ICompileWorkspaceConfiguration
    {
        public Task<CompileWorkspaceConfigurationResult> OpenAsync() => Task.FromResult(new CompileWorkspaceConfigurationResult(new(true, "Ready", "Modeled"), ports));
    }
    private static CompileWorkspaceSession Open(CompilationTestWorkspace f, LifecycleTestHost.Workers workers, bool legacy = false)
    {
        var config = new Configuration(new("selected-test", "Modeled", CompilationTestWorkspace.Descriptor(), [], [], workers, null,
            () => Task.FromResult(new CompileReadiness(true, "Ready", "Modeled")),
            SelectionOptions: legacy ? NativeAuthoringV4Contract.SelectionOptions : NativeSelectedEntryContract.SelectionOptions));
        var session = new CompileWorkspaceSession(f.Workspace, config);
        var ui = SynchronizationContext.Current!;
        session.AttachRenderer(action =>
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.Post(_ => { try { action(); done.SetResult(); } catch (Exception error) { done.SetException(error); } }, null);
            return done.Task;
        });
        return session;
    }
    private static async Task Close(CompileWorkspaceSession session, LifecycleTestHost.Workers workers)
    {
        foreach (var w in workers.Created) w.Reply.TrySetResult(w.Output());
        await session.DisposeAsync(); await session.Owner!.WhenSettled.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_B_A_rejects_held_original_even_when_final_root_and_input_bytes_match() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var a = f.Main;
        Assert.True(f.Workspace.RenameFile(a, "dir/paper.tex"));
        var b = f.Workspace.Files.Single(x => x.RelativePath == "chapter.tex").Id;
        var workers = new LifecycleTestHost.Workers(); var session = Open(f, workers);
        try
        {
            await session.RefreshToolsAsync(); session.SelectEngine("selected-test"); session.SelectMain(a); await session.CompileAsync();
            var old = await workers.Next(); var oldTicket = session.LastTicket!;
            Assert.Equal(NativeSelectedEntryContract.SelectionOptions, old.Context.Snapshot.Selection.Options);
            session.SelectMain(b); await session.CompileAsync(); var middle = await workers.Next();
            Assert.Equal(b, middle.Context.Snapshot.MainFileId); Assert.True(old.Terminations > 0);
            session.SelectMain(a); await session.CompileAsync(); var current = await workers.Next(); var currentTicket = session.LastTicket!;
            Assert.Equal(a, current.Context.Snapshot.MainFileId); Assert.True(middle.Terminations > 0);
            Assert.True(current.Context.WorkerEpoch > old.Context.WorkerEpoch);
            Assert.NotEqual(old.Context.SnapshotIdentity.SelectionVersion, current.Context.SnapshotIdentity.SelectionVersion);
            // Late original/middle attempts cannot install diagnostics, PDF or a current download candidate.
            old.Reply.SetResult(old.Output()); middle.Reply.SetResult(middle.Output());
            Assert.NotEqual(CompileLifecycleReason.Succeeded, (await oldTicket.Completion).Reason);
            Assert.Null(session.DisplayedOutput); Assert.False(session.State!.IsCurrent);
            current.Reply.SetResult(current.Output());
            Assert.Equal(CompileLifecycleReason.Succeeded, (await currentTicket.Completion).Reason);
            Assert.True(session.State!.IsCurrent); Assert.Equal(current.Context.RequestId, session.DisplayedOutput!.RequestId);
            session.SelectMain(b); Assert.False(session.State.IsCurrent); Assert.False(session.State.IsAttemptCurrent);
            session.SelectMain(a); Assert.False(session.State.IsCurrent); Assert.False(session.State.IsAttemptCurrent);
        }
        finally { await Close(session, workers); }
    });

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Source_generation_or_path_roundtrip_does_not_revalidate_old_result(bool rename) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var a = f.Main; f.Workspace.RenameFile(a, "dir/paper.tex");
        var workers = new LifecycleTestHost.Workers(); var session = Open(f, workers);
        try
        {
            await session.RefreshToolsAsync(); session.SelectMain(a); session.SelectEngine("selected-test"); await session.CompileAsync();
            var old = await workers.Next(); var ticket = session.LastTicket!;
            var text = f.Workspace.Files.Single(x => x.Id == a).Content!;
            if (rename) { Assert.True(f.Workspace.RenameFile(a, "other.tex")); Assert.True(f.Workspace.RenameFile(a, "dir/paper.tex")); }
            else { Assert.True(f.Workspace.EditText(a, "new generation Україна🙂")); Assert.True(f.Workspace.EditText(a, text)); }
            old.Reply.SetResult(old.Output()); Assert.NotEqual(CompileLifecycleReason.Succeeded, (await ticket.Completion).Reason);
            Assert.Null(session.DisplayedOutput); Assert.False(session.State!.IsCurrent);
            await session.CompileAsync(); var next = await workers.Next(); next.Reply.SetResult(next.Output());
            Assert.Equal(CompileLifecycleReason.Succeeded, (await session.LastTicket!.Completion).Reason);
            Assert.NotEqual(old.Context.SnapshotIdentity.WorkingGeneration, next.Context.SnapshotIdentity.WorkingGeneration);
        }
        finally { await Close(session, workers); }
    });

    [Theory]
    [InlineData("dir/paper.tex", false, true)]
    [InlineData("теза.tex", false, true)]
    [InlineData("space dir/paper.tex", false, true)]
    [InlineData("my thesis.tex", false, true)]
    [InlineData("dir/my thesis.tex", false, true)]
    [InlineData("dir/ my thesis.tex", false, false)]
    [InlineData("my thesis .tex", false, false)]
    [InlineData("my thesis.tex", true, false)]
    [InlineData("paper.txt", false, false)]
    [InlineData("paper.tex", true, false)]
    [InlineData("main.tex", true, true)]
    public void Selection_support_is_explicit_and_legacy_main_restriction_remains(string root, bool legacy, bool supported) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); Assert.True(f.Workspace.RenameFile(f.Main, root));
        var workers = new LifecycleTestHost.Workers(); var session = Open(f, workers, legacy);
        try
        {
            await session.RefreshToolsAsync(); session.SelectMain(f.Main); session.SelectEngine("selected-test"); await session.CompileAsync();
            if (supported)
            {
                var worker = await workers.Next(); Assert.Equal(root, worker.Context.Snapshot.Files.Single(x => x.FileId == f.Main).RelativePath);
                worker.Reply.SetResult(worker.Output()); Assert.Equal(CompileLifecycleReason.Succeeded, (await session.LastTicket!.Completion).Reason);
            }
            else { Assert.Empty(workers.Created); Assert.Null(session.LastTicket); }
        }
        finally { await Close(session, workers); }
    });
}
