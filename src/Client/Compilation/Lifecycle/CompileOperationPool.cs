namespace Client.Compilation.Lifecycle;

/// <summary>Bounded retirement, not a native process registry. Continuations
/// retain only this small lease/pool, never the coordinator or its event list.</summary>
internal sealed class CompileOperationPool(int capacity)
{
    private readonly object _gate = new();
    private int _count;
    internal int Count { get { lock (_gate) return _count; } }
    internal Lease? Acquire()
    {
        lock (_gate)
        {
            if (_count >= capacity) return null;
            _count++;
            return new(this);
        }
    }
    private void Release() { lock (_gate) _count--; }

    internal sealed class Lease(CompileOperationPool pool)
    {
        private int _remaining;
        private CancellationTokenSource? _lifetime;
        internal void Retire(CancellationTokenSource lifetime, params Task?[] operations)
        {
            _lifetime = lifetime;
            var tasks = operations.Where(t => t is not null).Cast<Task>().Distinct().ToArray();
            _remaining = tasks.Length;
            if (tasks.Length == 0) { lifetime.Dispose(); pool.Release(); return; }
            foreach (var task in tasks)
                _ = task.ContinueWith(static (completed, state) => ((Lease)state!).Completed(completed),
                    this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        private void Completed(Task task)
        {
            if (task.IsFaulted) _ = task.Exception; // observe late/foreign failures, never expose their text
            if (Interlocked.Decrement(ref _remaining) != 0) return;
            _lifetime!.Dispose();
            _lifetime = null;
            pool.Release();
        }
    }
}
