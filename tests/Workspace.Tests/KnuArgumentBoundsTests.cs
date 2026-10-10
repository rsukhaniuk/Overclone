using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Analysis;
using Xunit;

namespace Workspace.Tests;

public sealed class KnuArgumentBoundsTests
{
    private static JsonNode Observations(KnuFixture f) => f.Response["payload"]!["knu"]!["files"]![0]!["observations"]!;

    private static async Task AssertRejectedWithoutMutation(KnuFixture fixture)
    {
        var snapshot = fixture.Snapshot();
        var original = fixture.Bytes;
        // Exercise BOTH public paths, even when the parser assertion fails.
        var parseFailure = Record.Exception(() => AnalysisJson.ParseResponse(fixture.Json));
        var js = new KnuProtocolTests.FixtureJs((_,rid) => fixture.Reply(snapshot,rid));
        await using var analyzer = new RustSourceAnalyzer(js);
        var result = await analyzer.AnalyzeAsync(snapshot,CancellationToken.None);
        var failed = Assert.IsType<AnalysisJobResult.Failed>(result);
        Assert.Equal("InvalidInput",failed.Error.Code);
        Assert.IsType<JsonException>(parseFailure);
        Assert.Equal(original,snapshot.Files[0].Bytes.ToArray());
        Assert.Equal(original,js.Packed);
        Assert.Equal(new[] { "analyzeSource" },js.Calls);
    }

