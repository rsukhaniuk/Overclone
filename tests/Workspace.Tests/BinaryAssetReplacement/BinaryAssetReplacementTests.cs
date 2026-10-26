using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Client.Workspace.Imports;
using Client.Workspace.Transfer;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;
using static Workspace.Tests.BinaryAssetReplacement.ReplacementTestHost;

namespace Workspace.Tests.BinaryAssetReplacement;

public sealed class BinaryAssetReplacementTests
{
    private static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task<byte[]> ApplyB(ReplacementTestHost h, bool save = true)
    {
        byte[] bytes = [21, 0, 22, 255];
        await h.Stage(new ReadStream(bytes), "B.bin").WaitAsync(Bound);
        Assert.True(h.Controller.Confirm());
        if (save) Assert.Equal(CommitStatus.Saved, (await h.Workspace.SaveAsync(default)).Status);
        Assert.True(h.Controller.CanUndo); return bytes;
    }

    [Theory]
    [InlineData("negative-size")][InlineData("oversize")][InlineData("open")][InlineData("read")]
    [InlineData("dispose")][InlineData("hash")][InlineData("held-cancel")][InlineData("preview-cancel")]
    public void Nonmutating_new_stage_attempt_preserves_previous_successful_undo(string failure) => Run(async () =>
    {
        await using var h = await ReplacementTestHost.Open(); byte[] b = await ApplyB(h);
        string before = h.Work.State(); int opens = h.Opens;
        switch (failure)
        {
            case "negative-size":
            case "oversize":
                await h.Stage(new ReadStream([3]), size: failure == "negative-size" ? -1 : ArchiveLimits.SingleFile + 1).WaitAsync(Bound);
                Assert.Equal(opens, h.Opens); break;
            case "open":
                await h.Track(h.Controller.StageAsync("C.bin", 1, _ => throw new IOException("private open failure"))).WaitAsync(Bound); break;
            case "read": await h.Stage(new ReadStream([3]) { FailRead = true }).WaitAsync(Bound); break;
            case "dispose": await h.Stage(new ReadStream([3]) { FailDispose = true }).WaitAsync(Bound); break;
            case "hash":
                h.FailHashYield = true;
                await h.Stage(new ReadStream([3])).WaitAsync(Bound);
                h.FailHashYield = false; break;
            case "held-cancel":
                var stream = new ReadStream([3]) { HoldRead = true, HoldDispose = true };
                var pending = h.Stage(stream); await stream.ReadReached.Task.WaitAsync(Bound);
                h.Controller.Cancel(); Assert.True(h.Controller.Busy); Assert.False(h.Controller.CanUndo);
                stream.ReadRelease.TrySetResult(); await stream.DisposeReached.Task.WaitAsync(Bound);
                Assert.True(h.Controller.Busy); Assert.False(pending.IsCompleted);
                stream.DisposeRelease.TrySetResult(); await pending.WaitAsync(Bound); Assert.Equal(1, stream.Disposals); break;
            case "preview-cancel":
                await h.Stage(new ReadStream([3])).WaitAsync(Bound);
                Assert.True(h.Controller.CanConfirm); Assert.False(h.Controller.CanUndo);
                await h.Undo().WaitAsync(Bound); // Hidden/ineligible inverse cannot leave C staged after undoing B.
                Assert.True(h.Controller.CanConfirm); Assert.Equal(before, h.Work.State());
                h.Controller.Cancel(); break;
        }
        Assert.Equal(before, h.Work.State()); Assert.Equal(b, await h.Workspace.GetFileBytesAsync(h.Target, default));
        Assert.False(h.Controller.Busy); Assert.Null(h.Controller.Preview); Assert.True(h.Controller.CanUndo);
        await h.Undo().WaitAsync(Bound);
        Assert.Equal(h.Original, await h.Workspace.GetFileBytesAsync(h.Target, default)); Assert.False(h.Controller.CanUndo);
        Assert.Null(h.Controller.Preview); Assert.False(h.Controller.Confirm());
        string undone = h.Work.State(); await h.Undo().WaitAsync(Bound); Assert.Equal(undone, h.Work.State());
    });

