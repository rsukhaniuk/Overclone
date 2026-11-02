using System.Text.RegularExpressions;
using Client.Analysis;
using Client.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Workspace.Tests.ThesisSource;

public sealed class PresentationTests
{
    private static async Task<string> Render(AnalysisReport report,bool current=false)
    {
        using var services=new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer=new HtmlRenderer(services,services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async()=>
            (await renderer.RenderComponentAsync<ThesisReportView>(ParameterView.FromDictionary(new Dictionary<string,object?>{
                [nameof(ThesisReportView.Report)]=report,[nameof(ThesisReportView.IsCurrent)]=current,
            }))).ToHtmlString());
    }
    [Fact]
    public async Task Incomplete_known_findings_and_all_cited_support_cells_are_discoverable()
    {
        var f=ThesisFixture.Load("macro-known-literal-retained");var s=f.Snapshot();
        var result=Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(f.Reply(s,"r"))).Result;
        var html=await Render(new(s,result,"r",7));
        Assert.Equal(5,Regex.Matches(html,"data-testid=\"thesis-rule\"").Count);
        Assert.Equal(31,Regex.Matches(html,"data-testid=\"thesis-catalog-row\"").Count);
        Assert.Contains("Incomplete standards coverage",html);Assert.Contains("KNU Faculty of Computer Science and Cybernetics",html);
        Assert.Contains("not a university-wide standard",html);Assert.Contains("Known literal counterexample",html);
        Assert.Contains("measurement-definition-unresolved",html);Assert.Contains("not measured",html);
        Assert.Contains("Printed",html,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data-testid=\"preview-fix\"",html);Assert.DoesNotContain("data-testid=\"compliant\"",html);
    }
    [Fact]
    public async Task Literal_evidence_is_escaped_and_an_unbound_report_never_enables_navigation()
    {
        var f=ThesisFixture.Load("figure-positive");var s=f.Snapshot();
        var result=Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(f.Reply(s,"r"))).Result;
        var t=result.Thesis!;
        // Intentionally untrusted UI data, not a validated evaluator finding.
        var unsafeData=result with{Thesis=t with{Events=t.Events.SetItem(0,t.Events[0] with{Text="<img src=x onerror=alert(1)>"})}};
        var html=await Render(new(s,unsafeData,"r",7));
        Assert.DoesNotContain("<img",html);Assert.Contains("&lt;img",html);Assert.Contains("disabled",html);
        Assert.Contains("not actionable",html);Assert.DoesNotContain("<script",html);
    }
}
