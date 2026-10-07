using Client.Workspace.Domain;

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
    Task<CommitResult> CommitAsync(ProjectCommit batch, RevisionId? expectedHead, CancellationToken ct);
    Task<HistoryPage> ReadHistoryAsync(ProjectId id, HistoryCursor? cursor, CancellationToken ct);
    Task<Project> CreateProjectAsync(string name, CancellationToken ct);
    Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct);
    Task DeleteProjectAsync(ProjectId id, CancellationToken ct);
    Task<CommitResult> RepairHeadAsync(ProjectId id, RevisionId expectedCurrentHead, RevisionId newHead, CancellationToken ct);
}

/// <summary>Opaque history pagination cursor (M1a returns the full checkpoint
/// list; the cursor is reserved for future compaction).</summary>
public record HistoryCursor(string Value);
