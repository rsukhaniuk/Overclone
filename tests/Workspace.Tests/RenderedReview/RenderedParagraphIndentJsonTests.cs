using System.Text.Json.Nodes;
using Client.Analysis.RenderedReview;
using Xunit;

namespace Workspace.Tests.RenderedReview;

public sealed class RenderedParagraphIndentJsonTests
{
    private const string RuleId = "KNU-PARAGRAPH-RENDERED-001";

    [Theory]
    [InlineData("nominal", "indeterminate", "conditional-paragraph-indent-target-in-interval", 1)]
    [InlineData("short", "fail", "confirmed-paragraph-indent-differs", 1)]
    [InlineData("missing", "indeterminate", "confirmed-paragraph-three-line-range-required", 0)]
    [InlineData("clipped", "indeterminate", "confirmed-paragraph-three-line-range-required", 0)]
    public void Genuine_wasm_response_replays_the_author_confirmed_range(string name, string outcome, string reason, int count)
    {
        var (request, response) = Fixture(name);
        var result = Assert.Single(RenderedReviewJson.Parse(request, response.ToJsonString()).Results, r => r.RuleId == RuleId);
        Assert.Equal("1.1.0", result.RuleVersion);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(count, result.Evidence.Count);
        if (count == 1)
        {
            var evidence = result.Evidence[0];
            Assert.Null(evidence.GetProperty("sourceLocation").GetString());
            Assert.Equal(12.7, evidence.GetProperty("expected").GetDouble());
            Assert.Equal(3, evidence.GetProperty("observation").GetProperty("starts").GetArrayLength());
        }
    }

    [Fact]
    public void Forged_ranges_origins_units_and_proximity_are_rejected()
    {
        var (request, _) = Fixture("short");
        foreach (Action<JsonNode> forge in new Action<JsonNode>[]
        {
            e => e["characterStart"] = 0,
            e => e["expected"] = 6.0,
            e => e["roleProvenance"]!["certainty"] = "tentative",
            e => e["observation"]!["starts"]![1]!["origin"]![0] = 150,
            e => e["observation"]!["userUnit"] = 3,
            e => e["observation"]!["comparison"] = "targetWithinUncertainty",
            e => e["uncertainty"]!["absoluteError"] = 0.0
        })
        {
            var (_, response) = Fixture("short");
            var row = response["payload"]!["results"]!.AsArray().Single(r => (string?)r!["ruleId"] == RuleId)!;
            forge(row["evidence"]![0]!);
            Assert.Throws<System.Text.Json.JsonException>(() => RenderedReviewJson.Parse(request, response.ToJsonString()));
        }
        var (_, valid) = Fixture("short");
        Assert.Throws<System.Text.Json.JsonException>(() => RenderedReviewJson.Parse(
            request with { ProfileVersion = RenderedReviewContract.ProfileVersion }, valid.ToJsonString()));
    }

    private static (RenderedReviewRequest, JsonNode) Fixture(string name)
    {
        using var stream = typeof(RenderedParagraphIndentJsonTests).Assembly.GetManifestResourceStream(
            "Workspace.Tests.Fixtures.RenderedReview.producer-paragraph-v1_4.json")!;
        var vector = JsonNode.Parse(stream)![name]!;
        var root = vector["request"]!;
        var p = root["payload"]!;
        var annotations = p["annotations"]!.AsArray().Select(a => new RenderedRoleAnnotation(
            (long)a!["annotationId"]!, (string)a["role"]!, (int)a["pageIndex"]!, (int)a["characterStart"]!, (int)a["characterEnd"]!,
            null, null, (string)a["provenance"]!, (string)a["certainty"]!)).ToArray();
        var request = new RenderedReviewRequest((string)root["requestId"]!, (string)p["pdfSha256"]!, (string)p["compileRequestId"]!,
            (long)p["compileEpoch"]!, (string)p["inputDigest"]!, Guid.Parse((string)p["captureId"]!), (string)p["workType"]!,
            (long)p["annotationVersion"]!, (int)p["pageCount"]!, [0], annotations, (string)p["factsSha256"]!)
            { ProfileVersion = RenderedReviewContract.ParagraphIndentProfileVersion };
        return (request, vector["response"]!);
    }
}
