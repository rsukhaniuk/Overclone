using Client.Workspace.Domain;
using Client.Workspace.Renaming;

namespace Client.Workspace;

/// <summary>
/// Local persistence and recovery for project revisions. Optimistic head
/// checking is enforced inside the writer lock; stale commits return
/// RevisionConflict. A failed save never advances the durable head. There is no
/// atomic transaction spanning OPFS content and IndexedDB manifests, so content
/// objects are written and verified before the manifest/head transaction runs.
/// </summary>
public interface IProjectRepository
{
    Task<ProjectSnapshot> OpenAsync(ProjectId id, CancellationToken ct);
    /// <summary>Verify the expected durable head and its referenced objects,
    /// without loading binary content into an editing snapshot. Unsupported
    /// adapters fail closed. Does not change the head or working set.</summary>
    Task<bool> VerifyHeadAsync(ProjectId id, RevisionId? expectedHead, CancellationToken ct) => Task.FromResult(false);
    Task<CommitResult> CommitAsync(ProjectCommit batch, RevisionId? expectedHead, CancellationToken ct);
    Task<HistoryPage> ReadHistoryAsync(ProjectId id, HistoryCursor? cursor, CancellationToken ct);
    Task<Project> CreateProjectAsync(string name, CancellationToken ct);
    Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct);
    Task DeleteProjectAsync(ProjectId id, CancellationToken ct);
    /// <summary>Name-only CAS under this acquired writer lock. Return success
    /// only after actual durable transaction completion. Old adapters fail closed.</summary>
    Task<ProjectRenameResult> RenameProjectAsync(ProjectRenameSnapshot expected, string normalizedName, string writerLockId, CancellationToken ct)
        => Task.FromResult(new ProjectRenameResult(ProjectRenameCode.Unsupported));
    Task<CommitResult> RepairHeadAsync(ProjectId id, RevisionId expectedCurrentHead, RevisionId newHead, CancellationToken ct);

    // ---- M1b: byte-aware reads, atomic import publish, restore ----

    /// <summary>Read a specified revision, validating that it belongs to the
    /// requested project and verifying its referenced content objects. Used by
    /// history comparison and restore. Does not change the durable head.</summary>
    Task<RevisionManifest> ReadRevisionAsync(ProjectId projectId, RevisionId revisionId, CancellationToken ct);

    /// <summary>Read the exact content bytes of a content object by hash. Used
    /// by export, comparison and binary display/download. Never decodes.</summary>
    Task<byte[]> ReadContentAsync(SHA256 hash, CancellationToken ct);

    /// <summary>Atomically create a project from a completely validated staged
    /// import: writes and verifies all content objects, then in one IndexedDB
    /// transaction stores the project, initial revision manifest, an import
    /// checkpoint and advances the head. A failed or cancelled import must not
    /// appear as an empty project in the project list. Unreferenced OPFS staging
    /// objects are allowed.</summary>
    Task<ImportPublishResult> PublishImportAsync(StagedProject staged, CancellationToken ct);

    /// <summary>Create a new head/checkpoint for restore using the current
    /// expected head. The new revision's parent is the current head and its file
    /// set/bytes/IDs match the selected historical revision. A restore reason
    /// references the historical revision. Old manifests remain intact.</summary>
    Task<CommitResult> CreateRestoreRevisionAsync(
        ProjectId projectId,
        RevisionId expectedHead,
        ProjectRevision restoredRevision,
        RevisionId sourceRevisionId,
        string reason,
        CancellationToken ct);
}

/// <summary>Opaque history pagination cursor. The value encodes the last item's
/// (timestamp, id) so the next page resumes after it with a stable tie-break.</summary>
public record HistoryCursor(string Value);
