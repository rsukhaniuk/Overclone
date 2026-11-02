using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace Client.Analysis;

// RustSourceAnalyzer is the ONLY C# boundary that talks to the Rust analysis
// worker. It generates a requestId, invokes the EXISTING AnalysisRequestPacker,
// lazy-loads the rust-bridge JS module (single-flight shared import, not
// permanently faulted and not canceled for other callers; concurrent imports do
// not leak duplicate module refs), awaits the bridge JS Promise via IJSRuntime,
// and maps the canonical response to a typed job result using the existing
// AnalysisJson.ParseResponse (initialize/cancel ACK are NOT fed to it).
//
// Cancellation explicitly cancels the JS operation by id (cancelSource) AND uses
// a per-call linked token (ct + a dispose signal) for prompt .NET-side
// cancellation — never a .NET-only InvokeAsync cancellation that abandons a
// live JS job. Shared import/initialize does NOT take a caller token (one
// caller's ct must not cancel/fault the shared import for other callers); the
// caller rechecks its token after the await and returns Cancelled without
// dispatching.
//
// The worker epoch at settlement is carried in every terminal result (read from
// the bridge response, never a current-global read after an await) so a later
// publisher can discard stale pre-reset results. Disposal is idempotent and
// bounded: it cancels pending waits without waiting for a non-cooperative
// import; a module that arrives late is cleaned up exactly once; after
// disposal no fresh worker is created and AnalyzeAsync returns Disposed.

public sealed class RustSourceAnalyzer : ISourceAnalyzer
{
    private const string DefaultBridgeModule = "./js/rust-bridge.js";

    private readonly IJSRuntime _js;
    private readonly string _modulePath;
    private readonly CancellationTokenSource _disposeCts = new();
    private Task<IJSObjectReference>? _bridgeTask;
    private readonly object _bridgeLock = new();
    private bool _disposed;
    private int _bridgeDisposed; // 0/1 guard for exactly-once module disposal
    private long _lastKnownEpoch;

    public RustSourceAnalyzer(IJSRuntime js) : this(js, DefaultBridgeModule) { }

    /// <summary>Testable constructor with an explicit module path.</summary>
    public RustSourceAnalyzer(IJSRuntime js, string modulePath)
    {
        _js = js;
        _modulePath = modulePath;
    }

    /// <summary>Best-effort current epoch snapshot for synthesized (non-bridge)
    /// terminal outcomes; updated from every bridge response.</summary>
    public long LastKnownWorkerEpoch => Interlocked.Read(ref _lastKnownEpoch);

    public async Task<AnalysisJobResult> AnalyzeAsync(AnalysisSnapshot snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_disposed) return new AnalysisJobResult.Disposed();

        string requestId = Guid.NewGuid().ToString("N");

        // Cancellation before dispatch returns a typed Cancelled result (never a
        // thrown OperationCanceledException that abandons the caller).
        if (ct.IsCancellationRequested) return CancelledSynthesized(requestId);

        string metadataJson;
        byte[] packedBytes;
        try
        {
            (metadataJson, packedBytes) = AnalysisRequestPacker.Pack(snapshot, requestId);
        }
        catch (ArgumentException)
        {
            return FailedSafe(requestId, "InvalidInput", "The analysis request was malformed.");
        }
        catch (JsonException)
        {
            return FailedSafe(requestId, "InvalidInput", "The analysis request was malformed.");
        }
        catch (InvalidOperationException)
        {
            return FailedSafe(requestId, "QuotaExceeded", "The analysis request exceeded the input limits.");
        }

        // Per-call linked token: ct + a dispose signal. The shared import does
        // NOT take this token (other callers' ct must not fault it); the WAIT
        // (.WaitAsync) does, so a cancel/dispose settles promptly without
        // abandoning a live JS job or waiting for a non-cooperative import.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        var linked = linkedCts.Token;

        IJSObjectReference bridge;
        try
        {
            bridge = await GetBridgeAsync(AnalysisCatalog.IsThesis(snapshot.Profile) ? CancellationToken.None : linked).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return CancelledOrDisposed(requestId); }
        catch (JSException) { return FailedSafe(requestId, "WorkerCrashed", "The analysis worker could not be reached."); }
        catch (ObjectDisposedException) { return new AnalysisJobResult.Disposed(); }

        // Recheck after the shared import/initialize await: do not dispatch after
        // cancellation or disposal.
        if (_disposed) return new AnalysisJobResult.Disposed();
        if (linked.IsCancellationRequested) return CancelledSynthesized(requestId);

