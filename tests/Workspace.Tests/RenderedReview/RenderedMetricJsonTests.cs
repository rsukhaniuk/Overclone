using Client.Analysis.RenderedReview;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Workspace.Tests.RenderedReview;

// Actual pure-Rust responses: native-v2 single-glyph margins and separately
// labelled synthetic multiline coordinates. These test shape/correlation only.
public sealed class RenderedMetricJsonTests
{
    private static (RenderedReviewRequest Request, JsonObject Reply) Vector(bool paragraph = false)
    {
        string name = paragraph ? "producer-metrics-paragraph-v2.json" : "producer-metrics-v2.json";
        using var stream = typeof(RenderedMetricJsonTests).Assembly.GetManifestResourceStream("Workspace.Tests.Fixtures.RenderedReview." + name)!;
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
        return (request, JsonNode.Parse(document.RootElement.GetProperty("response").GetRawText())!.AsObject());
    }
    private static JsonObject Rule(JsonObject reply, string id = "KNU-MARGINS-001") => reply["payload"]!["results"]!.AsArray().OfType<JsonObject>().Single(r => r["ruleId"]!.GetValue<string>() == id);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Retains_numeric_observation_uncertainty_and_conditional_role(bool paragraph)
    {
        var (request, reply) = Vector(paragraph);
        var result = RenderedReviewJson.Parse(request, reply.ToJsonString());
        var metric = result.Results.Single(r => r.RuleId == (paragraph ? "KNU-PARAGRAPH-RENDERED-001" : "KNU-MARGINS-001"));
        Assert.Equal("indeterminate", metric.Outcome); Assert.Equal("partial", metric.SupportState);
        Assert.NotEmpty(metric.Evidence);
        foreach (var evidence in metric.Evidence)
        {
            Assert.Equal(JsonValueKind.Number, evidence.GetProperty("measured").ValueKind);
            Assert.True(evidence.GetProperty("uncertainty").GetProperty("absoluteError").GetDouble() > 0);
            Assert.Equal("authorLabel", evidence.GetProperty("roleProvenance").GetProperty("provenance").GetString());
        }
    }

    [Theory]
    [InlineData("pass")]
    [InlineData("fail")]
    public void Author_or_unproved_machine_labels_never_support_normative_outcomes(string outcome)
    {
        foreach (string provenance in new[] { "authorLabel", "literalSourceAndNativeMap", "validatedNativeClassification" })
        {
            var (request, reply) = Vector();
            request = request with { Annotations = request.Annotations.Select(a => a with { Provenance = provenance }).ToArray() };
            var rule = Rule(reply); rule["outcome"] = outcome;
            foreach (var evidence in rule["evidence"]!.AsArray()) evidence!["roleProvenance"]!["provenance"] = provenance;
            Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(request, reply.ToJsonString()));
        }
    }

    [Theory]
    [InlineData("annotation")][InlineData("certainty")][InlineData("role")][InlineData("page")]
    [InlineData("range")][InlineData("measurement-type")][InlineData("uncertainty-negative")][InlineData("uncertainty-method")]
    [InlineData("classification-count")][InlineData("classification-baseline")][InlineData("source-location")][InlineData("extra")]
    [InlineData("duplicate-metric")][InlineData("missing-side")]
    public void Rejects_uncorrelated_or_malformed_metric_evidence(string mode)
    {
        var (request, reply) = Vector(); var rule = Rule(reply); var evidence = rule["evidence"]!.AsArray(); var first = evidence[0]!;
        switch (mode)
        {
            case "annotation": first["roleProvenance"]!["annotationId"] = 999; break;
            case "certainty": first["roleProvenance"]!["certainty"] = "tentative"; break;
            case "role": first["roleProvenance"]!["role"] = "footnote"; break;
            case "page": first["pageIndex"] = 1; break;
            case "range": first["characterStart"] = 0; break;
            case "measurement-type": first["measured"] = "guessed"; break;
            case "uncertainty-negative": first["uncertainty"]!["absoluteError"] = -1; break;
            case "uncertainty-method": first["uncertainty"]!["method"] = "nominal-font-size"; break;
            case "classification-count": first["classification"]!["characterCount"] = 100000; break;
            case "classification-baseline": first["classification"]!["continuationBaselineRaw"] = 40; break;
            case "source-location": first["sourceLocation"] = new JsonObject { ["fileId"] = "unproved" }; break;
            case "extra": first["extra"] = true; break;
            case "duplicate-metric": evidence[1] = first.DeepClone(); break;
            case "missing-side": evidence.RemoveAt(1); break;
        }
        Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(request, reply.ToJsonString()));
    }

    [Theory]
    [InlineData("version")][InlineData("hash")][InlineData("compile")][InlineData("labels")]
    public void Refuses_stale_measurements_after_compilation_or_annotation_change(string change)
    {
        var (request, reply) = Vector();
        request = change switch
        {
            "version" => request with { AnnotationVersion = request.AnnotationVersion + 1 },
            "hash" => request with { FactsSha256 = new('0', 64) },
            "compile" => request with { CompileRequestId = new('0', 32) },
            _ => request with { Annotations = [] }
        };
        Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(request, reply.ToJsonString()));
    }
}
