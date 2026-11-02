using System.Text;
using Client.Editing.Search;
using Client.Review;
using Client.Workspace.Domain;
using Client.Workspace.Patching;
using Xunit;
using HistoryHost = Workspace.Tests.HistoryComparison.HistoryTestHost;

namespace Workspace.Tests.Editing.Search;

public sealed class ProjectReplaceTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);
    [Fact]
    public async Task Multi_file_literal_selection_decline_accept_save_and_Undo_preserve_exact_bytes()
    {
        await using var h = await HistoryHost.Open(); var second = await Sources(h);
        using var search = new ProjectSearchSession(h.Workspace); using var replace = new ProjectReplaceSession(h.Workspace, search, h.Locks, h.Repo);
        var report = await search.SearchAsync("old"); Assert.Equal(3, report.Matches.Length);
        string before = h.State(); var head = h.Workspace.CurrentRevision!.Id;
        byte[] a = (await h.Bytes(h.Main)).ToArray(), b = (await h.Bytes(second)).ToArray();
        var request = replace.CreateRequest(report.Matches, "$1\\🙂", allComplete: true).Request!;
        var preview = await request.PreviewAsync(h.Review, default);
        Assert.NotNull(preview.Preview); Assert.Equal(before, h.State()); Assert.Equal(TextPatchOrigin.User, preview.Preview.Proposal.Origin);
        Assert.Equal(2, preview.Preview.Prepared.Files.Length); Assert.Equal(3, preview.Preview.Prepared.Files.Sum(f => f.Edits.Length));
        Assert.All(preview.Preview.Prepared.Files.SelectMany(f => f.Edits), e => Assert.False(e.Replacement.Span.IsEmpty));
        Assert.Null(h.Review.Decline(preview.Preview.Id).Failure); Assert.Equal(a, await h.Bytes(h.Main)); Assert.Equal(b, await h.Bytes(second));
        var fresh = replace.CreateRequest(report.Matches, "$1\\🙂", true).Request!;
        preview = await fresh.PreviewAsync(h.Review, default);
        var applied = await h.Review.AcceptAsync(preview.Preview!.Id, default);
        Assert.True(applied.Changed); Assert.Equal(ReviewStatus.AppliedSaved, applied.Status); Assert.Equal(head, h.Workspace.CurrentRevision!.ParentRevisionId);
        Assert.Equal(Bom("Україна🙂 e\u0301\r\n$1\\🙂\r\n"), await h.Bytes(h.Main));
        Assert.Equal(B("$1\\🙂🙂 $1\\🙂\r\n"), await h.Bytes(second));
        Assert.Equal(h.OtherBytes, await h.Bytes(h.Other)); Assert.Equal(h.BinaryBytes, await h.Bytes(h.Binary));
        var undo = await h.Review.UndoAsync(applied.ActionId!.Value, default);
        Assert.Equal(ReviewStatus.UndoSaved, undo.Status); Assert.Equal(a, await h.Bytes(h.Main)); Assert.Equal(b, await h.Bytes(second));
        Assert.Equal(h.OtherBytes, await h.Bytes(h.Other)); Assert.Equal(h.BinaryBytes, await h.Bytes(h.Binary));
    }

    [Fact]
    public async Task Dirty_CRLF_source_maps_through_existing_encoding_policy_without_unrelated_changes()
    {
        await using var h = await HistoryHost.Open();
        h.Workspace.EditText(h.Main, "old🙂\r\nkeep\rold\n");
        using var search = new ProjectSearchSession(h.Workspace); using var replace = new ProjectReplaceSession(h.Workspace, search, h.Locks, h.Repo);
        var report = search.Search("old"); byte[] before = (await h.Bytes(h.Main)).ToArray();
        var preview = await replace.CreateRequest(report.Matches.Take(1).ToArray(), "new").Request!.PreviewAsync(h.Review, default);
        Assert.Equal(before, preview.Preview!.Prepared.Files.Single().Before.ToArray());
        Assert.Equal(Bom("new🙂\nkeep\nold\n"), preview.Preview.Prepared.Files.Single().After.ToArray());
        Assert.True(h.Workspace.IsDirty); Assert.Equal(before, await h.Bytes(h.Main));
    }

    [Fact]
    public async Task Truncated_listing_permits_only_honest_explicit_subset_not_Replace_all()
    {
        await using var h = await HistoryHost.Open(); await Sources(h);
        h.Workspace.CreateFile("many.tex", string.Concat(Enumerable.Repeat("old ", 101)));
        using var search = new ProjectSearchSession(h.Workspace); using var replace = new ProjectReplaceSession(h.Workspace, search, h.Locks, h.Repo);
        var report = await search.SearchAsync("old"); Assert.Equal(ProjectSearchStatus.Truncated, report.Status);
        Assert.Equal(ProjectReplaceCode.Incomplete, replace.CreateRequest(report.Matches, "new", true).Code);
        var selected = report.Matches.Where(m => m.FileId == h.Main).ToArray();
        var creation = replace.CreateRequest(selected, "new"); Assert.True(creation.Request!.FromIncompleteListing);
        var preview = await creation.Request.PreviewAsync(h.Review, default);
        Assert.Contains("incomplete", preview.Preview!.Proposal.Explanation); Assert.Single(preview.Preview.Prepared.Files);
    }

    [Fact]
    public async Task Duplicate_foreign_overlapping_old_report_invalid_UTF16_and_empty_selections_are_refused()
    {
        await using var h = await HistoryHost.Open(); await Sources(h);
        using var search = new ProjectSearchSession(h.Workspace); using var replace = new ProjectReplaceSession(h.Workspace, search, h.Locks, h.Repo);
        var old = search.Search("old"); var current = search.Search("ol");
        Assert.Equal(ProjectReplaceCode.InvalidInput, replace.CreateRequest([current.Matches[0], current.Matches[0]], "x").Code);
        Assert.Equal(ProjectReplaceCode.InvalidInput, replace.CreateRequest([old.Matches[0], current.Matches.First(m => m.FileId == old.Matches[0].FileId)], "x").Code);
        Assert.Equal(ProjectReplaceCode.InvalidInput, replace.CreateRequest(current.Matches, "\ud800").Code);
        Assert.Equal(ProjectReplaceCode.NoSelection, replace.CreateRequest([], "x").Code);
        Assert.Equal(ProjectReplaceCode.Incomplete, replace.CreateRequest(current.Matches.Take(1).ToArray(), "x", true).Code);
        Assert.Null(h.Review.State.Preview);
        var noOp = await replace.CreateRequest(current.Matches, "ol").Request!.PreviewAsync(h.Review, default);
        Assert.NotNull(noOp.Failure); Assert.Null(noOp.Preview);
        var bomb = replace.CreateRequest(current.Matches, new string('x', (int)WorkspaceTextLimits.FileBytes));
        // Three edits would fit replacement aggregate, but file/project output
        // preflight still rejects without applying even one edit.
        var refused = await bomb.Request!.PreviewAsync(h.Review, default); Assert.NotNull(refused.Failure); Assert.Null(refused.Preview);
        var bomChange = await replace.CreateRequest(current.Matches, "\uFEFF").Request!.PreviewAsync(h.Review, default);
        Assert.NotNull(bomChange.Failure); Assert.Null(bomChange.Preview); Assert.Equal(h.Original, await h.Bytes(h.Main));
    }

    [Fact]
    public async Task More_than_100_files_requires_a_fresh_bounded_subset_and_readonly_never_issues_a_request()
    {
        await using var h = await HistoryHost.Open();
        for (int i = 0; i < 101; i++) h.Workspace.CreateFile($"p{i:D3}.tex", "needle");
        using var search = new ProjectSearchSession(h.Workspace); using var replace = new ProjectReplaceSession(h.Workspace, search, h.Locks, h.Repo);
        var report = await search.SearchAsync("needle"); Assert.Equal(101, report.Matches.Length);
        Assert.Equal(ProjectReplaceCode.LimitExceeded, replace.CreateRequest(report.Matches, "x", true).Code);
        Assert.Equal(ProjectReplaceCode.Ready, replace.CreateRequest(report.Matches.Take(100).ToArray(), "x").Code);
        h.Io.AllowAcquire = false; await h.Workspace.OpenAsync(h.Workspace.Project!.Id, default); search.Search("new");
        Assert.True(h.Workspace.IsReadOnly); Assert.False(replace.CanRequest);
        Assert.Equal(ProjectReplaceCode.ReadOnly, replace.CreateRequest(search.Report.Matches, "x").Code);
    }

    [Fact]
    public async Task Fresh_selection_and_replacement_changes_retire_only_our_unaccepted_preview()
    {
        await using var h = await HistoryHost.Open(); await Sources(h);
        using var search = new ProjectSearchSession(h.Workspace); using var replace = new ProjectReplaceSession(h.Workspace, search, h.Locks, h.Repo);
        var report = search.Search("old"); var request = replace.CreateRequest(report.Matches, "new").Request!;
        var preview = await request.PreviewAsync(h.Review, default); Assert.NotNull(preview.Preview);
        replace.Invalidate(); Assert.Null(h.Review.State.Preview);
        Assert.Equal(ReviewCode.Stale, (await request.PreviewAsync(h.Review, default)).Failure!.Code);
        var unrelated = TextChangeSet.Create(TextPatchOrigin.User, "independent explicit user edit", [new(h.Other, "other.tex", Hash(h.OtherBytes), h.OtherBytes.Length,
            [new(0, h.OtherBytes.Length, h.OtherBytes, B("independent"))])]).ChangeSet!;
        var other = await h.Review.PreviewAsync(unrelated, default); Assert.NotNull(other.Preview);
        var next = replace.CreateRequest(report.Matches.Take(1).ToArray(), "changed").Request!;
        Assert.Equal(ReviewCode.PendingSave, (await next.PreviewAsync(h.Review, default)).Failure!.Code);
        search.Search("old"); replace.Dispose(); Assert.Equal(other.Preview.Id, h.Review.State.Preview!.Id);
    }

    [Fact]
    public async Task Save_failure_retains_pending_exact_bytes_Undo_and_retry_in_the_generic_review_owner()
    {
        await using var h = await HistoryHost.Open(); var second = await Sources(h);
        using var search = new ProjectSearchSession(h.Workspace); using var replace = new ProjectReplaceSession(h.Workspace, search, h.Locks, h.Repo);
        var a = (await h.Bytes(h.Main)).ToArray(); var b = (await h.Bytes(second)).ToArray();
        var report = search.Search("old"); var preview = await replace.CreateRequest(report.Matches, "new").Request!.PreviewAsync(h.Review, default);
        h.Io.FailWrites = true; var applied = await h.Review.AcceptAsync(preview.Preview!.Id, default);
        Assert.True(applied.Changed); Assert.False(h.Review.State.Action!.Persisted); Assert.True(h.Review.State.Action.CanUndo);
        Assert.NotNull(h.File(h.Main).PendingTextBytes); Assert.Equal(Bom("Україна🙂 e\u0301\r\nnew\r\n"), await h.Bytes(h.Main));
        report = await search.SearchAsync("new"); Assert.Equal(3, report.Matches.Length); // pending exact CRLF source
        Assert.Equal(ReviewCode.PendingSave, (await replace.CreateRequest(report.Matches, "later").Request!.PreviewAsync(h.Review, default)).Failure!.Code);
        Assert.Equal(applied.ActionId, h.Review.State.Action.Id);
        var undo = await h.Review.UndoAsync(applied.ActionId!.Value, default); Assert.True(undo.Changed);
        Assert.Equal(a, await h.Bytes(h.Main)); Assert.Equal(b, await h.Bytes(second));
        h.Io.FailWrites = false; var retry = await h.Review.RetrySaveAsync(applied.ActionId.Value, default);
        Assert.Equal(ReviewStatus.UndoSaved, retry.Status); Assert.Equal(a, await h.Bytes(h.Main)); Assert.Equal(b, await h.Bytes(second));
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("aba")]
    [InlineData("head")]
    [InlineData("writer")]
    [InlineData("path")]
    [InlineData("bom")]
    [InlineData("object")]
    [InlineData("alias")]
    [InlineData("cancel")]
    [InlineData("selection")]
    [InlineData("dispose")]
    public async Task Held_actual_source_reads_cannot_publish_after_authority_changes(string mode)
    {
        await using var h = await HistoryHost.Open(); var second = await Sources(h, saveSecond: mode != "alias");
        using var search = new ProjectSearchSession(h.Workspace); using var replace = new ProjectReplaceSession(h.Workspace, search, h.Locks, h.Repo);
        var report = search.Search("old"); var request = replace.CreateRequest(report.Matches, "new").Request!;
        var hold = h.Io.HoldNext("readContent", h.File(h.Main).DurableHash!.Value.Hex);
        var pending = request.PreviewAsync(h.Review, default); await hold.Wait();
        switch (mode)
        {
            case "edit": h.Workspace.EditText(h.Other, "later"); break;
            case "aba": string text = h.File(h.Other).Content; h.Workspace.EditText(h.Other, "away"); h.Workspace.EditText(h.Other, text); break;
            case "head": h.Io.Projects[h.Workspace.Project!.Id.ToString()]["HeadRevisionId"] = h.Old.ToString(); break;
            case "writer": h.Io.Owned = false; break;
            case "path": h.File(h.Main).RelativePath = "different.tex"; break;
            case "bom": h.File(h.Main).HasBom = false; break;
            case "object": var file = h.File(h.Main); h.Workspace.Files[h.Workspace.Files.IndexOf(file)] = file.Clone(); break;
            case "alias": h.File(second).ImportedBytes![0] ^= 1; break;
            case "cancel": replace.Cancel(); break;
            case "selection": Assert.Equal(ProjectReplaceCode.Busy, replace.CreateRequest(report.Matches.Take(1).ToArray(), "other").Code); break;
            case "dispose": replace.Dispose(); break;
        }
        Assert.True(replace.Busy); Assert.False(pending.IsCompleted); hold.Release.TrySetResult();
        var result = await pending.WaitAsync(Bound); Assert.NotNull(result.Failure); Assert.Null(result.Preview); Assert.Null(h.Review.State.Preview); Assert.False(replace.Busy);
        Assert.Equal(h.Original, await h.Bytes(h.Main)); // none of the test changes altered authoritative main bytes
    }

    [Fact]
    public async Task Held_generic_review_capture_and_held_lock_queries_keep_actual_admission_after_cancel()
    {
        await using var h = await HistoryHost.Open(); await Sources(h);
        using var search = new ProjectSearchSession(h.Workspace); using var replace = new ProjectReplaceSession(h.Workspace, search, h.Locks, h.Repo);
        var report = search.Search("old"); var request = replace.CreateRequest(report.Matches, "new").Request!;
        string hash = h.File(h.Main).DurableHash!.Value.Hex;
        var first = h.Io.HoldNext("readContent", hash); var pending = request.PreviewAsync(h.Review, default); await first.Wait();
        var second = h.Io.HoldNext("readContent", hash); first.Release.TrySetResult(); await second.Wait();
        Assert.True(h.Review.State.Busy); replace.Cancel(); Assert.True(replace.Busy); Assert.False(pending.IsCompleted); Assert.True(h.Review.State.Busy);
        second.Release.TrySetResult(); var cancelled = await pending.WaitAsync(Bound); Assert.Equal(ReviewCode.Cancelled, cancelled.Failure!.Code);
        Assert.False(replace.Busy); Assert.False(h.Review.State.Busy); Assert.Null(h.Review.State.Preview);
        report = search.Search("old"); request = replace.CreateRequest(report.Matches, "new").Request!;
        using var external = new CancellationTokenSource();
        var ownership = h.Io.HoldNext("ownsLock"); pending = request.PreviewAsync(h.Review, external.Token); await ownership.Wait();
        external.Cancel(); Assert.True(replace.Busy); Assert.False(pending.IsCompleted);
        ownership.Release.TrySetResult(); Assert.Equal(ReviewCode.Cancelled, (await pending.WaitAsync(Bound)).Failure!.Code);
        Assert.Equal(ReviewCode.Stale, (await request.PreviewAsync(h.Review, default)).Failure!.Code);
    }

    [Fact]
    public async Task A_newer_independent_review_is_not_retired_or_returned_as_our_old_preview_after_a_held_final_check()
    {
        await using var h = await HistoryHost.Open(); await Sources(h);
        using var search = new ProjectSearchSession(h.Workspace); using var replace = new ProjectReplaceSession(h.Workspace, search, h.Locks, h.Repo);
        var request = replace.CreateRequest(search.Search("old").Matches, "new").Request!;
        string hash = h.File(h.Main).DurableHash!.Value.Hex;
        var material = h.Io.HoldNext("readContent", hash); var pending = request.PreviewAsync(h.Review, default); await material.Wait();
        var capture = h.Io.HoldNext("readContent", hash); material.Release.TrySetResult(); await capture.Wait();
        var final = h.Io.HoldNext("getProject"); capture.Release.TrySetResult(); await final.Wait();
        var ours = h.Review.State.Preview!; Assert.Null(h.Review.Decline(ours.Id).Failure);
        var set = TextChangeSet.Create(TextPatchOrigin.User, "independent user preview after explicit decline", [new(h.Other, "other.tex", Hash(h.OtherBytes), h.OtherBytes.Length,
            [new(0, h.OtherBytes.Length, h.OtherBytes, B("other proposal"))])]).ChangeSet!;
        var newer = await h.Review.PreviewAsync(set, default); Assert.NotNull(newer.Preview);
        final.Release.TrySetResult(); var result = await pending.WaitAsync(Bound);
        Assert.Equal(ReviewCode.Stale, result.Failure!.Code); Assert.Null(result.Preview); Assert.Equal(newer.Preview.Id, h.Review.State.Preview!.Id);
    }

    private static async Task<FileId> Sources(HistoryHost h, bool saveSecond = true)
    {
        // Reuse accepted whole-restore byte materialization solely as fixture
        // setup; product replacement always goes through ordinary review.
        var restore = await h.Workspace.CaptureRestorePreviewAsync(h.Old, default);
        Assert.Equal(CommitStatus.Saved, (await h.Workspace.AcceptRestoreAsync(restore, default)).Status);
        Assert.True(h.Workspace.CreateFileFromBytes("two.tex", B("old🙂 old\r\n"))); var second = h.Workspace.ActiveFileId!.Value;
        if (saveSecond) Assert.Equal(CommitStatus.Saved, (await h.Workspace.SaveAsync(default)).Status);
        return second;
    }
    private static byte[] B(string text) => Encoding.UTF8.GetBytes(text);
    private static byte[] Bom(string text) => [239, 187, 191, .. B(text)];
    private static SHA256 Hash(byte[] bytes) => SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes));
}
