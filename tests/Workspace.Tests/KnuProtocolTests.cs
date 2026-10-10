using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Analysis;
using Microsoft.JSInterop;
using Xunit;

namespace Workspace.Tests;

public sealed class KnuProtocolTests
{
    [Theory]
    [InlineData("positive", RuleOutcome.Pass)]
    [InlineData("negative", RuleOutcome.Fail)]
    [InlineData("uncertain", RuleOutcome.Indeterminate)]
    [InlineData("unavailable", RuleOutcome.Unavailable)]
    [InlineData("engine", RuleOutcome.Pass)]
    [InlineData("argument-bounds", RuleOutcome.Indeterminate)]
    public async Task Actual_Rust_reports_round_trip_and_correlate_without_execution(string name, RuleOutcome outcome)
    {
        var fixture = KnuFixture.Load(name);
        var snapshot = fixture.Snapshot();
        var bytes = snapshot.Files[0].Bytes;
        Assert.Equal(fixture.Data["sourceSha256"]!.GetValue<string>(), snapshot.Files[0].Sha256.ToString());
        var parsed = Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(fixture.Json));
        Assert.Equal(outcome, parsed.Result.AggregateOutcome);
        var roundtrip = JsonSerializer.SerializeToElement(new AnalysisResponseEnvelope(1, parsed.RequestId, ResponseStatus.Completed, parsed.Result, null), AnalysisJson.Options);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(fixture.Response), roundtrip));
        var (meta, packed) = AnalysisRequestPacker.Pack(snapshot, "fixture-request");
        Assert.Equal(fixture.Bytes, packed);
        using var doc = JsonDocument.Parse(meta);
        Assert.Equal(9007199254740993L, doc.RootElement.GetProperty("payload").GetProperty("workingGeneration").GetInt64());
        if (name == "engine")
        {
            Assert.False(doc.RootElement.GetProperty("payload").TryGetProperty("configuration", out _));
            Assert.Null(parsed.Result.Knu);
        }
        else
        {
            Assert.Equal(snapshot.Configuration, parsed.Result.Configuration);
            Assert.Equal(3, parsed.Result.RuleResults.Count);
            Assert.Equal(2, parsed.Result.Knu!.Metadata.Rules.Length);
            Assert.Equal(AnalysisEvaluationDomain.SourceObservation, parsed.Result.Knu.Metadata.Rules[0].EvaluationDomain);
            if (name == "positive")
            {
                var title = Assert.IsType<AnalysisHeadingValueDto>(parsed.Result.Knu.Files[0].Observations[1].Value);
                Assert.Equal("🙂", title.FinalScalar);
                Assert.Equal(2, title.FinalScalar!.Length); // one Rust scalar, two UTF-16 units
            }
            if (name == "negative") Assert.Single(parsed.Result.Knu.FixDescriptors);
        }
        var js = new FixtureJs((_, rid) => fixture.Reply(snapshot, rid));
        await using var analyzer = new RustSourceAnalyzer(js);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        Assert.IsType<AnalysisJobResult.Completed>(result);
        Assert.Equal(bytes, snapshot.Files[0].Bytes);
        Assert.Equal(fixture.Bytes, js.Packed);
        Assert.Equal(new[] { "analyzeSource" }, js.Calls); // no save, apply, script or review calls
    }

    [Theory]
    [InlineData("unspecified", AnalysisWorkType.Unspecified)]
    [InlineData("coursework", AnalysisWorkType.Coursework)]
    [InlineData("bachelorQualification", AnalysisWorkType.BachelorQualification)]
    [InlineData("masterQualification", AnalysisWorkType.MasterQualification)]
    public void Explicit_selection_and_request_configuration_are_immutable(string wire, AnalysisWorkType work)
    {
        var cfg = AnalysisJson.ParseConfiguration("{\"workType\":\"" + wire + "\"}");
        var selection = AnalysisSelection.Knu(work);
        Assert.True(selection.IsValid); Assert.Equal(cfg, selection.Configuration);
        var snapshot = KnuFixture.Load("positive").Snapshot() with { Configuration = cfg };
        var (metadata, _) = AnalysisRequestPacker.Pack(snapshot);
        using var doc = JsonDocument.Parse(metadata);
        Assert.Equal(wire, doc.RootElement.GetProperty("payload").GetProperty("configuration").GetProperty("workType").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("payload").GetProperty("profile").EnumerateObject().Count());
        Assert.Equal(cfg, snapshot.Configuration);
    }

    [Theory]
    [InlineData("null")][InlineData("{}")][InlineData("[]")][InlineData("false")]
    [InlineData("{\"workType\":0}")][InlineData("{\"workType\":null}")]
    [InlineData("{\"workType\":\"bachelor\"}")]
    [InlineData("{\"workType\":\"unspecified\",\"extra\":true}")]
    [InlineData("{\"workType\":\"unspecified\",\"workType\":\"coursework\"}")]
    public void Invalid_configuration_is_not_defaulted(string json)
        => Assert.Throws<JsonException>(() => AnalysisJson.ParseConfiguration(json));

    [Theory]
    [InlineData("unknown")][InlineData("version")][InlineData("missingConfig")]
    [InlineData("engineWithConfig")][InlineData("unknownEnum")]
    public void Invalid_selections_fail_before_transport(string fault)
    {
        var snapshot = KnuFixture.Load("positive").Snapshot();
        snapshot = fault switch {
            "unknown" => snapshot with { Profile = new("unknown", "1") },
            "version" => snapshot with { Profile = new(AnalysisCatalog.KnuId, "2") },
            "missingConfig" => snapshot with { Configuration = null },
            "engineWithConfig" => snapshot with { Profile = AnalysisProfile.EngineStructural() },
            _ => snapshot with { Configuration = new((AnalysisWorkType)999) },
        };
        Assert.False(snapshot.Selection.IsValid);
        Assert.Throws<ArgumentException>(() => AnalysisRequestPacker.Pack(snapshot));
    }

    [Theory]
    [InlineData("hash")][InlineData("metadataVersion")][InlineData("title")][InlineData("url")]
    [InlineData("profileId")][InlineData("profileVersion")][InlineData("engineDataMismatch")]
    [InlineData("citation")][InlineData("sourceVersion")][InlineData("origin")][InlineData("severity")][InlineData("domain")][InlineData("expected")]
    [InlineData("duplicateRules")][InlineData("missingRule")][InlineData("foreignRule")][InlineData("fakePdfRule")]
    [InlineData("duplicateEvidence")][InlineData("missingEvidenceRef")][InlineData("foreignEvidenceRef")]
    [InlineData("duplicateFacts")][InlineData("foreignObservationFile")][InlineData("missingFacts")]
    [InlineData("observationCount")][InlineData("tokenCount")][InlineData("negativeCount")]
    [InlineData("spanNegative")][InlineData("spanOutside")][InlineData("spanFraction")][InlineData("spanOverflow")]
    [InlineData("lengthOverflow")][InlineData("lengthNegative")][InlineData("lengthUnit")][InlineData("lengthDenominator")]
    [InlineData("discriminator")][InlineData("extraVariant")][InlineData("missingVariantField")]
    [InlineData("reasonEnum")][InlineData("titleKind")][InlineData("scalarMany")][InlineData("scalarNull")]
    [InlineData("extraPayload")][InlineData("extraRoot")][InlineData("missingConfig")][InlineData("nullConfig")][InlineData("extraConfig")][InlineData("configEnum")]
    [InlineData("engineAggregate")][InlineData("aggregate")][InlineData("eligibility")][InlineData("diagEvidence")][InlineData("diagSpan")]
    [InlineData("fixVersion")][InlineData("fixId")][InlineData("fixEvidence")][InlineData("fixSnapshot")][InlineData("fixConfiguration")]
    [InlineData("fixPath")][InlineData("fixHash")][InlineData("fixReplacement")][InlineData("fixCount")]
    [InlineData("excerptTooLarge")][InlineData("expectedTooLarge")][InlineData("coverageTruncated")]
    public void Tampered_Rust_reports_are_rejected(string fault)
    {
        var root = KnuFixture.Load("negative").Response;
        var p = root["payload"]!; var k = p["knu"]!;
        var obs = k["files"]![0]!["observations"]!; var fix = k["fixDescriptors"]![0]!;
        var rr = p["ruleResults"]!;
        switch (fault)
        {
            case "profileId": p["profile"]!["id"] = "unknown"; break;
            case "profileVersion": p["profile"]!["version"] = "9"; break;
            case "engineDataMismatch": p["profile"] = new JsonObject { ["id"]="engine-structural",["version"]="0" }; break;
            case "hash": k["metadata"]!["guidelineSha256"] = new string('0',64); break;
            case "metadataVersion": k["metadata"]!["profile"]!["version"] = "9"; break;
            case "title": k["metadata"]!["guidelineTitle"] = "other"; break;
            case "url": k["metadata"]!["approvedUrl"] = "https://invalid.example/"; break;
            case "citation": rr[1]!["source"]!["citation"]!["pdfPages"]![0] = 19; break;
            case "sourceVersion": rr[1]!["source"]!["version"] = "2"; break;
            case "origin": rr[1]!["origin"] = "engine"; break;
            case "severity": rr[1]!["source"]!["severity"] = "error"; break;
            case "domain": rr[1]!["source"]!["evaluationDomain"] = "pdf"; break;
            case "expected": rr[1]!["source"]!["expected"] = "20 cm"; break;
            case "duplicateRules": rr[2] = rr[1]!.DeepClone(); break;
            case "missingRule": rr.AsArray().RemoveAt(2); break;
            case "foreignRule": rr[1]!["ruleId"] = "FOREIGN"; break;
            case "fakePdfRule": rr[2]!["ruleId"] = "PDF-001"; break;
            case "duplicateEvidence": obs.AsArray().Add(obs[0]!.DeepClone()); k["files"]![0]!["observationCount"] = 3; break;
            case "missingEvidenceRef": rr[1]!["evidenceIds"] = new JsonArray(); break;
            case "foreignEvidenceRef": rr[1]!["evidenceIds"]![0] = "missing"; break;
            case "duplicateFacts": p["facts"]!["files"]!.AsArray().Add(p["facts"]!["files"]![0]!.DeepClone()); break;
            case "foreignObservationFile": k["files"]![0]!["fileId"] = "missing"; break;
            case "missingFacts": p["facts"]!["files"] = new JsonArray(); break;
            case "observationCount": k["files"]![0]!["observationCount"] = 99; break;
            case "tokenCount": p["facts"]!["files"]![0]!["tokenCount"] = 99; break;
            case "negativeCount": k["files"]![0]!["observationCount"] = -1; break;
            case "spanNegative": obs[0]!["valueSpan"]!["startByte"] = -1; break;
            case "spanOutside": obs[0]!["valueSpan"]!["endByte"] = 999; break;
            case "spanFraction": obs[0]!["valueSpan"]!["startByte"] = 1.5; break;
            case "spanOverflow": obs[0]!["valueSpan"]!["endByte"] = JsonNode.Parse("18446744073709551616"); break;
            case "lengthOverflow": obs[0]!["value"]!["length"]!["numeratorMm"] = JsonNode.Parse("18446744073709551616"); break;
            case "lengthNegative": obs[0]!["value"]!["length"]!["numeratorMm"] = -1; break;
            case "lengthUnit": obs[0]!["value"]!["length"]!["unit"] = "pt"; break;
            case "lengthDenominator": obs[0]!["value"]!["length"]!["denominator"] = 0; break;
            case "discriminator": obs[0]!["value"]!["kind"] = "unknown"; break;
            case "extraVariant": obs[0]!["value"]!["extra"] = true; break;
            case "missingVariantField": obs[0]!["value"]!.AsObject().Remove("length"); break;
            case "reasonEnum": obs[0]!["reason"] = "unknown"; break;
            case "titleKind": obs[1]!["value"]!["titleKind"] = "unknown"; break;
            case "scalarMany": obs[1]!["value"]!["finalScalar"] = "🙂🙂"; break;
            case "scalarNull": obs[1]!["value"]!["finalScalar"] = null; break;
            case "extraPayload": p["extra"] = 1; break;
            case "extraRoot": root["extra"] = 1; break;
            case "missingConfig": p.AsObject().Remove("configuration"); break;
            case "nullConfig": p["configuration"] = null; break;
            case "extraConfig": p["configuration"]!["extra"] = true; break;
            case "configEnum": p["configuration"]!["workType"] = "unknown"; break;
            case "engineAggregate": k["engineAggregateOutcome"] = "fail"; break;
            case "aggregate": p["aggregateOutcome"] = "pass"; break;
            case "eligibility": k["files"]![0]!["eligible"] = false; break;
            case "diagEvidence": p["diagnostics"]![0]!["id"] = "missing"; break;
            case "diagSpan": p["diagnostics"]![0]!["span"]!["endByte"] = 51; break;
            case "fixVersion": fix["ruleVersion"] = "9"; break;
            case "fixId": fix["id"] = "AI-EXECUTE"; break;
            case "fixEvidence": fix["evidenceId"] = "missing"; break;
            case "fixSnapshot": fix["snapshotId"] = "missing"; break;
            case "fixConfiguration": fix["configuration"]!["workType"] = "coursework"; break;
            case "fixPath": fix["relativePath"] = "../escape.tex"; break;
            case "fixHash": fix["expectedFileSha256"] = new string('g',64); break;
            case "fixReplacement": fix["replacement"] = "execute()"; break;
            case "fixCount": k["fixDescriptors"]!.AsArray().Add(fix.DeepClone()); break;
            case "excerptTooLarge": obs[0]!["excerpt"] = new string('x',257); break;
            case "expectedTooLarge": fix["expectedOriginal"] = new string('x',4097); break;
            case "coverageTruncated": p["coverage"]!["resultTruncated"] = true; break;
            default: throw new InvalidOperationException(fault);
        }
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(root.ToJsonString()));
    }

    [Theory]
    [InlineData("project")][InlineData("session")][InlineData("generation")][InlineData("head")]
    [InlineData("path")][InlineData("hash")][InlineData("original")][InlineData("tokenSplit")]
    [InlineData("emojiSplit")][InlineData("bomSplit")]
    [InlineData("excerpt")][InlineData("configuration")][InlineData("byteLength")][InlineData("finalScalar")]
    public async Task Snapshot_correlations_reject_structurally_valid_but_inconsistent_data(string fault)
    {
        var fixture = KnuFixture.Load("negative"); var snapshot = fixture.Snapshot();
        var bytes = fixture.Bytes; var response = fixture.Response;
        var p = response["payload"]!; var k = p["knu"]!; var f = k["fixDescriptors"]![0]!;
        switch (fault)
        {
            case "project": f["projectId"] = "other-project"; break;
            case "session": f["sessionId"] = "other-session"; break;
            case "generation": f["workingGeneration"] = 9007199254740994L; break;
            case "head": f["baseHeadRevisionId"] = null; break;
            case "path": f["relativePath"] = "renamed.tex"; break;
            case "hash": f["expectedFileSha256"] = new string('0',64); break;
            case "original": f["expectedOriginal"] = "11 mm"; break;
            case "tokenSplit": p["facts"]!["files"]![0]!["tokens"]![0]!["startByte"] = 4; break;
            case "emojiSplit": p["facts"]!["files"]![0]!["tokens"]![0]!["startByte"] = 16; break;
            case "bomSplit": p["facts"]!["files"]![0]!["tokens"]![0]!["startByte"] = 1; break;
            case "excerpt": k["files"]![0]!["observations"]![0]!["excerpt"] = "11 mm"; break;
            case "configuration": snapshot = snapshot with { Configuration = new(AnalysisWorkType.MasterQualification) }; break;
            case "byteLength": p["facts"]!["files"]![0]!["byteLength"] = 78; break;
            case "finalScalar": k["files"]![0]!["observations"]![1]!["value"]!["finalScalar"] = "X"; break;
        }
        // Structural validation deliberately does not rescan/evaluate LaTeX or
        // claim a capture-supplied hash was verified by Rust.
        Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(response.ToJsonString()));
        var js = new FixtureJs((_, rid) => { response["requestId"] = rid; return response.ToJsonString(); });
        await using var analyzer = new RustSourceAnalyzer(js);
        var failure = Assert.IsType<AnalysisJobResult.Failed>(await analyzer.AnalyzeAsync(snapshot, CancellationToken.None));
        Assert.Equal("InvalidInput", failure.Error.Code);
        Assert.Equal(bytes, snapshot.Files[0].Bytes.ToArray());
        Assert.Equal(new[] { "analyzeSource" }, js.Calls);
    }

    [Fact]
    public void Duplicate_fields_and_lone_surrogate_scalars_are_not_sanitized_into_success()
    {
        var json = KnuFixture.Load("positive").Json;
        var duplicate = json.Replace("\"workType\":\"unspecified\"", "\"workType\":\"unspecified\",\"workType\":\"coursework\"", StringComparison.Ordinal);
        Assert.NotEqual(json, duplicate);
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(duplicate));
        using var doc = JsonDocument.Parse(json);
        var scalar = doc.RootElement.GetProperty("payload").GetProperty("knu").GetProperty("files")[0].GetProperty("observations")[1].GetProperty("value").GetProperty("finalScalar").GetRawText();
        var malformed = json.Replace("\"finalScalar\":" + scalar, "\"finalScalar\":\"\\uD800\"", StringComparison.Ordinal);
        Assert.NotEqual(json, malformed);
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(malformed));
    }

    [Fact]
    public void Derived_evidence_ids_are_bounded_but_not_limited_to_opaque_id_size()
    {
        var fixture = KnuFixture.Load("positive");
        var root = fixture.Response;
        var old = root["payload"]!["facts"]!["files"]![0]!["fileId"]!.GetValue<string>();
        var json = root.ToJsonString().Replace(old, new string('f',128), StringComparison.Ordinal);
        var r = Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(json)).Result;
        Assert.All(r.Knu!.Files[0].Observations, o => Assert.InRange(Encoding.UTF8.GetByteCount(o.Id),129,AnalysisCatalog.MaxDerivedIdBytes));
        var tooLong = root.ToJsonString().Replace(old, new string('f',129), StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(tooLong));
    }

    [Fact]
    public async Task Combining_scalar_split_is_rejected_against_exact_captured_bytes()
    {
        var fixture = KnuFixture.Load("positive"); var s = fixture.Snapshot();
        Assert.Equal(0xcc, fixture.Bytes[20]); Assert.Equal(0x81, fixture.Bytes[21]);
        fixture.Response["payload"]!["facts"]!["files"]![0]!["tokens"]![0]!["startByte"] = 21;
        Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(fixture.Json));
        var js = new FixtureJs((_,rid) => { fixture.Response["requestId"] = rid; return fixture.Json; });
        await using var analyzer = new RustSourceAnalyzer(js);
        Assert.IsType<AnalysisJobResult.Failed>(await analyzer.AnalyzeAsync(s,CancellationToken.None));
        Assert.Equal(fixture.Bytes,s.Files[0].Bytes.ToArray());
    }

    [Theory]
    [InlineData("files")][InlineData("observations")][InlineData("diagnosticRefs")][InlineData("text")]
    public void Oversized_collections_and_text_fail_bounded_validation(string kind)
    {
        var root = KnuFixture.Load("unavailable").Response;
        var p = root["payload"]!;
        if (kind == "text") p["coverage"]!["unsupported"] = new JsonArray(new string('x',257));
        else if (kind == "files") p["facts"]!["files"] = new JsonArray(Enumerable.Range(0,5001).Select(_ => (JsonNode)new JsonObject()).ToArray());
        else if (kind == "observations") p["knu"]!["files"]![0]!["observations"] = new JsonArray(Enumerable.Range(0,20001).Select(_ => (JsonNode)new JsonObject()).ToArray());
        else p["ruleResults"]![1]!["perFile"]![0]!["diagnosticIds"] = new JsonArray(Enumerable.Range(0,1001).Select(i => (JsonNode)JsonValue.Create("id"+i)!).ToArray());
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(root.ToJsonString()));
    }

    [Theory]
    [InlineData("failed")][InlineData("cancelled")][InlineData("progress")][InlineData("wrongId")]
    [InlineData("cancelBody")][InlineData("duplicate")][InlineData("largeError")]
    public async Task Synthetic_terminal_faults_never_become_Knu_success(string kind)
    {
        var fixture = KnuFixture.Load("positive");
        var js = new FixtureJs((_,rid) => kind switch {
            "cancelled" => new JsonObject { ["protocolVersion"]=1,["requestId"]=rid,["status"]="cancelled" }.ToJsonString(),
            "progress" => new JsonObject { ["protocolVersion"]=1,["requestId"]=rid,["status"]="progress" }.ToJsonString(),
            "cancelBody" => new JsonObject { ["protocolVersion"]=1,["requestId"]=rid,["status"]="cancelled",["payload"]=new JsonObject() }.ToJsonString(),
            "duplicate" => "{\"protocolVersion\":1,\"requestId\":\""+rid+"\",\"status\":\"completed\",\"status\":\"cancelled\"}",
            _ => new JsonObject { ["protocolVersion"]=1,["requestId"]=kind=="wrongId"?"other":rid,["status"]="failed",["error"]=new JsonObject { ["code"]="Timeout",["safeMessage"]=kind=="largeError"?new string('x',4097):"bounded synthetic timeout",["retryable"]=false } }.ToJsonString(),
        });
        await using var analyzer = new RustSourceAnalyzer(js);
        var result = await analyzer.AnalyzeAsync(fixture.Snapshot(),CancellationToken.None);
        if (kind == "cancelled") Assert.IsType<AnalysisJobResult.Cancelled>(result);
        else {
            var failed = Assert.IsType<AnalysisJobResult.Failed>(result);
            Assert.Equal(kind=="failed"?"Timeout":kind=="progress"?"UnsupportedCapability":"InvalidInput",failed.Error.Code);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Knu_cancel_or_dispose_during_held_initialize_cannot_return_completed(bool dispose)
    {
        var f = KnuFixture.Load("positive"); var snapshot = f.Snapshot();
        var js = new FixtureJs((_,rid)=>f.Reply(snapshot,rid)) { InitializeGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var analyzer = new RustSourceAnalyzer(js);
        using var ct = new CancellationTokenSource();
        var task = analyzer.AnalyzeAsync(snapshot,ct.Token);
        await js.InitializeReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (dispose) await analyzer.DisposeAsync(); else ct.Cancel();
        js.InitializeGate.TrySetResult(true);
        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        if (dispose) Assert.IsType<AnalysisJobResult.Disposed>(result);
        else Assert.IsType<AnalysisJobResult.Cancelled>(result);
        Assert.Equal(f.Bytes,snapshot.Files[0].Bytes.ToArray());
    }

    [Fact]
    public async Task Lowered_snapshot_result_and_shared_emission_caps_cannot_be_ignored()
    {
        var f = KnuFixture.Load("positive"); var original = f.Snapshot();
        foreach (var limit in new[] { original.Limits with { MaxResultBytes=1000 }, original.Limits with { MaxTokensPerProject=1 }, original.Limits with { MaxTokensPerFile=1 } })
        {
            var snapshot = original with { Limits=limit };
            var js = new FixtureJs((_,rid)=>f.Reply(snapshot,rid));
            await using var analyzer = new RustSourceAnalyzer(js);
            var failed = Assert.IsType<AnalysisJobResult.Failed>(await analyzer.AnalyzeAsync(snapshot,CancellationToken.None));
            Assert.Equal("InvalidInput",failed.Error.Code);
        }
    }

    internal sealed class FixtureJs(Func<string, string, string> response) : IJSRuntime, IJSObjectReference
    {
        public List<string> Calls { get; } = new();
        public byte[]? Packed;
        public List<string> Metadata { get; } = new();
        public long CurrentEpoch { get; set; } = 7;
        public TaskCompletionSource<bool>? InitializeGate { get; set; }
        public TaskCompletionSource<bool>? EpochGate { get; set; }
        public TaskCompletionSource<bool> InitializeReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> EpochReached { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool>? ImportGate { get; set; }
        public TaskCompletionSource<bool>? ResponseGate { get; set; }
        public TaskCompletionSource<bool> ImportReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ResponseReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => InvokeAsync<T>(id, CancellationToken.None, args);
        public async ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args)
        {
            if (id == "import")
            {
                ImportReached.TrySetResult(true);
                if (ImportGate is { } g) await g.Task; // deliberately noncooperative
                return (T)(object)this;
            }
            if (id == "analyzeSource")
            {
                Calls.Add(id); Packed = ((byte[])args![1]!).ToArray();
                var meta = (string)args[0]!;
                Metadata.Add(meta);
                var rid = JsonNode.Parse(meta)!["requestId"]!.GetValue<string>();
                InitializeReached.TrySetResult(true);
                // Models the bridge's held ready/initialize await, before it
                // returns a native result; not an added product initialize API.
                if (InitializeGate is { } init) await init.Task;
                var result = response(meta, rid);
                ResponseReached.TrySetResult(true);
                if (ResponseGate is { } g) await g.Task;
                return (T)(object)new AnalysisBridgeResponse(result, 7);
            }
            if (id == "getWorkerEpoch")
            {
                EpochReached.TrySetResult(true);
                if (EpochGate is { } g) await g.Task; // deliberate noncooperation
                return (T)(object)CurrentEpoch;
            }
            if (id is "cancelSource" or "dispose") return default!;
            throw new InvalidOperationException(id);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