    [Fact]
    public async Task Public_adapter_rejects_lead_probe_of_5020_byte_known_heading()
    {
        var f = KnuFixture.Load("positive");
        var bytes = f.Bytes.Concat(Encoding.UTF8.GetBytes(new string('x',5000)+"🙂")).ToArray();
        f.Data["sourceHex"] = Convert.ToHexString(bytes).ToLowerInvariant();
        var hash = Client.Workspace.Domain.SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes)).ToString();
        f.Data["sourceSha256"] = hash;
        f.Request["payload"]!["files"]![0]!["sha256"] = hash;
        f.Request["payload"]!["files"]![0]!["byteLength"] = bytes.Length;
        var facts = f.Response["payload"]!["facts"]!["files"]![0]!;
        facts["byteLength"] = bytes.Length;
        facts["tokens"]!.AsArray()[^1]!["endByte"] = bytes.Length;
        var heading = Observations(f)[1]!;
        var start = heading["valueSpan"]!["startByte"]!.GetValue<int>();
        heading["valueSpan"]!["endByte"] = bytes.Length;
        heading["declarationSpan"]!["endByte"] = bytes.Length;
        // Independent scalar iteration builds the exact bounded byte prefix;
        // this is deliberately adversarial reported ownership, not Rust output.
        var prefix = new StringBuilder(); int prefixBytes = 0;
        foreach (var rune in Encoding.UTF8.GetString(bytes.AsSpan(start)).EnumerateRunes())
        {
            if (prefixBytes + rune.Utf8SequenceLength > 256) break;
            prefix.Append(rune.ToString()); prefixBytes += rune.Utf8SequenceLength;
        }
        heading["excerpt"] = prefix.ToString(); heading["excerptTruncated"] = true;
        Assert.Equal(5020,bytes.Length-start);
        Assert.Equal("🙂",heading["value"]!["finalScalar"]!.GetValue<string>());
        Assert.True(heading["agrees"]!.GetValue<bool>());
        await AssertRejectedWithoutMutation(f);
    }

    [Theory]
    [InlineData("heading")][InlineData("indent")][InlineData("target")]
    public async Task Public_boundaries_reject_4097_byte_claims_of_known_values(string kind)
    {
        var f = KnuFixture.Load("argument-bounds"); var obs = Observations(f);
        // Start with actual native UNKNOWN values at 4097 bytes. Forge only the
        // known-value claims, keeping source/facts/counts/spans/IDs/excerpts and
        // legitimate common uncertainty internally consistent and unchanged.
        int index = kind switch { "target" => 2, "indent" => 3, _ => 4 };
        var o = obs[index]!;
        if (kind == "heading") o["value"]!["finalScalar"] = "🙂";
        else o["value"]!["length"] = obs[0]!["value"]!["length"]!.DeepClone();
        o["agrees"] = true; o["reason"] = null;
        var span = kind == "target" ? o["value"]!["targetSpan"]! : o["valueSpan"]!;
        Assert.Equal(4097,span["endByte"]!.GetValue<long>()-span["startByte"]!.GetValue<long>());
        if (kind == "heading")
        {
            var raw = f.Bytes.AsSpan((int)span["startByte"]!.GetValue<long>(),4097);
            Assert.Equal(2049,Encoding.UTF8.GetString(raw).Length); // not a 4096-char limit
        }
        await AssertRejectedWithoutMutation(f);
    }

    [Theory]
    [InlineData("heading")][InlineData("indent")][InlineData("target")]
    public async Task Exact_4096_byte_known_values_from_Rust_remain_accepted(string kind)
    {
        var f = KnuFixture.Load("argument-bounds");
        var result = Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(f.Json)).Result;
        var known = result.Knu!.Files[0].Observations[kind == "heading" ? 1 : 0];
        var span = kind == "target" ? Assert.IsType<AnalysisIndentValueDto>(known.Value).TargetSpan : known.ValueSpan;
        Assert.Equal(4096,span.EndByte-span.StartByte);
        Assert.True(known.Agrees);
        if (kind == "heading")
        {
            Assert.Equal("🙂",Assert.IsType<AnalysisHeadingValueDto>(known.Value).FinalScalar);
            Assert.Equal(2048,Encoding.UTF8.GetString(f.Bytes.AsSpan((int)span.StartByte,4096)).Length);
        }
        else Assert.NotNull(Assert.IsType<AnalysisIndentValueDto>(known.Value).Length);
        var snapshot = f.Snapshot();
        var js = new KnuProtocolTests.FixtureJs((_,rid) => f.Reply(snapshot,rid));
        await using var analyzer = new RustSourceAnalyzer(js);
        Assert.IsType<AnalysisJobResult.Completed>(await analyzer.AnalyzeAsync(snapshot,CancellationToken.None));
        Assert.Equal(f.Bytes,snapshot.Files[0].Bytes.ToArray());
    }

    [Fact]
    public async Task Actual_native_overlong_unknown_values_and_target_remain_accepted()
    {
        var f = KnuFixture.Load("argument-bounds");
        var result = Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(f.Json)).Result;
        Assert.Equal(RuleOutcome.Indeterminate,result.AggregateOutcome);
        var file = result.Knu!.Files[0];
        Assert.Contains(AnalysisLimitation.ArgumentTooLong,file.Limitations);
        Assert.Null(Assert.IsType<AnalysisIndentValueDto>(file.Observations[2].Value).Length);
        Assert.Null(Assert.IsType<AnalysisIndentValueDto>(file.Observations[3].Value).Length);
        Assert.Null(Assert.IsType<AnalysisHeadingValueDto>(file.Observations[4].Value).FinalScalar);
        foreach (var o in file.Observations.Skip(2))
        {
            Assert.Null(o.Agrees); Assert.Equal(AnalysisLimitation.UnsupportedArgument,o.Reason);
            Assert.InRange(Encoding.UTF8.GetByteCount(o.Excerpt),0,256);
        }
        Assert.Equal(4097,file.Observations[3].ValueSpan.EndByte-file.Observations[3].ValueSpan.StartByte);
        Assert.Equal(4097,file.Observations[4].ValueSpan.EndByte-file.Observations[4].ValueSpan.StartByte);
        Assert.Empty(result.Knu.FixDescriptors);
        var snapshot = f.Snapshot();
        var js = new KnuProtocolTests.FixtureJs((_,rid) => f.Reply(snapshot,rid));
        await using var analyzer = new RustSourceAnalyzer(js);
        Assert.IsType<AnalysisJobResult.Completed>(await analyzer.AnalyzeAsync(snapshot,CancellationToken.None));
        Assert.Equal(f.Bytes,snapshot.Files[0].Bytes.ToArray());
    }
}
