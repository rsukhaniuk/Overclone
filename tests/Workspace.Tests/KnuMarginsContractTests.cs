using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Analysis;
using Xunit;

namespace Workspace.Tests;

public sealed class KnuMarginsContractTests
{
    [Theory]
    [InlineData(AnalysisWorkType.Unspecified)]
    [InlineData(AnalysisWorkType.Coursework)]
    [InlineData(AnalysisWorkType.BachelorQualification)]
    [InlineData(AnalysisWorkType.MasterQualification)]
    public void KnuLatest_is_explicit_and_preserves_legacy_selection(AnalysisWorkType workType)
    {
        var legacy = AnalysisSelection.Knu(workType);
        var latest = AnalysisSelection.KnuLatest(workType);
        Assert.Equal("1.0.0", AnalysisCatalog.Version);
        Assert.Equal("1.0.0", legacy.Profile.Version);
        Assert.Equal("1.1.0", latest.Profile.Version);
        Assert.Equal(legacy.Profile.Id, latest.Profile.Id);
        Assert.Equal(legacy.Configuration, latest.Configuration);
        Assert.True(legacy.IsValid);
        Assert.True(latest.IsValid);
        var snapshot = KnuFixture.Load("positive").Snapshot() with { Profile = latest.Profile, Configuration = latest.Configuration };
        var (json, _) = AnalysisRequestPacker.Pack(snapshot);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("1.1.0", doc.RootElement.GetProperty("payload").GetProperty("profile").GetProperty("version").GetString());
    }

