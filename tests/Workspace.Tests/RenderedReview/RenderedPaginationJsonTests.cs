using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Analysis.RenderedReview;
using Client.Pdf;
using Xunit;

namespace Workspace.Tests.RenderedReview;

public sealed class RenderedPaginationJsonTests
{
    private const string RuleId = "KNU-PAGINATION-001";

    [Fact]
    public void Genuine_pdftex_and_pdfium_pagination_retains_conditional_counterexample_and_later_observations()
    {
        var (request, reply) = LoadWire("observed");
        var observation = RenderedReviewJson.Parse(request, reply);
        Assert.Equal(RenderedReviewContract.PaginationProfileVersion, observation.Request.ProfileVersion);
        var rows = observation.Results.Where(r => r.RuleId == RuleId).ToArray();
        Assert.Equal(request.RequestedPages.Count, rows.Length);
        foreach (var row in rows)
        {
            Assert.Equal("1.1.0", row.RuleVersion);
            Assert.Equal("partial", row.SupportState);
            Assert.Equal("3.3;3.16", row.Citation.GetProperty("section").GetString());
            Assert.Equal(new[] { 19, 31 }, row.Citation.GetProperty("pdfPages").EnumerateArray().Select(p => p.GetInt32()));
            Assert.Contains("extracted-unicode-not-visual-digit-proof", row.Limitations);
            Assert.Contains("top-right-corner-criterion-unspecified", row.Limitations);
            Assert.Contains("copied-appendix-own-numbering-unassessed", row.Limitations);
        }

        var title = Assert.Single(rows[0].Evidence);
        Assert.Equal("fail", rows[0].Outcome);
        Assert.Equal("confirmed-title-number-present", rows[0].Reason);
        Assert.Equal(0, title.GetProperty("pageIndex").GetInt32());
        Assert.Equal(JsonValueKind.Null, title.GetProperty("expected").ValueKind);
        Assert.Equal("1", title.GetProperty("measured").GetString());
        Assert.Equal(request.Annotations.Single(a => a.Role == "title").AnnotationId,
            title.GetProperty("observation").GetProperty("titleAnnotationId").GetInt64());
        Assert.Equal("title-number-present", title.GetProperty("observation").GetProperty("comparison").GetString());
        Assert.True(title.GetProperty("observation").GetProperty("placement").GetProperty("absoluteErrorMm").GetDouble() > 0);

        AssertLater(rows[1], 2, "2", "index-match");
        AssertLater(rows[2], 3, "1", "index-mismatch");
        AssertLater(rows[3], 4, "4", "index-match");
        Assert.DoesNotContain(rows, r => r.Outcome == "pass");
    }

