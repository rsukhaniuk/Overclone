using System.Text;
using Client.Review;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Client.Workspace.Patching;
using Xunit;

namespace Workspace.Tests.HistoryComparison;

public sealed class TextRestoreTests
{
    [Fact]
    public async Task Explicit_review_accept_save_and_ordinary_review_Undo_preserve_exact_bytes()
    {
        await using var h = await HistoryTestHost.Open();
        Assert.True(h.Workspace.EditText(h.Main, "dirty🙂 e\u0301\nworking\n"));
        byte[] before = (await h.Bytes(h.Main)).ToArray(); var head = h.Workspace.CurrentRevision!.Id;
        long generation = h.Workspace.WorkingGeneration; int commits = h.Io.Commits, changed = 0;
        h.Workspace.WorkspaceChanged += () => changed++;
        var proposal = await h.Proposal(h.Main);
        Assert.Equal(TextPatchOrigin.User, proposal.Exact.Origin);
        Assert.Equal(before, proposal.Exact.Files.Single().Before.ToArray());
        Assert.Equal(h.Original, proposal.Exact.Files.Single().After.ToArray());
        Assert.Equal(3, proposal.Exact.Files.Single().Edits.Single().StartByte); // BOM is not editable
        var preview = await proposal.PreviewAsync(h.Review, default);
        Assert.Equal(HistoryComparisonCode.Ready, preview.Code);
        Assert.Equal(before, await h.Bytes(h.Main)); Assert.Equal(commits, h.Io.Commits); Assert.Equal(0, changed);
        var accepted = await h.Review.AcceptAsync(preview.Review!.Preview!.Id, default);
        Assert.True(accepted.Changed); Assert.Equal(ReviewStatus.AppliedSaved, accepted.Status);
        Assert.Equal(h.Original, await h.Bytes(h.Main)); Assert.Equal(head, h.Workspace.CurrentRevision!.ParentRevisionId);
        Assert.Equal(generation + 1, h.Workspace.WorkingGeneration); Assert.Equal(1, changed); // existing external-patch notification/journal invalidation seam
        Assert.Equal(h.OtherBytes, await h.Bytes(h.Other)); Assert.Equal(h.BinaryBytes, await h.Bytes(h.Binary));
        var undo = await h.Review.UndoAsync(accepted.ActionId!.Value, default);
        Assert.Equal(ReviewStatus.UndoSaved, undo.Status); Assert.Equal(before, await h.Bytes(h.Main));
        Assert.Equal(h.OtherBytes, await h.Bytes(h.Other)); Assert.Equal(h.BinaryBytes, await h.Bytes(h.Binary));
        Assert.Equal(generation + 2, h.Workspace.WorkingGeneration);
    }

    [Fact]
    public async Task Each_selected_subset_is_freshly_prepared_and_leaves_other_dirty_text_untouched()
    {
        await using var h = await HistoryTestHost.Open();
        Assert.True(h.Workspace.EditText(h.Other, "dirty other\n")); byte[] other = (await h.Bytes(h.Other)).ToArray();
        await h.Compare(); var first = await HistoryTextRestoreProposal.CreateAsync(h.Session, [h.Main, h.Other], default);
        Assert.Equal(2, first.Proposal!.Exact.Files.Length);
        var subset = await HistoryTextRestoreProposal.CreateAsync(h.Session, [h.Main], default);
        Assert.Single(subset.Proposal!.Exact.Files);
        Assert.NotEqual(first.Proposal.Exact, subset.Proposal.Exact);
        var preview = await subset.Proposal.PreviewAsync(h.Review, default);
        var action = await h.Review.AcceptAsync(preview.Review!.Preview!.Id, default);
        Assert.True(action.Changed); Assert.Equal(other, await h.Bytes(h.Other)); Assert.Equal(h.Original, await h.Bytes(h.Main));
        Assert.False(first.Proposal.IsCurrent);
    }

    [Theory]
    [InlineData("binary")]
    [InlineData("renamed")]
    [InlineData("deleted")]
    [InlineData("added")]
    [InlineData("bom")]
    public async Task Structural_binary_and_BOM_changes_are_routed_without_mutation(string mode)
    {
        await using var h = await HistoryTestHost.Open(); FileId id = h.Main;
        switch (mode)
        {
            case "binary": id = h.Binary; break;
            case "renamed": Assert.True(h.Workspace.RenameFile(h.Main, "renamed.tex")); break;
            case "deleted": Assert.True(h.Workspace.DeleteFile(h.Main)); break;
            case "added": Assert.True(h.Workspace.CreateFileFromBytes("added.tex", Encoding.UTF8.GetBytes("new"))); id = h.Workspace.ActiveFileId!.Value; break;
            case "bom": h.File(h.Main).HasBom = false; Assert.True(h.Workspace.EditText(h.Main, "without BOM")); break;
        }
        string before = h.State(); int commits = h.Io.Commits;
        await h.Compare();
        var result = await HistoryTextRestoreProposal.CreateAsync(h.Session, [id], default);
        Assert.Equal(HistoryComparisonCode.WholeProjectRequired, result.Code); Assert.Contains(id, result.WholeProjectFiles);
        Assert.Null(result.Proposal); Assert.Equal(before, h.State()); Assert.Equal(commits, h.Io.Commits);
    }

