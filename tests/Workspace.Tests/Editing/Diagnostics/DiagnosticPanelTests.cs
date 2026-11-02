using Client.Analysis;
using Client.Compilation.Presentation;
using Client.Editing.Diagnostics;
using Client.Pages;
using Client.Workspace;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using static global::Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.Editing.Diagnostics;

// Actual Razor rendering over the real projection; not interactive/native /
// published-browser evidence. Root exercises keyboard/callbacks/cold-offline UI.
public sealed class DiagnosticPanelTests
{
    [Fact] public void Empty_unavailable_filtered_and_stale_states_are_distinct_and_messages_are_plain_text() => Run(async () =>
    {
        await using var h = await DiagnosticTestHost.Open(readOnly: true);
        using var services = Services(h); await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        string empty = await Render(renderer, h.List); Assert.Contains("diagnostic-no-data", empty); Assert.Contains("no report", empty);
        await h.Analyze(); string state = h.Work.State(); int reads = h.Work.Repo.Reads, inspections = h.Inspector.Calls.Count, evaluations = h.Evaluator.Calls;
        string html = await Render(renderer, h.List);
        Assert.Contains("diagnostic-filter-origin", html); Assert.Contains("diagnostic-filter-severity", html);
        Assert.Contains("diagnostic-filter-file", html); Assert.Contains("diagnostic-filter-rule", html); Assert.Contains("diagnostic-filter-currentness", html);
        Assert.Contains("&lt;img", html); Assert.DoesNotContain("<img src=x", html); Assert.Contains("Україна", System.Net.WebUtility.HtmlDecode(html));
        Assert.Contains("diagnostic-source", html); Assert.DoesNotContain("data-testid=\"preview-fix\"", html);
        Assert.Contains("does not prove", html); Assert.Contains("No UserUnit", html);
        h.List.SetFilter(new(DiagnosticOrigin.Rendered)); html = await Render(renderer, h.List); Assert.Contains("diagnostic-empty-filter", html);
        h.List.ResetFilters(); h.Analysis.Cancel(); html = await Render(renderer, h.List);
        Assert.Contains("STALE", html); Assert.DoesNotContain("data-testid=\"diagnostic-source\"", html);
        Assert.Equal(state, h.Work.State()); Assert.Equal(reads, h.Work.Repo.Reads); Assert.Equal(inspections, h.Inspector.Calls.Count); Assert.Equal(evaluations, h.Evaluator.Calls);
    });

    [Fact] public void Large_reports_render_only_the_current_page_with_explicit_continuation() => Run(async () =>
    {
        await using var h = await DiagnosticTestHost.Open(); h.Analyzer.Count = 1000; await h.Analyze();
        using var services = Services(h); await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        string html = await Render(renderer, h.List);
        Assert.Equal(50, html.Split("data-testid=\"diagnostic-row\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("Continue rows", html); Assert.Contains("1001 projected rows", html);
        h.List.Next(); string next = await Render(renderer, h.List); Assert.Equal(50, next.Split("data-testid=\"diagnostic-row\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("Page 2", next); Assert.Equal(1, h.Analyzer.Calls);
    });
    private static ServiceProvider Services(DiagnosticTestHost h) => new ServiceCollection().AddLogging()
        .AddSingleton<WorkspaceCoordinator>(h.Workspace).AddSingleton<AnalysisCoordinator>(h.Analysis)
        .AddSingleton<SourceNavigator>(h.Navigator).AddSingleton<CompileWorkspaceSession>(h.Compile).BuildServiceProvider();
    private static Task<string> Render(HtmlRenderer renderer, DiagnosticListSession session) => renderer.Dispatcher.InvokeAsync(async () =>
    {
        var component = await renderer.RenderComponentAsync<DiagnosticListPanel>(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["Session"] = session,
            ["SourceRequested"] = EventCallback.Factory.Create<DiagnosticSourceRequest>(new object(), (DiagnosticSourceRequest _) => Task.CompletedTask),
            ["CompilerRequested"] = EventCallback.Factory.Create<DiagnosticCompilerRequest>(new object(), (DiagnosticCompilerRequest _) => Task.CompletedTask),
            ["PageRequested"] = EventCallback.Factory.Create<DiagnosticPageRequest>(new object(), (DiagnosticPageRequest _) => Task.CompletedTask)
        }));
        return component.ToHtmlString();
    });
}
