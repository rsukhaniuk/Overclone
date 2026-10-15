using System.Runtime.ExceptionServices;
using Client.Compilation;
using Client.Compilation.Lifecycle;
using Xunit;
using static Workspace.Tests.CompilationTestWorkspace;

namespace Workspace.Tests.CompilationLifecycle;

// Modeled managed transport ONLY. Real WorkspaceCoordinator, B1 factory and B2a
// trust/ownership are exercised; no compiler/native/browser claim.
internal static class LifecycleTestHost
{
    internal static CompileLifecycleOptions Options => new(Budget,
        new(1024 * 1024, 65536, 32, 128, 4096, 8192), 65536, TimeSpan.FromSeconds(10));

    internal static void Run(Func<Task> test, OwnerContext? owner = null)
    {
        var previous = SynchronizationContext.Current;
        using var context = owner ?? new OwnerContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            ExceptionDispatchInfo? failure = null;
            try
            {
                var task = test();
                while (!task.IsCompleted) context.Step();
                task.GetAwaiter().GetResult();
            }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
            context.RequestTeardown();
            // Root completion is not owner quiescence. Only explicitly owned
            // capture/workflow/fixture tasks participate, never worker replies.
            while (!context.TryClose()) context.Step();
            failure?.Throw();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    internal enum PostStage { Entered, Enqueued }

    internal sealed class OwnerContext : SynchronizationContext, IDisposable, ICompilationOwnedOperationObserver
    {
        private readonly object _gate = new();
        private readonly Queue<(SendOrPostCallback, object?)> _queue = new();
        private readonly HashSet<Task> _owned = [];
        private readonly AutoResetEvent _ready = new(false);
        private int _posts;
        private bool _closed;
        internal Action<Task, CompilationOwnedOperationKind>? OperationObserved;
        internal Action<PostStage, SendOrPostCallback, object?>? PostProbe;
        internal Action? TeardownRequested;
        internal bool IsClosed { get { lock (_gate) return _closed; } }
        internal int PendingOwned { get { lock (_gate) return _owned.Count; } }
        internal int PendingPosts { get { lock (_gate) return _posts; } }
        internal int Queued { get { lock (_gate) return _queue.Count; } }
        public void ObserveOwnedOperation(Task operation, CompilationOwnedOperationKind kind)
        {
            ObserveFixtureOperation(operation);
            OperationObserved?.Invoke(operation, kind);
        }
        internal void ObserveFixtureOperation(Task operation)
        {
            lock (_gate)
            {
                if (_closed) throw new InvalidOperationException("Owned operation registered after owner close.");
                if (operation.IsCompleted || !_owned.Add(operation)) return;
            }
            _ = operation.ContinueWith(completed =>
            {
                lock (_gate)
                {
                    _owned.Remove(completed);
                    _ready.Set(); // same lock as removal/close, never after disposal
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_gate)
            {
                if (_closed) throw new InvalidOperationException("Post attempted after owner close.");
                _posts++;
            }
            try
            {
                PostProbe?.Invoke(PostStage.Entered, callback, state);
                lock (_gate)
                {
                    _queue.Enqueue((callback, state));
                    _ready.Set(); // enqueue and wakeup are atomic with close
                }
                PostProbe?.Invoke(PostStage.Enqueued, callback, state);
            }
            finally
            {
                lock (_gate) { _posts--; _ready.Set(); }
            }
        }
        internal void RequestTeardown() => TeardownRequested?.Invoke();
        internal bool TryClose()
        {
            lock (_gate)
            {
                if (_closed) return true;
                if (_owned.Count != 0 || _queue.Count != 0 || _posts != 0) return false;
                _closed = true;
                _ready.Dispose();
                return true;
            }
        }
        internal void Step()
        {
            (SendOrPostCallback Callback, object? State) work;
            lock (_gate)
            {
                if (_queue.TryDequeue(out work)) { }
                else work = default;
            }
            if (work.Callback is null)
            {
                // Existing progress failure bound, not a sleep/race oracle.
                Assert.True(_ready.WaitOne(TimeSpan.FromSeconds(15)), "Owner-context progress timed out.");
                lock (_gate) { if (!_queue.TryDequeue(out work)) return; }
            }
            work.Callback(work.State);
        }
        public void Dispose()
        {
            if (!TryClose()) throw new InvalidOperationException("Owner disposed before owned work drained.");
        }
    }

    internal sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        private readonly List<Timer> _timers = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state, _now + dueTime);
            _timers.Add(timer); return timer;
        }
        internal void Advance(TimeSpan delta)
        {
            _now += delta;
            foreach (var timer in _timers.ToArray())
                if (!timer.Disposed && timer.Due <= _now) { timer.Dispose(); timer.Callback(timer.State); }
        }
        private sealed class Timer(Clock clock, TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            internal bool Disposed;
            internal DateTimeOffset Due = due;
            internal TimerCallback Callback = callback;
            internal object? State = state;
            public bool Change(TimeSpan dueTime, TimeSpan period) { Due = clock._now + dueTime; return !Disposed; }
            public void Dispose() { Disposed = true; clock._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    internal sealed class Workers : ICompilationWorkerFactory
    {
        internal readonly List<Worker> Created = [];
        private readonly Queue<Worker> _ready = new();
        private TaskCompletionSource<Worker>? _waiting;
        internal Func<Worker>? Make;
        internal bool ThrowCreate, NullCreate;
        public ICompilationWorker? Create(CompileJobId id, long epoch)
        {
            if (ThrowCreate) throw new IOException("secret source and credential");
            if (NullCreate) return null;
            var worker = Make?.Invoke() ?? new Worker();
            worker.Id = id; worker.Epoch = epoch; Created.Add(worker);
            return worker;
        }
        internal void Started(Worker worker)
        {
            if (_waiting is { } waiting) { _waiting = null; waiting.SetResult(worker); }
            else _ready.Enqueue(worker);
        }
        internal Task<Worker> Next()
        {
            if (_ready.TryDequeue(out var worker)) return Task.FromResult(worker);
            Assert.Null(_waiting);
            _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        internal Worker NewWorker()
        {
            var worker = new Worker(); worker.Started = () => Started(worker); return worker;
        }
        internal Worker NewAsyncWorker()
        {
            var worker = new Worker(true); worker.Started = () => Started(worker); return worker;
        }
        internal Workers() => Make = NewWorker;
    }

    internal sealed class Worker : ICompilationWorker
    {
        // Inline completion permits deterministic observation of retired leases;
        // service continuations still resume through the owner context.
        internal readonly TaskCompletionSource<CompileOutputEnvelopeInput?> Reply;
        internal Worker(bool asynchronous = false)
            => Reply = new(asynchronous ? TaskCreationOptions.RunContinuationsAsynchronously : TaskCreationOptions.None);
        internal Action? Started;
        internal CompileJobId Id;
        internal long Epoch;
        internal CompileRequestContext Context = null!;
        internal CompileOutputBudget Budget = null!;
        internal CancellationToken Token;
        internal int Terminations;
        internal bool ThrowExecute, ForeignCancel, NullTask, ThrowTerminate;
        internal Action? OnExecute;
        public Task<CompileOutputEnvelopeInput?>? ExecuteAsync(CompileJobId id,
            CompileRequestContext context, CompileOutputBudget limits, CancellationToken cancellationToken)
        {
            Assert.Equal(Id, id); Assert.Equal(Epoch, context.WorkerEpoch);
            Context = context; Budget = limits; Token = cancellationToken;
            Started?.Invoke(); OnExecute?.Invoke();
            if (ThrowExecute) throw new IOException("secret source and credential");
            if (ForeignCancel) throw new OperationCanceledException("secret foreign cancellation");
            if (NullTask) return null;
            return Reply.Task;
        }
        public void RequestTermination(CompileJobId id, long epoch, CompileLifecycleReason reason)
        {
            Assert.Equal(Id, id); Assert.Equal(Epoch, epoch); Terminations++;
            if (ThrowTerminate) throw new IOException("secret termination detail");
        }
        internal CompileOutputEnvelopeInput Output(CompileOutputStatus status = CompileOutputStatus.Succeeded, byte[]? bytes = null)
        {
            var payload = status == CompileOutputStatus.Succeeded ? bytes ?? [37, 80, 68, 70, 0, 255] : null;
            return new()
            {
                RequestId = Context.RequestId,
                SnapshotIdentity = Context.SnapshotIdentity,
                InputContentDigest = Context.InputContentDigest,
                WorkerEpoch = Context.WorkerEpoch,
                Status = status,
                Payload = payload,
                ClaimedLength = payload?.LongLength ?? 0,
                ClaimedHash = payload is null ? null : Hash(payload),
                OutputFormat = Context.OutputFormat,
                OutputAdapter = Context.OutputAdapter
            };
        }
        internal void Succeed() => Reply.TrySetResult(Output());
    }
}
