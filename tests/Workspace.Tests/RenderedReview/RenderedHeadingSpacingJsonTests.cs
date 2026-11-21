using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Analysis.RenderedReview;
using Xunit;

namespace Workspace.Tests.RenderedReview;

public sealed class RenderedHeadingSpacingJsonTests
{
    private const string RuleId = "KNU-HEADINGS-RENDERED-001";

    [Theory]
    [InlineData("different", "confirmed-heading-baselines-differ-from-local-body", "outsideBothBodyIntervals")]
    [InlineData("same", "conditional-heading-body-baseline-separation-unestablished", "noSeparationEstablished")]
    [InlineData("unevenBody", "heading-spacing-body-reference-unestablished", null)]
    public void Genuine_native_spacing_reports_keep_the_distinction_and_scope(string fixture, string reason, string? comparison)
    {
        var (request, response) = Fixture(fixture);
        var result = Assert.Single(RenderedReviewJson.Parse(request, response.ToJsonString()).Results, r => r.RuleId == RuleId);
        Assert.Equal("1.3.0", result.RuleVersion);
        Assert.Equal("indeterminate", result.Outcome);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(19, result.Citation.GetProperty("printedPages")[0].GetInt32());
        if (comparison is null) Assert.Empty(result.Evidence);
        else
        {
            var evidence = Assert.Single(result.Evidence);
            Assert.Equal(comparison, evidence.GetProperty("observation").GetProperty("comparison").GetString());
            Assert.Equal(2, evidence.GetProperty("observation").GetProperty("bodyIntervals").GetArrayLength());
            Assert.Equal(JsonValueKind.Null, evidence.GetProperty("sourceLocation").ValueKind);
        }
    }

    [Fact]
    public void Refuses_misattributed_or_malformed_interval_and_an_invented_outcome()
    {
        foreach (Action<JsonNode, JsonNode> edit in new Action<JsonNode, JsonNode>[]
        {
            (row, _) => row["outcome"] = "fail",
            (row, _) => row["reason"] = "conditional-heading-body-baseline-separation-unestablished",
            (_, evidence) => evidence["observation"]!["bodyAnnotationId"] = 1,
            (_, evidence) => evidence["observation"]!["bodyCharacterStart"] = 56,
            (_, evidence) => evidence["roleProvenance"]!["role"] = "headingMajor",
            (_, evidence) => evidence["uncertainty"]!["absoluteError"] = 0.0,
            (_, evidence) => evidence["observation"]!["bodyIntervals"]![0]!["measuredMm"] = 0.1,
            (_, evidence) => evidence["observation"]!["comparison"] = "noSeparationEstablished"
        })
        {
            var (request, response) = Fixture("different");
            var row = response["payload"]!["results"]!.AsArray().Single(r => (string?)r!["ruleId"] == RuleId)!;
            edit(row, row["evidence"]![0]!);
            Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(request, response.ToJsonString()));
        }
    }

    [Fact]
    public void One_page_opt_in_uses_existing_request_schema_and_retains_exact_revision_authority()
    {
        var (request, response) = Fixture("different");
        using var packed = JsonDocument.Parse(RenderedReviewJson.Pack(request));
        var payload = packed.RootElement.GetProperty("payload");
        Assert.Equal("1.6.0", payload.GetProperty("profile").GetProperty("version").GetString());
        Assert.False(payload.TryGetProperty("authorConfirmedPageTextCoverage", out _));
        Assert.False((request with { RequestedPages = [0, 1] }).IsValid);
        Assert.False((request with { AuthorConfirmedPageTextCoverage = true }).IsValid);
        Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(request with { AnnotationVersion = request.AnnotationVersion + 1 },
            response.ToJsonString()));
        Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(request with { ProfileVersion = RenderedReviewContract.ProfileVersion },
            response.ToJsonString()));
    }

    private static (RenderedReviewRequest, JsonNode) Fixture(string name)
    {
        using var stream = typeof(RenderedHeadingSpacingJsonTests).Assembly.GetManifestResourceStream(
            "Workspace.Tests.Fixtures.RenderedReview.producer-heading-spacing-v1_6.json")!;
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
            (int)payload["pageCount"]!, payload["requestedPages"]!.AsArray().Select(p => (int)p!).ToArray(), labels, (string)payload["factsSha256"]!)
        {
            ProfileVersion = RenderedReviewContract.HeadingSpacingProfileVersion
        };
        return (request, vector["response"]!);
    }
}
