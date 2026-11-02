using System.Collections;
using System.Text;
using System.Text.Json;
using Client.Compilation;
using Client.Compilation.Lifecycle;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Client.Compilation.SyncTeX;
using Workspace.Tests.CompilationLifecycle;
using Workspace.Tests.CompilerSuite;
using Xunit;
using Xunit.Abstractions;

namespace Workspace.Tests.NavigationXeTransport;

// Complete real P9 package; all phase replies below are MODELS, not native/PDF proof.
public sealed class XeTransportTests(ITestOutputHelper output)
{
    private const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalCompilerXeNavigation;
    private static readonly CompileOutputBudget Budget = new(8388608,65536,32,128,4096,8192);
    internal static string? Root => Environment.GetEnvironmentVariable("XE_NAVIGATION_RUNTIME_DIR");
    private static readonly Lazy<NativeRuntimePackage> Fixture = new(() =>
    {
        var pins = Pins(); var inputs = pins.Select(p => new NativeResourceInput(p.Name,File.ReadAllBytes(Path.Combine(Root!,"files",p.Name)))).ToArray();
        return Assert.IsType<NativeRuntimePackage>(NativeRuntimePackage.Create(inputs,pins,LocalRuntimeSelection.LimitsFor(Profile),Profile).Package);
    });
    private static NativeRuntimePackage Package => Fixture.Value;
    private static NativeResourcePin[] Pins() => LocalRuntimeSelection.ParsePins(File.ReadAllBytes(Path.Combine(Root!,"pins.json"))).ToArray();
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
    private static readonly byte[] Sidecar = Bytes("{\"schemaVersion\":1,\"pages\":[{\"page\":1,\"status\":\"available\",\"reason\":\"none\",\"writerToPdfRaw\":[1,0,0,-1,3,800],\"writerPageBoxBp\":[0,0,600,800]}]}");

    [Fact]
    public void Closed_P9_matrix_preserves_P8_and_legacy_overloads_and_unknown10()
    {
        Assert.Equal(9,(int)Profile);
        Assert.Equal(new[]{6,7,8,9},Enum.GetValues<NativeCompilerEngine>().Select(NativeCompilerSuiteContract.Phase));
        Assert.Equal(new[]{6,12,8,14},Enum.GetValues<NativeCompilerEngine>().Select(e => NativeCompilerSuiteContract.Phase(NativeNavigationContract.Profile,e)));
        Assert.Equal(new[]{6,12,13,14},Enum.GetValues<NativeCompilerEngine>().Select(e => NativeCompilerSuiteContract.Phase(Profile,e)));
        foreach(var engine in Enum.GetValues<NativeCompilerEngine>())
        {
            var plan=NativeCompilerSuiteContract.CreatePlan(engine,profile:Profile); Assert.True(plan.IsValid);
            foreach(int phase in Enumerable.Range(1,16).Where(p => p!=plan.EffectivePhaseVersion))
                Assert.False((plan with {PhaseProtocolVersion=phase}).IsValid);
            Assert.False((plan with {Profile=(NativeRuntimeProfile)10}).IsValid);
            Assert.True(NativeCompilerSuiteContract.MatchesOptions(NativeCompilerSuiteContract.SelectionOptions(Profile,engine),Profile,engine));
        }
        foreach(var p in new[]{(NativeRuntimeProfile)10,(NativeRuntimeProfile)int.MaxValue})
        {
            Assert.False(NativeRuntimeProfiles.Known(p)); Assert.False(NativeRuntimeProfiles.IsSuite(p));
            Assert.Equal(0,NativeCompilerSuiteContract.Phase(p,NativeCompilerEngine.XeLaTeX));
        }
        var xe=NativeCompilerSuiteContract.SelectionOptions(Profile,NativeCompilerEngine.XeLaTeX);
        Assert.False(NativeCompilerSuiteContract.MatchesOptions(xe));
        Assert.False(NativeCompilerSuiteContract.MatchesOptions(xe,NativeNavigationContract.Profile));
        Assert.True(NativeCompilerSuiteContract.MatchesOptions(xe,Profile));
        Assert.False((NativeCompilerSuiteContract.CreatePlan(NativeCompilerEngine.XeLaTeX,profile:NativeNavigationContract.Profile) with {PhaseProtocolVersion=13}).IsValid);
        Assert.Equal(TimeSpan.FromSeconds(90),NativeCompilerSuiteContract.CreatePlan(NativeCompilerEngine.XeLaTeX,profile:Profile).JobDeadline);
        Assert.Equal(TimeSpan.FromMinutes(2),NativeCompilerSuiteContract.LifecycleDeadline);
        Assert.Equal(1048575,NativeSyncTeXContract.MaxMapBytes);Assert.Equal(131072,SourceMapGeometryCodec.MaxBytes);Assert.Equal(512,SourceMapGeometryCodec.MaxPages);
        Assert.Null(NativeCompilerSuiteContract.CreateEntry("𐐀/main.tex",["𐐀/main.tex"],NativeCompilerEngine.XeLaTeX,out var unsupported));
        Assert.Equal("xetex-non-bmp-main-unsupported",unsupported);
    }

