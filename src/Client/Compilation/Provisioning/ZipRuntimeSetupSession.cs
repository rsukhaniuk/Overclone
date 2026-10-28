namespace Client.Compilation.Provisioning;

/// <summary>One non-queuing admission for pins, ZIP, installation and recheck.
/// NativeRuntimeSetup remains the sole verifier/publisher. Preparation and ZIP
/// disposal retain admission through actual IO, even when cancellation is ignored.
/// </summary>
public sealed class ZipRuntimeSetupSession : IAsyncDisposable
{
    private readonly NativeRuntimeSetup _setup;
    private readonly object _gate = new();
    private CancellationTokenSource? _cancel;
    private TaskCompletionSource<bool>? _settled;
    private bool _busy, _preparing, _disposed, _handed;
    private RuntimeSetupState? _state;
    public ZipRuntimeSetupSession(NativeRuntimeSetup setup) { _setup = setup; _setup.Changed += Notify; }
    public bool Busy => _busy;
    public bool CanCancel => _busy && (_preparing || _setup.CanCancel);
    public RuntimeSetupState State => _state ?? _setup.State;
    public int Completed => _preparing ? 0 : _setup.Completed;
    public long OperationVersion { get; private set; }
    public event Action? Changed;
    private void Notify() { if (!_disposed) Changed?.Invoke(); }
    private bool Begin()
    {
        lock (_gate)
        {
            if (_disposed || _busy || OperationVersion == long.MaxValue) return false;
            OperationVersion++;
            _busy = true; _preparing = true; _handed = false; _state = RuntimeSetupState.Preparing;
            _cancel = new(); _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Notify(); return true;
    }
    public void Cancel()
    {
        if (_preparing) { _cancel?.Cancel(); _state = RuntimeSetupState.Finishing; Notify(); }
        else _setup.Cancel(); // respects the uncancellable publication/selection boundary
    }
    public Task InstallZipAsync(long length, Func<CancellationToken, Task<Stream>> open)
        => Run(async token =>
        {
            var zip = new ZipRuntimeArchive();
            try
            {
                await zip.PrepareAsync(length, open, token);
                token.ThrowIfCancellationRequested();
                Handoff(); await _setup.InstallAsync(zip.Pins, zip.Files);
            }
            finally
            {
                // Setup has drained every abandoned entry read AND store.WhenSettled
                // before this point. Still retain admission through source disposal.
                var outcome = State; _preparing = false; _state = RuntimeSetupState.Finishing; Notify();
                try { await zip.DisposeAsync(); }
                finally { _state = outcome; }
            }
        });
    public Task InstallManualAsync(Func<CancellationToken, Task<byte[]>> pins, IReadOnlyList<LocalRuntimeFile> files)
    {
        return Run(async token =>
        {
            // Admission precedes even pins IO. Bound/own descriptors before awaits.
            if (files.Count is not (497 or 499 or 507)) throw new InvalidDataException("Invalid resource selection.");
            var owned = files.ToArray();
            var json = await pins(token); token.ThrowIfCancellationRequested();
            Handoff(); await _setup.InstallAsync(json, owned);
        });
    }
    public Task RecheckAsync() => Run(async token =>
    { token.ThrowIfCancellationRequested(); Handoff(); await _setup.RecheckAsync(); });
    private void Handoff() { _handed = true; _preparing = false; _state = null; }
    private async Task Run(Func<CancellationToken, Task> operation)
    {
        if (!Begin()) return;
        try { await operation(_cancel!.Token); }
        catch (OperationCanceledException)
        { _state = _handed && _setup.State == RuntimeSetupState.Installed ? RuntimeSetupState.Uncertain : RuntimeSetupState.Cancelled; }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Never claim rollback after a committed selection or report raw exceptions.
            _state = _handed && _setup.State == RuntimeSetupState.Installed ? RuntimeSetupState.Uncertain
                : !_handed && _cancel!.IsCancellationRequested ? RuntimeSetupState.Cancelled : RuntimeSetupState.InvalidSelection;
        }
        finally
        {
            lock (_gate)
            {
                _preparing = false; _busy = false; _cancel?.Dispose(); _cancel = null;
                _settled!.TrySetResult(true);
            }
            Notify();
        }
    }
    public async ValueTask DisposeAsync()
    {
        Task pending;
        lock (_gate)
        {
            _disposed = true; Changed = null; pending = _settled?.Task ?? Task.CompletedTask;
        }
        Cancel(); await pending; // no cancelled-wait/timeout proxy, including late commits
        _setup.Changed -= Notify; await _setup.DisposeAsync();
    }
}