    [Theory]
    [InlineData("knu-csc-2025-source", "latest")]
    [InlineData("knu-csc-2025-source", "1.1")]
    [InlineData("knu-csc-2025-source", "1.1.1")]
    [InlineData("knu-csc-2025-source", "1.7.0")]
    [InlineData("unknown", "1.1.0")]
    [InlineData("engine-structural", "1.1.0")]
    public void Catalog_rejects_nonexact_versions_and_unknown_ids(string id, string version)
    {
        Assert.False(AnalysisCatalog.IsKnown(id, version));
        var snapshot = KnuFixture.Load("positive").Snapshot() with { Profile = new(id, version) };
        Assert.False(snapshot.Selection.IsValid);
        Assert.Throws<ArgumentException>(() => AnalysisRequestPacker.Pack(snapshot));
        var response = KnuFixture.Load("positive").Response;
        response["payload"]!["profile"]!["id"] = id;
        response["payload"]!["profile"]!["version"] = version;
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(response.ToJsonString()));
    }

    [Theory]
    [InlineData("unsupportedGeometry")]
    [InlineData("missingMarginSides")]
    [InlineData("multipleGeometryDeclarations")]
    public async Task Legacy_rejects_latest_only_limitations(string limitation)
    {
        var fixture = KnuFixture.Load("uncertain");
        fixture.Response["payload"]!["knu"]!["files"]![0]!["limitations"]!.AsArray().Add(limitation);
        await AssertRejected(fixture);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("sourceMetadata")]
    [InlineData("sourceRule")]
    [InlineData("observation")]
    public async Task Legacy_rejects_latest_metadata_rules_and_observations(string kind)
    {
        var legacy = KnuFixture.Load("positive");
        var latest = KnuFixture.Load("geometry-positive");
        var p = legacy.Response["payload"]!;
        var latestPayload = latest.Response["payload"]!;
        switch (kind)
        {
            case "metadata": p["knu"]!["metadata"] = latestPayload["knu"]!["metadata"]!.DeepClone(); break;
            case "sourceMetadata": p["ruleResults"]![1]!["source"] = MarginRule(latest)["source"]!.DeepClone(); break;
            case "sourceRule": p["ruleResults"]!.AsArray().Add(MarginRule(latest).DeepClone()); break;
            case "observation":
                var observations = p["knu"]!["files"]![0]!["observations"]!.AsArray();
                observations.Add(Margin(latest).DeepClone());
                p["knu"]!["files"]![0]!["observationCount"] = observations.Count;
                break;
        }
        await AssertRejected(legacy);
    }

    [Theory]
    [InlineData("geometry-positive", RuleOutcome.Pass)]
    [InlineData("geometry-negative", RuleOutcome.Fail)]
    [InlineData("geometry-uncertain", RuleOutcome.Indeterminate)]
    [InlineData("geometry-missing", RuleOutcome.Indeterminate)]
    [InlineData("geometry-multiple", RuleOutcome.Indeterminate)]
    [InlineData("geometry-duplicate", RuleOutcome.Indeterminate)]
    [InlineData("geometry-nonliteral", RuleOutcome.Indeterminate)]
    [InlineData("geometry-unsupported-length", RuleOutcome.Indeterminate)]
    [InlineData("geometry-indent-only", RuleOutcome.Indeterminate)]
    [InlineData("geometry-indent-conflict", RuleOutcome.Pass)]
    [InlineData("geometry-fix", RuleOutcome.Indeterminate)]
    [InlineData("geometry-budget", RuleOutcome.Indeterminate)]
    public async Task Actual_Rust_margin_reports_roundtrip_and_correlate(string name, RuleOutcome marginOutcome)
    {
        var fixture = KnuFixture.Load(name);
        var snapshot = fixture.Snapshot();
        Assert.Equal("1.1.0", snapshot.Profile.Version);
        Assert.Equal(fixture.Data["sourceSha256"]!.GetValue<string>(), snapshot.Files[0].Sha256.ToString());
        var parsed = Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(fixture.Json));
        Assert.Equal(4, parsed.Result.RuleResults.Count);
        var metadata = parsed.Result.Knu!.Metadata;
        Assert.Equal(3, metadata.Rules.Length);
        var margin = Assert.Single(metadata.Rules, r => r.RuleId == AnalysisCatalog.Margins);
        Assert.Equal("1.0.0", margin.Version);
        Assert.Equal("warning", margin.Severity);
        Assert.Equal(AnalysisEvaluationDomain.SourceObservation, margin.EvaluationDomain);
        Assert.Equal("3.1", margin.Citation.Section);
        Assert.Equal(new[] { 18 }, margin.Citation.PrintedPages);
        Assert.Equal(new[] { 18 }, margin.Citation.PdfPages);
        Assert.Equal("8be4d724ac108488e6b1766322e7fd6b3646412aa2b6de12b6945f48fb380714", metadata.GuidelineSha256);
        Assert.Equal(marginOutcome, parsed.Result.RuleResults.Single(r => r.RuleId == AnalysisCatalog.Margins).Outcome);
        var roundtrip = JsonSerializer.SerializeToElement(new AnalysisResponseEnvelope(1, parsed.RequestId, ResponseStatus.Completed, parsed.Result, null), AnalysisJson.Options);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(fixture.Response), roundtrip));
        var (request, packed) = AnalysisRequestPacker.Pack(snapshot);
        Assert.Equal(fixture.Bytes, packed);
        using var doc = JsonDocument.Parse(request);
        Assert.Equal("1.1.0", doc.RootElement.GetProperty("payload").GetProperty("profile").GetProperty("version").GetString());
        var js = new KnuProtocolTests.FixtureJs((_, rid) => fixture.Reply(snapshot, rid));
        await using var analyzer = new RustSourceAnalyzer(js);
        Assert.IsType<AnalysisJobResult.Completed>(await analyzer.AnalyzeAsync(snapshot, CancellationToken.None));
        Assert.Equal(fixture.Bytes, snapshot.Files[0].Bytes.ToArray());
        Assert.Equal(new[] { "analyzeSource" }, js.Calls);
    }

    [Fact]
    public void Latest_margin_values_are_exact_transport_facts_not_a_second_numeric_engine()
    {
        var fixture = KnuFixture.Load("geometry-positive");
        var result = Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(fixture.Json)).Result;
        var observations = result.Knu!.Files.SelectMany(f => f.Observations).Where(o => o.RuleId == AnalysisCatalog.Margins).ToArray();
        Assert.Equal(4, observations.Length);
        Assert.Equal(Enum.GetValues<AnalysisMarginSide>().Order(), observations.Select(o => Assert.IsType<AnalysisMarginValueDto>(o.Value).Side).Order());
        Assert.All(observations, o => {
            Assert.Equal("literal-geometry-option", o.Method);
            Assert.NotNull(Assert.IsType<AnalysisMarginValueDto>(o.Value).Length);
            Assert.True(o.Agrees);
            Assert.Null(o.Reason);
        });
        // Producer owns the numerical predicate: a structurally valid length
        // remains transport data even when it does not match reported agrees.
        Margin(fixture)["value"]!["length"]!["numeratorMm"] = 0;
        Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(fixture.Json));
    }

    [Theory]
    [InlineData("unknownSide")][InlineData("numericSide")][InlineData("missingSide")]
    [InlineData("missingLength")][InlineData("extraValue")][InlineData("kind")]
    [InlineData("method")][InlineData("rule")][InlineData("ruleVersion")]
    [InlineData("unit")][InlineData("denominator")][InlineData("numerator")]
    [InlineData("agreesNull")][InlineData("lengthNull")][InlineData("reason")]
    [InlineData("spanNegative")][InlineData("spanOutside")][InlineData("commandSpan")]
    [InlineData("evidenceCount")][InlineData("missingEvidence")][InlineData("extraRule")]
    [InlineData("missingRule")][InlineData("metadata")][InlineData("citation")]
    public async Task Margin_wire_tampering_is_rejected_at_both_public_boundaries(string fault)
    {
        var fixture = KnuFixture.Load("geometry-positive");
        var p = fixture.Response["payload"]!;
        var o = Margin(fixture);
        var value = o["value"]!;
        switch (fault)
        {
            case "unknownSide": value["side"] = "inner"; break;
            case "numericSide": value["side"] = 0; break;
            case "missingSide": value.AsObject().Remove("side"); break;
            case "missingLength": value.AsObject().Remove("length"); break;
            case "extraValue": value["targetSpan"] = o["valueSpan"]!.DeepClone(); break;
            case "kind": value["kind"] = "unknownMargin"; break;
            case "method": o["method"] = "literal-setlength"; break;
            case "rule": o["ruleId"] = AnalysisCatalog.Indent; break;
            case "ruleVersion": MarginRule(fixture)["source"]!["version"] = "1.1.0"; break;
            case "unit": value["length"]!["unit"] = "pt"; break;
            case "denominator": value["length"]!["denominator"] = 0; break;
            case "numerator": value["length"]!["numeratorMm"] = JsonNode.Parse("18446744073709551616"); break;
            case "agreesNull": o["agrees"] = null; break;
            case "lengthNull": value["length"] = null; break;
            case "reason": o["reason"] = "unsupportedGeometry"; break;
            case "spanNegative": o["valueSpan"]!["startByte"] = -1; break;
            case "spanOutside": o["valueSpan"]!["endByte"] = 999999; break;
            case "commandSpan": o["commandSpan"]!["endByte"] = 999999; break;
            case "evidenceCount": p["knu"]!["files"]![0]!["observationCount"] = 0; break;
            case "missingEvidence": MarginRule(fixture)["evidenceIds"] = new JsonArray(); break;
            case "extraRule": p["ruleResults"]!.AsArray().Add(MarginRule(fixture).DeepClone()); break;
            case "missingRule": p["ruleResults"]!.AsArray().Remove(MarginRule(fixture)); break;
            case "metadata": p["knu"]!["metadata"]!["profile"]!["version"] = "1.0.0"; break;
            case "citation": MarginRule(fixture)["source"]!["citation"]!["pdfPages"]![0] = 19; break;
        }
        await AssertRejected(fixture);
    }

    [Theory]
    [InlineData("geometry-uncertain")][InlineData("geometry-missing")]
    [InlineData("geometry-multiple")][InlineData("geometry-duplicate")]
    [InlineData("geometry-unsupported-length")][InlineData("geometry-indent-only")]
    public void Margin_only_uncertainty_preserves_known_indent_and_heading_outcomes(string name)
    {
        var result = Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(KnuFixture.Load(name).Json)).Result;
        Assert.True(result.Knu!.Files[0].Complete);
        Assert.Equal(RuleOutcome.Pass, result.RuleResults.Single(r => r.RuleId == AnalysisCatalog.Indent).Outcome);
        Assert.Equal(RuleOutcome.Pass, result.RuleResults.Single(r => r.RuleId == AnalysisCatalog.Heading).Outcome);
        Assert.Equal(RuleOutcome.Indeterminate, result.RuleResults.Single(r => r.RuleId == AnalysisCatalog.Margins).Outcome);
        Assert.Equal(RuleOutcome.Indeterminate, result.AggregateOutcome);
    }

    [Fact]
    public void Dynamic_geometry_arguments_retain_common_uncertainty_for_all_source_rules()
    {
        var result = Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(KnuFixture.Load("geometry-nonliteral").Json)).Result;
        Assert.Contains(AnalysisLimitation.UnsupportedArgument, result.Knu!.Files[0].Limitations);
        Assert.Contains(AnalysisLimitation.UnsupportedGeometry, result.Knu.Files[0].Limitations);
        Assert.All(result.RuleResults.Where(r => r.Origin == "knu"), r => Assert.Equal(RuleOutcome.Indeterminate, r.Outcome));
        Assert.DoesNotContain(result.Knu.Files[0].Observations, o => o.RuleId == AnalysisCatalog.Margins);
        Assert.Empty(result.Knu.FixDescriptors);
    }

    [Theory]
    [InlineData("missingMarginSides")][InlineData("multipleGeometryDeclarations")]
    public async Task File_only_margin_limitations_cannot_be_forged_into_observation_reasons(string reason)
    {
        var fixture = KnuFixture.Load("geometry-unsupported-length");
        var unknown = fixture.Response["payload"]!["knu"]!["files"]![0]!["observations"]!.AsArray()
            .Single(o => o!["reason"]?.GetValue<string>() == "unsupportedGeometry")!;
        unknown["reason"] = reason;
        fixture.Response["payload"]!["knu"]!["files"]![0]!["limitations"]!.AsArray().Add(reason);
        await AssertRejected(fixture);
    }

    [Fact]
    public void Indent_only_uncertainty_does_not_contaminate_margin_outcome()
    {
        var result = Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(KnuFixture.Load("geometry-indent-conflict").Json)).Result;
        Assert.Contains(AnalysisLimitation.ConflictingDeclarations, result.Knu!.Files[0].Limitations);
        Assert.Equal(RuleOutcome.Indeterminate, result.RuleResults.Single(r => r.RuleId == AnalysisCatalog.Indent).Outcome);
        Assert.Equal(RuleOutcome.Pass, result.RuleResults.Single(r => r.RuleId == AnalysisCatalog.Margins).Outcome);
    }

    [Fact]
    public void Margin_uncertainty_does_not_hide_violations_or_disable_the_legacy_indent_fix()
    {
        var result = Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(KnuFixture.Load("geometry-fix").Json)).Result;
        Assert.Equal(RuleOutcome.Indeterminate, result.AggregateOutcome);
        Assert.Equal(RuleOutcome.Fail, result.RuleResults.Single(r => r.RuleId == AnalysisCatalog.Indent).Outcome);
        Assert.Equal(RuleOutcome.Indeterminate, result.RuleResults.Single(r => r.RuleId == AnalysisCatalog.Margins).Outcome);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.RuleId == AnalysisCatalog.Margins);
        Assert.Equal("warning", diagnostic.Severity);
        var fix = Assert.Single(result.Knu!.FixDescriptors);
        Assert.Equal("1.0.0", fix.Version);
        Assert.Equal("1.0.0", fix.RuleVersion);
        Assert.Equal("1.1.0", fix.Profile.Version);
        Assert.Equal(AnalysisCatalog.Indent, fix.RuleId);
    }

    [Fact]
    public async Task The_same_geometry_source_keeps_its_legacy_unknown_semantics()
    {
        var fixture = KnuFixture.Load("geometry-legacy");
        var result = Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(fixture.Json)).Result;
        Assert.Equal("1.0.0", result.Profile.Version);
        Assert.Equal(3, result.RuleResults.Count);
        Assert.Equal(2, result.Knu!.Metadata.Rules.Length);
        Assert.Contains(AnalysisLimitation.UnknownSemantics, result.Knu.Files[0].Limitations);
        Assert.DoesNotContain(result.Knu.Files[0].Observations, o => o.RuleId == AnalysisCatalog.Margins);
        var snapshot = fixture.Snapshot();
        var js = new KnuProtocolTests.FixtureJs((_, rid) => fixture.Reply(snapshot, rid));
        await using var analyzer = new RustSourceAnalyzer(js);
        Assert.IsType<AnalysisJobResult.Completed>(await analyzer.AnalyzeAsync(snapshot, CancellationToken.None));
    }

    [Fact]
    public void Trimmed_margin_value_and_diagnostic_spans_map_across_BOM_Ukrainian_emoji_and_CRLF()
    {
        var fixture = KnuFixture.Load("geometry-negative");
        var result = Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(fixture.Json)).Result;
        var observation = Assert.Single(result.Knu!.Files[0].Observations, o => o.RuleId == AnalysisCatalog.Margins && o.Agrees == false);
        Assert.Equal("24.999 mm", observation.Excerpt);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.RuleId == AnalysisCatalog.Margins);
        Assert.Equal(observation.ValueSpan, diagnostic.Span);
        var file = fixture.Snapshot().Files[0];
        var mapping = Assert.IsType<SpanMappingResult.Exact>(SpanMapping.Map(file, diagnostic.FileId, diagnostic.Span!.StartByte, diagnostic.Span.EndByte));
        Assert.Equal("24.999 mm", mapping.ExpectedNormalizedSubstring);
        Assert.True(mapping.TextareaStart < mapping.StartByte);
        var raw = fixture.Bytes;
        Assert.Equal("\\geometry", System.Text.Encoding.UTF8.GetString(raw.AsSpan((int)observation.CommandSpan.StartByte, (int)(observation.CommandSpan.EndByte - observation.CommandSpan.StartByte))));
        Assert.Equal((byte)'}', raw[(int)observation.DeclarationSpan.EndByte - 1]);
    }

    [Fact]
    public void Legacy_embedded_metadata_bytes_are_preserved()
    {
        using var stream = typeof(AnalysisCatalog).Assembly.GetManifestResourceStream("Client.Analysis.knu-profile-metadata.json")!;
        Assert.Equal("348a7cd2d47bb14dcfbb050b9ef0e717de51af9e58cd6132fa7272a9b3963e37",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant());
    }

    [Fact]
    public async Task A_known_margin_value_cannot_exceed_the_producer_argument_bound()
    {
        var fixture = KnuFixture.Load("geometry-positive");
        var o = Margin(fixture);
        var end = o["valueSpan"]!["startByte"]!.GetValue<long>() + 4097;
        o["valueSpan"]!["endByte"] = end;
        o["declarationSpan"]!["endByte"] = end;
        fixture.Response["payload"]!["facts"]!["files"]![0]!["byteLength"] = end;
        await AssertRejected(fixture);
    }

    [Theory]
    [InlineData("excerpt")][InlineData("byteLength")]
    public async Task Margin_snapshot_correlations_reject_structurally_valid_forgery(string fault)
    {
        var fixture = KnuFixture.Load("geometry-positive");
        if (fault == "excerpt") Margin(fixture)["excerpt"] = "forged-value";
        else
        {
            var fact = fixture.Response["payload"]!["facts"]!["files"]![0]!;
            fact["byteLength"] = fact["byteLength"]!.GetValue<long>() + 1;
        }
        Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(fixture.Json));
        var snapshot = fixture.Snapshot();
        var js = new KnuProtocolTests.FixtureJs((_, rid) => fixture.Reply(snapshot, rid));
        await using var analyzer = new RustSourceAnalyzer(js);
        var failure = Assert.IsType<AnalysisJobResult.Failed>(await analyzer.AnalyzeAsync(snapshot, CancellationToken.None));
        Assert.Equal("InvalidInput", failure.Error.Code);
        Assert.Equal(fixture.Bytes, snapshot.Files[0].Bytes.ToArray());
    }

    [Theory]
    [InlineData("rule")][InlineData("ruleVersion")][InlineData("evidence")]
    public async Task No_margin_fix_can_be_forged_from_the_existing_indent_descriptor(string fault)
    {
        var fixture = KnuFixture.Load("geometry-fix");
        var fix = fixture.Response["payload"]!["knu"]!["fixDescriptors"]![0]!;
        switch (fault)
        {
            case "rule": fix["ruleId"] = AnalysisCatalog.Margins; break;
            case "ruleVersion": fix["ruleVersion"] = "1.1.0"; break;
            case "evidence": fix["evidenceId"] = Margin(fixture)["id"]!.DeepClone(); break;
        }
        await AssertRejected(fixture);
    }

    [Theory]
    [InlineData("missing")][InlineData("absent")][InlineData("duplicate")]
    [InlineData("multiple")][InlineData("partialPass")][InlineData("partialFail")]
    [InlineData("partialDeclarations")]
    public async Task Visible_margin_coverage_cannot_lose_its_required_uncertainty(string fault)
    {
        var name = fault switch {
            "absent" => "geometry-indent-only",
            "duplicate" => "geometry-duplicate",
            "multiple" or "partialDeclarations" => "geometry-multiple",
            "partialFail" => "geometry-negative",
            _ => "geometry-missing",
        };
        var fixture = KnuFixture.Load(name);
        var payload = fixture.Response["payload"]!;
        var file = payload["knu"]!["files"]![0]!;
        if (fault == "partialFail" || fault == "partialDeclarations")
        {
            var observations = file["observations"]!.AsArray();
            var marginObservations = observations.Where(o => o!["ruleId"]!.GetValue<string>() == AnalysisCatalog.Margins).ToArray();
            var remove = fault == "partialFail" ? new[] { marginObservations[^1] }
                : new[] { marginObservations[2], marginObservations[3], marginObservations[4], marginObservations[5] };
            foreach (var observation in remove) observations.Remove(observation);
            file["observationCount"] = observations.Count;
            MarginRule(fixture)["evidenceIds"] = new JsonArray(observations.Where(o => o!["ruleId"]!.GetValue<string>() == AnalysisCatalog.Margins)
                .Select(o => o!["id"]!.DeepClone()).ToArray());
        }
        else
        {
            var limitation = fault is "duplicate" or "multiple" ? "multipleGeometryDeclarations" : "missingMarginSides";
            var limitations = file["limitations"]!.AsArray();
            limitations.Remove(limitations.Single(l => l!.GetValue<string>() == limitation));
        }
        if (fault == "partialPass")
        {
            MarginRule(fixture)["outcome"] = "pass";
            MarginRule(fixture)["perFile"]![0]!["outcome"] = "pass";
            payload["aggregateOutcome"] = "pass";
        }
        await AssertRejected(fixture);
    }

    [Theory]
    [InlineData("snapshot")][InlineData("profile")][InlineData("generation")]
    public async Task Latest_reports_reject_stale_snapshot_version_and_fix_revision(string fault)
    {
        var fixture = KnuFixture.Load(fault == "generation" ? "geometry-fix" : "geometry-positive");
        var snapshot = fixture.Snapshot();
        if (fault == "profile") snapshot = snapshot with { Profile = new(AnalysisCatalog.KnuId, AnalysisCatalog.Version) };
        var js = new KnuProtocolTests.FixtureJs((_, rid) => {
            var reply = (JsonObject)JsonNode.Parse(fixture.Reply(snapshot, rid))!;
            if (fault == "snapshot") reply["payload"]!["snapshotId"] = new string('6', 32);
            if (fault == "generation") reply["payload"]!["knu"]!["fixDescriptors"]![0]!["workingGeneration"] = snapshot.WorkingGeneration + 1;
            return reply.ToJsonString();
        });
        await using var analyzer = new RustSourceAnalyzer(js);
        var failure = Assert.IsType<AnalysisJobResult.Failed>(await analyzer.AnalyzeAsync(snapshot, CancellationToken.None));
        Assert.Equal("InvalidInput", failure.Error.Code);
        Assert.Equal(fixture.Bytes, snapshot.Files[0].Bytes.ToArray());
    }

    private static JsonObject Margin(KnuFixture fixture) => (JsonObject)fixture.Response["payload"]!["knu"]!["files"]!.AsArray()
        .SelectMany(f => f!["observations"]!.AsArray()).First(o => o!["ruleId"]!.GetValue<string>() == AnalysisCatalog.Margins)!;
    private static JsonObject MarginRule(KnuFixture fixture) => (JsonObject)fixture.Response["payload"]!["ruleResults"]!.AsArray()
        .Single(r => r!["ruleId"]!.GetValue<string>() == AnalysisCatalog.Margins)!;

    private static async Task AssertRejected(KnuFixture fixture)
    {
        var snapshot = fixture.Snapshot();
        var parseFailure = Record.Exception(() => AnalysisJson.ParseResponse(fixture.Json));
        var js = new KnuProtocolTests.FixtureJs((_, rid) => fixture.Reply(snapshot, rid));
        await using var analyzer = new RustSourceAnalyzer(js);
        var failure = Assert.IsType<AnalysisJobResult.Failed>(await analyzer.AnalyzeAsync(snapshot, CancellationToken.None));
        Assert.Equal("InvalidInput", failure.Error.Code);
        Assert.IsType<JsonException>(parseFailure);
        Assert.Equal(fixture.Bytes, snapshot.Files[0].Bytes.ToArray());
        Assert.Equal(new[] { "analyzeSource" }, js.Calls);
    }
}
