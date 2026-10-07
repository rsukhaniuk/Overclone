using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Client.Workspace;

namespace Client.Analysis;

// AnalysisCoordinator (M2a-2b-2a) is C# orchestration ONLY. It debounces and
// single-flights independent source analysis over the REAL WorkspaceCoordinator
// and the existing ISourceAnalyzer (the Rust worker adapter), using the
// existing AnalysisSnapshotFactory to capture immutable snapshots without
// forcing a save. It owns no parser/rule/source-mapping policy (Rust owns
// analysis; C# owns orchestration). No diagnostics UI, DI registration, JS,
// storage schema, history/transfer, Program.cs or compose changes happen here.
//
// INVARIANTS (see docs/milestones/m2-source-analysis.md section D):
// - ONE AnalysisSnapshotFactory is created/owned FIRST, then this coordinator
//   subscribes its own WorkspaceChanged handler, so the factory's epoch bump
//   fires before this handler observes each raise.
// - IMMEDIATE INVALIDATION: the synchronous WorkspaceChanged handler clears
//   the usable Completed/visible report and bumps the local intent BEFORE it
//   schedules/awaits any debounce, and it ALWAYS raises Changed (including the
//   NoProject/close case) so a UI subscriber cannot keep a stale report.
// - BOUNDED SCHEDULING: at most ONE active capture/analyze/epoch workflow and
//   ONE replaceable latest queued intent. Debounce/timer ownership is
//   allocated under the scheduling lock BEFORE notifying, and re-checked after
//   a reentrant subscriber returns, so a stale outer scheduling can neither
//   create work after Cancel/Dispose nor overwrite a newer debounce reference.
// - PUBLISH FRESHNESS: a Completed is eligible ONLY if, after EVERY await and
//   immediately before final synchronous publication, ALL of: not disposed,
//   job not cancelled, intent still latest, CURRENT workspace
//   projectId/sessionId/workingGeneration match, factory epoch matches the
//   snapshot LocalEpoch, current profile matches, result SnapshotId/profile
//   match, and IsCurrentWorkerEpochAsync returns true; identity is RE-CHECKED
//   after the awaited worker-epoch check (TOCTOU). Stale failures never repaint.
// - EVERY CURRENT TERMINAL JOB REACHES A TERMINAL STATE: an adapter/factory
//   Cancelled/Superseded or a caught OperationCanceledException for the
//   still-latest intent publishes a typed Cancelled/Superseded (never leaves
//   Running); a stale job (intent bumped) never overrides a newer
//   Pending/Running/Completed/NoProject/Disposed. Explicit Cancel semantics
//   and no-implicit-restart are preserved.
// - REENTRANT NOTIFICATION: Changed is delivered per-subscriber; before each
//   delivery the passed state is confirmed still CURRENT (ReferenceEquals), so
//   an obsolete notification is never delivered as current after an earlier
//   subscriber mutated state; a faulting subscriber is isolated individually.
// - CANCELLATION/DISPOSAL: Cancel clears queued/debounce and the usable
//   report, bumps intent, cancels the active token, never silently queues a
//   new job. Dispose is idempotent, unsubscribes, cancels promptly without
//   blocking on a non-cooperative analyzer/import, disposes ONLY the owned
//   factory (never the injected coordinator/analyzer); a late completion after
//   dispose never publishes, schedules or emits UI changes.
//
// No Save/Flush, no writer lock, no polling/while-loop, no Task.Run, no unbounded
// Task queue, no fire-and-forget rejecting tasks. All async workflow tasks and
// cancellation cleanup are observed; expected cancellation is normal. An
// unexpected scheduling/delay failure surfaces an honest terminal Failed state
// for the current intent (never silently leaves Pending forever) and is
// observed (not swallowed).

