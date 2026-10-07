using Client.Workspace;
using Client.Workspace.Domain;

namespace Client;

// Result of the app-scoped service-worker UPDATE safety guard. The browser
// banner forwards the user's "Reload to update" click to the C# guard
// (`UpdateGuard.razor`, registered once at app root); C# owns the document/save
// policy and never lets a waiting worker activate / the page reload while unsaved
// work would be lost. `Action` is a stable UI hint ("save" / "retry" / "activate")
// so the banner can show actionable guidance instead of silently dropping a
// draft. The guard refuses conservatively; the user's existing Save action makes
// a draft durable, after which the user clicks update again.
public sealed record UpdateGuardDecision(bool Safe, string Message, string Action)
{
    public static UpdateGuardDecision SafeActivate() => new(true, "", "activate");

    public static UpdateGuardDecision Refuse(string message, string action) =>
        new(false, message, action);
}

// Pure, synchronous decision over a snapshot of workspace state. Extracted so the
// policy is unit-testable without constructing the Blazor component or DI. The
// `UpdateGuard` component reads the live `WorkspaceCoordinator` and delegates
// here; the banner RECHECKS before the actual reload (controllerchange and
// timeout fallback) so an edit landing during the activation wait is still
// caught by a fresh call to this policy.
public static class UpdateGuardPolicy
{
    // Conservative refusal. Never activate / reload while unsaved work would be
    // lost:
    //  - No open project, or a READ-ONLY tab (no own dirty draft; reload never
    //    writes or takes another tab's lock): safe to update.
    //  - A save is in flight: refuse (retry) — never reload mid-commit.
    //  - A prior save failed with unsaved changes: refuse (save) — draft/head
    //    stay intact for retry.
    //  - Any dirty working file: refuse (save) — never drop a draft.
    //  - Otherwise (open, writable, clean): safe to update.
    public static UpdateGuardDecision Decide(
        bool isOpen,
        bool isReadOnly,
        SavePhase savePhase,
        bool isDirty)
    {
        if (!isOpen || isReadOnly)
            return UpdateGuardDecision.SafeActivate();

        if (savePhase == SavePhase.Saving)
            return UpdateGuardDecision.Refuse(
                "A save is in progress. Wait for it to finish, then reload to update.",
                "retry");

        if (savePhase == SavePhase.Failed)
            return UpdateGuardDecision.Refuse(
                "A save failed. Save your changes, then reload to update.",
                "save");

        if (isDirty)
            return UpdateGuardDecision.Refuse(
                "You have unsaved changes. Save them, then reload to update.",
                "save");

        return UpdateGuardDecision.SafeActivate();
    }
}
