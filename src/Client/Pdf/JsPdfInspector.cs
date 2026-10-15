using System.Text.Json;
using Microsoft.JSInterop;

namespace Client.Pdf;

// Deliberately unregistered. Caller explicitly supplies provisioning; absence does
// not import a module, launch a worker or select/download a renderer dependency.
public sealed class JsPdfInspector : IPdfInspector
{
    private readonly IJSRuntime _js;
    private readonly IPdfRuntimeProvider _runtime;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task<Port>? _initialization;
    // Logical wait cancellation is not provider completion. Keep actual work
    // admitted until its task settles; never clear it from an old continuation.
    private Task<PdfRuntimePackage?>? _providerOperation;
    private Port? _live;
    private bool _disposed;
    private int _busy;
    private long _sequence;
    public JsPdfInspector(IJSRuntime js, IPdfRuntimeProvider runtime) { _js = js; _runtime = runtime; }

    public async Task<PdfInspectionOutcome> InspectAsync(PdfInspectRequest request, CancellationToken cancellationToken = default)
    {
        string id = Guid.NewGuid().ToString("N"), sha = request?.Document?.Sha256 ?? "";
        long epoch = Interlocked.Increment(ref _sequence);
        PdfRuntimeIdentity? runtimeIdentity = null;
        PdfInspectionOutcome End(PdfInspectionStatus status, string code = "") => new(status, id, sha, epoch, Code: code, Runtime: runtimeIdentity);
        CancellationTokenSource linked;
        CancellationTokenSource deadline;
        lock (_gate)
        {
            if (_disposed) return End(PdfInspectionStatus.Disposed);
            if (request is null || !request.IsValid || epoch > 9007199254740991) return End(PdfInspectionStatus.InvalidInput);
            if (_busy != 0 || _providerOperation is { IsCompleted: false }) return End(PdfInspectionStatus.Busy);
            _providerOperation = null;
            if (cancellationToken.IsCancellationRequested) return End(PdfInspectionStatus.Cancelled);
            _busy = 1; deadline = new(); deadline.CancelAfter(request.Budget.DeadlineMs);
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token, deadline.Token);
        }
        Port? cancellationPort = null;
        PdfInspectionOutcome Interrupted() => End(Volatile.Read(ref _disposed) ? PdfInspectionStatus.Disposed
            : cancellationToken.IsCancellationRequested ? PdfInspectionStatus.Cancelled : PdfInspectionStatus.TimedOut);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            var packageTask = _runtime.GetAsync(linked.Token).AsTask();
            Observe(packageTask);
            // _busy reserves admission across the synchronous call-out above.
            // Completed/faulted/canceled tasks release by actual IsCompleted,
            // not a racy count or a callback that can clear a newer operation.
            lock (_gate) if (!_disposed) _providerOperation = packageTask;
            var package = await packageTask.WaitAsync(linked.Token).ConfigureAwait(false);
            if (linked.IsCancellationRequested) return Interrupted();
            if (package is null) return End(PdfInspectionStatus.Unavailable, "RuntimeAbsent");
            if (!package.IsValid) return End(PdfInspectionStatus.Unavailable, "RuntimeMismatch");
            runtimeIdentity = new(package.LoaderSha256, package.WasmSha256);
            var port = await GetPortAsync(linked.Token).ConfigureAwait(false);
            cancellationPort = port;
            if (linked.IsCancellationRequested) return Interrupted();
            using var registration = linked.Token.Register(() => Observe(CancelAsync(port, id)));
            if (linked.IsCancellationRequested) return Interrupted();
            string metadata = JsonSerializer.Serialize(new
            {
                version = 2,
                inspectActions = request.InspectActions,
                requestId = id,
                documentSha256 = sha,
                epoch,
                pageIndex = request.PageIndex,
                search = request.Search,
                raster = request.Raster,
                width = request.RasterWidth,
                height = request.RasterHeight,
                loaderSha256 = package.LoaderSha256,
                wasmSha256 = package.WasmSha256,
                budget = new
                {
                    inputBytes = request.Budget.InputBytes,
                    pages = request.Budget.Pages,
                    textUnits = request.Budget.TextUnits,
                    searchHits = request.Budget.SearchHits,
                    rasterPixels = request.Budget.RasterPixels,
                    outputBytes = request.Budget.OutputBytes,
                    liveHandles = request.Budget.LiveHandles,
                    deadlineMs = request.Budget.DeadlineMs
                }
            });
            // Explicit cancellation-by-id accompanies cancellation of the WAIT;
            // neither late import nor abandoned JS work may create a fresh job.
            var invocation = port.Channel.InvokeAsync<PdfWireReply>("inspect", metadata, request.Document.CopyBytes(), package.CopyLoader(), package.CopyWasm()).AsTask();
            Observe(invocation);
            var wire = await invocation.WaitAsync(linked.Token).ConfigureAwait(false);
            if (linked.IsCancellationRequested || Volatile.Read(ref _disposed)) return Interrupted();
            return PdfWireParser.Parse(wire, request, id, epoch, runtimeIdentity);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { return Interrupted(); }
        catch (OperationCanceledException) { return End(PdfInspectionStatus.TransportError, "ForeignCancellation"); }
        catch (ObjectDisposedException) { return End(Volatile.Read(ref _disposed) ? PdfInspectionStatus.Disposed : PdfInspectionStatus.TransportError); }
        catch (JsonException) { return End(PdfInspectionStatus.InvalidResponse); }
        catch (JSException) { return End(PdfInspectionStatus.TransportError); }
        catch (Exception error) when (error is not OutOfMemoryException) { return End(PdfInspectionStatus.TransportError); }
        finally
        {
            try
            {
                // WaitAsync cancellation may resume inline and dispose the earlier
                // token registration BEFORE its JS-cancel callback executes.
                // Always issue the idempotent termination request independently.
                if (linked.IsCancellationRequested && cancellationPort is not null)
                    await BoundedAsync(CancelAsync(cancellationPort, id)).ConfigureAwait(false);
            }
            finally { linked.Dispose(); deadline.Dispose(); lock (_gate) _busy = 0; }
        }
    }

    private async Task<Port> GetPortAsync(CancellationToken cancellationToken)
    {
        Task<Port> task;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            task = _initialization ??= InitializeAsync(); Observe(task);
        }
        try { return await task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            lock (_gate) if (ReferenceEquals(_initialization, task)) _initialization = null;
            throw;
        }
    }
    private async Task<Port> InitializeAsync()
    {
        var module = await _js.InvokeAsync<IJSObjectReference>("import", "./js/pdf-port-bridge.js").ConfigureAwait(false);
        IJSObjectReference? channel = null;
        try
        {
            if (Volatile.Read(ref _disposed)) throw new ObjectDisposedException(nameof(JsPdfInspector));
            channel = await module.InvokeAsync<IJSObjectReference>("createPdfPort").ConfigureAwait(false);
            if (channel is null) throw new JSException("PDF port was not created.");
            var port = new Port(module, channel);
            lock (_gate)
            {
                if (!_disposed) { _live = port; return port; }
            }
            await port.CloseAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(JsPdfInspector));
        }
        catch
        {
            // If a late Port was already closed, its guard owns both refs.
            if (channel is null) await ReleaseRefAsync(module).ConfigureAwait(false);
            else if (!Volatile.Read(ref _disposed)) { await ReleaseRefAsync(channel).ConfigureAwait(false); await ReleaseRefAsync(module).ConfigureAwait(false); }
            throw;
        }
    }
    public async ValueTask DisposeAsync()
    {
        Port? port;
        lock (_gate) { if (_disposed) return; _disposed = true; port = _live; _live = null; _providerOperation = null; }
        _shutdown.Cancel(); _shutdown.Dispose();
        if (port is not null) await port.CloseAsync().ConfigureAwait(false);
        // No blanket wait for a noncooperative import. InitializeAsync closes a
        // late module/channel exactly once and never dispatches after disposal.
    }
    private static async Task CancelAsync(Port port, string id)
    {
        try { await port.Channel.InvokeVoidAsync("cancel", id).ConfigureAwait(false); }
        catch (Exception error) when (error is JSException or ObjectDisposedException or OperationCanceledException) { }
    }
    private static void Observe(Task task) => _ = task.ContinueWith(static t => { _ = t.Exception; }, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private static async Task BoundedAsync(Task task)
    {
        Observe(task);
        try { await task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (Exception error) when (error is JSException or ObjectDisposedException or OperationCanceledException or TimeoutException) { }
    }
    private static Task ReleaseRefAsync(IJSObjectReference reference) => BoundedAsync(reference.DisposeAsync().AsTask());
    private sealed class Port(IJSObjectReference module, IJSObjectReference channel)
    {
        private int _closed;
        internal IJSObjectReference Channel { get; } = channel;
        internal async Task CloseAsync()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            await BoundedAsync(Channel.InvokeVoidAsync("dispose").AsTask()).ConfigureAwait(false);
            await ReleaseRefAsync(Channel).ConfigureAwait(false); await ReleaseRefAsync(module).ConfigureAwait(false);
        }
    }
}
