using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Renaming;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;
using static Workspace.Tests.ProjectRename.RenameTestHost;

namespace Workspace.Tests.ProjectRename;

public sealed class ProjectRenameTests
{
    [Theory][InlineData("  Назва🙂  ", "Назва🙂")][InlineData(" duplicate ", "duplicate")][InlineData("<plain & text>", "<plain & text>")]
    public void Name_policy_trims_and_keeps_plain_unicode(string input, string expected)
    { Assert.True(ProjectRenameName.TryNormalize(input, out string actual)); Assert.Equal(expected, actual); }
    [Theory][InlineData("")][InlineData("   ")][InlineData("\nname")][InlineData("name\r")][InlineData("a\0b")][InlineData("\tname")]
    [InlineData("a\u0085b")][InlineData("a\u2028b")][InlineData("a\u2029b")]
    public void Invalid_names_refuse_without_normalizing_controls_away(string input)
    { Assert.False(ProjectRenameName.TryNormalize(input, out _)); }
    [Fact] public void Runtime_malformed_UTF16_is_refused_but_valid_pair_and_replacement_scalar_are_allowed()
    {
        // Do not put unmatched surrogates in attribute metadata: runner/metadata
        // serialization can substitute U+FFFD before the policy receives them.
        (string Input, int[] Units)[] malformed =
        [
            (new string((char)0xD800, 1), [0xD800]),
            (new string((char)0xDC00, 1), [0xDC00]),
            (new string(['a', (char)0xD800, 'b']), [0x61, 0xD800, 0x62]),
            (new string(['a', (char)0xDC00, 'b']), [0x61, 0xDC00, 0x62]),
            (new string([(char)0xD800, 'a']), [0xD800, 0x61]),
            (new string(['a', (char)0xDC00]), [0x61, 0xDC00]),
            (new string([(char)0xD800, (char)0xD800]), [0xD800, 0xD800]),
            (new string([(char)0xDC00, (char)0xD800]), [0xDC00, 0xD800]),
            (new string([(char)0xD83D, (char)0xDE42, (char)0xD800]), [0xD83D, 0xDE42, 0xD800])
        ];
        foreach (var (input, units) in malformed)
        {
            Assert.Equal(units.Length, input.Length);
            for (int i = 0; i < units.Length; i++) Assert.Equal(units[i], (int)input[i]);
            Assert.False(ProjectRenameName.TryNormalize(input, out _));
        }
        string pair = new string([(char)0xD83D, (char)0xDE42]);
        Assert.Equal(2, pair.Length); Assert.Equal(0xD83D, (int)pair[0]); Assert.Equal(0xDE42, (int)pair[1]);
        Assert.True(ProjectRenameName.TryNormalize(pair, out string normalizedPair)); Assert.Equal(pair, normalizedPair);
        string replacement = new string((char)0xFFFD, 1);
        Assert.Equal(1, replacement.Length); Assert.Equal(0xFFFD, (int)replacement[0]);
        Assert.True(ProjectRenameName.TryNormalize(replacement, out string normalizedReplacement)); Assert.Equal(replacement, normalizedReplacement);
    }
    [Fact] public void Scalar_limit_counts_emoji_as_one_and_preserves_exact_boundary()
    {
        string exact = string.Concat(Enumerable.Repeat("🙂", 256));
        Assert.True(ProjectRenameName.TryNormalize("  " + exact + "  ", out string normalized)); Assert.Equal(exact, normalized);
        Assert.False(ProjectRenameName.TryNormalize(exact + "a", out _));
        Assert.True(ProjectRenameName.TryNormalize(new string('Ж', 256), out _)); Assert.False(ProjectRenameName.TryNormalize(new string('Ж', 257), out _));
    }

