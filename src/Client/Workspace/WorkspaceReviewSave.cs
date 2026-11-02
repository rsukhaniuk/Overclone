using Client.Workspace.Domain;

namespace Client.Workspace;

/// <summary>A successful exact revision commit, bound to the session captured
/// before save I/O. Not a claim that later working edits are saved.</summary>
public sealed record WorkspaceSaveReceipt(Guid SessionId, ProjectRevision Revision);

public sealed partial class WorkspaceCoordinator
{
    public event Action<WorkspaceSaveReceipt>? RevisionCommitted;

    private void RaiseRevisionCommitted(Guid session, ProjectRevision revision)
    {
        if (RevisionCommitted is not { } handlers) return;
        var receipt = new WorkspaceSaveReceipt(session, revision);
        foreach (Action<WorkspaceSaveReceipt> handler in handlers.GetInvocationList())
            try { handler(receipt); } catch { /* a durable commit cannot become a failed save */ }
    }

    /// <summary>Review persistence/retry, outside the batch gate. Uses the same
    /// save queue and verifies the intended session and actual writer ownership
    /// after waits. Never saves whichever different project happens to be open.</summary>
    public async Task<CommitResult> SaveSessionAsync(ProjectId projectId, Guid session, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            bool Current() => Project?.Id == projectId && SessionId == session && CanEdit() && LockId is not null;
            if (!Current()) return CommitResult.Unavailable("The review session is no longer writable.");
            var id = LockId!;
            if (!await _locks.OwnsAsync(id, LockName(projectId), ct)
                || !Current() || LockId != id || !_locks.IsOwnedLocally(id, LockName(projectId)))
                return CommitResult.Unavailable("The review session no longer owns the writer lock.");
            ct.ThrowIfCancellationRequested();
            if (!IsDirty && CurrentRevision is null) return CommitResult.Unavailable("No durable revision exists.");
            return await SaveCoreAsync(ct);
        }
        finally { _gate.Release(); }
    }
}