    [Fact]
    public void Distinct_genuine_Xe_format_and_ABI3_converter_do_not_relabel_P8_or_Lua_DVI()
    {
        var xe=NativeXeNavigationContract.Component(NativeCompilerEngine.XeLaTeX);
        Assert.Equal(1,xe.NativeAbiVersion); Assert.True(xe.SupportsSourceMap); Assert.Equal("maps-abi1",xe.EngineVersion);
        Assert.Equal(NativeCompilerXeNavigationPins.WasmHash,xe.WasmHash); Assert.Equal(NativeCompilerXeNavigationPins.FormatHash,xe.FormatHash);
        Assert.Equal("geometry-abi3",xe.OutputAdapter.Version); Assert.Equal(NativeCompilerXeNavigationPins.ConverterWasmHash,xe.OutputAdapter.Hash!.Value.Hex);
        Assert.Equal(xe,NativeCompilerComponents.For(Profile,NativeCompilerEngine.XeLaTeX)); // required minimal master delegation
        var old=NativeCompilerComponents.For(NativeNavigationContract.Profile,NativeCompilerEngine.XeLaTeX);
        Assert.False(old.SupportsSourceMap); Assert.Equal(NativeCompilerSuitePins.XeWasmHash,old.WasmHash);
        Assert.NotEqual(old.FormatHash,xe.FormatHash); Assert.NotEqual(old.OutputAdapter,xe.OutputAdapter);
        foreach(var engine in new[]{NativeCompilerEngine.PdfLaTeX,NativeCompilerEngine.LaTeXDvi,NativeCompilerEngine.LuaLaTeX})
            Assert.Equal(NativeNavigationContract.Component(engine),NativeXeNavigationContract.Component(engine));
        Assert.Equal("local-compiler-navigation-v1",NativeRuntimeProfiles.IdentityName(NativeNavigationContract.Profile));
        Assert.Equal(NativeCompilerNavigationPins.ClosureHash,NativeRuntimeProfiles.ClosureHash(NativeNavigationContract.Profile));
        Assert.Equal(3707,NativeRuntimeProfiles.InstalledCount(NativeNavigationContract.Profile));
        Assert.Equal("local-compiler-xe-navigation-v1",NativeRuntimeProfiles.IdentityName(Profile));
    }

