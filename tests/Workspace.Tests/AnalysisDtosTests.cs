using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using Client.Analysis;
using Client.Workspace.Domain;
using Xunit;

namespace Workspace.Tests;

/// <summary>
/// Wire DTO serialization/deserialization + the protocol-boundary response parser
/// and the defensive request packer (M2a-2a). Covers camelCase fields, lowerCamelCase
/// enum STRINGS (a strict string-enum converter that REJECTS numeric enum tokens),
/// wire identity IDs as strings, all five RuleOutcome spellings, completed/failed
/// Rust-shaped JSON, nested per-file token/diagnostic links, malformed-protocol
/// rejection (fractional/huge protocolVersion, non-object nested elements,
/// negative byteLength, tokenCount mismatch, diagnostic span vs file byteLength,
/// completed+error / failed+payload opposite-body rejection, perFile diagnostic
/// links belonging to the referenced file AND rule, duplicate perFile, absent/null
/// opposite body allowed), and packer preflight (forged ByteLength, too many files,
/// negative/oversize lengths, invalid IDs/profile, exact two-file packing, failed
/// packing leaves arrays untouched). No JS runtime is used.
/// </summary>
public class AnalysisDtosTests
{
    private static readonly JsonSerializerOptions Options = AnalysisJson.Options;

