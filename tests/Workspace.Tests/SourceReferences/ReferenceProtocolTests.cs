using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Analysis;
using Xunit;

namespace Workspace.Tests.SourceReferences;

public sealed class ReferenceProtocolTests
{
    [Fact]
    public void Optional_configuration_preserves_old_constructors_and_omission()
    {
        var old = new AnalysisSelection(AnalysisProfile.EngineStructural(), null);
        Assert.True(old.IsValid); Assert.Null(old.ReferenceConfiguration);
        var s = SyntheticReferenceFixture.Snapshot() with { Profile = old.Profile, ReferenceConfiguration = null };
        var packet = AnalysisRequestPacker.Pack(s, "legacy");
        Assert.DoesNotContain("referenceConfiguration", packet.MetadataJson);
        var dto = SyntheticReferenceFixture.Result(SyntheticReferenceFixture.Snapshot()) with { ReferenceConfiguration = null };
        Assert.DoesNotContain("referenceConfiguration", JsonSerializer.Serialize(dto, AnalysisJson.Options));
        Assert.False(AnalysisCatalog.IsKnown(AnalysisCatalog.ReferenceId, "2.0.0"));
    }

    [Fact]
    public void New_request_has_only_reference_configuration_and_exact_owned_bytes()
    {
        var s = SyntheticReferenceFixture.Snapshot(); var before = s.Files.Select(f => f.Bytes.ToArray()).ToArray();
        var (json, bytes) = AnalysisRequestPacker.Pack(s, "request");
        var p = JsonNode.Parse(json)!["payload"]!;
        Assert.Equal(s.ReferenceConfiguration!.RootFileId, p["referenceConfiguration"]!["rootFileId"]!.GetValue<string>());
        Assert.Null(p["configuration"]); Assert.Null(p["knu"]); Assert.Null(p["referenceReport"]);
        Assert.Equal(s.Files.SelectMany(f => f.Bytes), bytes);
        bytes[0] = 0; // caller-owned packet cannot mutate immutable capture
        for (int n = 0; n < s.Files.Length; n++) Assert.Equal(before[n], s.Files[n].Bytes.ToArray());
    }

    [Theory]
    [InlineData("null")][InlineData("{}")][InlineData("{\"rootFileId\":null}")]
    [InlineData("{\"rootFileId\":3}")][InlineData("{\"rootFileId\":\"\"}")]
    [InlineData("{\"rootFileId\":\"a\",\"extra\":true}")]
    [InlineData("{\"rootFileId\":\"a\",\"rootFileId\":\"b\"}")]
    [InlineData("{\"RootFileId\":\"a\"}")]
    public void Configuration_is_exact_nonnull_and_duplicate_safe(string json)
        => Assert.Throws<JsonException>(() => AnalysisJson.ParseReferenceConfiguration(json));

    [Theory]
    [InlineData("missing")][InlineData("null")][InlineData("empty")][InlineData("oversized")][InlineData("knu")]
    public void Invalid_selections_cannot_pack(string fault)
    {
        var s = SyntheticReferenceFixture.Snapshot();
        s = fault switch {
            "missing" or "null" => s with { ReferenceConfiguration = null },
            "empty" => s with { ReferenceConfiguration = new("") },
            "oversized" => s with { ReferenceConfiguration = new(new string('x',129)) },
            _ => s with { Configuration = new(AnalysisWorkType.Unspecified) },
        };
        Assert.Throws<ArgumentException>(() => AnalysisRequestPacker.Pack(s, "request"));
    }

    [Theory]
    [InlineData("unknown")][InlineData("nontex")][InlineData("path")][InlineData("duplicatePath")]
    public void Root_and_canonical_descriptor_membership_checked_before_pack(string fault)
    {
        var s = SyntheticReferenceFixture.Snapshot(); var f = s.Files[0];
        s = fault switch {
            "unknown" => s with { ReferenceConfiguration = new("unknown") },
            "nontex" => s with { Files = [f with { RelativePath = "notes.txt" }, s.Files[1]] },
            "path" => s with { Files = [f with { RelativePath = "../escape.tex" }, s.Files[1]] },
            _ => s with { Files = [f, s.Files[1] with { RelativePath = f.RelativePath }] },
        };
        Assert.Throws<JsonException>(() => AnalysisRequestPacker.Pack(s, "request"));
    }