    [Theory]
    [InlineData("unlabelled", "confirmed-page-number-range-required")]
    [InlineData("ambiguous", "ambiguous-page-number-labels")]
    public void Genuine_uncertain_reports_keep_pagination_indeterminate_without_proving_absence(string scenario, string reason)
    {
        var (request, reply) = LoadWire(scenario);
        var rows = RenderedReviewJson.Parse(request, reply).Results.Where(r => r.RuleId == RuleId).ToArray();
        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.Equal("indeterminate", row.Outcome);
            Assert.Equal(reason, row.Reason);
            Assert.Empty(row.Evidence);
            Assert.Contains("empty-text-does-not-prove-number-absence", row.Limitations);
        });
    }

    [Fact]
    public void Single_requested_appendix_page_keeps_its_physical_ordinal_and_only_the_selected_work_number()
    {
        var (request, reply) = LoadWire("appendix");
        Assert.Equal(3, Assert.Single(request.RequestedPages));
        var row = RenderedReviewJson.Parse(request, reply).Results.Single(r => r.RuleId == RuleId);
        AssertLater(row, 4, "4", "index-match");
    }

    [Fact]
    public void Opt_in_changes_only_version_and_rejects_legacy_or_caption_image_replay()
    {
        var (request, reply) = Load("observed");
        var legacy = request with { ProfileVersion = RenderedReviewContract.ProfileVersion };
        string legacyBytes = RenderedReviewJson.Pack(legacy);
        string selectedBytes = RenderedReviewJson.Pack(request);
        Assert.Equal(legacyBytes.Replace("\"version\":\"1.0.0\"", "\"version\":\"1.2.0\"", StringComparison.Ordinal), selectedBytes);
        Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(legacy, reply.ToJsonString()));
        Assert.Throws<ArgumentException>(() => RenderedReviewJson.Pack(request with { ProfileVersion = "1.1.0" }));
    }

    [Theory]
    [InlineData("pagination-version")]
    [InlineData("support")]
    [InlineData("citation")]
    [InlineData("title-fail-without-evidence")]
    [InlineData("unbounded-placement")]
    [InlineData("missing-limits")]
    [InlineData("unrelated-title-id")]
    [InlineData("unrelated-number-id")]
    [InlineData("native-source-invented")]
    [InlineData("role-provenance")]
    [InlineData("arabic-indic")]
    [InlineData("wrong-digit-value")]
    [InlineData("wrong-range")]
    [InlineData("out-of-frame")]
    [InlineData("corner-certificate")]
    [InlineData("later-pass")]
    [InlineData("later-fail")]
    [InlineData("other-rule-fail")]
    [InlineData("later-index")]
    [InlineData("other-page-evidence")]
    [InlineData("extra-evidence")]
    public void Rejects_unsupported_or_uncorrelated_pagination_evidence(string mutation)
    {
        var (request, reply) = Load("observed");
        var rows = RuleRows(reply);
        var title = rows[0]["evidence"]![0]!.AsObject();
        var later = rows[1]["evidence"]![0]!.AsObject();
        var observed = title["observation"]!.AsObject();
        switch (mutation)
        {
            case "pagination-version": rows[0]["ruleVersion"] = "1.0.0"; break;
            case "support": rows[0]["supportState"] = "supported"; break;
            case "citation": rows[0]["citation"]!["section"] = "3.3"; break;
            case "title-fail-without-evidence": rows[0]["evidence"] = new JsonArray(); break;
            case "unbounded-placement": observed["placement"]!["topClearanceMm"] = 1e20; break;
            case "missing-limits": rows[0]["limitations"]!.AsArray().RemoveAt(1); break;
            case "unrelated-title-id": observed["titleAnnotationId"] = 99999; break;
            case "unrelated-number-id": title["roleProvenance"]!["annotationId"] = 99999; break;
            case "native-source-invented": title["sourceLocation"] = new JsonObject { ["fileId"] = "invented" }; break;
            case "role-provenance": title["roleProvenance"]!["provenance"] = "validatedNativeClassification"; break;
            case "arabic-indic": title["measured"] = "١"; break;
            case "wrong-digit-value": observed["asciiDigitValue"] = 0; break;
            case "wrong-range": title["characterEnd"] = 2; break;
            case "out-of-frame": title["rawBox"]![3] = 1_000_000; break;
            case "corner-certificate": observed["placement"]!["topRightCornerPass"] = true; break;
            case "later-pass": rows[1]["outcome"] = "pass"; break;
            case "later-fail": rows[2]["outcome"] = "fail"; break;
            case "other-rule-fail": reply["payload"]!["results"]![0]!["outcome"] = "fail"; break;
            case "later-index": later["expected"] = 3; break;
            case "other-page-evidence": rows[1]["evidence"] = rows[2]["evidence"]!.DeepClone(); break;
            case "extra-evidence": rows[0]["evidence"]!.AsArray().Add(title.DeepClone()); break;
        }
        Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(request, reply.ToJsonString()));
    }

    [Theory]
    [InlineData("missing-title")]
    [InlineData("tentative-title")]
    [InlineData("machine-number")]
    [InlineData("duplicate-number")]
    [InlineData("later-title")]
    public void Requires_exact_current_author_labels_for_each_page(string mutation)
    {
        var (request, reply) = Load("observed");
        var title = request.Annotations.Single(a => a.PageIndex == 0 && a.Role == "title");
        var number = request.Annotations.Single(a => a.PageIndex == 0 && a.Role == "pageNumber");
        request = request with { Annotations = mutation switch
        {
            "missing-title" => request.Annotations.Where(a => a != title).ToArray(),
            "tentative-title" => request.Annotations.Select(a => a == title ? a with { Certainty = "tentative" } : a).ToArray(),
            "machine-number" => request.Annotations.Select(a => a == number ? a with { Provenance = "literalSourceAndNativeMap" } : a).ToArray(),
            "duplicate-number" => [.. request.Annotations, number with { AnnotationId = 99999, Certainty = "tentative" }],
            _ => [.. request.Annotations, title with { PageIndex = 1, AnnotationId = 99999 }]
        } };
        Assert.Throws<JsonException>(() => RenderedReviewJson.Parse(request, reply.ToJsonString()));
    }

    private static void AssertLater(RenderedRuleResult row, int physical, string literal, string comparison)
    {
        Assert.Equal("indeterminate", row.Outcome);
        Assert.Equal("confirmed-page-number-" + comparison, row.Reason);
        var evidence = Assert.Single(row.Evidence);
        var observed = evidence.GetProperty("observation");
        Assert.Equal(physical, observed.GetProperty("physicalPageNumber").GetInt32());
        Assert.Equal(physical, evidence.GetProperty("expected").GetInt32());
        Assert.Equal(literal, evidence.GetProperty("measured").GetString());
        Assert.Equal(JsonValueKind.Null, observed.GetProperty("titleAnnotationId").ValueKind);
        Assert.Equal(comparison, observed.GetProperty("comparison").GetString());
    }

    private static JsonObject[] RuleRows(JsonObject reply) => reply["payload"]!["results"]!.AsArray().OfType<JsonObject>()
        .Where(row => row["ruleId"]!.GetValue<string>() == RuleId).ToArray();

    private static (RenderedReviewRequest Request, JsonObject Response) Load(string scenario)
    {
        var (request, response) = LoadWire(scenario);
        return (request, JsonNode.Parse(response)!.AsObject());
    }

    private static (RenderedReviewRequest Request, string Response) LoadWire(string scenario)
    {
        using var stream = typeof(RenderedPaginationJsonTests).Assembly.GetManifestResourceStream(
            "Workspace.Tests.Fixtures.RenderedReview.producer-pagination-v1_2.json")!;
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement.GetProperty(scenario);
        var wire = root.GetProperty("request");
        var p = wire.GetProperty("payload");
        static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;
        static int? Range(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.Null
            ? null : value.GetProperty(name).GetInt32();
        var annotations = p.GetProperty("annotations").EnumerateArray().Select(a => new RenderedRoleAnnotation(
            a.GetProperty("annotationId").GetInt64(), Text(a, "role"), a.GetProperty("pageIndex").GetInt32(),
            Range(a, "characterStart"), Range(a, "characterEnd"), null, null, Text(a, "provenance"), Text(a, "certainty"))).ToArray();
        var request = new RenderedReviewRequest(Text(wire, "requestId"), Text(p, "pdfSha256"), Text(p, "compileRequestId"),
            p.GetProperty("compileEpoch").GetInt64(), Text(p, "inputDigest"), p.GetProperty("captureId").GetGuid(),
            Text(p, "workType"), p.GetProperty("annotationVersion").GetInt64(), p.GetProperty("pageCount").GetInt32(),
            p.GetProperty("requestedPages").EnumerateArray().Select(page => page.GetInt32()).ToArray(),
            annotations, Text(p, "factsSha256")) { ProfileVersion = Text(p.GetProperty("profile"), "version") };
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(wire.GetRawText()), JsonNode.Parse(RenderedReviewJson.Pack(request))));
        string response = root.GetProperty("response").GetRawText();
        Assert.Equal(Text(root, "responseSha256"), PdfHashes.Of(Encoding.UTF8.GetBytes(response)));
        return (request, response);
    }
}
