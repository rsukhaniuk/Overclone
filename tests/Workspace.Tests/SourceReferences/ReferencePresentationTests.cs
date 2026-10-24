using System.Text.RegularExpressions;
using Client.Analysis;
using Client.Pages;
using Client.Workspace;
using Client.Workspace.Domain;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Workspace.Tests.SourceReferences;

public sealed class ReferencePresentationTests
{
    private static WorkingFile File(string path, string kind = "text", bool deleted = false)
        => new(FileId.New(), path, kind, "", null, false, false, false, false, false, deleted, null, 0, null);

    [Fact]
    public void Explicit_profile_default_is_main_then_active_then_first_without_mutation()
    {
        var first = File("first.tex"); var active = File("active.tex"); var main = File("main.tex");
        var binary = File("binary.tex", "binary"); var deleted = File("deleted.tex", deleted: true);
        WorkingFile[] files = [first, active, main, binary, deleted, File("notes.txt")];
        Assert.Same(main, ReferenceChecks.DefaultRoot(files, active.Id));
        main.Deleted = true; Assert.Same(active, ReferenceChecks.DefaultRoot(files, active.Id));
        Assert.Same(first, ReferenceChecks.DefaultRoot(files, FileId.New()));
        Assert.Null(ReferenceChecks.DefaultRoot([binary, deleted, File("notes.txt")], null));
        Assert.Equal("active.tex", active.RelativePath); Assert.False(active.DirtyContent);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Rendered_section_uses_report_correlation_even_without_findings(bool warning)
    {
        var s = SyntheticReferenceFixture.Snapshot(); var result = SyntheticReferenceFixture.Result(s, warning);
        var report = new AnalysisReport(s, result, "synthetic", 7);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<ReferenceChecksView>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(ReferenceChecksView.Report)] = report }))).ToHtmlString());
        Assert.Contains("data-testid=\"reference-checks\"", html);
        Assert.Contains("data-root-file-id=\"" + result.ReferenceConfiguration!.RootFileId + "\"", html);
        Assert.Contains("data-snapshot-id=\"" + result.SnapshotId + "\"", html);
        Assert.Contains("data-profile-version=\"1.0.0\"", html);
        Assert.Equal(2, Regex.Matches(html, "data-testid=\"reference-rule\"").Count);
        Assert.Contains("data-rule-id=\"ENGINE-LABEL-001\"", html);
        Assert.Contains("data-rule-id=\"ENGINE-REFERENCE-001\"", html);
        Assert.Contains("data-outcome=\"" + (warning ? "Fail" : "NotApplicable") + "\"", html);
        Assert.Contains("Macro expansion", html); Assert.Contains("no PDF or physical layout measurements", html);
        Assert.DoesNotContain("preview-fix", html); Assert.DoesNotContain("review-accept", html);
        Assert.Equal(warning ? 1 : 0, result.Diagnostics.Count);
    }

    [Theory]
    [InlineData("unsupportedContext")][InlineData("nonliteralKey")][InlineData("missingInclude")]
    [InlineData("repeatedInclude")][InlineData("includeCycle")][InlineData("unsupportedIncludeSyntax")]
    [InlineData("budgetExhausted")][InlineData("incompleteScan")]
    public void Limitations_are_safe_finite_display_text(string code)
    {
        var text = ReferenceChecks.Limitation(code); Assert.NotEmpty(text); Assert.NotEqual(code, text);
        Assert.DoesNotContain("untrusted-key", ReferenceChecks.Limitation("untrusted-key"));
    }
}
