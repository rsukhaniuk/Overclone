using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Client.Compilation;
using Client.Compilation.Lifecycle;
using Client.Workspace.Domain;
using Client.Workspace.Transfer;
using Xunit;
using static Workspace.Tests.CompilationTestWorkspace;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.CompilationLifecycle;

public sealed class CompilationLifecycleTests
{
    [Fact]
    public void Dirty_capture_and_payload_ownership_use_real_B1_B2a_without_save_or_lock() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open();
        Assert.True(f.Workspace.EditText(f.Main, "unsaved Україна🙂"));
        var state = f.State(); var acquired = f.Lock.Acquires;
        var workers = new Workers(); var clock = new Clock();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), clock);
        var ticket = service.Request(Options); var worker = await workers.Next();
        Assert.Same(ticket.Snapshot, worker.Context.Snapshot);
        Assert.Contains("unsaved Україна🙂", Encoding.UTF8.GetString(ticket.Snapshot!.Files.Single(x => x.FileId == f.Main).ByteSpan));
        byte[] payload = [0, 255, 37, 4]; worker.Reply.SetResult(worker.Output(bytes: payload));
        var result = await ticket.Completion; await service.WhenIdle;
        Assert.Equal(CompileLifecycleReason.Succeeded, result.Reason);
        Assert.Same(worker.Context, result.Output!.RequestContext);
        payload[0] = 99; var copy = result.Output.ToPayloadArray()!; copy[1] = 99;
        Assert.Equal(new byte[] { 0, 255, 37, 4 }, result.Output.ToPayloadArray());
        Assert.Same(result.Output, service.State.LastSuccessful); Assert.True(service.State.IsCurrent);
        Assert.Equal(state, f.State()); Assert.Equal(0, f.Repo.Commits); Assert.Equal(acquired, f.Lock.Acquires);
        Assert.Equal(1, worker.Terminations);
    });

    [Fact]
    public void One_latest_pending_is_captured_only_at_dispatch_after_dirty_edit() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var first = service.Request(Options); var old = await workers.Next();
        var reads = f.Repo.Reads;
        var replaced = service.Request(Options); var latest = service.Request(Options);
        Assert.Equal(CompileLifecycleReason.Superseded, (await replaced.Completion).Reason);
        Assert.Null(replaced.Snapshot); Assert.Null(latest.Snapshot); Assert.Equal(reads, f.Repo.Reads);
        Assert.Equal(latest.JobId, service.State.Pending);
        f.Workspace.EditText(f.Main, "newest unsaved bytes");
        Assert.Equal(CompileLifecycleReason.Superseded, (await first.Completion).Reason);
        var fresh = await workers.Next();
        Assert.Contains("newest unsaved bytes", Encoding.UTF8.GetString(fresh.Context.Snapshot.Files.Single(x => x.FileId == f.Main).ByteSpan));
        old.Succeed(); Assert.False(latest.Completion.IsCompleted); Assert.Null(service.State.LastSuccessful);
        fresh.Succeed(); var output = (await latest.Completion).Output!; await service.WhenIdle;
        Assert.Same(fresh.Context, output.RequestContext); Assert.True(service.State.IsCurrent);
        Assert.Equal(0, f.Repo.Commits); Assert.Equal(2, workers.Created.Count);
    });

    [Fact]
    public void Successful_old_request_with_new_pending_intent_is_discarded() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var first = service.Request(Options); var one = await workers.Next();
        var second = service.Request(Options); one.Succeed();
        Assert.Equal(CompileLifecycleReason.Superseded, (await first.Completion).Reason);
        var two = await workers.Next(); Assert.Null(service.State.LastSuccessful);
        Assert.NotEqual(one.Context.RequestId, two.Context.RequestId); Assert.True(two.Epoch > one.Epoch);
        two.Succeed(); Assert.Equal(CompileLifecycleReason.Succeeded, (await second.Completion).Reason);
    });

    [Theory]
    [InlineData("request")]
    [InlineData("epoch")]
    [InlineData("snapshot")]
    [InlineData("digest")]
    [InlineData("format")]
    [InlineData("adapter")]
    [InlineData("null")]
    [InlineData("payload")]
    [InlineData("hash")]
    [InlineData("length")]
    [InlineData("log")]
    [InlineData("status")]
    public void Raw_outputs_pass_actual_B2a_intrinsic_and_expected_context_checks(string kind) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var ticket = service.Request(Options); var worker = await workers.Next(); var input = worker.Output();
        input = kind switch
        {
            "request" => input with { RequestId = CompileRequestId.New() },
            "epoch" => input with { WorkerEpoch = input.WorkerEpoch + 1 },
            "snapshot" => input with { SnapshotIdentity = input.SnapshotIdentity with { SessionId = Guid.NewGuid() } },
            "digest" => input with { InputContentDigest = new SHA256(new string('b', 64)) },
            "format" => input with { OutputFormat = new("foreign", "1", null) },
            "adapter" => input with { OutputAdapter = new("foreign", "1", null) },
            "payload" => input with { Payload = null },
            "hash" => input with { ClaimedHash = new SHA256(new string('b', 64)) },
            "length" => input with { ClaimedLength = -1 },
            "log" => input with { LogText = new string('x', Options.OutputBudget.MaxLogBytes + 1) },
            "status" => input with { Status = (CompileOutputStatus)99 },
            _ => input
        };
        worker.Reply.SetResult(kind == "null" ? null : input);
        var result = await ticket.Completion; await service.WhenIdle;
        Assert.Equal(CompileLifecycleReason.InvalidOutput, result.Reason); Assert.Null(result.Output);
        Assert.NotNull(result.OutputIssue); Assert.Null(service.State.LastSuccessful); Assert.Equal(1, worker.Terminations);
    });

    [Theory]
    [InlineData(CompileOutputStatus.Failed, CompileLifecycleReason.CompilerFailed)]
    [InlineData(CompileOutputStatus.Cancelled, CompileLifecycleReason.WorkerCancelled)]
    [InlineData(CompileOutputStatus.TimedOut, CompileLifecycleReason.WorkerTimedOut)]
    [InlineData(CompileOutputStatus.Unsupported, CompileLifecycleReason.Unsupported)]
    public void Trusted_non_success_never_rebrands_previous_artifact(CompileOutputStatus status, CompileLifecycleReason reason) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var first = service.Request(Options); (await workers.Next()).Succeed(); var historical = (await first.Completion).Output!;
        await service.WhenIdle;
        var next = service.Request(Options); var worker = await workers.Next(); worker.Reply.SetResult(worker.Output(status));
        Assert.Equal(reason, (await next.Completion).Reason); Assert.Null((await next.Completion).Output); await service.WhenIdle;
        Assert.Same(historical, service.State.LastSuccessful); Assert.False(service.State.IsCurrent);
    });

    [Theory]
    [InlineData("selection")]
    [InlineData("A-B-A")]
    [InlineData("edit")]
    [InlineData("close")]
    [InlineData("reopen")]
    [InlineData("project")]
    public void Historical_output_cannot_become_current_after_real_identity_changes(string change) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var request = service.Request(Options); (await workers.Next()).Succeed(); var output = (await request.Completion).Output!; await service.WhenIdle;
        var original = f.Selection.Current.Selection!;
        switch (change)
        {
            case "selection": f.Selection.SetSelection(Select(f.Main, "B")); break;
            case "A-B-A": f.Selection.SetSelection(Select(f.Main, "B")); f.Selection.SetSelection(original); break;
            case "edit": f.Workspace.EditText(f.Main, "changed"); break;
            case "close": await f.Workspace.CloseAsync(None); break;
            case "reopen": await f.Workspace.OpenAsync(f.Repo.Project.Id, None); break;
            case "project":
                var bytes = Bytes("new project");
                var staged = new StagedProject("new", ImmutableArray.Create(new StagedFile("main.tex", "text", false, bytes, Hash(bytes), bytes.Length)), []);
                var published = await f.Workspace.PublishImportAsync(staged, "new", None);
                await f.Workspace.OpenAsync(published.Project!.Id, None); break;
        }
        Assert.Same(output, service.State.LastSuccessful); Assert.False(service.State.IsCurrent); Assert.False(service.State.IsCurrent);
    });

    [Fact]
    public void Head_only_save_invalidation_is_detected_on_reads_without_workspace_event() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open();
        f.Workspace.EditText(f.Workspace.Files.Single(x => x.RelativePath == "empty.txt").Id, "held save");
        var gate = f.Repo.CommitGate = new(); var saving = f.Workspace.SaveAsync(None); await gate.WaitReached();
        var workers = new Workers(); await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var ticket = service.Request(Options); (await workers.Next()).Succeed(); var output = (await ticket.Completion).Output!; await service.WhenIdle;
        Assert.True(service.State.IsCurrent); var generation = f.Workspace.WorkingGeneration; var epoch = f.Capture.CurrentWorkspaceEpoch;
        gate.Release.SetResult(); Assert.Equal(CommitStatus.Saved, (await saving).Status);
        Assert.Equal(generation, f.Workspace.WorkingGeneration); Assert.Equal(epoch, f.Capture.CurrentWorkspaceEpoch);
        Assert.False(service.State.IsCurrent); Assert.Same(output, service.State.LastSuccessful);
        Assert.Equal(output.InputContentDigest, (await f.Snapshot()).ContentDigest);
    });

    [Fact]
    public void Head_only_change_rejects_pending_native_success_at_publication() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open();
        f.Workspace.EditText(f.Workspace.Files.Single(x => x.RelativePath == "empty.txt").Id, "save before capture");
        var gate = f.Repo.CommitGate = new(); var saving = f.Workspace.SaveAsync(None); await gate.WaitReached();
        var workers = new Workers(); await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var ticket = service.Request(Options); var worker = await workers.Next();
        gate.Release.SetResult(); await saving; worker.Succeed();
        Assert.Equal(CompileLifecycleReason.Superseded, (await ticket.Completion).Reason); Assert.Null(service.State.LastSuccessful);
    });

    [Fact]
    public void Selection_A_B_A_while_worker_active_cannot_publish_by_equal_digest() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var ticket = service.Request(Options); var worker = await workers.Next(); var a = f.Selection.Current.Selection!;
        f.Selection.SetSelection(Select(f.Main, "B")); f.Selection.SetSelection(a); worker.Succeed();
        Assert.Equal(CompileLifecycleReason.Superseded, (await ticket.Completion).Reason); Assert.Null(service.State.LastSuccessful);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Timeout_or_cancel_does_not_wait_for_ignored_token_and_next_epoch_succeeds(bool cancel) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers(); var clock = new Clock(); using var caller = new CancellationTokenSource();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(2, long.MaxValue), clock);
        var first = service.Request(Options, caller.Token); var old = await workers.Next();
        if (cancel) caller.Cancel(); else clock.Advance(Options.Deadline);
        var terminal = await first.Completion; await service.WhenIdle;
        Assert.Equal(cancel ? CompileLifecycleReason.Cancelled : CompileLifecycleReason.TimedOut, terminal.Reason);
        Assert.False(old.Reply.Task.IsCompleted); Assert.Equal(1, old.Terminations); Assert.Null(service.State.LastSuccessful);
        var second = service.Request(Options); var fresh = await workers.Next(); Assert.True(fresh.Epoch > old.Epoch);
        old.Reply.SetException(new IOException("late secret fault")); Assert.False(second.Completion.IsCompleted);
        fresh.Succeed(); Assert.Equal(CompileLifecycleReason.Succeeded, (await second.Completion).Reason); await service.WhenIdle;
        Assert.Same(terminal, await first.Completion); Assert.True(service.State.IsCurrent);
    });

    [Fact]
    public void Capacity_refuses_new_orphans_instead_of_accumulating_never_finishing_tasks() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers(); var clock = new Clock();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(1, long.MaxValue), clock);
        var first = service.Request(Options); var never = await workers.Next(); clock.Advance(Options.Deadline);
        Assert.Equal(CompileLifecycleReason.TimedOut, (await first.Completion).Reason); await service.WhenIdle;
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(CompileLifecycleReason.ResourceLimit, (await service.Request(Options).Completion).Reason); await service.WhenIdle;
            Assert.Null(service.State.LastSuccessful); Assert.Single(workers.Created); Assert.Equal(1, service.State.UnsettledJobs);
        }
        never.Reply.SetException(new IOException("late observed"));
    });

    [Fact]
    public void Caller_cancel_before_publication_wins_and_success_after_settlement_cannot_replace_it() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers(); using var caller = new CancellationTokenSource();
        // Force response observation back through the owner queue. Inline TCS
        // completion may already publish BEFORE Cancel, which is the opposite race.
        workers.Make = workers.NewAsyncWorker;
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var ticket = service.Request(Options, caller.Token); var worker = await workers.Next(); worker.Succeed();
        Assert.False(ticket.Completion.IsCompleted); // reached response, not publication
        caller.Cancel();
        var result = await ticket.Completion; await service.WhenIdle;
        Assert.Equal(CompileLifecycleReason.Cancelled, result.Reason); Assert.Null(service.State.LastSuccessful); Assert.Same(result, await ticket.Completion);
    });

    [Fact]
    public void Caller_cancel_after_publication_does_not_rewrite_success() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers(); using var caller = new CancellationTokenSource();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var ticket = service.Request(Options, caller.Token); (await workers.Next()).Succeed(); var result = await ticket.Completion; caller.Cancel();
        Assert.Equal(CompileLifecycleReason.Succeeded, (await ticket.Completion).Reason); Assert.Same(result.Output, service.State.LastSuccessful);
    });

    [Fact]
    public void Pending_caller_cancel_settles_immediately_without_capturing_it() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers(); using var caller = new CancellationTokenSource();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var first = service.Request(Options); var worker = await workers.Next(); var pending = service.Request(Options, caller.Token); caller.Cancel();
        Assert.Equal(CompileLifecycleReason.Cancelled, (await pending.Completion).Reason); Assert.Null(pending.Snapshot);
        worker.Succeed(); Assert.Equal(CompileLifecycleReason.Superseded, (await first.Completion).Reason); await service.WhenIdle;
        Assert.Single(workers.Created); Assert.Null(service.State.LastSuccessful);
    });

    [Theory]
    [InlineData("create")]
    [InlineData("null-create")]
    [InlineData("execute")]
    [InlineData("foreign-OCE")]
    [InlineData("fault-OCE")]
    [InlineData("null-task")]
    public void Throwing_or_malformed_transport_is_bounded_typed_and_redacted(string kind) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        workers.ThrowCreate = kind == "create"; workers.NullCreate = kind == "null-create";
        workers.Make = () => { var w = workers.NewWorker(); w.ThrowExecute = kind == "execute"; w.ForeignCancel = kind == "foreign-OCE"; w.NullTask = kind == "null-task"; if (kind == "fault-OCE") w.Reply.SetException(new OperationCanceledException("secret")); return w; };
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var result = await service.Request(Options).Completion; await service.WhenIdle;
        Assert.Equal(CompileLifecycleReason.TransportError, result.Reason); Assert.DoesNotContain("secret", result.SafeMessage);
        Assert.Null(result.Output); Assert.Null(service.State.LastSuccessful);
        if (workers.Created.Count > 0) Assert.Equal(1, workers.Created[0].Terminations);
    });

    [Theory]
    [InlineData("no-project")]
    [InlineData("no-selection")]
    [InlineData("quota")]
    [InlineData("read")]
    [InlineData("foreign-read-OCE")]
    public void Real_capture_terminals_are_typed_and_never_dispatch_transport(string kind) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        if (kind == "no-project") await f.Workspace.CloseAsync(None);
        if (kind == "no-selection") f.Selection.SetSelection(null);
        if (kind == "read") f.Repo.ReadOverride = _ => throw new IOException("secret source");
        if (kind == "foreign-read-OCE") f.Repo.ReadOverride = _ => throw new OperationCanceledException("secret");
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var options = kind == "quota" ? Options with { CaptureBudget = Budget with { MaxFileBytes = 1 } } : Options;
        var result = await service.Request(options).Completion; await service.WhenIdle;
        var expected = kind switch { "no-project" => CompileLifecycleReason.NoProject, "no-selection" => CompileLifecycleReason.NoSelection, "quota" => CompileLifecycleReason.CaptureQuota, _ => CompileLifecycleReason.CaptureUnavailable };
        Assert.Equal(expected, result.Reason); Assert.Empty(workers.Created); Assert.Null(result.Output); Assert.DoesNotContain("secret", result.SafeMessage);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Disposal_or_cancel_during_real_held_capture_settles_without_foreign_read_release(bool dispose) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var gate = f.Hold(); var first = service.Request(Options); await gate.WaitReached(); var pending = service.Request(Options);
        if (dispose) await service.DisposeAsync(); else service.Cancel();
        var expected = dispose ? CompileLifecycleReason.Disposed : CompileLifecycleReason.Cancelled;
        Assert.Equal(expected, (await first.Completion).Reason); Assert.Equal(expected, (await pending.Completion).Reason);
        Assert.False(gate.Release.Task.IsCompleted); await service.WhenIdle; Assert.Empty(workers.Created);
        Assert.True(f.Workspace.EditText(f.Main, "still usable")); Assert.Equal(0, f.Repo.Commits);
    });

    [Fact]
    public void Session_change_settles_active_and_pending_and_retains_no_new_artifact() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var active = service.Request(Options); var worker = await workers.Next(); var pending = service.Request(Options);
        await f.Workspace.CloseAsync(None);
        Assert.Equal(CompileLifecycleReason.Superseded, (await active.Completion).Reason);
        Assert.Equal(CompileLifecycleReason.Superseded, (await pending.Completion).Reason); await service.WhenIdle;
        worker.Succeed(); Assert.Null(service.State.LastSuccessful); Assert.Single(workers.Created);
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Malformed_options_fail_before_capture_or_worker_dispatch(int kind) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var reads = f.Repo.Reads;
        var options = kind switch
        {
            0 => null,
            1 => Options with { CaptureBudget = Budget with { MaxFiles = 0 } },
            2 => Options with { OutputBudget = Options.OutputBudget with { MaxPayloadBytes = 0 } },
            3 => Options with { MaxDescriptorMetadataBytes = 0 },
            4 => Options with { Deadline = TimeSpan.Zero },
            5 => Options with { Deadline = Timeout.InfiniteTimeSpan },
            _ => Options with { Deadline = TimeSpan.MaxValue }
        };
        Assert.Equal(CompileLifecycleReason.InvalidOptions, (await service.Request(options).Completion).Reason);
        Assert.Equal(reads, f.Repo.Reads); Assert.Empty(workers.Created);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Epoch_ceiling_or_actual_overflow_guard_refuses_new_worker(bool actualOverflow) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, actualOverflow ? long.MaxValue : 1), new Clock());
        if (actualOverflow) typeof(CompilationLifecycleService).GetField("_epoch", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, long.MaxValue);
        else { var first = service.Request(Options); (await workers.Next()).Succeed(); await first.Completion; await service.WhenIdle; }
        Assert.Equal(CompileLifecycleReason.EpochExhausted, (await service.Request(Options).Completion).Reason); await service.WhenIdle;
        Assert.Equal(actualOverflow ? 0 : 1, workers.Created.Count); Assert.False(service.State.IsCurrent);
    });

    [Fact]
    public void Reentrant_cancel_and_throwing_subscriber_cannot_poison_later_notifications() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var once = false; var reads = 0;
        service.Changed += () => { if (!once) { once = true; service.Cancel(); } throw new IOException("secret subscriber"); };
        service.Changed += () => { _ = service.State; reads++; };
        var ticket = service.Request(Options); Assert.Equal(CompileLifecycleReason.Cancelled, (await ticket.Completion).Reason); await service.WhenIdle;
        Assert.True(reads > 0); Assert.Empty(workers.Created);
        var second = service.Request(Options); (await workers.Next()).Succeed(); Assert.Equal(CompileLifecycleReason.Succeeded, (await second.Completion).Reason);
    });

    [Fact]
    public void Reentrant_disposal_before_capture_is_prompt_and_does_not_start_port() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var prompt = false;
        service.Changed += () => { prompt = service.DisposeAsync().IsCompletedSuccessfully; };
        var ticket = service.Request(Options); Assert.Equal(CompileLifecycleReason.Disposed, (await ticket.Completion).Reason); await service.WhenIdle;
        Assert.True(prompt); Assert.Empty(workers.Created); Assert.True(service.State.Disposed);
        Assert.Equal(CompileLifecycleReason.Disposed, (await service.Request(Options).Completion).Reason);
    });

    [Fact]
    public void Wrong_context_is_rejected_and_never_touches_workspace_or_port() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var reads = f.Repo.Reads;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => service.Request(Options)));
        Assert.Equal("Compilation lifecycle requires its owner UI context.", exception.Message);
        Assert.Equal(reads, f.Repo.Reads); Assert.Empty(workers.Created);
    });
}
