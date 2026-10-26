using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Analysis;
using Client.Pages;
using Client.Workspace.Domain;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Workspace.Tests.ThesisFormatting;

public sealed class ThesisFormattingContractTests
{
    public static TheoryData<string> Cases => new()
    {
        "a4-half", "letter-dots", "a3-purpose", "geometry-command", "geometry-package",
        "setstretch", "linespread", "renewal", "missing", "dynamic", "repeat", "conflict",
        "caption-optional", "caption-star", "macro-condition", "unknown-environment",
        "comment-verbatim", "include", "unsupported-units", "table-unicode",
    };

    [Theory]
    [InlineData(AnalysisWorkType.Unspecified)][InlineData(AnalysisWorkType.Coursework)]
    [InlineData(AnalysisWorkType.BachelorQualification)][InlineData(AnalysisWorkType.MasterQualification)]
    public void New_selection_is_explicit_and_does_not_replace_old_defaults(AnalysisWorkType work)
    {
        var selection = AnalysisSelection.KnuWithFormatting(work);
        Assert.True(selection.IsValid); Assert.Equal("1.4.0", selection.Profile.Version);
        Assert.Equal(work, selection.Configuration!.WorkType);
        Assert.Equal("1.0.0", AnalysisSelection.Knu(work).Profile.Version);
        Assert.Equal("1.1.0", AnalysisSelection.KnuLatest(work).Profile.Version);
        Assert.Equal("1.2.0", AnalysisSelection.KnuWithMarginFixes(work).Profile.Version);
        Assert.Equal("1.3.0", AnalysisSelection.KnuWithGeometryPackage(work).Profile.Version);
        Assert.False(AnalysisCatalog.IsValid(AnalysisCatalog.KnuId, "1.4.0", null));
        Assert.False(AnalysisCatalog.IsKnown(AnalysisCatalog.KnuId, "1.4.1"));
        Assert.Throws<JsonException>(() => AnalysisJson.ParseConfiguration("{\"workType\":\"coursework\",\"font\":14}"));
    }

