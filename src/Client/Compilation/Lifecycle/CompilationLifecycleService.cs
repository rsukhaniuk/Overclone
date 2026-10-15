using Client.Compilation.Presentation;
using Client.Workspace;
using Client.Workspace.Domain;

namespace Client.Compilation.Lifecycle;

/// <summary>Unregistered, engine-independent orchestration. All service and
/// workspace/selection operations MUST use the constructing single-thread UI
/// SynchronizationContext. Foreign tasks/tokens may finish on any thread; only
/// ticket settlement and bounded retirement are thread-safe outside that context.
/// Context progress and promptly returning synchronous ports/subscribers are
/// prerequisites, not a native termination guarantee.</summary>
public sealed class CompilationLifecycleService : IAsyncDisposable
{
    private readonly WorkspaceCoordinator _workspace;
    private readonly CompilationSnapshotFactory _capture;
    private readonly ICompilationWorkerFactory _workers;
    private readonly CompileLifecycleLimits _limits;
    private readonly CompileOperationPool _operations;
    private readonly TimeProvider _time;
    private readonly SynchronizationContext _owner;
    private readonly Action<Action>? _workspaceEventDispatch;
    private readonly int _thread;
    private CompileLifecycleTicket? _active, _pending, _latest;
    private AcceptedCompileOutput? _last;
    private ValidatedCompileAttempt? _lastAttempt;
    private bool _eligible, _attemptEligible, _disposed, _notifying;
    private long _epoch;
    private ProjectId? _project;
    private Guid _session;
    private Action? _changed;
    private TaskCompletionSource _idle = NewSignal();

