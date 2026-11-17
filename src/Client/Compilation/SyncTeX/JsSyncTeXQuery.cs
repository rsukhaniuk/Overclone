using System.Text.Json;
using Microsoft.JSInterop;

namespace Client.Compilation.SyncTeX;

// Intentionally not registered: composition must supply the trusted local provider.
public sealed class JsSyncTeXQuery(IJSRuntime js, ISyncTeXRuntimeProvider runtime) : ISyncTeXQuery
{
    private readonly object _gate = new();
    private Operation? _active;
    private bool _disposed;

    public async Task<SyncTeXQueryOutcome> QueryAsync(SyncTeXQueryRequest request, CancellationToken cancellationToken = default)
    {
        Operation op;
        lock (_gate)
        {
            if (_disposed) return SyncTeXWire.End(SyncTeXQueryStatus.Disposed, "Disposed");
            // Logical cancellation never releases actual provider/import/invocation work.
            if (_active is { } previous && (!previous.Work.IsCompleted || previous.Auxiliary.Any(t => !t.IsCompleted)))
                return SyncTeXWire.End(SyncTeXQueryStatus.Busy, "Busy");
            if (cancellationToken.IsCancellationRequested) return SyncTeXWire.End(SyncTeXQueryStatus.Cancelled, "Cancelled");
            if (!SyncTeXWire.Capture(request, out var owned))
                return SyncTeXWire.End(SyncTeXQueryStatus.InvalidInput, "InvalidInput");
            op = new(cancellationToken);
            _active = op;
            op.Work = RunAsync(op, owned!);
            Observe(op.Work);
        }
        var token = op.Token;
        try { return await op.Work.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return Interrupted(op); }
    }

    private async Task<SyncTeXQueryOutcome> RunAsync(Operation op, SyncTeXQueryRequest request)
    {
        // Establish admission before calling even synchronously throwing foreign code.
        await Task.Yield();
        using var registration = op.Token.Register(() => Interrupt(op));
        Port? port = null;
        IJSObjectReference? module = null, channel = null;
        try
        {
            var token = op.Token;
            token.ThrowIfCancellationRequested();
            var owned = request; // Captured synchronously before the public method's first await.
            // Await ACTUAL tasks, not cancellation of the wait. The public wait has
            // its own deadline, but this reservation stays until late work settles.
            var package = await runtime.GetAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (package is null) return SyncTeXWire.End(SyncTeXQueryStatus.Unavailable, "RuntimeAbsent");
            if (owned.RuntimeBinding is { } required && !required.Matches(package.CompilerBinding)
                || (package.CompilerBinding?.Profile is Native.NativeOriginCompilerContract.Profile or Native.NativeInputBudgetContract.Profile) && owned.RuntimeBinding is null)
                return SyncTeXWire.End(SyncTeXQueryStatus.Unavailable, "RuntimeProfileMismatch");
            if (!package.IsValid) return SyncTeXWire.End(SyncTeXQueryStatus.Unavailable, "RuntimeMismatch");
            byte[] loader = package.CopyLoader(), wasm = package.CopyWasm();
            token.ThrowIfCancellationRequested();
            module = await js.InvokeAsync<IJSObjectReference>("import", "./js/synctex-query-bridge.js").ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            channel = await module.InvokeAsync<IJSObjectReference>("createSyncTeXQueryPort").ConfigureAwait(false);
            if (channel is null) return SyncTeXWire.End(SyncTeXQueryStatus.Unavailable, "WorkerUnavailable");
            port = new(module, channel);
            module = null; channel = null; // Port now owns BOTH references.
            lock (_gate) op.Port = port;
            token.ThrowIfCancellationRequested();
            var metadataFields = new Dictionary<string, object?>
            {
                ["version"] = 1, ["requestId"] = op.Id, ["deadlineMs"] = SyncTeXWire.DeadlineMs,
                ["direction"] = owned.Direction == SyncTeXDirection.Forward ? "forward" : "inverse",
                ["file"] = owned.File, ["line"] = owned.Line, ["page"] = owned.Page, ["h"] = owned.H, ["v"] = owned.V
            };
            if (package.CompilerBinding?.Profile is Native.NativeOriginCompilerContract.Profile or Native.NativeInputBudgetContract.Profile)
                metadataFields.Add("queryProfile", 15);
            string metadata = JsonSerializer.Serialize(metadataFields);
            // Cancellation can run between this check and invocation. The port
            // tombstones the correlated ID, including cancellation before query().
            token.ThrowIfCancellationRequested();
            var wire = await port.Channel.InvokeAsync<SyncTeXWireReply>("query", metadata, owned.Map, loader, wasm).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return SyncTeXWire.Parse(wire, op.Id, owned.Direction);
        }
        catch (OperationCanceledException) when (op.Linked.IsCancellationRequested) { return Interrupted(op); }
        catch (JsonException) { return SyncTeXWire.End(SyncTeXQueryStatus.InvalidResponse, "InvalidResponse"); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return op.Linked.IsCancellationRequested ? Interrupted(op)
                : SyncTeXWire.End(SyncTeXQueryStatus.Failed, "TransportFailed");
        }
        finally
        {
            // Also handles late imports/port creation after public cancellation.
            if (port is not null) await port.CloseAsync().ConfigureAwait(false);
            else
            {
                if (channel is not null) await ReleaseAsync(channel).ConfigureAwait(false);
                if (module is not null) await ReleaseAsync(module).ConfigureAwait(false);
            }
            op.Linked.Dispose(); op.Deadline.Dispose();
        }
    }

