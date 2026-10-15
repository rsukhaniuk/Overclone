using System.Collections.Immutable;
using Client.Compilation.Lifecycle;

namespace Client.Compilation.Presentation;

public enum CompileLogCompleteness { Unknown }

/// <summary>Metadata from an actual B2a accepted response, after lifecycle freshness
/// and terminal settlement. No public constructor, payload accessor, mutable raw
/// envelope or workspace lookup. Immutable log/entry storage and the trusted
/// request/snapshot are retained by reference, not duplicated or reconstructed.
/// Log text is local plain data, never HTML. B2a log text (LogText only) admits
/// TAB/LF/CR among control characters; all other metadata fields (descriptors,
/// diagnostic codes, SafeMessage) keep strict control rejection. This API does
/// not repair, normalize or bypass that boundary beyond the log-only whitelist.</summary>
public sealed class ValidatedCompileAttempt
{
    internal ValidatedCompileAttempt(CompileJobId jobId, AcceptedCompileOutput accepted)
    {
        JobId = jobId; RequestContext = accepted.RequestContext; Status = accepted.Status;
        LogText = accepted.LogText; Diagnostics = accepted.Diagnostics;
    }
    public CompileJobId JobId { get; }
    public CompileRequestContext RequestContext { get; }
    public CompilationInputSnapshot Snapshot => RequestContext.Snapshot;
    public CompilationSnapshotIdentity SnapshotIdentity => RequestContext.SnapshotIdentity;
    public Client.Workspace.Domain.SHA256 InputContentDigest => RequestContext.InputContentDigest;
    public CompileOutputStatus Status { get; }
    public string LogText { get; }
    public ImmutableArray<CompileDiagnosticEntry> Diagnostics { get; }
    /// <summary>B2a has no completeness field. Unknown even on Succeeded or empty
    /// text; no native completeness protocol or clean/no-errors claim is made.</summary>
    public CompileLogCompleteness LogCompleteness => CompileLogCompleteness.Unknown;
}
