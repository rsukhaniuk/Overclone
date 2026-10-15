using Client.Compilation;
using Client.Compilation.Lifecycle;
using Xunit;
using Xunit.Abstractions;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.CompilationLifecycle;

public sealed class OwnedCleanupTests
{
    private readonly ITestOutputHelper _output;
    public OwnedCleanupTests(ITestOutputHelper output) => _output = output;
    [Theory]
    [InlineData("timeout", false)]
    [InlineData("cancel", false)]
    [InlineData("timeout", true)]
    public async Task Real_B1_continuation_must_finish_before_owner_close_even_after_test_failure(string mode, bool fail)
    {
        var expectedReason = mode switch
        {
            "timeout" => CompileLifecycleReason.TimedOut,
            "cancel" => CompileLifecycleReason.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown cleanup regression trigger.")
        };
        var owner = new OwnerContext();
        using var reached = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var teardown = new ManualResetEventSlim(false);
        Task? capture = null;
        var held = 0;
        var original = new InvalidOperationException("controlled original test failure");
        owner.OperationObserved = (operation, kind) =>
        {
            if (kind == CompilationOwnedOperationKind.Capture && !operation.IsCompleted) capture = operation;
        };
        owner.PostProbe = (stage, _, state) =>
        {
            // Test barrier ONLY: exact explicitly observed Task identity via the
            // delegate's public Target, no reflection/field/type-name matching.
            // The dispatcher FIX never inspects callback state or task internals.
            if (stage == PostStage.Entered && Thread.CurrentThread.IsThreadPoolThread
                && state is Action action && ReferenceEquals(action.Target, capture)
                && Interlocked.CompareExchange(ref held, 1, 0) == 0)
            {
                reached.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)), "release actual owned B1 Post");
            }
        };
        owner.TeardownRequested = () => teardown.Set();
        var controller = Task.Run(() =>
        {
            try
            {
                Assert.True(reached.Wait(TimeSpan.FromSeconds(10)), "real B1 continuation reached Post");
                Assert.True(teardown.Wait(TimeSpan.FromSeconds(10)), "root requested teardown");
                Assert.NotNull(capture); Assert.False(capture.IsCompleted);
                Assert.False(owner.IsClosed); Assert.True(owner.PendingOwned >= 1); Assert.True(owner.PendingPosts >= 1);
            }
            finally { release.Set(); }
        });
        var error = Record.Exception(() => Run(async () =>
        {
            await using var f = await CompilationTestWorkspace.Open();
            var workers = new Workers(); var clock = new Clock();
            await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(2, long.MaxValue), clock);
            var gate = f.Hold(); var ticket = service.Request(Options); await gate.WaitReached();
            Assert.NotNull(capture); Assert.False(capture.IsCompleted);
            if (mode == "cancel") service.Cancel();
            else if (mode == "timeout") clock.Advance(Options.Deadline);
            else throw new ArgumentOutOfRangeException(nameof(mode));
            var terminal = await ticket.Completion; await service.WhenIdle;
            Assert.Equal(expectedReason, terminal.Reason);
            Assert.True(reached.Wait(TimeSpan.FromSeconds(10)), "actual canceled capture continuation held");
            Assert.False(capture.IsCompleted); Assert.False(gate.Release.Task.IsCompleted); Assert.Empty(workers.Created);
            var disposal = service.DisposeAsync(); Assert.True(disposal.IsCompletedSuccessfully); await disposal;
            Assert.True(service.WhenIdle.IsCompleted); Assert.False(capture.IsCompleted);
            Assert.Null(ticket.Snapshot); Assert.Null(service.State.LastSuccessful);
            if (fail) throw original;
        }, owner));
        release.Set(); await controller.WaitAsync(TimeSpan.FromSeconds(10));
        if (fail) Assert.Same(original, error); else Assert.Null(error);
        Assert.Equal(1, held); Assert.True(capture!.IsCompleted);
        Assert.True(owner.IsClosed); Assert.Equal(0, owner.PendingOwned); Assert.Equal(0, owner.PendingPosts); Assert.Equal(0, owner.Queued);
    }

    // This asserts the measured pinned fixture's inline path, NOT a universal
    // API promise that every capture disposal must complete inline.
    [Fact]
    public void Direct_disposal_of_held_read_completes_capture_inline_in_pinned_fixture()
    {
        var owner = new OwnerContext(); Task? capture = null, readRelease = null;
        Task[] readSettlements = []; var capturePosts = 0;
        owner.OperationObserved = (task, kind) =>
        {
            if (kind == CompilationOwnedOperationKind.Capture && !task.IsCompleted) capture = task;
        };
        owner.PostProbe = (stage, _, state) =>
        {
            if (stage == PostStage.Entered && state is Action action && ReferenceEquals(action.Target, capture))
                Interlocked.Increment(ref capturePosts);
        };
        Run(async () =>
        {
            await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers(); var clock = new Clock();
            await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(2, long.MaxValue), clock);
            var gate = f.Hold(); readRelease = gate.Release.Task;
            var ticket = service.Request(Options); await gate.WaitReached(); readSettlements = f.Repo.PendingReadSettlements.ToArray();
            Assert.NotNull(capture); Assert.False(capture.IsCompleted); Assert.False(readRelease.IsCompleted);
            Assert.Equal(DateTimeOffset.UnixEpoch, clock.GetUtcNow());
            _output.WriteLine("INLINE_DISPOSE_TRIGGER read-held=True capture-pending=True deadline-not-advanced=True");
            var disposal = service.DisposeAsync(); Assert.True(disposal.IsCompletedSuccessfully); await disposal;
            Assert.Equal(CompileLifecycleReason.Disposed, (await ticket.Completion).Reason);
            Assert.True(capture.IsCompleted); Assert.False(readRelease.IsCompleted); Assert.Equal(0, capturePosts);
            Assert.Equal(DateTimeOffset.UnixEpoch, clock.GetUtcNow()); Assert.Empty(workers.Created);
            Assert.Null(ticket.Snapshot); Assert.Null(service.State.LastSuccessful);
            _output.WriteLine("INLINE_DISPOSE_RESULT prompt=True terminal=Disposed capture-complete=True read-held=True capture-posts=0");
            await service.WhenIdle;
        }, owner);
        Assert.True(capture!.IsCompleted); Assert.True(readRelease!.IsCompletedSuccessfully); Assert.NotEmpty(readSettlements);
        Assert.All(readSettlements, task => Assert.True(task.IsCompletedSuccessfully)); Assert.Equal(0, capturePosts);
        Assert.True(owner.IsClosed); Assert.Equal(0, owner.PendingOwned); Assert.Equal(0, owner.PendingPosts); Assert.Equal(0, owner.Queued);
        _output.WriteLine("INLINE_DISPOSE_CLEANUP capture-complete=True reads-settled=True owner-closed=True owned=0 posts=0 queued=0");
    }

    [Fact]
    public async Task Direct_disposal_must_keep_real_finite_read_Post_alive_until_fixture_cleanup()
    {
        var owner = new OwnerContext(); Task? read = null, readAtTrigger = null, readAtTeardown = null;
        Task? root = null, idle = null, fixtureCleanup = null, producer = null, dataGate = null;
        Task[] readSettlements = []; var observations = 0; var held = 0; var checkpoints = 0; var ownerThread = 0;
        Exception? checkpointError = null;
        using var reached = new ManualResetEventSlim(false); using var release = new ManualResetEventSlim(false);
        using var teardown = new ManualResetEventSlim(false);
        owner.PostProbe = (stage, _, state) =>
        {
            // Actual owner-captured ReadCoreAsync continuation, not a manual
            // Post or inferred parent/child workflow callback identity.
            if (stage == PostStage.Entered && Thread.CurrentThread.IsThreadPoolThread
                && Environment.CurrentManagedThreadId != ownerThread && !ReferenceEquals(SynchronizationContext.Current, owner)
                && state is Action action && ReferenceEquals(action.Target, read)
                && Interlocked.CompareExchange(ref held, 1, 0) == 0)
            {
                _output.WriteLine("FINITE_READ_POST_REACHED exact-observed-read=True off-owner=True before-enqueue=True");
                reached.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10)), "release actual finite read Post");
            }
        };
        owner.TeardownRequested = () =>
        {
            checkpointError = Record.Exception(() =>
            {
                Assert.Same(owner, SynchronizationContext.Current);
                Assert.NotNull(root); Assert.True(root.IsCompletedSuccessfully);
                Assert.NotNull(read); Assert.Same(readAtTrigger, read); Assert.False(read.IsCompleted);
                Assert.NotNull(dataGate); Assert.True(dataGate.IsCompletedSuccessfully);
                Assert.NotNull(idle); Assert.True(idle.IsCompletedSuccessfully);
                Assert.NotNull(fixtureCleanup); Assert.False(fixtureCleanup.IsCompleted);
                Assert.False(owner.IsClosed); Assert.True(owner.PendingOwned > 0); Assert.True(owner.PendingPosts > 0);
                readAtTeardown = read; checkpoints++;
                _output.WriteLine("FINITE_READ_TEARDOWN root-complete=True same-read-pending=True data-supplied=True idle-complete=True fixture-cleanup-pending=True owner-open=True owned-nonzero=True posts-nonzero=True");
            });
            teardown.Set(); // report assertions only after normal draining
        };
        var controller = Task.Run(() =>
        {
            try
            {
                Assert.True(reached.Wait(TimeSpan.FromSeconds(10)), "actual read continuation reached Post");
                Assert.True(teardown.Wait(TimeSpan.FromSeconds(10)), "root requested finite read teardown");
                Assert.Null(checkpointError);
            }
            finally { release.Set(); }
        });
        async Task Body()
        {
            ownerThread = Environment.CurrentManagedThreadId;
            var f = await CompilationTestWorkspace.Open();
            try
            {
                f.Repo.ReadOperationObserved = operation =>
                {
                    if (operation.IsCompleted) return;
                    Assert.Null(read); read = operation; observations++;
                };
                var workers = new Workers(); var clock = new Clock();
                await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(2, long.MaxValue), clock);
                var gate = f.Hold(); dataGate = gate.Release.Task;
                var ticket = service.Request(Options); await gate.WaitReached(); readSettlements = f.Repo.PendingReadSettlements.ToArray();
                Assert.NotNull(read); Assert.False(read.IsCompleted); Assert.False(dataGate.IsCompleted);
                producer = Task.Run(() => gate.Release.SetResult(f.Repo.Content[gate.Hash]));
                Assert.True(reached.Wait(TimeSpan.FromSeconds(10)), "real read Post held before direct disposal");
                Assert.True(dataGate.IsCompletedSuccessfully); Assert.False(read.IsCompleted); readAtTrigger = read;
                Assert.Equal(DateTimeOffset.UnixEpoch, clock.GetUtcNow());
                _output.WriteLine("FINITE_READ_DISPOSE_TRIGGER data-supplied=True same-read-pending=True actual-post-held=True deadline-not-advanced=True");
                var disposal = service.DisposeAsync(); Assert.True(disposal.IsCompletedSuccessfully); await disposal;
                Assert.Equal(CompileLifecycleReason.Disposed, (await ticket.Completion).Reason);
                idle = service.WhenIdle; await idle; // read callback is NOT the workflow callback
                Assert.False(read.IsCompleted); Assert.True(dataGate.IsCompletedSuccessfully);
                Assert.Empty(workers.Created); Assert.Null(ticket.Snapshot); Assert.Null(service.State.LastSuccessful);
                Assert.Equal(DateTimeOffset.UnixEpoch, clock.GetUtcNow());
                _output.WriteLine("FINITE_READ_DISPOSE_RESULT prompt=True terminal=Disposed idle-complete=True same-read-pending=True data-supplied=True worker-created=False artifact=False");
            }
            finally
            {
                fixtureCleanup = f.DisposeAsync().AsTask(); owner.ObserveFixtureOperation(fixtureCleanup);
            }
        }
        var error = Record.Exception(() => Run(() => root = Body(), owner));
        release.Set();
        var controllerError = await Record.ExceptionAsync(() => controller.WaitAsync(TimeSpan.FromSeconds(10)));
        Exception? producerError = producer is null ? null : await Record.ExceptionAsync(() => producer.WaitAsync(TimeSpan.FromSeconds(10)));
        // Preserve original root/checkpoint/producer exception identities after
        // owned cleanup; no checkpoint exception can prevent the normal drain.
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        if (checkpointError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(checkpointError).Throw();
        if (controllerError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(controllerError).Throw();
        if (producerError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(producerError).Throw();
        Assert.Equal(1, observations); Assert.Equal(1, held); Assert.Equal(1, checkpoints);
        Assert.Same(readAtTrigger, read); Assert.Same(readAtTeardown, read); Assert.True(read!.IsCompletedSuccessfully);
        Assert.True(root!.IsCompletedSuccessfully); Assert.True(producer!.IsCompletedSuccessfully); Assert.True(idle!.IsCompletedSuccessfully);
        Assert.True(fixtureCleanup!.IsCompletedSuccessfully); Assert.True(dataGate!.IsCompletedSuccessfully); Assert.NotEmpty(readSettlements);
        Assert.All(readSettlements, task => Assert.True(task.IsCompletedSuccessfully));
        Assert.True(owner.IsClosed); Assert.Equal(0, owner.PendingOwned); Assert.Equal(0, owner.PendingPosts); Assert.Equal(0, owner.Queued);
        _output.WriteLine("FINITE_READ_CLEANUP same-read-complete=True producer-complete=True fixture-cleanup-complete=True idle-complete=True reads-settled=True owner-closed=True owned=0 posts=0 queued=0");
    }

    [Fact]
    public async Task Enqueued_callback_can_finish_root_but_inflight_Post_must_finish_before_close()
    {
        var owner = new OwnerContext();
        using var enqueued = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var teardown = new ManualResetEventSlim(false);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SendOrPostCallback callback = _ => delivered.SetResult();
        owner.PostProbe = (stage, cb, _) =>
        {
            if (stage == PostStage.Enqueued && ReferenceEquals(cb, callback))
            { enqueued.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10)), "complete original Post"); }
        };
        owner.TeardownRequested = () => teardown.Set();
        var controller = Task.Run(() =>
        {
            try
            {
                Assert.True(enqueued.Wait(TimeSpan.FromSeconds(10))); Assert.True(teardown.Wait(TimeSpan.FromSeconds(10)));
                Assert.False(owner.IsClosed); Assert.True(owner.PendingPosts >= 1);
            }
            finally { release.Set(); }
        });
        Task? post = null;
        Run(async () => { post = Task.Run(() => owner.Post(callback, null)); await delivered.Task; }, owner);
        await controller.WaitAsync(TimeSpan.FromSeconds(10)); await post!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(owner.IsClosed); Assert.Equal(0, owner.PendingPosts); Assert.Equal(0, owner.Queued);
        Assert.Throws<InvalidOperationException>(() => owner.Post(callback, null)); // explicit rejection, never Set on disposed event
        Assert.Equal(0, owner.Queued); owner.Dispose();
    }

    [Fact]
    public void Never_finishing_worker_is_not_an_owned_cleanup_activity_or_disposal_wait()
    {
        var owner = new OwnerContext(); var observed = new List<Task>();
        owner.OperationObserved = (task, _) => observed.Add(task);
        Task? foreign = null;
        Run(async () =>
        {
            await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
            await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(2, long.MaxValue), new Clock());
            var ticket = service.Request(Options); var worker = await workers.Next(); foreign = worker.Reply.Task;
            service.Cancel(); Assert.Equal(CompileLifecycleReason.Cancelled, (await ticket.Completion).Reason); await service.WhenIdle;
            var disposal = service.DisposeAsync(); Assert.True(disposal.IsCompletedSuccessfully); await disposal;
            Assert.False(foreign.IsCompleted); Assert.Equal(1, service.State.UnsettledJobs);
            Assert.DoesNotContain(foreign, observed); Assert.Null(service.State.LastSuccessful);
        }, owner);
        Assert.False(foreign!.IsCompleted); Assert.True(owner.IsClosed); Assert.Equal(0, owner.PendingOwned);
    }

    [Fact]
    public void Fixture_joins_its_finite_read_without_rethrowing_the_already_consumed_port_error()
    {
        var owner = new OwnerContext();
        Run(async () =>
        {
            await using var f = await CompilationTestWorkspace.Open(); var gate = f.Hold();
            var operation = f.Capture.CaptureAsync(CompilationTestWorkspace.Budget, CancellationToken.None);
            await gate.WaitReached(); gate.Release.SetException(new IOException("controlled finite read failure"));
            Assert.IsType<CompilationCaptureResult.Unavailable>(await operation);
            Assert.NotEmpty(f.Repo.PendingReadSettlements);
        }, owner);
        Assert.True(owner.IsClosed); Assert.Equal(0, owner.PendingOwned); Assert.Equal(0, owner.Queued);
    }
}