    [Theory][InlineData(false)][InlineData(true)]
    public void Save_only_expires_new_stage_but_preserves_prior_undo(bool pendingRead) => Run(async () =>
    {
        await using var h = await ReplacementTestHost.Open(); byte[] b = await ApplyB(h, save: false);
        var stream = new ReadStream([4]) { HoldRead = pendingRead }; var pending = h.Stage(stream);
        if (pendingRead) await stream.ReadReached.Task.WaitAsync(Bound);
        else { await pending.WaitAsync(Bound); Assert.False(h.Controller.CanUndo); Assert.True(h.Controller.CanConfirm); }
        long generation = h.Workspace.WorkingGeneration;
        Assert.Equal(CommitStatus.Saved, (await h.Workspace.SaveAsync(default)).Status);
        // Same real saved B; future ordinary normalization may clear this buffer.
        h.Target.BinaryContent = null;
        Assert.Equal(generation, h.Workspace.WorkingGeneration); Assert.Null(h.Controller.Preview);
        if (pendingRead) Assert.True(h.Controller.Busy);
        string saved = h.Work.State(); stream.ReleaseAll(); await pending.WaitAsync(Bound);
        Assert.Equal(saved, h.Work.State()); Assert.Equal(b, await h.Workspace.GetFileBytesAsync(h.Target, default));
        Assert.False(h.Controller.CanConfirm); Assert.True(h.Controller.CanUndo);
        await h.Undo().WaitAsync(Bound); Assert.Equal(h.Original, await h.Workspace.GetFileBytesAsync(h.Target, default));
        Assert.False(h.Controller.CanUndo); Assert.Null(h.Controller.Preview);
    });

    [Fact] public void Successful_B_to_C_replaces_one_level_undo_with_B_not_A() => Run(async () =>
    {
        await using var h = await ReplacementTestHost.Open(); byte[] b = await ApplyB(h); byte[] c = [31, 0, 255];
        await h.Stage(new ReadStream(c), "C.bin").WaitAsync(Bound); Assert.False(h.Controller.CanUndo);
        Assert.True(h.Controller.Confirm()); Assert.True(h.Controller.CanUndo);
        Assert.Equal(c, await h.Workspace.GetFileBytesAsync(h.Target, default));
        await h.Undo().WaitAsync(Bound); Assert.Equal(b, await h.Workspace.GetFileBytesAsync(h.Target, default));
        Assert.False(h.Controller.CanUndo); Assert.Null(h.Controller.Preview); Assert.False(h.Controller.Confirm());
        string state = h.Work.State(); await h.Undo().WaitAsync(Bound); Assert.Equal(state, h.Work.State());
    });

    [Theory][InlineData("source")][InlineData("target-ABA")][InlineData("selection-ABA")][InlineData("session")][InlineData("lock")][InlineData("dispose")]
    public void New_stage_does_not_revive_prior_undo_after_real_authority_loss(string change) => Run(async () =>
    {
        await using var h = await ReplacementTestHost.Open(); byte[] b = await ApplyB(h);
        await h.Stage(new ReadStream([7])).WaitAsync(Bound);
        switch (change)
        {
            case "source": h.Workspace.EditText(h.Work.Main, "later unrelated change"); break;
            case "target-ABA": h.Workspace.ReplaceFileBytes(h.TargetId, [9]); h.Workspace.ReplaceFileBytes(h.TargetId, b.ToArray()); break;
            case "selection-ABA": h.Workspace.SelectFile(h.OtherId); h.Controller.SetTarget(h.Workspace.ActiveFile); h.Workspace.SelectFile(h.TargetId); h.Controller.SetTarget(h.Target); break;
            case "session": var id = h.Workspace.Project!.Id; await h.Workspace.CloseAsync(default); await h.Workspace.OpenAsync(id, default); h.Workspace.SelectFile(h.TargetId); h.Controller.SetTarget(h.Target); break;
            case "lock": h.Work.Lock.AllowAcquire = false; h.Controller.Cancel(); h.Work.Lock.AllowAcquire = true; break;
            case "dispose": h.Mount(); break;
        }
        h.Controller.Cancel(); string before = h.Work.State();
        Assert.False(h.Controller.CanUndo); await h.Undo().WaitAsync(Bound); Assert.Equal(before, h.Work.State());
    });

