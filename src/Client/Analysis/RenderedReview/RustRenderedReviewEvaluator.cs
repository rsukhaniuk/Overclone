using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Client.Pdf;

using Client.Analysis.RenderedExtent;

namespace Client.Analysis.RenderedReview;

/// <summary>Owns one local Worker port. Imports/factory/evaluate calls are ACTUAL
/// uncancellable tasks; caller cancellation signals JS but never abandons their
/// settlement or frees admission. Disposal invalidates synchronously and observes
/// late cleanup without waiting on a noncooperative import.</summary>
public sealed class RustRenderedReviewEvaluator(IJSRuntime js) : IRenderedReviewEvaluator, IRenderedCaptionImageEvaluator
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task<Port>? _portTask;
    private bool _disposed;
    private int _busy;
    private long _lastEpoch;

    public Task<RenderedReviewEvaluationReply> EvaluateAsync(RenderedReviewRequest request, ReadOnlyMemory<byte> factsUtf8,
        CancellationToken cancellationToken = default)
        => EvaluateTransportAsync(request?.RequestId ?? "", request?.FactsSha256, factsUtf8,
            () => RenderedReviewJson.Pack(request!), cancellationToken);

    public async Task<RenderedCaptionImageEvaluationReply> EvaluateCaptionImageAsync(RenderedCaptionImageRequest request,
        CancellationToken cancellationToken = default)
    {
        var reply = await EvaluateTransportAsync(request?.Review.RequestId ?? "", request?.Review.FactsSha256,
            request?.FactsUtf8 ?? ReadOnlyMemory<byte>.Empty, () => RenderedCaptionImageJson.Pack(request!),
            cancellationToken).ConfigureAwait(false);
        if (reply.Status != RenderedExtentEvaluationStatus.Completed)
            return new(reply.Status, reply.RequestId, reply.WorkerEpoch);
        try { return RenderedCaptionImageJson.Parse(request!, reply.WorkerEpoch, reply.ResponseJson!); }
        catch (JsonException) { return new(RenderedExtentEvaluationStatus.InvalidResponse, reply.RequestId, reply.WorkerEpoch); }
    }

    private async Task<RenderedReviewEvaluationReply> EvaluateTransportAsync(string id, string? factsSha256,
        ReadOnlyMemory<byte> factsUtf8, Func<string> pack, CancellationToken cancellationToken)
    {
        RenderedReviewEvaluationReply End(RenderedExtentEvaluationStatus status) => new(status, id, 0);
        CancellationTokenSource linked;
        CancellationTokenRegistration registration = default;
        Task? cancellation = null;
        lock (_gate)
        {
            if (_disposed) return End(RenderedExtentEvaluationStatus.Disposed);
            if (_busy != 0) return End(RenderedExtentEvaluationStatus.Busy);
            _busy = 1;
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        }
        try
        {
            if (linked.IsCancellationRequested) return End(RenderedExtentEvaluationStatus.Cancelled);
            if (factsUtf8.Length is < 1 or > RenderedReviewContract.FactsMaxBytes) return End(RenderedExtentEvaluationStatus.InvalidInput);
            var facts = factsUtf8.ToArray(); // Own exact bounded bytes before any await.
            if (PdfHashes.Of(facts) != factsSha256) return End(RenderedExtentEvaluationStatus.InvalidInput);
            string json;
            try { json = pack(); }
            catch (ArgumentException) { return End(RenderedExtentEvaluationStatus.InvalidInput); }
            var port = await GetPortAsync().ConfigureAwait(false); // Actual transport task; no UI/model continuation or owner capture.
            if (_disposed) return End(RenderedExtentEvaluationStatus.Disposed);
            if (linked.IsCancellationRequested) return End(RenderedExtentEvaluationStatus.Cancelled);
            registration = linked.Token.Register(() => cancellation = CancelAsync(port, id));
            if (linked.IsCancellationRequested) return End(RenderedExtentEvaluationStatus.Cancelled);
            var reply = await BeginTransport(() => port.Channel.InvokeAsync<RenderedReviewBridgeReply>("evaluate", [json, facts])).ConfigureAwait(false);
            if (_disposed) return End(RenderedExtentEvaluationStatus.Disposed);
            if (linked.IsCancellationRequested) return End(RenderedExtentEvaluationStatus.Cancelled);
            if (reply is null || reply.Version != 1 || reply.RequestId != id
                || reply.WorkerEpoch < 0 || reply.WorkerEpoch > 9007199254740991)
                return End(RenderedExtentEvaluationStatus.InvalidResponse);
            if (reply.WorkerEpoch > 0)
            {
                if (reply.WorkerEpoch <= _lastEpoch) return End(RenderedExtentEvaluationStatus.InvalidResponse);
                _lastEpoch = reply.WorkerEpoch; // From THIS operation, never a global epoch read.
            }
            var status = reply.Status switch
            {
                "completed" => RenderedExtentEvaluationStatus.Completed, "invalidInput" => RenderedExtentEvaluationStatus.InvalidInput,
                "cancelled" => RenderedExtentEvaluationStatus.Cancelled, "timedOut" => RenderedExtentEvaluationStatus.TimedOut,
                "busy" => RenderedExtentEvaluationStatus.Busy, "disposed" => RenderedExtentEvaluationStatus.Disposed,
                "unavailable" => RenderedExtentEvaluationStatus.Unavailable, _ => RenderedExtentEvaluationStatus.InvalidResponse
            };
            if (status == RenderedExtentEvaluationStatus.Completed)
            {
                if (!RenderedExtentContract.Epoch(reply.WorkerEpoch) || reply.ResponseJson is null
                    || reply.ResponseJson.Length > RenderedReviewContract.ResponseMaxBytes || !PdfHashes.ValidUtf16(reply.ResponseJson)
                    || Encoding.UTF8.GetByteCount(reply.ResponseJson) > RenderedReviewContract.ResponseMaxBytes)
                    return End(RenderedExtentEvaluationStatus.InvalidResponse);
            }
            else if (reply.ResponseJson is not null) return End(RenderedExtentEvaluationStatus.InvalidResponse);
            return new(status, id, reply.WorkerEpoch, reply.ResponseJson);
        }
        catch (ObjectDisposedException) { return End(_disposed ? RenderedExtentEvaluationStatus.Disposed : RenderedExtentEvaluationStatus.TransportError); }
        catch (OperationCanceledException) { return End(linked.IsCancellationRequested ? RenderedExtentEvaluationStatus.Cancelled : RenderedExtentEvaluationStatus.TransportError); }
        catch (JsonException) { return End(RenderedExtentEvaluationStatus.InvalidResponse); }
        catch (JSException) { return End(RenderedExtentEvaluationStatus.Unavailable); }
        finally
        {
            registration.Dispose(); // Callback assignment is settled before examining its actual cancel task.
            if (cancellation is not null) await cancellation.ConfigureAwait(false);
            linked.Dispose(); lock (_gate) _busy = 0;
        }
    }

    private async Task<Port> GetPortAsync()
    {
        Task<Port> task;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            task = _portTask ??= InitializeAsync();
        }
        try { return await task.ConfigureAwait(false); }
        catch
        {
            lock (_gate) if (ReferenceEquals(_portTask, task)) _portTask = null;
            throw;
        }
    }
    private async Task<Port> InitializeAsync()
    {
        var module = await BeginTransport(() => js.InvokeAsync<IJSObjectReference>("import", ["./js/rendered-review-bridge.js"])).ConfigureAwait(false);
        if (module is null) throw new JSException("Extent module unavailable.");
        IJSObjectReference? channel = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            channel = await BeginTransport(() => module.InvokeAsync<IJSObjectReference>("createRenderedReviewPort", [])).ConfigureAwait(false); // Idle port, no Worker.
            if (channel is null) throw new JSException("Extent port unavailable.");
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new(module, channel);
        }
        catch
        {
            if (channel is not null) await new Port(module, channel).CloseAsync().ConfigureAwait(false);
            else await ReleaseAsync(module).ConfigureAwait(false);
            throw;
        }
    }
    public ValueTask DisposeAsync()
    {
        Task<Port>? task;
        lock (_gate) { if (_disposed) return ValueTask.CompletedTask; _disposed = true; task = _portTask; }
        _shutdown.Cancel(); _shutdown.Dispose();
        if (task is not null) Observe(CloseWhenReadyAsync(task));
        return ValueTask.CompletedTask; // No bounded-dispose wait on foreign init/evaluation.
    }
    private static async Task CloseWhenReadyAsync(Task<Port> task)
    { try { var port = await task.ConfigureAwait(false); await port.CloseAsync().ConfigureAwait(false); } catch (Exception e) when (e is not OutOfMemoryException) { } }
    // SDK 10.0.13 void, default-timeout and reference-disposal wrappers can
    // themselves await WITHOUT ConfigureAwait(false). Prevent that INTERNAL
    // capture too: start only SDK transport on this same thread without an
    // ambient owner, then immediately restore it. No UI/model work enters here.
    private static T BeginTransport<T>(Func<T> begin)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { return begin(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static async Task CancelAsync(Port port, string id)
    { try { await BeginTransport(() => port.Channel.InvokeAsync<IJSVoidResult>("cancel", [id])).ConfigureAwait(false); } catch (Exception e) when (e is not OutOfMemoryException) { } }
    private static async Task ReleaseAsync(IJSObjectReference reference)
    { try { await BeginTransport(reference.DisposeAsync).ConfigureAwait(false); } catch (Exception e) when (e is not OutOfMemoryException) { } }
    private static void Observe(Task task) => _ = task.ContinueWith(t => { _ = t.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private sealed class Port(IJSObjectReference module, IJSObjectReference channel)
    {
        private int _closed;
        internal IJSObjectReference Channel => channel;
        internal async Task CloseAsync()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try { await BeginTransport(() => channel.InvokeAsync<IJSVoidResult>("dispose", [])).ConfigureAwait(false); }
            catch (Exception e) when (e is not OutOfMemoryException) { }
            finally { await ReleaseAsync(channel).ConfigureAwait(false); await ReleaseAsync(module).ConfigureAwait(false); }
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RenderedReviewBridgeReply
{
    [JsonRequired] public int Version { get; set; }
    [JsonRequired] public string RequestId { get; set; } = "";
    [JsonRequired] public long WorkerEpoch { get; set; }
    [JsonRequired] public string Status { get; set; } = "";
    [JsonRequired] public string? ResponseJson { get; set; }
}
