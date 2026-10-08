using Client.Workspace.Domain;
using Client.Workspace.Patching;
using Client.Workspace.Transfer;
using Xunit;

namespace Workspace.Tests;

public class TextPatchPreparationTests
{
    internal static byte[] B(string s) => System.Text.Encoding.UTF8.GetBytes(s);
    internal static SHA256 Hash(byte[] b) => SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(b));
    private static readonly FileId Id = FileId.New();
    private static readonly TextPatchIdentity Identity = new(ProjectId.New(), Guid.NewGuid(), 0, RevisionId.New());
    private static PatchSourceInput Source(byte[] b) => new(Id, "main.tex", "text", b.Length, TextContent.HasBom(b), b);
    private static FilePatchInput Patch(byte[] b, params TextEditInput[] edits) => new(Id, "main.tex", Hash(b), b.Length, edits);
    private static TextEditInput Edit(long a, long b, string before, string after) => new(a,b,B(before),B(after));
    private static TextPatchPreparation Prepare(byte[] b, params TextEditInput[] edits) => PreparedTextBatch.Prepare(Identity,[Patch(b,edits)],[Source(b)]);

    [Fact]
    public void Descending_edits_preserve_every_unrelated_byte_and_own_all_inputs()
    {
        byte[] source = [239,187,191,.. B("Привіт🙂\r\nabc\rdef\n")];
        int pos = source.AsSpan().IndexOf(B("abc"));
        var expected = B("b"); var replacement = B("Ж🙂");
        var edits = new List<TextEditInput> { new(pos+1,pos+2,expected,replacement), Edit(source.Length,source.Length,"","tail") };
        var patches = new List<FilePatchInput> { new(Id,"main.tex",Hash(source),source.Length,edits) };
        var sources = new List<PatchSourceInput> { Source(source) };
        var prep = PreparedTextBatch.Prepare(Identity,patches,sources,TextPatchOrigin.Rule,"Review only");
        var batch = Assert.IsType<PreparedTextBatch>(prep.Batch); Assert.Null(prep.Failure);
        var file=Assert.Single(batch.Files);
        byte[] wanted = [239,187,191,.. B("Привіт🙂\r\naЖ🙂c\rdef\ntail")];
        Assert.Equal(wanted,file.After.ToArray()); Assert.Equal("Привіт🙂\r\naЖ🙂c\rdef\ntail",file.After.Content);
        var original=source.ToArray(); Array.Fill(source,(byte)0); Array.Fill(expected,(byte)0); Array.Fill(replacement,(byte)0);
        edits.Clear(); patches.Clear(); sources.Clear();
        var copy=file.After.ToArray(); Array.Fill(copy,(byte)0);
        var editCopy=file.Edits[0].Replacement.ToArray(); Array.Fill(editCopy,(byte)0);
        Assert.Equal(original,file.Before.ToArray()); Assert.Equal(wanted,file.After.ToArray());
        Assert.Equal(B("b"),file.Edits[0].Expected.ToArray()); Assert.Equal(B("Ж🙂"),file.Edits[0].Replacement.ToArray());
        Assert.Empty(typeof(PreparedTextBatch).GetConstructors()); Assert.Empty(typeof(ExactTextBytes).GetConstructors());
    }

    [Theory]
    [InlineData(0,1,"a","X","Xbc")]
    [InlineData(0,0,"","X","Xabc")]
    [InlineData(3,3,"","X","abcX")]
    [InlineData(0,3,"abc","","")]
    public void Single_replacement_insertion_deletion(long start,long end,string old,string replacement,string wanted)
    {
        var p=Prepare(B("abc"),Edit(start,end,old,replacement));
        Assert.Null(p.Failure); Assert.Equal(B(wanted),p.Batch!.Files[0].After.ToArray());
    }

    [Fact]
    public void Adjacent_ranges_and_insertion_at_previous_end_are_unambiguous()
    {
        var p=Prepare(B("abc"),Edit(0,1,"a","X"),Edit(1,2,"b","Y"),Edit(2,2,"","!"));
        Assert.Equal(B("XY!c"),p.Batch!.Files[0].After.ToArray());
    }

    [Theory]
    [InlineData("overlap")][InlineData("same-start")][InlineData("two-inserts")][InlineData("insert-inside")]
    public void Rejects_overlaps_and_ambiguous_starts(string kind)
    {
        var second = kind switch { "overlap"=>Edit(1,3,"bc","Y"),"same-start"=>Edit(0,0,"","Y"),"two-inserts"=>Edit(0,0,"","Y"),_=>Edit(1,1,"","Y") };
        var first=kind=="two-inserts"?Edit(0,0,"","X"):Edit(0,2,"ab","X");
        var p=Prepare(B("abc"),first,second); Assert.Null(p.Batch); Assert.Equal(TextPatchError.Overlap,p.Failure!.Code);
    }

    [Theory]
    [InlineData("hash")][InlineData("length")][InlineData("id")][InlineData("path")][InlineData("binary")]
    [InlineData("missing")][InlineData("expected")][InlineData("duplicate-file")][InlineData("null-edits")][InlineData("deleted")]
    public void Invalid_preconditions_never_produce_applicable_data(string kind)
    {
        var bytes=B("abc"); var patch=Patch(bytes,Edit(0,1,"a","X")); var source=Source(bytes);
        patch=kind switch {
            "hash"=>patch with{ExpectedHash=new(new string('0',64))},"length"=>patch with{ExpectedLength=4},
            "id"=>patch with{FileId=FileId.New()},"path"=>patch with{RelativePath="other.tex"},
            "expected"=>patch with{Edits=[Edit(0,1,"Z","X")]},"null-edits"=>patch with{Edits=null!},_=>patch };
        source=kind switch{"binary"=>source with{MediaKind="binary"},"missing"=>source with{Bytes=null},"deleted"=>source with{Deleted=true},_=>source};
        var p=PreparedTextBatch.Prepare(Identity,kind=="duplicate-file"?[patch,patch]:[patch],[source]);
        Assert.Null(p.Batch); Assert.NotNull(p.Failure); Assert.Equal(B("abc"),bytes);
    }

    [Theory]
    [InlineData(-1,0)][InlineData(2,1)][InlineData(0,4)][InlineData(long.MaxValue,long.MaxValue)]
    [InlineData(1,2)] // inside first Ukrainian scalar
    public void Invalid_and_non_scalar_byte_boundaries(long a,long b)
    {
        var p=Prepare(B("Жx"),new TextEditInput(a,b,new byte[(int)Math.Clamp(b-a,0,4)],B("Y")));
        Assert.Null(p.Batch); Assert.Equal(TextPatchError.InvalidSpan,p.Failure!.Code);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Strict_invalid_UTF8_is_not_replaced(bool invalidSource)
    {
        byte[] bytes=invalidSource?[255]:B("a");
        var p=Prepare(bytes,new TextEditInput(0,1,bytes,invalidSource?B("x"):new byte[]{255}));
        Assert.Null(p.Batch); Assert.Equal(TextPatchError.InvalidUtf8,p.Failure!.Code);
    }

    [Theory]
    [InlineData(0,0)][InlineData(0,1)][InlineData(1,2)][InlineData(0,3)]
    public void No_edit_may_touch_or_split_a_retained_BOM(long a,long b)
    {
        byte[] bytes=[239,187,191,..B("abc")];
        var p=Prepare(bytes,new TextEditInput(a,b,bytes.AsMemory((int)a,(int)(b-a)),B("X")));
        Assert.Null(p.Batch); Assert.Equal(TextPatchError.BomChange,p.Failure!.Code);
    }

    [Fact]
    public void Introducing_a_BOM_is_not_an_implicit_text_patch()
    {
        var p=Prepare(B("abc"),new TextEditInput(0,0,Array.Empty<byte>(),new byte[]{239,187,191}));
        Assert.Equal(TextPatchError.BomChange,p.Failure!.Code);
    }

    [Theory]
    [InlineData("../escape.tex")][InlineData("/root.tex")][InlineData("a\\b.tex")]
    [InlineData(" x.tex")][InlineData(".git/a.tex")]
    public void Invalid_project_paths_fail_closed(string path)
    {
        var bytes=B("abc"); var source=Source(bytes) with{RelativePath=path};var patch=Patch(bytes,Edit(0,1,"a","X")) with{RelativePath=path};
        Assert.Null(PreparedTextBatch.Prepare(Identity,[patch],[source]).Batch);
    }

    [Fact]
    public void Intact_lone_surrogate_path_is_constructed_inline_not_replaced_by_theory_transport()
    {
        var bytes=B("abc");var path="\ud800/x.tex";
        Assert.Null(PreparedTextBatch.Prepare(Identity,[Patch(bytes,Edit(0,1,"a","X")) with{RelativePath=path}],[Source(bytes) with{RelativePath=path}]).Batch);
    }

    [Theory]
    [InlineData("empty-batch")][InlineData("empty-edits")][InlineData("empty-insert")][InlineData("same-bytes")][InlineData("net-noop")]
    public void No_op_batches_are_explicitly_rejected(string kind)
    {
        var bytes=B("aa"); TextEditInput[] edits=kind switch {
            "empty-edits"=>[],"empty-insert"=>[Edit(0,0,"","")],"same-bytes"=>[Edit(0,1,"a","a")],
            "net-noop"=>[Edit(0,1,"a",""),Edit(2,2,"","a")],_=>[Edit(0,1,"a","X")] };
        var p=PreparedTextBatch.Prepare(Identity,kind=="empty-batch"?[]:[Patch(bytes,edits)],[Source(bytes)]);
        Assert.Null(p.Batch); Assert.Equal(TextPatchError.NoChanges,p.Failure!.Code);
    }

    [Fact]
    public void Output_and_entire_project_limits_include_unchanged_binary_metadata()
    {
        var bytes=B("abc"); var source=Source(bytes);
        var binary=new PatchSourceInput(FileId.New(),"a.bin","binary",6,false);
        var limits=new TextPatchLimits(8,10,2);
        var patch=Patch(bytes,Edit(3,3,"","XX")); // output5 + binary6 > project10
        Assert.Equal(TextPatchError.QuotaExceeded,PreparedTextBatch.Prepare(Identity,[patch],[source,binary],requestedLimits:limits).Failure!.Code);
        Assert.NotNull(PreparedTextBatch.Prepare(Identity,[Patch(bytes,Edit(3,3,"","X"))],[source,binary],requestedLimits:limits).Batch);
        Assert.Null(PreparedTextBatch.Prepare(Identity,[patch],[source],requestedLimits:limits with{FileBytes=4}).Batch);
        Assert.Null(PreparedTextBatch.Prepare(Identity,[patch],[source,binary],requestedLimits:limits with{FileCount=1}).Batch);
        Assert.Null(PreparedTextBatch.Prepare(Identity,[patch],[source,binary with{ByteLength=long.MaxValue}]).Batch);
        Assert.Null(PreparedTextBatch.Prepare(Identity,[patch],[source with{ByteLength=long.MaxValue}]).Batch);
        var bounded=PreparedTextBatch.Prepare(Identity,[patch],[source],requestedLimits:new(long.MaxValue,long.MaxValue,int.MaxValue)).Batch!;
        Assert.Equal(TextPatchLimits.Default,bounded.Limits);
    }

    [Fact]
    public void Actual_8MiB_boundary_and_oversized_replacement_are_bounded_before_output()
    {
        var bytes=new byte[(int)WorkspaceTextLimits.FileBytes]; Array.Fill(bytes,(byte)'a');
        var good=Prepare(bytes,Edit(0,1,"a","b")); Assert.NotNull(good.Batch);
        var tooBig=new byte[bytes.Length+1];
        Assert.Equal(TextPatchError.QuotaExceeded,Prepare(bytes,new TextEditInput(0,1,B("a"),tooBig)).Failure!.Code);
        Assert.Equal(TextPatchError.QuotaExceeded,Prepare(bytes,Edit(bytes.Length,bytes.Length,"","b")).Failure!.Code);
    }

    [Fact]
    public void Invalid_second_file_discards_all_prepared_output_and_never_modifies_input()
    {
        var bytes=B("abc");var second=Source(bytes) with{FileId=FileId.New(),RelativePath="b.tex"};
        var bad=Patch(bytes,Edit(0,1,"a","Y")) with{FileId=second.FileId,RelativePath=second.RelativePath,ExpectedHash=new(new string('0',64))};
        var result=PreparedTextBatch.Prepare(Identity,[Patch(bytes,Edit(0,1,"a","X")),bad],[Source(bytes),second]);
        Assert.Null(result.Batch); Assert.Equal(B("abc"),bytes);
    }
}
