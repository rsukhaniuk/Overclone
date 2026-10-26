using System.Collections.Immutable;
using Client.Analysis;
using Client.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Workspace.Tests;

// Actual Razor static rendering of Rust-generated facts, not browser acceptance.
public sealed class KnuReportPresentationTests
{
    private static AnalysisReport Report(string fixtureName)
    {
        var fixture = KnuFixture.Load(fixtureName);
        var response = Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(fixture.Json));
        return new(fixture.Snapshot(), response.Result, response.RequestId, 1);
    }

    private static async Task<string> Render(AnalysisReport report)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<KnuReportView>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(KnuReportView.Report)] = report }))).ToHtmlString());
    }

    [Fact]
    public async Task Known_margins_show_each_friendly_side_exact_rational_and_original_unit()
    {
        var html = await Render(Report("geometry-positive"));
        Assert.Contains("Observed left margin: 250/10 mm (original unit cm).", html);
        Assert.Contains("Observed right margin: 10/1 mm (original unit cm).", html);
        Assert.Contains("Observed top margin: 20/1 mm (original unit cm).", html);
        Assert.Contains("Observed bottom margin: 20/1 mm (original unit mm).", html);
        Assert.DoesNotContain("Actual value is unknown", html);
        Assert.Contains("Observed literal length: 1270/100 mm (original unit cm).", html);
        Assert.Contains("Observed heading final scalar:", html);
        Assert.Equal(6, html.Split("data-testid=\"knu-actual\"").Length - 1);
        Assert.Contains("data-testid=\"knu-excerpt\"", html);
    }

    [Fact]
    public async Task Unsupported_margin_length_remains_explicitly_unknown_without_hiding_known_sides()
    {
        var html = await Render(Report("geometry-unsupported-length"));
        Assert.Contains("Actual value is unknown; no macro expansion or guessed value.", html);
        Assert.DoesNotContain("Observed top margin:", html);
        Assert.Contains("Observed left margin: 25/1 mm (original unit mm).", html);
        Assert.Contains("Observed right margin: 10/1 mm (original unit mm).", html);
        Assert.Contains("Observed bottom margin: 20/1 mm (original unit mm).", html);
        Assert.Contains("data-testid=\"knu-indeterminate\"", html);
    }

    [Fact]
    public async Task Margin_branch_preserves_Razor_encoding_even_for_untrusted_display_text()
    {
        var report = Report("geometry-positive");
        // Direct display-boundary defense test; this deliberately invalid unit
        // would be rejected by wire validation before a real report is published.
        var knu = report.Result.Knu!;
        var file = knu.Files[0];
        var observations = file.Observations.Select(o => o.Value is AnalysisMarginValueDto { Length: { } length } margin
            ? o with { Excerpt = "<script>source</script>", Value = margin with { Length = length with { Unit = "<script>unit</script>" } } }
            : o).ToImmutableArray();
        report = report with { Result = report.Result with { Knu = knu with { Files = knu.Files.SetItem(0, file with { Observations = observations }) } } };
        var html = await Render(report);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;source&lt;/script&gt;", html);
        Assert.Contains("original unit &lt;script&gt;unit&lt;/script&gt;", html);
        Assert.Contains("Observed left margin: 250/10 mm", html);
    }

    [Theory]
    [InlineData(AnalysisMarginSide.Left, "left")]
    [InlineData(AnalysisMarginSide.Right, "right")]
    [InlineData(AnalysisMarginSide.Top, "top")]
    [InlineData(AnalysisMarginSide.Bottom, "bottom")]
    [InlineData((AnalysisMarginSide)999, "unknown side")]
    public void Margin_side_labels_are_finite_display_only(AnalysisMarginSide side, string expected)
        => Assert.Equal(expected, AnalysisPresentation.MarginSide(side));
}
