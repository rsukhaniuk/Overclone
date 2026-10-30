using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Analysis;
using Client.Editing.Assistance;
using Client.Workspace.Domain;
using Xunit;

namespace Workspace.Tests.AuthoringAssistance;

// Real accepted Rust source/result vectors; no fabricated native/compiler data.
public sealed class AuthoringAssistJsonTests
{
    internal static string RepositoryRoot([CallerFilePath] string sourceFile="")
    {
        // Cached SDK tests execute from /artifacts, not the source mount. The
        // compiler-supplied source path keeps fixtures tied to this checkout;
        // an explicit seam supports deterministic/path-mapped build hosts.
        string? configured=Environment.GetEnvironmentVariable("AUTHORING_ASSIST_REPOSITORY_ROOT");
        foreach(string? start in new[]{configured,Path.GetDirectoryName(sourceFile),Directory.GetCurrentDirectory(),AppContext.BaseDirectory})
        {
            if(string.IsNullOrEmpty(start) || !Directory.Exists(start))continue;
            for(var p=new DirectoryInfo(start);p is not null;p=p.Parent)
                if(Directory.Exists(Path.Combine(p.FullName,"rust/tests/fixtures/r2/assist")))return p.FullName;
        }
        throw new InvalidOperationException("Accepted Rust vectors are required, never skipped. Set AUTHORING_ASSIST_REPOSITORY_ROOT to the supplied source checkout.");
    }
    internal static (string Json,AuthoringAssistExpectation Expected,JsonNode Metadata,byte[] Packed) Vector(string name)
    {
        string dir=Path.Combine(RepositoryRoot(),"rust/tests/fixtures/r2/assist",name);
        string json=File.ReadAllText(Path.Combine(dir,"result.json"));var metadata=JsonNode.Parse(File.ReadAllText(Path.Combine(dir,"metadata.json")))!;
        byte[] packed=File.ReadAllBytes(Path.Combine(dir,"packed.utf8"));var p=metadata["payload"]!;
        var files=p["files"]!.AsArray().ToDictionary(f=>f!["fileId"]!.GetValue<string>(),f=>ImmutableArray.CreateRange(
            packed.AsSpan(f!["byteOffset"]!.GetValue<int>(),f["byteLength"]!.GetValue<int>()).ToArray()),StringComparer.Ordinal);
        Assert.True(Enum.TryParse<AuthoringAssistContext>(p["context"]!.GetValue<string>(),true,out var context));
        return (json,new(metadata["requestId"]!.GetValue<string>(),p["snapshotId"]!.GetValue<string>(),p["rootFileId"]!.GetValue<string>(),
            p["fileId"]!.GetValue<string>(),p["caretByte"]!.GetValue<long>(),context,p["query"]!.GetValue<string>(),p["catalogVersion"]!.GetValue<string>(),files),metadata,packed);
    }
    public static IEnumerable<object[]> Vectors()=>new[]{"command","environment","reference-unicode","citation","inputPath","definition","outline","unknown-catalog"}.Select(s=>new object[]{s});
    [Theory,MemberData(nameof(Vectors))]
    public void Exact_accepted_Rust_vector_is_consumed_without_reinterpreting_source(string name)
    {
        var v=Vector(name);var parsed=AuthoringAssistJson.Parse(v.Json,v.Expected);var expected=JsonNode.Parse(v.Json)!;
        if(name=="unknown-catalog"){Assert.Null(parsed.Result);Assert.Equal("UnsupportedCapability",parsed.ErrorCode);return;}
        Assert.Null(parsed.ErrorCode);Assert.NotNull(parsed.Result);
        var actual=JsonSerializer.SerializeToNode(parsed.Result,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        Assert.True(JsonNode.DeepEquals(expected["payload"],actual),name);
    }
    [Theory]
    [InlineData("version")][InlineData("id")][InlineData("extra")][InlineData("snapshot")][InlineData("root")][InlineData("file")]
    [InlineData("caret")][InlineData("catalog")][InlineData("candidate-kind")][InlineData("origin")][InlineData("extra-span")]
    [InlineData("out-of-bounds")][InlineData("end-not-caret")][InlineData("duplicate-id")][InlineData("101-candidates")]
    [InlineData("long-label")][InlineData("long-insert")][InlineData("wrong-prefix")][InlineData("caret-outside")]
    [InlineData("unknown-limitation")][InlineData("missing-limitation")][InlineData("false-complete")][InlineData("missing-field")]
    public void Malformed_shape_identity_context_and_budgets_fail_closed(string mutation)
    {
        var v=Vector("command");var root=JsonNode.Parse(v.Json)!;var p=root["payload"]!;var c=p["candidates"]![0]!;
        switch(mutation)
        {
            case "version":root["protocolVersion"]=2;break;case "id":root["requestId"]="wrong";break;case "extra":root["extra"]=true;break;
            case "snapshot":p["snapshotId"]="wrong";break;case "root":p["rootFileId"]="wrong";break;case "file":p["fileId"]="wrong";break;
            case "caret":p["caretByte"]=3;break;case "catalog":p["catalogVersion"]="2.0";break;
            case "candidate-kind":c["kind"]="label";break;case "origin":c["origin"]="projectLabel";break;
            case "extra-span":c["replaceSpan"]!["extra"]=0;break;case "out-of-bounds":c["replaceSpan"]!["endByte"]=999;break;
            case "end-not-caret":c["replaceSpan"]!["endByte"]=3;break;case "wrong-prefix":c["replaceSpan"]!["startByte"]=1;break;
            case "duplicate-id":p["candidates"]!.AsArray().Add(c.DeepClone());break;
            case "101-candidates":for(int i=1;i<101;i++){var next=c.DeepClone();next["id"]="next-"+i;p["candidates"]!.AsArray().Add(next);}break;
            case "long-label":c["label"]=new string('a',513);break;case "long-insert":c["insertText"]=new string('a',513);break;
            case "caret-outside":c["caretOffsetUtf16"]=999;break;
            case "unknown-limitation":p["limitations"]!.AsArray().Add("invented");break;
            case "missing-limitation":p["limitations"]!.AsArray().RemoveAt(0);break;
            case "false-complete":p["complete"]=true;break;case "missing-field":c.AsObject().Remove("definition");break;
        }
        var parsed=AuthoringAssistJson.Parse(root.ToJsonString(),v.Expected);Assert.Null(parsed.Result);Assert.Equal("InvalidResponse",parsed.ErrorCode);
    }
    [Fact] public void Duplicate_fields_invalid_unicode_noninteger_and_oversize_never_throw_or_publish()
    {
        var v=Vector("command");string compact=JsonNode.Parse(v.Json)!.ToJsonString();
        foreach(string json in new[]{compact.Replace("\"protocolVersion\":1","\"protocolVersion\":1,\"protocolVersion\":1"),
            compact.Replace("\"startByte\":0","\"startByte\":0,\"startByte\":0"),compact.Replace("\"startByte\":0","\"startByte\":1e309"),
            compact.Replace("\"protocolVersion\":1","\"protocolVersion\":1.5"),compact.Replace("\"section\"","\"\\ud800\""),
            compact+new string(' ',262145),"null","[]","{}"})
        {var parsed=AuthoringAssistJson.Parse(json,v.Expected);Assert.Null(parsed.Result);Assert.Equal("InvalidResponse",parsed.ErrorCode);}
    }
    [Fact] public void Scalar_splitting_utf8_definition_and_utf16_insertion_caret_are_rejected()
    {
        var v=Vector("reference-unicode");var root=JsonNode.Parse(v.Json)!;var c=root["payload"]!["candidates"]![0]!;
        c["replaceSpan"]!["startByte"]=c["replaceSpan"]!["startByte"]!.GetValue<long>()+1;
        Assert.Null(AuthoringAssistJson.Parse(root.ToJsonString(),v.Expected).Result);
        root=JsonNode.Parse(v.Json)!;c=root["payload"]!["candidates"]![0]!;
        c["definition"]!["span"]!["startByte"]=c["definition"]!["span"]!["startByte"]!.GetValue<long>()+1;
        Assert.Null(AuthoringAssistJson.Parse(root.ToJsonString(),v.Expected).Result);
        root=JsonNode.Parse(v.Json)!;c=root["payload"]!["candidates"]![0]!;c["insertText"]="😀";c["caretOffsetUtf16"]=1;
        Assert.Null(AuthoringAssistJson.Parse(root.ToJsonString(),v.Expected).Result);
    }
    [Fact] public void Outline_parent_and_package_status_are_strict()
    {
        var v=Vector("outline");var root=JsonNode.Parse(v.Json)!;root["payload"]!["outline"]![0]!["parentIndex"]=0;
        Assert.Null(AuthoringAssistJson.Parse(root.ToJsonString(),v.Expected).Result);
        v=Vector("environment");root=JsonNode.Parse(v.Json)!;root["payload"]!["loadedPackages"]![0]!["status"]="available";
        Assert.Null(AuthoringAssistJson.Parse(root.ToJsonString(),v.Expected).Result);
    }
    internal static AuthoringAssistRequest Request(string source="\\sec")
    {
        byte[] bytes=Encoding.UTF8.GetBytes(source);var file=new CapturedFile(FileId.New(),"main.tex",false,bytes.Length,
            CompilationTestWorkspace.Hash(bytes),ImmutableArray.CreateRange(bytes));
        var snapshot=new AnalysisSnapshot(AnalysisSnapshotId.New(),ProjectId.New(),Guid.NewGuid(),1,0,null,
            AnalysisProfile.EngineStructural(),AnalysisLimits.Defaults(),[file],[]);
        return new(snapshot,"request-1",file.FileId.ToString(),file.FileId.ToString(),bytes.Length,AuthoringAssistContext.Command,source[1..]);
    }
    [Fact] public void Packing_reuses_analysis_descriptors_and_exact_bytes_with_binary_metadata_only()
    {
        var r=Request();var binary=new ExcludedFile(FileId.New(),"asset.bin","binary","binary-file");
        r=r with{Snapshot=r.Snapshot with{Excluded=[binary]}};
        var packed=AuthoringAssistJson.Pack(r);Assert.Equal(Encoding.UTF8.GetBytes("\\sec"),packed.PackedBytes);
        using var doc=JsonDocument.Parse(packed.MetadataJson);var p=doc.RootElement.GetProperty("payload");
        Assert.Equal("authoringAssist",doc.RootElement.GetProperty("operation").GetString());
        Assert.False(p.TryGetProperty("profile",out _));Assert.Equal(2,p.GetProperty("files").GetArrayLength());
        var excluded=p.GetProperty("files")[1];Assert.Equal("binary",excluded.GetProperty("mediaKind").GetString());
        Assert.Equal(0,excluded.GetProperty("byteLength").GetInt64());Assert.Equal(262144,p.GetProperty("limits").GetProperty("maxResultBytes").GetInt64());
    }
}