    [XeFixtureFact]
    public void Full_closure_and_new_format_bind_before_untrusted_input_access()
    {
        var pins=Pins(); Assert.Equal(3709,pins.Length); Assert.Equal(192565170L,pins.Sum(p => (long)p.Length));
        Assert.Equal(Profile,LocalRuntimeSelection.ProfileFor(pins));
        Assert.True(NativeCompilerXeNavigationPins.MatchesComponents(pins.ToDictionary(p => p.Name,StringComparer.Ordinal)));
        var changed=pins.Select(p => p.Name=="xelatex.fmt" ? p with {Sha256=NativeCompilerCompatibilityPins.XeFormatHash} : p).ToArray();
        Assert.Throws<FormatException>(() => LocalRuntimeSelection.ProfileFor(changed));
        Assert.Equal(NativeRuntimeIssue.TechnicalHashMismatch,NativeRuntimePackage.Create(new NoInputs(pins.Length),changed,LocalRuntimeSelection.LimitsFor(Profile),Profile).Issue);
        var bytes=Package.Copy("xelatex.fmt"); bytes.AsSpan().Fill(0);
        Assert.Equal(NativeCompilerXeNavigationPins.FormatHash,NativeRuntimePackage.Digest(Package.Copy("xelatex.fmt")).Hex);
        Assert.Equal("maps-abi1",Package.EngineFor(NativeCompilerEngine.XeLaTeX).Version);
        Assert.Equal(NativeCompilerNavigationPins.ConverterWasmHash,Package.DviOutputAdapter.Hash!.Value.Hex);
    }
    private sealed class NoInputs(int count):IReadOnlyList<NativeResourceInput>
    {
        public int Count=>count; public NativeResourceInput this[int i]=>throw new Exception("Untrusted input inspected before component/closure check");
        public IEnumerator<NativeResourceInput> GetEnumerator()=>throw new NotSupportedException(); IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    }
    private static void Prepare(CompilationTestWorkspace f,bool bib=false)
    {
        Assert.True(f.Workspace.RenameFile(f.Main,"тези/my thesis.tex"));
        Assert.True(f.Workspace.CreateFile("розділ/part one.tex","Included synthetic source."));
        Assert.True(f.Workspace.CreateFile("стилі/local.sty","% synthetic project style\n"));
        if(bib)Assert.True(f.Workspace.CreateFile("references.bib","@book{entry,title={Model},author={Synthetic Author},publisher={Synthetic Press},year={2025}}"));
    }
    private static async Task<CompileRequestContext> Context(CompilationTestWorkspace f,bool bib=false,long epoch=1)
    {
        f.Selection.SetSelection(CompilationSelection.Create(new(f.Main,Package.EngineFor(NativeCompilerEngine.XeLaTeX),[Package.Bundle],
            bib ? [Package.Bibtex] : [],NativeCompilerSuiteContract.SelectionOptions(Profile,NativeCompilerEngine.XeLaTeX)),65536).Selection!);
        return CompileRequestContext.Create(await f.Snapshot(new(64,8388608,16777216,131072)),epoch,65536,NativeRuntimePackage.Pdf,Package.AdapterFor(NativeCompilerEngine.XeLaTeX)).Context!;
    }
    private sealed class Model(bool bib=false):INativePhaseTransport
    {
        public bool Bib {get;}=bib;
        public bool ChangeAuxAfterBbl {get;init;}
        private byte[] Aux(string job,bool hasBbl)=>Bytes(Bib?BibliographyPipelineFixture.Aux(job,biblatex:false)
            +(ChangeAuxAfterBbl&&hasBbl?"\\bibcite{entry}{1}\n":""):"\\relax\n");
        public List<NativePhaseTag> Tags {get;}=[];
        public List<string> LoaderHashes {get;}=[];
        public List<int> BblLatexOrdinals {get;}=[];
        public byte[]? SelectedXdv,LastMap,LastSidecar;
        public Func<NativePhaseRequest,NativePhaseReply,NativePhaseReply>? Mutate;
        public bool HoldConversion;
        public readonly TaskCompletionSource ConversionEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<NativePhaseReply> Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public NativePhaseReply? Pending;
        public int Stops;
        private static byte[] Bbl()=>Bytes("\\begin{thebibliography}{1}\n\\bibitem{entry} Synthetic Author. Model. 2025.\n\\end{thebibliography}\n");
        public Task<NativePhaseReply> ExecuteAsync(NativePhaseRequest r)
        {
            Tags.Add(r.Tag); LoaderHashes.Add(NativeRuntimePackage.Digest(r.Loader).Hex);
            Assert.Equal(13,r.Tag.Version); Assert.Equal(Profile,r.Tag.Profile); Assert.Equal("xelatex",r.Tag.EngineId);
            Assert.DoesNotContain(r.GeneratedInputNames!,n=>n.EndsWith(".xdv",StringComparison.Ordinal)||n.EndsWith(".synctex",StringComparison.Ordinal));
            string job=r.Outputs.Single(n=>n.EndsWith(".pdf",StringComparison.Ordinal))[..^4];
            List<NativeFileTransfer> files=[]; byte[]? map=null,sidecar=null;
            if(r.Tag.Tool=="dvipdfmx")
            {
                SelectedXdv=r.Files.Single(f=>f.Name==job+".xdv").Bytes.ToArray();
                Assert.DoesNotContain(r.Files,f=>f.Name==job+".aux"||f.Name==job+".bbl");
                files.Add(new(job+".pdf",Bytes("%PDF-modeled-Xe-conversion"))); sidecar=Sidecar.ToArray();
                Assert.Equal(NativeCompilerXeNavigationPins.ConverterWasmHash,NativeRuntimePackage.Digest(r.Wasm).Hex);
            }
            else if(r.Tag.Tool=="bibtex")
            {
                Assert.True(Bib); Assert.Equal(Aux(job,r.Files.Any(f=>f.Name==job+".bbl")),r.Files.Single(f=>f.Name==job+".aux").Bytes);
                Assert.Contains(r.Files,f=>f.Name=="references.bib"); Assert.Contains("__oop_runtime__/plain.bst",r.RuntimeResourceNames!);
                Assert.Equal(NativeRuntimeProfiles.SyncTeXCompilerWasmHash,NativeRuntimePackage.Digest(r.Wasm).Hex);
                files.Add(new(job+".bbl",Bbl()));
            }
            else
            {
                Assert.Equal(NativeCompilerXeNavigationPins.WasmHash,NativeRuntimePackage.Digest(r.Wasm).Hex);
                Assert.Equal(NativeCompilerXeNavigationPins.FormatHash,NativeRuntimePackage.Digest(r.Files.Single(f=>f.Name=="__oop_runtime__/xelatex.fmt").Bytes).Hex);
                files.Add(new(job+".aux",Aux(job,r.Files.Any(f=>f.Name==job+".bbl"))));
                foreach(var bbl in r.Files.Where(f=>f.Name==job+".bbl")){Assert.Equal(Bbl(),bbl.Bytes);BblLatexOrdinals.Add(r.Tag.Ordinal);files.Add(new(bbl.Name,bbl.Bytes.ToArray()));}
                files.Add(new(job+".xdv",[247,7,checked((byte)r.Tag.Ordinal)])); map=Bytes("modeled-Xe-map-"+r.Tag.Ordinal);
            }
            LastMap=map??LastMap;LastSidecar=sidecar??LastSidecar;
            var reply=new NativePhaseReply(r.Tag,"native-return",0,true,"modeled Xe phase\n",files.ToArray(),
                r.Files.Select(f=>new NativeInputFact(f.Name,f.Bytes.Length,NativeRuntimePackage.Digest(f.Bytes).Hex)).ToArray(),
                WasmSha256:NativeRuntimePackage.Digest(r.Wasm).Hex,LoaderSha256:NativeRuntimePackage.Digest(r.Loader).Hex,SourceMapPayload:map){SourceMapGeometryPayload=sidecar};
            reply=Mutate?.Invoke(r,reply)??reply;
            if(r.Tag.Tool=="dvipdfmx"&&HoldConversion){Pending=reply;ConversionEntered.TrySetResult();return Release.Task;}
            return Task.FromResult(reply);
        }
        public void RequestTermination(string job,long epoch){Stops++;}
    }
    private static (NativeCompilationWorkerFactory Factory,ICompilationWorker Worker,CompileJobId Id,Task<CompileOutputEnvelopeInput?> Operation) Start(Model model,CompileRequestContext context,TimeSpan? deadline=null,Action<NativePhaseObservation>? observe=null)
    {
        var factory=new NativeCompilationWorkerFactory(new FixedNativeRuntimeProvider(Package),model,NativeCompilerSuiteContract.CreatePlan(NativeCompilerEngine.XeLaTeX,deadline:deadline,profile:Profile));
        if(observe is not null)factory.PhaseObserved+=observe;
        var id=new CompileJobId(Guid.NewGuid());var worker=Assert.IsAssignableFrom<ICompilationWorker>(factory.Create(id,1));
        return(factory,worker,id,Assert.IsAssignableFrom<Task<CompileOutputEnvelopeInput?>>(worker.ExecuteAsync(id,context,Budget,default)));
    }
    private void Trace(CompileOutputEnvelopeInput result,Model model)=>output.WriteLine($"status={result.Status}; phases={string.Join(",",model.Tags.Select(t=>$"{t.Tool}:{t.Ordinal}"))}; diagnostics={JsonSerializer.Serialize(result.Diagnostics)}; log={result.LogText}");

