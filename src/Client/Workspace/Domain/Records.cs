using System.Collections.Immutable;

namespace Client.Workspace.Domain;

/// <summary>A file manifest entry. Stable file ID is independent of the
/// normalized relative path; rename changes the path, not the ID.</summary>
public record ProjectFile(
    FileId Id,
    string RelativePath,
    string MediaKind,
    SHA256 ContentHash,
    long ByteLength);

/// <summary>Immutable project revision. The file manifest captures the exact
/// content objects referenced by this revision; settings are versioned with it.</summary>
public record ProjectRevision(
    RevisionId Id,
    ProjectId ProjectId,
    RevisionId? ParentRevisionId,
    ImmutableArray<ProjectFile> Files,
    DateTimeOffset CreatedAt)
{
    public static ProjectRevision Root(ProjectId projectId)
        => new(RevisionId.New(), projectId, null, ImmutableArray<ProjectFile>.Empty, DateTimeOffset.UtcNow);

    public ProjectFile? FindFile(FileId id)
    {
        foreach (var f in Files) if (f.Id == id) return f;
        return null;
    }
}

/// <summary>A project. Head points at the durable revision; null until the
/// first commit. Schema version supports additive migrations of the local
/// store. Engine configuration and rule profile are future fields, intentionally
/// omitted from this milestone.</summary>
public record Project(
    ProjectId Id,
    string Name,
    RevisionId? HeadRevisionId,
    int SchemaVersion = 1);

/// <summary>SHA-256-addressed, verified-before-reference content object stored
/// in OPFS. The location is derived from the hash.</summary>
public record ContentObject(SHA256 Hash, long ByteLength, bool Verified);

/// <summary>A content object to write during this commit. The repository writes
/// and verifies these to OPFS before the IndexedDB manifest/head transaction.</summary>
public record ContentInput(SHA256 Hash, byte[] Bytes, long ByteLength);

/// <summary>A commit batch: the selected head revision plus any unpersisted
/// ancestor manifests and content dependencies that must land in the same
/// IndexedDB transaction. Coalescing autosave never omits unpersisted parents;
/// for the non-coalescing M1a writer, Ancestors is empty and SelectedHead is the
/// single new revision.</summary>
public record ProjectCommit(
    ProjectRevision SelectedHead,
    ImmutableArray<ProjectRevision> Ancestors,
    ImmutableArray<ContentInput> Content)
{
    public static ProjectCommit Of(ProjectRevision head)
        => new(head, ImmutableArray<ProjectRevision>.Empty, ImmutableArray<ContentInput>.Empty);

    /// <summary>Every manifest that must be stored in this commit transaction.</summary>
    public IEnumerable<ProjectRevision> Manifests
    {
        get
        {
            foreach (var a in Ancestors) yield return a;
            yield return SelectedHead;
        }
    }
}

/// <summary>Restoring history creates a new revision; a checkpoint records a
/// recoverable revision and the reason it was captured.</summary>
public record HistoryCheckpoint(
    CheckpointId Id,
    ProjectId ProjectId,
    RevisionId RevisionId,
    string Reason,
    DateTimeOffset CreatedAt);

public enum CommitStatus
{
    Saved,
    RevisionConflict,
    StorageFailure,
    CapabilityUnavailable
}

/// <summary>Result of CommitAsync. On Saved the head advanced to the selected
/// revision. On conflict, ActualHead is the current durable head so the caller
/// can reconcile. A failed save never reports Saved and never advances the head.</summary>
public record CommitResult(
    CommitStatus Status,
    RevisionId? Head,
    string? ErrorCode = null,
    string? SafeMessage = null)
{
    public static CommitResult Saved(RevisionId head) => new(CommitStatus.Saved, head);
    public static CommitResult Conflict(RevisionId? actualHead)
        => new(CommitStatus.RevisionConflict, actualHead, "RevisionConflict",
            "Another writer changed the project head. Reopen to reconcile.");
    public static CommitResult Failure(string code, string message)
        => new(CommitStatus.StorageFailure, null, code, message);
    public static CommitResult Unavailable(string message)
        => new(CommitStatus.CapabilityUnavailable, null, "CapabilityUnavailable", message);
}

/// <summary>Recovery information surfaced when the durable head cannot be fully
/// verified. Damaged data is retained; recovery selects a complete checkpoint.</summary>
public record RecoveryReport(
    RevisionId DamagedHead,
    IReadOnlyList<HistoryCheckpoint> CompleteCheckpoints);

/// <summary>An immutable project revision plus decoded text content for its
/// files. IsReadOnly indicates the writer lock could not be acquired. Recovery
/// is non-null only when the head is damaged.</summary>
public record ProjectSnapshot(
    Project Project,
    ProjectRevision? Revision,
    IReadOnlyDictionary<FileId, string> Content,
    bool IsReadOnly,
    RecoveryReport? Recovery = null);

public record HistoryPage(
    IReadOnlyList<HistoryCheckpoint> Checkpoints,
    string? NextCursor = null);
