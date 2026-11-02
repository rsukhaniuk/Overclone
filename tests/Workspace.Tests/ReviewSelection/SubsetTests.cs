using Client.Analysis;
using Client.Review;
using Client.Workspace.Domain;
using Client.Workspace.Patching;
using Xunit;
using static Workspace.Tests.TextPatchPreparationTests;
using static Workspace.Tests.ReviewSelection.SubsetHost;

namespace Workspace.Tests.ReviewSelection;

public class SubsetTests
{
    [Theory][InlineData(TextPatchOrigin.User)][InlineData(TextPatchOrigin.Ai)]
    public async Task Noncontiguous_exact_edits_new_preview_decline_atomic_accept_save_export_undo(TextPatchOrigin origin)
    {
        await using var h = await Open(); var set = await h.Proposal(origin);
        var p = (await h.Service.PreviewAsync(set, default)).Preview!;
        var before = h.F.State(); var head = h.F.C.CurrentRevision!.Id;
        var choices = Select(p, (1, 1), (0, 2), (0, 0));
        var r = await h.Service.PreviewSubsetAsync(p.Id, choices, default); var q = Assert.IsType<ReviewPreview>(r.Preview);
        Assert.NotEqual(p.Id, q.Id); Assert.NotEqual(p.Proposal.Id, q.Proposal.Id); Assert.NotSame(p.Prepared, q.Prepared);
        Assert.Equal(origin, q.Proposal.Origin); Assert.Equal(p.Binding, q.Binding);
        Assert.Equal(2, q.Prepared.Files[0].Edits.Length); Assert.Single(q.Prepared.Files[1].Edits);
        Assert.Equal("l", System.Text.Encoding.UTF8.GetString(p.Proposal.Files[0].Edits[1].Expected.Span));
        Assert.Contains("left", q.Prepared.Files[0].After.Content); Assert.StartsWith("second", q.Prepared.Files[1].After.Content);
        Assert.Contains("$1\\literal🙂\r\n", q.Prepared.Files[0].After.Content); Assert.True(q.Prepared.Files[0].After.HasBom);
        Assert.Equal(before, h.F.State()); Assert.Equal(0, h.F.Repo.Commits); Assert.Equal(head, h.F.C.CurrentRevision.Id);
        Assert.Null(h.Service.Decline(q.Id).Failure); Assert.Equal(before, h.F.State());
        p = (await h.Service.PreviewAsync(await h.Proposal(origin), default)).Preview!; q = (await h.Service.PreviewSubsetAsync(p.Id, Select(p, (0, 0), (0, 2), (1, 1)), default)).Preview!;
        var oldBinary = h.F.Repo.Revision.Files.Single(f => f.MediaKind == "binary"); var binary = h.F.Repo.Content[oldBinary.ContentHash].ToArray();
        var applied = await h.Service.AcceptAsync(q.Id, default); Assert.True(applied.Changed); Assert.Equal(ReviewStatus.AppliedSaved, applied.Status);
        foreach (var f in q.Prepared.Files) Assert.Equal(f.After.ToArray(), await h.F.C.GetFileBytesAsync(h.F.C.Files.Single(w => w.Id == f.FileId), default));
        Assert.Equal(binary, h.F.Repo.Content[oldBinary.ContentHash]); Assert.Equal(0, h.F.Repo.BinaryReads);
        var export = await h.F.C.CaptureExportSourceAsync(default);
        foreach (var f in q.Prepared.Files) Assert.Equal(f.After.ToArray(), await export.ReadContent(f.After.Hash, default));
        Assert.Equal(ReviewStatus.UndoSaved, (await h.Service.UndoAsync(applied.ActionId!.Value, default)).Status);
        foreach (var f in q.Prepared.Files) Assert.Equal(f.Before.ToArray(), await h.F.C.GetFileBytesAsync(h.F.C.Files.Single(w => w.Id == f.FileId), default));
    }