    [XeFixtureTheory][InlineData(false,false)][InlineData(true,false)][InlineData(true,true)]
    public void Final_Xe_map_and_exact_XDV_combine_with_later_geometry_without_converter_raw_map(bool bib,bool controlChurn)=>LifecycleTestHost.Run(async()=>
    {
        await using var f=await CompilationTestWorkspace.Open();Prepare(f,bib);var context=await Context(f,bib);var model=new Model(bib){ChangeAuxAfterBbl=controlChurn};
        var run=Start(model,context,observe:o=>{if(o.Reply.SourceMapPayload is{} map)map.AsSpan().Fill(0);if(o.Reply.SourceMapGeometryPayload is{} g)g.AsSpan().Fill(0);});
        var result=Assert.IsType<CompileOutputEnvelopeInput>(await run.Operation);Trace(result,model);
        Assert.Equal(CompileOutputStatus.Succeeded,result.Status); Assert.Equal("dvipdfmx",model.Tags.Last().Tool);
        var final=model.Tags.Last(t=>t.Tool=="latex");Assert.Equal(bib?controlChurn?6:4:2,final.Ordinal);Assert.Equal(final.Ordinal+1,model.Tags.Last().Ordinal);
        Assert.Equal(bib?controlChurn?new[]{3,5,6}:new[]{3,4}:Array.Empty<int>(),model.BblLatexOrdinals);
        Assert.Equal(bib?controlChurn?2:1:0,model.Tags.Count(t=>t.Tool=="bibtex"));
        Assert.Equal(NativeCompilerXeNavigationPins.LoaderHash,model.LoaderHashes[0]);Assert.Equal(NativeCompilerXeNavigationPins.ConverterLoaderHash,model.LoaderHashes[^1]);
        if(bib)Assert.Equal(NativeRuntimeProfiles.SyncTeXCompilerLoaderHash,model.LoaderHashes[1]);
        Assert.Equal(Bytes("modeled-Xe-map-"+final.Ordinal),result.SourceMapPayload);Assert.Equal(final.Ordinal,(int)model.SelectedXdv![2]);
        var wrapper=Assert.IsType<SourceMapGeometryArtifact>(SourceMapGeometryCodec.ParseWrapper(result.SourceMapGeometryPayload).Artifact);
        Assert.Equal("xelatex",wrapper.Geometry.EngineId);Assert.Equal("xdv",wrapper.Geometry.IntermediateKind);Assert.Equal((uint)final.Ordinal,wrapper.Geometry.FinalPass);
        Assert.Equal(NativeRuntimePackage.Digest(model.SelectedXdv).Hex,wrapper.Geometry.IntermediateSha256);
        Assert.Equal(NativeRuntimePackage.Digest(result.Payload!).Hex,wrapper.Geometry.PdfSha256);Assert.Equal(NativeRuntimePackage.Digest(result.SourceMapPayload!).Hex,wrapper.Geometry.MapSha256);
        model.LastMap!.AsSpan().Fill(0);model.LastSidecar!.AsSpan().Fill(0);
        Assert.Equal(wrapper.Identity,SourceMapGeometryCodec.ParseWrapper(result.SourceMapGeometryPayload).Artifact!.Identity);
        var envelope=CompileOutputEnvelope.Create(result,Budget).Envelope!;var accepted=envelope.TryAccept(context).Output!;
        Assert.Equal(2,accepted.ContentIdentity!.ArtifactVersion);Assert.Equal(wrapper.Identity,accepted.SourceMapGeometry);
        var owned=accepted.ToSourceMapGeometryArray()!;owned.AsSpan().Fill(0);Assert.Equal(wrapper.ToArray(),accepted.ToSourceMapGeometryArray());
        // Same bytes/new captured request and a new epoch are not the old owner.
        Assert.Null(envelope.TryAccept(await Context(f,bib)).Output);Assert.Null(envelope.TryAccept(await Context(f,bib,epoch:2)).Output);
        Assert.True(f.Workspace.EditText(f.Main,"changed source"));Assert.Null(envelope.TryAccept(await Context(f,bib)).Output);
    });

