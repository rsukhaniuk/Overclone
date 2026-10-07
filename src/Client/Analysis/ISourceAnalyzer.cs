using System.Threading;
using System.Threading.Tasks;

namespace Client.Analysis;

// Application port for independent source analysis (M2a-2b-1). The adapter
// runs the actual Rust analysis worker over the canonical v1 envelope via JS
// interop; it owns no parser/rule/source-mapping policy (Rust owns analysis,
// C# owns orchestration). Results are typed: a Completed job carries the
// analysis result DTO; a Failed job carries a safe error DTO; a Cancelled job
// is distinct from both; Disposed is returned after disposal. No fake empty
// success is ever produced. The adapter is the ONLY public boundary that
// talks to the worker; the coordinator (M2a-2b-2, not built here) will call it.

/// <summary>Typed analysis job outcome. Every terminal outcome carries the
/// correlated requestId and the worker epoch at the time of settlement so a
/// later publisher can validate freshness (stale pre-reset results are
/// discarded). <see cref="Completed"/> additionally carries the analysis
/// result; <see cref="Failed"/> carries a safe error. No outcome reinterprets
/// Rust rule outcomes or runs a second lexical engine.</summary>
public abstract record AnalysisJobResult
{
    private AnalysisJobResult() { }

    public sealed record Completed(string RequestId, long WorkerEpoch, AnalysisResultDto Result) : AnalysisJobResult;
    public sealed record Failed(string RequestId, long WorkerEpoch, AnalysisErrorDto Error) : AnalysisJobResult;
    public sealed record Cancelled(string RequestId, long WorkerEpoch) : AnalysisJobResult;
    /// <summary>The analyzer was disposed; no worker is created and no
    /// dispatch occurs. A later call after disposal never revives a fresh
    /// worker.</summary>
    public sealed record Disposed() : AnalysisJobResult;
}

/// <summary>Bridge transport DTO: the raw canonical response JSON string
/// (forwarded unchanged from the worker — no numeric precision loss) plus the
/// worker epoch at settlement. Deserialized from the JS object
/// { responseJson, workerEpoch } returned by rust-bridge.analyzeSource.</summary>
public sealed record AnalysisBridgeResponse(string ResponseJson, long WorkerEpoch);

/// <summary>Independent source analyzer port. <see cref="AnalyzeAsync"/> packs
/// the immutable snapshot via the existing <see cref="AnalysisRequestPacker"/>,
/// dispatches the actual Rust worker, and returns a typed job result.
/// <see cref="IsCurrentWorkerEpochAsync"/> lets a later publisher confirm a
/// returned epoch is still current before publishing (freshness).</summary>
public interface ISourceAnalyzer : IAsyncDisposable
{
    /// <summary>Analyze an immutable snapshot. Generates a requestId, invokes
    /// the existing packer, lazy-loads and initializes the rust-bridge worker,
    /// awaits the bridge JS Promise via IJSRuntime, and maps the response to a
    /// typed Completed/Failed/Cancelled result. Cancellation explicitly
    /// cancels the JS operation by id (not a .NET-only InvokeAsync cancellation
    /// that abandons a live JS job). Disposal during a pending job settles it.
    /// Never leaks raw payload/source/internal exception details.</summary>
    Task<AnalysisJobResult> AnalyzeAsync(AnalysisSnapshot snapshot, CancellationToken ct);

    /// <summary>True iff <paramref name="epoch"/> is still the current worker
    /// epoch (no replacement/reset/dispose/crash has bumped it). Used by a later
    /// publisher to discard stale pre-reset results.</summary>
    Task<bool> IsCurrentWorkerEpochAsync(long epoch, CancellationToken ct);
}
