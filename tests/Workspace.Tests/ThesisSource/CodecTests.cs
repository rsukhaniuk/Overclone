using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Analysis;
using Xunit;

namespace Workspace.Tests.ThesisSource;

// Actual accepted producer bytes/outcomes, not a C# thesis evaluator. Managed
// execution is requested from root; this worker has no SDK.
public sealed class CodecTests
{
    public static IEnumerable<object[]> Cases => ThesisFixture.Ids.Select(id=>new object[]{id});
    [Theory,MemberData(nameof(Cases))]
    public async Task Every_actual_WASM_vector_roundtrips_and_matches_privately_owned_capture(string id)
    {
        var f=ThesisFixture.Load(id); var raw=f.Response;
        var original=Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(raw.ToJsonString()));
        Assert.Equal(5,original.Result.Thesis!.RuleResults.Length);Assert.Equal(7,original.Result.RuleResults.Count);
        Assert.Empty(original.Result.Knu!.FixDescriptors);
        var encoded=JsonSerializer.SerializeToElement(new AnalysisResponseEnvelope(1,original.RequestId,ResponseStatus.Completed,original.Result,null),AnalysisJson.Options);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(raw),encoded));
        var snapshot=f.Snapshot();
        var bound=Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(f.Reply(snapshot,"actual-wasm-fixture")));
        ThesisValidation.ValidateCapturedResult(bound.Result,snapshot);
        var (metadata,bytes)=AnalysisRequestPacker.Pack(snapshot,"actual-wasm-fixture");Assert.Equal(f.Packed,bytes);
        var js=new KnuProtocolTests.FixtureJs((_,rid)=>f.Reply(snapshot,rid));
        await using var analyzer=new RustSourceAnalyzer(js);
        var result=Assert.IsType<AnalysisJobResult.Completed>(await analyzer.AnalyzeAsync(snapshot,default));
        Assert.Equal(snapshot.ReferenceConfiguration,result.Result.ReferenceConfiguration);
        Assert.Equal(snapshot.Configuration,result.Result.Configuration);Assert.Equal(bytes,js.Packed);
        Assert.Single(js.Calls);Assert.Contains("\"version\":\"1.5.0\"",metadata);
    }

    [Theory]
    [InlineData("root")][InlineData("rootMissing")][InlineData("workType")][InlineData("workNumeric")]
    [InlineData("catalogHash")][InlineData("catalogVersion")][InlineData("metadataVersion")][InlineData("schema")]
    [InlineData("extraRoot")][InlineData("extraPayload")][InlineData("extraThesis")][InlineData("missingThesis")]
    [InlineData("nullEvents")][InlineData("unknownEvent")][InlineData("foreignEventFile")][InlineData("eventTooLong")]
    [InlineData("spanNegative")][InlineData("spanFraction")][InlineData("spanOverflow")][InlineData("spanOutside")]
    [InlineData("duplicateClosure")][InlineData("missingClosureRoot")][InlineData("foreignClosure")]
    [InlineData("extraRule")][InlineData("ruleOrder")][InlineData("ruleId")][InlineData("ruleVersion")]
    [InlineData("domain")][InlineData("support")][InlineData("method")][InlineData("citation")][InlineData("severity")]
    [InlineData("fixPolicy")][InlineData("outcomeNumeric")][InlineData("unavailableGreen")][InlineData("fakeMeasured")][InlineData("fakePassWithReason")]
    [InlineData("evidenceDuplicate")][InlineData("evidenceOutside")][InlineData("evidenceOrder")]
    [InlineData("limitation")][InlineData("missingUncertainty")][InlineData("unknownReason")]
    [InlineData("missingUnavailable")][InlineData("capability")][InlineData("titleType")][InlineData("statusMismatch")]
    public void Closed_protocol_mutations_fail_without_fallback(string fault)
    {
        var root=ThesisFixture.Load("manual-positive").Response;var p=root["payload"]!;var t=p["thesis"]!;var r=t["ruleResults"]![0]!;var e=t["events"]![0]!;
        switch(fault)
        {
            case "root":t["rootFileId"]="unknown";break;
            case "rootMissing":p.AsObject().Remove("referenceConfiguration");break;
            case "workType":t["workType"]="bachelor";break;
            case "workNumeric":p["configuration"]!["workType"]=0;break;
            case "catalogHash":t["guidelineSha256"]=new string('0',64);break;
            case "catalogVersion":t["catalogVersion"]="2";break;
            case "metadataVersion":p["knu"]!["metadata"]!["profile"]!["version"]="9";break;
            case "schema":t["schemaVersion"]=1.0m+0.5m;break;
            case "extraRoot":root["extra"]=true;break;
            case "extraPayload":p["extra"]=true;break;
            case "extraThesis":t["extra"]=true;break;
            case "missingThesis":p.AsObject().Remove("thesis");break;
            case "nullEvents":t["events"]=null;break;
            case "unknownEvent":e["kind"]="measuredMargin";break;
            case "foreignEventFile":e["fileId"]="outside";break;
            case "eventTooLong":e["text"]=new string('ї',257);break;
            case "spanNegative":e["span"]!["startByte"]=-1;break;
            case "spanFraction":e["span"]!["startByte"]=1.5;break;
            case "spanOverflow":e["span"]!["endByte"]=JsonNode.Parse("18446744073709551616");break;
            case "spanOutside":e["span"]!["endByte"]=8388609;break;
            case "duplicateClosure":t["closureFileIds"]!.AsArray().Add(t["rootFileId"]!.DeepClone());break;
            case "missingClosureRoot":t["closureFileIds"]=new JsonArray();break;
            case "foreignClosure":t["closureFileIds"]!.AsArray().Add("uncaptured");break;
            case "extraRule":t["ruleResults"]!.AsArray().Add(r.DeepClone());break;
            case "ruleOrder":t["ruleResults"]![0]=t["ruleResults"]![1]!.DeepClone();break;
            case "ruleId":r["ruleId"]="other";break;
            case "ruleVersion":r["ruleVersion"]="2";break;
            case "domain":r["domain"]="renderedObservation";break;
            case "support":r["supportState"]="supported";break;
            case "method":r["method"]="native-page-box";break;
            case "citation":r["citation"]!["pdfPages"]![0]=18;break;
            case "severity":r["severity"]="error";break;
            case "fixPolicy":r["fixPolicy"]="automatic";break;
            case "outcomeNumeric":r["outcome"]=0;break;
            case "unavailableGreen":r["outcome"]="pass";break;
            case "fakePassWithReason":r["outcome"]="pass";r["reason"]="observed-literals-only";break;
            case "fakeMeasured":t["renderedCompliance"]="compliant";break;
            case "evidenceDuplicate":r["evidenceIndexes"]!.AsArray().Add(0);break;
            case "evidenceOutside":r["evidenceIndexes"]![0]=999;break;
            case "evidenceOrder":r["evidenceIndexes"]![0]=1;break;
            case "limitation":t["limitations"]!.AsArray().Add("unknown");break;
            case "missingUncertainty":t["completeLiteralScan"]=false;break;
            case "unknownReason":r["reason"]="looksGood";break;
            case "missingUnavailable":t["unavailableCells"]=new JsonArray();break;
            case "capability":p["unavailableCapabilities"]!.AsArray().Add("unknown");break;
            case "titleType":t["workTypeDependentTitle"]="bachelor-default";break;
            case "statusMismatch":root["status"]="failed";break;
        }
        Assert.Throws<JsonException>(()=>AnalysisJson.ParseResponse(root.ToJsonString()));
    }
    [Fact]
    public void Duplicate_missing_null_unknown_version_and_old_edition_injection_are_rejected()
    {
        var r=ThesisFixture.Load("manual-positive").Response;
        Assert.Throws<JsonException>(()=>AnalysisJson.ParseResponse(r.ToJsonString().Replace("\"schemaVersion\":1","\"schemaVersion\":1,\"schemaVersion\":1")));
        foreach(var version in new[]{"1.0.0","1.1.0","1.2.0","1.3.0","1.4.0","1.6.0"})
        { var clone=r.DeepClone();clone["payload"]!["profile"]!["version"]=version;Assert.Throws<JsonException>(()=>AnalysisJson.ParseResponse(clone.ToJsonString())); }
        var invalid=r.ToJsonString().Replace("\"rootFileId\":\"f0\"","\"rootFileId\":\"\\ud800\"");
        Assert.Throws<JsonException>(()=>AnalysisJson.ParseResponse(invalid));
    }
    [Theory]
    [InlineData("root")][InlineData("workType")][InlineData("noConfig")][InlineData("zeroBudget")][InlineData("sourceBytes")][InlineData("hash")]
    public void Capture_and_request_binding_rejects_mismatched_authority(string fault)
    {
        var f=ThesisFixture.Load("unicode-bom-crlf");var s=f.Snapshot();var response=Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(f.Reply(s,"r"))).Result;
        switch(fault) {
            case "root":s=s with{ReferenceConfiguration=new("uncaptured")};break;
            case "workType":s=s with{Configuration=new(AnalysisWorkType.Coursework)};break;
            case "noConfig":s=s with{Configuration=null};break;
            case "zeroBudget":s=s with{Limits=s.Limits with{MaxDiagnosticsPerProject=0}};break;
            case "sourceBytes":s=s with{Files=s.Files.SetItem(0,s.Files[0] with{Bytes=ImmutableArray.Create((byte)255)})};break;
            case "hash":s=s with{Files=s.Files.SetItem(0,s.Files[0] with{Sha256=new(new string('a',64))})};break;
        }
        Assert.Throws<JsonException>(()=>ThesisValidation.ValidateCapturedResult(response,s));
    }
    [Fact]
    public void Wrong_event_preimage_scalar_boundary_requested_budget_and_transport_identity_are_rejected()
    {
        var f=ThesisFixture.Load("unicode-bom-crlf");var s=f.Snapshot();var raw=JsonNode.Parse(f.Reply(s,"r"))!;
        var eventNode=raw["payload"]!["thesis"]!["events"]![0]!;
        eventNode["span"]!["startByte"]=3;eventNode["span"]!["endByte"]=3;
        var bad=Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(raw.ToJsonString()));
        Assert.Throws<JsonException>(()=>ThesisValidation.ValidateCapturedResult(bad.Result,s));
        var good=Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(f.Reply(s,"r")));
        var thesis=good.Result.Thesis!;var e=thesis.Events[0];
        var split=good.Result with { Thesis=thesis with { Events=thesis.Events.SetItem(0,e with { Span=e.Span with { StartByte=e.Span.StartByte+1 } }) } };
        Assert.Throws<JsonException>(()=>ThesisValidation.ValidateCapturedResult(split,s));
        Assert.Throws<JsonException>(()=>ThesisValidation.ValidateCapturedResult(good.Result,s with{Limits=s.Limits with{MaxDiagnosticsPerProject=1}}));
    }
    [Theory]
    [InlineData("request")][InlineData("snapshot")][InlineData("unknownFailure")][InlineData("extraFailure")][InlineData("duplicateFailure")]
    public void Expected_request_and_closed_failed_envelopes_are_not_optional(string fault)
    {
        var f=ThesisFixture.Load("manual-positive");var s=f.Snapshot();string json=f.Reply(s,"expected");
        if(fault=="request") json=f.Reply(s,"wrong");
        if(fault=="snapshot") {var n=JsonNode.Parse(json)!;n["payload"]!["snapshotId"]="other";json=n.ToJsonString();}
        if(fault.EndsWith("Failure",StringComparison.Ordinal)) {
            json="{\"protocolVersion\":1,\"requestId\":\"expected\",\"status\":\"failed\",\"error\":{\"code\":\"InvalidInput\",\"safeMessage\":\"Bounded failure\",\"retryable\":false}}";
            if(fault=="unknownFailure")json=json.Replace("InvalidInput","UnknownError");
            if(fault=="extraFailure")json=json.Replace("\"retryable\":false","\"retryable\":false,\"extra\":true");
            if(fault=="duplicateFailure")json=json.Replace("\"retryable\":false","\"retryable\":false,\"retryable\":false");
        }
        Assert.Throws<JsonException>(()=>ThesisValidation.ParseResponse(json,s,"expected"));
    }
    [Theory]
    [InlineData("positive")][InlineData("negative")][InlineData("uncertain")][InlineData("unavailable")][InlineData("engine")]
    public void Existing_golden_shapes_and_fixes_stay_unchanged(string id)
    {
        var f=KnuFixture.Load(id);var parsed=Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(f.Json));Assert.Null(parsed.Result.Thesis);
        var encoded=JsonSerializer.SerializeToElement(new AnalysisResponseEnvelope(1,parsed.RequestId,ResponseStatus.Completed,parsed.Result,null),AnalysisJson.Options);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(f.Response),encoded));
    }
}
