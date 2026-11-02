using System.Text;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Client.Workspace.Transfer;
using Xunit;

namespace Workspace.Tests.HistoryComparison;

public sealed class ComparisonTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saved_pairs_and_working_comparison_are_read_only_and_left_to_right(bool readOnly)
    {
        await using var h = await HistoryTestHost.Open(readOnly);
        if (!readOnly) Assert.True(h.Workspace.EditText(h.Main, "dirty🙂\n"));
        string before = h.State(); int commits = h.Io.Commits;
        var forward = (await h.Session.CompareSavedAsync(h.Old, h.Current, default)).Comparison!;
        Assert.NotNull(forward); Assert.False(forward.Binding.RightIsWorking);
        var change = forward.Summary.Changes.Single(f => f.FileId == h.Main);
        Assert.Contains(change.Diff!, d => d.Op == DiffOp.Delete && d.Text == "old");
        Assert.Contains(change.Diff!, d => d.Op == DiffOp.Insert && d.Text == "new");
        var reverse = (await h.Session.CompareSavedAsync(h.Current, h.Old, default)).Comparison!;
        var reversed = reverse.Summary.Changes.Single(f => f.FileId == h.Main);
        Assert.Contains(reversed.Diff!, d => d.Op == DiffOp.Delete && d.Text == "new");
        Assert.Contains(reversed.Diff!, d => d.Op == DiffOp.Insert && d.Text == "old");
        var working = await h.Compare();
        Assert.True(working.Binding.RightIsWorking); Assert.Null(working.Binding.Right);
        Assert.Equal(h.Workspace.WorkingGeneration, working.Binding.WorkingGeneration);
        Assert.Equal(before, h.State()); Assert.Equal(commits, h.Io.Commits);
        Assert.Equal(h.BinaryBytes, await h.Bytes(h.Binary));
    }

    [Fact]
    public async Task Save_failure_and_new_imported_CRLF_bytes_compare_without_save_or_selection()
    {
        await using var h = await HistoryTestHost.Open();
        byte[] exact = [239, 187, 191, .. Encoding.UTF8.GetBytes("новий🙂 e\u0301\r\n")];
        Assert.True(h.Workspace.CreateFileFromBytes("новий.tex", exact)); var added = h.Workspace.ActiveFileId!.Value;
        h.Io.FailWrites = true;
        Assert.Equal(CommitStatus.StorageFailure, (await h.Workspace.SaveAsync(default)).Status);
        string before = h.State(); int commits = h.Io.Commits;
        var comparison = await h.Compare();
        var change = comparison.Summary.Changes.Single(f => f.FileId == added);
        Assert.Equal(FileChangeKind.Added, change.Kind); Assert.Equal(exact.Length, change.NewLength);
        Assert.Equal(Hash(exact), change.NewHash); Assert.Equal(exact, await h.Bytes(added));
        Assert.Equal(before, h.State()); Assert.Equal(commits, h.Io.Commits);
    }

    [Fact]
    public async Task Rename_plus_content_binary_and_filters_keep_exact_direction_and_hashes()
    {
        await using var h = await HistoryTestHost.Open();
        Assert.True(h.Workspace.RenameFile(h.Main, "renamed.tex"));
        // Use the existing mutable fixture model only to simulate a dirty binary
        // owner; no new product mutation API is being introduced by this test.
        h.File(h.Binary).BinaryContent = [4, 5, 255]; h.File(h.Binary).DirtyBinary = true;
        var comparison = await h.Compare();
        var renamed = comparison.Filter("теза", FileChangeKind.ContentChanged).Single();
        Assert.Equal("теза/main.tex", renamed.OldPath); Assert.Equal("renamed.tex", renamed.Path);
        Assert.NotNull(comparison.DetailedFile(h.Main)); Assert.Equal(h.Original.Length, renamed.OldLength);
        var binary = comparison.Filter(kind: FileChangeKind.BinaryChanged).Single();
        Assert.Null(binary.Diff); Assert.Equal(3, binary.NewLength); Assert.Equal(Hash([4, 5, 255]), binary.NewHash);
        Assert.False(comparison.CanSelectTextRestore(h.Main)); Assert.False(comparison.CanSelectTextRestore(h.Binary));
    }

    [Fact]
    public async Task Oversized_text_is_summary_only_but_lengths_and_hashes_remain_exact()
    {
        await using var h = await HistoryTestHost.Open();
        var a = Encoding.UTF8.GetBytes(new string('a', (int)ArchiveLimits.DiffByteBudget + 8));
        Assert.True(h.Workspace.CreateFileFromBytes("big.tex", a)); var id = h.Workspace.ActiveFileId!.Value;
        Assert.Equal(CommitStatus.Saved, (await h.Workspace.SaveAsync(default)).Status); var old = h.Workspace.CurrentRevision!.Id;
        Assert.True(h.Workspace.EditText(id, new string('b', a.Length)));
        var result = (await h.Session.CompareWorkingAsync(old, default)).Comparison!;
        var change = result.Summary.Changes.Single(f => f.FileId == id);
        Assert.Null(change.Diff); Assert.True(result.Summary.DiffBudgetExceeded);
        Assert.Equal(a.Length, change.OldLength); Assert.Equal(a.Length, change.NewLength); Assert.NotEqual(change.OldHash, change.NewHash);
        Assert.Null(result.DetailedFile(id)); Assert.True(result.CanSelectTextRestore(id));
    }

    [Theory]
    [InlineData("unchanged")]
    [InlineData("binary")]
    [InlineData("manifest")]
    [InlineData("late-read")]
    public async Task Corrupt_required_data_withholds_the_entire_result(string kind)
    {
        await using var h = await HistoryTestHost.Open(); string before = h.State();
        var target = kind == "binary" ? h.Binary : h.Other;
        if (kind == "manifest") h.Io.Revisions[h.Old.ToString()]["Id"] = RevisionId.New().ToString();
        else if (kind == "late-read")
        {
            var hold = h.Io.HoldNext("readContent", h.File(target).DurableHash!.Value.Hex);
            var pending = h.Session.CompareSavedAsync(h.Old, h.Current, default); await hold.Wait();
            h.Io.Content[h.File(target).DurableHash!.Value.Hex][0] ^= 1; hold.Release.TrySetResult();
            Assert.Equal(HistoryComparisonCode.CorruptContent, (await pending.WaitAsync(HistoryTestHost.Bound)).Code);
            Assert.Null(h.Session.Comparison); Assert.Equal(before, h.State()); return;
        }
        else h.Io.Content[h.File(target).DurableHash!.Value.Hex][0] ^= 1;
        var result = await h.Session.CompareSavedAsync(h.Old, h.Current, default);
        // Head verification may identify corruption first and refuse freshness.
        Assert.Contains(result.Code, new[] { HistoryComparisonCode.CorruptContent, HistoryComparisonCode.Stale });
        Assert.Null(result.Comparison); Assert.Null(h.Session.Comparison); Assert.Equal(before, h.State());
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("same-bytes-new-generation")]
    [InlineData("rename")]
    [InlineData("durable-head")]
    [InlineData("session")]
    [InlineData("project")]
    [InlineData("alias")]
    public async Task Held_actual_read_cannot_publish_after_authority_changes(string mode)
    {
        await using var h = await HistoryTestHost.Open();
        var hold = h.Io.HoldNext("readContent"); var pending = h.Session.CompareWorkingAsync(h.Old, default); await hold.Wait();
        switch (mode)
        {
            case "edit": h.Workspace.EditText(h.Main, "later"); break;
            case "same-bytes-new-generation": var text = h.File(h.Main).Content; h.Workspace.EditText(h.Main, "other"); h.Workspace.EditText(h.Main, text); break;
            case "rename": h.Workspace.RenameFile(h.Other, "moved.tex"); break;
            case "durable-head": h.Io.Projects[h.Workspace.Project!.Id.ToString()]["HeadRevisionId"] = h.Old.ToString(); break;
            case "session": await h.Workspace.OpenAsync(h.Workspace.Project!.Id, default); break;
            case "project": var p = await h.Repo.CreateProjectAsync("other project", default); await h.Workspace.OpenAsync(p.Id, default); break;
            case "alias": h.File(h.Binary).BinaryContent![0] ^= 1; break;
        }
        Assert.True(h.Session.Busy); hold.Release.TrySetResult();
        var result = await pending.WaitAsync(HistoryTestHost.Bound);
        Assert.Contains(result.Code, new[] { HistoryComparisonCode.Cancelled, HistoryComparisonCode.Stale });
        Assert.Null(result.Comparison); Assert.False(h.Session.Busy);
    }

    [Fact]
    public async Task Cancel_retains_admission_until_actual_read_and_render_callbacks_settle()
    {
        await using var h = await HistoryTestHost.Open();
        var hold = h.Io.HoldNext("readContent"); var pending = h.Session.CompareWorkingAsync(h.Old, default); await hold.Wait();
        h.Session.Cancel(); Assert.True(h.Session.Busy); Assert.False(pending.IsCompleted);
        Assert.Equal(HistoryComparisonCode.Busy, (await h.Session.CompareSavedAsync(h.Old, h.Current, default)).Code);
        hold.Release.TrySetResult(); Assert.Equal(HistoryComparisonCode.Cancelled, (await pending.WaitAsync(HistoryTestHost.Bound)).Code);
        Assert.False(h.Session.Busy); Assert.Null(h.Session.Comparison);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var renderSession = new HistoryComparisonSession(h.Workspace, h.History, async () => { reached.TrySetResult(); await release.Task; });
        var rendering = renderSession.CompareWorkingAsync(h.Old, default); await reached.Task.WaitAsync(HistoryTestHost.Bound);
        renderSession.Cancel(); Assert.True(renderSession.Busy); Assert.False(rendering.IsCompleted);
        Assert.Equal(HistoryComparisonCode.Busy, (await renderSession.CompareWorkingAsync(h.Old, default)).Code);
        release.TrySetResult(); Assert.Equal(HistoryComparisonCode.Cancelled, (await rendering.WaitAsync(HistoryTestHost.Bound)).Code);
        Assert.Null(renderSession.Comparison); Assert.False(renderSession.Busy);
    }

    [Fact]
    public async Task Published_snapshot_stays_visible_but_stale_without_rebasing()
    {
        await using var h = await HistoryTestHost.Open(); var snapshot = await h.Compare();
        h.Workspace.EditText(h.Main, "another generation");
        Assert.True(h.Session.IsStale); Assert.False(h.Session.IsCurrent(snapshot)); Assert.Same(snapshot, h.Session.Comparison);
    }

    private static SHA256 Hash(byte[] b) => SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(b));
}
