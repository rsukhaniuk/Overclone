using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Analysis.RenderedReview;
using Client.Pdf;
using Xunit;

namespace Workspace.Tests.RenderedReview;

public sealed class RenderedHeadingJsonTests
{
    private const string RuleId = "KNU-HEADINGS-RENDERED-001";

    [Theory]
    [InlineData("body-above", "fail", "body-above-heading", 0)]
    [InlineData("body-below", "indeterminate", "body-below-heading", 21)]
    public void Exact_native_pdf_and_Rust_reply_correlate_current_author_pair(
        string scenario, string outcome, string relation, int bodyStart)
    {
        var (request, response) = Load(scenario);
        var result = RenderedReviewJson.Parse(request, response);
        var heading = result.Results.Single(r => r.RuleId == RuleId);
        Assert.Equal(outcome, heading.Outcome);
        Assert.Equal("1.1.0", heading.RuleVersion);
        Assert.Equal("partial", heading.SupportState);
        Assert.Equal("3.2", heading.Citation.GetProperty("section").GetString());
        Assert.Equal(new[] { 18, 19 }, heading.Citation.GetProperty("pdfPages").EnumerateArray().Select(p => p.GetInt32()));
        Assert.Contains("unselected-content-unassessed", heading.Limitations);
        var evidence = Assert.Single(heading.Evidence);
        Assert.Equal(11, evidence.GetProperty("characterStart").GetInt32());
        var observed = evidence.GetProperty("observation");
        Assert.Equal(relation, observed.GetProperty("relativeOrder").GetString());
        Assert.Equal(bodyStart, observed.GetProperty("bodyCharacterStart").GetInt32());
        Assert.Equal(2.0, observed.GetProperty("userUnit").GetDouble());
        double gap = evidence.GetProperty("measured").GetDouble();
        Assert.Equal(bodyStart == 0, gap > 0);
        Assert.True(Math.Abs(gap) > evidence.GetProperty("uncertainty").GetProperty("absoluteError").GetDouble());
        Assert.Equal("1.1.0", result.Results.Single(r => r.RuleId == "KNU-PAGINATION-001").RuleVersion);
    }

    [Fact]
    public void Checks_same_fact_pair_only_with_explicit_labels_and_selected_profile()
    {
        var (before, _) = Load("body-above");
        var (after, _) = Load("body-below");
        Assert.Equal(before.PdfSha256, after.PdfSha256);
        Assert.Equal(before.FactsSha256, after.FactsSha256);
        Assert.Equal(before.Annotations[0], after.Annotations[0]);
        Assert.Equal(before.Annotations[1] with { CharacterStart = 21, CharacterEnd = 30 }, after.Annotations[1]);
        var (missing, response) = Load("no-body");
        var heading = RenderedReviewJson.Parse(missing, response).Results.Single(r => r.RuleId == RuleId);
        Assert.Equal("indeterminate", heading.Outcome);
        Assert.Equal("confirmed-heading-body-range-required", heading.Reason);
        Assert.Empty(heading.Evidence);
        Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(
            missing with { ProfileVersion = RenderedReviewContract.PaginationProfileVersion }, response));
    }

    [Theory]
    [InlineData("submission-revision")]
    [InlineData("outcome-pass")]
    [InlineData("citation")]
    [InlineData("wrong-body-id")]
    [InlineData("wrong-range")]
    [InlineData("out-of-frame")]
    [InlineData("uncertain-separation")]
    [InlineData("reversed-sign")]
    [InlineData("inconsistent-scale")]
    [InlineData("invented-style")]
    [InlineData("unconfirmed-heading")]
    [InlineData("uncorrelated-body")]
    [InlineData("duplicate-proof")]
    public void Strict_wire_rejects_tampered_or_stale_heading_evidence(string mode)
    {
        var (request, response) = Load("body-above");
        var reply = JsonNode.Parse(response)!.AsObject();
        var row = reply["payload"]!["results"]!.AsArray().OfType<JsonObject>()
            .Single(r => r["ruleId"]!.GetValue<string>() == RuleId);
        var evidence = row["evidence"]![0]!;
        var observed = evidence["observation"]!;
        switch (mode)
        {
            case "submission-revision": reply["payload"]!["compileEpoch"] = 99; break;
            case "outcome-pass": row["outcome"] = "pass"; break;
            case "citation": row["citation"]!["pdfPages"]![0] = 20; break;
            case "wrong-body-id": observed["bodyAnnotationId"] = 999; break;
            case "wrong-range": observed["bodyCharacterEnd"] = 30; break;
            case "out-of-frame": observed["bodyInkBox"]![3] = 1_000_000; break;
            case "uncertain-separation": evidence["uncertainty"]!["absoluteError"] = 1000; break;
            case "reversed-sign": observed["rawSignedGap"] = -12; break;
            case "inconsistent-scale": observed["userUnit"] = 1; break;
            case "invented-style": observed["bold"] = true; break;
            case "unconfirmed-heading":
                request = request with { Annotations = [request.Annotations[0] with { Certainty = "tentative" }, request.Annotations[1]] };
                break;
            case "uncorrelated-body":
                request = request with { Annotations = [request.Annotations[0], request.Annotations[1] with { Provenance = "validatedNativeClassification" }] };
                break;
            case "duplicate-proof":
                request = request with { Annotations = [.. request.Annotations, request.Annotations[1] with { AnnotationId = 99 }] };
                break;
        }
        Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(request, reply.ToJsonString()));
    }

    private static (RenderedReviewRequest Request, string Response) Load(string scenario)
    {
        using var stream = typeof(RenderedHeadingJsonTests).Assembly.GetManifestResourceStream(
            "Workspace.Tests.Fixtures.RenderedReview.producer-heading-v1_3.json")!;
        using var json = JsonDocument.Parse(stream);
        var sample = json.RootElement.GetProperty(scenario);
        var wire = sample.GetProperty("request");
        var p = wire.GetProperty("payload");
        static string Text(JsonElement value, string member) => value.GetProperty(member).GetString()!;
        var annotations = p.GetProperty("annotations").EnumerateArray().Select(a => new RenderedRoleAnnotation(
            a.GetProperty("annotationId").GetInt64(), Text(a, "role"), a.GetProperty("pageIndex").GetInt32(),
            a.GetProperty("characterStart").GetInt32(), a.GetProperty("characterEnd").GetInt32(), null, null,
            Text(a, "provenance"), Text(a, "certainty"))).ToArray();
        var request = new RenderedReviewRequest(Text(wire, "requestId"), Text(p, "pdfSha256"), Text(p, "compileRequestId"),
            p.GetProperty("compileEpoch").GetInt64(), Text(p, "inputDigest"), p.GetProperty("captureId").GetGuid(),
            Text(p, "workType"), p.GetProperty("annotationVersion").GetInt64(), p.GetProperty("pageCount").GetInt32(),
            p.GetProperty("requestedPages").EnumerateArray().Select(page => page.GetInt32()).ToArray(), annotations,
            Text(p, "factsSha256")) { ProfileVersion = Text(p.GetProperty("profile"), "version") };
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(wire.GetRawText()), JsonNode.Parse(RenderedReviewJson.Pack(request))));
        string response = sample.GetProperty("response").GetRawText();
        Assert.Equal(Text(sample, "responseSha256"), PdfHashes.Of(Encoding.UTF8.GetBytes(response)));
        return (request, response);
    }
}
