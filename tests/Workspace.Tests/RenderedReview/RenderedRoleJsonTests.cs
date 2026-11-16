using Client.Analysis.RenderedReview;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Workspace.Tests.RenderedReview;

// Exact pure-Rust responses. Native-heading retains the unchanged native A-glyph
// fixture; digit/caption/multiglyph heading vectors are synthetic consumer data.
public sealed class RenderedRoleJsonTests
{
    private static (RenderedReviewRequest Request, JsonObject Reply, string Rule) Vector(string kind = "number")
    {
        using var stream = typeof(RenderedRoleJsonTests).Assembly.GetManifestResourceStream("Workspace.Tests.Fixtures.RenderedReview.producer-role-" + kind + ".json")!;
        using var document = JsonDocument.Parse(stream); var envelope = document.RootElement.GetProperty("request"); var p = envelope.GetProperty("payload");
        var annotations = p.GetProperty("annotations").EnumerateArray().Select(a => new RenderedRoleAnnotation(
            a.GetProperty("annotationId").GetInt64(), a.GetProperty("role").GetString()!, a.GetProperty("pageIndex").GetInt32(),
            a.GetProperty("characterStart").ValueKind == JsonValueKind.Null ? null : a.GetProperty("characterStart").GetInt32(),
            a.GetProperty("characterEnd").ValueKind == JsonValueKind.Null ? null : a.GetProperty("characterEnd").GetInt32(),
            null, null, a.GetProperty("provenance").GetString()!, a.GetProperty("certainty").GetString()!)).ToArray();
        var request = new RenderedReviewRequest(envelope.GetProperty("requestId").GetString()!, p.GetProperty("pdfSha256").GetString()!,
            p.GetProperty("compileRequestId").GetString()!, p.GetProperty("compileEpoch").GetInt64(), p.GetProperty("inputDigest").GetString()!,
            p.GetProperty("captureId").GetGuid(), p.GetProperty("workType").GetString()!, p.GetProperty("annotationVersion").GetInt64(),
            p.GetProperty("pageCount").GetInt32(), p.GetProperty("requestedPages").EnumerateArray().Select(i => i.GetInt32()).ToArray(), annotations,
            p.GetProperty("factsSha256").GetString()!);
        return (request, JsonNode.Parse(document.RootElement.GetProperty("response").GetRawText())!.AsObject(),
            kind == "number" ? "KNU-PAGINATION-001" : kind == "caption" ? "KNU-CAPTION-RENDERED-001" : "KNU-HEADINGS-RENDERED-001");
    }
    private static JsonObject Rule(JsonObject reply, string id) => reply["payload"]!["results"]!.AsArray().OfType<JsonObject>().Single(r => r["ruleId"]!.GetValue<string>() == id);

    [Theory]
    [InlineData("number")][InlineData("native-heading")][InlineData("caption")][InlineData("heading")]
    public void Retains_literal_text_and_conditional_position_without_visual_or_role_proof(string kind)
    {
        var (request, reply, id) = Vector(kind); var result = RenderedReviewJson.Parse(request, reply.ToJsonString());
        var role = result.Results.Single(r => r.RuleId == id);
        Assert.Equal("partial", role.SupportState); Assert.Equal("indeterminate", role.Outcome);
        var evidence = Assert.Single(role.Evidence); Assert.Equal("annotation-scoped-native-text", evidence.GetProperty("method").GetString());
        Assert.Contains("extracted-text-not-visual-glyph-proof", evidence.GetProperty("uncertainty").GetProperty("limitations").EnumerateArray().Select(v => v.GetString()));
        if (kind == "caption") Assert.Equal("illustration-geometry-unavailable", evidence.GetProperty("observation").GetProperty("relativeOrder").GetString());
        if (kind == "number") Assert.True(evidence.GetProperty("observation").GetProperty("placement").GetProperty("absoluteErrorMm").GetDouble() > 0);
    }

    [Theory]
    [InlineData("pass")][InlineData("fail")]
    public void Merely_claimed_machine_role_or_author_label_cannot_enable_normative_outcome(string outcome)
    {
        foreach (string kind in new[] { "number", "native-heading", "caption" })
        foreach (string provenance in new[] { "authorLabel", "literalSourceAndNativeMap", "validatedNativeClassification" })
        {
            var (request, reply, id) = Vector(kind); request = request with { Annotations = request.Annotations.Select(a => a with { Provenance = provenance }).ToArray() };
            var rule = Rule(reply, id); rule["outcome"] = outcome;
            rule["evidence"]![0]!["roleProvenance"]!["provenance"] = provenance;
            Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(request, reply.ToJsonString()));
        }
    }

    [Theory]
    [InlineData("method")][InlineData("unknown-scalar")][InlineData("text-bytes")][InlineData("role")][InlineData("range")]
    [InlineData("ordinal")][InlineData("case")][InlineData("number")][InlineData("visual-bold")][InlineData("uncertainty")]
    [InlineData("duplicate")][InlineData("placement-error")][InlineData("placement-box")][InlineData("placement-field")]
    public void Refuses_unbounded_or_uncorrelated_role_evidence(string mode)
    {
        var (request, reply, id) = Vector(); var rule = Rule(reply, id); var e = rule["evidence"]![0]!; var o = e["observation"]!;
        switch (mode)
        {
            case "method": e["method"] = "visual-glyph-proof"; break;
            case "unknown-scalar": e["measured"] = "1\0"; break;
            case "text-bytes": e["measured"] = new string('Ї', 129); break;
            case "role": e["roleProvenance"]!["role"] = "headingMajor"; break;
            case "range": e["characterEnd"] = 3; break;
            case "ordinal": o["oneBasedPdfPage"] = 3; break;
            case "case": o["unicodeCase"] = "visually-uppercase"; break;
            case "number": o["asciiDigitValue"] = 9007199254740992L; break;
            case "visual-bold": o["bold"] = true; break;
            case "uncertainty": e["uncertainty"]!["limitations"]!.AsArray().RemoveAt(1); break;
            case "duplicate": rule["evidence"]!.AsArray().Add(e.DeepClone()); break;
            case "placement-error": o["placement"]!["absoluteErrorMm"] = -1; break;
            case "placement-box": o["placement"]!["inkBox"]![0] = 0; break;
            case "placement-field": o["placement"]!["bodyAlignment"] = "centred"; break;
        }
        Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(request, reply.ToJsonString()));
    }

    [Fact]
    public void Refuses_fabricated_caption_order_and_stale_annotation_context()
    {
        var (request, reply, id) = Vector("caption"); var observation = Rule(reply, id)["evidence"]![0]!["observation"]!;
        observation["relativeOrder"] = "above-illustration";
        Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(request, reply.ToJsonString()));
        (request, reply, id) = Vector("caption");
        Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(request with { AnnotationVersion = request.AnnotationVersion + 1 }, reply.ToJsonString()));
        Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(request with { Annotations = [] }, reply.ToJsonString()));
        Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(request with { PdfSha256 = new('0', 64) }, reply.ToJsonString()));
    }
}
