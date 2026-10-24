using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Analysis.RenderedExtent;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.RenderedExtent;

public sealed class RenderedSectionTests
{
    private static JsonObject Fixture(string name)
    {
        using var stream = typeof(RenderedSectionTests).Assembly.GetManifestResourceStream(
            $"Workspace.Tests.Fixtures.RenderedSections.{name}.json")!;
        return (JsonObject)JsonNode.Parse(stream)!;
    }
    private static RenderedExtentRequest Request(JsonNode node)
    {
        var sections = node["sections"]!;
        RenderedPageRange? Range(string key) => sections[key] is { } r ? new(r["start"]!.GetValue<int>(), r["end"]!.GetValue<int>()) : null;
        return new(node["requestId"]!.GetValue<string>(), node["pdfSha256"]!.GetValue<string>(), node["pageCount"]!.GetValue<int>(), node["workType"]!.GetValue<string>(),
            new(Range("introduction"), Range("mainBody"), Range("conclusions"), sections["appendixStartPage"]?.GetValue<int>(), sections["noAppendices"]!.GetValue<bool>()));
    }
    private static string Reply(string fixture, RenderedExtentRequest request)
    {
        var data = Fixture(fixture)["response"]!;
        data["requestId"] = request.RequestId; data["pdfSha256"] = request.PdfSha256;
        return data.ToJsonString(); // Only transport identity changes; Rust observations remain exact.
    }
    [Theory]
    [InlineData("coursework")][InlineData("bachelor")][InlineData("master")][InlineData("unspecified")]
    [InlineData("limits")][InlineData("unknown")][InlineData("no-appendices")]
    public void Actual_Rust_section_fixtures_roundtrip_correlation_and_author_labels(string name)
    {
        var data = Fixture(name); var request = Request(data["request"]!); Assert.True(request.IsValid);
        Assert.True(JsonNode.DeepEquals(data["request"], JsonNode.Parse(RenderedExtentJson.Pack(request))));
        var observation = Assert.IsType<RenderedExtentObservation>(RenderedExtentJson.Parse(request, data["response"]!.ToJsonString()));
        Assert.Equal("2.0.0", observation.ProfileVersion); Assert.NotNull(observation.Sections);
        Assert.Equal(request.Sections!.MainBody?.Count, observation.Sections.MainBody.Pages);
    }
    [Theory]
    [InlineData("labelSource")][InlineData("rangeEcho")][InlineData("count")][InlineData("sectionCount")]
    [InlineData("rule")][InlineData("extra")][InlineData("legacy")][InlineData("citation")]
    [InlineData("duplicate")][InlineData("missing")][InlineData("status")]
    public void Forged_section_response_shape_echo_or_measurement_is_rejected(string fault)
    {
        var data = Fixture("bachelor"); var request = Request(data["request"]!); var r = data["response"]!;
        switch (fault)
        {
            case "labelSource": r["sections"]!["labelsSource"] = "automaticallyVerified"; break;
            case "rangeEcho": r["sections"]!["ranges"]!["mainBody"]!["start"] = 7; break;
            case "count": r["nonAppendixPages"] = 59; break;
            case "sectionCount": r["sections"]!["mainBody"]!["pages"] = 34; break;
            case "rule": r["sections"]!["mainBody"]!["ruleId"] = "foreign"; break;
            case "extra": r["sections"]!["mainBody"]!["fix"] = true; break;
            case "legacy": r["schemaVersion"] = 1; break;
            case "citation": r["printedPage"] = 18; break;
            case "missing": r["sections"]!.AsObject().Remove("conclusions"); break;
            case "status": r["sections"]!["mainBody"]!["status"] = "compliant"; break;
        }
        var json = r.ToJsonString();
        if (fault == "duplicate") json = json.Replace("\"noAppendices\":false", "\"noAppendices\":false,\"noAppendices\":false");
        Assert.Throws<JsonException>(() => RenderedExtentJson.Parse(request, json));
    }
    [Fact]
    public void Invalid_page_labels_cannot_enter_the_policy()
    {
        var q = Request(Fixture("bachelor")["request"]!);
        Assert.False((q with { Sections = q.Sections! with { NoAppendices = true } }).IsValid);
        Assert.False((q with { Sections = q.Sections! with { MainBody = new(5, 40) } }).IsValid);
        Assert.False((q with { PageCount = 39 }).IsValid);
        Assert.Throws<ArgumentException>(() => RenderedExtentJson.Pack(q with { PageCount = 39 }));
    }
    [Fact]
    public void Labels_require_the_exact_current_compile_and_reconfirmation_after_recompile() => Run(async () =>
    {
        await using var f = await RenderedExtentControllerTests.Fixture.Open(); f.Inspector.Pages = 80;
        await f.Compile(); var request = Request(Fixture("bachelor")["request"]!);
        f.Controller.SelectWorkType(request.WorkType); f.Controller.SelectSections(request.Sections);
        f.Evaluator.Json = r => Reply("bachelor", r);
        Assert.True(f.Controller.CanCheck); await f.Check(); Assert.NotNull(f.Controller.Report!.Observation.Sections);
        var calls = f.Evaluator.Calls;
        await f.Compile(); Assert.False(f.Controller.SectionRangesCurrent); Assert.False(f.Controller.CanCheck);
        await f.Check(); Assert.Equal(calls, f.Evaluator.Calls);
        f.Controller.SelectSections(request.Sections); Assert.True(f.Controller.CanCheck);
        await f.Check(); Assert.Equal(calls + 1, f.Evaluator.Calls);
    });
    [Fact]
    public void Outside_actual_measured_PDF_is_rejected_before_policy_dispatch() => Run(async () =>
    {
        await using var f = await RenderedExtentControllerTests.Fixture.Open(); await f.Compile();
        f.Controller.SelectSections(new(new(1, 3), null, null, null, true));
        await f.Check(); Assert.Equal(RenderedExtentCheckState.InvalidRanges, f.Controller.State);
        Assert.Equal(0, f.Evaluator.Calls); Assert.Null(f.Controller.Report);
    });
    [Fact]
    public void Range_A_B_A_during_held_policy_work_cannot_revive_old_report_or_free_admission() => Run(async () =>
    {
        await using var f = await RenderedExtentControllerTests.Fixture.Open(); f.Inspector.Pages = 80; await f.Compile();
        var q = Request(Fixture("bachelor")["request"]!); f.Controller.SelectWorkType(q.WorkType); f.Controller.SelectSections(q.Sections);
        f.Evaluator.Json = r => Reply("bachelor", r); f.Evaluator.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = f.Check(); await f.Evaluator.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Controller.SelectSections(q.Sections! with { Introduction = null }); f.Controller.SelectSections(q.Sections);
        Assert.True(f.Controller.Busy); Assert.False(f.Controller.CanCheck); await f.Controller.CheckAsync(); Assert.Equal(1, f.Evaluator.Calls);
        f.Evaluator.Hold.SetResult(true); await running.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Null(f.Controller.Report);
        Assert.False(f.Controller.Busy);
    });
}
