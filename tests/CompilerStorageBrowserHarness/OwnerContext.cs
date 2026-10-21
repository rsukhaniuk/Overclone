using Client.Compilation;
using Client.Compilation.Lifecycle;
namespace CompilerStorageBrowserHarness;

// Harness-only single UI owner for single-thread Blazor's null ambient context.
// NOT a lifecycle/identity implementation. JS merely schedules a microtask Pump.
public sealed class OwnerContext(Action schedule) : SynchronizationContext, ICompilationOwnedOperationObserver
{
    private readonly object _gate = new();
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly HashSet<Task> _owned = [];
    private TaskCompletionSource _idle = Ready();
    private bool _scheduled, _closed;
    private static TaskCompletionSource Ready() { var s = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); s.SetResult(); return s; }
    public Task WhenIdle { get { lock (_gate) return _idle.Task; } }
    private bool Enqueue(SendOrPostCallback callback, object? state)
    {
        if (_closed) throw new InvalidOperationException("Owner post after close.");
        if (_idle.Task.IsCompleted) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue((callback, state)); if (_scheduled) return false; _scheduled = true; return true;
    }
    public override void Post(SendOrPostCallback callback, object? state)
    {
        bool wake; lock (_gate) wake = Enqueue(callback, state); if (wake) schedule();
    }
    public void ObserveOwnedOperation(Task operation, CompilationOwnedOperationKind kind)
    {
        lock (_gate)
        {
            if (_closed) throw new InvalidOperationException("Owner operation after close.");
            if (operation.IsCompleted || !_owned.Add(operation)) return;
            if (_idle.Task.IsCompleted) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = operation.ContinueWith(t =>
        {
            bool wake;
            lock (_gate) { _ = t.Exception; _owned.Remove(t); wake = Enqueue(_ => { }, null); }
            if (wake) schedule();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    public Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ => { try { result.TrySetResult(await action()); } catch (Exception e) { result.TrySetException(e); } }, null);
        return result.Task;
    }
    public void Pump()
    {
        lock (_gate) { if (_closed) throw new InvalidOperationException("Owner pump after close."); _scheduled = false; }
        for (int i = 0; i < 64; i++)
        {
            (SendOrPostCallback Callback, object? State) work;
            lock (_gate) { if (!_queue.TryDequeue(out work)) break; }
            var previous = Current;
            try { SetSynchronizationContext(this); work.Callback(work.State); }
            finally { SetSynchronizationContext(previous); }
        }
        bool wake = false;
        lock (_gate)
        {
            if (_queue.Count == 0 && _owned.Count == 0 && !_scheduled) _idle.TrySetResult();
            if (_queue.Count > 0 && !_scheduled) { _scheduled = true; wake = true; }
        }
        if (wake) schedule();
    }
    public void Close()
    {
        lock (_gate)
        {
            if (_queue.Count != 0 || _owned.Count != 0 || _scheduled) throw new InvalidOperationException("Owner closed before quiescence.");
            _closed = true;
        }
    }
}