    [Fact]
    public async Task Summary_only_diff_still_uses_fresh_exact_historical_bytes_not_display_lines()
    {
        await using var h = await HistoryTestHost.Open();
        var original = Encoding.UTF8.GetBytes(new string('a', 1024 * 1024 + 16));
        Assert.True(h.Workspace.CreateFileFromBytes("big.tex", original)); var id = h.Workspace.ActiveFileId!.Value;
        Assert.Equal(CommitStatus.Saved, (await h.Workspace.SaveAsync(default)).Status); var saved = h.Workspace.CurrentRevision!.Id;
        Assert.True(h.Workspace.EditText(id, new string('b', original.Length)));
        var comparison = await h.Session.CompareWorkingAsync(saved, default); Assert.Null(comparison.Comparison!.DetailedFile(id));
        var proposal = await HistoryTextRestoreProposal.CreateAsync(h.Session, [id], default);
        Assert.Equal(original, proposal.Proposal!.Exact.Files.Single().After.ToArray());
        var preview = await proposal.Proposal.PreviewAsync(h.Review, default);
        Assert.True(preview.Review!.Preview!.Files.Single().SummaryOnly);
        Assert.Equal(original, preview.Review.Preview.Prepared.Files.Single().After.ToArray());
        Assert.False(h.Workspace.CurrentRevision!.Id == saved && !h.Workspace.IsDirty);
    }

    [Fact]
    public async Task Quota_failure_keeps_exact_pending_bytes_inverse_retry_and_export_materialization()
    {
        await using var h = await HistoryTestHost.Open(); var before = (await h.Bytes(h.Main)).ToArray();
        var proposal = await h.Proposal(h.Main); var preview = await proposal.PreviewAsync(h.Review, default);
        h.Io.FailWrites = true;
        var applied = await h.Review.AcceptAsync(preview.Review!.Preview!.Id, default);
        Assert.True(applied.Changed); Assert.Equal(h.Original, await h.Bytes(h.Main));
        Assert.Equal(ReviewStatus.SaveFailed, h.Review.State.Status); Assert.False(h.Review.State.Action!.Persisted);
        Assert.True(h.Review.State.Action.CanUndo); Assert.True(h.Workspace.IsDirty); Assert.NotNull(h.File(h.Main).PendingTextBytes);
        await h.Compare(); var next = await HistoryTextRestoreProposal.CreateAsync(h.Session, [h.Main], default);
        // Already restored text is a no-op; choose the formerly current revision
        // for a second proposal and ensure it cannot displace failed recovery.
        await h.Session.CompareWorkingAsync(h.Current, default);
        next = await HistoryTextRestoreProposal.CreateAsync(h.Session, [h.Main], default);
        Assert.Equal(HistoryComparisonCode.PendingReview, (await next.Proposal!.PreviewAsync(h.Review, default)).Code);
        Assert.Equal(applied.ActionId, h.Review.State.Action.Id);
        h.Io.FailWrites = false;
        var retry = await h.Review.RetrySaveAsync(applied.ActionId!.Value, default);
        Assert.Equal(ReviewStatus.AppliedSaved, retry.Status); Assert.Equal(h.Original, await h.Bytes(h.Main));
        var undone = await h.Review.UndoAsync(applied.ActionId.Value, default);
        Assert.Equal(ReviewStatus.UndoSaved, undone.Status); Assert.Equal(before, await h.Bytes(h.Main));
    }

    [Theory]
    [InlineData("writer-loss")]
    [InlineData("readonly")]
    [InlineData("head")]
    public async Task Real_mutation_boundary_refuses_lost_writer_or_head_without_partial_changes(string mode)
    {
        await using var h = await HistoryTestHost.Open(); var proposal = await h.Proposal(h.Main);
        var preview = await proposal.PreviewAsync(h.Review, default); var before = (await h.Bytes(h.Main)).ToArray();
        if (mode == "writer-loss") h.Io.Owned = false;
        if (mode == "readonly") { h.Io.AllowAcquire = false; await h.Workspace.OpenAsync(h.Workspace.Project!.Id, default); }
        if (mode == "head") h.Io.Projects[h.Workspace.Project!.Id.ToString()]["HeadRevisionId"] = h.Old.ToString();
        var accepted = await h.Review.AcceptAsync(preview.Review!.Preview!.Id, default);
        Assert.False(accepted.Changed); Assert.Equal(before, await h.Bytes(h.Main));
        Assert.Equal(h.OtherBytes, await h.Bytes(h.Other)); Assert.Equal(h.BinaryBytes, await h.Bytes(h.Binary));
    }

