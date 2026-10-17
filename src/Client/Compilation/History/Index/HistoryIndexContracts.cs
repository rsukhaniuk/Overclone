using System.Collections.Immutable;
using Client.Workspace.Domain;

namespace Client.Compilation.History.Index;

public enum HistoryIndexStatus
{
    Missing, Ready, Saved, AlreadyIndexed, Conflict, RecordConflict, WrongProject,
    Full, LimitExceeded, InvalidInput, Corrupt, UnsupportedSchema, Unavailable,
    PermissionDenied, QuotaExceeded, StorageFailure, Busy, Cancelled, TimedOut, Disposed, Uncertain
}
public sealed record HistoryIndexBudget(int MaxEntries, int MaxJsonBytes, int DeadlineMs)
{
    public bool IsValid => MaxEntries is > 0 and <= 64 && MaxJsonBytes is > 0 and <= 65536 && DeadlineMs is > 0 and <= 30000;
}
/// <summary>Untrusted discovery pointers only, in append order. Opening STILL requires
/// CompileArtifactHistoryStore.ReadAsync(expectedKey, expectedProject, ...).
/// This is never an artifact receipt, accepted output, request context or currentness.</summary>
public sealed record HistoryIndexSnapshot(ProjectId Project, long Revision, ImmutableArray<HistoricalArtifactKey> Entries);
public sealed record HistoryIndexResult(HistoryIndexStatus Status, HistoryIndexSnapshot? Snapshot = null,
    bool WriteMayHaveCommitted = false, HistoryIndexStatus? Cause = null);
// A read/CAS token is separate from the opaque metadata. Both must agree on read.
public sealed record HistoryIndexRead(string Revision, string? Json);
public sealed record HistoryIndexCas(HistoryIndexStatus Status);
public interface IHistoryIndexBackend : IAsyncDisposable
{
    Task<HistoryIndexRead> ReadAsync(ProjectId project, int maxJsonBytes);
    // Return failures ONLY for known no-write/aborted outcomes. Lost/unknown
    // acknowledgements must throw: the service reports Uncertain after dispatch.
    Task<HistoryIndexCas> CompareExchangeAsync(ProjectId project, long expectedRevision, long nextRevision, string json, int maxJsonBytes);
}
public sealed class HistoryIndexException(HistoryIndexStatus status) : Exception
{ public HistoryIndexStatus Status { get; } = status; }