    [Fact] public void Preview_is_plain_metadata_and_confirm_undo_keep_nested_identity_and_exact_bytes() => Run(async () =>
    {
        await using var h = await ReplacementTestHost.Open();
        byte[] next = [0, 255, 7, 128, 13, 10]; string before = h.Work.State();
        int commits = h.Work.Repo.Commits; long generation = h.Workspace.WorkingGeneration;
        var path = h.Target.RelativePath; var other = h.Workspace.Files.Where(f => f.Id != h.TargetId).Select(f => (f.Id, f.Content, f.BinaryContent)).ToArray();
        var stream = new ReadStream(next);
        await h.Stage(stream, "<different-name>.tex").WaitAsync(Bound);
        Assert.Equal(before, h.Work.State()); Assert.Equal(commits, h.Work.Repo.Commits);
        var preview = Assert.IsType<BinaryReplacementPreview>(h.Controller.Preview);
        Assert.Equal(h.TargetId, preview.TargetFileId); Assert.Equal(path, preview.TargetPath);
        Assert.Equal("<different-name>.tex", preview.PickedFilename);
        Assert.Equal(h.Original.LongLength, preview.OldBytes); Assert.Equal(next.LongLength, preview.NewBytes);
        Assert.Equal(Hash(h.Original), preview.OldSha256); Assert.Equal(Hash(next), preview.NewSha256);
        Assert.Equal(1, stream.Disposals); Assert.Null(stream.ReadContext); Assert.Null(stream.DisposeContext);
        Assert.True(h.Controller.CanConfirm); Assert.True(h.Controller.Confirm()); Assert.False(h.Controller.Confirm());
        Assert.Equal(BinaryReplacementState.Applied, h.Controller.State); Assert.True(h.Controller.CanUndo);
        Assert.Equal(next, await h.Workspace.GetFileBytesAsync(h.Target, default)); Assert.Equal(generation + 1, h.Workspace.WorkingGeneration);
        Assert.Equal(path, h.Target.RelativePath); Assert.Equal("binary", h.Target.MediaKind); Assert.Equal(h.TargetId, h.Workspace.ActiveFileId);
        Assert.Equal(other, h.Workspace.Files.Where(f => f.Id != h.TargetId).Select(f => (f.Id, f.Content, f.BinaryContent)).ToArray());
        await h.Undo().WaitAsync(Bound);
        Assert.Equal(BinaryReplacementState.Undone, h.Controller.State); Assert.False(h.Controller.CanUndo);
        Assert.Equal(h.Original, await h.Workspace.GetFileBytesAsync(h.Target, default)); Assert.Equal(generation + 2, h.Workspace.WorkingGeneration);
        string undone = h.Work.State(); await h.Undo().WaitAsync(Bound); Assert.Equal(undone, h.Work.State());
        Assert.Equal(commits, h.Work.Repo.Commits);
    });

    [Theory][InlineData(false)][InlineData(true)]
    public void Save_reopen_export_old_history_and_save_only_undo(bool failSave) => Run(async () =>
    {
        await using var h = await ReplacementTestHost.Open();
        var old = h.Workspace.CurrentRevision!; byte[] next = [255, 1, 2, 0];
        await h.Stage(new ReadStream(next)).WaitAsync(Bound); Assert.True(h.Controller.Confirm());
        h.Work.Repo.FailSave = failSave;
        var saved = await h.Workspace.SaveAsync(default);
        Assert.Equal(!failSave, saved.Status == CommitStatus.Saved);
        Assert.True(h.Controller.CanUndo); Assert.Equal(next, await h.Workspace.GetFileBytesAsync(h.Target, default));
        Assert.Equal(h.Original, h.Work.Repo.Content[old.FindFile(h.TargetId)!.ContentHash]);
        Assert.Equal(old, h.Work.Repo.Revisions[old.Id]);
        h.Work.Repo.FailSave = false;
        Assert.Equal(CommitStatus.Saved, (await h.Workspace.SaveAsync(default)).Status);
        // Simulate future ordinary save dropping its transient buffer, not a
        // workspace mutation: actual effective bytes remain the saved hash.
        h.Target.BinaryContent = null;
        Assert.True(h.Controller.CanUndo);
        await h.Undo().WaitAsync(Bound); Assert.Equal(h.Original, await h.Workspace.GetFileBytesAsync(h.Target, default));
        Assert.Equal(CommitStatus.Saved, (await h.Workspace.SaveAsync(default)).Status);
        var export = await h.Workspace.CaptureExportSourceAsync(default); var zip = new ZipTransferService(h.Work.Repo);
        var archive = await zip.BuildZipAsync(export, default); var stage = await zip.StageZipImportAsync(archive.Bytes, "copy", default);
        Assert.True(stage.Valid); Assert.Equal(h.Original, stage.Staged.Files.Single(f => f.RelativePath == h.Target.RelativePath).Bytes);
        var project = h.Workspace.Project!.Id; await h.Workspace.CloseAsync(default); await h.Workspace.OpenAsync(project, default);
        h.Controller.SetTarget(h.Target); h.Workspace.SelectFile(h.TargetId);
        Assert.False(h.Controller.CanUndo); Assert.Equal(h.Original, await h.Workspace.GetFileBytesAsync(h.Target, default));
    });