    [Fact]
    public async Task Same_hash_new_generation_stales_proposal_and_cancel_retires_only_owned_preview()
    {
        await using var h = await HistoryTestHost.Open(); var proposal = await h.Proposal(h.Main);
        string text = h.File(h.Main).Content; h.Workspace.EditText(h.Main, "away"); h.Workspace.EditText(h.Main, text);
        Assert.False(proposal.IsCurrent); Assert.Equal(HistoryComparisonCode.Stale, (await proposal.PreviewAsync(h.Review, default)).Code);
        proposal = await h.Proposal(h.Main); var preview = await proposal.PreviewAsync(h.Review, default);
        Assert.NotNull(preview.Review!.Preview); h.Session.Cancel(); Assert.Null(h.Review.State.Preview);
        Assert.Equal(0, h.Review.State.Action is null ? 0 : 1);
    }

    [Fact]
    public async Task Held_selection_read_and_held_preview_keep_actual_admission_after_cancel()
    {
        await using var h = await HistoryTestHost.Open(); await h.Compare();
        var hold = h.Io.HoldNext("readContent");
        var creating = HistoryTextRestoreProposal.CreateAsync(h.Session, [h.Main], default); await hold.Wait();
        h.Session.Cancel(); Assert.True(h.Session.Busy); Assert.False(creating.IsCompleted);
        Assert.Equal(HistoryComparisonCode.Busy, (await HistoryTextRestoreProposal.CreateAsync(h.Session, [h.Main], default)).Code);
        hold.Release.TrySetResult(); Assert.Equal(HistoryComparisonCode.Cancelled, (await creating.WaitAsync(HistoryTestHost.Bound)).Code);
        var proposal = await h.Proposal(h.Main);
        hold = h.Io.HoldNext("readContent");
        var previewing = proposal.PreviewAsync(h.Review, default); await hold.Wait();
        h.Session.Cancel();
        // Requires the proposed shared settled-capture/review admission patch.
        Assert.True(h.Session.Busy); Assert.True(h.Review.State.Busy); Assert.False(previewing.IsCompleted);
        Assert.Equal(ReviewCode.Busy, (await h.Review.PreviewAsync(await Generic(h), default)).Failure!.Code);
        hold.Release.TrySetResult();
        Assert.Equal(HistoryComparisonCode.Cancelled, (await previewing.WaitAsync(HistoryTestHost.Bound)).Code);
        Assert.False(h.Session.Busy); Assert.False(h.Review.State.Busy); Assert.Null(h.Review.State.Preview);
    }

    [Fact]
    public async Task Held_review_presentation_is_admitted_and_cancellation_retires_its_preview()
    {
        await using var h = await HistoryTestHost.Open(); var proposal = await h.Proposal(h.Main);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = proposal.PreviewAsync(h.Review, default, async _ => { reached.TrySetResult(); await release.Task; });
        await reached.Task.WaitAsync(HistoryTestHost.Bound); h.Session.Cancel(); Assert.True(h.Session.Busy); Assert.False(pending.IsCompleted);
        Assert.Null(h.Review.State.Preview); release.TrySetResult();
        Assert.Equal(HistoryComparisonCode.Cancelled, (await pending.WaitAsync(HistoryTestHost.Bound)).Code);
    }

    [Fact]
    public async Task Whole_restore_still_creates_new_child_and_prior_checkpoint_remains_reversible()
    {
        await using var h = await HistoryTestHost.Open();
        var head = h.Workspace.CurrentRevision!.Id;
        var preview = await h.Workspace.CaptureRestorePreviewAsync(h.Old, default);
        var result = await h.Workspace.AcceptRestoreAsync(preview, default);
        Assert.Equal(CommitStatus.Saved, result.Status); Assert.NotEqual(h.Old, result.Head);
        Assert.Equal(head, h.Workspace.CurrentRevision!.ParentRevisionId); Assert.Equal(h.Original, await h.Bytes(h.Main));
        var reverse = await h.Workspace.CaptureRestorePreviewAsync(head, default);
        Assert.Equal(CommitStatus.Saved, (await h.Workspace.AcceptRestoreAsync(reverse, default)).Status);
        Assert.NotEqual(head, h.Workspace.CurrentRevision!.Id); Assert.NotEqual(h.Original, await h.Bytes(h.Main));
        Assert.NotNull((await h.Repo.ReadRevisionAsync(h.Workspace.Project!.Id, head, default)).Revision);
    }

    private static async Task<TextChangeSet> Generic(HistoryTestHost h)
    {
        // Edited text avoids another durable read while the deliberate hold is
        // active; proposal preimages remain those from the current real owner.
        var bytes = Encoding.UTF8.GetBytes("replacement"); var file = h.File(h.Other);
        byte[] before = h.OtherBytes;
        await Task.CompletedTask;
        return TextChangeSet.Create(TextPatchOrigin.User, "independent user proposal", [new(file.Id, file.RelativePath,
            SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(before)), before.Length, [new(0, before.Length, before, bytes)])]).ChangeSet!;
    }
}
