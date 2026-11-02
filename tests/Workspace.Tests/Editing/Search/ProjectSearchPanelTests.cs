using Client.Editing.Search;
using Client.Pages;
using Client.Workspace;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using HistoryHost = global::Workspace.Tests.HistoryComparison.HistoryTestHost;

namespace Workspace.Tests.Editing.Search;

// Static real Blazor rendering only. Root owns actual events, focus/navigation,
// browser timer heartbeat, downloads, and cold-offline acceptance.
public sealed class ProjectSearchPanelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Panel_preserves_stable_navigation_selectors_escapes_text_and_discloses_writer_requirement(bool readOnly)
    {
        await using var h = await HistoryHost.Open(readOnly);
        if (!readOnly) h.Workspace.EditText(h.Main, "<script>alert(1)</script> new🙂");
        using var search = new ProjectSearchSession(h.Workspace); search.Search("new");
        string state = h.State();
        using var services = Services(h);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        string html = await Render(renderer, search);
        Assert.Contains("project-search-query", html); Assert.Contains("project-search-case", html);
        Assert.Contains("project-search-submit", html); Assert.Contains("project-search-cancel", html);
        Assert.Contains("project-search-result", html); Assert.Contains("project-replace-preview", html);
        Assert.Contains("project-replace-match", html); Assert.Contains("fresh exact review", html);
        if (readOnly) Assert.Contains("Read-only/recovery", html);
        else { Assert.Contains("&lt;script&gt;", html); Assert.DoesNotContain("<script>", html); }
        Assert.Equal(state, h.State()); Assert.Null(h.Review.State.Preview);
    }

    [Fact]
    public async Task Incomplete_listing_exposes_selected_subset_but_not_select_all_complete()
    {
        await using var h = await HistoryHost.Open(); h.Workspace.EditText(h.Main, new string('x', 101));
        using var search = new ProjectSearchSession(h.Workspace); Assert.Equal(ProjectSearchStatus.Truncated, search.Search("x").Status);
        using var services = Services(h); await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        string html = await Render(renderer, search);
        Assert.Contains("project-replace-incomplete", html); Assert.Contains("project-replace-listed", html);
        Assert.DoesNotContain("data-testid=\"project-replace-all\"", html);
        Assert.Contains("not Replace all", html); Assert.Null(h.Review.State.Preview);
    }
    private static ServiceProvider Services(HistoryHost h) => new ServiceCollection().AddLogging()
        .AddSingleton(h.Workspace).AddSingleton<IWorkspaceLocks>(h.Locks).AddSingleton<IProjectRepository>(h.Repo).BuildServiceProvider();
    private static Task<string> Render(HtmlRenderer renderer, ProjectSearchSession search) => renderer.Dispatcher.InvokeAsync(async () =>
    {
        var rendered = await renderer.RenderComponentAsync<ProjectSearchPanel>(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["Session"] = search,
            ["OnNavigate"] = EventCallback.Factory.Create<ProjectSearchNavigationRequest>(new object(), (ProjectSearchNavigationRequest _) => Task.CompletedTask),
            ["OnPreviewRequested"] = EventCallback.Factory.Create<ProjectReplaceRequest>(new object(), (ProjectReplaceRequest _) => Task.CompletedTask)
        }));
        return rendered.ToHtmlString();
    });
}
