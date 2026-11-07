using System.Text;
using System.Xml.Linq;
using Client.Compilation.Native;
using Workspace.Tests.CompilationLifecycle;
using Xunit;

namespace Workspace.Tests.CompilerSuite;

// Synthetic admission/fingerprint tests. Genuine native BCF -> BBL evidence is separate.
public sealed class BiberControlTests
{
    private static NativeSelectedEntry Entry => NativeSelectedEntryContract.Create("main.tex", ["main.tex"], out _)!;
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static string Source(string name, string type = "file", string glob = "false") =>
        new XElement(XName.Get("datasource", NativeBiberControlContract.Namespace),
            new XAttribute("type",type),new XAttribute("datatype","bibtex"),new XAttribute("glob",glob),name).ToString(SaveOptions.DisableFormatting);
    private static byte[] Control(params string[] records) => Bytes(
        $"<controlfile xmlns=\"{NativeBiberControlContract.Namespace}\" version=\"3.10\" bltxversion=\"3.19\">{string.Concat(records)}</controlfile>");
    private static Dictionary<string, byte[]> State(byte[] bcf) => new() { ["main.bcf"] = bcf };

    [Fact] public void Current_native_control_admits_only_exact_captured_sources_and_reads_adjacent_records()
    {
        var bcf=Control(Source("розділ 😀/джерела.bib"),Source("a.bib"),Source("a.bib"));
        var result=NativeBiberControlContract.Read(Entry,State(bcf),["main.tex","a.bib","розділ 😀/джерела.bib"],out var issue);
        Assert.Null(issue);Assert.NotNull(result);Assert.Equal("main.bcf",result.BcfName);
        Assert.Equal(new[]{"a.bib","розділ 😀/джерела.bib"},result.Sources.ToArray());
    }
    [Theory]
    [InlineData("https://example.org/refs.bib")][InlineData("file:///work/runtime/perl/bin/biber")]
    [InlineData("../refs.bib")][InlineData("/refs.bib")][InlineData("a/../refs.bib")]
    [InlineData("*.bib")][InlineData("x?.bib")][InlineData("main.bcf")][InlineData("main.run.xml")]
    [InlineData(" refs.bib")][InlineData("refs.bib ")][InlineData("a\\refs.bib")]
    public void Unsafe_datasources_are_refused_even_if_the_capture_claims_the_same_name(string name)
    {
        Assert.Null(NativeBiberControlContract.Read(Entry,State(Control(Source(name))),[name],out var issue));
        Assert.Equal("unsupported-biber-datasource",issue);
    }
    [Fact] public void Url_glob_foreign_namespace_and_missing_capture_never_gain_file_authority()
    {
        foreach(var record in new[]{Source("refs.bib","url"),Source("refs.bib",glob:"true"),"<datasource xmlns=\"other\" type=\"file\">refs.bib</datasource>"})
        {
            Assert.Null(NativeBiberControlContract.Read(Entry,State(Control(record)),["refs.bib"],out var issue));
            Assert.Equal("unsupported-biber-datasource",issue);
        }
        Assert.Null(NativeBiberControlContract.Read(Entry,State(Control(Source("refs.bib"))),["REFS.BIB"],out var missing));
        Assert.Equal("missing-biber-datasource",missing);
    }
    [Fact] public void Wrong_version_malformed_utf8_xml_entities_and_nested_source_markup_are_refused()
    {
        var good=Control(Source("refs.bib"));
        Assert.Null(NativeBiberControlContract.Read(Entry,State(Bytes(Encoding.UTF8.GetString(good).Replace("version=\"3.10\"","version=\"3.11\""))),["refs.bib"],out var version));
        Assert.Equal("incompatible-biber-control",version);
        foreach(var bytes in new[]{new byte[]{0xc0,0xaf},Bytes("<controlfile>"),
            Bytes("<!DOCTYPE controlfile [<!ENTITY data SYSTEM 'file:///private'>]>"+Encoding.UTF8.GetString(good)),
            Control("<datasource type=\"file\"><nested>refs.bib</nested></datasource>")})
        {
            Assert.Null(NativeBiberControlContract.Read(Entry,State(bytes),["refs.bib"],out var issue));
            Assert.NotNull(issue);
        }
    }
    [Fact] public void Missing_empty_oversize_deep_and_source_count_bounds_are_explicit()
    {
        Assert.Null(NativeBiberControlContract.Read(Entry,new Dictionary<string,byte[]>(),[],out var missing));Assert.Equal("missing-native-bcf",missing);
        Assert.Null(NativeBiberControlContract.Read(Entry,State([]),[],out var empty));Assert.Equal("missing-native-bcf",empty);
        Assert.Null(NativeBiberControlContract.Read(Entry,State(new byte[8388609]),[],out var large));Assert.Equal("biber-input-quota",large);
        var names=Enumerable.Range(0,64).Select(i=>$"refs{i}.bib").ToArray();
        Assert.NotNull(NativeBiberControlContract.Read(Entry,State(Control(names.Take(63).Select(n=>Source(n)).ToArray())),names,out _));
        Assert.Null(NativeBiberControlContract.Read(Entry,State(Control(names.Select(n=>Source(n)).ToArray())),names,out var count));Assert.Equal("biber-input-quota",count);
        var deep=Control(string.Concat(Enumerable.Repeat("<x>",65))+Source("refs.bib")+string.Concat(Enumerable.Repeat("</x>",65)));
        Assert.Null(NativeBiberControlContract.Read(Entry,State(deep),["refs.bib"],out var depth));Assert.Equal("biber-input-quota",depth);
    }
    [Fact] public void Fingerprint_binds_control_referenced_source_tool_and_runtime_but_not_backend_outputs() => LifecycleTestHost.Run(async () =>
    {
        await using var f=await CompilationTestWorkspace.Open();
        Assert.True(f.Workspace.RenameFile(f.Main,"main.tex"));Assert.True(f.Workspace.CreateFile("refs.bib","original"));
        Assert.True(f.Workspace.CreateFile("unused.bib","unrelated"));
        var state=State(Control(Source("refs.bib")));var snapshot=await f.Snapshot();
        var control=NativeBiberControlContract.Read(Entry,state,["refs.bib"],out _)!;
        var tool=CompilationTestWorkspace.Descriptor("biber","2.19");
        string Fingerprint() => NativeBiberControlContract.Fingerprint(Entry,control,state,snapshot,new string('a',64),tool);
        var original=Fingerprint();state["main.bbl"]=Bytes("new bibliography");state["main.blg"]=Bytes("new log");
        state["main.aux"]=Bytes("new aux");state["main.run.xml"]=Bytes("new run instructions");Assert.Equal(original,Fingerprint());
        var unused=f.Workspace.Files.Single(x=>x.RelativePath=="unused.bib");Assert.True(f.Workspace.EditText(unused.Id,"changed unused"));
        snapshot=await f.Snapshot();Assert.Equal(original,Fingerprint());
        var source=f.Workspace.Files.Single(x=>x.RelativePath=="refs.bib");Assert.True(f.Workspace.EditText(source.Id,"changed source"));
        snapshot=await f.Snapshot();Assert.NotEqual(original,Fingerprint());
        original=Fingerprint();tool=tool with {Version="different"};Assert.NotEqual(original,Fingerprint());
        original=Fingerprint();state["main.bcf"]=Control(Source("refs.bib"),Source("refs.bib"));Assert.NotEqual(original,Fingerprint());
        Assert.NotEqual(Fingerprint(),NativeBiberControlContract.Fingerprint(Entry,control,state,snapshot,new string('b',64),tool));
    });
}
