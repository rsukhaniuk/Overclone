using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Client.Analysis;
using Client.Workspace.Domain;

namespace Workspace.Tests.ThesisSource;

internal sealed class ThesisFixture(JsonObject data)
{
    internal JsonObject Data { get; } = data;
    internal JsonObject Response => Data["response"]!.DeepClone().AsObject();
    internal byte[] Packed => Convert.FromHexString(Data["packedUtf8Hex"]!.GetValue<string>());
    internal static JsonObject Resource(string name)
    {
        using var stream = typeof(ThesisFixture).Assembly.GetManifestResourceStream("Workspace.Tests.Fixtures.ThesisSource."+name+".json")
            ?? throw new InvalidOperationException("Required actual-WASM thesis fixture missing.");
        return JsonNode.Parse(stream)!.AsObject();
    }
    internal static IEnumerable<string> Ids => Resource("actual-wasm-vectors")["vectors"]!.AsArray().Select(v=>v!["id"]!.GetValue<string>()).ToArray();
    internal static ThesisFixture Load(string id) => new(Resource("actual-wasm-vectors")["vectors"]!.AsArray().Single(v=>v!["id"]!.GetValue<string>()==id)!.DeepClone().AsObject());
    internal AnalysisSnapshot Snapshot()
    {
        var p=Data["request"]!["payload"]!;var packed=Packed;
        var files=p["files"]!.AsArray().Select(f=>{
            var b=packed.AsSpan(f!["byteOffset"]!.GetValue<int>(),f["byteLength"]!.GetValue<int>()).ToArray();
            return new CapturedFile(FileId.New(),f["relativePath"]!.GetValue<string>(),f["hasBom"]!.GetValue<bool>(),b.Length,
                SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(b)),ImmutableArray.CreateRange(b));
        }).ToImmutableArray();
        int root=p["files"]!.AsArray().Select((f,i)=>(f,i)).Single(x=>x.f!["fileId"]!.GetValue<string>()==p["referenceConfiguration"]!["rootFileId"]!.GetValue<string>()).i;
        return new(AnalysisSnapshotId.New(),ProjectId.New(),Guid.NewGuid(),1,1,RevisionId.New(),
            new(AnalysisCatalog.KnuId,AnalysisCatalog.ThesisVersion),AnalysisLimits.Defaults(),files,[],
            AnalysisJson.ParseConfiguration(p["configuration"]!.ToJsonString()),ReferenceConfiguration:new(files[root].FileId.ToString()));
    }
    internal string Reply(AnalysisSnapshot s,string requestId)
    {
        var map=Data["request"]!["payload"]!["files"]!.AsArray().ToDictionary(f=>f!["fileId"]!.GetValue<string>(),
            f=>s.Files.Single(c=>c.RelativePath==f!["relativePath"]!.GetValue<string>()).FileId.ToString(),StringComparer.Ordinal);
        return Rebind(Response,s,requestId,map);
    }
    internal static string Rebind(JsonObject response,AnalysisSnapshot s,string requestId,Dictionary<string,string> map)
    {
        string Derived(string value)
        {
            foreach(var kv in map)
            {
                if(value.StartsWith(kv.Key+":",StringComparison.Ordinal))return kv.Value+value[kv.Key.Length..];
                var engine="ENGINE-STRUCT-001:"+kv.Key+":";
                if(value.StartsWith(engine,StringComparison.Ordinal))return "ENGINE-STRUCT-001:"+kv.Value+":"+value[engine.Length..];
            }
            return value;
        }
        void Visit(JsonNode? node,string field="")
        {
            if(node is JsonObject obj)foreach(var kv in obj.ToArray())
            {
                if(kv.Key=="fileId"||kv.Key=="rootFileId")obj[kv.Key]=map[kv.Value!.GetValue<string>()];
                else if(kv.Key=="snapshotId")obj[kv.Key]=s.Id.ToString();
                else if(kv.Key=="id" && kv.Value is JsonValue val && val.TryGetValue<string>(out var text))obj[kv.Key]=Derived(text);
                else Visit(kv.Value,kv.Key);
            }
            else if(node is JsonArray array)for(int i=0;i<array.Count;i++)
            {
                if(field is "closureFileIds" or "truncatedFiles")array[i]=map[array[i]!.GetValue<string>()];
                else if(field is "evidenceIds" or "diagnosticIds")array[i]=Derived(array[i]!.GetValue<string>());
                else Visit(array[i]);
            }
        }
        Visit(response);response["requestId"]=requestId;
        return response.ToJsonString();
    }
}
