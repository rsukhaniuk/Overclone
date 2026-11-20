using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Analysis.RenderedReview;
using Xunit;

namespace Workspace.Tests.RenderedReview;

public sealed class RenderedHeadingPageEndJsonTests
{
    private const string RuleId = "KNU-HEADINGS-RENDERED-001";

    [Theory]
    [InlineData("final", "fail", "confirmed-minor-heading-is-final-page-text-line", 0)]
    [InlineData("earlier", "indeterminate", "conditional-minor-heading-has-following-page-text", 2)]
    [InlineData("missing", "indeterminate", "confirmed-minor-heading-range-required", null)]
    [InlineData("partial", "indeterminate", "minor-heading-not-complete-single-line", null)]
    public void Genuine_wasm_response_replays_upright_page_text(string name, string outcome, string reason, int? following)
    {
        var (request, response) = Fixture(name);
        var result = Assert.Single(RenderedReviewJson.Parse(request, response.ToJsonString()).Results, r => r.RuleId == RuleId);
        Assert.Equal("1.2.0", result.RuleVersion);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(reason, result.Reason);
        if (following is int expected)
        {
            var evidence = Assert.Single(result.Evidence);
            Assert.Equal(expected, evidence.GetProperty("measured").GetInt32());
            Assert.Equal(4, evidence.GetProperty("observation").GetProperty("textLineCount").GetInt32());
            Assert.Equal(JsonValueKind.Null, evidence.GetProperty("sourceLocation").ValueKind);
        }
        else Assert.Empty(result.Evidence);
    }

    [Fact]
    public void Coverage_confirmation_is_required_only_by_the_new_single_page_profile()
    {
        var (request, response) = Fixture("final");
        using var packed = JsonDocument.Parse(RenderedReviewJson.Pack(request));
        Assert.True(packed.RootElement.GetProperty("payload").GetProperty("authorConfirmedPageTextCoverage").GetBoolean());
        var withoutConfirmation = request with { AuthorConfirmedPageTextCoverage = false };
        Assert.False(withoutConfirmation.IsValid);
        Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(withoutConfirmation, response.ToJsonString()));
        Assert.False((request with { PageCount = 2, RequestedPages = [0, 1] }).IsValid);
        foreach (string version in new[] { "1.0.0", "1.2.0", "1.3.0", "1.4.0" })
        {
            Assert.False((request with { ProfileVersion = version }).IsValid);
            var previous = withoutConfirmation with { ProfileVersion = version };
            using var historical = JsonDocument.Parse(RenderedReviewJson.Pack(previous));
            Assert.False(historical.RootElement.GetProperty("payload").TryGetProperty("authorConfirmedPageTextCoverage", out _));
            Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(previous, response.ToJsonString()));
        }
    }

    [Fact]
    public void Forged_coverage_range_counts_neighbor_and_geometry_are_rejected()
    {
        var (request, _) = Fixture("final");
        foreach (Action<JsonNode> forge in new Action<JsonNode>[]
        {
            e => e["characterStart"] = 90,
            e => e["measured"] = 1,
            e => e["roleProvenance"]!["role"] = "headingMajor",
            e => e["observation"]!["authorConfirmedPageTextCoverage"] = false,
            e => e["observation"]!["textLineCount"] = 257,
            e => e["observation"]!["headingLineIndex"] = 2,
            e => e["observation"]!["headingInkErrorRaw"] = 0.0,
            e => e["observation"]!["neighbor"]!["side"] = "following",
            e => e["observation"]!["neighbor"]!["characterEnd"] = 86,
            e => e["observation"]!["neighbor"]!["rawBox"] = e["rawBox"]!.DeepClone(),
            e => e["observation"]!["unprovedGlobalCoverage"] = true
        })
        {
            var (_, response) = Fixture("final");
            var rule = response["payload"]!["results"]!.AsArray().Single(r => (string?)r!["ruleId"] == RuleId)!;
            forge(rule["evidence"]![0]!);
            Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(request, response.ToJsonString()));
        }
    }

    private static (RenderedReviewRequest, JsonNode) Fixture(string name)
    {
        using var stream = typeof(RenderedHeadingPageEndJsonTests).Assembly.GetManifestResourceStream(
            "Workspace.Tests.Fixtures.RenderedReview.producer-heading-page-end-v1_5.json")!;
        var vector = JsonNode.Parse(stream)![name]!;
        var root = vector["request"]!;
        var payload = root["payload"]!;
        var labels = payload["annotations"]!.AsArray().Select(a => new RenderedRoleAnnotation(
            (long)a!["annotationId"]!, (string)a["role"]!, (int)a["pageIndex"]!,
            (int)a["characterStart"]!, (int)a["characterEnd"]!, null, null,
            (string)a["provenance"]!, (string)a["certainty"]!)).ToArray();
        var request = new RenderedReviewRequest((string)root["requestId"]!, (string)payload["pdfSha256"]!,
            (string)payload["compileRequestId"]!, (long)payload["compileEpoch"]!, (string)payload["inputDigest"]!,
            Guid.Parse((string)payload["captureId"]!), (string)payload["workType"]!, (long)payload["annotationVersion"]!,
            (int)payload["pageCount"]!, [0], labels, (string)payload["factsSha256"]!)
        {
            ProfileVersion = RenderedReviewContract.HeadingPageEndProfileVersion,
            AuthorConfirmedPageTextCoverage = true
        };
        return (request, vector["response"]!);
    }
}