    [Fact] public void Confirmed_new_bytes_are_saved_reopened_and_exported_without_renaming() => Run(async () =>
    {
        await using var h = await ReplacementTestHost.Open(); byte[] bytes = [3, 0, 9, 255];
        await h.Stage(new ReadStream(bytes), "other-local.pdf").WaitAsync(Bound); Assert.True(h.Controller.Confirm());
        Assert.Equal(CommitStatus.Saved, (await h.Workspace.SaveAsync(default)).Status);
        var zip = new ZipTransferService(h.Work.Repo);
        var archive = await zip.BuildZipAsync(await h.Workspace.CaptureExportSourceAsync(default), default);
        var stage = await zip.StageZipImportAsync(archive.Bytes, "new", default);
        Assert.Equal(bytes, stage.Staged.Files.Single(f => f.RelativePath == "figures/original.bin").Bytes);
        var project = h.Workspace.Project!.Id; await h.Workspace.CloseAsync(default); await h.Workspace.OpenAsync(project, default);
        Assert.Equal(bytes, await h.Workspace.GetFileBytesAsync(h.Target, default)); Assert.Equal("figures/original.bin", h.Target.RelativePath);
        Assert.False(h.Controller.CanUndo);
    });

    [Theory]
    [InlineData("source")][InlineData("rename")][InlineData("delete")][InlineData("replacement-ABA")]
    [InlineData("selection-ABA")][InlineData("same-object-parameters")][InlineData("save")][InlineData("session-ABA")][InlineData("project-ABA")][InlineData("lock")]
    public void Conservative_preview_cannot_be_confirmed_after_intervening_change(string change) => Run(async () =>
    {
        await using var h = await ReplacementTestHost.Open();
        if (change == "save") Assert.True(h.Workspace.ReplaceFileBytes(h.TargetId, h.Original.ToArray()));
        await h.Stage(new ReadStream([9])).WaitAsync(Bound);
        switch (change)
        {
            case "source": h.Workspace.EditText(h.Work.Main, "later text"); break;
            case "rename": h.Workspace.RenameFile(h.TargetId, "moved.bin"); break;
            case "delete": h.Workspace.DeleteFile(h.TargetId); break;
            case "replacement-ABA": h.Workspace.ReplaceFileBytes(h.TargetId, [4]); h.Workspace.ReplaceFileBytes(h.TargetId, h.Original.ToArray()); break;
            case "selection-ABA":
                h.Workspace.SelectFile(h.OtherId); h.Controller.SetTarget(h.Workspace.ActiveFile);
                h.Workspace.SelectFile(h.TargetId); h.Controller.SetTarget(h.Target); break;
            case "same-object-parameters":
                h.Controller.SetTarget(h.Workspace.Files.Single(f => f.Id == h.OtherId)); h.Controller.SetTarget(h.Target); break;
            case "save":
                // Real dirty bytes were present BEFORE stage; save alone now
                // changes its head without another generation mutation.
                await h.Workspace.SaveAsync(default); break;
            case "session-ABA":
                var a = h.Workspace.Project!.Id; await h.Workspace.CloseAsync(default); await h.Workspace.OpenAsync(a, default); break;
            case "project-ABA":
                var project = h.Workspace.Project!.Id; await h.Workspace.CloseAsync(default);
                var b = new Project(ProjectId.New(), "B", null); h.Work.Repo.Projects[b.Id] = (b, null);
                await h.Workspace.OpenAsync(b.Id, default); await h.Workspace.CloseAsync(default); await h.Workspace.OpenAsync(project, default); break;
            case "lock": h.Work.Lock.AllowAcquire = false; break;
        }
        string before = h.Work.State(); Assert.False(h.Controller.CanConfirm); Assert.False(h.Controller.Confirm());
        Assert.Equal(before, h.Work.State()); Assert.Null(h.Controller.Preview);
    });

    [Theory][InlineData("source")][InlineData("target")][InlineData("rename")][InlineData("selection")][InlineData("unmount")][InlineData("session")]
    public void Undo_never_rolls_back_later_changes(string change) => Run(async () =>
    {
        await using var h = await ReplacementTestHost.Open(); await h.Stage(new ReadStream([8, 7])).WaitAsync(Bound); Assert.True(h.Controller.Confirm());
        switch (change)
        {
            case "source": h.Workspace.EditText(h.Work.Main, "keep unrelated edit"); break;
            case "target": h.Workspace.ReplaceFileBytes(h.TargetId, [3]); break;
            case "rename": h.Workspace.RenameFile(h.TargetId, "renamed.bin"); break;
            case "selection": h.Workspace.SelectFile(h.OtherId); h.Controller.SetTarget(h.Workspace.ActiveFile); h.Workspace.SelectFile(h.TargetId); h.Controller.SetTarget(h.Target); break;
            case "unmount": h.Mount(); break;
            case "session": var id = h.Workspace.Project!.Id; await h.Workspace.CloseAsync(default); await h.Workspace.OpenAsync(id, default); h.Controller.SetTarget(h.Target); break;
        }
        string before = h.Work.State(); Assert.False(h.Controller.CanUndo); await h.Undo().WaitAsync(Bound); Assert.Equal(before, h.Work.State());
    });

