using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Analysis;
using Client.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Workspace.Tests;

public sealed class GeometryPackageContractTests
{
    [Theory]
    [InlineData(AnalysisWorkType.Unspecified)][InlineData(AnalysisWorkType.Coursework)]
    [InlineData(AnalysisWorkType.BachelorQualification)][InlineData(AnalysisWorkType.MasterQualification)]
    public async Task Explicit_selection_preserves_old_APIs_and_exact_worktype(AnalysisWorkType work)
    {
        Assert.Equal("1.0.0", AnalysisCatalog.Version); Assert.Equal("1.1.0", AnalysisCatalog.LatestVersion);
        Assert.Equal("1.0.0", AnalysisSelection.Knu(work).Profile.Version);
        Assert.Equal("1.1.0", AnalysisSelection.KnuLatest(work).Profile.Version);
        Assert.Equal("1.2.0", AnalysisSelection.KnuWithMarginFixes(work).Profile.Version);
        var selected = AnalysisSelection.KnuWithGeometryPackage(work); Assert.True(selected.IsValid); Assert.Equal("1.3.0", selected.Profile.Version);
        var fixture = GeometryPackageFixture.Load("left-mm"); var snapshot = fixture.Snapshot() with { Profile = selected.Profile, Configuration = selected.Configuration };
        var (request, packed) = AnalysisRequestPacker.Pack(snapshot); Assert.Equal(fixture.Bytes, packed);
        using var doc = JsonDocument.Parse(request); Assert.Equal("1.3.0", doc.RootElement.GetProperty("payload").GetProperty("profile").GetProperty("version").GetString());
        var js = new KnuProtocolTests.FixtureJs((_, rid) => fixture.Reply(snapshot, rid));
        await using var analyzer = new RustSourceAnalyzer(js);
        var completed = Assert.IsType<AnalysisJobResult.Completed>(await analyzer.AnalyzeAsync(snapshot, default));
        Assert.Equal(work, completed.Result.Configuration!.WorkType); Assert.Equal(selected.Profile.Id, completed.Result.Profile.Id); Assert.Equal(selected.Profile.Version, completed.Result.Profile.Version);
        Assert.Equal(selected.Configuration, Assert.Single(completed.Result.Knu!.FixDescriptors).Configuration);
    }

