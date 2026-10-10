using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Analysis;
using Client.Workspace.Domain;

namespace Workspace.Tests;

internal sealed class KnuFixture
{
    public JsonObject Data { get; }
    public byte[] Bytes => Convert.FromHexString(Data["sourceHex"]!.GetValue<string>());
    public JsonObject Request => (JsonObject)Data["request"]!;
    public JsonObject Response => (JsonObject)Data["response"]!;
    public string Json => Response.ToJsonString();
    public static KnuFixture Load(string name) => new(name);
    private KnuFixture(string name)
    {
        using var stream = typeof(KnuFixture).Assembly.GetManifestResourceStream($"Workspace.Tests.Fixtures.Knu.{name}.json")!;
        Data = (JsonObject)JsonNode.Parse(stream)!;
    }
    public AnalysisSnapshot Snapshot()
    {
        var p = Request["payload"]!;
        var bytes = Bytes;
        var fid = new FileId(Guid.Parse(p["files"]![0]!["fileId"]!.GetValue<string>()));
        var file = new CapturedFile(fid, "main.tex", bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }), bytes.Length,
            SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes)), ImmutableArray.Create(bytes));
        return new AnalysisSnapshot(new(Guid.Parse(p["snapshotId"]!.GetValue<string>())),
            new(Guid.Parse(p["projectId"]!.GetValue<string>())), Guid.Parse(p["sessionId"]!.GetValue<string>()),
            p["workingGeneration"]!.GetValue<long>(), 0, new RevisionId(Guid.Parse(p["baseHeadRevisionId"]!.GetValue<string>())),
            new(p["profile"]!["id"]!.GetValue<string>(), p["profile"]!["version"]!.GetValue<string>()), AnalysisLimits.Defaults(),
            ImmutableArray.Create(file), ImmutableArray<ExcludedFile>.Empty,
            p["configuration"] is { } cfg ? AnalysisJson.ParseConfiguration(cfg.ToJsonString()) : null);
    }
    // Rebind transport identities to an actual capture without inventing rule
    // output. Source, facts, observations, metadata and spans remain Rust data.
    public string Reply(AnalysisSnapshot snapshot, string requestId)
    {
        var reply = (JsonObject)Response.DeepClone();
        var p = Request["payload"]!;
        var oldFile = p["files"]![0]!["fileId"]!.GetValue<string>();
        var map = new Dictionary<string, string> {
            [p["snapshotId"]!.GetValue<string>()] = snapshot.Id.ToString(),
            [p["projectId"]!.GetValue<string>()] = snapshot.ProjectId.ToString(),
            [p["sessionId"]!.GetValue<string>()] = snapshot.SessionId.ToString("N"),
            [oldFile] = snapshot.Files[0].FileId.ToString(),
        };
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
                foreach (var kv in obj.ToArray())
                {
                    if (kv.Value is JsonValue value && value.TryGetValue<string>(out var s))
                    {
                        if (map.TryGetValue(s, out var updated)) obj[kv.Key] = updated;
                        else if (s.StartsWith(oldFile + ":", StringComparison.Ordinal)) obj[kv.Key] = snapshot.Files[0].FileId + s[oldFile.Length..];
                    }
                    else Visit(kv.Value);
                }
            else if (node is JsonArray array)
                for (int i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var s) && s.StartsWith(oldFile + ":", StringComparison.Ordinal))
                        array[i] = snapshot.Files[0].FileId + s[oldFile.Length..];
                    else Visit(array[i]);
                }
        }
        Visit(reply);
        reply["requestId"] = requestId;
        if (reply["payload"]!["configuration"] is not null)
        {
            reply["payload"]!["configuration"] = JsonSerializer.SerializeToNode(snapshot.Configuration, AnalysisJson.Options);
            foreach (var f in reply["payload"]!["knu"]!["fixDescriptors"]!.AsArray())
            {
                f!["configuration"] = JsonSerializer.SerializeToNode(snapshot.Configuration, AnalysisJson.Options);
                f["workingGeneration"] = snapshot.WorkingGeneration;
                f["baseHeadRevisionId"] = snapshot.BaseHeadRevisionId?.ToString();
            }
        }
        return reply.ToJsonString();
    }
}
