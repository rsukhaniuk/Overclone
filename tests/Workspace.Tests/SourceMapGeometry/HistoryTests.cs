using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Compilation;
using Client.Compilation.Assets;
using Client.Compilation.History;
using Client.RuntimeAssets;
using Client.Workspace.Domain;
using Xunit;
using Backend=global::Workspace.Tests.CompilationNative.SetupBackend;
using static Workspace.Tests.SourceMapGeometry.GeometryFixture;

namespace Workspace.Tests.SourceMapGeometry;

// Existing public history store and repository fixtures; no live/native bridge.
public sealed class HistoryTests
{
    private static ArtifactHistoryBudget Limits()=>new(new(32,1048576,8388608,65536),1048576,65536,new(new(2,256,1048576,2097152,4096,256,256),16384,16,32,30000));
    private static async Task<AcceptedCompileOutput> Output(CompilationTestWorkspace f,bool geometry)
    {
        var ctx=CompileRequestContext.Create(await f.Snapshot(),3,4096,Format,Adapter).Context!;
        var made=CompileOutputEnvelope.Create(Input(ctx,geometry),Budget);Assert.Null(made.Failure);return made.Envelope!.TryAccept(ctx).Output!;
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Historical_roundtrip_retains_versioned_identity_and_only_pdf_never_raw_geometry_or_map(bool geometry)
    {
        await using var f=await CompilationTestWorkspace.Open();var accepted=await Output(f,geometry);var io=new MutableBackend();
        await using var store=new CompileArtifactHistoryStore(io,f.Repo,Format,Adapter);var before=f.State();
        var persisted=await store.PersistAsync(accepted,null,Limits());Assert.Equal(RuntimeAssetStatus.Installed,persisted.Status);
        var metadata=JsonNode.Parse(io.Inner.Files[persisted.Key!.RecordId][0])!;
        Assert.Equal(geometry?2:1,metadata["version"]!.GetValue<int>());
        if(geometry){Assert.Equal(2,metadata["artifactVersion"]!.GetValue<int>());Assert.Equal(accepted.SourceMapGeometry!.Hash.Hex,metadata["sourceMapGeometry"]!["hash"]!["hex"]!.GetValue<string>());}
        else {Assert.Null(metadata["artifactVersion"]);Assert.Null(metadata["sourceMapGeometry"]);}
        Assert.Null(metadata["sourceMapGeometryPayload"]);Assert.Null(metadata["sourceMapPayload"]);Assert.Null(metadata["pages"]);Assert.Equal(2,io.Inner.Files[persisted.Key.RecordId].Count);
        var loaded=await store.ReadAsync(persisted.Key,f.Repo.Project.Id,null,Limits());Assert.Equal(RuntimeAssetStatus.Ready,loaded.Status);
        var artifact=loaded.Artifact!;Assert.Equal(accepted.ContentIdentity,artifact.ContentIdentity);Assert.Equal(accepted.ToPayloadArray(),artifact.CopyPdf());
        var returned=artifact.CopyPdf();returned[0]^=1;Assert.Equal(accepted.ToPayloadArray(),artifact.CopyPdf());
        Assert.Equal("HistoricalOnly",artifact.Classification);Assert.Null(typeof(HistoricalCompileArtifact).GetProperty("IsCurrent"));
        Assert.Null(typeof(HistoricalCompileArtifact).GetMethod("ToSourceMapGeometryArray"));Assert.Null(typeof(HistoricalCompileArtifact).GetMethod("ToSourceMapPayloadArray"));Assert.Equal(before,f.State());
    }
    [Fact]
    public async Task Version1_metadata_bytes_keep_exact_old_field_order_and_serialization()
    {
        await using var f=await CompilationTestWorkspace.Open();var accepted=await Output(f,false);var io=new MutableBackend();await using var store=new CompileArtifactHistoryStore(io,f.Repo,Format,Adapter);
        var saved=await store.PersistAsync(accepted,null,Limits());Assert.Equal(RuntimeAssetStatus.Installed,saved.Status);
        // Independent old wire shape: no new zero/null/version/geometry fields.
        var old=new{
            Version=1,Classification="HistoricalOnly",ProjectId=accepted.SnapshotIdentity.ProjectId,OriginalRequest=accepted.RequestId,
            OriginalSnapshot=accepted.SnapshotIdentity,OriginalWorkerEpoch=accepted.WorkerEpoch,InputDigest=accepted.InputContentDigest,
            Selection=new CompilationSelectionInput(accepted.Snapshot.Selection.MainFileId,accepted.Snapshot.Selection.Engine,accepted.Snapshot.Selection.Bundles,accepted.Snapshot.Selection.Tools,accepted.Snapshot.Selection.Options),
            Files=accepted.Snapshot.Files.Select(c=>new ProjectFile(c.FileId,c.RelativePath,c.MediaKind,c.Hash,c.ByteLength,c.HasBom)).ToArray(),
            PdfLength=accepted.OutputLength,PdfHash=accepted.OutputHash!.Value,Format=accepted.OutputFormat!,Adapter=accepted.OutputAdapter!,Mapping=accepted.SourceMapping,
            ContentDigest=accepted.ContentIdentity!.Digest,OriginalRequestedRevision=(RevisionId?)null,
        };
        var expected=JsonSerializer.SerializeToUtf8Bytes(old,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        Assert.Equal(expected,io.Inner.Files[saved.Key!.RecordId][0]);
    }
    [Theory]
    [InlineData("version")][InlineData("artifactVersion")][InlineData("missingArtifactVersion")][InlineData("missingGeometry")]
    [InlineData("hash")][InlineData("lengthZero")][InlineData("lengthHuge")][InlineData("wrongDomain")][InlineData("missingMap")]
    [InlineData("extra")][InlineData("rawGeometry")][InlineData("duplicate")][InlineData("v1Geometry")]
    public async Task Forged_v2_metadata_with_coherent_storage_hashes_is_rejected_before_pdf_read(string fault)
    {
        await using var f=await CompilationTestWorkspace.Open();var accepted=await Output(f,true);var io=new MutableBackend();await using var store=new CompileArtifactHistoryStore(io,f.Repo,Format,Adapter);
        var saved=await store.PersistAsync(accepted,null,Limits());Assert.Equal(RuntimeAssetStatus.Installed,saved.Status);var key=saved.Key!;
        var root=JsonNode.Parse(io.Inner.Files[key.RecordId][0])!;
        switch(fault){
            case "version":root["version"]=3;break;
            case "artifactVersion":root["artifactVersion"]=1;break;
            case "missingArtifactVersion":root.AsObject().Remove("artifactVersion");break;
            case "missingGeometry":root.AsObject().Remove("sourceMapGeometry");break;
            case "hash":root["sourceMapGeometry"]!["hash"]!["hex"]=new string('0',64);break;
            case "lengthZero":root["sourceMapGeometry"]!["byteLength"]=0;break;
            case "lengthHuge":root["sourceMapGeometry"]!["byteLength"]=131073;break;
            case "wrongDomain":root["contentDigest"]!["hex"]=(await Output(f,false)).ContentIdentity!.Digest.Hex;break;
            case "missingMap":root["mapping"]=null;break;
            case "extra":root["isCurrent"]=true;break;
            case "rawGeometry":root["sourceMapGeometryPayload"]=Convert.ToBase64String(accepted.ToSourceMapGeometryArray()!);break;
            case "v1Geometry":root["version"]=1;break;
        }
        var raw=Bytes(root.ToJsonString());if(fault=="duplicate")raw=Bytes(root.ToJsonString().Replace("\"version\":2","\"version\":2,\"version\":2"));
        var forged=await io.Rebind(key,raw,Limits());int reads=io.PdfReads;
        var result=await store.ReadAsync(forged,f.Repo.Project.Id,null,Limits());Assert.Equal(RuntimeAssetStatus.Corrupt,result.Status);Assert.Null(result.Artifact);Assert.Equal(reads,io.PdfReads);
    }
    [Fact]
    public async Task Missing_v2_fields_do_not_turn_forged_version1_into_new_authority()
    {
        await using var f=await CompilationTestWorkspace.Open();var io=new MutableBackend();await using var store=new CompileArtifactHistoryStore(io,f.Repo,Format,Adapter);
        var saved=await store.PersistAsync(await Output(f,false),null,Limits());var key=saved.Key!;var r=JsonNode.Parse(io.Inner.Files[key.RecordId][0])!;r["version"]=2;
        var forged=await io.Rebind(key,Bytes(r.ToJsonString()),Limits());Assert.Equal(RuntimeAssetStatus.Corrupt,(await store.ReadAsync(forged,f.Repo.Project.Id,null,Limits())).Status);
    }
    private sealed class MutableBackend:IRuntimeAssetBackend
    {
        internal Backend Inner=new();internal int PdfReads;private readonly Dictionary<Guid,string> _overrides=[];
        public Task<IRuntimeAssetLease?> TryAcquireAsync()=>Inner.TryAcquireAsync();
        public Task CreateStageAsync(IRuntimeAssetLease lease,Guid id,int max)=>Inner.CreateStageAsync(lease,id,max);
        public Task WriteAsync(IRuntimeAssetLease lease,Guid id,int index,byte[] bytes)=>Inner.WriteAsync(lease,id,index,bytes);
        public Task<StoredAssetBytes?> ReadAsync(Guid id,int index,long expected,long maximum){if(index==1)PdfReads++;return Inner.ReadAsync(id,index,expected,maximum);}
        public Task<string?> CatalogAsync(Guid id,int max)=>_overrides.TryGetValue(id,out var json)?Task.FromResult<string?>(json):Inner.CatalogAsync(id,max);
        public Task<bool> StageExistsAsync(Guid id)=>Inner.StageExistsAsync(id);
        public Task PublishAsync(IRuntimeAssetLease lease,Guid id,string text,int max)=>Inner.PublishAsync(lease,id,text,max);
        public Task<bool> DiscardAsync(IRuntimeAssetLease lease,Guid id)=>Inner.DiscardAsync(lease,id);
        public ValueTask DisposeAsync()=>Inner.DisposeAsync();
        internal async Task<HistoricalArtifactKey> Rebind(HistoricalArtifactKey key,byte[] metadata,ArtifactHistoryBudget budget)
        {
            Inner.Files[key.RecordId][0]=metadata;var root=JsonNode.Parse((await CatalogAsync(key.RecordId,budget.Storage.MaxCatalogBytes))!)!;
            var assets=new List<DeclaredAssetInput>();
            foreach(var a in root["assets"]!.AsArray())
            {
                var n=a!;int index=n["relativePath"]!.GetValue<string>().EndsWith("metadata.json",StringComparison.Ordinal)?0:1;var data=Inner.Files[key.RecordId][index];
                n["hash"]=Digest(data).Hex;n["declaredLength"]=data.Length;
                assets.Add(new(){AssetId=new(Guid.ParseExact(n["assetId"]!.GetValue<string>(),"N")),RelativePath=n["relativePath"]!.GetValue<string>(),DeclaredHash=Digest(data),DeclaredLength=data.Length,Version=n["version"]!.GetValue<string>()});
            }
            var inventory=AssetInventoryFactory.Create(new(assets),budget.Storage.Inventory).Inventory!;root["inventoryDigest"]=inventory.Identity.Digest.Hex;_overrides[key.RecordId]=root.ToJsonString();return key with{StorageInventoryDigest=inventory.Identity.Digest};
        }
    }
}
