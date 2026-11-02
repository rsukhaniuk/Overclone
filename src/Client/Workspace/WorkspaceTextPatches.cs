using Client.Workspace.Domain;
using Client.Workspace.Patching;
using Client.Workspace.Transfer;
using Microsoft.JSInterop;

namespace Client.Workspace;

public sealed partial class WorkspaceCoordinator
{
    public TextPatchIdentity? CurrentTextPatchIdentity => Project is { } p
        ? new(p.Id, SessionId, WorkingGeneration, CurrentRevision?.Id) : null;

    /// <summary>Foundation only: no UI caller, auto-save, authorization or undo.
    /// Serializes with save/lifecycle on the existing gate. The optional 4b
    /// eligibility predicate must be synchronous and side-effect-free; it can
    /// check local report/selection epochs, never perform I/O or mutate state.
    /// All fallible preparation and callbacks precede the synchronous swap.</summary>
    public async Task<TextPatchApplication> ApplyTextBatchAsync(PreparedTextBatch batch, CancellationToken ct,
        Func<bool>? finalEligibility = null,
        Func<CancellationToken, Task<bool>>? finalAsyncEligibility = null,
        Action? committedBeforeNotification = null)
    {
        TextPatchApplication Fail(TextPatchError e) => new(false, PreparedTextBatch.FailureFor(e));
        if (batch is null) return Fail(TextPatchError.InvalidInput);
        TextPatchError? Check()
        {
            if (ct.IsCancellationRequested) return TextPatchError.Cancelled;
            if (CurrentTextPatchIdentity != batch.Identity || Project?.HeadRevisionId != batch.Identity.ExpectedHead) return TextPatchError.Stale;
            if (!CanEdit() || LockId is null || Capabilities?.WebLocks != true) return TextPatchError.ReadOnly;
            return null;
        }
        if (Check() is { } early) return Fail(early);
        bool atCommit = false;
        try { await _gate.WaitAsync(ct); }
        catch (OperationCanceledException) { return Fail(TextPatchError.Cancelled); }
        try
        {
            if (Check() is { } gated) return Fail(gated);
            var lockId = LockId!;
            if (!PatchProjectFits(batch)) return Fail(TextPatchError.QuotaExceeded);
            var targets = new List<(WorkingFile Live, PreparedTextFile Patch)>();
            foreach (var patch in batch.Files)
            {
                var live = Files.SingleOrDefault(f => f.Id == patch.FileId && !f.Deleted);
                if (live is null || live.MediaKind != "text" || live.RelativePath != patch.RelativePath) return Fail(TextPatchError.PreimageMismatch);
                // Frozen clone, not a live file used after an await. Pending exact
                // bytes are immutable; imported arrays are compared again below.
                var bytes = await GetFileBytesAsync(live.Clone(), ct);
                if (Check() is { } read) return Fail(read);
                if (bytes.LongLength > batch.Limits.FileBytes || !patch.Before.Span.SequenceEqual(bytes)) return Fail(TextPatchError.PreimageMismatch);
                targets.Add((live, patch));
            }
            // Query the actual browser writer lock as well as this instance's
            // lock-ID table. Another tab holding the name is not ownership.
            var owns = await _locks.OwnsAsync(lockId, LockName(batch.Identity.ProjectId), ct);
            if (Check() is { } ownership) return Fail(ownership);
            if (!owns) return Fail(TextPatchError.LockLost);
            var headOk = await _repo.VerifyHeadAsync(batch.Identity.ProjectId, batch.Identity.ExpectedHead, ct);
            if (Check() is { } head) return Fail(head);
            if (!headOk) return Fail(TextPatchError.RevisionConflict);
            // Head verification may await many object checks. Recheck browser
            // ownership too; a lost/stolen lease cannot authorize the swap.
            owns = await _locks.OwnsAsync(lockId, LockName(batch.Identity.ProjectId), ct);
            if (Check() is { } rechecked) return Fail(rechecked);
            if (!owns) return Fail(TextPatchError.LockLost);

            // Last asynchronous boundary: review verifies the authoritative
            // worker epoch here, never a LastKnownWorkerEpoch cache.
            if (finalAsyncEligibility is not null)
            {
                bool eligible;
                try { eligible = await finalAsyncEligibility(ct); }
                catch (OperationCanceledException) { return Fail(TextPatchError.Cancelled); }
                catch (Exception) { return Fail(TextPatchError.Ineligible); }
                if (Check() is { } asyncCheck) return Fail(asyncCheck);
                if (!eligible) return Fail(TextPatchError.Ineligible);
            }

            // The trusted review policy callback runs before the final local
            // checks, never between swaps. No exception text crosses this seam.
            try { if (finalEligibility?.Invoke() == false) return Fail(TextPatchError.Ineligible); }
            catch (Exception) { return Fail(TextPatchError.Ineligible); }
            if (Check() is { } final) return Fail(final);
            if (LockId != lockId || !_locks.IsOwnedLocally(lockId, LockName(batch.Identity.ProjectId))) return Fail(TextPatchError.LockLost);
            if (!PatchProjectFits(batch)) return Fail(TextPatchError.QuotaExceeded);
            foreach (var (live, patch) in targets)
                if (!Files.Contains(live) || !LivePatchPreimageMatches(live, patch)) return Fail(TextPatchError.PreimageMismatch);
            if (WorkingGeneration == long.MaxValue) return Fail(TextPatchError.QuotaExceeded);

            // Commit point. No await, callback, allocation, decoding, hashing or
            // notification between files. All observers see the complete batch.
            // Notification subscribers must obey the existing nonthrowing seam;
            // their bugs must not be translated into a false no-change result.
            atCommit = true;
            foreach (var (live, patch) in targets)
            {
                live.Content = patch.After.Content;
                live.HasBom = patch.After.HasBom;
                live.PendingTextBytes = patch.After;
                live.ImportedBytes = null;
                live.DirtyContent = true;
            }
            SavePhase = SavePhase.Dirty;
            LastError = null;
            // Trusted, nonthrowing local bookkeeping only. Recovery must exist
            // before synchronous observers invalidate the old analysis report.
            committedBeforeNotification?.Invoke();
            BumpGeneration();
            return new(true, null); // AppliedUnsaved, NOT persisted or authorized review
        }
        catch (OperationCanceledException) when (!atCommit) { return Fail(TextPatchError.Cancelled); }
        catch (Exception e) when (!atCommit && e is (IOException or UnauthorizedAccessException or JSException or WorkspaceException or FormatException or System.Text.Json.JsonException))
        { return Fail(Check() ?? TextPatchError.StorageFailure); }
        catch (Exception e) when (!atCommit && e is (ArgumentException or OverflowException))
        { return Fail(Check() ?? TextPatchError.InvalidInput); }
        finally { _gate.Release(); }
    }

