using Client.Compilation.SyncTeX;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;
using static Workspace.Tests.SyncTeXNavigation.NavigationFixture;

namespace Workspace.Tests.SyncTeXNavigation;

public sealed class ToolReadinessNavigationTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public void Explicit_choices_made_while_tools_are_unavailable_compile_after_readiness_recovers(bool readOnly) => Run(async () =>
    {
        await using var f = await Open(readOnly);
        f.Config.Ready = false; await f.Session.RefreshToolsAsync();
        f.Session.SelectMain(f.Main); f.Session.SelectEngine("contract");
        await f.Session.CompileAsync(); Assert.Empty(f.Config.Workers.Created);
        f.Config.Ready = true;
        var output = await f.Compile();
        Assert.True(f.Session.State!.IsCurrent); Assert.Same(output, f.Session.DisplayedOutput);
        Assert.NotNull(await f.Policy.InverseAsync(1, 14, 20));
    });

    [Theory][InlineData("main")][InlineData("engine")]
    public void Recovering_readiness_never_invents_missing_user_choices(string missing) => Run(async () =>
    {
        await using var f = await Open();
        f.Config.Ready = false; await f.Session.RefreshToolsAsync();
        if (missing == "main") f.Session.SelectMain(null); else f.Session.SelectEngine("");
        f.Config.Ready = true; await f.Session.CompileAsync();
        Assert.Empty(f.Config.Workers.Created); Assert.Null(f.Session.LastTicket);
    });

    [Theory][InlineData(false)][InlineData(true)]
    public void Recovery_notifications_cannot_rebase_a_compile_over_a_source_or_selection_change(bool selectionAba) => Run(async () =>
    {
        await using var f = await Open();
        f.Config.Ready = false; await f.Session.RefreshToolsAsync();
        f.Session.SelectMain(f.Main); f.Session.SelectEngine("contract");
        long version = f.Session.SelectionVersion; bool changed = false;
        void Changed()
        {
            if (changed || f.Session.SelectionVersion == version) return;
            changed = true;
            if (selectionAba) { f.Session.SelectEngine("xelatex"); f.Session.SelectEngine("contract"); }
            else Assert.True(f.Workspace.EditText(f.Main, "newer source"));
        }
        f.Session.Changed += Changed;
        try { f.Config.Ready = true; await f.Session.CompileAsync(); }
        finally { f.Session.Changed -= Changed; }
        Assert.True(changed); Assert.Empty(f.Config.Workers.Created); Assert.Null(f.Session.LastTicket);
    });
}
