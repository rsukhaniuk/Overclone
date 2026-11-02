using Client.Editing.Undo;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.SourceUndo;

public sealed partial class SourceEditJournalTests
{
    [Fact] public void Earlier_workspace_view_reads_preserve_exact_own_typing_coalescence_and_undo_redo() => Run(async () =>
    {
        const string prefix = "Typing: ";
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, prefix);
        SourceEditJournal? view = null; int reads = 0;
        // Registration order is essential: actual analysis/UI subscribers predate
        // the journal, and synchronously render these properties inside EditText.
        void EarlierView()
        {
            if (view is null) return;
            reads++; _ = view.CanUndo; _ = view.CanRedo; _ = view.UndoTransaction; _ = view.RedoTransaction;
        }
        f.Workspace.WorkspaceChanged += EarlierView;
        try
        {
            var clock = new Clock(); var editor = new Editor(f.Workspace);
            using var journal = new SourceEditJournal(f.Workspace, f.Lock, editor, timeProvider: clock); view = journal;
            Assert.True(Record(f, journal, prefix + "a").Recorded);
            clock.Milliseconds = 100; Assert.True(Record(f, journal, prefix + "ab").Coalesced);
            clock.Milliseconds = 200; Assert.True(Record(f, journal, prefix + "abc").Coalesced);
            Assert.Equal(1, journal.UnitCount); Assert.Equal(prefix, journal.UndoTransaction!.Before);
            Assert.Equal(prefix + "abc", journal.UndoTransaction.After);
            await f.Workspace.SaveAsync(default); // Actual published failure crossed a new-head save too.
            var undone = await journal.UndoAsync(); Assert.True(undone.Applied); Assert.True(undone.CaretRestored);
            Assert.Equal(prefix, f.Workspace.ActiveFile!.Content); Assert.Equal(1, journal.UnitCount); Assert.True(journal.CanRedo);
            var redone = await journal.RedoAsync(); Assert.True(redone.Applied); Assert.True(redone.CaretRestored);
            Assert.Equal(prefix + "abc", f.Workspace.ActiveFile.Content); Assert.Equal(1, journal.UnitCount); Assert.True(journal.CanUndo);
            Assert.True(reads >= 5); // Three observed native inputs and two real history mutations.
            f.Workspace.EditText(f.Main, "external"); f.Workspace.EditText(f.Main, prefix + "abc");
            Assert.Equal(0, journal.UnitCount); Assert.False(journal.CanUndo); Assert.False(journal.CanRedo);
            Assert.Equal(prefix + "abc", f.Workspace.ActiveFile.Content); // Equal text after external ABA never revives the chain.
        }
        finally { f.Workspace.WorkspaceChanged -= EarlierView; }
    });

    [Fact] public void Earlier_reentrant_unrelated_edit_is_not_mistaken_for_the_exact_own_mutation() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, "");
        var a = f.Main; var b = f.Workspace.Files.Single(x => x.RelativePath == "chapter.tex").Id;
        SourceEditJournal? view = null; bool interrupt = false, injected = false;
        void EarlierView()
        {
            if (view is null) return;
            if (interrupt && !injected && f.Workspace.ActiveFileId == a && f.Workspace.ActiveFile!.Content == "ab")
            {
                injected = true;
                Assert.True(f.Workspace.EditText(b, "external-other-file")); // Second generation step before journal's own notification.
            }
            _ = view.CanUndo; _ = view.CanRedo; _ = view.UndoTransaction;
        }
        f.Workspace.WorkspaceChanged += EarlierView;
        try
        {
            var editor = new Editor(f.Workspace); using var journal = new SourceEditJournal(f.Workspace, f.Lock, editor); view = journal;
            Record(f, journal, "a", "replace"); f.Workspace.SelectFile(b); journal.NotifyFileSwitch(); Record(f, journal, "B", "replace");
            f.Workspace.SelectFile(a); journal.NotifyFileSwitch(); interrupt = true;
            Assert.False(Record(f, journal, "ab").Recorded); Assert.True(injected);
            Assert.Equal("ab", f.Workspace.ActiveFile!.Content); Assert.Equal("external-other-file", f.Workspace.Files.Single(x => x.Id == b).Content);
            Assert.Equal(0, journal.UnitCount); Assert.False(journal.CanUndo); Assert.False(journal.CanRedo);
        }
        finally { f.Workspace.WorkspaceChanged -= EarlierView; }
    });
}
