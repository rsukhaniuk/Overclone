namespace Client.Analysis;

// Immutable coordinator state (M2a-2b-2). This is C# orchestration metadata
// only — it is NOT a competing Rust revision id and NOT a wire requirement.
// Only <see cref="AnalysisState.Completed"/> exposes a currently-usable report;
// every other state has no visible report (a stale report is dropped the moment
// the intent is invalidated, BEFORE any debounce dispatch). Diagnostics from
// different jobs are never merged: each Completed carries exactly one
// (snapshot, result) pair and is fully replaced on the next publish.
//
// States distinguish honest outcomes:
// - NoProject: no project is open (close/reset); no work is queued.
// - Pending: an invalidation queued a fresh analyze and the debounce has not yet
//   matured (or it matured but an active workflow has not yet settled).
// - Running: a capture/analyze/epoch-validation workflow is in flight.
// - Completed: a fresh analysis finished; the report is currently usable.
// - Cancelled: an explicit Cancel cleared queued/active work; no implicit restart.
// - InvalidInput: capture rejected malformed input (e.g. lone surrogate); the
//   caller must fix the input and request again.
// - QuotaExceeded: capture rejected an over-limit request before packing.
// - Unavailable: capture could not read/verify a text file's bytes (no empty
//   substitution); the caller must resolve storage and request again.
// - Superseded: the analysis finished but a newer intent is NOT queued yet the
//   result was stale (worker-epoch/correlation mismatch); no report is shown
//   until an explicit request.
// - Failed: the analyzer returned a typed safe error (worker crash/timeout/etc).
// - Disposed: the coordinator was disposed; methods no longer revive work.

public abstract record AnalysisState
{
    private AnalysisState() { }

    public sealed record NoProject : AnalysisState;
    public sealed record Pending : AnalysisState;
    public sealed record Running : AnalysisState;
    public sealed record Completed(AnalysisReport Report) : AnalysisState;
    public sealed record Cancelled : AnalysisState;
    public sealed record InvalidInput(string SafeMessage) : AnalysisState;
    public sealed record QuotaExceeded(string SafeMessage) : AnalysisState;
    public sealed record Unavailable(string SafeMessage) : AnalysisState;
    public sealed record Superseded : AnalysisState;
    public sealed record Failed(AnalysisErrorDto Error) : AnalysisState;
    public sealed record Disposed : AnalysisState;
}

/// <summary>The currently-usable analysis report, published only after every
/// freshness check passes (workspace identity, factory epoch, profile,
/// snapshotId/profile correlation and worker epoch) immediately before the
/// synchronous state publication. Retains the exact immutable
/// <see cref="AnalysisSnapshot"/> (privately owned by the factory), the exact
/// <see cref="AnalysisResultDto"/> (coverage, unsupported, unavailableCapabilities
/// and excluded-binary metadata retained unchanged from the snapshot), plus the
/// correlated requestId and worker epoch. No invented PDF/KNU findings, no
/// compliance statement, no auto-edits.</summary>
public sealed record AnalysisReport(
    AnalysisSnapshot Snapshot,
    AnalysisResultDto Result,
    string RequestId,
    long WorkerEpoch);