    private bool PatchProjectFits(PreparedTextBatch batch)
    {
        var project = new List<PatchSourceInput>();
        foreach (var f in Files)
        {
            if (f.Deleted) continue;
            if (project.Count >= batch.Limits.FileCount) return false;
            if (!TryWorkingLength(f, out long length)) return false;
            project.Add(new(f.Id, f.RelativePath, f.MediaKind, length, f.HasBom));
        }
        if (!PreparedTextBatch.ValidProject(project, batch.Limits, out long total)) return false;
        foreach (var p in batch.Files)
        {
            var before = project.SingleOrDefault(f => f.FileId == p.FileId);
            if (before is null || before.MediaKind != "text" || before.RelativePath != p.RelativePath || before.ByteLength != p.Before.Length) return false;
            total = checked(total - before.ByteLength + p.After.Length);
        }
        return total <= batch.Limits.ProjectBytes;
    }

    private static bool TryWorkingLength(WorkingFile f, out long length)
    {
        if (f.MediaKind == "binary") { length = f.BinaryContent?.LongLength ?? f.DurableLength; return true; }
        if (f.PendingTextBytes is { } exact) { length = exact.Length; return true; }
        if (f.IsNew && !f.DirtyContent && f.ImportedBytes is { } imported) { length = imported.LongLength; return true; }
        if (!f.IsNew && !f.DirtyContent && f.DurableHash is not null) { length = f.DurableLength; return true; }
        return TextContent.TryCountEditedBytes(f.Content, f.HasBom, out length);
    }

    private static bool LivePatchPreimageMatches(WorkingFile f, PreparedTextFile patch)
    {
        if (f.Deleted || f.MediaKind != "text" || f.RelativePath != patch.RelativePath || f.HasBom != patch.Before.HasBom) return false;
        if (f.PendingTextBytes is { } pending) return f.Content == patch.Before.Content && pending.Span.SequenceEqual(patch.Before.Span);
        if (f.IsNew && !f.DirtyContent && f.ImportedBytes is { } imported) return f.Content == patch.Before.Content && patch.Before.Span.SequenceEqual(imported);
        if (!f.IsNew && !f.DirtyContent && f.DurableHash is { } durable)
            return f.Content == patch.Before.Content && durable == patch.Before.Hash && f.DurableLength == patch.Before.Length;
        // Final equality also defends against direct mutable-model changes that
        // did not increment generation. Size was checked without encoding above.
        return patch.Before.Span.SequenceEqual(TextContent.EncodeEdited(f.Content, f.HasBom));
    }
}