    [FormattingTheory, MemberData(nameof(Cases))]
    public async Task Actual_Rust_cases_pass_strict_codec_and_immutable_capture(string name)
    {
        var f = ThesisFormattingFixture.Load(name); var snapshot = f.Snapshot();
        var parsed = Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(f.Json));
        Assert.Equal(7, parsed.Result.RuleResults.Count); Assert.Empty(parsed.Result.Knu!.FixDescriptors);
        Assert.Equal(6, parsed.Result.Knu.Metadata.Rules.Length);
        Assert.Equal("8be4d724ac108488e6b1766322e7fd6b3646412aa2b6de12b6945f48fb380714", parsed.Result.Knu.Metadata.GuidelineSha256);
        foreach (var id in new[] { AnalysisCatalog.Paper, AnalysisCatalog.LineSpacing, AnalysisCatalog.Caption })
        {
            var rule = Assert.Single(parsed.Result.Knu.Metadata.Rules, r => r.RuleId == id);
            Assert.Equal("1.0.0",rule.Version); Assert.Equal("warning",rule.Severity);
            Assert.Equal(AnalysisEvaluationDomain.SourceObservation,rule.EvaluationDomain);
            Assert.Equal(id == AnalysisCatalog.Caption ? "3.7; 3.8" : "3.1",rule.Citation.Section);
            Assert.Equal(id == AnalysisCatalog.Caption ? new[] { 21,22,23 } : new[] { 18 },rule.Citation.PrintedPages);
        }
        var roundtrip = JsonSerializer.SerializeToElement(new AnalysisResponseEnvelope(1, parsed.RequestId, ResponseStatus.Completed, parsed.Result, null), AnalysisJson.Options);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(f.Response),roundtrip));
        var js = new KnuProtocolTests.FixtureJs((_,rid) => f.Reply(snapshot,rid));
        await using var analyzer = new RustSourceAnalyzer(js);
        var completed = Assert.IsType<AnalysisJobResult.Completed>(await analyzer.AnalyzeAsync(snapshot,default));
        Assert.Equal(parsed.Result.AggregateOutcome, completed.Result.AggregateOutcome);
        Assert.Equal(f.Bytes,js.Packed);
    }

    [FormattingTheory]
    [InlineData("metadata")][InlineData("new-field")][InlineData("discriminator")][InlineData("foreign-rule")]
    [InlineData("foreign-method")][InlineData("scalar")][InlineData("span")][InlineData("count")]
    [InlineData("missing-rule")][InlineData("foreign-paper")][InlineData("denominator")][InlineData("missing-numerator")]
    [InlineData("foreign-environment")][InlineData("uncertainty-pass")][InlineData("fix")][InlineData("old-version")]
    public void Forged_metadata_membership_shapes_and_fixes_are_refused(string mutation)
    {
        var f = ThesisFormattingFixture.Load(mutation == "uncertainty-pass" ? "a3-purpose" : "letter-dots");
        var p = f.Response["payload"]!;
        var observations = p["knu"]!["files"]![0]!["observations"]!.AsArray();
        JsonNode Observation(string id) => observations.First(o => o!["ruleId"]!.GetValue<string>() == id)!;
        switch (mutation)
        {
            case "metadata": p["knu"]!["metadata"]!["rules"]![3]!["citation"]!["printedPages"]![0] = 19; break;
            case "new-field": Observation(AnalysisCatalog.Paper)["physicalCompliance"] = true; break;
            case "discriminator": Observation(AnalysisCatalog.Paper)["value"]!["kind"] = "physicalPaper"; break;
            case "foreign-rule": Observation(AnalysisCatalog.Paper)["ruleId"] = AnalysisCatalog.Caption; break;
            case "foreign-method": Observation(AnalysisCatalog.Paper)["method"] = "rendered-paper"; break;
            case "scalar": Observation(AnalysisCatalog.Caption)["value"]!["finalScalar"] = ".."; break;
            case "span": Observation(AnalysisCatalog.Caption)["valueSpan"]!["endByte"] = f.Bytes.Length + 1; break;
            case "count": p["knu"]!["files"]![0]!["observationCount"] = 20001; break;
            case "missing-rule": p["ruleResults"]!.AsArray().RemoveAt(6); break;
            case "foreign-paper": Observation(AnalysisCatalog.Paper)["value"]!["paper"] = "custom"; break;
            case "denominator": Observation(AnalysisCatalog.LineSpacing)["value"]!["denominator"] = 0; break;
            case "missing-numerator": Observation(AnalysisCatalog.LineSpacing)["value"]!.AsObject().Remove("numerator"); break;
            case "foreign-environment": Observation(AnalysisCatalog.Caption)["value"]!["environment"] = "subfigure"; break;
            case "uncertainty-pass":
                var rule = p["ruleResults"]!.AsArray().Single(r => r!["ruleId"]!.GetValue<string>() == AnalysisCatalog.Paper)!;
                rule["outcome"] = "pass"; rule["perFile"]![0]!["outcome"] = "pass"; break;
            case "fix": p["knu"]!["fixDescriptors"]!.AsArray().Add(KnuFixture.Load("negative").Response["payload"]!["knu"]!["fixDescriptors"]![0]!.DeepClone()); break;
            case "old-version": p["profile"]!["version"] = "1.3.0"; break;
        }
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(f.Json));
    }

    [FormattingTheory]
    [InlineData("snapshot")][InlineData("configuration")][InlineData("unicode")][InlineData("scalar-echo")][InlineData("excerpt")]
    public async Task Stale_configuration_and_Unicode_capture_echo_are_refused(string mutation)
    {
        var f = ThesisFormattingFixture.Load("letter-dots"); var original = f.Snapshot(); var snapshot = original;
        string Reply(string rid)
        {
            var raw = JsonNode.Parse(f.Reply(original,rid))!;
            var caption = raw["payload"]!["knu"]!["files"]![0]!["observations"]!.AsArray()
                .First(o => o!["ruleId"]!.GetValue<string>() == AnalysisCatalog.Caption)!;
            if (mutation == "unicode")
            {
                // Make a bounded UTF-8 span split the multibyte scalar at title start.
                var start = caption["valueSpan"]!["startByte"]!.GetValue<int>();
                var oldId = caption["id"]!.GetValue<string>();
                var newId = original.Files[0].FileId + ":" + AnalysisCatalog.Caption + ":" + (start+1);
                caption["valueSpan"]!["startByte"] = start + 1;
                void Rebind(JsonNode? node)
                {
                    if (node is JsonObject obj)
                        foreach (var pair in obj.ToArray())
                        {
                            if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text) && text == oldId) obj[pair.Key] = newId;
                            else Rebind(pair.Value);
                        }
                    else if (node is JsonArray array)
                        for (int i = 0; i < array.Count; i++)
                        {
                            if (array[i] is JsonValue value && value.TryGetValue<string>(out var text) && text == oldId) array[i] = newId;
                            else Rebind(array[i]);
                        }
                }
                Rebind(raw);
            }
            if (mutation == "scalar-echo") caption["value"]!["finalScalar"] = "😀";
            if (mutation == "excerpt") caption["excerpt"] = "Different source";
            return raw.ToJsonString();
        }
        if (mutation == "snapshot") snapshot = original with { Id = AnalysisSnapshotId.New() };
        if (mutation == "configuration") snapshot = original with { Configuration = new(AnalysisWorkType.MasterQualification) };
        var js = new KnuProtocolTests.FixtureJs((_,rid) => Reply(rid)); await using var analyzer = new RustSourceAnalyzer(js);
        Assert.IsType<AnalysisJobResult.Failed>(await analyzer.AnalyzeAsync(snapshot,default));
    }

    [FormattingFact]
    public async Task Real_report_labels_declared_intent_exact_dots_and_no_preview_button()
    {
        var f = ThesisFormattingFixture.Load("letter-dots"); var snapshot = f.Snapshot();
        var parsed = Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(f.Reply(snapshot,"report")));
        foreach (var d in parsed.Result.Diagnostics.Where(d => d.RuleId == AnalysisCatalog.Caption))
        {
            Assert.Equal(1,d.Span!.EndByte-d.Span.StartByte);
            Assert.Equal(".",Encoding.UTF8.GetString(f.Bytes.AsSpan((int)d.Span.StartByte,1)));
        }
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services,services.GetRequiredService<ILoggerFactory>());
        var report = new AnalysisReport(snapshot,parsed.Result,parsed.RequestId,1);
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<KnuReportView>(ParameterView.FromDictionary(new Dictionary<string,object?> { [nameof(KnuReportView.Report)] = report }))).ToHtmlString());
        Assert.Contains("Declared paper intent",html); Assert.Contains("Declared spacing intent",html);
        Assert.Contains("Not measured baseline or glyph geometry",html); Assert.Contains("caption final scalar",html);
        Assert.Contains("no admitted fixes",html); Assert.DoesNotContain("data-testid=\"preview-fix\"",html);
    }

    [Fact]
    public void Existing_profiles_keep_known_fix_descriptors_admissible()
    {
        var legacy = Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(KnuFixture.Load("negative").Json));
        Assert.Equal(AnalysisCatalog.Fix,Assert.Single(legacy.Result.Knu!.FixDescriptors).Id);
        var package = Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(GeometryPackageFixture.Load("left-mm").Json));
        Assert.Equal(AnalysisCatalog.MarginLeftFix,Assert.Single(package.Result.Knu!.FixDescriptors).Id);
    }
}