    [Fact]
    public async Task Unknown_root_is_correlated_InvalidInput_before_any_bridge_import()
    {
        var snapshot = SyntheticReferenceFixture.Snapshot() with { ReferenceConfiguration = new("unknown") };
        var js = new KnuProtocolTests.FixtureJs((_, _) => throw new InvalidOperationException("must not dispatch"));
        await using var analyzer = new RustSourceAnalyzer(js);
        var result = Assert.IsType<AnalysisJobResult.Failed>(await analyzer.AnalyzeAsync(snapshot, CancellationToken.None));
        Assert.Equal("InvalidInput", result.Error.Code); Assert.False(string.IsNullOrEmpty(result.RequestId));
        Assert.False(js.ImportReached.Task.IsCompleted); Assert.Empty(js.Calls);
    }

    [Theory]
    [InlineData("missing")][InlineData("null")][InlineData("extraConfig")][InlineData("foreignConfig")]
    [InlineData("unknownRoot")][InlineData("unknownProfile")][InlineData("unknownVersion")]
    [InlineData("knu")][InlineData("fix")][InlineData("report")][InlineData("extraEnvelope")]
    [InlineData("missingRule")][InlineData("duplicateRule")][InlineData("foreignRule")][InlineData("origin")]
    [InlineData("severity")][InlineData("message")][InlineData("derivedId")][InlineData("fileFirstId")][InlineData("missingSpan")]
    [InlineData("spanOutside")][InlineData("extraSpan")][InlineData("missingLocation")]
    [InlineData("diagnosticRef")][InlineData("duplicateRef")][InlineData("unlisted")]
    [InlineData("perFileMismatch")][InlineData("rootNotReachable")][InlineData("unknownLimitation")]
    [InlineData("mandatoryLimitation")][InlineData("duplicateLimitation")][InlineData("uncertainFail")][InlineData("uncertainMissing")]
    [InlineData("aggregate")][InlineData("ruleAggregate")][InlineData("fileLengthOverflow")]
    [InlineData("extraDiagnostic")][InlineData("normative")][InlineData("extraToken")]
    public void Strict_synthetic_response_negatives(string fault)
    {
        var root = JsonNode.Parse(SyntheticReferenceFixture.Reply(SyntheticReferenceFixture.Snapshot(), "request"))!;
        var p = root["payload"]!; var rr = p["ruleResults"]!; var d = p["diagnostics"]![0]!;
        switch (fault)
        {
            case "missing": p.AsObject().Remove("referenceConfiguration"); break;
            case "null": p["referenceConfiguration"] = null; break;
            case "extraConfig": p["referenceConfiguration"]!["extra"] = true; break;
            case "foreignConfig": p["configuration"] = new JsonObject { ["workType"] = "unspecified" }; break;
            case "unknownRoot": p["referenceConfiguration"]!["rootFileId"] = "unknown"; break;
            case "unknownProfile": p["profile"]!["id"] = "unknown"; break;
            case "unknownVersion": p["profile"]!["version"] = "2.0.0"; break;
            case "knu": p["knu"] = new JsonObject(); break;
            case "fix": d["fixDescriptors"] = new JsonArray(); break;
            case "report": p["referenceReport"] = new JsonObject(); break;
            case "extraEnvelope": root["extra"] = true; break;
            case "missingRule": rr.AsArray().RemoveAt(1); break;
            case "duplicateRule": rr[2] = rr[1]!.DeepClone(); break;
            case "foreignRule": rr[1]!["ruleId"] = "KNU-SRC-MARGINS-001"; break;
            case "origin": d["origin"] = "knu"; break;
            case "severity": d["severity"] = "error"; break;
            case "message": d["message"] = "raw key alleged by worker"; break;
            case "derivedId": d["id"] = "wrong"; rr[2]!["perFile"]![0]!["diagnosticIds"]![0] = "wrong"; break;
            case "fileFirstId":
                string oldOrder = d["fileId"]!.GetValue<string>() + ":" + AnalysisCatalog.References + ":" + d["span"]!["startByte"]!.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture);
                d["id"] = oldOrder; rr[2]!["perFile"]![0]!["diagnosticIds"]![0] = oldOrder; break;
            case "missingSpan": d["span"] = null; break;
            case "spanOutside": d["span"]!["endByte"] = 999999; break;
            case "extraSpan": d["span"]!["column"] = 5; break;
            case "missingLocation": d.AsObject().Remove("primaryLocation"); break;
            case "diagnosticRef": rr[2]!["perFile"]![0]!["diagnosticIds"]![0] = "missing"; break;
            case "duplicateRef": rr[2]!["perFile"]![0]!["diagnosticIds"]!.AsArray().Add(d["id"]!.GetValue<string>()); break;
            case "unlisted": rr[2]!["perFile"]![0]!["diagnosticIds"] = new JsonArray(); break;
            case "perFileMismatch": rr[1]!["perFile"]![0]!["fileId"] = p["facts"]!["files"]![1]!["fileId"]!.DeepClone(); break;
            case "rootNotReachable": rr[1]!["perFile"] = new JsonArray(); rr[2]!["perFile"] = new JsonArray(); break;
            case "unknownLimitation": rr[1]!["limitations"]!.AsArray().Add("fullCompliance"); break;
            case "mandatoryLimitation": rr[1]!["limitations"] = new JsonArray("sourceIntentOnly"); break;
            case "duplicateLimitation": rr[1]!["limitations"]!.AsArray().Add("sourceIntentOnly"); break;
            case "uncertainFail": rr[2]!["limitations"]!.AsArray().Add("missingInclude"); break;
            case "uncertainMissing":
                rr[2]!["limitations"]!.AsArray().Add("missingInclude"); rr[2]!["outcome"] = "indeterminate";
                rr[2]!["perFile"]![0]!["outcome"] = "indeterminate"; p["aggregateOutcome"] = "indeterminate"; break;
            case "aggregate": p["aggregateOutcome"] = "pass"; break;
            case "ruleAggregate": rr[2]!["outcome"] = "pass"; break;
            case "fileLengthOverflow": p["facts"]!["files"]![0]!["byteLength"] = long.MaxValue; p["facts"]!["files"]![1]!["byteLength"] = long.MaxValue; break;
            case "extraDiagnostic": d["rawKey"] = "untrusted"; break;
            case "normative": rr[1]!["source"] = new JsonObject(); break;
            case "extraToken": p["facts"]!["files"]![0]!["tokens"] = new JsonArray(new JsonObject { ["kind"]="label",["startByte"]=0,["endByte"]=1,["extra"]=true }); p["facts"]!["files"]![0]!["tokenCount"] = 1; break;
        }
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(root.ToJsonString()));
    }

    [Fact]
    public void Synthetic_known_duplicate_occurrences_survive_uncertainty_without_deduplication()
    {
        // Codec-only prescribed graph/facts, not a claim about executing fixture TeX.
        var node = JsonNode.Parse(SyntheticReferenceFixture.Reply(SyntheticReferenceFixture.Snapshot(), "request"))!;
        var p = node["payload"]!; var rules = p["ruleResults"]!;
        var template = p["diagnostics"]![0]!.DeepClone(); var diagnostics = new JsonArray();
        var labels = new JsonArray(); var references = new JsonArray();
        foreach (var fact in p["facts"]!["files"]!.AsArray())
        {
            var entry = fact!;
            var id = entry["fileId"]!.GetValue<string>(); entry["tokens"]![0]!["kind"] = "label";
            var diagnostic = template.DeepClone(); diagnostic["fileId"] = id;
            diagnostic["ruleId"] = AnalysisCatalog.Labels; diagnostic["message"] = ReferenceChecks.DuplicateMessage;
            string did = AnalysisCatalog.Labels + ":" + id + ":" + diagnostic["span"]!["startByte"]!.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture);
            diagnostic["id"] = did; diagnostics.Add(diagnostic);
            labels.Add(new JsonObject { ["fileId"]=id,["outcome"]="indeterminate",["diagnosticIds"]=new JsonArray(did) });
            references.Add(new JsonObject { ["fileId"]=id,["outcome"]="indeterminate",["diagnosticIds"]=new JsonArray() });
        }
        p["diagnostics"] = diagnostics; rules[1]!["perFile"] = labels; rules[2]!["perFile"] = references;
        for (int n = 1; n <= 2; n++) { rules[n]!["outcome"] = "indeterminate"; rules[n]!["limitations"]!.AsArray().Add("unsupportedContext"); }
        p["aggregateOutcome"] = "indeterminate";
        var result = Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(node.ToJsonString())).Result;
        Assert.Equal(2, result.Diagnostics.Count); Assert.All(result.Diagnostics, d => Assert.Equal(ReferenceChecks.DuplicateMessage, d.Message));
        Assert.Equal(RuleOutcome.Indeterminate, result.AggregateOutcome);
    }

    [Fact]
    public async Task Lowered_response_and_emission_limits_are_not_ignored()
    {
        var original = SyntheticReferenceFixture.Snapshot();
        foreach (var limits in new[] { original.Limits with { MaxResultBytes = 1 }, original.Limits with { MaxDiagnosticsPerProject = 0 },
            original.Limits with { MaxTokensPerProject = 1 }, original.Limits with { MaxTokensPerFile = 0 } })
        {
            var snapshot = original with { Limits = limits };
            var js = new KnuProtocolTests.FixtureJs((_, rid) => SyntheticReferenceFixture.Reply(snapshot, rid));
            await using var analyzer = new RustSourceAnalyzer(js);
            var failure = Assert.IsType<AnalysisJobResult.Failed>(await analyzer.AnalyzeAsync(snapshot, CancellationToken.None));
            Assert.Equal("InvalidInput", failure.Error.Code);
        }
    }

    [Fact]
    public void Duplicate_configuration_and_foreign_null_are_rejected()
    {
        var s = SyntheticReferenceFixture.Snapshot(); var json = SyntheticReferenceFixture.Reply(s, "request");
        string pair = "\"rootFileId\":\"" + s.ReferenceConfiguration!.RootFileId + "\"";
        var duplicate = json.Replace(pair, pair + "," + pair, StringComparison.Ordinal);
        Assert.NotEqual(json, duplicate); Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(duplicate));
        var node = JsonNode.Parse(json)!; node["payload"]!["profile"] = new JsonObject { ["id"]="engine-structural",["version"]="0" };
        node["payload"]!["referenceConfiguration"] = null;
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(node.ToJsonString()));
    }

    [Fact]
    public async Task Positive_synthetic_wire_roundtrip_and_exact_unicode_navigation_span()
    {
        var s = SyntheticReferenceFixture.Snapshot(); var json = SyntheticReferenceFixture.Reply(s, "request");
        var completed = Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(json));
        Assert.Equal(s.ReferenceConfiguration, completed.Result.ReferenceConfiguration); Assert.Null(completed.Result.Knu);
        var js = new KnuProtocolTests.FixtureJs((_, rid) => SyntheticReferenceFixture.Reply(s, rid));
        await using var analyzer = new RustSourceAnalyzer(js);
        var result = Assert.IsType<AnalysisJobResult.Completed>(await analyzer.AnalyzeAsync(s, CancellationToken.None));
        var d = Assert.Single(result.Result.Diagnostics);
        Assert.Equal(AnalysisCatalog.References + ":" + d.FileId + ":" + d.Span!.StartByte.ToString(System.Globalization.CultureInfo.InvariantCulture), d.Id);
        var mapped = Assert.IsType<SpanMappingResult.Exact>(SpanMapping.Map(s.Files[0], d.FileId, d.Span!.StartByte, d.Span!.EndByte));
        Assert.Equal(SyntheticReferenceFixture.Key, mapped.ExpectedNormalizedSubstring);
        Assert.Equal(SyntheticReferenceFixture.Key.Length, mapped.TextareaEnd - mapped.TextareaStart);
        Assert.Equal(3, mapped.Line); Assert.Equal(5, mapped.Column);
    }

    [Fact]
    public void Raw_metadata_only_binary_fact_is_outside_structural_scan_scope()
    {
        // Synthetic transport shape matching the producer's binary-target
        // policy, NOT a substitute for root's actual exported Rust fixture.
        var node = JsonNode.Parse(SyntheticReferenceFixture.Reply(SyntheticReferenceFixture.Snapshot(), "request"))!;
        node["payload"]!["facts"]!["files"]!.AsArray().Add(new JsonObject {
            ["fileId"]="binary-target", ["byteLength"]=2, ["hasBom"]=false, ["newlineKind"]="lf",
            ["truncated"]=false, ["braceDepthExceeded"]=false, ["tokenCount"]=0,
            ["engineOutcome"]="notApplicable", ["tokens"]=new JsonArray(),
        });
        var result = Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(node.ToJsonString())).Result;
        Assert.Equal(3, result.Facts.Files.Count);
        Assert.Equal(2, result.RuleResults.Single(r => r.RuleId == AnalysisCatalog.Engine).PerFile.Count);
    }

    [Theory]
    [InlineData("normalTokens")][InlineData("normalZeroTokens")][InlineData("uncertainZeroTokens")]
    [InlineData("naWithTokens")][InlineData("naTruncated")][InlineData("naDepth")]
    public void Structural_omissions_cannot_hide_scanned_or_inconsistent_facts(string fault)
    {
        var node = JsonNode.Parse(SyntheticReferenceFixture.Reply(SyntheticReferenceFixture.Snapshot(), "request"))!;
        var p = node["payload"]!; var file = p["facts"]!["files"]![1]!;
        p["ruleResults"]![0]!["perFile"]!.AsArray().RemoveAt(1);
        if (fault is not ("normalTokens" or "naWithTokens"))
        { file["tokens"] = new JsonArray(); file["tokenCount"] = 0; }
        if (fault.StartsWith("na", StringComparison.Ordinal)) file["engineOutcome"] = "notApplicable";
        if (fault == "uncertainZeroTokens") file["engineOutcome"] = "indeterminate";
        if (fault == "naTruncated") file["truncated"] = true;
        if (fault == "naDepth") file["braceDepthExceeded"] = true;
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(node.ToJsonString()));
    }

    [Fact]
    public async Task Metadata_only_raw_omission_cannot_hide_a_captured_eligible_text_file()
    {
        var snapshot = SyntheticReferenceFixture.Snapshot();
        var node = JsonNode.Parse(SyntheticReferenceFixture.Reply(snapshot, "request"))!; var p = node["payload"]!;
        var file = p["facts"]!["files"]![1]!;
        file["engineOutcome"] = "notApplicable"; file["tokenCount"] = 0; file["tokens"] = new JsonArray();
        p["ruleResults"]![0]!["perFile"]!.AsArray().RemoveAt(1);
        Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(node.ToJsonString()));
        var js = new KnuProtocolTests.FixtureJs((_, rid) => { node["requestId"] = rid; return node.ToJsonString(); });
        await using var analyzer = new RustSourceAnalyzer(js);
        var failure = Assert.IsType<AnalysisJobResult.Failed>(await analyzer.AnalyzeAsync(snapshot, CancellationToken.None));
        Assert.Equal("InvalidInput", failure.Error.Code);
    }

    [Theory]
    [InlineData("echo")][InlineData("byteLength")][InlineData("bom")][InlineData("scalar")]
    [InlineData("primary")][InlineData("snapshot")][InlineData("order")][InlineData("factText")]
    public async Task Structurally_valid_result_still_requires_exact_capture_correlation(string fault)
    {
        var s = SyntheticReferenceFixture.Snapshot(); var node = JsonNode.Parse(SyntheticReferenceFixture.Reply(s, "request"))!; var p = node["payload"]!;
        switch (fault)
        {
            case "echo":
                var other = s with { ReferenceConfiguration = new(s.Files[1].FileId.ToString()) };
                node = JsonNode.Parse(SyntheticReferenceFixture.Reply(other, "request"))!; break;
            case "byteLength": p["facts"]!["files"]![0]!["byteLength"] = s.Files[0].ByteLength + 1; break;
            case "bom": p["facts"]!["files"]![0]!["hasBom"] = false; break;
            case "scalar":
                long end = p["diagnostics"]![0]!["span"]!["endByte"]!.GetValue<long>() - 1;
                p["diagnostics"]![0]!["span"]!["endByte"] = end;
                p["facts"]!["files"]![0]!["tokens"]![0]!["endByte"] = end;
                break;
            case "primary": p["diagnostics"]![0]!["primaryLocation"]!["column"] = 6; break;
            case "factText": p["facts"]!["files"]![0]!["tokens"]![0]!["text"] = "different"; break;
            case "snapshot": p["snapshotId"] = "other"; break;
            case "order":
                var files = p["facts"]!["files"]!; var first = files[0]!.DeepClone(); files[0] = files[1]!.DeepClone(); files[1] = first;
                // Keep the synthetic wire internally consistent; only the
                // immutable capture order should reject this completed result.
                var structural = p["ruleResults"]![0]!["perFile"]!;
                var firstOutcome = structural[0]!.DeepClone(); structural[0] = structural[1]!.DeepClone(); structural[1] = firstOutcome;
                break;
        }
        Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(node.ToJsonString()));
        var js = new KnuProtocolTests.FixtureJs((_, rid) => { node["requestId"] = rid; return node.ToJsonString(); });
        await using var analyzer = new RustSourceAnalyzer(js);
        var failure = Assert.IsType<AnalysisJobResult.Failed>(await analyzer.AnalyzeAsync(s, CancellationToken.None));
        Assert.Equal("InvalidInput", failure.Error.Code);
    }
}