    [Fact] public void Cancel_preview_never_mutates_and_new_invalid_pick_cannot_confirm_old_preview() => Run(async () =>
    {
        await using var h = await ReplacementTestHost.Open(); string before = h.Work.State();
        await h.Stage(new ReadStream([5])).WaitAsync(Bound); h.Controller.Cancel();
        Assert.Null(h.Controller.Preview); Assert.False(h.Controller.Confirm()); Assert.Equal(before, h.Work.State());
        await h.Stage(new ReadStream([6])).WaitAsync(Bound);
        await h.Stage(new ReadStream([7]), size: ArchiveLimits.SingleFile + 1).WaitAsync(Bound);
        Assert.False(h.Controller.Confirm()); Assert.Null(h.Controller.Preview); Assert.Equal(before, h.Work.State());
    });

    [Theory][InlineData("old")][InlineData("hash")][InlineData("new")][InlineData("dispose")]
    public void Cancel_or_unmount_holds_shared_slot_until_every_actual_stage_settles(string phase) => Run(async () =>
    {
        await using var h = await ReplacementTestHost.Open();
        CompilationTestWorkspace.ReadBarrier? old = phase == "old" ? h.Work.Hold(h.TargetId) : null;
        if (phase == "hash") h.HashHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new ReadStream([9]) { HoldRead = phase == "new", HoldDispose = phase == "dispose" };
        var pending = h.Stage(stream);
        switch (phase)
        {
            case "old": await old!.WaitReached(); break;
            case "hash": await h.HashReached.Task.WaitAsync(Bound); break;
            case "new": await stream.ReadReached.Task.WaitAsync(Bound); break;
            case "dispose": await stream.DisposeReached.Task.WaitAsync(Bound); break;
        }
        h.Controller.Cancel(); Assert.True(h.Controller.Busy); Assert.False(pending.IsCompleted);
        int opens = h.Opens; await h.Import(new ReadStream([1])).WaitAsync(Bound); Assert.Equal(opens, h.Opens);
        string before = h.Work.State();
        h.HashHold?.TrySetResult();
        if (old is not null) old.Release.TrySetResult(h.Work.Repo.Content[old.Hash]);
        stream.ReleaseAll();
        await pending.WaitAsync(Bound); Assert.Equal(before, h.Work.State()); Assert.False(h.Controller.Busy);
        Assert.Equal(BinaryReplacementState.Cancelled, h.Controller.State); Assert.False(h.Controller.CanConfirm);
        if (opens != 0) Assert.Equal(1, stream.Disposals);
    });

    [Fact] public void New_byte_hash_is_real_pending_work_even_after_stream_disposal() => Run(async () =>
    {
        await using var h = await ReplacementTestHost.Open(); var stream = new ReadStream([5]) { HoldDispose = true };
        var pending = h.Stage(stream); await stream.DisposeReached.Task.WaitAsync(Bound);
        h.HashHold = new(TaskCreationOptions.RunContinuationsAsynchronously); h.NextHashReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        stream.DisposeRelease.TrySetResult(); await h.NextHashReached.Task.WaitAsync(Bound);
        h.Controller.Cancel(); int opens = h.Opens; Assert.True(h.Controller.Busy); Assert.False(pending.IsCompleted);
        await h.Import(new ReadStream([7])).WaitAsync(Bound); Assert.Equal(opens, h.Opens);
        string before = h.Work.State(); h.HashHold.TrySetResult(); await pending.WaitAsync(Bound);
        Assert.Equal(before, h.Work.State()); Assert.False(h.Controller.Busy); Assert.False(h.Controller.CanConfirm);
        Assert.Equal(1, stream.Disposals);
    });