        // Register explicit JS cancel-by-id BEFORE dispatching so a cancel that
        // arrives while JS ready/init is pending cannot be lost, and recheck after
        // registering (a pre-registered cancellation runs immediately).
        CancellationTokenRegistration reg = default;
        var cancelSignal = AnalysisCatalog.IsThesis(snapshot.Profile) ? linked : ct;
        if (cancelSignal.CanBeCanceled)
        {
            reg = cancelSignal.Register(() => _ = SafeCancelAsync(bridge, requestId));
        }
        try
        {
            if (linked.IsCancellationRequested) return CancelledSynthesized(requestId);

            AnalysisBridgeResponse resp;
            try
            {
                resp = await bridge.InvokeAsync<AnalysisBridgeResponse>("analyzeSource", AnalysisCatalog.IsThesis(snapshot.Profile) ? CancellationToken.None : linked, metadataJson, packedBytes)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return CancelledOrDisposed(requestId); }
            catch (JSException) { return FailedSafe(requestId, "WorkerCrashed", "The analysis worker could not be reached."); }
            catch (ObjectDisposedException) { return new AnalysisJobResult.Disposed(); }

            return Interpret(resp, requestId, snapshot, ct);
        }
        finally
        {
            reg.Dispose();
        }
    }

    public async Task<bool> IsCurrentWorkerEpochAsync(long epoch, CancellationToken ct)
    {
        if (_disposed) return false;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        IJSObjectReference bridge;
        try { bridge = await GetBridgeAsync(linkedCts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
        catch (JSException) { return false; }
        catch (ObjectDisposedException) { return false; }
        try
        {
            var current = await bridge.InvokeAsync<long>("getWorkerEpoch", linkedCts.Token).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastKnownEpoch, current);
            return current == epoch;
        }
        catch (OperationCanceledException) { return false; }
        catch (JSException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _disposeCts.Cancel();
        var task = _bridgeTask;
        _bridgeTask = null;
        if (task is null) return;
        // Bounded disposal: do NOT wait for a non-cooperative import/job. A
        // module that arrives late is cleaned up exactly once (ImportBridgeAsync
        // observes _disposed and disposes under the _bridgeDisposed guard).
        if (!task.IsCompleted) return;
        try
        {
            var bridge = await task.ConfigureAwait(false);
            if (Interlocked.Exchange(ref _bridgeDisposed, 1) == 0)
            {
                try { await bridge.InvokeVoidAsync("dispose").ConfigureAwait(false); }
                catch { /* expected teardown */ }
                await bridge.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch { /* expected teardown */ }
    }

    // ---- helpers ----

    private async Task<IJSObjectReference> GetBridgeAsync(CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RustSourceAnalyzer));
        var task = _bridgeTask;
        if (task is null)
        {
            lock (_bridgeLock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(RustSourceAnalyzer));
                task = _bridgeTask ??= ImportBridgeAsync();
            }
        }
        // Per-caller cancellation of the WAIT only (the shared import is not
        // canceled for other callers). A genuine interop fault clears the
        // shared task so a later caller can retry; a programmer error propagates.
        try
        {
            return await task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // per-caller cancel (ct or dispose signal); do NOT fault the shared import.
            throw;
        }
        catch (JSException)
        {
            lock (_bridgeLock) { if (ReferenceEquals(_bridgeTask, task)) _bridgeTask = null; }
            throw;
        }
    }

    private async Task<IJSObjectReference> ImportBridgeAsync()
    {
        // The shared import does NOT take a caller token (one caller's ct must not
        // cancel the import for other independent callers). Callers recheck
        // their (linked) token after the await.
        var bridge = await _js.InvokeAsync<IJSObjectReference>("import", _modulePath).ConfigureAwait(false);
        if (_disposed)
        {
            // Arrived late: clean up the module exactly once (guarded).
            if (Interlocked.Exchange(ref _bridgeDisposed, 1) == 0)
            {
                try { await bridge.DisposeAsync().ConfigureAwait(false); }
                catch { /* best-effort */ }
            }
            throw new ObjectDisposedException(nameof(RustSourceAnalyzer));
        }
        return bridge;
    }

    private static async Task SafeCancelAsync(IJSObjectReference bridge, string requestId)
    {
        // Best-effort: the deadline/dispose settles the job if this fails. Only
        // expected teardown exceptions are suppressed; a programmer error
        // propagates and is observed (no unobserved failed task).
        try { await bridge.InvokeVoidAsync("cancelSource", requestId).ConfigureAwait(false); }
        catch (JSException) { /* worker gone / unreachable */ }
        catch (OperationCanceledException) { /* disposing concurrently */ }
        catch (ObjectDisposedException) { /* bridge disposed concurrently */ }
    }

    private AnalysisJobResult Interpret(AnalysisBridgeResponse resp, string requestId, AnalysisSnapshot snapshot, CancellationToken ct)
    {
        // A post-await caller cancellation/disposal cannot return Completed
        // from a stale just-settled invocation.
        if (_disposed) return new AnalysisJobResult.Disposed();
        if (ct.IsCancellationRequested) return CancelledSynthesized(requestId);

        var json = resp?.ResponseJson;
        var workerEpoch = resp?.WorkerEpoch ?? 0L;
        if (workerEpoch != 0) Interlocked.Exchange(ref _lastKnownEpoch, workerEpoch);

        if (resp is null || string.IsNullOrEmpty(json))
            return FailedSafeWithEpoch(requestId, workerEpoch, "InvalidInput", "The analysis worker returned an empty response.");

        // Common bounded wire validation BEFORE any JsonDocument.Parse: an
        // oversized envelope (any status, including cancelled which would
        // otherwise bypass the body bound) is rejected before an unbounded
        // graph is allocated. The char length is a cheap upper bound; the UTF-8
        // byte count is the exact bound (a lone surrogate encodes to '?', no throw).
        if (json.Length > AnalysisJson.MaxResponseBytes
            || Encoding.UTF8.GetByteCount(json) > AnalysisJson.MaxResponseBytes)
            return FailedSafeWithEpoch(requestId, workerEpoch, "InvalidInput", "The analysis worker response exceeded the hard response limit.");

        string status;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return FailedSafeWithEpoch(requestId, workerEpoch, "InvalidInput", "The analysis worker returned a non-object response.");
            KnuWireValidation.NoDuplicates(doc.RootElement);
            if (!doc.RootElement.TryGetProperty("status", out var s) || s.ValueKind != JsonValueKind.String)
                return FailedSafeWithEpoch(requestId, workerEpoch, "InvalidInput", "The analysis worker returned a response without a status.");
            status = s.GetString() ?? "";
        }
        // Narrow local catches only (no broad programmer-exception suppression):
        // a malformed document is a JsonException; GetString on a non-string
        // (should be unreachable after the ValueKind check) is InvalidOperationException.
        catch (JsonException)
        {
            return FailedSafeWithEpoch(requestId, workerEpoch, "InvalidInput", "The analysis worker returned a malformed response.");
        }
        catch (InvalidOperationException)
        {
            return FailedSafeWithEpoch(requestId, workerEpoch, "InvalidInput", "The analysis worker returned a malformed response.");
        }

        switch (status)
        {
            case "completed":
                {
                    if (Encoding.UTF8.GetByteCount(json) > snapshot.Limits.MaxResultBytes)
                        return FailedSafeWithEpoch(requestId, workerEpoch, "InvalidInput", "The completed analysis exceeded the requested result limit.");
                    AnalysisResponse.Completed c;
                    try { c = (AnalysisResponse.Completed)(AnalysisCatalog.IsThesis(snapshot.Profile) ? ThesisValidation.ParseResponse(json, snapshot, requestId) : AnalysisJson.ParseResponse(json)); }
                    catch (JsonException) { return FailedSafeWithEpoch(requestId, workerEpoch, "InvalidInput", "The analysis worker returned a malformed completed response."); }
                    catch (InvalidOperationException) { return FailedSafeWithEpoch(requestId, workerEpoch, "InvalidInput", "The analysis worker returned a malformed completed response."); }
                    if (!string.Equals(c.RequestId, requestId, StringComparison.Ordinal))
                        return FailedSafeWithEpoch(requestId, workerEpoch, "InvalidInput", "The analysis response did not match the request.");
                    if (!string.Equals(c.Result.SnapshotId, snapshot.Id.ToString(), StringComparison.Ordinal))
                        return FailedSafeWithEpoch(requestId, workerEpoch, "InvalidInput", "The analysis response did not match the requested snapshot.");
                    if (!string.Equals(c.Result.Profile.Id, snapshot.Profile.Id, StringComparison.Ordinal)
                        || !string.Equals(c.Result.Profile.Version, snapshot.Profile.Version, StringComparison.Ordinal))
                        return FailedSafeWithEpoch(requestId, workerEpoch, "InvalidInput", "The analysis response did not match the requested profile.");
                    try { KnuWireValidation.Snapshot(c.Result, snapshot); }
                    catch (JsonException) { return FailedSafeWithEpoch(requestId, workerEpoch, "InvalidInput", "The analysis response did not match the captured bytes or selection."); }
                    return new AnalysisJobResult.Completed(requestId, workerEpoch, c.Result);
                }
            case "failed":
                {
                    AnalysisResponse.Failed f;
                    try { f = (AnalysisResponse.Failed)(AnalysisCatalog.IsThesis(snapshot.Profile) ? ThesisValidation.ParseResponse(json, snapshot, requestId) : AnalysisJson.ParseResponse(json)); }
                    catch (JsonException) { return FailedSafeWithEpoch(requestId, workerEpoch, "InvalidInput", "The analysis worker returned a malformed failed response."); }
                    catch (InvalidOperationException) { return FailedSafeWithEpoch(requestId, workerEpoch, "InvalidInput", "The analysis worker returned a malformed failed response."); }
                    // A failed envelope may legitimately carry no trusted requestId
                    // (Rust could not recover one); but a nonnull id that does NOT
                    // match the request is rejected (never silently accepted).
                    if (f.RequestId is { } rid && !string.Equals(rid, requestId, StringComparison.Ordinal))
                        return FailedSafeWithEpoch(requestId, workerEpoch, "InvalidInput", "The analysis response did not match the request.");
                    return new AnalysisJobResult.Failed(requestId, workerEpoch, f.Error);
                }
            case "cancelled":
                {
                    // A cancelled envelope must carry the valid trusted requestId of
                    // this request; an invalid/missing/foreign id, or a bad
                    // protocolVersion, is NOT a legitimate Cancelled. Check ValueKind
                    // BEFORE TryGetInt32 (a string "1" throws InvalidOperationException,
                    // not returns false). GetString on a string with a lone surrogate
                    // is wrapped narrowly.
                    JsonDocument doc;
                    try { doc = JsonDocument.Parse(json); }
                    catch (JsonException) { return FailedSafeWithEpoch(requestId, workerEpoch, "InvalidInput", "The cancelled response was malformed."); }
                    try
                    {
                        using (doc)
                        {
                            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                                return FailedSafeWithEpoch(requestId, workerEpoch, "InvalidInput", "The cancelled response was malformed.");
                            if (!doc.RootElement.TryGetProperty("requestId", out var r) || r.ValueKind != JsonValueKind.String)
                                return FailedSafeWithEpoch(requestId, workerEpoch, "InvalidInput", "The cancelled response did not carry a valid requestId.");
                            string rid = r.GetString()!;
                            if (!AnalysisIdentity.IsValidOpaqueId(rid, AnalysisLimits.MaxIdBytes))
                                return FailedSafeWithEpoch(requestId, workerEpoch, "InvalidInput", "The cancelled response did not carry a valid requestId.");
                            if (!doc.RootElement.TryGetProperty("protocolVersion", out var pv)
                                || pv.ValueKind != JsonValueKind.Number
                                || !pv.TryGetInt32(out var pvn) || pvn != 1)
                                return FailedSafeWithEpoch(requestId, workerEpoch, "UnsupportedProtocol", "The cancelled response has an unsupported protocolVersion.");
                            if (!string.Equals(rid, requestId, StringComparison.Ordinal))
                                return FailedSafeWithEpoch(requestId, workerEpoch, "InvalidInput", "The cancelled response did not match the request.");
                            if ((doc.RootElement.TryGetProperty("payload", out var pl) && pl.ValueKind != JsonValueKind.Null)
                                || (doc.RootElement.TryGetProperty("error", out var er) && er.ValueKind != JsonValueKind.Null))
                                return FailedSafeWithEpoch(requestId, workerEpoch, "InvalidInput", "The cancelled response carried an unexpected body.");
                            return new AnalysisJobResult.Cancelled(requestId, workerEpoch);
                        }
                    }
                    catch (JsonException) { return FailedSafeWithEpoch(requestId, workerEpoch, "InvalidInput", "The cancelled response was malformed."); }
                    catch (InvalidOperationException) { return FailedSafeWithEpoch(requestId, workerEpoch, "InvalidInput", "The cancelled response was malformed."); }
                }
            default:
                return FailedSafeWithEpoch(requestId, workerEpoch, "UnsupportedCapability", "The analysis worker returned an unknown status.");
        }
    }

    private AnalysisJobResult.Failed FailedSafe(string requestId, string code, string message)
        => new(requestId, LastKnownWorkerEpoch, new AnalysisErrorDto(code, message, false));

    private static AnalysisJobResult.Failed FailedSafeWithEpoch(string requestId, long workerEpoch, string code, string message)
        => new(requestId, workerEpoch, new AnalysisErrorDto(code, message, false));

    private AnalysisJobResult.Cancelled CancelledSynthesized(string requestId)
        => new(requestId, LastKnownWorkerEpoch);

    private AnalysisJobResult CancelledOrDisposed(string requestId) =>
        _disposed ? new AnalysisJobResult.Disposed() : CancelledSynthesized(requestId);
}
