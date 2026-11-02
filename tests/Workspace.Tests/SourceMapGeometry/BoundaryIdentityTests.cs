using System.Collections;
using System.Text.Json.Nodes;
using Client.Compilation;
using Client.Compilation.SyncTeX;
using Client.Workspace.Domain;
using Xunit;
using static Workspace.Tests.SourceMapGeometry.GeometryFixture;

namespace Workspace.Tests.SourceMapGeometry;

public sealed class BoundaryIdentityTests
{
    [Fact]
    public async Task Geometry_is_owned_with_map_pdf_context_and_defensive_getters_not_live_authority()
    {
        await using var f=await CompilationTestWorkspace.Open();var snapshot=await f.Snapshot();var context=CompileRequestContext.Create(snapshot,3,4096,Format,Adapter).Context!;
        var input=Input(context);var originalGeometry=input.SourceMapGeometryPayload!.ToArray();var originalMap=input.SourceMapPayload!.ToArray();var originalPdf=input.Payload!.ToArray();var before=f.State();
        var made=CompileOutputEnvelope.Create(input,Budget);Assert.Null(made.Failure);var envelope=Assert.IsType<CompileOutputEnvelope>(made.Envelope);
        input.SourceMapGeometryPayload![0]=0;input.SourceMapPayload![0]^=1;input.Payload![0]^=1;
        var accepted=Assert.IsType<AcceptedCompileOutput>(envelope.TryAccept(context).Output);Assert.Same(context,accepted.RequestContext);
        Assert.Equal(originalGeometry,accepted.ToSourceMapGeometryArray());Assert.Equal(originalMap,accepted.ToSourceMapPayloadArray());Assert.Equal(originalPdf,accepted.ToPayloadArray());
        var copy=accepted.ToSourceMapGeometryArray()!;copy[0]=0;Assert.Equal(originalGeometry,accepted.SourceMapGeometryBytes.ToArray());
        Assert.Equal(2,accepted.ContentIdentity!.ArtifactVersion);Assert.Equal(accepted.SourceMapGeometry,accepted.ContentIdentity.SourceMapGeometry);
        Assert.Equal(Digest(originalGeometry),accepted.SourceMapGeometry!.Hash);Assert.True(accepted.HasSourceMapGeometry);
        Assert.Equal(before,f.State());Assert.Equal(0,f.Repo.Commits);
        Assert.Null(typeof(CompileOutputEnvelope).GetMethod("ToSourceMapGeometryArray"));
        Assert.Equal(CompileOutputIssue.RequestMismatch,envelope.TryAccept(CompileRequestContext.Create(snapshot,3,4096,Format,Adapter).Context!).Failure);
    }
    [Theory]
    [InlineData("noMap")][InlineData("emptyMap")][InlineData("identityOnlyMap")][InlineData("emptyPdf")]
    [InlineData("emptyGeometry")][InlineData("identityOnlyGeometry")][InlineData("noGeometryIdentity")]
    [InlineData("geometryHash")][InlineData("geometryLength")][InlineData("mapDigest")][InlineData("pdfDigest")][InlineData("malformed")]
    public async Task Missing_owned_bytes_and_forged_correlations_never_create_accepted_geometry(string fault)
    {
        await using var f=await CompilationTestWorkspace.Open();var context=CompileRequestContext.Create(await f.Snapshot(),3,4096,Format,Adapter).Context!;var input=Input(context);
        switch(fault){
            case "noMap":input=input with{SourceMapPayload=null,SourceMapping=null};break;
            case "emptyMap":input=input with{SourceMapPayload=[]};break;
            case "identityOnlyMap":input=input with{SourceMapPayload=null};break;
            case "emptyPdf":input=input with{Payload=[],ClaimedLength=0,ClaimedHash=Digest([])};break;
            case "emptyGeometry":input=input with{SourceMapGeometryPayload=[]};break;
            case "identityOnlyGeometry":input=input with{SourceMapGeometryPayload=null};break;
            case "noGeometryIdentity":input=input with{SourceMapGeometry=null};break;
            case "geometryHash":input=input with{SourceMapGeometry=input.SourceMapGeometry! with{Hash=new(new string('0',64))}};break;
            case "geometryLength":input=input with{SourceMapGeometry=input.SourceMapGeometry! with{ByteLength=1}};break;
            case "malformed":input=input with{SourceMapGeometryPayload=[1,2],SourceMapGeometry=new(Digest([1,2]),2)};break;
            default:
                var json=JsonNode.Parse(input.SourceMapGeometryPayload!)!;json[fault=="mapDigest"?"mapSha256":"pdfSha256"]=new string('0',64);
                var bytes=Bytes(json.ToJsonString());input=input with{SourceMapGeometryPayload=bytes,SourceMapGeometry=new(Digest(bytes),bytes.Length)};break;
        }
        var result=CompileOutputEnvelope.Create(input,Budget);Assert.Null(result.Envelope);Assert.NotNull(result.Failure);
    }
    [Theory]
    [InlineData(CompileOutputStatus.Failed)][InlineData(CompileOutputStatus.Cancelled)][InlineData(CompileOutputStatus.TimedOut)][InlineData(CompileOutputStatus.Unsupported)]
    public async Task Non_success_forbids_geometry_even_empty_or_identity_only(CompileOutputStatus status)
    {
        await using var f=await CompilationTestWorkspace.Open();var ctx=CompileRequestContext.Create(await f.Snapshot(),1,4096,Format,Adapter).Context!;var input=Input(ctx) with{Status=status,Payload=null,SourceMapPayload=null,ClaimedHash=null,ClaimedLength=0};
        foreach(var bytes in new byte[]?[]{input.SourceMapGeometryPayload,[],null})
            Assert.Equal(CompileOutputIssue.PayloadOnNonSuccess,CompileOutputEnvelope.Create(input with{SourceMapGeometryPayload=bytes},Budget).Failure);
        var old=CompileOutputEnvelope.Create(input with{SourceMapGeometryPayload=null,SourceMapGeometry=null},Budget).Envelope!.TryAccept(ctx).Output!;
        Assert.False(old.HasSourceMapGeometry);Assert.Null(old.ToSourceMapGeometryArray());Assert.Null(old.ContentIdentity);
    }
    [Fact]
    public async Task Geometry_cap_and_combined_metadata_budget_win_before_copy_hash_or_hostile_walk()
    {
        await using var f=await CompilationTestWorkspace.Open();var ctx=CompileRequestContext.Create(await f.Snapshot(),1,4096,Format,Adapter).Context!;var input=Input(ctx);var hostile=new NoWalk();
        var tooBig=input with{SourceMapGeometryPayload=new byte[131073],Diagnostics=hostile,LogText="\ud800",ClaimedHash=new(new string('0',64))};
        Assert.Equal(CompileOutputIssue.OverSourceMapGeometryBudget,CompileOutputEnvelope.Create(tooBig,Budget).Failure);Assert.Equal(0,hostile.Walks);
        var first=CompileOutputEnvelope.Create(input,Budget).Envelope!;var exact=Budget with{MaxSourceMapGeometryBytes=input.SourceMapGeometryPayload!.Length,MaxMetadataBytes=(int)first.MetadataByteLength};
        Assert.NotNull(CompileOutputEnvelope.Create(input,exact).Envelope);
        Assert.Equal(CompileOutputIssue.OverSourceMapGeometryBudget,CompileOutputEnvelope.Create(input,exact with{MaxSourceMapGeometryBytes=exact.MaxSourceMapGeometryBytes-1}).Failure);
        Assert.Equal(CompileOutputIssue.OverMetadataBudget,CompileOutputEnvelope.Create(input,exact with{MaxMetadataBytes=exact.MaxMetadataBytes-1}).Failure);
        foreach(var cap in new[]{0,-1,131073})Assert.Equal(CompileOutputIssue.InvalidBudget,CompileOutputEnvelope.Create(input,Budget with{MaxSourceMapGeometryBytes=cap}).Failure);
    }
    [Fact]
    public void V1_and_V2_fixed_golden_domains_and_metadata_lengths_are_independently_encoded()
    {
        var golden=JsonNode.Parse(CodecTests.Fixture("goldens.json"))!;
        var sid=new CompilationSnapshotIdentity(Guid.Parse("00000000-0000-0000-0000-000000000001"),Guid.Parse("00000000-0000-0000-0000-000000000002"),new(Guid.Parse("00000000-0000-0000-0000-000000000003")),Guid.Parse("00000000-0000-0000-0000-000000000004"),0,null,0,1);
        byte[] pdf=[1,2,3],map=[4,5,6];var geometry=CodecTests.Fixture("canonical-wrapper.json");
        var input=new CompileOutputEnvelopeInput{RequestId=CompileRequestId.New(),SnapshotIdentity=sid,InputContentDigest=new(new string('a',64)),WorkerEpoch=1,Status=CompileOutputStatus.Succeeded,Payload=pdf,ClaimedLength=3,ClaimedHash=Digest(pdf),SourceMapPayload=map,SourceMapping=new(Digest(map),3)};
        var v1=CompileOutputEnvelope.Create(input,Budget).Envelope!;var v2=CompileOutputEnvelope.Create(input with{SourceMapGeometryPayload=geometry,SourceMapGeometry=new(Digest(geometry),geometry.Length)},Budget).Envelope!;
        Assert.Equal(golden["v1ArtifactDigest"]!.GetValue<string>(),v1.ContentIdentity!.Digest.Hex);Assert.Equal(golden["v2ArtifactDigest"]!.GetValue<string>(),v2.ContentIdentity!.Digest.Hex);
        Assert.Equal(golden["v1MetadataBytes"]!.GetValue<long>(),v1.MetadataByteLength);Assert.Equal(golden["v2MetadataBytes"]!.GetValue<long>(),v2.MetadataByteLength);
        Assert.Equal(1,v1.ContentIdentity.ArtifactVersion);Assert.Null(v1.ContentIdentity.SourceMapGeometry);Assert.Equal(2,v2.ContentIdentity.ArtifactVersion);
        var json=JsonNode.Parse(geometry)!;json["pages"]![0]!["writerToPdfRaw"]![4]=1;var changed=Bytes(json.ToJsonString());
        var other=CompileOutputEnvelope.Create(input with{SourceMapGeometryPayload=changed,SourceMapGeometry=new(Digest(changed),changed.Length)},Budget).Envelope!;
        Assert.NotEqual(v2.ContentIdentity.Digest,other.ContentIdentity!.Digest);
        // Existing v1 no-option golden remains unchanged as well.
        var bare=CompileOutputEnvelope.Create(input with{SourceMapPayload=null,SourceMapping=null},Budget).Envelope!;
        Assert.Equal("0f248f9ca06d75aee548a66f280b5683f01112b4e9b4d3b964c0d2603d138edf",bare.ContentIdentity!.Digest.Hex);
    }
    [Fact]
    public void Original_positional_shapes_and_enum_ordinals_are_not_recycled()
    {
        var map=new CompileSourceMappingIdentity(new(new string('a',64)),9);var(hash,length)=map;Assert.Equal(9,length);Assert.Equal(map.Hash,hash);
        var budget=new CompileOutputBudget(1,2,3,4,5,6);var(a,b,c,d,e,f)=budget;Assert.Equal((1,2,3,4,5,6),(a,b,c,d,e,f));
        Assert.Equal(22,(int)CompileOutputIssue.SourceMapHashMismatch);Assert.Equal(23,(int)CompileOutputIssue.OverSourceMapGeometryBudget);
        Assert.Equal(24,(int)CompileOutputIssue.SourceMapGeometryMismatch);Assert.Equal(25,(int)CompileOutputIssue.InvalidSourceMapGeometry);
    }
    private sealed class NoWalk:IReadOnlyList<CompileDiagnosticEntry>
    {
        internal int Walks;public int Count=>1;public CompileDiagnosticEntry this[int i]{get{Walks++;throw new InvalidOperationException("Must reject before walk");}}
        public IEnumerator<CompileDiagnosticEntry> GetEnumerator(){Walks++;throw new InvalidOperationException("Must reject before walk");}IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    }
}
