using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Analysis.RenderedExtent;
using Xunit;

namespace Workspace.Tests.RenderedExtent;

// Fixed shared-contract JSON fixtures, not another implementation of the Rust
// usual-extent policy. No WASM/native/browser acceptance is inferred here.
internal static class ExtentFixtureJson
{
    internal static RenderedExtentRequest Request(string workType = "unspecified") => new(new('a', 32), new('b', 64), 1, workType);
    internal static string Body(RenderedExtentRequest r, string status = "indeterminate", string reason = "workTypeRequired",
        int? minimum = null, int? maximum = null) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, kind = "observation", requestId = r.RequestId, pdfSha256 = r.PdfSha256, pageCount = r.PageCount,
        profileId = RenderedExtentContract.ProfileId, profileVersion = RenderedExtentContract.ProfileVersion,
        ruleId = RenderedExtentContract.RuleId, evaluationDomain = RenderedExtentContract.Domain, workType = r.WorkType,
        status, reason, usualMinimum = minimum, usualMaximum = maximum, nonAppendixPages = (int?)null, mainBodyPages = (int?)null,
        compliance = "notAssessed", guidelineSha256 = RenderedExtentContract.GuidelineSha256, sourceSection = "2",
        printedPage = 11, sourceUrl = RenderedExtentContract.SourceUrl
    });
    internal static string Known(RenderedExtentRequest r) => Body(r, "belowUsualMinimum", "totalBelowUsualNonAppendixMinimum", 40, 60);
}
public sealed class RenderedExtentJsonTests
{
    [Fact] public void Pack_has_only_exact_scalar_keys_and_no_source_bytes_or_paths()
    {
        var r = ExtentFixtureJson.Request(); using var d = JsonDocument.Parse(RenderedExtentJson.Pack(r));
        Assert.Equal(new[] { "pageCount", "pdfSha256", "requestId", "schemaVersion", "workType" }, d.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(r.RequestId, d.RootElement.GetProperty("requestId").GetString());
        Assert.Equal(r.PdfSha256, d.RootElement.GetProperty("pdfSha256").GetString());
        Assert.Equal(1, d.RootElement.GetProperty("schemaVersion").GetInt32());
    }
    [Fact] public void Unspecified_and_known_examples_validate_correlation_without_hard_pass()
    {
        var r = ExtentFixtureJson.Request(); var unspecified = RenderedExtentJson.Parse(r, ExtentFixtureJson.Body(r))!;
        Assert.Equal("indeterminate", unspecified.Status); Assert.Null(unspecified.UsualMinimum); Assert.Null(unspecified.UsualMaximum);
        var known = ExtentFixtureJson.Request("bachelorQualification");
        var result = RenderedExtentJson.Parse(known, ExtentFixtureJson.Known(known))!;
        Assert.Equal("belowUsualMinimum", result.Status); Assert.Equal(40, result.UsualMinimum); Assert.Equal(60, result.UsualMaximum);
        Assert.Same(known, result.Request);
        var indeterminate = RenderedExtentJson.Parse(known, ExtentFixtureJson.Body(known, "indeterminate", "nonAppendixBoundaryUnknown", 40, 60))!;
        Assert.Equal("indeterminate", indeterminate.Status);
    }
    [Theory]
    [InlineData("requestId", "foreign")][InlineData("pdfSha256", "foreign")][InlineData("workType", "coursework")]
    [InlineData("profileId", "other")][InlineData("profileVersion", "2")][InlineData("ruleId", "other")]
    [InlineData("evaluationDomain", "source")][InlineData("compliance", "pass")][InlineData("sourceSection", "3")]
    [InlineData("sourceUrl", "https://example.invalid/")][InlineData("guidelineSha256", "other")]
    [InlineData("status", "compliant")][InlineData("reason", "looksGood")]
    public void Wrong_correlation_metadata_or_enum_is_refused(string key, string value)
    {
        var r = ExtentFixtureJson.Request(); var json = JsonNode.Parse(ExtentFixtureJson.Body(r))!; json[key] = value;
        Assert.Throws<JsonException>(() => RenderedExtentJson.Parse(r, json.ToJsonString()));
    }
    [Theory]
    [InlineData("schemaVersion", "\"1\"")][InlineData("schemaVersion", "2")][InlineData("schemaVersion", "1.0")]
    [InlineData("pageCount", "513")][InlineData("pageCount", "0")][InlineData("pageCount", "true")]
    [InlineData("pageCount", "2")][InlineData("pageCount", "null")][InlineData("printedPage", "12")]
    [InlineData("nonAppendixPages", "1")][InlineData("mainBodyPages", "1")][InlineData("usualMinimum", "0")]
    [InlineData("usualMaximum", "true")][InlineData("usualMinimum", "{}")]
    public void Numeric_types_bounds_and_unclassified_counts_are_strict(string key, string rawValue)
    {
        var r = ExtentFixtureJson.Request(); var obj = JsonNode.Parse(ExtentFixtureJson.Body(r))!;
        obj[key] = JsonNode.Parse(rawValue);
        Assert.Throws<JsonException>(() => RenderedExtentJson.Parse(r, obj.ToJsonString()));
    }
    [Fact] public void Duplicate_missing_unknown_fields_bad_unicode_and_caps_are_refused()
    {
        var r = ExtentFixtureJson.Request(); var good = ExtentFixtureJson.Body(r);
        Assert.Throws<JsonException>(() => RenderedExtentJson.Parse(r, good.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1")));
        foreach (var key in JsonNode.Parse(good)!.AsObject().Select(p => p.Key).ToArray())
        {
            var obj = JsonNode.Parse(good)!; obj.AsObject().Remove(key);
            Assert.Throws<JsonException>(() => RenderedExtentJson.Parse(r, obj.ToJsonString()));
        }
        var extra = JsonNode.Parse(good)!; extra["message"] = "untrusted";
        Assert.Throws<JsonException>(() => RenderedExtentJson.Parse(r, extra.ToJsonString()));
        Assert.Throws<JsonException>(() => RenderedExtentJson.Parse(r, good.Replace("workTypeRequired", "\\ud800")));
        Assert.Throws<JsonException>(() => RenderedExtentJson.Parse(r, good + "\ud800"));
        Assert.Throws<JsonException>(() => RenderedExtentJson.Parse(r, new string(' ', 8193)));
        Assert.Throws<JsonException>(() => RenderedExtentJson.Parse(r, new string('é', 5000))); // UTF-8 cap, not just chars.
        Assert.ThrowsAny<JsonException>(() => RenderedExtentJson.Parse(r, "🙂")); // JsonReaderException is a JsonException; raw non-JSON input remains rejected.
    }
    [Fact] public void Only_fixed_three_key_error_schema_is_admitted_and_never_a_partial_observation()
    {
        var r = ExtentFixtureJson.Request();
        Assert.Null(RenderedExtentJson.Parse(r, "{\"schemaVersion\":1,\"kind\":\"error\",\"code\":\"invalidInput\"}"));
        foreach (var raw in new[] {
            "{\"schemaVersion\":1,\"kind\":\"error\",\"code\":\"invalidInput\",\"message\":\"secret\"}",
            "{\"schemaVersion\":1,\"kind\":\"error\",\"code\":\"other\"}",
            "{\"schemaVersion\":1,\"kind\":\"error\",\"code\":\"invalidInput\",\"code\":\"invalidInput\"}" })
            Assert.Throws<JsonException>(() => RenderedExtentJson.Parse(r, raw));
    }
    [Theory][InlineData(0)][InlineData(513)]
    public void Invalid_request_is_rejected_before_transport(int count) => Assert.Throws<ArgumentException>(() =>
        RenderedExtentJson.Pack(ExtentFixtureJson.Request() with { PageCount = count }));
}