    [Fact] public async Task Selection_order_and_caller_or_exposed_arrays_cannot_change_admitted_bytes()
    {
        await using var h = await Open(); var p = await h.Preview();
        var array = new[] { new ReviewSelectedEdit(p.Proposal.Files[1].FileId, 1), new ReviewSelectedEdit(p.Proposal.Files[0].FileId, 0) };
        var s = new ReviewSubsetSelection(p.Proposal.Id, array); array[0] = new(FileId.New(), -1);
        var q = (await h.Service.PreviewSubsetAsync(p.Id, s, default)).Preview!;
        var expected = q.Prepared.Files.Select(f => f.After.ToArray()).ToArray();
        var bytes = q.Proposal.Files[0].Edits[0].Replacement.ToArray(); Array.Fill(bytes, (byte)0);
        var copy = s.Edits.ToArray(); copy[0] = new(FileId.New(), 999);
        var again = (await h.Service.PreviewAsync(p.Proposal, default)).Preview!;
        var second = (await h.Service.PreviewSubsetAsync(again.Id, Select(again, (0, 0), (1, 1)), default)).Preview!;
        Assert.Equal(expected.Length, second.Prepared.Files.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], second.Prepared.Files[i].After.ToArray());
    }

    [Theory][InlineData("empty")][InlineData("duplicate")][InlineData("negative")][InlineData("outside")][InlineData("unknown")][InlineData("old-preview")][InlineData("old-proposal")][InlineData("budget")]
    public async Task Invalid_choices_refuse_without_relocating_or_replacing_the_current_preview(string kind)
    {
        await using var h = await Open(); var p = await h.Preview(); var id = p.Proposal.Files[0].FileId;
        IReadOnlyList<ReviewSelectedEdit> keys = kind switch {
            "empty" => [], "duplicate" => [new(id, 0), new(id, 0)], "negative" => [new(id, -1)],
            "outside" => [new(id, 999)], "unknown" => [new(FileId.New(), 0)], "budget" => new Oversized(), _ => [new(id, 0)] };
        var s = new ReviewSubsetSelection(kind == "old-proposal" ? Guid.NewGuid() : p.Proposal.Id, keys);
        var before = h.F.State(); var r = await h.Service.PreviewSubsetAsync(kind == "old-preview" ? Guid.NewGuid() : p.Id, s, default);
        Assert.NotNull(r.Failure); Assert.Null(r.Preview); Assert.Same(p, h.Service.State.Preview); Assert.Equal(before, h.F.State()); Assert.Equal(0, h.F.Repo.Commits);
    }
    private sealed class Oversized : IReadOnlyList<ReviewSelectedEdit>
    {
        public int Count => ReviewSubsetSelection.MaxSelectedEdits + 1;
        public ReviewSelectedEdit this[int index] => throw new InvalidOperationException("must preflight before indexing");
        public IEnumerator<ReviewSelectedEdit> GetEnumerator() => throw new InvalidOperationException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory][InlineData("edit")][InlineData("other")][InlineData("edit-aba")][InlineData("head")][InlineData("profile")][InlineData("work-aba")][InlineData("root-aba")][InlineData("open")][InlineData("readonly")][InlineData("lock")][InlineData("cancel")][InlineData("token")][InlineData("dispose")][InlineData("new-preview")]
    public async Task Held_actual_reads_revoke_without_rebasing_and_keep_admission_until_real_settlement(string change)
    {
        await using var h = await Open(); var p = await h.Preview(); var set = p.Proposal; var before = h.F.State();
        h.Repo.Hold = true; using var cts = new CancellationTokenSource();
        var pending = h.Service.PreviewSubsetAsync(p.Id, Select(p, (0, 0)), cts.Token);
        await h.Repo.Reached.Task.WaitAsync(Bound);
        var first = h.F.C.Files.First(f => f.MediaKind == "text");
        switch (change)
        {
            case "edit": h.F.C.EditText(first.Id, "new text"); break;
            case "other": h.F.C.EditText(h.F.C.Files.Last(f => f.MediaKind == "text").Id, "other changed"); break;
            case "edit-aba": var text = first.Content; h.F.C.EditText(first.Id, "new"); h.F.C.EditText(first.Id, text); break;
            case "head": h.F.Repo.AdvanceHead(); break;
            case "profile": h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.BachelorQualification)); break;
            case "work-aba": h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.Coursework)); h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.BachelorQualification)); h.Analysis.SetSelection(AnalysisSelection.EngineStructural()); break;
            case "root-aba": h.Analysis.SetSelection(AnalysisSelection.SourceReferences(first.Id.ToString())); h.Analysis.SetSelection(AnalysisSelection.SourceReferences(h.F.C.Files.Last(f => f.MediaKind == "text").Id.ToString())); h.Analysis.SetSelection(AnalysisSelection.EngineStructural()); break;
            case "open": await h.F.C.OpenAsync(h.F.Repo.Project.Id, default); break;
            case "readonly": h.F.Locks.Acquire = false; await h.F.C.OpenAsync(h.F.Repo.Project.Id, default); break;
            case "lock": h.F.Locks.Owned = false; break;
            case "dispose": await h.Service.DisposeAsync(); break;
            case "token": cts.Cancel(); break;
            default: h.Service.Cancel(); break;
        }
        Assert.True(h.Service.State.Busy); Assert.False(pending.IsCompleted);
        Assert.Equal(change == "dispose" ? ReviewCode.Disposed : ReviewCode.Busy, (await h.Service.PreviewAsync(set, default)).Failure!.Code);
        h.Repo.Hold = false; h.Repo.Release.TrySetResult(); var result = await pending.WaitAsync(Bound);
        Assert.Null(result.Preview); Assert.NotNull(result.Failure); Assert.False(h.Service.State.Busy); Assert.Equal(0, h.F.Repo.Commits);
        if (change is "cancel" or "token" or "new-preview") Assert.Equal(before, h.F.State());
        if (change == "new-preview")
        { var newer = (await h.Service.PreviewAsync(set, default)).Preview!; Assert.NotNull(newer); Assert.Same(newer, h.Service.State.Preview); }
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task Recovery_pending_save_inverse_retry_export_and_only_one_undo_entry_survive(bool undoBeforeRetry)
    {
        await using var h = await Open(); var p = await h.Preview(); var q = (await h.Service.PreviewSubsetAsync(p.Id, Select(p, (0, 0)), default)).Preview!;
        h.F.Repo.FailSave = true; var applied = await h.Service.AcceptAsync(q.Id, default); Assert.True(applied.Changed);
        var action = h.Service.State.Action!; Assert.False(action.Persisted); Assert.True(action.CanUndo);
        var another = await h.Preview(); var subset = await h.Service.PreviewSubsetAsync(another.Id, Select(another, (1, 1)), default);
        Assert.NotNull(subset.Preview); Assert.Equal(action.Id, h.Service.State.Action!.Id);
        Assert.Equal(ReviewCode.PendingSave, (await h.Service.AcceptAsync(subset.Preview!.Id, default)).Failure!.Code);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.F.C.CaptureExportSourceAsync(default));
        if (undoBeforeRetry) Assert.True((await h.Service.UndoAsync(action.Id, default)).Changed);
        h.F.Repo.FailSave = false; var generation = h.F.C.WorkingGeneration;
        Assert.False((await h.Service.RetrySaveAsync(action.Id, default)).Changed); Assert.Equal(generation, h.F.C.WorkingGeneration);
        Assert.True(h.Service.State.Action!.Persisted); await h.F.C.CaptureExportSourceAsync(default);
        if (!undoBeforeRetry) Assert.True((await h.Service.UndoAsync(action.Id, default)).Changed);
        Assert.Equal(q.Prepared.Files[0].Before.ToArray(), await h.F.C.GetFileBytesAsync(h.F.C.Files.Single(w => w.Id == q.Prepared.Files[0].FileId), default));
        Assert.Equal(ReviewCode.Consumed, (await h.Service.UndoAsync(action.Id, default)).Failure!.Code);
    }

    [Theory][InlineData("cancel")][InlineData("edit")][InlineData("replace")]
    public async Task Idle_notification_cannot_return_a_stale_subset_or_displace_a_newer_preview(string change)
    {
        await using var h = await Open(); var p = await h.Preview(); bool once = false; ReviewPreview? newer = null;
        void Observe(ReviewServiceState s)
        {
            if (once || s.Busy || s.Preview is not { } q || q.Id == p.Id) return;
            once = true;
            if (change == "cancel") h.Service.Cancel();
            else if (change == "edit") h.F.C.EditText(h.F.C.Files[0].Id, "changed at notification");
            else { var task = h.Service.PreviewAsync(p.Proposal, default); if (task.IsCompletedSuccessfully) newer = task.Result.Preview; }
        }
        h.Service.Changed += Observe;
        var r = await h.Service.PreviewSubsetAsync(p.Id, Select(p, (0, 0)), default);
        h.Service.Changed -= Observe; Assert.True(once); Assert.Null(r.Preview); Assert.NotNull(r.Failure);
        if (newer is not null) Assert.Same(newer, h.Service.State.Preview);
        Assert.Equal(0, h.F.Repo.Commits);
    }
}