    private SyncTeXQueryOutcome Interrupted(Operation op) => SyncTeXWire.End(
        Volatile.Read(ref _disposed) ? SyncTeXQueryStatus.Disposed : op.Caller.IsCancellationRequested
            ? SyncTeXQueryStatus.Cancelled : SyncTeXQueryStatus.TimedOut,
        Volatile.Read(ref _disposed) ? "Disposed" : op.Caller.IsCancellationRequested ? "Cancelled" : "Deadline");

    private void Interrupt(Operation op)
    {
        lock (_gate)
        {
            if (op.Port is not { } port || op.Interrupted) return;
            op.Interrupted = true;
            // Start independently: a noncooperative cancel must not prevent
            // dispose from terminating the worker and revoking its loader URL.
            Track(op, CancelAsync(port, op.Id));
            Track(op, port.CloseAsync());
        }
    }
    private static void Track(Operation op, Task task) { op.Auxiliary.Add(task); Observe(task); }
    private static void Observe(Task task) => _ = task.ContinueWith(static t => { _ = t.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private static async Task CancelAsync(Port port, string id)
    {
        try { await port.Channel.InvokeVoidAsync("cancel", id).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }
    private static async Task ReleaseAsync(IJSObjectReference reference)
    {
        try { await reference.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }
    public ValueTask DisposeAsync()
    {
        Operation? op;
        lock (_gate) { if (_disposed) return ValueTask.CompletedTask; _disposed = true; op = _active; }
        // Never block disposal on hostile provider/interop. RunAsync owns late refs.
        if (op is not null)
        {
            try { op.Linked.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        return ValueTask.CompletedTask;
    }
    private sealed class Operation
    {
        internal readonly string Id = Guid.NewGuid().ToString("N");
        internal readonly CancellationToken Caller, Token;
        internal readonly CancellationTokenSource Deadline = new(SyncTeXWire.DeadlineMs);
        internal readonly CancellationTokenSource Linked;
        internal Task<SyncTeXQueryOutcome> Work = null!;
        internal readonly List<Task> Auxiliary = [];
        internal Port? Port;
        internal bool Interrupted;
        internal Operation(CancellationToken caller)
        {
            Caller = caller;
            Linked = CancellationTokenSource.CreateLinkedTokenSource(caller, Deadline.Token);
            Token = Linked.Token;
        }
    }
    private sealed class Port(IJSObjectReference module, IJSObjectReference channel)
    {
        private readonly object _gate = new();
        private Task? _close;
        internal IJSObjectReference Channel { get; } = channel;
        internal Task CloseAsync() { lock (_gate) return _close ??= CloseCoreAsync(); }
        private async Task CloseCoreAsync()
        {
            await Task.Yield();
            try { await Channel.InvokeVoidAsync("dispose").ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            await ReleaseAsync(Channel).ConfigureAwait(false);
            await ReleaseAsync(module).ConfigureAwait(false);
        }
    }
}
