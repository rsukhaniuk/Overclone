using Client.Editing;
using Client.Editing.Undo;
using Client.Workspace;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.SourceUndo;

// Real coordinator, locks, source tools and save path. DOM callbacks only are
// modeled; these tests do not establish browser keyboard/composition behavior.
public sealed partial class SourceEditJournalTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private sealed class Clock : TimeProvider
    {
        internal long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }
    private sealed class Editor(WorkspaceCoordinator workspace) : ISourceJournalEditor
    {
        public long Intent { get; set; } = 42;
        public bool IsComposing { get; set; }
        internal int Reads, Applies, Selections;
        internal bool RefuseApply;
        internal Action? BeforeApply;
        internal TaskCompletionSource<SourceJournalDom?>? ReadHold;
        internal TaskCompletionSource? SelectHold;
        internal SourceJournalRead? LastRead;
        internal SourceJournalMutation? LastMutation;
        internal SourceJournalSelection? LastSelection;
        internal readonly TaskCompletionSource ReadReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource SelectReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal SourceRange? RestoredSelection;
        public bool BeginHistoryAction() => true;
        internal SourceJournalDom Dom() => new(LastRead!.FileId, LastRead.ExpectedTextareaText,
            new(LastRead.ExpectedTextareaText.Length, LastRead.ExpectedTextareaText.Length), LastRead.Intent);
        public async Task<SourceJournalDom?> ReadAsync(SourceJournalRead request)
        { Reads++; LastRead = request; ReadReached.TrySetResult(); return ReadHold is null ? Dom() : await ReadHold.Task; }
        public bool TryApply(SourceJournalMutation mutation)
        {
            Applies++; LastMutation = mutation; BeforeApply?.Invoke();
            return !RefuseApply && mutation.IsCurrent() && workspace.EditText(mutation.Before.File, mutation.Edit.Text);
        }
        public async Task<bool> SelectAsync(SourceJournalSelection selection)
        {
            Selections++; LastSelection = selection; SelectReached.TrySetResult();
            if (SelectHold is not null) await SelectHold.Task;
            if (!selection.IsCurrent()) return false;
            RestoredSelection = selection.Selection; return true;
        }
    }
    private static SourceJournalRecordResult Record(CompilationTestWorkspace f, SourceEditJournal journal, string after,
        string kind = "typing", SourceRange? beforeSelection = null, SourceRange? afterSelection = null)
    {
        var file = f.Workspace.ActiveFile!;
        var before = beforeSelection ?? new SourceRange(file.Content.Length, file.Content.Length);
        var selection = afterSelection ?? new SourceRange(after.Length, after.Length);
        var lease = journal.BeginEdit(kind, before);
        if (lease is null) { Assert.True(f.Workspace.EditText(file.Id, after)); return new(false, false, journal.Notice); }
        var observed = journal.ObserveEdit(lease, new(after, selection), () => f.Workspace.EditText(file.Id, after));
        // Recording refusal never discards an otherwise valid ordinary edit.
        // A real root additionally rechecks its own DOM/preimage/writer guard.
        if (!observed.CallbackInvoked) Assert.True(f.Workspace.EditText(file.Id, after));
        return journal.CompleteEdit(lease, selection);
    }

    [Fact] public void Typing_coalesces_only_adjacent_collapsed_insertions_and_750ms_boundary() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, "");
        var clock = new Clock(); var editor = new Editor(f.Workspace); using var journal = new SourceEditJournal(f.Workspace, f.Lock, editor, timeProvider: clock);
        Assert.True(Record(f, journal, "a").Recorded); clock.Milliseconds = 750;
        Assert.True(Record(f, journal, "ab").Coalesced); Assert.Equal(1, journal.UnitCount);
        Assert.Equal("", journal.UndoTransaction!.Before); Assert.Equal("ab", journal.UndoTransaction.After);
        clock.Milliseconds = 1501; Assert.False(Record(f, journal, "abc").Coalesced); Assert.Equal(2, journal.UnitCount);
        Assert.True((await journal.UndoAsync()).Applied); Assert.Equal("ab", f.Workspace.ActiveFile!.Content);
        Assert.True((await journal.UndoAsync()).Applied); Assert.Equal("", f.Workspace.ActiveFile.Content);
        Assert.True((await journal.RedoAsync()).Applied); Assert.Equal("ab", f.Workspace.ActiveFile.Content);
        Assert.True(Record(f, journal, "abX", "completion").Recorded); Assert.False(journal.CanRedo);
        Assert.Equal(2, journal.UnitCount); Assert.Equal("completion", journal.UndoTransaction!.Kind);
    });
    [Fact] public void Nonadjacent_selection_replacement_and_explicit_actions_are_separate_units() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, "");
        var editor = new Editor(f.Workspace); using var journal = new SourceEditJournal(f.Workspace, f.Lock, editor);
        Record(f, journal, "ab"); Record(f, journal, "Xab", beforeSelection: new(0, 0), afterSelection: new(1, 1));
        Assert.Equal(2, journal.UnitCount); Record(f, journal, "Yab", beforeSelection: new(0, 1), afterSelection: new(1, 1));
        foreach (string kind in new[] { "lineTool", "completion", "replace" }) Record(f, journal, f.Workspace.ActiveFile!.Content + "!", kind);
        Assert.Equal(6, journal.UnitCount); Assert.Equal("replace", journal.UndoTransaction!.Kind);
        Assert.True((await journal.UndoAsync()).Applied);
    });
    [Fact] public void Completed_composition_is_one_unit_and_never_undoes_or_selects_while_composing() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, "");
        var editor = new Editor(f.Workspace) { IsComposing = true }; using var journal = new SourceEditJournal(f.Workspace, f.Lock, editor);
        var lease = Assert.IsType<SourceEditLease>(journal.BeginEdit("composition", new(0, 0)));
        foreach (var text in new[] { "П", "Привіт", "Привіт😀" })
        {
            Assert.True(journal.ObserveEdit(lease, new(text, new(text.Length, text.Length)), () => f.Workspace.EditText(f.Main, text)).Tracking);
            Assert.False((await journal.UndoAsync()).Applied); Assert.Equal(0, editor.Reads); Assert.Equal(0, editor.Selections);
        }
        Assert.Equal(0, journal.UnitCount); editor.IsComposing = false;
        Assert.True(journal.CompleteEdit(lease, new(8, 8)).Recorded); Assert.Equal(1, journal.UnitCount); Assert.Equal("composition", journal.UndoTransaction!.Kind);
        Assert.True((await journal.UndoAsync()).Applied); Assert.Equal("", f.Workspace.ActiveFile!.Content);
        Assert.True((await journal.RedoAsync()).Applied); Assert.Equal("Привіт😀", f.Workspace.ActiveFile.Content);
    });
    [Fact] public void Save_retains_undo_but_breaks_typing_coalescence_and_undo_uses_fresh_head() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, "");
        var editor = new Editor(f.Workspace); var clock = new Clock(); using var journal = new SourceEditJournal(f.Workspace, f.Lock, editor, timeProvider: clock);
        Record(f, journal, "a"); var oldHead = f.Workspace.CurrentRevision!.Id; await f.Workspace.SaveAsync(default);
        Assert.NotEqual(oldHead, f.Workspace.CurrentRevision!.Id); Assert.True(journal.CanUndo);
        clock.Milliseconds = 1; Assert.False(Record(f, journal, "ab").Coalesced); Assert.Equal(2, journal.UnitCount);
        await f.Workspace.SaveAsync(default); Assert.True((await journal.UndoAsync()).Applied); Assert.Equal("a", f.Workspace.ActiveFile!.Content);
        await f.Workspace.SaveAsync(default); Assert.True(journal.CanRedo); Assert.True((await journal.RedoAsync()).Applied); Assert.Equal("ab", f.Workspace.ActiveFile.Content);
    });
    [Fact] public void File_switch_keeps_stable_file_chains_and_breaks_coalescence() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, "");
        var a = f.Main; var b = f.Workspace.Files.Single(x => x.RelativePath == "chapter.tex").Id;
        var editor = new Editor(f.Workspace); using var journal = new SourceEditJournal(f.Workspace, f.Lock, editor);
        Record(f, journal, "A"); f.Workspace.SelectFile(b); journal.NotifyFileSwitch(); Record(f, journal, "B", "lineTool");
        Assert.True((await journal.UndoAsync()).Applied); Assert.Equal("chapter\r\n", f.Workspace.ActiveFile!.Content);
        f.Workspace.SelectFile(a); journal.NotifyFileSwitch(); Assert.True(journal.CanUndo); Assert.False(Record(f, journal, "AX").Coalesced);
        Assert.True((await journal.UndoAsync()).Applied); Assert.Equal("A", f.Workspace.ActiveFile!.Content);
        Assert.True(journal.CanRedo); // This undo created A's redo; B's older redo was cleared by the new edit.
        f.Workspace.SelectFile(b); journal.NotifyFileSwitch(); Assert.False(journal.CanRedo);
    });
    [Fact] public void Unicode_CRLF_source_selections_and_ordinary_LF_BOM_save_are_separate_from_exact_history_bytes() => Run(async () =>
    {
        const string before = "Україна😀е\u0301\r\n"; await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, before);
        var editor = new Editor(f.Workspace); using var journal = new SourceEditJournal(f.Workspace, f.Lock, editor);
        Record(f, journal, before + "X", "replace"); Assert.True((await journal.UndoAsync()).Applied); Assert.Equal(before, f.Workspace.ActiveFile!.Content);
        Assert.Equal(new SourceRange(before.Length, before.Length), editor.RestoredSelection); Assert.Null(f.Workspace.ActiveFile.PendingTextBytes);
        byte[] bytes = await f.Workspace.GetFileBytesAsync(f.Workspace.ActiveFile, default);
        Assert.Equal(Client.Workspace.Transfer.TextContent.EncodeEdited(before, true), bytes); Assert.Equal(before, f.Workspace.ActiveFile.Content);
    });
}