    [Fact] public void Explicit_capture_confirm_changes_only_name_no_head_content_history_or_save() => Run(async () =>
    {
        await using var h = new RenameTestHost(); var original = h.Project;
        var revision = h.Data.Revision; var content = h.Data.Content.ToDictionary(p => p.Key, p => p.Value.ToArray());
        Assert.True(h.Session.Show(original)); var proposal = h.Session.CurrentProposal!;
        Assert.Equal(original.Name, h.Project.Name); Assert.Equal(0, h.Repo.Renames);
        var result = await h.Confirm("  Нова назва🙂  ").WaitAsync(Bound);
        Assert.Equal(ProjectRenameCode.Applied, result.Code); Assert.False(result.WriterReleaseFailed);
        Assert.Equal(original with { Name = "Нова назва🙂" }, h.Project);
        Assert.Same(revision, h.Data.Revision); Assert.Equal(0, h.Data.Commits); Assert.Equal(0, h.Data.Restores);
        foreach (var pair in content) Assert.Equal(pair.Value, h.Data.Content[pair.Key]);
        Assert.Null(h.Workspace.Project); Assert.Empty(h.Workspace.Files); Assert.Equal(1, h.Lock.Releases); Assert.Equal(0, h.Lock.Waits);
        Assert.Equal(ProjectRenameCode.Stale, (await h.Session.ConfirmAsync(proposal, "must not run")).Code);
        Assert.Equal(1, h.Repo.Renames); Assert.Equal(ProjectRenameState.Applied, h.Session.State);
        await h.Workspace.OpenAsync(original.Id, default);
        Assert.Equal("Нова назва🙂", h.Workspace.Project!.Name); Assert.Equal(original.Id, h.Workspace.Project.Id);
        Assert.Equal(original.HeadRevisionId, h.Workspace.CurrentRevision!.Id);
        foreach (var file in h.Workspace.Files)
            Assert.Equal(content[file.DurableHash!.Value], await h.Workspace.GetFileBytesAsync(file, default));
        Assert.Equal(0, h.Data.Commits);
    });
    [Fact] public void Same_normalized_name_is_checked_noop_and_duplicate_names_remain_legal() => Run(async () =>
    {
        await using var h = new RenameTestHost(); Assert.True(h.Session.Show(h.Project));
        Assert.Equal(ProjectRenameCode.Unchanged, (await h.Confirm(" " + h.Project.Name + " ").WaitAsync(Bound)).Code);
        Assert.Equal(0, h.Repo.NameWrites); Assert.Equal(1, h.Lock.Releases);
        var other = new Project(ProjectId.New(), "duplicate", null); h.Data.Projects[other.Id] = (other, null);
        Assert.True(h.Session.Show(h.Project)); Assert.Equal(ProjectRenameCode.Applied, (await h.Confirm("duplicate")).Code);
        Assert.Equal("duplicate", h.Project.Name); Assert.Equal("duplicate", h.Data.Projects[other.Id].Item1.Name);
    });
    [Theory][InlineData("name")][InlineData("head")][InlineData("schema")][InlineData("missing")]
    public void Captured_metadata_is_CAS_not_a_later_catalog_authority(string mismatch) => Run(async () =>
    {
        await using var h = new RenameTestHost(); Assert.True(h.Session.Show(h.Project)); var original = h.Project;
        if (mismatch == "missing") h.Repo.Missing = true;
        else
        {
            var changed = mismatch switch
            {
                "name" => original with { Name = "other" },
                "head" => original with { HeadRevisionId = RevisionId.New() },
                _ => original with { SchemaVersion = original.SchemaVersion + 1 }
            };
            h.Data.Project = changed; h.Data.Projects[changed.Id] = (changed, h.Data.Revision);
        }
        var expected = h.Project; var result = await h.Confirm("requested").WaitAsync(Bound);
        Assert.Equal(mismatch == "missing" ? ProjectRenameCode.Missing : ProjectRenameCode.Stale, result.Code);
        Assert.Equal(expected, h.Project); Assert.Equal(0, h.Repo.NameWrites); Assert.Equal(1, h.Lock.Releases);
    });
    [Fact] public void Cancel_stale_button_and_superseded_proposal_never_retarget_a_row() => Run(async () =>
    {
        await using var h = new RenameTestHost(); h.Session.Show(h.Project); var old = h.Session.CurrentProposal!;
        h.Session.Cancel(old); Assert.Equal(ProjectRenameState.Cancelled, h.Session.State);
        Assert.Equal(ProjectRenameCode.Stale, (await h.Session.ConfirmAsync(old, "no")).Code); Assert.Equal(0, h.Lock.Acquires);
        h.Session.Show(h.Project); var current = h.Session.CurrentProposal!;
        h.Session.Cancel(old); Assert.Same(current, h.Session.CurrentProposal);
        Assert.True(h.Session.TryOtherAction()); Assert.Equal(ProjectRenameCode.Stale, (await h.Session.ConfirmAsync(current, "no")).Code);
        Assert.Equal(0, h.Repo.Renames);
    });
    [Fact] public void Invalid_input_keeps_actionable_proposal_and_no_lock_or_storage_call() => Run(async () =>
    {
        await using var h = new RenameTestHost(); h.Session.Show(h.Project); var proposal = h.Session.CurrentProposal!;
        Assert.Equal(ProjectRenameCode.InvalidName, (await h.Confirm("bad\nname")).Code); Assert.Same(proposal, h.Session.CurrentProposal);
        Assert.Equal(0, h.Lock.Acquires); Assert.Equal(0, h.Repo.Renames);
        Assert.Equal(ProjectRenameCode.Applied, (await h.Confirm("valid")).Code);
    });
    [Theory][InlineData("acquire")][InlineData("transaction")][InlineData("release")]
    public void Actual_busy_excludes_dashboard_mutations_through_all_settlement(string phase) => Run(async () =>
    {
        await using var h = new RenameTestHost(); h.Session.Show(h.Project);
        if (phase == "acquire") h.Lock.AcquireHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (phase == "transaction") h.Repo.BeforeWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (phase == "release") h.Lock.ReleaseHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = h.Confirm("new name");
        if (phase == "acquire") await h.Lock.AcquireReached.Task.WaitAsync(Bound);
        if (phase == "transaction") await h.Repo.Reached.Task.WaitAsync(Bound);
        if (phase == "release") await h.Lock.ReleaseReached.Task.WaitAsync(Bound);
        Assert.True(h.Session.Busy); Assert.True(h.Workspace.ProjectRenameBusy); Assert.False(h.Session.TryOtherAction()); Assert.False(pending.IsCompleted);
        await Assert.ThrowsAsync<WorkspaceException>(() => h.Workspace.OpenAsync(h.Project.Id, default));
        await Assert.ThrowsAsync<WorkspaceException>(() => h.Workspace.CreateProjectAsync("other", default));
        await Assert.ThrowsAsync<WorkspaceException>(() => h.Workspace.DeleteProjectAsync(h.Project.Id, default));
        Assert.Equal(0, h.Repo.OpenCalls); Assert.Equal(0, h.Repo.Creates); Assert.Equal(0, h.Repo.Deletes);
        h.Lock.AcquireHold?.TrySetResult(); h.Repo.BeforeWrite?.TrySetResult(); h.Lock.ReleaseHold?.TrySetResult();
        Assert.Equal(ProjectRenameCode.Applied, (await pending.WaitAsync(Bound)).Code); Assert.False(h.Session.Busy);
    });
    [Theory][InlineData(false)][InlineData(true)]
    public void Cancellation_or_disposal_before_acquisition_settlement_cannot_write_or_free_early(bool dispose) => Run(async () =>
    {
        await using var h = new RenameTestHost(); var original = h.Project; h.Lock.AcquireHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Session.Show(h.Project); var proposal = h.Session.CurrentProposal!; var pending = h.Confirm("new"); await h.Lock.AcquireReached.Task.WaitAsync(Bound);
        if (dispose) h.Session.Dispose(); else h.Session.Cancel(proposal);
        Assert.True(h.Workspace.ProjectRenameBusy); Assert.False(pending.IsCompleted);
        h.Lock.AcquireHold.TrySetResult(); Assert.Equal(ProjectRenameCode.Cancelled, (await pending.WaitAsync(Bound)).Code);
        Assert.Equal(original, h.Project); Assert.Equal(0, h.Repo.Renames); Assert.Equal(1, h.Lock.Releases); Assert.False(h.Workspace.ProjectRenameBusy);
    });
    [Theory][InlineData(false)][InlineData(true)]
    public void Transaction_cancellation_preserves_original_before_write_but_not_late_rollback(bool afterWrite) => Run(async () =>
    {
        await using var h = new RenameTestHost(); var original = h.Project;
        if (afterWrite) h.Repo.AfterWrite = new(TaskCreationOptions.RunContinuationsAsynchronously); else h.Repo.BeforeWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Session.Show(h.Project); var proposal = h.Session.CurrentProposal!; var pending = h.Confirm("new");
        await (afterWrite ? h.Repo.Wrote.Task : h.Repo.Reached.Task).WaitAsync(Bound);
        h.Session.Cancel(proposal); Assert.True(h.Session.Busy); Assert.False(pending.IsCompleted);
        h.Repo.AfterWrite?.TrySetResult(); h.Repo.BeforeWrite?.TrySetResult(); var result = await pending.WaitAsync(Bound);
        Assert.Equal(afterWrite ? ProjectRenameCode.Applied : ProjectRenameCode.Cancelled, result.Code);
        Assert.Equal(afterWrite ? "new" : original.Name, h.Project.Name);
        Assert.Equal(afterWrite ? ProjectRenameState.Applied : ProjectRenameState.Cancelled, h.Session.State); Assert.Equal(1, h.Lock.Releases);
    });
    [Theory][InlineData("foreign")][InlineData("lost")][InlineData("unsupported")][InlineData("transaction-error")][InlineData("old-adapter")]
    public void Writer_and_storage_failures_never_claim_success(string reason) => Run(async () =>
    {
        await using var h = new RenameTestHost(); var original = h.Project;
        h.Lock.ForeignId = reason == "foreign"; h.Lock.LoseOwnership = reason == "lost"; h.Lock.Supported = reason != "unsupported";
        h.Repo.FailWrite = reason == "transaction-error"; h.Repo.Unsupported = reason == "old-adapter";
        h.Session.Show(h.Project); var result = await h.Confirm("new").WaitAsync(Bound);
        Assert.False(result.Succeeded); Assert.Equal(original, h.Project); Assert.Equal(0, h.Repo.NameWrites);
        Assert.Equal(reason is "foreign" or "unsupported" ? 0 : 1, h.Lock.Releases); Assert.Equal(0, h.Lock.Waits);
    });
    [Fact] public void Release_failure_does_not_erase_durable_success_and_is_visible() => Run(async () =>
    {
        await using var h = new RenameTestHost(); h.Lock.FailRelease = true; h.Session.Show(h.Project);
        var result = await h.Confirm("durable").WaitAsync(Bound);
        Assert.Equal(ProjectRenameCode.Applied, result.Code); Assert.True(result.WriterReleaseFailed); Assert.Equal("durable", h.Project.Name);
        Assert.Contains("cleanup failed", h.Session.Notice); Assert.False(h.Session.Busy);
    });
    [Fact] public void Existing_open_or_inflight_open_refuses_rename_and_old_dashboard_session_stales() => Run(async () =>
    {
        await using var h = new RenameTestHost(); var expected = ProjectRenameSnapshot.Capture(h.Project); var session = h.Workspace.SessionId;
        h.Session.Show(h.Project); var old = h.Session.CurrentProposal!;
        h.Repo.OpenHold = new(TaskCreationOptions.RunContinuationsAsynchronously); var opening = h.Workspace.OpenAsync(h.Project.Id, default);
        try
        {
            await h.Repo.OpenReached.Task.WaitAsync(Bound);
            Assert.Equal(ProjectRenameCode.Unavailable, (await h.Workspace.RenameProjectAsync(expected, "no", session, default)).Code);
        }
        finally { h.Repo.OpenHold.TrySetResult(); await opening.WaitAsync(Bound); }
        Assert.Equal(ProjectRenameCode.Stale, (await h.Session.ConfirmAsync(old, "no")).Code);
        Assert.False(h.Session.Show(h.Project)); Assert.Equal(0, h.Repo.Renames);
    });
    [Fact] public void Catalog_failure_keeps_durable_result_and_cannot_paint_over_new_proposal() => Run(async () =>
    {
        await using var h = new RenameTestHost(); h.Session.Show(h.Project); var old = h.Session.CurrentProposal!;
        Assert.Equal(ProjectRenameCode.Applied, (await h.Confirm("new")).Code);
        h.Session.CatalogRefreshFailed(old); Assert.Equal(ProjectRenameState.Applied, h.Session.State);
        Assert.Contains("could not be refreshed", h.Session.Notice); Assert.Equal("new", h.Project.Name);
        h.Session.Show(h.Project); var current = h.Session.CurrentProposal!;
        h.Session.CatalogRefreshFailed(old); Assert.Same(current, h.Session.CurrentProposal); Assert.Equal(ProjectRenameState.Ready, h.Session.State);
        Assert.DoesNotContain("could not be refreshed", h.Session.Notice);
        h.Session.Cancel(current);
    });
    [Fact] public void Existing_long_names_are_not_rewritten_or_invalidated_by_show_cancel() => Run(async () =>
    {
        await using var h = new RenameTestHost(); var legacy = h.Project with { Name = new string('Ж', 300) };
        h.Data.Project = legacy; h.Data.Projects[legacy.Id] = (legacy, h.Data.Revision);
        Assert.True(h.Session.Show(legacy)); h.Session.Cancel(h.Session.CurrentProposal!);
        Assert.Equal(legacy, h.Project); Assert.Equal(0, h.Repo.Renames); Assert.Equal(0, h.Lock.Acquires);
        await h.Workspace.OpenAsync(legacy.Id, default); Assert.Equal(legacy.Name, h.Workspace.Project!.Name);
    });
    [Fact] public void Separate_coordinators_cannot_acquire_each_others_project_writer() => Run(async () =>
    {
        var network = new LockNetwork(); await using var first = new RenameTestHost(network); await using var second = new RenameTestHost(network);
        first.Repo.BeforeWrite = new(TaskCreationOptions.RunContinuationsAsynchronously); first.Session.Show(first.Project);
        var pending = first.Confirm("first"); await first.Repo.Reached.Task.WaitAsync(Bound);
        var result = await second.Workspace.RenameProjectAsync(ProjectRenameSnapshot.Capture(first.Project), "second", second.Workspace.SessionId, default);
        Assert.Equal(ProjectRenameCode.WriterUnavailable, result.Code); Assert.Equal(0, second.Lock.Releases); Assert.Equal(0, second.Repo.Renames);
        first.Repo.BeforeWrite.TrySetResult(); Assert.Equal(ProjectRenameCode.Applied, (await pending.WaitAsync(Bound)).Code);
    });
}
