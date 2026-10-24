using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Client.Analysis;
using Client.Workspace.Domain;
using Xunit;

namespace Workspace.Tests.SourceReferences;

// Captured public analyze_source responses, not a second reference evaluator.
public sealed class ActualRustReferenceTests
{
    public static TheoryData<string> Cases => new()
    {
        "forward", "missing", "duplicate", "unicode", "macro", "conditional",
        "unknown-environment", "comment-verbatim", "nonliteral", "unbraced",
        "dynamic", "unsafe", "include-extension", "multifile", "unreachable",
        "root-cwd", "cycle", "repeat", "binary-target", "missing-include",
    };

    private static JsonObject Load(string name)
    {
        using var stream = typeof(ActualRustReferenceTests).Assembly.GetManifestResourceStream(
            $"Workspace.Tests.Fixtures.SourceReferences.{name}.json")!;
        return (JsonObject)JsonNode.Parse(stream)!;
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task Actual_Rust_output_passes_strict_codec_and_captured_byte_boundary(string name)
    {
        var fixture = Load(name);
        var raw = (JsonObject)fixture["response"]!;
        var original = Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(raw.ToJsonString()));
        Assert.Equal(AnalysisCatalog.ReferenceId, original.Result.Profile.Id);
        Assert.Equal(3, original.Result.RuleResults.Count);
        Assert.Null(original.Result.Knu);
        // The browser captures only eligible UTF-8 .tex text; the binary-target
        // case tests Rust/codec semantics, not a fabricated C# text capture.
        if (name == "binary-target") return;
        var packed = Convert.FromHexString(fixture["sourceHex"]!.GetValue<string>());
        var request = fixture["request"]!["payload"]!;
        var files = ImmutableArray.CreateBuilder<CapturedFile>();
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var descriptor in request["files"]!.AsArray())
        {
            var bytes = packed.AsSpan(descriptor!["byteOffset"]!.GetValue<int>(), descriptor["byteLength"]!.GetValue<int>()).ToArray();
            var id = FileId.New(); ids.Add(descriptor["fileId"]!.GetValue<string>(), id.ToString());
            files.Add(new(id, descriptor["relativePath"]!.GetValue<string>(), descriptor["hasBom"]!.GetValue<bool>(), bytes.Length,
                SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes)), ImmutableArray.CreateRange(bytes)));
        }
        var snapshot = new AnalysisSnapshot(AnalysisSnapshotId.New(), ProjectId.New(), Guid.NewGuid(), 7, 1,
            RevisionId.New(), new(AnalysisCatalog.ReferenceId, AnalysisCatalog.ReferenceVersion), AnalysisLimits.Defaults(),
            files.ToImmutable(), [], ReferenceConfiguration: new(ids[request["referenceConfiguration"]!["rootFileId"]!.GetValue<string>()]));
        string Reply(string requestId)
        {
            // Rebind only capture/transport identities. Keep every Rust token,
            // key, byte span, outcome and limitation unchanged.
            var response = (JsonObject)raw.DeepClone(); var payload = response["payload"]!;
            response["requestId"] = requestId; payload["snapshotId"] = snapshot.Id.ToString();
            payload["referenceConfiguration"]!["rootFileId"] = snapshot.ReferenceConfiguration!.RootFileId;
            var diagnostics = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var d in payload["diagnostics"]!.AsArray())
            {
                var oldFile = d!["fileId"]!.GetValue<string>();
                var oldId = d["id"]!.GetValue<string>();
                var newId = d["ruleId"]!.GetValue<string>() + ":" + ids[oldFile] + ":" + d["span"]!["startByte"]!.GetValue<long>();
                diagnostics.Add(oldId, newId); d["id"] = newId; d["fileId"] = ids[oldFile];
            }
            foreach (var file in payload["facts"]!["files"]!.AsArray()) file!["fileId"] = ids[file["fileId"]!.GetValue<string>()];
            foreach (var rule in payload["ruleResults"]!.AsArray())
                foreach (var file in rule!["perFile"]!.AsArray())
                {
                    file!["fileId"] = ids[file["fileId"]!.GetValue<string>()];
                    var entries = file["diagnosticIds"]!.AsArray();
                    for (int i = 0; i < entries.Count; i++) entries[i] = diagnostics[entries[i]!.GetValue<string>()];
                }
            var truncated = payload["coverage"]!["truncatedFiles"]!.AsArray();
            for (int i = 0; i < truncated.Count; i++) truncated[i] = ids[truncated[i]!.GetValue<string>()];
            return response.ToJsonString();
        }
        var js = new KnuProtocolTests.FixtureJs((_, requestId) => Reply(requestId));
        await using var analyzer = new RustSourceAnalyzer(js);
        var completed = Assert.IsType<AnalysisJobResult.Completed>(await analyzer.AnalyzeAsync(snapshot, default));
        Assert.Equal(original.Result.AggregateOutcome, completed.Result.AggregateOutcome);
        Assert.Equal(original.Result.Diagnostics.Count, completed.Result.Diagnostics.Count);
        Assert.Equal(packed, js.Packed);
    }
}
