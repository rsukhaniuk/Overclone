using Client.Workspace.Domain;

namespace Client.Compilation.History.Index;

/// <summary>Bounded explicit discovery-pointer index, NOT an artifact store.
/// No automatic persistence, retries, eviction, UI or currentness authority.</summary>
public sealed class CompileHistoryIndex(IHistoryIndexBackend backend) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _active, _closing;
    private bool _disposed;
    public Task<HistoryIndexResult> ListAsync(ProjectId project, HistoryIndexBudget budget, CancellationToken caller = default)
    {
        if (project.Value == Guid.Empty || budget is not { IsValid: true }) return Task.FromResult(new HistoryIndexResult(HistoryIndexStatus.InvalidInput));
        return Run(budget, caller, async op =>
        {
            var state = await Read(project, budget, op).ConfigureAwait(false);
            return new(state.Revision == 0 ? HistoryIndexStatus.Missing : HistoryIndexStatus.Ready, state);
        });
    }
    /// <summary>Call ONLY after explicit successful N14 persistence, passing its
    /// returned key. This method cannot authenticate a public key or validate PDF
    /// content. Every later open MUST use N14 ReadAsync with expected key/project.</summary>
    public Task<HistoryIndexResult> AppendPersistedAsync(ProjectId project, HistoricalArtifactKey key, long expectedRevision,
        HistoryIndexBudget budget, CancellationToken caller = default)
    {
        if (key is not null && project.Value != Guid.Empty && key.ProjectId != project)
            return Task.FromResult(new HistoryIndexResult(HistoryIndexStatus.WrongProject));
        if (key is null || !HistoryIndexCodec.ValidKey(key, project) || expectedRevision < 0 || budget is not { IsValid: true })
            return Task.FromResult(new HistoryIndexResult(HistoryIndexStatus.InvalidInput));
        return Run(budget, caller, async op =>
        {
            var state = await Read(project, budget, op).ConfigureAwait(false);
            var previous = state.Entries.FirstOrDefault(e => e.RecordId == key.RecordId);
            // Idempotent retry after uncertain commit, even if caller still holds
            // the prior CAS revision. Never replace a conflicting record pointer.
            if (previous is not null) return new(previous == key ? HistoryIndexStatus.AlreadyIndexed : HistoryIndexStatus.RecordConflict, state);
            if (state.Revision != expectedRevision) return new(HistoryIndexStatus.Conflict);
            if (state.Entries.Length >= budget.MaxEntries) return new(HistoryIndexStatus.Full, state);
            if (state.Revision == long.MaxValue) return new(HistoryIndexStatus.LimitExceeded, state);
            var next = state with { Revision = state.Revision + 1, Entries = state.Entries.Add(key) };
            string json = HistoryIndexCodec.Encode(next, budget); op.Check();
            Volatile.Write(ref op.WriteStarted, 1);
            var result = await backend.CompareExchangeAsync(project, state.Revision, next.Revision, json, budget.MaxJsonBytes).ConfigureAwait(false);
            return result.Status == HistoryIndexStatus.Saved ? new(HistoryIndexStatus.Saved, next) : new(result.Status);
        });
    }
    private async Task<HistoryIndexSnapshot> Read(ProjectId project, HistoryIndexBudget budget, Operation op)
    {
        op.Check(); var raw = await backend.ReadAsync(project, budget.MaxJsonBytes).ConfigureAwait(false); op.Check();
        return HistoryIndexCodec.Decode(project, raw, budget);
    }
    private Task<HistoryIndexResult> Run(HistoryIndexBudget budget, CancellationToken caller, Func<Operation, Task<HistoryIndexResult>> action)
    {
        TaskCompletionSource<HistoryIndexResult> actual;
        CancellationTokenSource deadline, linked;
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(new HistoryIndexResult(HistoryIndexStatus.Disposed));
            if (_active is { IsCompleted: false }) return Task.FromResult(new HistoryIndexResult(HistoryIndexStatus.Busy));
            if (caller.IsCancellationRequested) return Task.FromResult(new HistoryIndexResult(HistoryIndexStatus.Cancelled));
            deadline = new(); deadline.CancelAfter(budget.DeadlineMs);
            linked = CancellationTokenSource.CreateLinkedTokenSource(caller, deadline.Token, _shutdown.Token);
            actual = new(TaskCreationOptions.RunContinuationsAsynchronously); _active = actual.Task;
        }
        var op = new Operation(linked.Token);
        var wait = Wait(); Observe(Execute()); return wait;
        HistoryIndexResult Interrupted()
        {
            var cause = Volatile.Read(ref _disposed) ? HistoryIndexStatus.Disposed : caller.IsCancellationRequested ? HistoryIndexStatus.Cancelled : HistoryIndexStatus.TimedOut;
            return Volatile.Read(ref op.WriteStarted) != 0 ? new(HistoryIndexStatus.Uncertain, WriteMayHaveCommitted: true, Cause: cause) : new(cause);
        }
        async Task<HistoryIndexResult> Wait()
        {
            try { return await actual.Task.WaitAsync(op.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (op.Token.IsCancellationRequested) { return Interrupted(); }
        }
        async Task Execute()
        {
            HistoryIndexResult result;
            try { result = await action(op).ConfigureAwait(false); }
            catch (OperationCanceledException) when (op.Token.IsCancellationRequested) { result = Interrupted(); }
            catch (HistoryIndexException error) { result = Volatile.Read(ref op.WriteStarted) != 0 ? new(HistoryIndexStatus.Uncertain, WriteMayHaveCommitted: true, Cause: error.Status) : new(error.Status); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { result = Volatile.Read(ref op.WriteStarted) != 0 ? new(HistoryIndexStatus.Uncertain, WriteMayHaveCommitted: true, Cause: HistoryIndexStatus.StorageFailure) : new(HistoryIndexStatus.StorageFailure); }
            finally { linked.Dispose(); deadline.Dispose(); }
            lock (_gate) { if (!_disposed) { actual.TrySetResult(result); return; } }
            try { await Close().ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { result = new(HistoryIndexStatus.StorageFailure); }
            finally { actual.TrySetResult(result); }
        }
    }
    public Task WhenSettled { get { lock (_gate) return _active is { IsCompleted: false } ? _active : _closing ?? _active ?? Task.CompletedTask; } }
    public ValueTask DisposeAsync()
    {
        Task? active; lock (_gate) { if (_disposed) return ValueTask.CompletedTask; _disposed = true; active = _active; }
        _shutdown.Cancel(); if (active is null || active.IsCompleted) Observe(Close()); return ValueTask.CompletedTask;
    }
    private Task Close() { lock (_gate) return _closing ??= CloseBackend(); }
    private async Task CloseBackend() { try { await backend.DisposeAsync().ConfigureAwait(false); } finally { _shutdown.Dispose(); } }
    private static void Observe(Task task) => _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private sealed class Operation(CancellationToken token)
    { public CancellationToken Token { get; } = token; public int WriteStarted; public void Check() => Token.ThrowIfCancellationRequested(); }
}