    [Theory][InlineData(false)][InlineData(true)]
    public void Additive_and_replacement_share_admission_in_both_directions_and_across_remount(bool additiveFirst) => Run(async () =>
    {
        await using var h = await ReplacementTestHost.Open();
        var held = new ReadStream([5]) { HoldRead = true, HoldDispose = true };
        var pending = additiveFirst ? h.Import(held) : h.Stage(held);
        await held.ReadReached.Task.WaitAsync(Bound);
        if (additiveFirst) await h.Stage(new ReadStream([1])).WaitAsync(Bound); else await h.Import(new ReadStream([1])).WaitAsync(Bound);
        Assert.Equal(1, h.Opens);
        h.Mount(); Assert.True(h.Controller.Busy); Assert.True(h.Additive.Busy);
        await h.Stage(new ReadStream([2])).WaitAsync(Bound); await h.Import(new ReadStream([3])).WaitAsync(Bound); Assert.Equal(1, h.Opens);
        held.ReadRelease.TrySetResult(); await held.DisposeReached.Task.WaitAsync(Bound); Assert.True(h.Controller.Busy);
        await h.Stage(new ReadStream([4])).WaitAsync(Bound); Assert.Equal(1, h.Opens);
        held.DisposeRelease.TrySetResult(); await pending.WaitAsync(Bound); await h.Drain();
        Assert.False(h.Controller.Busy); Assert.DoesNotContain(h.Workspace.Files, f => f.RelativePath == "addition.bin");
        await h.Stage(new ReadStream([6])).WaitAsync(Bound); Assert.Equal(2, h.Opens); Assert.True(h.Controller.CanConfirm);
    });

    [Theory][InlineData("read")][InlineData("dispose")][InlineData("open")][InlineData("old-read")][InlineData("old-hash")]
    public void IO_failure_keeps_bytes_and_has_safe_status(string phase) => Run(async () =>
    {
        await using var h = await ReplacementTestHost.Open(); string before = h.Work.State();
        if (phase == "old-read") h.Work.Repo.ReadOverride = _ => throw new IOException("private old error");
        if (phase == "old-hash") h.Work.Repo.ReadOverride = _ => [4, 3, 2];
        if (phase == "open") await h.Track(h.Controller.StageAsync("name", 1, _ => throw new IOException("secret"))).WaitAsync(Bound);
        else await h.Stage(new ReadStream([3]) { FailRead = phase == "read", FailDispose = phase == "dispose" }).WaitAsync(Bound);
        Assert.Equal(BinaryReplacementState.Failed, h.Controller.State); Assert.False(h.Controller.CanConfirm); Assert.Equal(before, h.Work.State());
        Assert.DoesNotContain("secret", h.Controller.Notice); Assert.DoesNotContain("private", h.Controller.Notice);
        Assert.False(h.Controller.Busy); h.Work.Repo.ReadOverride = null;
    });

    [Theory][InlineData(0)][InlineData(33554432)][InlineData(33554433)]
    public void Actual_byte_limit_not_lying_metadata_controls_preview(long size) => Run(async () =>
    {
        await using var h = await ReplacementTestHost.Open(); string before = h.Work.State(); var stream = new ReadStream(size);
        await h.Stage(stream, size: 0).WaitAsync(Bound);
        Assert.InRange(stream.Consumed, 0, ArchiveLimits.SingleFile + 1); Assert.InRange(stream.MaxRequest, 1, 64 * 1024);
        Assert.Equal(1, stream.Disposals); Assert.Equal(before, h.Work.State());
        Assert.Equal(size <= ArchiveLimits.SingleFile, h.Controller.CanConfirm);
        if (size <= ArchiveLimits.SingleFile) Assert.Equal(size, h.Controller.Preview!.NewBytes);
    });

    [Theory][InlineData(-1)][InlineData(33554433)]
    public void Advertised_out_of_bound_opens_nothing(long size) => Run(async () =>
    {
        await using var h = await ReplacementTestHost.Open(); int reads = h.Work.Repo.Reads;
        await h.Stage(new ReadStream([1]), size: size).WaitAsync(Bound);
        Assert.Equal(0, h.Opens); Assert.Equal(reads, h.Work.Repo.Reads); Assert.False(h.Controller.CanConfirm);
    });

    [Fact] public void Reentrant_later_mutation_is_not_rolled_back_or_reported_as_no_change() => Run(async () =>
    {
        await using var h = await ReplacementTestHost.Open(); byte[] bytes = [9, 8, 7]; await h.Stage(new ReadStream(bytes)).WaitAsync(Bound);
        bool edited = false;
        void Later()
        {
            if (edited || !h.Target.DirtyBinary) return;
            edited = true; h.Workspace.EditText(h.Work.Main, "later unrelated source"); h.Workspace.SelectFile(h.OtherId);
        }
        h.Workspace.WorkspaceChanged += Later;
        try
        {
            Assert.True(h.Controller.Confirm()); Assert.Equal(BinaryReplacementState.Applied, h.Controller.State); Assert.False(h.Controller.CanUndo);
            Assert.Equal(bytes, await h.Workspace.GetFileBytesAsync(h.Target, default)); Assert.Equal(h.OtherId, h.Workspace.ActiveFileId);
            Assert.Equal("later unrelated source", h.Workspace.Files.Single(f => f.Id == h.Work.Main).Content);
        }
        finally { h.Workspace.WorkspaceChanged -= Later; }
    });

