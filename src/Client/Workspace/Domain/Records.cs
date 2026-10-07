using System.Collections.Immutable;

namespace Client.Workspace.Domain;

/// <summary>A file manifest entry. Stable file ID is independent of the
/// normalized relative path; rename changes the path, not the ID. MediaKind is
/// <c>"text"</c> for valid-UTF-8 editable files or <c>"binary"</c> otherwise;
/// <see cref="HasBom"/> records a retained UTF-8 BOM for text files. Binary
/// files are never decoded for editing.</summary>
public record ProjectFile(
    FileId Id,
    string RelativePath,
    string MediaKind,
    SHA256 ContentHash,
    long ByteLength,
    bool HasBom = false);

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
/// and verifies these to OPFS before the IndexedDB manifest/head transaction.
/// <see cref="Bytes"/> is the exact content; the hash/length are validated to
/// match before writing.</summary>
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
/// text files and raw bytes for its binary files. IsReadOnly indicates the
/// writer lock could not be acquired. Recovery is non-null only when the head
/// is damaged. Binary files are keyed in <see cref="BinaryContent"/> and never
/// appear in <see cref="Content"/>; their BOM/newline bytes are preserved
/// exactly and never decoded for editing.</summary>
public record ProjectSnapshot(
    Project Project,
    ProjectRevision? Revision,
    IReadOnlyDictionary<FileId, string> Content,
    IReadOnlyDictionary<FileId, byte[]> BinaryContent,
    IReadOnlyDictionary<FileId, bool> TextHasBom,
    bool IsReadOnly,
    RecoveryReport? Recovery = null);

/// <summary>An arbitrary historical revision read from the repository, with its
/// objects verified. Used by history comparison and restore.</summary>
public record RevisionManifest(
    ProjectRevision Revision,
    bool Verified,
    string? FailureReason = null)
{
    public static RevisionManifest Ok(ProjectRevision r) => new(r, true);
    public static RevisionManifest Damaged(ProjectRevision r, string reason) => new(r, false, reason);
}

public record HistoryPage(
    IReadOnlyList<HistoryCheckpoint> Checkpoints,
    string? NextCursor = null);

/// <summary>A file proposed for a new imported project, before validation has
/// committed it to the repository. Bytes are exact; the hash is computed by the
/// caller and re-verified on publish.</summary>
public record StagedFile(
    string RelativePath,
    string MediaKind,
    bool HasBom,
    byte[] Bytes,
    SHA256 Hash,
    long ByteLength);

/// <summary>A fully validated staged project awaiting publication as a new
/// project. The repository publishes the project, its initial revision/head and
/// an import checkpoint atomically; a failed or cancelled import never appears
/// as an empty project in the list.</summary>
public record StagedProject(
    string Name,
    ImmutableArray<StagedFile> Files,
    IReadOnlyList<ValidationError> ValidationErrors);

/// <summary>A single validation error from import staging, bound to a path.</summary>
public record ValidationError(string RelativePath, string Code, string Message);

/// <summary>Result of publishing a staged import. On success, the new project is
/// immediately openable.</summary>
public record ImportPublishResult(
    bool Published,
    Project? Project,
    RevisionId? Head,
    string? ErrorCode = null,
    string? SafeMessage = null);

/// <summary>A capture token for a restore/transfer preview. It binds the
/// preview to a specific session, working-generation token and durable head so
/// any intervening edit, tree change, session switch or head advance invalidates
/// the preview. A durable revision ID alone cannot establish freshness.</summary>
public record PreviewToken(
    Guid SessionId,
    long WorkingGeneration,
    RevisionId? ExpectedHead,
    RevisionId CapturedRevision);

/// <summary>Kind of per-file change between two revisions.</summary>
public enum FileChangeKind
{
    Unchanged,
    Added,
    Removed,
    Renamed,
    ContentChanged,
    BinaryChanged,
}

/// <summary>A per-file comparison result. Renamed files share the same stable
/// FileId across revisions; content-changed files differ in bytes/hash. Text
/// files carry an optional bounded line diff; binary files report sizes/hashes.</summary>
public record FileChange(
    FileId? FileId,
    string Path,
    FileChangeKind Kind,
    string? OldPath,
    long OldLength,
    long NewLength,
    SHA256? OldHash,
    SHA256? NewHash,
    IReadOnlyList<DiffLine>? Diff,
    string? Note = null);

/// <summary>A single line of a bounded line-based diff.</summary>
public record DiffLine(DiffOp Op, string Text);

public enum DiffOp { Equal, Insert, Delete }

/// <summary>Summary of a revision comparison, with per-file changes and a flag
/// if detailed diff computation exceeded its budget and fell back to a summary.</summary>
public record ComparisonSummary(
    IReadOnlyList<FileChange> Changes,
    bool DiffBudgetExceeded,
    IReadOnlyList<string>? OversizedFiles = null);
