using Client.Workspace.Patching;

namespace Client.Workspace;

public sealed partial class WorkspaceCoordinator
{
    // Nonmutating authority query, not patch admission or a replacement gate.
    public bool IsCurrentTextPatchWriter(TextPatchIdentity identity)
        => CurrentTextPatchIdentity == identity && Project?.HeadRevisionId == identity.ExpectedHead
            && CanEdit() && Capabilities?.WebLocks == true && LockId is { } id
            && _locks.IsOwnedLocally(id, LockName(identity.ProjectId));

    public async Task<bool> VerifyTextPatchPreviewAuthorityAsync(TextPatchIdentity identity, CancellationToken ct)
    {
        if (!IsCurrentTextPatchWriter(identity) || ct.IsCancellationRequested) return false;
        var id = LockId!;
        bool Current() => !ct.IsCancellationRequested && LockId == id && IsCurrentTextPatchWriter(identity);
        if (!await _locks.OwnsAsync(id, LockName(identity.ProjectId), ct) || !Current()) return false;
        if (!await _repo.VerifyHeadAsync(identity.ProjectId, identity.ExpectedHead, ct) || !Current()) return false;
        return await _locks.OwnsAsync(id, LockName(identity.ProjectId), ct) && Current();
    }
}