public sealed class AnalysisCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(300);

    private readonly WorkspaceCoordinator _coordinator;
    private readonly ISourceAnalyzer _analyzer;
    private readonly AnalysisSnapshotFactory _factory; // owned (disposed here)
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly TimeSpan _debounce;

    private readonly object _lock = new();
    // ---- under _lock (NO await while holding _lock) ----
    private long _intent;                   // monotonically increasing intent id
    private long _activeIntent = -1;        // intent id that owns _active
    private bool _active;                  // a capture/analyze workflow is in flight
    private bool _ready;                    // debounce matured, waiting to start
    private bool _queued;                   // a latest queued intent exists
    private AnalysisProfile _queuedProfile; // the queued intent's profile
    private AnalysisProfile _profile;       // currently selected profile
    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _activeCts;
    private Task? _debounceTask;
    private Task? _workflowTask;
    private AnalysisState _state = new AnalysisState.NoProject();
    private bool _disposed;
    // Changed backing delegate + lock-protected accessors so RaiseChanged can
    // snapshot a stable invocation list and reentrant subscribe/unsubscribe
    // during delivery cannot corrupt iteration.
    private Action<AnalysisState>? _changed;

    /// <summary>Construct the coordinator. The factory is created/owned FIRST
    /// so its WorkspaceChanged handler (epoch bump) fires before this
    /// coordinator's handler on every raise. If a project is already open, the
    /// first analysis is scheduled (debounced). The injected
    /// <paramref name="coordinator"/> and <paramref name="analyzer"/> are NOT
    /// owned or disposed here (future DI owns them).</summary>
    /// <param name="coordinator">The real workspace coordinator (source of
    /// current identity; never owned/disposed here).</param>
    /// <param name="analyzer">The existing source analyzer (Rust worker
    /// adapter); never owned/disposed here.</param>
    /// <param name="debounce">Debounce window after each invalidation. Default
    /// 300ms. Must be non-negative.</param>
    /// <param name="delayAsync">Optional SMALL scheduling seam for tests (a
    /// controllable replacement for Task.Delay). Default Task.Delay.</param>
    public AnalysisCoordinator(
        WorkspaceCoordinator coordinator,
        ISourceAnalyzer analyzer,
        TimeSpan? debounce = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(analyzer);
        var db = debounce ?? DefaultDebounce;
        if (db < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(debounce), "debounce must be non-negative");
        _coordinator = coordinator;
        _analyzer = analyzer;
        _debounce = db;
        _delayAsync = delayAsync ?? DefaultDelay;
        _profile = AnalysisProfile.EngineStructural();
        // Owned factory, subscribed first.
        _factory = new AnalysisSnapshotFactory(coordinator);
        _coordinator.WorkspaceChanged += OnWorkspaceChanged;
        if (_coordinator.IsOpen)
            Schedule(immediate: false);
    }

    private static Task DefaultDelay(TimeSpan d, CancellationToken ct) => Task.Delay(d, ct);

    /// <summary>Current immutable coordinator state (snapshot under the lock).
    /// Only <see cref="AnalysisState.Completed"/> exposes a usable report.</summary>
    public AnalysisState State
    {
        get { lock (_lock) return _state; }
    }

    /// <summary>The currently selected analysis profile. Default
    /// engine-structural version 0 (the single non-normative engine check; no
    /// KNU rules in M2a). Other profiles are used in tests only to exercise
    /// freshness; this coordinator adds no profile selector.</summary>
    public AnalysisProfile CurrentProfile
    {
        get { lock (_lock) return _profile; }
    }

    /// <summary>The owned factory's local workspace epoch (delegated). Bumped on
    /// every WorkspaceChanged raise before this coordinator's handler observes
    /// it; captured in each snapshot's LocalEpoch and rechecked at publish so a
    /// restore/recovery that replaces contents without a generation bump
    /// invalidates a stale capture.</summary>
    public long CurrentWorkspaceEpoch => _factory.CurrentWorkspaceEpoch;

    /// <summary>Raised after every synchronous state update, OUTSIDE the lock,
    /// so subscribers may re-enter the coordinator without deadlock. Delivery is
    /// per-subscriber and reentrancy-safe: before each delivery the passed
    /// state is confirmed still CURRENT (ReferenceEquals with the live state),
    /// so an obsolete notification is never delivered as current after an
    /// earlier subscriber mutated state; a faulting subscriber is isolated
    /// individually so it cannot suppress later subscribers.</summary>
    public event Action<AnalysisState>? Changed
    {
        add { lock (_lock) _changed += value; }
        remove { lock (_lock) _changed -= value; }
    }

    // ---- public operations (later UI) ----

    /// <summary>Request/reanalyze now: invalidate and dispatch a fresh analyze
    /// as soon as the active slot is free (no debounce wait). Repeated identical
    /// requests coalesce via the intent/queued mechanism. Does not cancel-and-
    /// leave; it queues a new job (the older active result is discarded by the
    /// intent mismatch when it settles).</summary>
    public void RequestReanalyze() => Schedule(immediate: true);

    /// <summary>Cancel pending/running work: clears queued/debounce and the
    /// usable report immediately, bumps intent, cancels the active token, and
    /// does NOT silently queue a new job. A late completion/failure cannot
    /// replace Cancelled. A new explicit request after Cancel works once the old
    /// call settles; a WorkspaceChanged (edit) after Cancel schedules a fresh
    /// debounced analyze normally.</summary>
    public void Cancel()
    {
        AnalysisState? toRaise = null;
        lock (_lock)
        {
            if (_disposed) return;
            _intent++;
            CancelDebounceLocked();
            _activeCts?.Cancel();
            _queued = false;
            _ready = false;
            _queuedProfile = default;
            _state = new AnalysisState.Cancelled();
            toRaise = _state;
        }
        RaiseChanged(toRaise!);
    }

    /// <summary>Change the selected analysis profile. Invalid input (opaque
    /// id/version failing AnalysisIdentity) is typed InvalidInput and is NOT
    /// dispatched. A repeated identical profile does not schedule redundant work.
    /// A valid change invalidates even without WorkspaceChanged/generation bump
    /// and reanalyzes immediately with the new profile.</summary>
    public void SetProfile(AnalysisProfile profile)
    {
        AnalysisState? toRaise = null;
        bool invalid = false;
        lock (_lock)
        {
            if (_disposed) return;
            if (!AnalysisIdentity.IsValidOpaqueId(profile.Id, AnalysisLimits.MaxIdBytes)
                || !AnalysisIdentity.IsValidOpaqueId(profile.Version, AnalysisLimits.MaxIdBytes))
            {
                _intent++;
                CancelDebounceLocked();
                _activeCts?.Cancel();
                _queued = false;
                _ready = false;
                _queuedProfile = default;
                _state = new AnalysisState.InvalidInput(
                    "profile id/version must be nonempty strict-UTF-8 <=128 bytes");
                toRaise = _state;
                invalid = true;
            }
            else if (_profile != profile)
            {
                _profile = profile;
            }
            else
            {
                return; // identical: no redundant work
            }
        }
        if (toRaise is { } r) RaiseChanged(r);
        if (!invalid) Schedule(immediate: true); // reanalyze with the new profile
    }

    // ---- the single invalidation seam ----

    private void OnWorkspaceChanged() => Schedule(immediate: false);

    // Core scheduling op. Under the lock it mutates scheduling state AND
    // allocates debounce ownership BEFORE notifying; OUTSIDE the lock it
    // notifies, then re-checks ownership and only then starts the delay (or
    // TryStart for immediate). This prevents a reentrant subscriber
    // (Cancel/Edit/Dispose) during notification from leaving a stale
    // uncancelled debounce that later creates work after cancellation, and
    // prevents a stale outer scheduling from overwriting a newer debounce.
    private void Schedule(bool immediate)
    {
        AnalysisState? toRaise = null;
        CancellationTokenSource? debounceCts = null; // owned by this Schedule call
        long debIntent = 0;
        AnalysisProfile debProfile = default;
        bool startDebounce = false;
        bool startImmediate = false;
        lock (_lock)
        {
            if (_disposed) return;
            if (_coordinator.Project is null)
            {
                // Close/reset: cancel everything, show NoProject, do not queue.
                _intent++;
                CancelDebounceLocked();
                _activeCts?.Cancel();
                _queued = false;
                _ready = false;
                _queuedProfile = default;
                if (_state is not AnalysisState.NoProject)
                    _state = new AnalysisState.NoProject();
                toRaise = _state;
                // No debounce/immediate to start in the no-project branch.
            }
            else
            {
                // Open: IMMEDIATE INVALIDATION — bump intent (any active workflow
                // is now stale), cancel prior debounce and active CTS, replace the
                // ONE latest queued intent, then reset the debounce.
                _intent++;
                CancelDebounceLocked();
                _activeCts?.Cancel();
                _queuedProfile = _profile;
                _queued = true;
                _ready = false;
                _state = new AnalysisState.Pending();
                toRaise = _state;
                if (immediate)
                {
                    _ready = true;
                    startImmediate = true;
                }
                else
                {
                    // Allocate debounce ownership UNDER the lock, BEFORE notifying,
                    // so a reentrant Cancel/Edit/Dispose during RaiseChanged can
                    // cancel THIS cts (it exists) rather than a later stale one.
                    debounceCts = new CancellationTokenSource();
                    _debounceCts = debounceCts;
                    debIntent = _intent;
                    debProfile = _profile;
                    startDebounce = true;
                }
            }
        }
        // Notify OUTSIDE the lock. A reentrant subscriber may mutate state
        // (Cancel/Edit/Dispose/Request); that's safe because ownership was
        // already recorded under the lock above.
        if (toRaise is { } r) RaiseChanged(r);
        // After a possibly-reentrant notification, re-check ownership before
        // starting the delay. A stale outer scheduling (superseded by the
        // reentrant op) must neither create work after Cancel/Dispose nor
        // overwrite a newer debounce reference.
        if (startDebounce && debounceCts is not null)
        {
            bool stillOurs = false;
            lock (_lock)
            {
                if (!_disposed
                    && _intent == debIntent
                    && ReferenceEquals(_debounceCts, debounceCts)
                    && !debounceCts.IsCancellationRequested)
                    stillOurs = true;
            }
            if (stillOurs)
            {
                var dt = DebounceAsync(debIntent, debProfile, debounceCts.Token);
                lock (_lock) { if (ReferenceEquals(_debounceCts, debounceCts)) _debounceTask = dt; }
                FireAndForget(dt);
            }
            else
            {
                // Superseded/cancelled by a reentrant op: clean up our orphaned cts.
                debounceCts.Dispose();
            }
        }
        if (startImmediate) TryStart();
    }

    private async Task DebounceAsync(long myIntent, AnalysisProfile profile, CancellationToken ct)
    {
        // Expected token cancellation (a newer invalidation/Cancel/Dispose) is
        // normal: it must not repaint a newer state.
        try { await _delayAsync(_debounce, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            // An UNEXPECTED scheduling/delay failure must NOT silently leave the
            // current intent Pending forever. Surface an honest terminal Failed
            // for the still-latest intent, then re-throw so the task fault is
            // observed (FireAndForget) rather than swallowed.
            SetStateIfLatest(myIntent, new AnalysisState.Failed(
                new AnalysisErrorDto("Unavailable", "The analysis scheduler failed unexpectedly.", false)));
            throw new InvalidOperationException("The analysis delay seam failed unexpectedly.", ex);
        }
        bool shouldStart = false;
        lock (_lock)
        {
            if (_disposed || myIntent != _intent) return; // superseded by a newer intent
            _ready = true;
            shouldStart = true;
        }
        if (shouldStart) TryStart();
    }

    // Single gatekeeper. Under the lock it reserves the active slot and prepares
    // the kickoff; OUTSIDE the lock it starts the async workflow (so a
    // synchronously-completing routine does not re-enter the held lock).
    private void TryStart()
    {
        (long myIntent, AnalysisProfile profile, CancellationToken ct)? start = null;
        AnalysisState? toRaise = null;
        lock (_lock)
        {
            toRaise = TryStartLocked(out start);
        }
        if (toRaise is { } r) RaiseChanged(r);
        if (start is { } s)
        {
            var task = RunWorkflowAsync(s.Item1, s.Item2, s.Item3);
            lock (_lock) { _workflowTask = task; }
            FireAndForget(task);
        }
    }

    private AnalysisState? TryStartLocked(out (long, AnalysisProfile, CancellationToken)? start)
    {
        start = null;
        if (_disposed) return null;
        if (_active || !_ready || !_queued) return null;
        var profile = _queuedProfile;
        long myIntent = _intent;
        _queued = false;
        _ready = false;
        _active = true;          // set BEFORE the potentially-synchronously-completing routine
        _activeIntent = myIntent;
        _activeCts?.Dispose();
        _activeCts = new CancellationTokenSource();
        _state = new AnalysisState.Running();
        start = (myIntent, profile, _activeCts.Token);
        return _state; // Running
    }

    // The single active capture/analyze/epoch workflow. No await while holding
    // _lock; each await is followed by an intent/freshness recheck. ANY
    // terminal-ish outcome for the STILL-LATEST intent (adapter Cancelled/
    // factory Cancelled/Superseded, caught OCE) publishes a typed terminal
    // state (Cancelled/Superseded) so Running is never left forever. A
    // stale job (intent bumped) never overrides a newer state. The finally
    // clears ONLY its own active ownership and then tries to start the queued
    // latest (no implicit restart on a terminal outcome).
    private async Task RunWorkflowAsync(long myIntent, AnalysisProfile profile, CancellationToken ct)
    {
        try
        {
            // ---- CAPTURE (reuses the factory; no live Files enumeration) ----
            CaptureResult cap;
            try { cap = await _factory.CaptureAsync(profile, ct).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                SetStateIfLatest(myIntent, new AnalysisState.Cancelled());
                return;
            }
            catch (Exception)
            {
                SetStateIfLatest(myIntent, new AnalysisState.Failed(
                    new AnalysisErrorDto("Unavailable", "The analysis snapshot could not be captured.", false)));
                return;
            }
            if (!IsCurrentIntent(myIntent)) return;

            AnalysisSnapshot? snapshot = null;
            switch (cap)
            {
                case CaptureResult.Captured c:
                    snapshot = c.Snapshot;
                    break;
                case CaptureResult.NoProject:
                    SetStateIfLatest(myIntent, new AnalysisState.NoProject());
                    return;
                case CaptureResult.Cancelled:
                    // ct cancelled (e.g. a worker reset/dispose that cancelled the
                    // job even though this coordinator did not call Cancel): if
                    // still latest, publish a terminal Cancelled, not Running.
                    SetStateIfLatest(myIntent, new AnalysisState.Cancelled());
                    return;
                case CaptureResult.Superseded:
                    // A newer intent usually owns Pending already (intent bumped);
                    // if still latest for any reason, leave Superseded (no report).
                    SetStateIfLatest(myIntent, new AnalysisState.Superseded());
                    return;
                case CaptureResult.Disposed:
                    SetStateIfLatest(myIntent, new AnalysisState.Disposed());
                    return;
                case CaptureResult.InvalidInput ii:
                    SetStateIfLatest(myIntent, new AnalysisState.InvalidInput(ii.SafeMessage));
                    return;
                case CaptureResult.QuotaExceeded qe:
                    SetStateIfLatest(myIntent, new AnalysisState.QuotaExceeded(qe.SafeMessage));
                    return;
                case CaptureResult.Unavailable u:
                    SetStateIfLatest(myIntent, new AnalysisState.Unavailable(u.SafeMessage));
                    return;
                default:
                    return;
            }

            // ---- DISPATCH (only on Captured) ----
            AnalysisJobResult job;
            try { job = await _analyzer.AnalyzeAsync(snapshot, ct).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                SetStateIfLatest(myIntent, new AnalysisState.Cancelled());
                return;
            }
            catch (Exception)
            {
                SetStateIfLatest(myIntent, new AnalysisState.Failed(
                    new AnalysisErrorDto("WorkerCrashed", "The analysis worker failed unexpectedly.", false)));
                return;
            }
            if (!IsCurrentIntent(myIntent)) return;

            // ---- PUBLISH ----
            switch (job)
            {
                case AnalysisJobResult.Completed completed:
                    await PublishCompletedAsync(myIntent, snapshot, completed, ct).ConfigureAwait(false);
                    break;
                case AnalysisJobResult.Failed failed:
                    SetStateIfLatest(myIntent, new AnalysisState.Failed(failed.Error));
                    break;
                case AnalysisJobResult.Cancelled:
                    // An adapter/bridge cancellation (worker reset/dispose) for the
                    // still-latest job: publish a terminal Cancelled (an explicit
                    // Cancel already bumped intent and set Cancelled, so a stale
                    // adapter Cancelled here is discarded by the intent mismatch).
                    SetStateIfLatest(myIntent, new AnalysisState.Cancelled());
                    break;
                case AnalysisJobResult.Disposed:
                    // Externally-owned analyzer was disposed; honest failure, not
                    // the coordinator's Disposed (the coordinator may still live).
                    SetStateIfLatest(myIntent, new AnalysisState.Failed(
                        new AnalysisErrorDto("Unavailable", "The analysis worker is unavailable.", false)));
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            SetStateIfLatest(myIntent, new AnalysisState.Cancelled());
        }
        catch (Exception)
        {
            SetStateIfLatest(myIntent, new AnalysisState.Failed(
                new AnalysisErrorDto("WorkerCrashed", "The analysis workflow failed unexpectedly.", false)));
        }
        finally
        {
            (long, AnalysisProfile, CancellationToken)? start = null;
            AnalysisState? toRaise = null;
            lock (_lock)
            {
                // Clear ONLY own active ownership (a newer workflow cannot have
                // started while _active was true, so this is always ours).
                if (_activeIntent == myIntent)
                {
                    _active = false;
                    _activeIntent = -1;
                }
                if (!_disposed) toRaise = TryStartLocked(out start);
            }
            if (toRaise is { } r) RaiseChanged(r);
            if (start is { } s)
            {
                var task = RunWorkflowAsync(s.Item1, s.Item2, s.Item3);
                lock (_lock) { _workflowTask = task; }
                FireAndForget(task);
            }
        }
    }

    private async Task PublishCompletedAsync(
        long myIntent, AnalysisSnapshot snapshot, AnalysisJobResult.Completed completed, CancellationToken ct)
    {
        // Pre-await: if a newer intent/dispose already owns the state, do not
        // publish (the invalidator set Pending/NoProject/Disposed).
        if (!IsCurrentIntent(myIntent)) return;

        // Awaited worker-epoch check. An edit/close/restore during this await
        // bumps the intent (the invalidator's handler took _lock after we
        // released it), so the post-await recheck below discards the result.
        bool epochCurrent;
        try { epochCurrent = await _analyzer.IsCurrentWorkerEpochAsync(completed.WorkerEpoch, ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            // ct cancelled (e.g. worker reset) for the still-latest job: terminal
            // Cancelled, not a stale Completed repaint.
            SetStateIfLatest(myIntent, new AnalysisState.Cancelled());
            return;
        }
        catch (Exception) { epochCurrent = false; }

        // RE-CHECK identity + correlation AFTER the awaited worker-epoch check,
        // then publish. With myIntent still current, ANY workspace change would
        // have bumped the intent (caught above), so a failing gate here is a
        // stale RESULT (wrong snapshotId/profile) or worker-epoch mismatch ->
        // Superseded with no report (never a stale Completed/error repaint).
        AnalysisState? toRaise = null;
        lock (_lock)
        {
            if (_disposed || myIntent != _intent) return; // superseded by a newer intent
            var project = _coordinator.Project;
            bool fresh = project is not null
                && project!.Id == snapshot.ProjectId
                && _coordinator.SessionId == snapshot.SessionId
                && _coordinator.WorkingGeneration == snapshot.WorkingGeneration
                && _factory.CurrentWorkspaceEpoch == snapshot.LocalEpoch
                && _profile == snapshot.Profile
                && string.Equals(completed.Result.SnapshotId, snapshot.Id.ToString(), StringComparison.Ordinal)
                && string.Equals(completed.Result.Profile.Id, snapshot.Profile.Id, StringComparison.Ordinal)
                && string.Equals(completed.Result.Profile.Version, snapshot.Profile.Version, StringComparison.Ordinal);
            if (!fresh || !epochCurrent)
            {
                _state = new AnalysisState.Superseded();
                toRaise = _state;
            }
            else
            {
                var report = new AnalysisReport(snapshot, completed.Result, completed.RequestId, completed.WorkerEpoch);
                _state = new AnalysisState.Completed(report);
                toRaise = _state;
            }
        }
        if (toRaise is { } r) RaiseChanged(r);
    }

    private void SetStateIfLatest(long myIntent, AnalysisState newState)
    {
        AnalysisState? toRaise = null;
        lock (_lock)
        {
            if (_disposed || myIntent != _intent) return; // stale: do not repaint
            _state = newState;
            toRaise = newState;
        }
        if (toRaise is { } r) RaiseChanged(r);
    }

    private bool IsCurrentIntent(long myIntent)
    {
        lock (_lock) return !_disposed && myIntent == _intent;
    }

    private void CancelDebounceLocked()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = null;
    }

    // Per-subscriber delivery. The handler list is snapshotted under the lock
    // (so reentrant subscribe/unsubscribe during delivery cannot corrupt
    // iteration). Before each delivery the passed state is confirmed still
    // CURRENT (ReferenceEquals with the live _state); if an earlier subscriber
    // mutated state, the obsolete notification is NOT delivered to later
    // subscribers (the reentrant mutation raised its own current notification).
    // A faulting subscriber is isolated individually so it cannot suppress
    // later subscribers.
    private void RaiseChanged(AnalysisState state)
    {
        Action<AnalysisState>[]? handlers;
        lock (_lock)
        {
            if (_disposed) return;
            handlers = _changed?.GetInvocationList()?.OfType<Action<AnalysisState>>().ToArray();
        }
        if (handlers is null) return;
        foreach (var h in handlers)
        {
            AnalysisState current;
            lock (_lock) current = _state;
            if (!ReferenceEquals(current, state)) break; // obsolete: stop delivering
            try { h(state); }
            catch { /* isolate this subscriber; later subscribers still receive */ }
        }
    }

    // Observe eventual task completion so a faulted/cancelled task is never
    // unobserved. Does NOT block; disposal never waits forever on a
    // non-cooperative analyzer/import.
    private static void FireAndForget(Task task)
    {
        _ = task.ContinueWith(t => { if (t.IsFaulted) _ = t.Exception; }, TaskScheduler.Default);
    }

    /// <summary>Dispose the coordinator: idempotent; unsubscribes from
    /// WorkspaceChanged; cancels debounce/active promptly; disposes ONLY the
    /// owned factory (never the injected coordinator/analyzer); does not block
    /// forever on a non-cooperative underlying job; late completion after dispose
    /// never publishes, schedules or emits UI changes. Methods after dispose do
    /// not revive work.</summary>
    public async ValueTask DisposeAsync()
    {
        List<Task> toObserve = new();
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _intent++;
            CancelDebounceLocked();
            _activeCts?.Cancel();
            _queued = false;
            _ready = false;
            _queuedProfile = default;
            _state = new AnalysisState.Disposed();
            if (_workflowTask is { } wf) toObserve.Add(wf);
            if (_debounceTask is { } db) toObserve.Add(db);
        }
        _coordinator.WorkspaceChanged -= OnWorkspaceChanged;
        // Dispose ONLY the owned factory (it unsubscribes its own handler).
        await _factory.DisposeAsync().ConfigureAwait(false);
        lock (_lock)
        {
            _activeCts?.Dispose();
            _debounceCts?.Dispose();
            _activeCts = null;
            _debounceCts = null;
        }
        // Observe eventual completion without blocking (a non-cooperative
        // analyzer/import that ignores cancellation must not stall disposal).
        foreach (var t in toObserve) FireAndForget(t);
    }
}
