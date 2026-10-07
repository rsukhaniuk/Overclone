using Client;
using Client.Workspace;
using Client.Workspace.Domain;
using Xunit;

namespace Workspace.Tests;

// M2a-2b-2b update-DRAFT preservation: the app-scoped update guard policy never
// lets a waiting service worker activate / the page reload while unsaved work
// would be lost. The pure policy is unit-tested here (the integrated behavior —
// refused reload, retained draft, visible guidance, held-guard stale-approval
// recheck, dirty-on-another-route, clean accept — is covered by
// scripts/test-sw-update-draft.cjs against the published bundle).
public class UpdateGuardPolicyTests
{
    [Fact]
    public void NoOpenProject_IsSafe()
    {
        var d = UpdateGuardPolicy.Decide(isOpen: false, isReadOnly: false,
            SavePhase.Idle, isDirty: false);
        Assert.True(d.Safe);
        Assert.Equal("activate", d.Action);
    }

    [Fact]
    public void ReadOnlyTab_IsSafe_NeverWritesOrTakesLock()
    {
        // A read-only tab has no own dirty draft and reload never writes / takes
        // another tab's lock, so it may update even if (defensively) dirty.
        var d = UpdateGuardPolicy.Decide(isOpen: true, isReadOnly: true,
            SavePhase.Dirty, isDirty: true);
        Assert.True(d.Safe);
        Assert.Equal("activate", d.Action);
    }

    [Fact]
    public void OpenWritableClean_IsSafe()
    {
        var d = UpdateGuardPolicy.Decide(isOpen: true, isReadOnly: false,
            SavePhase.Saved, isDirty: false);
        Assert.True(d.Safe);
    }

    [Fact]
    public void Dirty_RefusesWithSaveAction()
    {
        var d = UpdateGuardPolicy.Decide(isOpen: true, isReadOnly: false,
            SavePhase.Dirty, isDirty: true);
        Assert.False(d.Safe);
        Assert.Equal("save", d.Action);
        Assert.Contains("Save", d.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Saving_RefusesWithRetryAction_NeverReloadMidCommit()
    {
        var d = UpdateGuardPolicy.Decide(isOpen: true, isReadOnly: false,
            SavePhase.Saving, isDirty: false);
        Assert.False(d.Safe);
        Assert.Equal("retry", d.Action);
        Assert.Contains("save", d.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FailedSave_RefusesWithSaveAction_DraftHeadIntact()
    {
        // A prior save failed with unsaved changes still present: refuse so the
        // draft/head are not discarded; the user retries Save first.
        var d = UpdateGuardPolicy.Decide(isOpen: true, isReadOnly: false,
            SavePhase.Failed, isDirty: true);
        Assert.False(d.Safe);
        Assert.Equal("save", d.Action);
        Assert.Contains("failed", d.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SavingTakesPriorityOverDirty()
    {
        // Even if dirty, a save in flight must refuse with "retry" (not "save")
        // so the in-flight commit is not interrupted by a reload.
        var d = UpdateGuardPolicy.Decide(isOpen: true, isReadOnly: false,
            SavePhase.Saving, isDirty: true);
        Assert.False(d.Safe);
        Assert.Equal("retry", d.Action);
    }

    [Fact]
    public void FailedWithNoDirtyFlag_RefusesForSafety()
    {
        // Defensive: a Failed phase without the dirty flag still refuses (never
        // silently allow a reload that could drop a not-yet-recomputed draft).
        var d = UpdateGuardPolicy.Decide(isOpen: true, isReadOnly: false,
            SavePhase.Failed, isDirty: false);
        Assert.False(d.Safe);
    }
}