    [Theory][InlineData("source")][InlineData("lock")][InlineData("selection")][InlineData("session")]
    public void Held_new_read_is_discarded_after_authority_change(string change) => Run(async () =>
    {
        await using var h = await ReplacementTestHost.Open(); var stream = new ReadStream([2]) { HoldRead = true };
        var pending = h.Stage(stream); await stream.ReadReached.Task.WaitAsync(Bound);
        switch (change)
        {
            case "source": h.Workspace.EditText(h.Work.Main, "retain later source"); break;
            case "lock": h.Work.Lock.AllowAcquire = false; break;
            case "selection": h.Workspace.SelectFile(h.OtherId); h.Controller.SetTarget(h.Workspace.ActiveFile); break;
            case "session": var project = h.Workspace.Project!.Id; await h.Workspace.CloseAsync(default); await h.Workspace.OpenAsync(project, default); break;
        }
        string before = h.Work.State(); stream.ReleaseAll(); await pending.WaitAsync(Bound);
        Assert.Equal(before, h.Work.State()); Assert.Null(h.Controller.Preview); Assert.False(h.Controller.Confirm());
        Assert.Equal(1, stream.Disposals); Assert.False(h.Controller.Busy);
    });

    [Fact] public void Pending_undo_checks_actual_effective_hash_and_cannot_rollback_later_source() => Run(async () =>
    {
        await using var h = await ReplacementTestHost.Open(); await h.Stage(new ReadStream([7])).WaitAsync(Bound); Assert.True(h.Controller.Confirm());
        await h.Workspace.SaveAsync(default);
        var hold = h.Work.Hold(h.TargetId); var pending = h.Undo(); await hold.WaitReached();
        h.Workspace.EditText(h.Work.Main, "later text must survive"); string before = h.Work.State();
        hold.Release.TrySetResult(h.Work.Repo.Content[hold.Hash]); await pending.WaitAsync(Bound);
        Assert.Equal(before, h.Work.State()); Assert.False(h.Controller.CanUndo); Assert.Equal(BinaryReplacementState.Stale, h.Controller.State);
    });

    [Fact] public void Undo_refuses_actual_hash_mismatch_without_changing_the_working_model() => Run(async () =>
    {
        await using var h = await ReplacementTestHost.Open(); await h.Stage(new ReadStream([9])).WaitAsync(Bound); Assert.True(h.Controller.Confirm());
        await h.Workspace.SaveAsync(default); h.Work.Repo.ReadOverride = _ => [4, 3]; string before = h.Work.State();
        await h.Undo().WaitAsync(Bound);
        Assert.Equal(before, h.Work.State()); Assert.False(h.Controller.CanUndo); Assert.Equal(BinaryReplacementState.Stale, h.Controller.State);
        h.Work.Repo.ReadOverride = null;
        int reads = h.Work.Repo.Reads; await h.Undo().WaitAsync(Bound);
        Assert.Equal(reads, h.Work.Repo.Reads); Assert.Equal(before, h.Work.State()); // Restoring storage does not revive the consumed inverse.
    });

    [Fact] public void New_stage_observing_actual_old_target_mismatch_consumes_prior_inverse() => Run(async () =>
    {
        await using var h = await ReplacementTestHost.Open(); await ApplyB(h);
        string before = h.Work.State(); int opens = h.Opens;
        h.Work.Repo.ReadOverride = _ => [4, 3]; // Actual saved B disagrees with captured durable identity.
        await h.Stage(new ReadStream([8])).WaitAsync(Bound);
        Assert.Equal(BinaryReplacementState.Failed, h.Controller.State); Assert.Equal(before, h.Work.State());
        Assert.Equal(opens, h.Opens); Assert.False(h.Controller.CanUndo); Assert.False(h.Controller.CanConfirm);
        h.Work.Repo.ReadOverride = null;
        int reads = h.Work.Repo.Reads; await h.Undo().WaitAsync(Bound);
        Assert.Equal(reads, h.Work.Repo.Reads); Assert.Equal(before, h.Work.State());
    });

