using Client.Compilation;

namespace Client.Compilation.Presentation;

/// <summary>Compile-local context over the actual component renderer. Never
/// replaces the app's ambient context globally or runs workspace work on a pool.
/// Post's context-free Yield only schedules a wakeup; mutable work enters renderer.
/// Local capture/workflow tasks drain after prompt close; foreign tasks are not awaited.</summary>
public sealed class CompileOwnerContext(Func<Action, Task> renderer) : SynchronizationContext, ICompilationOwnedOperationObserver
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly object _gate = new();
    private readonly HashSet<Task> _owned = [];
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _posts;
    private bool _closing;
    public int PendingPosts { get { lock (_gate) return _posts; } }
    public int PendingOwned { get { lock (_gate) return _owned.Count; } }
    public Task WhenSettled => _closed.Task;
    public T Run<T>(Func<T> action)
    {
        lock (_gate) if (_closing) throw new ObjectDisposedException(nameof(CompileOwnerContext));
        return Enter(action);
    }
    public void Run(Action action) => Run(() => { action(); return true; });
    private T Enter<T>(Func<T> action)
    {
        if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("Compile work requires the real UI thread.");
        var previous = Current; SetSynchronizationContext(this);
        try { return action(); }
        finally { SetSynchronizationContext(previous); }
    }
    public void ForwardWorkspaceEvent(Action action)
    {
        lock (_gate) if (_closing) return;
        if (Environment.CurrentManagedThreadId == _thread) Enter(() => { action(); return true; });
        else Post(_ => action(), null);
    }
    public override void Post(SendOrPostCallback callback, object? state)
    {
        lock (_gate)
        {
            if (_closed.Task.IsCompleted) throw new ObjectDisposedException(nameof(CompileOwnerContext));
            if (_posts >= 64) throw new InvalidOperationException("Compile renderer wakeup bound reached.");
            _posts++;
        }
        var previous = Current; SetSynchronizationContext(null);
        try { Observe(Deferred()); }
        finally { SetSynchronizationContext(previous); }
        async Task Deferred()
        {
            try
            {
                await Task.Yield(); // no recursive inline Post; no mutable workspace access
                await renderer(() => Enter(() => { callback(state); return true; }));
            }
            finally { lock (_gate) { _posts--; CheckClosed(); } }
        }
    }
    private static void Observe(Task task) => _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    public void ObserveOwnedOperation(Task operation, CompilationOwnedOperationKind kind)
    {
        lock (_gate)
        {
            if (operation.IsCompleted) return;
            if (_owned.Count >= 32) throw new InvalidOperationException("Compile local operation bound reached.");
            if (!_owned.Add(operation)) return;
        }
        _ = operation.ContinueWith(t => { _ = t.Exception; lock (_gate) { _owned.Remove(t); CheckClosed(); } }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    public void RequestClose() { lock (_gate) { _closing = true; CheckClosed(); } }
    private void CheckClosed() { if (_closing && _posts == 0 && _owned.Count == 0) _closed.TrySetResult(); }
}
