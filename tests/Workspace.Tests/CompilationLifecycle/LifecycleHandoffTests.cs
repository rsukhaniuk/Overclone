using System.IO;
using System.Reflection;
using System.Text;
using Client.Compilation.Lifecycle;
using Client.Workspace;
using Client.Workspace.History;
using Xunit;
using static Workspace.Tests.CompilationTestWorkspace;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.CompilationLifecycle;

public sealed class LifecycleHandoffTests
{
    // Public-API regression adapted from the read-only lead probe. Assertions
    // deliberately live OUTSIDE the Changed subscriber: subscriber exceptions are
    // isolated and cannot suppress other subscribers or steal pending intent.
    [Fact]
    public void Reentrant_latest_request_supersedes_existing_pending() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open();
        var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var first = service.Request(Options);
        var old = await workers.Next();
        var pending = service.Request(Options);
        var idle = service.WhenIdle;
        Assert.False(idle.IsCompleted);
        CompileLifecycleTicket? third = null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += () =>
        {
            if (third is null && service.State.Active is null && service.State.Pending == pending.JobId)
            {
                third = service.Request(Options);
                entered.TrySetResult();
            }
        };
        old.Succeed();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(third);
        Assert.True(pending.Completion.IsCompleted, "Older pending intent must settle promptly before the newer worker completes.");
        Assert.Equal(CompileLifecycleReason.Superseded, (await pending.Completion).Reason);
        Assert.Null(pending.Snapshot);
        Assert.False(idle.IsCompleted);
        var newest = await workers.Next();
        Assert.Equal(third!.JobId, newest.Id);
        Assert.Same(third.Snapshot, newest.Context.Snapshot);
        newest.Succeed();
        var output = (await third.Completion).Output!;
        await idle;
        Assert.Equal(CompileLifecycleReason.Superseded, (await first.Completion).Reason);
        Assert.Same(output, service.State.LastSuccessful);
        Assert.True(service.State.IsCurrent);
        Assert.Null(service.State.Active);
        Assert.Null(service.State.Pending);
        Assert.Equal(2, workers.Created.Count);
    });

    [Fact]
    public void Multiple_notification_requests_keep_only_latest_pending_and_capture_at_dispatch() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open();
        var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var first = service.Request(Options);
        var one = await workers.Next();
        var second = service.Request(Options);
        var idle = service.WhenIdle;
        CompileLifecycleTicket? third = null, fourth = null, fifth = null;
        var handoff = false;
        var secondSettledBeforeLaterSubscriber = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += () =>
        {
            if (!handoff && service.State.Active is null && service.State.Pending == second.JobId)
            {
                handoff = true;
                third = service.Request(Options);
                secondSettledBeforeLaterSubscriber = second.Completion.IsCompleted;
            }
        };
        service.Changed += () =>
        {
            if (handoff && fourth is null)
            {
                fourth = service.Request(Options);
                fifth = service.Request(Options);
                entered.TrySetResult();
            }
        };
        one.Succeed();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(secondSettledBeforeLaterSubscriber);
        Assert.NotNull(third);
        Assert.NotNull(fourth);
        Assert.NotNull(fifth);
        Assert.Equal(CompileLifecycleReason.Superseded, (await second.Completion).Reason);
        Assert.Equal(CompileLifecycleReason.Superseded, (await fourth.Completion).Reason);
        Assert.Null(second.Snapshot);
        Assert.Null(fourth.Snapshot);
        Assert.Null(fifth.Snapshot);
        Assert.Equal(fifth!.JobId, service.State.Pending);
        Assert.False(idle.IsCompleted);
        var three = await workers.Next();
        Assert.Equal(third!.JobId, three.Id);
        Assert.True(f.Workspace.EditText(f.Main, "newest handoff bytes Україна🙂"));
        Assert.Equal(CompileLifecycleReason.Superseded, (await third.Completion).Reason);
        var five = await workers.Next();
        Assert.Equal(fifth.JobId, five.Id);
        Assert.True(five.Epoch > three.Epoch);
        Assert.Contains("newest handoff bytes Україна🙂", Encoding.UTF8.GetString(five.Context.Snapshot.Files.Single(x => x.FileId == f.Main).ByteSpan));
        three.Succeed();
        Assert.False(fifth.Completion.IsCompleted);
        Assert.False(idle.IsCompleted);
        five.Succeed();
        var output = (await fifth.Completion).Output!;
        await idle;
        Assert.Same(output, service.State.LastSuccessful);
        Assert.True(service.State.IsCurrent);
        Assert.Equal(CompileLifecycleReason.Superseded, (await first.Completion).Reason);
        Assert.Equal(3, workers.Created.Count);
        Assert.Null(service.State.Active);
        Assert.Null(service.State.Pending);
        Assert.Equal(0, f.Repo.Commits);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancel_or_dispose_at_notified_handoff_settles_pending_without_phantom_dispatch(bool dispose) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open();
        var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var first = service.Request(Options);
        var old = await workers.Next();
        var pending = service.Request(Options);
        var idle = service.WhenIdle;
        var fired = false;
        var prompt = true;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += () =>
        {
            if (!fired && service.State.Active is null && service.State.Pending == pending.JobId)
            {
                fired = true;
                if (dispose) prompt = service.DisposeAsync().IsCompletedSuccessfully;
                else service.Cancel();
                entered.TrySetResult();
            }
        };
        old.Succeed();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(prompt);
        Assert.Equal(dispose ? CompileLifecycleReason.Disposed : CompileLifecycleReason.Cancelled, (await pending.Completion).Reason);
        Assert.Equal(CompileLifecycleReason.Superseded, (await first.Completion).Reason);
        await idle;
        Assert.Null(pending.Snapshot);
        Assert.Single(workers.Created);
        Assert.Null(service.State.Active);
        Assert.Null(service.State.Pending);
        Assert.Null(service.State.LastSuccessful);
        Assert.Equal(1, old.Terminations);
    });

    [Fact]
    public void Invalid_reentrant_request_does_not_steal_previously_accepted_pending_intent() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open();
        var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var first = service.Request(Options);
        var one = await workers.Next();
        var pending = service.Request(Options);
        var idle = service.WhenIdle;
        CompileLifecycleTicket? invalid = null;
        service.Changed += () =>
        {
            if (invalid is null && service.State.Active is null && service.State.Pending == pending.JobId)
                invalid = service.Request(Options with { Deadline = TimeSpan.Zero });
        };
        one.Succeed();
        var two = await workers.Next();
        Assert.NotNull(invalid);
        Assert.Equal(CompileLifecycleReason.InvalidOptions, (await invalid.Completion).Reason);
        Assert.Equal(pending.JobId, two.Id);
        Assert.False(idle.IsCompleted);
        two.Succeed();
        Assert.Equal(CompileLifecycleReason.Succeeded, (await pending.Completion).Reason);
        await idle;
        Assert.Equal(CompileLifecycleReason.Superseded, (await first.Completion).Reason);
        Assert.Equal(2, workers.Created.Count);
    });

    [Fact]
    public void WhenIdle_stays_incomplete_through_active_pending_and_handoff_then_signals() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open();
        var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        Assert.True(service.WhenIdle.IsCompleted);
        var first = service.Request(Options);
        Assert.False(service.WhenIdle.IsCompleted);
        (await workers.Next()).Succeed();
        var output = (await first.Completion).Output!;
        await service.WhenIdle;
        Assert.True(service.WhenIdle.IsCompleted);
        Assert.Same(output, service.State.LastSuccessful);
        Assert.Null(service.State.Active);
        Assert.Null(service.State.Pending);
    });

    [Fact]
    public void Throwing_foreign_storage_callback_during_real_B1_read_does_not_break_disposal() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open();
        var proxy = DispatchProxy.Create<IProjectRepository, B3ThrowingReaderProxy>();
        var reader = (B3ThrowingReaderProxy)proxy;
        reader.Target = f.Repo;
        await using var workspace = new WorkspaceCoordinator(proxy, f.Lock, new HistoryService(proxy));
        await workspace.OpenAsync(f.Repo.Project.Id, None);
        var workers = new Workers();
        await using var service = new CompilationLifecycleService(workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var ticket = service.Request(Options);
        await reader.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var error = Record.Exception(() => Assert.True(service.DisposeAsync().IsCompletedSuccessfully));
        Assert.Null(error);
        Assert.Equal(CompileLifecycleReason.Disposed, (await ticket.Completion).Reason);
        await reader.CallbackReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.WhenIdle;
        Assert.Same(await ticket.Completion, await ticket.Completion);
        Assert.Empty(workers.Created);
        Assert.Null(service.State.LastSuccessful);
        Assert.DoesNotContain("storage cancellation", (await ticket.Completion).SafeMessage);
    });
}

// Public proxy required by DispatchProxy. This is a passing cross-port regression,
// not a B1 defect: B1 passes the caller token to foreign storage, while its own
// lifetime cancels only our WaitAsync. The throwing callback fires on lifetime
// cancellation and must not break disposal or settle a raw exception reason.
public class B3ThrowingReaderProxy : DispatchProxy
{
    public IProjectRepository Target = null!;
    public readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource CallbackReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == nameof(IProjectRepository.ReadContentAsync))
        {
            var token = (CancellationToken)args![1]!;
            token.Register(() => { CallbackReached.TrySetResult(); throw new IOException("controlled storage cancellation callback"); });
            Reached.TrySetResult();
            return new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }
        return method.Invoke(Target, args);
    }
}
