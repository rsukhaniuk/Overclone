using Client.Editing;
using Client.Editing.Undo;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.SourceUndo;

public sealed partial class SourceEditJournalTests
{
    [Fact] public void Hundred_complete_units_evict_oldest_whole_unit_with_visible_disclosure() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, "");
        var editor = new Editor(f.Workspace); using var journal = new SourceEditJournal(f.Workspace, f.Lock, editor);
        for (int i = 1; i <= 101; i++)
        {
            Assert.True(Record(f, journal, new string('a', i), "replace").Recorded);
            Assert.InRange(journal.UnitCount, 1, 100); Assert.InRange(journal.RetainedUtf8Bytes, 1, 16L * 1024 * 1024);
        }
        Assert.Equal(100, journal.UnitCount); Assert.True(journal.EvictedUnits > 0); Assert.NotNull(journal.HistoryNotice);
        long retained = journal.RetainedUtf8Bytes;
        for (int i = 0; i < 100; i++) Assert.True((await journal.UndoAsync()).Applied);
        Assert.Equal("a", f.Workspace.ActiveFile!.Content); Assert.False(journal.CanUndo); Assert.True(journal.CanRedo);
        Assert.Equal(retained, journal.RetainedUtf8Bytes); // Moving a cursor neither duplicates nor forgets retained redo text.
    });
    [Fact] public void Utf8_budget_counts_nonBMP_and_combining_text_and_pending_record_reservations() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, "");
        var editor = new Editor(f.Workspace); using var journal = new SourceEditJournal(f.Workspace, f.Lock, editor, new(100, 32));
        Record(f, journal, "😀", "replace"); Assert.Equal(4, journal.RetainedUtf8Bytes);
        Record(f, journal, "😀е\u0301", "replace"); Assert.Equal(16, journal.RetainedUtf8Bytes); // 0+4 and 4+8, not UTF16 length.
        var lease = journal.BeginEdit("composition", new(4, 4)); Assert.NotNull(lease); Assert.InRange(journal.RetainedUtf8Bytes, 1, 32);
        Assert.True(journal.ObserveEdit(lease!, new("😀е\u0301X", new(5, 5)), () => f.Workspace.EditText(f.Main, "😀е\u0301X")).Tracking);
        Assert.InRange(journal.RetainedUtf8Bytes, 1, 32); Assert.True(journal.CompleteEdit(lease!, new(5, 5)).Recorded);
        Assert.InRange(journal.RetainedUtf8Bytes, 1, 32); Assert.True(journal.EvictedUnits > 0); Assert.NotNull(journal.HistoryNotice);
    });
    [Fact] public void Oversized_record_does_not_block_a_valid_ordinary_edit_or_cross_it_with_old_undo() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, "");
        var editor = new Editor(f.Workspace); using var journal = new SourceEditJournal(f.Workspace, f.Lock, editor, new(100, 12));
        Record(f, journal, "abc", "replace"); var lease = journal.BeginEdit("typing", new(3, 3))!;
        bool called = false;
        var observation = journal.ObserveEdit(lease, new("abcdefghijk", new(11, 11)), () => { called = true; return f.Workspace.EditText(f.Main, "abcdefghijk"); });
        Assert.False(observation.Tracking); Assert.False(observation.CallbackInvoked); Assert.False(called);
        Assert.True(f.Workspace.EditText(f.Main, "abcdefghijk")); // Root's still-valid ordinary path, not a journal mutation/fallback Undo.
        Assert.False(journal.CompleteEdit(lease, new(11, 11)).Recorded); Assert.Equal("abcdefghijk", f.Workspace.ActiveFile!.Content);
        Assert.False(journal.CanUndo); Assert.True(journal.UnrecordedEdits > 0); Assert.NotNull(journal.HistoryNotice); Assert.InRange(journal.RetainedUtf8Bytes, 0, 12);
    });
    [Theory][InlineData("surrogate")][InlineData("crlf")][InlineData("invalid-text")]
    public void Recording_refuses_invalid_scalar_or_CRLF_boundaries_before_retaining_a_unit(string invalid) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, "😀\r\n");
        var editor = new Editor(f.Workspace); using var journal = new SourceEditJournal(f.Workspace, f.Lock, editor);
        string before = f.State();
        if (invalid is "surrogate" or "crlf") Assert.Null(journal.BeginEdit("typing", invalid == "surrogate" ? new(1, 1) : new(3, 3)));
        else
        {
            var lease = journal.BeginEdit("typing", new(4, 4))!; bool called = false;
            Assert.False(journal.ObserveEdit(lease, new("\ud800", new(1, 1)), () => { called = true; return true; }).CallbackInvoked); Assert.False(called);
        }
        Assert.Equal(before, f.State()); Assert.Equal(0, journal.UnitCount); Assert.Equal(0, journal.RetainedUtf8Bytes);
    });
    [Fact] public void Lowered_count_budget_and_invalid_raise_are_explicit() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, ""); var editor = new Editor(f.Workspace);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceEditJournal(f.Workspace, f.Lock, editor, new(101)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceEditJournal(f.Workspace, f.Lock, editor, new(100, 16L * 1024 * 1024 + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceEditJournal(f.Workspace, f.Lock, editor, new(0)));
        using var journal = new SourceEditJournal(f.Workspace, f.Lock, editor, new(2, 1024));
        Record(f, journal, "a", "replace"); Record(f, journal, "ab", "replace"); Record(f, journal, "abc", "replace");
        Assert.Equal(2, journal.UnitCount); Assert.True(journal.EvictedUnits > 0);
        Assert.True((await journal.UndoAsync()).Applied); Assert.True((await journal.UndoAsync()).Applied); Assert.Equal("a", f.Workspace.ActiveFile!.Content); Assert.False(journal.CanUndo);
    });
}
