using Client.Compilation;
using Client.Compilation.Lifecycle;
using Xunit;
using static Workspace.Tests.CompilationTestWorkspace;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.CompilationLifecycle;

public sealed class LifecycleAdditionalTests
{
    [Fact]
    public void Blocking_foreign_cancellation_callback_cannot_hold_disposal_or_next_worker() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        workers.Make = () =>
        {
            var w = workers.NewWorker();
            w.OnExecute = () => registration = w.Token.Register(() => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); });
            return w;
        };
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(2, long.MaxValue), new Clock());
        var first = service.Request(Options); var old = await workers.Next(); service.Cancel();
        Assert.Equal(CompileLifecycleReason.Cancelled, (await first.Completion).Reason); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            workers.Make = workers.NewWorker;
            var next = service.Request(Options); var fresh = await workers.Next(); fresh.Succeed();
            Assert.Equal(CompileLifecycleReason.Succeeded, (await next.Completion).Reason);
            var disposal = service.DisposeAsync(); Assert.True(disposal.IsCompletedSuccessfully); await disposal;
            Assert.False(release.Task.IsCompleted); Assert.False(old.Reply.Task.IsCompleted);
        }
        finally { release.TrySetResult(); old.Reply.TrySetException(new IOException("late secret")); registration.Unregister(); }
    });

    [Fact]
    public void Termination_fault_is_isolated_after_exactly_once_settlement() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        workers.Make = () => { var w = workers.NewWorker(); w.ThrowTerminate = true; return w; };
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(2, long.MaxValue), new Clock());
        var ticket = service.Request(Options); var worker = await workers.Next(); service.Cancel();
        var terminal = await ticket.Completion; await service.WhenIdle; worker.Succeed();
        Assert.Equal(CompileLifecycleReason.Cancelled, terminal.Reason); Assert.Same(terminal, await ticket.Completion);
        Assert.Equal(1, worker.Terminations); Assert.Null(service.State.LastSuccessful);
    });

    [Fact]
    public void Capture_deadline_is_covered_by_same_finite_job_deadline() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var gate = f.Hold(); var workers = new Workers(); var clock = new Clock();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(2, long.MaxValue), clock);
        var ticket = service.Request(Options); await gate.WaitReached(); clock.Advance(Options.Deadline);
        Assert.Equal(CompileLifecycleReason.TimedOut, (await ticket.Completion).Reason); await service.WhenIdle;
        Assert.False(gate.Release.Task.IsCompleted); Assert.Empty(workers.Created); Assert.Null(ticket.Snapshot);
    });

    [Fact]
    public void Selection_changed_during_real_capture_has_explicit_capture_stale_terminal() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var gate = f.Hold(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(2, long.MaxValue), new Clock());
        var ticket = service.Request(Options); await gate.WaitReached(); var a = f.Selection.Current.Selection!;
        f.Selection.SetSelection(Select(f.Main, "B")); f.Selection.SetSelection(a);
        gate.Release.SetResult(f.Repo.Content[gate.Hash]);
        Assert.Equal(CompileLifecycleReason.CaptureStale, (await ticket.Completion).Reason); Assert.Empty(workers.Created);
    });

    [Fact]
    public void Invalid_main_and_invalid_descriptor_metadata_never_start_worker() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(2, long.MaxValue), new Clock());
        f.Selection.SetSelection(Select(Client.Workspace.Domain.FileId.New()));
        Assert.Equal(CompileLifecycleReason.CaptureInvalid, (await service.Request(Options).Completion).Reason); await service.WhenIdle;
        f.Selection.SetSelection(Select(f.Main));
        var options = Options with { MaxDescriptorMetadataBytes = 1 };
        Assert.Equal(CompileLifecycleReason.InvalidOptions, (await service.Request(options).Completion).Reason); await service.WhenIdle;
        Assert.Empty(workers.Created);
    });

    [Fact]
    public void Invalid_request_does_not_invalidate_existing_current_output() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(2, long.MaxValue), new Clock());
        var ticket = service.Request(Options); (await workers.Next()).Succeed(); var output = (await ticket.Completion).Output!; await service.WhenIdle;
        Assert.Equal(CompileLifecycleReason.InvalidOptions, (await service.Request(null).Completion).Reason);
        Assert.True(service.State.IsCurrent); Assert.Same(output, service.State.LastSuccessful);
    });

    [Fact]
    public void Already_cancelled_caller_never_reads_or_dispatches() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); using var ct = new CancellationTokenSource(); ct.Cancel(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(2, long.MaxValue), new Clock());
        var reads = f.Repo.Reads; Assert.Equal(CompileLifecycleReason.Cancelled, (await service.Request(Options, ct.Token).Completion).Reason);
        Assert.Equal(reads, f.Repo.Reads); Assert.Empty(workers.Created);
    });

    [Fact]
    public void Disposal_with_native_slot_and_pending_is_prompt_and_late_reply_is_historical_only() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(2, long.MaxValue), new Clock());
        var first = service.Request(Options); var worker = await workers.Next(); var pending = service.Request(Options);
        var disposal = service.DisposeAsync(); Assert.True(disposal.IsCompletedSuccessfully); await disposal;
        Assert.Equal(CompileLifecycleReason.Disposed, (await first.Completion).Reason);
        Assert.Equal(CompileLifecycleReason.Disposed, (await pending.Completion).Reason); await service.WhenIdle;
        worker.Succeed(); Assert.Null(service.State.LastSuccessful); Assert.False(service.State.IsCurrent); Assert.Equal(1, worker.Terminations);
        Assert.True(f.Workspace.EditText(f.Main, "workspace remains independently usable"));
    });

    [Fact]
    public void Malformed_foreign_output_collection_exception_is_fixed_failure_not_exception_text() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(2, long.MaxValue), new Clock());
        var ticket = service.Request(Options); var worker = await workers.Next();
        worker.Reply.SetResult(worker.Output() with { Diagnostics = new ThrowingDiagnostics() });
        var result = await ticket.Completion; await service.WhenIdle;
        Assert.Equal(CompileLifecycleReason.InvalidOutput, result.Reason); Assert.DoesNotContain("secret", result.SafeMessage);
        Assert.Null(result.Output); Assert.NotNull(ticket.Snapshot); Assert.Null(service.State.LastSuccessful);
    });

    [Fact]
    public void Deadline_is_rechecked_after_synchronous_factory_return_before_worker_execution() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers(); var clock = new Clock();
        workers.Make = () => { clock.Advance(Options.Deadline); return workers.NewWorker(); };
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(2, long.MaxValue), clock);
        var result = await service.Request(Options).Completion; await service.WhenIdle;
        Assert.Equal(CompileLifecycleReason.TimedOut, result.Reason); Assert.Null(result.Output);
        var worker = Assert.Single(workers.Created); Assert.Null(worker.Context); Assert.Equal(1, worker.Terminations);
    });

    [Fact]
    public void Deadline_is_rechecked_after_actual_B2a_metadata_validation_before_publication() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers(); var clock = new Clock();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(2, long.MaxValue), clock);
        var ticket = service.Request(Options); var worker = await workers.Next();
        var metadata = new ClockMetadata(() => clock.Advance(Options.Deadline));
        worker.Reply.SetResult(worker.Output() with { Diagnostics = metadata });
        var result = await ticket.Completion; await service.WhenIdle;
        Assert.True(metadata.Reached); Assert.Equal(CompileLifecycleReason.TimedOut, result.Reason);
        Assert.Null(result.Output); Assert.Null(service.State.LastSuccessful); Assert.Equal(1, worker.Terminations);
    });

    private sealed class ClockMetadata(Action reached) : IReadOnlyList<CompileDiagnosticEntry>
    {
        internal bool Reached;
        public int Count { get { if (!Reached) { Reached = true; reached(); } return 0; } }
        public CompileDiagnosticEntry this[int index] => throw new IndexOutOfRangeException();
        public IEnumerator<CompileDiagnosticEntry> GetEnumerator() => Enumerable.Empty<CompileDiagnosticEntry>().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowingDiagnostics : IReadOnlyList<CompileDiagnosticEntry>
    {
        public int Count => throw new IOException("secret metadata port exception");
        public CompileDiagnosticEntry this[int index] => throw new IOException("secret");
        public IEnumerator<CompileDiagnosticEntry> GetEnumerator() => throw new IOException("secret");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(0, 1L)]
    [InlineData(1, 0L)]
    [InlineData(-1, 1L)]
    public void Invalid_lifecycle_limits_fail_construction_without_ports(int jobs, long epoch) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(jobs, epoch), new Clock()));
        Assert.Empty(workers.Created);
    });
}