    [Fact]
    public void Domain_Id_Struct_Serializes_As_Value_Object_Not_A_String()
    {
        var fid = FileId.New();
        var json = JsonSerializer.Serialize(fid, Options);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("value", out _));
        Assert.DoesNotContain("\"fileId\"", json);
    }

    [Fact]
    public void Request_Payload_Serializes_CamelCase_With_String_Ids()
    {
        var snap = MakeMiniSnapshot();
        var (metadataJson, packed) = AnalysisRequestPacker.Pack(snap, requestId: "req-1");
        using var doc = JsonDocument.Parse(metadataJson);

        Assert.Equal(1, doc.RootElement.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("req-1", doc.RootElement.GetProperty("requestId").GetString());
        Assert.Equal("analyzeSource", doc.RootElement.GetProperty("operation").GetString());
        var payload = doc.RootElement.GetProperty("payload");
        Assert.True(payload.TryGetProperty("snapshotId", out _));
        Assert.True(payload.TryGetProperty("projectId", out _));
        Assert.True(payload.TryGetProperty("sessionId", out _));
        Assert.True(payload.TryGetProperty("workingGeneration", out _));
        Assert.True(payload.TryGetProperty("baseHeadRevisionId", out var bh));
        Assert.Equal("00000000000000000000000000000004", bh.GetString());
        Assert.True(payload.TryGetProperty("profile", out _));
        Assert.True(payload.TryGetProperty("limits", out _));
        Assert.True(payload.TryGetProperty("files", out _));

        Assert.Equal(snap.WorkingGeneration.ToString(), payload.GetProperty("workingGeneration").GetRawText());

        var fileObj = payload.GetProperty("files")[0];
        Assert.Equal("00000000000000000000000000000001", fileObj.GetProperty("fileId").GetString());
        Assert.Equal("main.tex", fileObj.GetProperty("relativePath").GetString());
        Assert.Equal("text", fileObj.GetProperty("mediaKind").GetString());
        Assert.Equal(packed.LongLength, fileObj.GetProperty("byteLength").GetInt64());
        Assert.Equal(0, fileObj.GetProperty("byteOffset").GetInt64());

        Assert.Single(payload.GetProperty("files").EnumerateArray());
    }

    [Fact]
    public void All_Five_Outcomes_Serialize_As_LowerCamelCase_Strings()
    {
        foreach (var (outcome, spelling) in new[] {
            (RuleOutcome.Pass, "pass"),
            (RuleOutcome.Fail, "fail"),
            (RuleOutcome.Indeterminate, "indeterminate"),
            (RuleOutcome.NotApplicable, "notApplicable"),
            (RuleOutcome.Unavailable, "unavailable"),
        })
        {
            var dto = new AnalysisRuleResultDto("ENGINE-STRUCT-001", "engine", outcome, new List<AnalysisPerFileOutcomeDto>(), new List<string>());
            var json = JsonSerializer.Serialize(dto, Options);
            Assert.Contains($"\"outcome\":\"{spelling}\"", json);
            var back = JsonSerializer.Deserialize<AnalysisRuleResultDto>(json, Options)!;
            Assert.Equal(outcome, back.Outcome);
        }
    }

    [Fact]
    public void Token_And_Newline_Kinds_Serialize_As_LowerCamelCase_Strings()
    {
        var tok = new AnalysisTokenDto(TokenKind.IncludePath, 7, 14, "chapter", null);
        var json = JsonSerializer.Serialize(tok, Options);
        Assert.Contains("\"kind\":\"includePath\"", json);

        var f = new AnalysisSourceFileFactsDto("f1", 14, true, NewlineKind.Crlf, false, false, 1, RuleOutcome.Pass, new[] { tok });
        var fjson = JsonSerializer.Serialize(f, Options);
        Assert.Contains("\"newlineKind\":\"crlf\"", fjson);
        Assert.Contains("\"engineOutcome\":\"pass\"", fjson);
    }

    [Fact]
    public void Completed_Rust_Shaped_Json_Round_Trips()
    {
        string rustJson = """
        {"protocolVersion":1,"requestId":"req-1","status":"completed","payload":{"snapshotId":"snap-1","profile":{"id":"engine-structural","version":"0"},"facts":{"files":[{"fileId":"f1","byteLength":14,"hasBom":true,"newlineKind":"crlf","truncated":false,"braceDepthExceeded":false,"tokenCount":2,"engineOutcome":"fail","tokens":[{"kind":"command","startByte":0,"endByte":6},{"kind":"braceOpen","startByte":6,"endByte":7}]}]},"diagnostics":[{"id":"ENGINE-STRUCT-001:f1:6","ruleId":"ENGINE-STRUCT-001","origin":"engine","severity":"warning","fileId":"f1","span":{"startByte":6,"endByte":7},"message":"Unmatched opening brace (group never closed).","primaryLocation":{"line":1,"column":6}}],"ruleResults":[{"ruleId":"ENGINE-STRUCT-001","origin":"engine","outcome":"fail","perFile":[{"fileId":"f1","outcome":"fail","diagnosticIds":["ENGINE-STRUCT-001:f1:6"]}],"limitations":[]}],"aggregateOutcome":"fail","coverage":{"factKinds":["command","braceOpen"],"unsupported":["macro-expansion","pdf-facts"],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":["pdfFacts","pdfChecks"]}}
        """;
        var resp = AnalysisJson.ParseResponse(rustJson);
        var completed = Assert.IsType<AnalysisResponse.Completed>(resp);
        Assert.Equal("req-1", completed.RequestId);
        Assert.Equal("snap-1", completed.Result.SnapshotId);
        Assert.Equal("engine-structural", completed.Result.Profile.Id);
        Assert.Equal(RuleOutcome.Fail, completed.Result.AggregateOutcome);
        Assert.Single(completed.Result.RuleResults);
        Assert.Equal("ENGINE-STRUCT-001", completed.Result.RuleResults[0].RuleId);

        var f = completed.Result.Facts.Files[0];
        Assert.Equal("f1", f.FileId);
        Assert.Equal(14, f.ByteLength);
        Assert.True(f.HasBom);
        Assert.Equal(NewlineKind.Crlf, f.NewlineKind);
        Assert.Equal(RuleOutcome.Fail, f.EngineOutcome);
        Assert.Equal(2, f.Tokens.Count);
        Assert.Equal(TokenKind.Command, f.Tokens[0].Kind);
        Assert.Equal(TokenKind.BraceOpen, f.Tokens[1].Kind);
        Assert.Equal((6u, 7u), (f.Tokens[1].StartByte, f.Tokens[1].EndByte));

        var d = completed.Result.Diagnostics[0];
        Assert.Equal("ENGINE-STRUCT-001", d.RuleId);
        Assert.NotNull(d.Span);
        Assert.Equal(6, d.Span!.StartByte);
        Assert.Equal(7, d.Span.EndByte);
        Assert.Equal((1, 6), (d.PrimaryLocation!.Line, d.PrimaryLocation.Column));

        var pf = completed.Result.RuleResults[0].PerFile[0];
        Assert.Equal("f1", pf.FileId);
        Assert.Equal(RuleOutcome.Fail, pf.Outcome);
        Assert.Single(pf.DiagnosticIds);
        Assert.Equal(d.Id, pf.DiagnosticIds[0]);

        Assert.Contains("pdfFacts", completed.Result.UnavailableCapabilities);
        Assert.Contains("pdfChecks", completed.Result.UnavailableCapabilities);
        Assert.Contains("pdf-facts", completed.Result.Coverage.Unsupported);
    }

    [Fact]
    public void Failed_Rust_Shaped_Json_Round_Trips()
    {
        string rustJson = """
        {"protocolVersion":1,"requestId":"req-2","status":"failed","error":{"code":"QuotaExceeded","safeMessage":"result exceeded the 4 MiB limit","retryable":false}}
        """;
        var resp = AnalysisJson.ParseResponse(rustJson);
        var failed = Assert.IsType<AnalysisResponse.Failed>(resp);
        Assert.Equal("req-2", failed.RequestId);
        Assert.Equal("QuotaExceeded", failed.Error.Code);
        Assert.False(failed.Error.Retryable);
        Assert.Equal("result exceeded the 4 MiB limit", failed.Error.SafeMessage);
    }

    [Fact]
    public void Completed_Envelope_Without_Payload_Is_Rejected_Not_Empty_Success()
    {
        string badJson = """{"protocolVersion":1,"requestId":"req-3","status":"completed"}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(badJson));
    }

    [Fact]
    public void Failed_Envelope_Without_Error_Is_Rejected()
    {
        string badJson = """{"protocolVersion":1,"requestId":"req-4","status":"failed"}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(badJson));
    }

    // ---- malformed protocol values (gap 5) ----

    [Fact]
    public void Unsupported_ProtocolVersion_Is_Rejected()
    {
        string bad = """{"protocolVersion":99,"requestId":"r","status":"completed","payload":{}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void Fractional_ProtocolVersion_Is_Rejected()
    {
        string bad = """{"protocolVersion":1.5,"requestId":"r","status":"completed","payload":{}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void Huge_ProtocolVersion_Is_Rejected()
    {
        string bad = """{"protocolVersion":99999999999999999999,"requestId":"r","status":"completed","payload":{}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void Completed_Payload_Empty_Object_Is_Rejected_Not_EmptySuccess()
    {
        string bad = """{"protocolVersion":1,"requestId":"r","status":"completed","payload":{}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void Completed_Missing_AggregateOutcome_Is_Rejected()
    {
        string bad = """{"protocolVersion":1,"requestId":"r","status":"completed","payload":{"snapshotId":"s","profile":{"id":"engine-structural","version":"0"},"facts":{"files":[]},"diagnostics":[],"ruleResults":[],"coverage":{"factKinds":[],"unsupported":[],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":[]}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void Completed_Missing_Files_List_Is_Rejected()
    {
        string bad = """{"protocolVersion":1,"requestId":"r","status":"completed","payload":{"snapshotId":"s","profile":{"id":"engine-structural","version":"0"},"facts":{},"diagnostics":[],"ruleResults":[],"aggregateOutcome":"pass","coverage":{"factKinds":[],"unsupported":[],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":[]}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void Completed_Missing_SnapshotId_Is_Rejected()
    {
        string bad = """{"protocolVersion":1,"requestId":"r","status":"completed","payload":{"profile":{"id":"engine-structural","version":"0"},"facts":{"files":[]},"diagnostics":[],"ruleResults":[],"aggregateOutcome":"pass","coverage":{"factKinds":[],"unsupported":[],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":[]}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void Completed_Missing_RequestId_Is_Rejected()
    {
        string bad = """{"protocolVersion":1,"status":"completed","payload":{"snapshotId":"s","profile":{"id":"engine-structural","version":"0"},"facts":{"files":[]},"diagnostics":[],"ruleResults":[],"aggregateOutcome":"pass","coverage":{"factKinds":[],"unsupported":[],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":[]}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void Numeric_Status_Enum_Is_Rejected()
    {
        string bad = """{"protocolVersion":1,"requestId":"r","status":1,"payload":{}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void Numeric_AggregateOutcome_Enum_Is_Rejected()
    {
        string bad = """{"protocolVersion":1,"requestId":"r","status":"completed","payload":{"snapshotId":"s","profile":{"id":"engine-structural","version":"0"},"facts":{"files":[]},"diagnostics":[],"ruleResults":[],"aggregateOutcome":0,"coverage":{"factKinds":[],"unsupported":[],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":[]}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void Unknown_Status_Is_Rejected()
    {
        string bad = """{"protocolVersion":1,"requestId":"r","status":"bogus"}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void Progress_And_Cancelled_Are_Not_Completed()
    {
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse("""{"protocolVersion":1,"requestId":"r","status":"progress"}"""));
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse("""{"protocolVersion":1,"requestId":"r","status":"cancelled"}"""));
    }

    [Fact]
    public void Failed_Without_RequestId_Is_Legitimate_Failed()
    {
        string noId = """{"protocolVersion":1,"status":"failed","error":{"code":"InvalidInput","safeMessage":"bad","retryable":false}}""";
        var resp = AnalysisJson.ParseResponse(noId);
        var failed = Assert.IsType<AnalysisResponse.Failed>(resp);
        Assert.Null(failed.RequestId);
        Assert.Equal("InvalidInput", failed.Error.Code);
    }

    [Fact]
    public void Diagnostic_Referencing_Unknown_File_Is_Rejected()
    {
        string bad = """{"protocolVersion":1,"requestId":"r","status":"completed","payload":{"snapshotId":"s","profile":{"id":"engine-structural","version":"0"},"facts":{"files":[{"fileId":"f1","byteLength":2,"hasBom":false,"newlineKind":"lf","truncated":false,"braceDepthExceeded":false,"tokenCount":0,"engineOutcome":"pass","tokens":[]}]},"diagnostics":[{"id":"d1","ruleId":"ENGINE-STRUCT-001","origin":"engine","severity":"warning","fileId":"nope","message":"x"}],"ruleResults":[],"aggregateOutcome":"pass","coverage":{"factKinds":[],"unsupported":[],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":[]}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void RuleResult_Referencing_Unknown_Diagnostic_Is_Rejected()
    {
        string bad = """{"protocolVersion":1,"requestId":"r","status":"completed","payload":{"snapshotId":"s","profile":{"id":"engine-structural","version":"0"},"facts":{"files":[]},"diagnostics":[],"ruleResults":[{"ruleId":"ENGINE-STRUCT-001","origin":"engine","outcome":"pass","perFile":[{"fileId":"f1","outcome":"pass","diagnosticIds":["nope"]}],"limitations":[]}],"aggregateOutcome":"pass","coverage":{"factKinds":[],"unsupported":[],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":[]}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void Oversize_Response_Is_Rejected()
    {
        var big = new string('x', AnalysisJson.MaxResponseBytes + 1);
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(big));
    }

    // ---- nested non-object kinds (gap 6) ----

    [Fact]
    public void Facts_Files_NonObject_Element_Is_Rejected()
    {
        // files:[3] — an array element that is a number, not an object.
        string bad = """{"protocolVersion":1,"requestId":"r","status":"completed","payload":{"snapshotId":"s","profile":{"id":"engine-structural","version":"0"},"facts":{"files":[3]},"diagnostics":[],"ruleResults":[],"aggregateOutcome":"pass","coverage":{"factKinds":[],"unsupported":[],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":[]}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void Diagnostic_Span_NonObject_Is_Rejected()
    {
        string bad = """{"protocolVersion":1,"requestId":"r","status":"completed","payload":{"snapshotId":"s","profile":{"id":"engine-structural","version":"0"},"facts":{"files":[]},"diagnostics":[{"id":"d1","ruleId":"ENGINE-STRUCT-001","origin":"engine","severity":"warning","fileId":"f1","message":"x","span":3}],"ruleResults":[],"aggregateOutcome":"pass","coverage":{"factKinds":[],"unsupported":[],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":[]}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    // ---- completed/failed invariant violations (gap 7) ----

    [Fact]
    public void Negative_ByteLength_Is_Rejected()
    {
        string bad = """{"protocolVersion":1,"requestId":"r","status":"completed","payload":{"snapshotId":"s","profile":{"id":"engine-structural","version":"0"},"facts":{"files":[{"fileId":"f1","byteLength":-1,"hasBom":false,"newlineKind":"lf","truncated":false,"braceDepthExceeded":false,"tokenCount":0,"engineOutcome":"pass","tokens":[]}]},"diagnostics":[],"ruleResults":[],"aggregateOutcome":"pass","coverage":{"factKinds":[],"unsupported":[],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":[]}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void TokenCount_Mismatch_With_Tokens_Is_Rejected()
    {
        // tokenCount 5 but the tokens list has 0 entries.
        string bad = """{"protocolVersion":1,"requestId":"r","status":"completed","payload":{"snapshotId":"s","profile":{"id":"engine-structural","version":"0"},"facts":{"files":[{"fileId":"f1","byteLength":2,"hasBom":false,"newlineKind":"lf","truncated":false,"braceDepthExceeded":false,"tokenCount":5,"engineOutcome":"pass","tokens":[]}]},"diagnostics":[],"ruleResults":[],"aggregateOutcome":"pass","coverage":{"factKinds":[],"unsupported":[],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":[]}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void Diagnostic_Span_Exceeds_FileByteLength_Is_Rejected()
    {
        // diagnostic endByte 999 with the referenced file byteLength 14.
        string bad = """{"protocolVersion":1,"requestId":"r","status":"completed","payload":{"snapshotId":"s","profile":{"id":"engine-structural","version":"0"},"facts":{"files":[{"fileId":"f1","byteLength":14,"hasBom":false,"newlineKind":"lf","truncated":false,"braceDepthExceeded":false,"tokenCount":0,"engineOutcome":"pass","tokens":[]}]},"diagnostics":[{"id":"d1","ruleId":"ENGINE-STRUCT-001","origin":"engine","severity":"warning","fileId":"f1","message":"x","span":{"startByte":6,"endByte":999}}],"ruleResults":[],"aggregateOutcome":"pass","coverage":{"factKinds":[],"unsupported":[],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":[]}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void Completed_With_NonNull_Error_Is_Rejected()
    {
        string bad = """{"protocolVersion":1,"requestId":"r","status":"completed","error":{"code":"X","safeMessage":"y","retryable":false},"payload":{"snapshotId":"s","profile":{"id":"engine-structural","version":"0"},"facts":{"files":[]},"diagnostics":[],"ruleResults":[],"aggregateOutcome":"pass","coverage":{"factKinds":[],"unsupported":[],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":[]}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void Failed_With_NonNull_Payload_Is_Rejected()
    {
        string bad = """{"protocolVersion":1,"requestId":"r","status":"failed","error":{"code":"X","safeMessage":"y","retryable":false},"payload":{"snapshotId":"s"}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void PerFile_DiagnosticId_Referencing_Different_File_Is_Rejected()
    {
        // The diagnostic belongs to f1, but the perFile entry is for f2.
        string bad = """{"protocolVersion":1,"requestId":"r","status":"completed","payload":{"snapshotId":"s","profile":{"id":"engine-structural","version":"0"},"facts":{"files":[{"fileId":"f1","byteLength":2,"hasBom":false,"newlineKind":"lf","truncated":false,"braceDepthExceeded":false,"tokenCount":0,"engineOutcome":"pass","tokens":[]},{"fileId":"f2","byteLength":2,"hasBom":false,"newlineKind":"lf","truncated":false,"braceDepthExceeded":false,"tokenCount":0,"engineOutcome":"pass","tokens":[]}]},"diagnostics":[{"id":"d1","ruleId":"ENGINE-STRUCT-001","origin":"engine","severity":"warning","fileId":"f1","message":"x"}],"ruleResults":[{"ruleId":"ENGINE-STRUCT-001","origin":"engine","outcome":"fail","perFile":[{"fileId":"f2","outcome":"fail","diagnosticIds":["d1"]}],"limitations":[]}],"aggregateOutcome":"fail","coverage":{"factKinds":[],"unsupported":[],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":[]}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void PerFile_DiagnosticId_Referencing_Different_Rule_Is_Rejected()
    {
        // The diagnostic is for rule A, but the perFile entry is under rule B.
        string bad = """{"protocolVersion":1,"requestId":"r","status":"completed","payload":{"snapshotId":"s","profile":{"id":"engine-structural","version":"0"},"facts":{"files":[{"fileId":"f1","byteLength":2,"hasBom":false,"newlineKind":"lf","truncated":false,"braceDepthExceeded":false,"tokenCount":0,"engineOutcome":"pass","tokens":[]}]},"diagnostics":[{"id":"d1","ruleId":"RULE-A","origin":"engine","severity":"warning","fileId":"f1","message":"x"}],"ruleResults":[{"ruleId":"RULE-B","origin":"engine","outcome":"fail","perFile":[{"fileId":"f1","outcome":"fail","diagnosticIds":["d1"]}],"limitations":[]}],"aggregateOutcome":"fail","coverage":{"factKinds":[],"unsupported":[],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":[]}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void Duplicate_PerFile_Entry_Is_Rejected()
    {
        string bad = """{"protocolVersion":1,"requestId":"r","status":"completed","payload":{"snapshotId":"s","profile":{"id":"engine-structural","version":"0"},"facts":{"files":[{"fileId":"f1","byteLength":2,"hasBom":false,"newlineKind":"lf","truncated":false,"braceDepthExceeded":false,"tokenCount":0,"engineOutcome":"pass","tokens":[]}]},"diagnostics":[],"ruleResults":[{"ruleId":"ENGINE-STRUCT-001","origin":"engine","outcome":"pass","perFile":[{"fileId":"f1","outcome":"pass","diagnosticIds":[]},{"fileId":"f1","outcome":"pass","diagnosticIds":[]}],"limitations":[]}],"aggregateOutcome":"pass","coverage":{"factKinds":[],"unsupported":[],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":[]}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void Completed_With_Null_Error_Is_Accepted()
    {
        // Absent or null opposite body is allowed (matches Rust omissions).
        string good = """{"protocolVersion":1,"requestId":"r","status":"completed","error":null,"payload":{"snapshotId":"s","profile":{"id":"engine-structural","version":"0"},"facts":{"files":[]},"diagnostics":[],"ruleResults":[],"aggregateOutcome":"pass","coverage":{"factKinds":[],"unsupported":[],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":[]}}""";
        var resp = AnalysisJson.ParseResponse(good);
        Assert.IsType<AnalysisResponse.Completed>(resp);
    }

    [Fact]
    public void Failed_With_Null_Payload_Is_Accepted()
    {
        string good = """{"protocolVersion":1,"status":"failed","payload":null,"error":{"code":"QuotaExceeded","safeMessage":"m","retryable":false}}""";
        var resp = AnalysisJson.ParseResponse(good);
        Assert.IsType<AnalysisResponse.Failed>(resp);
    }

    [Fact]
    public void Valid_Production_Shaped_Completed_Is_Accepted()
    {
        // A positive production-shaped fixture must still be accepted by the stricter parser.
        string good = """
        {"protocolVersion":1,"requestId":"req-1","status":"completed","payload":{"snapshotId":"snap-1","profile":{"id":"engine-structural","version":"0"},"facts":{"files":[{"fileId":"f1","byteLength":14,"hasBom":true,"newlineKind":"crlf","truncated":false,"braceDepthExceeded":false,"tokenCount":2,"engineOutcome":"fail","tokens":[{"kind":"command","startByte":0,"endByte":6},{"kind":"braceOpen","startByte":6,"endByte":7}]}]},"diagnostics":[{"id":"ENGINE-STRUCT-001:f1:6","ruleId":"ENGINE-STRUCT-001","origin":"engine","severity":"warning","fileId":"f1","span":{"startByte":6,"endByte":7},"message":"Unmatched opening brace (group never closed).","primaryLocation":{"line":1,"column":6}}],"ruleResults":[{"ruleId":"ENGINE-STRUCT-001","origin":"engine","outcome":"fail","perFile":[{"fileId":"f1","outcome":"fail","diagnosticIds":["ENGINE-STRUCT-001:f1:6"]}],"limitations":[]}],"aggregateOutcome":"fail","coverage":{"factKinds":["command","braceOpen"],"unsupported":["macro-expansion","pdf-facts"],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":["pdfFacts","pdfChecks"]}}
        """;
        var resp = AnalysisJson.ParseResponse(good);
        var completed = Assert.IsType<AnalysisResponse.Completed>(resp);
        Assert.Equal("req-1", completed.RequestId);
        Assert.Equal(RuleOutcome.Fail, completed.Result.AggregateOutcome);
        Assert.Equal(2, completed.Result.Facts.Files[0].TokenCount);
    }

    // ---- required opaque-identity validation (round 4 follow-up) ----

    [Fact]
    public void Empty_SnapshotId_Is_Rejected()
    {
        string bad = """{"protocolVersion":1,"requestId":"r","status":"completed","payload":{"snapshotId":"","profile":{"id":"engine-structural","version":"0"},"facts":{"files":[]},"diagnostics":[],"ruleResults":[],"aggregateOutcome":"pass","coverage":{"factKinds":[],"unsupported":[],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":[]}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void Empty_Facts_FileId_Is_Rejected()
    {
        // An empty fileId, with the matching diagnostic/perFile fileIds also empty, must NOT be accepted.
        string bad = """{"protocolVersion":1,"requestId":"r","status":"completed","payload":{"snapshotId":"s","profile":{"id":"engine-structural","version":"0"},"facts":{"files":[{"fileId":"","byteLength":2,"hasBom":false,"newlineKind":"lf","truncated":false,"braceDepthExceeded":false,"tokenCount":0,"engineOutcome":"pass","tokens":[]}]},"diagnostics":[],"ruleResults":[],"aggregateOutcome":"pass","coverage":{"factKinds":[],"unsupported":[],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":[]}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void Empty_Profile_Id_Is_Rejected()
    {
        string bad = """{"protocolVersion":1,"requestId":"r","status":"completed","payload":{"snapshotId":"s","profile":{"id":"","version":"0"},"facts":{"files":[]},"diagnostics":[],"ruleResults":[],"aggregateOutcome":"pass","coverage":{"factKinds":[],"unsupported":[],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":[]}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void Empty_Diagnostic_Id_Is_Rejected()
    {
        string bad = """{"protocolVersion":1,"requestId":"r","status":"completed","payload":{"snapshotId":"s","profile":{"id":"engine-structural","version":"0"},"facts":{"files":[{"fileId":"f1","byteLength":2,"hasBom":false,"newlineKind":"lf","truncated":false,"braceDepthExceeded":false,"tokenCount":0,"engineOutcome":"pass","tokens":[]}]},"diagnostics":[{"id":"","ruleId":"ENGINE-STRUCT-001","origin":"engine","severity":"warning","fileId":"f1","message":"x"}],"ruleResults":[],"aggregateOutcome":"pass","coverage":{"factKinds":[],"unsupported":[],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":[]}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void Empty_RuleId_Is_Rejected()
    {
        string bad = """{"protocolVersion":1,"requestId":"r","status":"completed","payload":{"snapshotId":"s","profile":{"id":"engine-structural","version":"0"},"facts":{"files":[{"fileId":"f1","byteLength":2,"hasBom":false,"newlineKind":"lf","truncated":false,"braceDepthExceeded":false,"tokenCount":0,"engineOutcome":"pass","tokens":[]}]},"diagnostics":[{"id":"d1","ruleId":"","origin":"engine","severity":"warning","fileId":"f1","message":"x"}],"ruleResults":[],"aggregateOutcome":"pass","coverage":{"factKinds":[],"unsupported":[],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":[]}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void Empty_PerFile_DiagnosticId_Is_Rejected()
    {
        string bad = """{"protocolVersion":1,"requestId":"r","status":"completed","payload":{"snapshotId":"s","profile":{"id":"engine-structural","version":"0"},"facts":{"files":[{"fileId":"f1","byteLength":2,"hasBom":false,"newlineKind":"lf","truncated":false,"braceDepthExceeded":false,"tokenCount":0,"engineOutcome":"pass","tokens":[]}]},"diagnostics":[],"ruleResults":[{"ruleId":"ENGINE-STRUCT-001","origin":"engine","outcome":"pass","perFile":[{"fileId":"f1","outcome":"pass","diagnosticIds":[""]}],"limitations":[]}],"aggregateOutcome":"pass","coverage":{"factKinds":[],"unsupported":[],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":[]}}""";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void Lone_Surrogate_Opaque_Id_Is_Rejected()
    {
        // A lone high surrogate in snapshotId is not strict-valid UTF-8; the
        // parser surfaces it as JsonException (not a leaked InvalidOperationException).
        // The JSON carries the literal \uD800 escape (JsonDocument.GetString throws
        // on an unpaired surrogate; RequireOpaqueIdentity converts that to JsonException).
        string bad = "{\"protocolVersion\":1,\"requestId\":\"r\",\"status\":\"completed\",\"payload\":{\"snapshotId\":\"\\uD800x\",\"profile\":{\"id\":\"engine-structural\",\"version\":\"0\"},\"facts\":{\"files\":[]},\"diagnostics\":[],\"ruleResults\":[],\"aggregateOutcome\":\"pass\",\"coverage\":{\"factKinds\":[],\"unsupported\":[],\"truncatedFiles\":[],\"diagnosticsTruncated\":false,\"resultTruncated\":false},\"unavailableCapabilities\":[]}}";
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void Over_128_Byte_Opaque_Id_Is_Rejected()
    {
        // A snapshotId whose UTF-8 encoding exceeds 128 bytes is rejected.
        var longId = new string('q', 200);
        string bad = CompletedWith(snapshotId: longId);
        Assert.Throws<JsonException>(() => AnalysisJson.ParseResponse(bad));
    }

    [Fact]
    public void NonAscii_Unicode_Opaque_Id_Within_Bound_Is_Accepted()
    {
        // A valid multi-byte UTF-8 opaque id (Cyrillic) within 128 bytes is accepted.
        string good = """{"protocolVersion":1,"requestId":"r","status":"completed","payload":{"snapshotId":" snapshot-київ-€ ","profile":{"id":"engine-structural","version":"0"},"facts":{"files":[{"fileId":" f1-файл ","byteLength":2,"hasBom":false,"newlineKind":"lf","truncated":false,"braceDepthExceeded":false,"tokenCount":0,"engineOutcome":"pass","tokens":[]}]},"diagnostics":[],"ruleResults":[],"aggregateOutcome":"pass","coverage":{"factKinds":[],"unsupported":[],"truncatedFiles":[],"diagnosticsTruncated":false,"resultTruncated":false},"unavailableCapabilities":[]}}""";
        var resp = AnalysisJson.ParseResponse(good);
        var completed = Assert.IsType<AnalysisResponse.Completed>(resp);
        Assert.Equal(" snapshot-київ-€ ", completed.Result.SnapshotId);
    }

    [Fact]
    public void Legitimately_Long_Derived_Diagnostic_Id_Is_Accepted()
    {
        // A derived diagnostic id (file-id + rule-id + offset) may legitimately
        // exceed the 128-byte opaque-id bound; it must NOT be constrained by
        // MaxIdBytes. Here a 200-character derived id is accepted.
        var longDerived = new string('d', 200);
        string good = CompletedWith(diagnosticId: longDerived);
        var resp = AnalysisJson.ParseResponse(good);
        var completed = Assert.IsType<AnalysisResponse.Completed>(resp);
        Assert.Equal(longDerived, completed.Result.Diagnostics[0].Id);
    }

    // ---- packer reproducers (Area 2) ----

    [Fact]
    public void Packer_Forged_ByteLength_Cannot_Bypass_Limit()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("abcde");
        var hash = Sha(bytes);
        var forged = new CapturedFile(
            FileId.Parse(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            "a.tex", HasBom: false, ByteLength: 0, hash, ImmutableArray.Create(bytes));
        var snap = SnapshotWithLimits(MaxBytesPerFile: 1, MaxTotalBytes: 1, forged);
        var ex = Assert.Throws<InvalidOperationException>(() => AnalysisRequestPacker.Pack(snap, "req"));
        Assert.Contains("byte length", ex.Message);
    }

    [Fact]
    public void Packer_Too_Many_Files_Is_Rejected()
    {
        var a = MakeCaptured("a", "a.tex", "a");
        var b = MakeCaptured("b", "b.tex", "b");
        var snap = SnapshotWithLimits(MaxBytesPerFile: 100, MaxTotalBytes: 100, MaxFiles: 1, a, b);
        Assert.Throws<InvalidOperationException>(() => AnalysisRequestPacker.Pack(snap, "req"));
    }

    [Fact]
    public void Packer_Negative_And_Oversize_Lengths_Are_Rejected()
    {
        var bytes = new byte[] { 1 };
        var hash = Sha(bytes);
        var bad = new CapturedFile(FileId.New(), "a.tex", false, long.MaxValue, hash, ImmutableArray.Create(bytes));
        var snap = SnapshotWithLimits(long.MaxValue, long.MaxValue, AnalysisLimits.DMaxFiles, bad);
        Assert.Throws<InvalidOperationException>(() => AnalysisRequestPacker.Pack(snap, "req"));
    }

    [Fact]
    public void Packer_Invalid_Ids_And_Profile_Are_Rejected()
    {
        var bytes = new byte[] { 1 };
        var hash = Sha(bytes);
        var good = new CapturedFile(FileId.New(), "a.tex", false, 1, hash, ImmutableArray.Create(bytes));
        var snap = MakeSnapshotFrom(good);
        // invalid requestId (lone surrogate) -> ArgumentException before packet.
        Assert.Throws<ArgumentException>(() => AnalysisRequestPacker.Pack(snap, "\ud800"));
        // invalid profile (empty version).
        var badProfile = new AnalysisSnapshot(AnalysisSnapshotId.New(), snap.ProjectId, snap.SessionId, 0, 0, null,
            new AnalysisProfile("engine-structural", ""), snap.Limits, snap.Files, snap.Excluded);
        Assert.Throws<ArgumentException>(() => AnalysisRequestPacker.Pack(badProfile, "req"));
    }

    [Fact]
    public void Packer_Exact_Two_File_Packing()
    {
        var aBytes = System.Text.Encoding.UTF8.GetBytes("AAA");
        var bBytes = System.Text.Encoding.UTF8.GetBytes("BB");
        var a = new CapturedFile(FileId.Parse(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            "a.tex", false, aBytes.LongLength, Sha(aBytes), ImmutableArray.Create(aBytes));
        var b = new CapturedFile(FileId.Parse(Guid.Parse("00000000-0000-0000-0000-000000000002")),
            "b.tex", false, bBytes.LongLength, Sha(bBytes), ImmutableArray.Create(bBytes));
        var snap = MakeSnapshotFrom(a, b);
        var (meta, packed) = AnalysisRequestPacker.Pack(snap, "req");
        Assert.Equal(aBytes.Concat(bBytes).ToArray(), packed);
        using var doc = JsonDocument.Parse(meta);
        var files = doc.RootElement.GetProperty("payload").GetProperty("files");
        Assert.Equal(2, files.GetArrayLength());
        Assert.Equal(0, files[0].GetProperty("byteOffset").GetInt64());
        Assert.Equal(3, files[0].GetProperty("byteLength").GetInt64());
        Assert.Equal(3, files[1].GetProperty("byteOffset").GetInt64());
        Assert.Equal(2, files[1].GetProperty("byteLength").GetInt64());
    }

    [Fact]
    public void Packer_Failed_Packing_Leaves_Snapshot_Arrays_Untouched()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("abcde");
        var hash = Sha(bytes);
        var forged = new CapturedFile(FileId.New(), "a.tex", false, 0, hash, ImmutableArray.Create(bytes));
        var snap = SnapshotWithLimits(MaxBytesPerFile: 1, MaxTotalBytes: 1, forged);
        var before = snap.Files[0].Bytes.ToArray();
        Assert.Throws<InvalidOperationException>(() => AnalysisRequestPacker.Pack(snap, "req"));
        Assert.Equal(before, snap.Files[0].Bytes.ToArray());
    }

    // ---- helpers ----

    /// <summary>Build a well-formed canonical completed envelope with
    /// injectable opaque/derived identity values (serialized with
    /// JsonSerializer so the JSON is always well-formed). Used by the
    /// identity-boundary tests.</summary>
    private static string CompletedWith(string? snapshotId = null, string? fileId = null, string? diagnosticId = null, string? ruleId = null)
    {
        var sid = JsonSerializer.Serialize(snapshotId ?? "s");
        var fid = JsonSerializer.Serialize(fileId ?? "f1");
        var did = JsonSerializer.Serialize(diagnosticId ?? "ENGINE-STRUCT-001:f1:6");
        var rid = JsonSerializer.Serialize(ruleId ?? "ENGINE-STRUCT-001");
        return "{\"protocolVersion\":1,\"requestId\":\"r\",\"status\":\"completed\",\"payload\":{\"snapshotId\":" + sid
            + ",\"profile\":{\"id\":\"engine-structural\",\"version\":\"0\"},\"facts\":{\"files\":[{\"fileId\":" + fid
            + ",\"byteLength\":7,\"hasBom\":false,\"newlineKind\":\"lf\",\"truncated\":false,\"braceDepthExceeded\":false,\"tokenCount\":0,\"engineOutcome\":\"pass\",\"tokens\":[]}]},"
            + "\"diagnostics\":[{\"id\":" + did + ",\"ruleId\":" + rid + ",\"origin\":\"engine\",\"severity\":\"warning\",\"fileId\":" + fid
            + ",\"span\":{\"startByte\":6,\"endByte\":7},\"message\":\"x\",\"primaryLocation\":{\"line\":1,\"column\":6}}]"
            + ",\"ruleResults\":[{\"ruleId\":" + rid + ",\"origin\":\"engine\",\"outcome\":\"fail\",\"perFile\":[{\"fileId\":" + fid
            + ",\"outcome\":\"fail\",\"diagnosticIds\":[" + did + "]}],\"limitations\":[]}]"
            + ",\"aggregateOutcome\":\"fail\",\"coverage\":{\"factKinds\":[],\"unsupported\":[],\"truncatedFiles\":[],\"diagnosticsTruncated\":false,\"resultTruncated\":false},\"unavailableCapabilities\":[]}}";
    }

    private static byte[] B(string s) => System.Text.Encoding.UTF8.GetBytes(s);
    private static SHA256 Sha(byte[] b) => Client.Workspace.Domain.SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(b));

    private static AnalysisSnapshot MakeMiniSnapshot()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("hello");
        var hash = Sha(bytes);
        var captured = new CapturedFile(
            FileId.Parse(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            "main.tex", HasBom: false, ByteLength: bytes.LongLength, hash,
            ImmutableArray.Create(bytes));
        return new AnalysisSnapshot(
            Id: AnalysisSnapshotId.New(),
            ProjectId: ProjectId.Parse(Guid.Parse("00000000-0000-0000-0000-000000000002")),
            SessionId: Guid.Parse("00000000-0000-0000-0000-000000000003"),
            WorkingGeneration: (1L << 53) + 7, // above JS 2^53; must survive as JSON integer text
            LocalEpoch: 0,
            BaseHeadRevisionId: RevisionId.Parse(Guid.Parse("00000000-0000-0000-0000-000000000004")),
            Profile: AnalysisProfile.EngineStructural(),
            Limits: AnalysisLimits.Defaults(),
            Files: ImmutableArray.Create(captured),
            Excluded: ImmutableArray<ExcludedFile>.Empty);
    }

    private static CapturedFile MakeCaptured(string tag, string path, string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = Sha(bytes);
        return new CapturedFile(FileId.New(), path, false, bytes.LongLength, hash, ImmutableArray.Create(bytes));
    }

    private static AnalysisSnapshot MakeSnapshotFrom(params CapturedFile[] files)
        => new AnalysisSnapshot(
            AnalysisSnapshotId.New(),
            ProjectId.Parse(Guid.Parse("00000000-0000-0000-0000-000000000002")),
            Guid.Parse("00000000-0000-0000-0000-000000000003"),
            WorkingGeneration: 1, LocalEpoch: 0,
            BaseHeadRevisionId: null, AnalysisProfile.EngineStructural(),
            AnalysisLimits.Defaults(), ImmutableArray.CreateRange(files),
            ImmutableArray<ExcludedFile>.Empty);

    private static AnalysisSnapshot SnapshotWithLimits(
        long MaxBytesPerFile, long MaxTotalBytes, int MaxFiles, params CapturedFile[] files)
    {
        var limits = new AnalysisLimits(MaxBytesPerFile, MaxFiles, MaxTotalBytes,
            AnalysisLimits.DMaxResultBytes, AnalysisLimits.DMaxTokensPerFile,
            AnalysisLimits.DMaxTokensPerProject, AnalysisLimits.DMaxDiagnosticsPerProject,
            AnalysisLimits.DMaxBraceDepth);
        return new AnalysisSnapshot(AnalysisSnapshotId.New(),
            ProjectId.Parse(Guid.Parse("00000000-0000-0000-0000-000000000002")),
            Guid.Parse("00000000-0000-0000-0000-000000000003"), 1, 0, null,
            AnalysisProfile.EngineStructural(), limits,
            ImmutableArray.CreateRange(files), ImmutableArray<ExcludedFile>.Empty);
    }

    private static AnalysisSnapshot SnapshotWithLimits(long MaxBytesPerFile, long MaxTotalBytes, params CapturedFile[] files)
        => SnapshotWithLimits(MaxBytesPerFile, MaxTotalBytes, AnalysisLimits.DMaxFiles, files);
}
