using Client.Editing;
using Client.Editing.Undo;
using Client.Workspace.Domain;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.SourceUndo;

public sealed partial class SourceEditJournalTests
{
    [Theory][InlineData("text-ABA")][InlineData("file-ABA")][InlineData("rename")][InlineData("delete")]
    [InlineData("head")][InlineData("writer")][InlineData("intent")][InlineData("session")][InlineData("restore")]
    [InlineData("cancel")][InlineData("dispose")][InlineData("DOM")][InlineData("scalar")]
    public void Held_read_rechecks_fresh_authority_and_never_mutates_stale_source(string change) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, "");
        var file = f.Main; var editor = new Editor(f.Workspace) { ReadHold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var journal = new SourceEditJournal(f.Workspace, f.Lock, editor); Record(f, journal, "a", "replace");
        var pending = journal.UndoAsync(); await editor.ReadReached.Task.WaitAsync(Bound);
        switch (change)
        {
            case "text-ABA": f.Workspace.EditText(file, "other"); f.Workspace.EditText(file, "a"); break;
            case "file-ABA":
                var other = f.Workspace.Files.Single(x => x.RelativePath == "chapter.tex").Id;
                f.Workspace.SelectFile(other); journal.NotifyFileSwitch(); f.Workspace.SelectFile(file); journal.NotifyFileSwitch(); break;
            case "rename": f.Workspace.RenameFile(file, "renamed.tex"); break;
            case "delete": f.Workspace.DeleteFile(file); break;
            case "head": await f.Workspace.SaveAsync(default); break;
            case "writer": f.Lock.AllowAcquire = false; break;
            case "intent": editor.Intent++; break;
            case "session":
                var id = f.Workspace.Project!.Id; await f.Workspace.CloseAsync(default); await f.Workspace.OpenAsync(id, default); break;
            case "restore":
                var historical = f.Repo.Revision!.Id; var preview = await f.Workspace.CaptureRestorePreviewAsync(historical, default);
                await f.Workspace.AcceptRestoreAsync(preview, default); break;
            case "cancel": journal.Cancel(); break;
            case "dispose": journal.Dispose(); break;
        }
        Assert.True(journal.Busy); Assert.False(pending.IsCompleted);
        Assert.False((await journal.RedoAsync()).Applied); // No replacement queue while the actual DOM read is held.
        var dom = editor.Dom();
        if (change == "DOM") dom = dom with { Value = "uncommitted DOM value" };
        if (change == "scalar") dom = dom with { Selection = new(-1, 0) };
        string before = f.State(); editor.ReadHold.TrySetResult(dom);
        Assert.False((await pending.WaitAsync(Bound)).Applied); Assert.Equal(before, f.State()); Assert.Equal(0, editor.Applies); Assert.False(journal.Busy);
    });
    [Fact] public void Fresh_root_guard_refuses_writer_loss_between_DOM_read_and_synchronous_apply() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, "");
        var editor = new Editor(f.Workspace); using var journal = new SourceEditJournal(f.Workspace, f.Lock, editor); Record(f, journal, "a");
        editor.BeforeApply = () => f.Lock.AllowAcquire = false; string before = f.State();
        Assert.False((await journal.UndoAsync()).Applied); Assert.Equal(before, f.State()); Assert.False(editor.LastMutation!.IsCurrent());
    });
    [Theory][InlineData("cancel")][InlineData("dispose")][InlineData("edit")][InlineData("intent")]
    public void Applied_undo_is_not_rolled_back_or_called_nonmutating_when_late_selection_stales(string change) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, "");
        var editor = new Editor(f.Workspace) { SelectHold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var journal = new SourceEditJournal(f.Workspace, f.Lock, editor); Record(f, journal, "a");
        var pending = journal.UndoAsync(); await editor.SelectReached.Task.WaitAsync(Bound); Assert.Equal("", f.Workspace.ActiveFile!.Content);
        if (change == "cancel") journal.Cancel(); if (change == "dispose") journal.Dispose();
        if (change == "edit") f.Workspace.EditText(f.Main, "new input"); if (change == "intent") editor.Intent++;
        string before = f.State(); Assert.True(journal.Busy); Assert.False(pending.IsCompleted);
        Assert.False((await journal.UndoAsync()).Applied); editor.SelectHold.TrySetResult();
        var result = await pending.WaitAsync(Bound); Assert.True(result.Applied); Assert.False(result.CaretRestored);
        Assert.Equal(before, f.State()); Assert.False(journal.Busy); Assert.False(editor.LastSelection!.IsCurrent());
    });
    [Fact] public void Own_synchronous_WorkspaceChanged_preserves_units_and_failing_apply_is_not_recorded() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, "");
        var editor = new Editor(f.Workspace); using var journal = new SourceEditJournal(f.Workspace, f.Lock, editor);
        Record(f, journal, "a"); Assert.True(journal.CanUndo); Assert.Equal(1, journal.UnitCount);
        editor.RefuseApply = true; string before = f.State(); Assert.False((await journal.UndoAsync()).Applied); Assert.Equal(before, f.State());
        editor.RefuseApply = false; Assert.True((await journal.UndoAsync()).Applied); Assert.Equal(1, journal.UnitCount); Assert.True(journal.CanRedo);
        Assert.True((await journal.RedoAsync()).Applied); Assert.True(journal.CanUndo); Assert.Equal(1, journal.UnitCount);
    });
    [Fact] public void External_review_patch_replacement_and_same_text_object_restore_clear_only_affected_chains() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, "");
        var a = f.Main; var b = f.Workspace.Files.Single(x => x.RelativePath == "chapter.tex").Id;
        var editor = new Editor(f.Workspace); using var journal = new SourceEditJournal(f.Workspace, f.Lock, editor);
        Record(f, journal, "A"); f.Workspace.SelectFile(b); journal.NotifyFileSwitch(); Record(f, journal, "B", "replace");
        journal.InvalidateFile(b); f.Workspace.EditText(b, "reviewed external patch"); Assert.False(journal.CanUndo);
        f.Workspace.SelectFile(a); journal.NotifyFileSwitch(); Assert.True(journal.CanUndo);
        var index = f.Workspace.Files.FindIndex(x => x.Id == a); f.Workspace.Files[index] = f.Workspace.Files[index].Clone();
        Assert.False(journal.CanUndo); Assert.Equal(0, journal.UnitCount); // Same content hash cannot resurrect the old file-object authority.
    });
    [Fact] public void Aborted_changed_composition_keeps_source_but_does_not_cross_an_unrecorded_edit() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, "");
        var editor = new Editor(f.Workspace); using var journal = new SourceEditJournal(f.Workspace, f.Lock, editor); Record(f, journal, "a");
        editor.IsComposing = true; var lease = journal.BeginEdit("composition", new(1, 1))!;
        Assert.True(journal.ObserveEdit(lease, new("aБ", new(2, 2)), () => f.Workspace.EditText(f.Main, "aБ")).Applied);
        journal.AbortEdit(lease); editor.IsComposing = false; Assert.Equal("aБ", f.Workspace.ActiveFile!.Content);
        Assert.False(journal.CanUndo); Assert.NotNull(journal.HistoryNotice); Assert.Equal(0, journal.RetainedUtf8Bytes);
    });
}
