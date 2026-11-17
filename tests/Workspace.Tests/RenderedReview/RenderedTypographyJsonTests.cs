using Client.Analysis.RenderedReview;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Workspace.Tests.RenderedReview;

// Actual Rust response from a genuine two-line native v2 measurement fixture.
public sealed class RenderedTypographyJsonTests
{
    private static (RenderedReviewRequest Request, JsonObject Reply) Vector()
    {
        string name = "producer-typography-v2.json";
        using var stream = typeof(RenderedTypographyJsonTests).Assembly.GetManifestResourceStream("Workspace.Tests.Fixtures.RenderedReview." + name)!;
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
    private static JsonObject Rule(JsonObject reply, string id = "KNU-TYPOGRAPHY-001") => reply["payload"]!["results"]!.AsArray().OfType<JsonObject>().Single(r => r["ruleId"]!.GetValue<string>() == id);


    [Fact]
    public void Genuine_native_ink_heights_and_baselines_are_observations_with_error()
    {
        var (request, reply) = Vector(); var parsed = RenderedReviewJson.Parse(request, reply.ToJsonString());
        var typography = parsed.Results.Single(r => r.RuleId == "KNU-TYPOGRAPHY-001");
        Assert.Equal("partial", typography.SupportState); Assert.Equal("indeterminate", typography.Outcome);
        Assert.Equal("conditional-typography-observations", typography.Reason); Assert.Equal(4, typography.Evidence.Count);
        foreach (var e in typography.Evidence)
        {
            Assert.Equal(JsonValueKind.Number, e.GetProperty("measured").ValueKind);
            Assert.Equal(JsonValueKind.String, e.GetProperty("expected").ValueKind);
            Assert.Equal("mm", e.GetProperty("unit").GetString());
            Assert.True(e.GetProperty("uncertainty").GetProperty("absoluteError").GetDouble() > 0);
            Assert.Equal("authorLabel", e.GetProperty("roleProvenance").GetProperty("provenance").GetString());
            Assert.Equal(JsonValueKind.Null, e.GetProperty("sourceLocation").ValueKind);
        }
    }
    [Theory]
    [InlineData("pass")][InlineData("fail")]
    public void Claimed_machine_roles_cannot_mint_normative_typography(string outcome)
    {
        foreach (string provenance in new[] { "authorLabel", "literalSourceAndNativeMap", "validatedNativeClassification" })
        {
            var (request, reply) = Vector(); request = request with { Annotations = request.Annotations.Select(a => a with { Provenance = provenance }).ToArray() };
            var rule = Rule(reply); rule["outcome"] = outcome;
            foreach (var e in rule["evidence"]!.AsArray()) e!["roleProvenance"]!["provenance"] = provenance;
            Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(request, reply.ToJsonString()));
        }
    }
    [Theory]
    [InlineData("metric")][InlineData("method")][InlineData("definition")][InlineData("numeric-threshold")][InlineData("unit")]
    [InlineData("negative")][InlineData("non-numeric")][InlineData("zero-error")][InlineData("negative-error")][InlineData("error-method")]
    [InlineData("missing-limit")][InlineData("annotation")][InlineData("role")][InlineData("certainty")][InlineData("range")]
    [InlineData("source-location")][InlineData("classification")][InlineData("extra-field")][InlineData("stale-facts")]
    public void Refuses_invented_predicates_bad_measurements_and_uncorrelated_evidence(string corruption)
    {
        var (request, reply) = Vector(); var e = Rule(reply)["evidence"]![0]!.AsObject();
        switch (corruption)
        {
            case "metric": e["metric"] = "font-height"; break;
            case "method": e["method"] = "font-name-and-flags"; break;
            case "definition": e["expected"] = "font height must equal letter ink"; break;
            case "numeric-threshold": e["expected"] = 1.8; break;
            case "unit": e["unit"] = "pt"; break;
            case "negative": e["measured"] = -1; break;
            case "non-numeric": e["measured"] = "NaN"; break;
            case "zero-error": e["uncertainty"]!["absoluteError"] = 0; break;
            case "negative-error": e["uncertainty"]!["absoluteError"] = -1; break;
            case "error-method": e["uncertainty"]!["method"] = "guideline-tolerance"; break;
            case "missing-limit": e["uncertainty"]!["limitations"]!.AsArray().RemoveAt(1); break;
            case "annotation": e["roleProvenance"]!["annotationId"] = 987; break;
            case "role": e["roleProvenance"]!["role"] = "headingMajor"; break;
            case "certainty": e["roleProvenance"]!["certainty"] = "confirmedMachineProof"; break;
            case "range": e["characterStart"] = 0; break;
            case "source-location": e["sourceLocation"] = "guessed.tex:1"; break;
            case "classification": e["classification"]!["lineCount"] = 0; break;
            case "extra-field": e["visualBold"] = true; break;
            default: reply["payload"]!["factsSha256"] = new string('0', 64); break;
        }
        Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(request, reply.ToJsonString()));
    }
    [Theory]
    [InlineData("missing")][InlineData("duplicate")][InlineData("reversed")][InlineData("one-line-baselines")][InlineData("wrong-rule")]
    public void Refuses_incomplete_or_inconsistent_extrema_pairs(string corruption)
    {
        var (request, reply) = Vector(); var rule = Rule(reply); var evidence = rule["evidence"]!.AsArray();
        switch (corruption)
        {
            case "missing": evidence.RemoveAt(0); break;
            case "duplicate": evidence.Add(evidence[0]!.DeepClone()); break;
            case "reversed": evidence[0]!["measured"] = evidence[1]!["measured"]!.GetValue<double>() + 1; break;
            case "one-line-baselines": foreach (var e in evidence) { e!["classification"]!["lineCount"] = 1; e["classification"]!["continuationBaselineRaw"] = null; } break;
            default: rule["ruleId"] = "KNU-MARGINS-001"; break;
        }
        Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(request, reply.ToJsonString()));
    }
}