    public CompilationLifecycleService(WorkspaceCoordinator workspace, CompilationSelectionState selection,
        ICompilationWorkerFactory workers, CompileLifecycleLimits limits, TimeProvider timeProvider,
        Action<Action>? workspaceEventDispatch = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(workers);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (limits is null || !limits.IsValid) throw new ArgumentOutOfRangeException(nameof(limits), "Compilation lifecycle limits are invalid.");
        _owner = SynchronizationContext.Current ?? throw new InvalidOperationException("Compilation lifecycle requires an owner UI context.");
        _thread = Environment.CurrentManagedThreadId;
        _workspace = workspace; _workers = workers; _limits = limits; _time = timeProvider;
        _workspaceEventDispatch = workspaceEventDispatch;
        _operations = new(limits.MaxUnsettledJobs);
        // B1 receives the workspace event FIRST, so its epoch is authoritative.
        _capture = new(workspace, selection);
        _workspace.WorkspaceChanged += ForwardWorkspaceChanged;
        _project = workspace.Project?.Id; _session = workspace.SessionId;
        _idle.SetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void VerifyAccess()
    {
        if (SynchronizationContext.Current != _owner || Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("Compilation lifecycle requires its owner UI context.");
    }

    public event Action Changed
    {
        add { VerifyAccess(); _changed += value; }
        remove { VerifyAccess(); _changed -= value; }
    }

    /// <summary>Invalidation signal, not one event per transition. Reentrant
    /// notifications are coalesced (not queued); each subscriber reads fresh
    /// State. Throwing subscribers are isolated and cannot suppress others.</summary>
    private void Notify()
    {
        if (_notifying) return;
        _notifying = true;
        try
        {
            if (_changed is { } handlers)
                foreach (Action subscriber in handlers.GetInvocationList())
                    try { subscriber(); } catch (Exception error) when (error is not OutOfMemoryException) { /* observer isolation */ }
        }
        finally { _notifying = false; }
    }

    public CompileLifecycleView State
    {
        get
        {
            VerifyAccess();
            if (_last is not null && !_capture.IsCurrent(_last.Snapshot)) _eligible = false;
            if (_lastAttempt is not null && !_capture.IsCurrent(_lastAttempt.Snapshot)) _attemptEligible = false;
            return new(_active is { IsSettled: false } a ? a.JobId : null,
                _pending is { IsSettled: false } p ? p.JobId : null, _last,
                !_disposed && _eligible && _last is not null, _disposed, _operations.Count,
                _lastAttempt, !_disposed && _attemptEligible && _lastAttempt is not null);
        }
    }

    /// <summary>Observed workflow idle, not proof retired foreign operations or
    /// native workers have stopped. No new waiter queue is allocated.</summary>
    public Task WhenIdle { get { VerifyAccess(); return _idle.Task; } }

    public CompileLifecycleTicket Request(CompileLifecycleOptions? options, CancellationToken caller = default)
    {
        VerifyAccess();
        var job = new CompileLifecycleTicket(options, caller);
        if (_disposed) { job.Finish(CompileLifecycleReason.Disposed); return job; }
        if (options is null || !options.IsValid) { job.Finish(CompileLifecycleReason.InvalidOptions); return job; }
        if (job.IsSettled) return job;
        _eligible = false; _attemptEligible = false; _latest = job;
        if (_idle.Task.IsCompleted) _idle = NewSignal();
        // Supersede any older pending intent BEFORE deciding to start directly or
        // queue. A reentrant Request during the active-to-pending handoff (Notify
        // fires before StartPending in ProcessAsync's finally) sees _active null
        // with _pending still set; starting the newer job directly must settle the
        // older pending promptly, not orphan it to execute as obsolete work later.
        _pending?.Finish(CompileLifecycleReason.Superseded);
        if (_active is null) { _pending = null; Start(job); }
        else _pending = job;
        Notify();
        return job;
    }

    public void Cancel()
    {
        VerifyAccess();
        if (_disposed) return;
        _eligible = false; _attemptEligible = false; _latest = null;
        StopActive(CompileLifecycleReason.Cancelled);
        _pending?.Finish(CompileLifecycleReason.Cancelled); _pending = null;
        SignalIdle(); Notify();
    }

    // B1 remains the FIRST synchronous subscriber and invalidates its epoch.
    // An explicit UI composition bridge may enter/dispatch the owner for B3 only.
    // The default path and every VerifyAccess guard remain unchanged.
    private void ForwardWorkspaceChanged()
    {
        if (_workspaceEventDispatch is null) WorkspaceChanged();
        else _workspaceEventDispatch(WorkspaceChanged);
    }

    private void WorkspaceChanged()
    {
        VerifyAccess();
        _eligible = false; _attemptEligible = false;
        var sessionChanged = _workspace.Project?.Id != _project || _workspace.SessionId != _session;
        _project = _workspace.Project?.Id; _session = _workspace.SessionId;
        StopActive(CompileLifecycleReason.Superseded);
        // In-session edits preserve the explicit pending intent, whose capture
        // has not happened. Close/open/session change invalidates that intent.
        if (sessionChanged) { _pending?.Finish(CompileLifecycleReason.Superseded); _pending = null; }
        _latest = _pending;
        SignalIdle(); Notify();
    }

    private void StopActive(CompileLifecycleReason reason)
    {
        if (_active is not { } job) return;
        job.Finish(reason);
        Terminate(job);
    }

    private static void Terminate(CompileLifecycleTicket job)
    {
        if (job.Worker is null || job.TerminationRequested) return;
        job.TerminationRequested = true;
        try { job.Worker.RequestTermination(job.JobId, job.Epoch, job.Reason); }
        catch (Exception error) when (error is not OutOfMemoryException) { /* a request is not native-stop proof */ }
    }

    private static void Observe(Task task)
        => _ = task.ContinueWith(static completed => { _ = completed.Exception; },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private void Start(CompileLifecycleTicket job)
    {
        _active = job;
        var operation = ProcessAsync(job);
        (_owner as ICompilationOwnedOperationObserver)?.ObserveOwnedOperation(operation, CompilationOwnedOperationKind.Workflow);
        Observe(operation);
    }

    private void StartPending()
    {
        if (_active is not null || _disposed) { SignalIdle(); return; }
        var job = _pending; _pending = null;
        if (job is not null && !job.IsSettled) Start(job);
        SignalIdle();
    }
    private void SignalIdle() { if (_active is null && _pending is null) _idle.TrySetResult(); }

    private async Task<bool> ReachedAsync(Task operation, Task deadline, CompileLifecycleTicket job)
    {
        await Task.WhenAny(operation, deadline, job.StopSignal.Task);
        VerifyAccess();
        if (job.IsSettled) return false;
        if (job.Caller.IsCancellationRequested) { job.Finish(CompileLifecycleReason.Cancelled); return false; }
        if (deadline.IsCompleted) { job.Finish(CompileLifecycleReason.TimedOut); return false; }
        return true;
    }

    private async Task ProcessAsync(CompileLifecycleTicket job)
    {
        // One deferred owner-context callback. No recursive synchronous pump or
        // unbounded notification/work queue when a port completes inline.
        await Task.Yield();
        CompileOperationPool.Lease? lease = null;
        CancellationTokenSource? lifetime = null, timerCancellation = null;
        Task? captureTask = null, workerTask = null, cancelTask = null;
        var stage = CompileLifecycleReason.CaptureUnavailable;
        try
        {
            VerifyAccess();
            if (job.IsSettled || _disposed) return;
            lease = _operations.Acquire();
            if (lease is null) { job.Finish(CompileLifecycleReason.ResourceLimit); return; }
            lifetime = new(); timerCancellation = new();
            var options = job.Options!;
            var deadline = Task.Delay(options.Deadline, _time, timerCancellation.Token);
            var capture = _capture.CaptureAsync(options.CaptureBudget, lifetime.Token);
            captureTask = capture;
            if (!await ReachedAsync(capture, deadline, job)) return;
            var captured = await capture;
            if (captured is not CompilationCaptureResult.Captured owned)
            {
                job.Finish(CaptureReason(captured)); return;
            }
            job.Record(owned.Snapshot); // retain owned bytes BEFORE transfer
            if (job.IsSettled) return;
            if (!_capture.IsCurrent(owned.Snapshot)) { job.Finish(CompileLifecycleReason.CaptureStale); return; }
            if (_epoch >= _limits.EpochLimit) { job.Finish(CompileLifecycleReason.EpochExhausted); return; }
            job.Epoch = ++_epoch; // ceiling checked before increment; no response authority
            var context = CompileRequestContext.Create(owned.Snapshot, job.Epoch,
                options.MaxDescriptorMetadataBytes, options.OutputFormat, options.OutputAdapter);
            if (context.Context is not { } trusted)
            {
                job.Finish(CompileLifecycleReason.InvalidOptions, issue: context.Failure); return;
            }
            if (deadline.IsCompleted) { job.Finish(CompileLifecycleReason.TimedOut); return; }
            stage = CompileLifecycleReason.TransportError;
            job.Worker = _workers.Create(job.JobId, job.Epoch);
            if (job.IsSettled || _disposed) return;
            if (deadline.IsCompleted) { job.Finish(CompileLifecycleReason.TimedOut); return; }
            if (job.Worker is null) { job.Finish(CompileLifecycleReason.TransportError); return; }
            var response = job.Worker.ExecuteAsync(job.JobId, trusted, options.OutputBudget, lifetime.Token);
            workerTask = response;
            if (response is null) { job.Finish(CompileLifecycleReason.TransportError); return; }
            if (!await ReachedAsync(response, deadline, job)) return;
            var input = await response;
            if (job.IsSettled) return;
            stage = CompileLifecycleReason.InvalidOutput;
            // Intrinsic budgets/copy/hash AND trust comparison are actual B2a.
            var envelope = CompileOutputEnvelope.Create(input!, options.OutputBudget);
            var accepted = envelope.Envelope?.TryAccept(trusted);
            if (accepted?.Output is not { } output)
            {
                job.Finish(CompileLifecycleReason.InvalidOutput, issue: envelope.Failure ?? accepted?.Failure); return;
            }
            if (job.Caller.IsCancellationRequested) { job.Finish(CompileLifecycleReason.Cancelled); return; }
            if (deadline.IsCompleted) { job.Finish(CompileLifecycleReason.TimedOut); return; }
            // A new explicit intent makes the old success obsolete. No artifact
            // is published from a queued-over, edited, reselected or closed job.
            if (_latest != job || !_capture.IsCurrent(owned.Snapshot))
            {
                job.Finish(CompileLifecycleReason.Superseded); return;
            }
            var attempt = new ValidatedCompileAttempt(job.JobId, output);
            var reason = output.Status switch
            {
                CompileOutputStatus.Succeeded => CompileLifecycleReason.Succeeded,
                CompileOutputStatus.Failed => CompileLifecycleReason.CompilerFailed,
                CompileOutputStatus.Cancelled => CompileLifecycleReason.WorkerCancelled,
                CompileOutputStatus.TimedOut => CompileLifecycleReason.WorkerTimedOut,
                _ => CompileLifecycleReason.Unsupported
            };
            var artifact = output.Status == CompileOutputStatus.Succeeded ? output : null;
            // Owner-context publication only after the atomic terminal winner.
            // Result metadata and artifact have the same winner; a cancellation
            // on another thread can neither publish a losing attempt nor split it.
            if (job.Finish(reason, artifact, attempt: attempt))
            {
                _lastAttempt = attempt; _attemptEligible = true;
                if (artifact is not null) { _last = artifact; _eligible = true; }
            }
        }
        catch (OperationCanceledException)
        {
            job.Finish(job.Caller.IsCancellationRequested ? CompileLifecycleReason.Cancelled : stage);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { job.Finish(stage); }
        finally
        {
            if (!job.IsSettled) job.Finish(_disposed ? CompileLifecycleReason.Disposed : CompileLifecycleReason.TransportError);
            Terminate(job); job.Worker = null;
            // Do not execute foreign token callbacks synchronously on the UI or
            // wait for them during disposal. A bounded lease observes them even
            // when a transport ignores cancellation or a callback never ends.
            if (lifetime is not null)
            {
                if (job.Reason != CompileLifecycleReason.Succeeded)
                    try { cancelTask = lifetime.CancelAsync(); }
                    catch (Exception error) when (error is not OutOfMemoryException) { cancelTask = Task.FromException(error); }
                lease!.Retire(lifetime, captureTask, workerTask, cancelTask);
            }
            if (timerCancellation is not null) { timerCancellation.Cancel(); timerCancellation.Dispose(); }
            if (_active == job) _active = null;
            if (!_disposed) Notify();
            StartPending();
        }
    }

    private static CompileLifecycleReason CaptureReason(CompilationCaptureResult capture) => capture switch
    {
        CompilationCaptureResult.NoProject => CompileLifecycleReason.NoProject,
        CompilationCaptureResult.NoSelection => CompileLifecycleReason.NoSelection,
        CompilationCaptureResult.InvalidInput => CompileLifecycleReason.CaptureInvalid,
        CompilationCaptureResult.QuotaExceeded => CompileLifecycleReason.CaptureQuota,
        CompilationCaptureResult.Cancelled => CompileLifecycleReason.CaptureCancelled,
        CompilationCaptureResult.Superseded => CompileLifecycleReason.CaptureStale,
        CompilationCaptureResult.Disposed => CompileLifecycleReason.Disposed,
        _ => CompileLifecycleReason.CaptureUnavailable
    };

    public ValueTask DisposeAsync()
    {
        VerifyAccess();
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true; _eligible = false; _attemptEligible = false; _latest = null;
        _workspace.WorkspaceChanged -= ForwardWorkspaceChanged;
        StopActive(CompileLifecycleReason.Disposed);
        _pending?.Finish(CompileLifecycleReason.Disposed); _pending = null;
        Observe(_capture.DisposeAsync().AsTask());
        SignalIdle(); Notify();
        _changed = null;
        return ValueTask.CompletedTask;
    }
}