    [Theory]
    [InlineData("left-mm", AnalysisCatalog.MarginLeftFix, "25mm")]
    [InlineData("command-left", AnalysisCatalog.MarginLeftFix, "25mm")]
    [InlineData("right-cm", AnalysisCatalog.MarginRightFix, "10mm")]
    [InlineData("top-cm", AnalysisCatalog.MarginTopFix, "20mm")]
    [InlineData("bottom-mm", AnalysisCatalog.MarginBottomFix, "20mm")]
    [InlineData("all-below", AnalysisCatalog.MarginLeftFix, "25mm")]
    [InlineData("after-left", AnalysisCatalog.MarginRightFix, "10mm")]
    [InlineData("indent-priority", AnalysisCatalog.Fix, "1.27cm")]
    public async Task Actual_Rust_goldens_roundtrip_exact_metadata_evidence_bytes_and_trusted_preview_UI(string name, string id, string replacement)
    {
        var fixture = GeometryPackageFixture.Load(name); var snapshot = fixture.Snapshot();
        var parsed = Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(fixture.Json));
        var fix = Assert.Single(parsed.Result.Knu!.FixDescriptors); Assert.Equal(id, fix.Id); Assert.Equal(replacement, fix.Replacement);
        Assert.Equal("1.0.0", fix.Version); Assert.Equal(id == AnalysisCatalog.Fix ? "1.0.0" : "1.2.0", fix.RuleVersion);
        var rule = Assert.Single(parsed.Result.Knu.Metadata.Rules, r => r.RuleId == fix.RuleId);
        Assert.Equal(fix.RuleVersion, rule.Version); Assert.Equal("3.1", rule.Citation.Section); Assert.Equal(new[] { 18 }, rule.Citation.PrintedPages);
        var margin = Assert.Single(parsed.Result.Knu.Metadata.Rules, r => r.RuleId == AnalysisCatalog.Margins);
        Assert.Equal("1.2.0",margin.Version);
        Assert.DoesNotContain(margin.Limitations, l => l.Contains("no margin fix", StringComparison.Ordinal));
        Assert.Equal(fix.ExpectedOriginal, Encoding.UTF8.GetString(fixture.Bytes.AsSpan((int)fix.Span.StartByte, (int)(fix.Span.EndByte - fix.Span.StartByte))));
        Assert.True(fixture.Bytes.AsSpan().StartsWith(new byte[] { 239,187,191 })); Assert.Contains("\r\n", Encoding.UTF8.GetString(fixture.Bytes));
        var roundtrip = JsonSerializer.SerializeToElement(new AnalysisResponseEnvelope(1, parsed.RequestId, ResponseStatus.Completed, parsed.Result, null), AnalysisJson.Options);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(fixture.Response), roundtrip));
        var js = new KnuProtocolTests.FixtureJs((_, rid) => fixture.Reply(snapshot, rid));
        await using var analyzer = new RustSourceAnalyzer(js);
        Assert.IsType<AnalysisJobResult.Completed>(await analyzer.AnalyzeAsync(snapshot, default));
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var report = new AnalysisReport(snapshot, parsed.Result, parsed.RequestId, 1);
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<KnuReportView>(ParameterView.FromDictionary(new Dictionary<string,object?> { [nameof(KnuReportView.Report)] = report }))).ToHtmlString());
        Assert.Contains("data-testid=\"preview-fix\"", html); Assert.Contains("data-fix-id=\"" + id + "\"", html);
        Assert.Contains("rendered layout", html);
    }

    [Theory]
    [InlineData("compliant")][InlineData("larger")][InlineData("unknown")][InlineData("duplicate")]
    [InlineData("multiple")][InlineData("diagnostic-budget")][InlineData("unknown-package")][InlineData("dynamic")]
    public async Task No_descriptor_for_compliant_larger_or_uncertain_actual_Rust_output(string name)
    {
        var fixture = GeometryPackageFixture.Load(name); var snapshot = fixture.Snapshot();
        var parsed = Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(fixture.Json)); Assert.Empty(parsed.Result.Knu!.FixDescriptors);
        var js = new KnuProtocolTests.FixtureJs((_, rid) => fixture.Reply(snapshot, rid)); await using var analyzer = new RustSourceAnalyzer(js);
        Assert.IsType<AnalysisJobResult.Completed>(await analyzer.AnalyzeAsync(snapshot, default));
    }

    [Theory]
    [InlineData("id")][InlineData("side")][InlineData("replacement")][InlineData("ruleVersion")][InlineData("oldRuleVersion")][InlineData("span")]
    [InlineData("expected")][InlineData("hash")][InlineData("path")][InlineData("generation")][InlineData("head")]
    [InlineData("configuration")][InlineData("duplicateFix")][InlineData("oldProfile")]
    public async Task Forged_descriptor_or_capture_is_rejected_by_existing_typed_boundary(string kind)
    {
        var fixture = GeometryPackageFixture.Load("left-mm"); var snapshot = fixture.Snapshot(); var p = fixture.Response["payload"]!;
        var fixes = p["knu"]!["fixDescriptors"]!.AsArray(); var f = fixes[0]!;
        switch (kind)
        {
            case "id": f["id"] = AnalysisCatalog.MarginRightFix; break;
            case "side":
                var evidence = p["knu"]!["files"]![0]!["observations"]!.AsArray().Single(o => o!["id"]!.GetValue<string>() == f["evidenceId"]!.GetValue<string>());
                evidence!["value"]!["side"] = "bottom"; break;
            case "replacement": f["replacement"] = "100mm"; break;
            case "ruleVersion": f["ruleVersion"] = "1.0.0"; break;
            case "oldRuleVersion": f["ruleVersion"] = "1.1.0"; break;
            case "span": f["span"]!["startByte"] = 0; break;
            case "expected": f["expectedOriginal"] = "wrong bytes"; break;
            case "hash": f["expectedFileSha256"] = new string('a',64); break;
            case "path": f["relativePath"] = "other.tex"; break;
            case "generation": f["workingGeneration"] = 900; break;
            case "head": f["baseHeadRevisionId"] = "99999999999999999999999999999999"; break;
            case "configuration": f["configuration"]!["workType"] = "coursework"; break;
            case "duplicateFix": fixes.Add(f.DeepClone()); break;
            case "oldProfile":
                var old = KnuFixture.Load("geometry-negative").Response["payload"]!;
                p["profile"]!["version"] = "1.1.0"; f["profile"]!["version"] = "1.1.0";
                p["knu"]!["metadata"] = old["knu"]!["metadata"]!.DeepClone();
                var rule = p["ruleResults"]!.AsArray().Single(r => r!["ruleId"]!.GetValue<string>() == AnalysisCatalog.Margins)!;
                rule["source"] = old["ruleResults"]!.AsArray().Single(r => r!["ruleId"]!.GetValue<string>() == AnalysisCatalog.Margins)!["source"]!.DeepClone(); break;
        }
        var js = new KnuProtocolTests.FixtureJs((_, rid) => { var reply = (JsonObject)fixture.Response.DeepClone(); reply["requestId"] = rid; return reply.ToJsonString(); });
        await using var analyzer = new RustSourceAnalyzer(js);
        Assert.IsType<AnalysisJobResult.Failed>(await analyzer.AnalyzeAsync(snapshot, default));
    }
}