    [Fact] public void Reentrant_undo_mutation_remains_real_without_reselecting_or_reversing_unrelated_edit() => Run(async () =>
    {
        await using var h = await ReplacementTestHost.Open(); await h.Stage(new ReadStream([9])).WaitAsync(Bound); Assert.True(h.Controller.Confirm());
        bool edited = false;
        void Later()
        {
            if (edited || !h.Target.BinaryContent!.SequenceEqual(h.Original)) return;
            edited = true; h.Workspace.EditText(h.Work.Main, "later during undo"); h.Workspace.SelectFile(h.OtherId);
        }
        h.Workspace.WorkspaceChanged += Later;
        try
        {
            await h.Undo().WaitAsync(Bound);
            Assert.Equal(BinaryReplacementState.Undone, h.Controller.State); Assert.False(h.Controller.CanUndo);
            Assert.Equal(h.Original, await h.Workspace.GetFileBytesAsync(h.Target, default)); Assert.Equal(h.OtherId, h.Workspace.ActiveFileId);
            Assert.Equal("later during undo", h.Workspace.Files.Single(f => f.Id == h.Work.Main).Content);
        }
        finally { h.Workspace.WorkspaceChanged -= Later; }
    });

    [Fact] public void Disposed_view_never_receives_late_publication() => Run(async () =>
    {
        await using var h = await ReplacementTestHost.Open(); var stream = new ReadStream([1]) { HoldRead = true };
        var pending = h.Stage(stream); await stream.ReadReached.Task.WaitAsync(Bound);
        h.ViewDisposed = true; h.Controller.Dispose(); await h.Drain(); int paints = h.Paints, notifications = h.Notifications;
        string before = h.Work.State(); stream.ReleaseAll(); await pending.WaitAsync(Bound); await h.Drain();
        Assert.Equal(before, h.Work.State()); Assert.Equal(paints, h.Paints); Assert.Equal(notifications, h.Notifications);
        Assert.Equal(1, stream.Disposals); Assert.False(h.Controller.Busy); Assert.Equal(BinaryReplacementState.Disposed, h.Controller.State);
    });

    [Theory][InlineData(-1)][InlineData(int.MaxValue)]
    public void Invalid_read_counts_refuse_before_copy(int count) => Run(async () =>
    {
        await using var h = await ReplacementTestHost.Open(); string before = h.Work.State();
        var stream = new ReadStream([1]) { InvalidCount = count }; await h.Stage(stream).WaitAsync(Bound);
        Assert.Equal(before, h.Work.State()); Assert.Equal(BinaryReplacementState.Failed, h.Controller.State); Assert.Equal(1, stream.Disposals);
    });

    [Fact] public void Text_target_is_never_opened_or_retyped() => Run(async () =>
    {
        await using var h = await ReplacementTestHost.Open(); h.Workspace.SelectFile(h.Work.Main); h.Controller.SetTarget(h.Workspace.ActiveFile);
        string before = h.Work.State(); int reads = h.Work.Repo.Reads;
        await h.Stage(new ReadStream([1])).WaitAsync(Bound);
        Assert.Equal(before, h.Work.State()); Assert.Equal(reads, h.Work.Repo.Reads); Assert.Equal(0, h.Opens); Assert.False(h.Controller.Confirm());
    });

    [Fact] public void Recovery_and_refused_writer_never_read_or_modify_target() => Run(async () =>
    {
        await using (var h = await ReplacementTestHost.Open())
        {
            h.Work.Lock.AllowAcquire = false; string before = h.Work.State();
            await h.Stage(new ReadStream([1])).WaitAsync(Bound); Assert.Equal(0, h.Opens); Assert.Equal(before, h.Work.State());
        }
        var project = ProjectId.New(); var damaged = RevisionId.New(); var recovery = new RecoveryReport(damaged, []);
        var fileId = FileId.New(); byte[] retained = [3, 0];
        var file = new ProjectFile(fileId, "retained.bin", "binary", CompilationTestWorkspace.Hash(retained), retained.Length, false);
        var revision = new ProjectRevision(RevisionId.New(), project, null, [file], DateTimeOffset.UtcNow);
        var snapshot = new ProjectSnapshot(new Project(project, "recovery", damaged), revision, new Dictionary<FileId, string>(),
            new Dictionary<FileId, byte[]> { [fileId] = retained }, new Dictionary<FileId, bool>(), false, recovery);
        var repo = new RecoveryRepository(snapshot); var locks = new CompilationTestWorkspace.Locks();
        await using var workspace = new WorkspaceCoordinator(repo, locks, new HistoryService(repo)); await workspace.OpenAsync(project, default);
        using var controller = new BinaryAssetReplacementController(workspace, locks, action => { action(); return Task.CompletedTask; });
        controller.SetTarget(workspace.ActiveFile);
        int opens = 0; await controller.StageAsync("file", 1, _ => { opens++; return new ValueTask<Stream>(new MemoryStream([1])); }).WaitAsync(Bound);
        Assert.Equal(0, opens); Assert.Equal(0, repo.Reads); Assert.Same(recovery, workspace.Recovery);
        Assert.Single(workspace.Files); Assert.Equal(retained, workspace.ActiveFile!.BinaryContent); Assert.False(controller.Confirm());
    });
}
