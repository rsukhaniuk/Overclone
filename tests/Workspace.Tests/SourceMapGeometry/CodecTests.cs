using System.Collections;
using System.Text.Json.Nodes;
using Client.Compilation;
using Client.Compilation.SyncTeX;
using Xunit;
using static Workspace.Tests.SourceMapGeometry.GeometryFixture;

namespace Workspace.Tests.SourceMapGeometry;

public sealed class CodecTests
{
    [Theory]
    [InlineData("lualatex",2)][InlineData("latex-dvi",4)]
    public void Genuine_copied_native_shapes_assemble_and_correlate_actual_bytes(string engine,int pages)
    {
        var g=Assemble(engine,uint.MaxValue);var parsed=SourceMapGeometryCodec.ParseWrapper(g.ToArray());Assert.Null(parsed.Error);
        Assert.Equal(engine,g.Geometry.EngineId);Assert.Equal(uint.MaxValue,g.Geometry.FinalPass);Assert.Equal(pages,g.Geometry.Pages.Count);
        Assert.Equal(Digest(Native(engine,"output.pdf")).Hex,g.Geometry.PdfSha256);Assert.Equal(Digest(Native(engine,"source.synctex")).Hex,g.Geometry.MapSha256);
        Assert.Equal(engine=="latex-dvi"?Digest(Native(engine,"intermediate.dvi")).Hex:null,g.Geometry.IntermediateSha256);
        Assert.All(g.Geometry.Pages,p=>Assert.NotNull(p.WriterToPdfRaw));
        var json=JsonNode.Parse(g.ToArray())!;Assert.IsType<JsonArray>(json["pages"]![0]!["writerPageBoxBp"]);
    }
    [Fact]
    public void Canonical_new_wrapper_is_stable_with_independent_byte_golden()
    {
        var side=Fixture("synthetic-sidecar.json");var result=SourceMapGeometryCodec.AssembleData("pdflatex",1,side,[4,5,6],[1,2,3],null,Budget);
        var g=Assert.IsType<SourceMapGeometryArtifact>(result.Artifact);Assert.Equal(Fixture("canonical-wrapper.json"),g.ToArray());
        var again=SourceMapGeometryCodec.AssembleData("pdflatex",1,side,[4,5,6],[1,2,3],null,Budget);
        Assert.Equal(g.Identity,again.Artifact!.Identity);Assert.Equal(g.ToArray(),again.Artifact.ToArray());
    }
    [Fact]
    public void Owned_pages_matrix_values_and_byte_getters_do_not_alias_caller_arrays_or_ROM()
    {
        var caller=Assemble().ToArray();var original=caller.ToArray();var parsed=SourceMapGeometryCodec.ParseWrapper(caller).Artifact!;
        caller[0]=0;var copy=parsed.ToArray();copy[1]=0;Assert.Equal(original,parsed.ToArray());Assert.Equal(Digest(original),parsed.Identity.Hash);
        var pages=parsed.Geometry.Pages;var cleared=pages.Clear();Assert.Empty(cleared);Assert.Equal(2,parsed.Geometry.Pages.Count);
        var changed=pages[0].WriterToPdfRaw!.Value with{A=999};Assert.NotEqual(changed,parsed.Geometry.Pages[0].WriterToPdfRaw!.Value);
        Assert.DoesNotContain(typeof(SourceMapGeometryArtifact).GetProperties(),p=>p.PropertyType==typeof(byte[])||p.PropertyType==typeof(ReadOnlyMemory<byte>));
        Assert.DoesNotContain(typeof(SourceMapPageTransform).GetProperties(),p=>p.PropertyType==typeof(double[])||p.PropertyType==typeof(ReadOnlyMemory<double>));
    }
    [Fact]
    public void Unavailable_native_facts_have_no_guessed_matrix_or_box_and_rotation_data_is_not_rendering_proof()
    {
        foreach(var status in new[]{"unsupported","indeterminate"})
        {
            var source=Bytes("{\"schemaVersion\":1,\"pages\":[{\"page\":1,\"status\":\""+status+"\",\"reason\":\"writer-origin-unavailable\",\"writerToPdfRaw\":null,\"writerPageBoxBp\":null}]}");
            var p=SourceMapGeometryCodec.ParseNativeSidecar(source);Assert.Null(p.Error);Assert.Null(p.Sidecar!.Pages[0].WriterToPdfRaw);Assert.Null(p.Sidecar.Pages[0].WriterPageBoxBp);
        }
        var vectors=JsonNode.Parse(Fixture("matrix-vectors.json"))!["vectors"]!.AsArray();
        foreach(var v in vectors)
        {
            var p=SourceMapGeometryCodec.ParseNativeSidecar(Bytes(v!["sidecar"]!.ToJsonString()));Assert.Null(p.Error);
            var m=p.Sidecar!.Pages[0].WriterToPdfRaw!.Value;Assert.True(m.TryInverse(out var inv));
            // Independent algebra checks include signs, offsets, scales and all
            // quarter turns as DATA. No crop/rotation display oracle is claimed.
            double h=13.25,k=-7.5,x=m.A*h+m.C*k+m.E,y=m.B*h+m.D*k+m.F;
            Assert.InRange(Math.Abs(inv.A*x+inv.C*y+inv.E-h),0,1e-9);Assert.InRange(Math.Abs(inv.B*x+inv.D*y+inv.F-k),0,1e-9);
        }
    }
    [Theory]
    [InlineData("unknown")][InlineData("duplicatePage")][InlineData("gap")][InlineData("zeroPages")][InlineData("tooMany")]
    [InlineData("pageFraction")][InlineData("pageCase")][InlineData("version")][InlineData("nullVersion")]
    [InlineData("status")][InlineData("reason")][InlineData("availableReason")][InlineData("unavailableMatrix")]
    [InlineData("matrixShort")][InlineData("matrixNull")][InlineData("matrixString")][InlineData("matrixOverflow")]
    [InlineData("matrixHuge")][InlineData("singular")][InlineData("inverseOverflow")][InlineData("boxObject")][InlineData("boxOrder")][InlineData("boxZero")]
    public void Bad_native_sidecars_are_never_returned_as_partial_success(string fault)
    {
        var r=JsonNode.Parse(Native("lualatex","geometry.json"))!;var p=r["pages"]![0]!;
        switch(fault){
            case "unknown":p["crop"]="not-in-protocol";break;
            case "duplicatePage":r["pages"]![1]!["page"]=1;break;
            case "gap":r["pages"]![1]!["page"]=3;break;
            case "zeroPages":r["pages"]=new JsonArray();break;
            case "tooMany":while(r["pages"]!.AsArray().Count<=512)r["pages"]!.AsArray().Add(p.DeepClone());break;
            case "pageFraction":p["page"]=1.5;break;
            case "pageCase":p.AsObject().Remove("page");p["Page"]=1;break;
            case "version":r["schemaVersion"]=2;break;
            case "nullVersion":r["schemaVersion"]=null;break;
            case "status":p["status"]="Available";break;
            case "reason":p["reason"]="unknown";break;
            case "availableReason":p["reason"]="noninvertible-transform";break;
            case "unavailableMatrix":p["status"]="unsupported";p["reason"]="writer-origin-unavailable";break;
            case "matrixShort":p["writerToPdfRaw"]!.AsArray().RemoveAt(5);break;
            case "matrixNull":p["writerToPdfRaw"]=null;break;
            case "matrixString":p["writerToPdfRaw"]![0]="NaN";break;
            case "matrixOverflow":p["writerToPdfRaw"]![0]=JsonNode.Parse("1e999");break;
            case "matrixHuge":p["writerToPdfRaw"]![0]=1000001;break;
            case "singular":p["writerToPdfRaw"]=new JsonArray(1,2,2,4,0,0);break;
            case "inverseOverflow":p["writerToPdfRaw"]=new JsonArray(JsonNode.Parse("1e-320"),0,0,1,0,0);break;
            case "boxObject":p["writerPageBoxBp"]=new JsonObject{["left"]=0,["bottom"]=0,["right"]=3,["top"]=2};break;
            case "boxOrder":p["writerPageBoxBp"]![2]=-1;break;
            case "boxZero":p["writerPageBoxBp"]![2]=p["writerPageBoxBp"]![0]!.DeepClone();break;
        }
        var parsed=SourceMapGeometryCodec.ParseNativeSidecar(Bytes(r.ToJsonString()));Assert.NotNull(parsed.Error);Assert.Null(parsed.Sidecar);
    }
    [Theory]
    [InlineData("duplicate")][InlineData("bom")][InlineData("utf8")][InlineData("surrogate")][InlineData("trailing")]
    public void Invalid_encoding_duplicate_fields_and_multiple_documents_are_rejected(string mode)
    {
        var data=Native("lualatex","geometry.json");
        data=mode switch{
            "duplicate"=>Bytes(System.Text.Encoding.UTF8.GetString(data).Replace("\"schemaVersion\":1","\"schemaVersion\":1,\"schemaVersion\":1")),
            "bom"=>[239,187,191,..data],"utf8"=>[255,..data],
            "surrogate"=>Bytes(System.Text.Encoding.UTF8.GetString(data).Replace("\"available\"","\"\\ud800\"")),_=>[..data,..data],
        };
        Assert.Null(SourceMapGeometryCodec.ParseNativeSidecar(data).Sidecar);
    }
    [Theory]
    [InlineData("engine")][InlineData("kind")][InlineData("directHash")][InlineData("missingHash")][InlineData("mapHash")][InlineData("pdfHash")]
    [InlineData("passZero")][InlineData("passNegative")][InlineData("passOverflow")][InlineData("passFraction")][InlineData("wrapperExtra")]
    public void Wrapper_relationships_and_exact_hash_pass_fields_are_closed(string fault)
    {
        var r=JsonNode.Parse(Assemble().ToArray())!;
        switch(fault){
            case "engine":r["engineId"]="luaLaTeX";break;
            case "kind":r["intermediateKind"]="dvi";break;
            case "directHash":r["intermediateSha256"]=new string('a',64);break;
            case "missingHash":r["engineId"]="xelatex";r["intermediateKind"]="xdv";break;
            case "mapHash":r["mapSha256"]="abc";break;
            case "pdfHash":r["pdfSha256"]=r["pdfSha256"]!.GetValue<string>().ToUpperInvariant();break;
            case "passZero":r["finalPass"]=0;break;
            case "passNegative":r["finalPass"]=-1;break;
            case "passOverflow":r["finalPass"]=4294967296L;break;
            case "passFraction":r["finalPass"]=1.1;break;
            case "wrapperExtra":r["success"]=true;break;
        }
        Assert.Null(SourceMapGeometryCodec.ParseWrapper(Bytes(r.ToJsonString())).Artifact);
    }
    [Fact]
    public void Native_and_wrapper_caps_are_independent_exact_limits_and_do_not_create_pdf_validity()
    {
        var side=Native("lualatex","geometry.json");var padded=side.Concat(Enumerable.Repeat((byte)' ',131072-side.Length)).ToArray();
        Assert.NotNull(SourceMapGeometryCodec.ParseNativeSidecar(padded).Sidecar);
        Assert.Equal(SourceMapGeometryError.OverBudget,SourceMapGeometryCodec.ParseNativeSidecar([..padded,32]).Error);
        var wrapper=Assemble().ToArray();var full=wrapper.Concat(Enumerable.Repeat((byte)' ',131072-wrapper.Length)).ToArray();
        Assert.NotNull(SourceMapGeometryCodec.ParseWrapper(full).Artifact);Assert.Equal(SourceMapGeometryError.OverBudget,SourceMapGeometryCodec.ParseWrapper([..full,32]).Error);
        Assert.Equal(SourceMapGeometryError.OverBudget,SourceMapGeometryCodec.AssembleData("lualatex",1,side,[1],[2],null,Budget with{MaxSourceMapGeometryBytes=1}).Error);
        Assert.Null(SourceMapGeometryCodec.AssembleData("lualatex",1,side,[1],[2],null,Budget).Error); // opaque bytes, not valid map/PDF
        Assert.NotNull(SourceMapGeometryCodec.AssembleData("lualatex",1,side,[1],[2],[],Budget).Error);
    }
    internal static byte[] Fixture(string file)
    {
        using var stream=typeof(CodecTests).Assembly.GetManifestResourceStream("Workspace.Tests.Fixtures.SourceMapGeometry."+file)??throw new InvalidOperationException("Required synthetic geometry fixture missing.");
        using var bytes=new MemoryStream();stream.CopyTo(bytes);return bytes.ToArray();
    }
}