    [XeFixtureTheory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)]
    public void Optional_native_navigation_failure_keeps_PDF_and_fixed_limitation(int mode)=>LifecycleTestHost.Run(async()=>
    {
        await using var f=await CompilationTestWorkspace.Open();Prepare(f);var context=await Context(f);
        var model=new Model{Mutate=(r,reply)=>r.Tag.Tool=="latex"&&r.Tag.Ordinal==2&&mode>=4?reply with{SourceMapPayload=mode==4?null:new byte[1048576]}
            :r.Tag.Tool!="dvipdfmx"?reply:reply with{SourceMapGeometryPayload=mode switch{0=>null,1=>[255],2=>new byte[131073],3=>[],_=>Sidecar.ToArray()}}};
        var run=Start(model,context);var result=Assert.IsType<CompileOutputEnvelopeInput>(await run.Operation);Trace(result,model);
        Assert.Equal("dvipdfmx",model.Tags.Last().Tool);Assert.Equal(CompileOutputStatus.Succeeded,result.Status);
        Assert.NotNull(result.Payload);Assert.Null(result.SourceMapPayload);Assert.Null(result.SourceMapGeometryPayload);Assert.Null(result.SourceMapGeometry);
        Assert.Contains(result.Diagnostics!,d=>d.Code==NativeNavigationContract.LimitationCode&&d.SafeMessage==NativeNavigationContract.LimitationMessage);
        Assert.NotNull(CompileOutputEnvelope.Create(result,Budget).Envelope!.TryAccept(context).Output);
    });

    [XeFixtureTheory][InlineData("ordinal")][InlineData("epoch")][InlineData("nonce")][InlineData("request")][InlineData("xdv")][InlineData("module")][InlineData("failed")][InlineData("timeout-data")]
    public void Forged_or_failed_converter_is_reached_but_cannot_publish_any_navigation(string mode)=>LifecycleTestHost.Run(async()=>
    {
        await using var f=await CompilationTestWorkspace.Open();Prepare(f);var context=await Context(f);
        var model=new Model{Mutate=(r,reply)=>r.Tag.Tool!="dvipdfmx"?reply:mode switch
        {
            "ordinal"=>reply with{Tag=reply.Tag with{Ordinal=1}},"epoch"=>reply with{Tag=reply.Tag with{Epoch=2}},
            "nonce"=>reply with{Tag=reply.Tag with{Nonce="old-operation"}},"request"=>reply with{Tag=reply.Tag with{Request="old-request"}},
            "xdv"=>reply with{Inputs=reply.Inputs.Select(i=>i.Name.EndsWith(".xdv",StringComparison.Ordinal)?i with{Sha256=new('0',64)}:i).ToArray()},
            "module"=>reply with{WasmSha256=NativeCompilerNavigationPins.ConverterWasmHash},
            "timeout-data"=>reply with{Outcome="transport-timeout",NativeReturn=null,Files=[],Inputs=[],WasmSha256=null,LoaderSha256=null},
            _=>reply with{NativeReturn=1,SourceMapGeometryPayload=null}
        }};
        var run=Start(model,context);var result=Assert.IsType<CompileOutputEnvelopeInput>(await run.Operation);Trace(result,model);
        Assert.Equal("dvipdfmx",model.Tags.Last().Tool);Assert.Equal(CompileOutputStatus.Failed,result.Status);
        Assert.Equal(mode=="failed"?"native-dvi-conversion-failed":"invalid-dvi-reply",Assert.Single(result.Diagnostics!).Code);
        Assert.Null(result.Payload);Assert.Null(result.SourceMapPayload);Assert.Null(result.SourceMapGeometryPayload);
    });

    [XeFixtureTheory][InlineData("producer-geometry")][InlineData("bib-map")]
    public void Unexpected_navigation_fields_reject_in_the_actual_wrong_tool_cell(string mode)=>LifecycleTestHost.Run(async()=>
    {
        bool bib=mode=="bib-map";await using var f=await CompilationTestWorkspace.Open();Prepare(f,bib);var context=await Context(f,bib);
        var model=new Model(bib){Mutate=(r,reply)=>mode=="producer-geometry"&&r.Tag.Tool=="latex"?reply with{SourceMapGeometryPayload=Sidecar.ToArray()}
            :mode=="bib-map"&&r.Tag.Tool=="bibtex"?reply with{SourceMapPayload=[1]}:reply};
        var run=Start(model,context);var result=Assert.IsType<CompileOutputEnvelopeInput>(await run.Operation);Trace(result,model);
        Assert.Equal(bib?"bibtex":"latex",model.Tags.Last().Tool);Assert.Equal(bib?2:1,model.Tags.Last().Ordinal);
        Assert.Equal(CompileOutputStatus.Failed,result.Status);Assert.Equal("invalid-native-reply",Assert.Single(result.Diagnostics!).Code);
        Assert.Null(result.Payload);Assert.Null(result.SourceMapPayload);Assert.Null(result.SourceMapGeometryPayload);
    });

    [XeFixtureFact]
    public void Native_unsupported_page_is_retained_as_data_without_a_fabricated_transform()=>LifecycleTestHost.Run(async()=>
    {
        await using var f=await CompilationTestWorkspace.Open();Prepare(f);var context=await Context(f);
        var model=new Model{Mutate=(r,reply)=>r.Tag.Tool!="dvipdfmx"?reply:reply with{SourceMapGeometryPayload=Bytes("{\"schemaVersion\":1,\"pages\":[{\"page\":1,\"status\":\"unsupported\",\"reason\":\"out-of-domain\",\"writerToPdfRaw\":null,\"writerPageBoxBp\":null}]}")}};
        var run=Start(model,context);var result=Assert.IsType<CompileOutputEnvelopeInput>(await run.Operation);Trace(result,model);
        Assert.Equal(CompileOutputStatus.Succeeded,result.Status);Assert.Equal("dvipdfmx",model.Tags.Last().Tool);
        var wrapper=Assert.IsType<SourceMapGeometryArtifact>(SourceMapGeometryCodec.ParseWrapper(result.SourceMapGeometryPayload).Artifact);
        var page=Assert.Single(wrapper.Geometry.Pages);Assert.Equal("unsupported",page.Status);Assert.Equal("out-of-domain",page.Reason);
        Assert.Null(page.WriterToPdfRaw);Assert.Null(page.WriterPageBoxBp);
        Assert.NotNull(CompileOutputEnvelope.Create(result,Budget).Envelope!.TryAccept(context).Output);
    });

    [XeFixtureTheory][InlineData(false)][InlineData(true)]
    public void Cancellation_or_job_deadline_does_not_release_actual_held_conversion_slot(bool deadline)=>LifecycleTestHost.Run(async()=>
    {
        await using var f=await CompilationTestWorkspace.Open();Prepare(f);var context=await Context(f);var model=new Model{HoldConversion=true};
        var run=Start(model,context,deadline?TimeSpan.FromSeconds(8):null);
        await Task.WhenAny(run.Operation,model.ConversionEntered.Task).WaitAsync(TimeSpan.FromSeconds(5));
        if(run.Operation.IsCompleted)Trace(Assert.IsType<CompileOutputEnvelopeInput>(await run.Operation),model);
        Assert.False(run.Operation.IsCompleted,"Compilation ended before actual conversion hold.");
        if(!deadline)run.Worker.RequestTermination(run.Id,1,CompileLifecycleReason.Cancelled);
        var result=Assert.IsType<CompileOutputEnvelopeInput>(await run.Operation.WaitAsync(TimeSpan.FromSeconds(12)));Trace(result,model);
        Assert.Equal(deadline?CompileOutputStatus.TimedOut:CompileOutputStatus.Cancelled,result.Status);
        Assert.Null(result.Payload);Assert.Null(result.SourceMapPayload);Assert.Null(result.SourceMapGeometryPayload);
        Assert.Equal(deadline?0:1,model.Stops);Assert.Equal(1,run.Factory.Operations.Active);Assert.False(run.Factory.Operations.WhenIdle.IsCompleted);
        model.Release.SetResult(Assert.IsType<NativePhaseReply>(model.Pending));await run.Factory.Operations.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(0,run.Factory.Operations.Active);
    });
}
public sealed class XeFixtureFactAttribute:FactAttribute
{public XeFixtureFactAttribute(){if(!File.Exists(Path.Combine(XeTransportTests.Root??"","pins.json")))Skip="Exact complete P9 fixture requires XE_NAVIGATION_RUNTIME_DIR; modeled replies are not native proof.";}}
public sealed class XeFixtureTheoryAttribute:TheoryAttribute
{public XeFixtureTheoryAttribute(){if(!File.Exists(Path.Combine(XeTransportTests.Root??"","pins.json")))Skip="Exact complete P9 fixture requires XE_NAVIGATION_RUNTIME_DIR; modeled replies are not native proof.";}}
