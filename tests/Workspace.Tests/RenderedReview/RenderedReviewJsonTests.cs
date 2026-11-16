using Client.Analysis.RenderedReview;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Workspace.Tests.RenderedReview;

// Synthetic facts, exact pure-Rust producer response. The hash in this vector
// exercises correlation only; byte/hash integrity is tested by the transport.
public sealed class RenderedReviewJsonTests
{
    private static (RenderedReviewRequest Request, JsonObject Reply) Vector()
    {
        using var stream = typeof(RenderedReviewJsonTests).Assembly.GetManifestResourceStream("Workspace.Tests.Fixtures.RenderedReview.producer-v1.json")!;
        using var doc = JsonDocument.Parse(stream); var envelope = doc.RootElement.GetProperty("request"); var p = envelope.GetProperty("payload");
        var request = new RenderedReviewRequest(envelope.GetProperty("requestId").GetString()!, p.GetProperty("pdfSha256").GetString()!,
            p.GetProperty("compileRequestId").GetString()!, p.GetProperty("compileEpoch").GetInt64(), p.GetProperty("inputDigest").GetString()!,
            p.GetProperty("captureId").GetGuid(), p.GetProperty("workType").GetString()!, p.GetProperty("annotationVersion").GetInt64(), p.GetProperty("pageCount").GetInt32(),
            p.GetProperty("requestedPages").EnumerateArray().Select(v => v.GetInt32()).ToArray(), Array.Empty<RenderedRoleAnnotation>(), p.GetProperty("factsSha256").GetString()!);
        return (request, JsonNode.Parse(doc.RootElement.GetProperty("response").GetRawText())!.AsObject());
    }
    [Fact]
    public void AcceptsActualProducerShapeAndRetainsUnknownCoverage()
    {
        var (request, reply) = Vector(); var observation = RenderedReviewJson.Parse(request, reply.ToJsonString());
        Assert.Equal(13, observation.Results.Count);
        Assert.Equal("pass", observation.Results.Single(r => r.RuleId == "KNU-PAPER-001").Outcome);
        Assert.Equal("unavailable", observation.Results.Single(r => r.RuleId == "KNU-MARGINS-001").Outcome);
        Assert.Equal("measurement-definition-unresolved", observation.Results.Single(r => r.RuleId == "KNU-TYPOGRAPHY-001").Reason);
    }
    [Theory]
    [InlineData("pdfSha256")][InlineData("compileRequestId")][InlineData("inputDigest")][InlineData("factsSha256")]
    public void RefusesChangedCorrelation(string field)
    {
        var (request, reply) = Vector(); reply["payload"]![field] = "changed";
        Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(request, reply.ToJsonString()));
    }
    [Fact]
    public void RefusesStaleAnnotationUnknownFieldsAndDuplicateRules()
    {
        var (request, reply) = Vector(); reply["payload"]!["annotationVersion"] = request.AnnotationVersion + 1;
        Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(request, reply.ToJsonString()));
        (request, reply) = Vector(); reply["payload"]!["unexpected"] = true;
        Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(request, reply.ToJsonString()));
        (request, reply) = Vector(); var results = reply["payload"]!["results"]!.AsArray(); results[1] = results[0]!.DeepClone();
        Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(request, reply.ToJsonString()));
    }
}
