using System.Text;
using System.Text.Json;
using System.Collections;
using Client.Compilation;
using Client.Compilation.Lifecycle;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Workspace.Tests.CompilationLifecycle;
using Workspace.Tests.CompilerSuite;
using Xunit;

namespace Workspace.Tests.CompilerBiber;

// Exact authenticated package, MODELED phase replies. Native/browser proof is separate.
public sealed class BiberPipelineTests
{
    private const NativeRuntimeProfile P = NativeBiberCompilerContract.Profile;
    private static NativeRuntimePackage Package => BiberFixture.Package;
    private static readonly CompileOutputBudget Budget = new(8388608, 65536, 32, 128, 4096, 8192);
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private const string Bib = "література/джерела 😀.bib";
    private const string Bib2 = "other source.bib";

    [Fact]
    public void Closed_profile_retains_old_phases_and_keeps_bootstrap_separate()
    {
        Assert.Equal(13, (int)P);
        Assert.Equal(new[] { 23, 24, 21, 22 }, Enum.GetValues<NativeCompilerEngine>().Select(e => NativeCompilerSuiteContract.Phase(P, e)));
        Assert.Equal(new[] { 6, 15, 21, 22 }, Enum.GetValues<NativeCompilerEngine>().Select(e => NativeCompilerSuiteContract.Phase(NativeUnicodeCompilerContract.Profile, e)));
        Assert.False(NativeRuntimeProfiles.Known((NativeRuntimeProfile)11));
        Assert.False(NativeRuntimeProfiles.Known((NativeRuntimeProfile)17));
        Assert.True(LocalRuntimeSelection.LimitsFor(P).IsValid);
        Assert.Equal(NativeRuntimeProfiles.SyncTeXCompilerWasmHash, NativeBiberCompilerContract.BibtexComponent.WasmHash);
        foreach (var e in Enum.GetValues<NativeCompilerEngine>()) Assert.True(NativeCompilerSuiteContract.CreatePlan(e, profile: P).IsValid);
        Assert.False(NativeNavigationContract.IsMapped(P, 23, NativeCompilerEngine.PdfLaTeX));
        Assert.True(NativeCompilerBiberPins.BiberDataLength <= NativeBiberCompilerContract.BootstrapBytes);
    }

    private static void Prepare(CompilationTestWorkspace f)
    {
        Assert.True(f.Workspace.RenameFile(f.Main, "тези/my thesis.tex"));
        Assert.True(f.Workspace.CreateFile(Bib, "@book{київ,title={Українська книжка 😀},year={2025}}"));
        Assert.True(f.Workspace.CreateFile(Bib2, "@book{second,title={Second},year={2025}}"));
        Assert.True(f.Workspace.CreateFile("unreferenced.bib", "@book{excluded,title={Not captured by BCF}}"));
        Assert.True(f.Workspace.CreateFile("references.bib", "@book{entry,title={Classic model},year={2025}}"));
    }
    private static async Task<CompileRequestContext> Context(CompilationTestWorkspace f, NativeCompilerEngine engine, string backend)
    {
        CompilationDescriptor[] tools = backend switch { "biber" => [Package.Biber], "bibtex" => [Package.Bibtex],
            "both" => [Package.Bibtex, Package.Biber], "unknown" => [new("biber", "2.19", new(new string('0', 64)))], _ => [] };
        f.Selection.SetSelection(CompilationSelection.Create(new(f.Main, Package.EngineFor(engine), [Package.Bundle], tools,
            NativeCompilerSuiteContract.SelectionOptions(P, engine)), 65536).Selection!);
        return CompileRequestContext.Create(await f.Snapshot(new(64, 8388608, 16777216, 131072)), 1, 65536,
            NativeRuntimePackage.Pdf, Package.AdapterFor(engine)).Context!;
    }
    private static (NativeCompilationWorkerFactory Factory, ICompilationWorker Worker, CompileJobId Job, Task<CompileOutputEnvelopeInput?> Result)
        Start(Model model, CompileRequestContext context, NativeCompilerEngine engine, CancellationToken token = default)
    {
        var factory = new NativeCompilationWorkerFactory(new FixedNativeRuntimeProvider(Package), model,
            NativeCompilerSuiteContract.CreatePlan(engine, profile: P));
        var job = new CompileJobId(Guid.NewGuid()); var worker = factory.Create(job, 1);
        return (factory, worker, job, worker.ExecuteAsync(job, context, Budget, token)!);
    }
    private sealed record Call(string Tool, int Version, string Entry, string[] Files, string[] Generated, string? Backend);
    private sealed class Model(NativeCompilerEngine engine, string backend = "biber") : INativePhaseTransport
    {
        public readonly List<Call> Calls = [];
        public int TexPasses, BackendPasses, Stops;
        public int MissingBcfPass;
        public bool ChangedBcf, HoldBiber;
        public Func<NativePhaseRequest, NativePhaseReply, NativePhaseReply>? Mutate;
        public NativePhaseReply? Pending;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<NativePhaseReply> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<NativePhaseReply> ExecuteAsync(NativePhaseRequest request)
        {
            bool biber = request.Tag.Tool == "biber", bibtex = request.Tag.Tool == "bibtex", converter = request.Tag.Tool == "dvipdfmx";
            Assert.Equal(P, request.Tag.Profile); Assert.Equal(backend, request.BibliographyTool);
            Assert.Equal(biber ? 10 : NativeCompilerSuiteContract.Phase(P, engine), request.Tag.Version);
            Calls.Add(new(request.Tag.Tool, request.Tag.Version, request.MainEntry, request.Files.Select(f => f.Name).ToArray(),
                request.GeneratedInputNames?.ToArray() ?? [], request.BibliographyTool));
            var job = request.Outputs[0][..request.Outputs[0].LastIndexOf('.')];
            // TeX output order starts PDF; Biber starts BBL. All use one exact stem.
            var files = new List<NativeFileTransfer>();
            if (biber)
            {
                BackendPasses++;
                Assert.NotNull(request.BootstrapData);
                Assert.Equal(NativeCompilerBiberPins.BiberDataHash, NativeRuntimePackage.Digest(request.BootstrapData!).Hex);
                Assert.Equal(NativeCompilerBiberPins.BiberWasmHash, NativeRuntimePackage.Digest(request.Wasm).Hex);
                Assert.Empty(request.RuntimeResourceNames!); Assert.Equal(new[] { job + ".bcf" }, request.GeneratedInputNames!);
                Assert.Equal(new[] { Bib, Bib2, job + ".bcf" }.Order(StringComparer.Ordinal), request.Files.Select(f => f.Name).Order(StringComparer.Ordinal));
                Assert.DoesNotContain("unreferenced.bib", request.Files.Select(f => f.Name));
                Assert.DoesNotContain(request.Files, f => f.Name.EndsWith(".bbl") || f.Name.EndsWith(".run.xml"));
                files.Add(new(job + ".bbl", Bytes("modeled BBL generation " + BackendPasses)));
                files.Add(new(job + ".blg", Bytes("modeled BLG")));
            }
            else if (bibtex)
            {
                BackendPasses++; Assert.Null(request.BootstrapData);
                Assert.Equal(NativeRuntimeProfiles.SyncTeXCompilerWasmHash, NativeRuntimePackage.Digest(request.Wasm).Hex);
                files.Add(new(job + ".bbl", Bytes("modeled BibTeX BBL")));
            }
            else if (converter) { Assert.Null(request.BootstrapData); files.Add(new(job + ".pdf", Bytes("%PDF-modeled converter"))); }
            else
            {
                TexPasses++; Assert.Null(request.BootstrapData);
                Assert.DoesNotContain(request.Files, f => f.Name.EndsWith("biber.data", StringComparison.Ordinal));
                Assert.DoesNotContain(request.GeneratedInputNames!, n => n.EndsWith(".bcf") || n.EndsWith(".run.xml"));
                files.Add(new(job + ".aux", Bytes(backend == "bibtex" ? BibliographyPipelineFixture.Aux(job, false) : "\\relax\n")));
                if (backend == "biber")
                {
                    Assert.DoesNotContain(request.Outputs, n => n.EndsWith("-blx.bib"));
                    if (TexPasses != MissingBcfPass) files.Add(new(job + ".bcf", Bytes("<bcf:controlfile xmlns:bcf=\""+NativeBiberControlContract.Namespace+"\" version=\"3.10\" bltxversion=\"3.19\"><bcf:bibdata><bcf:datasource type=\"file\" glob=\"false\">"+Bib+"</bcf:datasource><bcf:datasource type=\"file\" glob=\"false\">"+Bib2+"</bcf:datasource></bcf:bibdata><bcf:section number=\""+(ChangedBcf && TexPasses>1?"1":"0")+"\"/></bcf:controlfile>")));
                    files.Add(new(job + ".run.xml", Bytes("volatile audit " + TexPasses)));
                }
                foreach(var old in request.Files.Where(f => f.Name == job + ".bbl")) files.Add(new(old.Name, old.Bytes.ToArray()));
                files.Add(engine == NativeCompilerEngine.LaTeXDvi ? new(job + ".dvi", [247,2,0])
                    : engine == NativeCompilerEngine.XeLaTeX ? new(job + ".xdv", [247,7,0]) : new(job + ".pdf", Bytes("%PDF-modeled TeX")));
            }
            var inputs = request.Files.OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => new NativeInputFact(f.Name, f.Bytes.Length, NativeRuntimePackage.Digest(f.Bytes).Hex)).ToArray();
            var result = new NativePhaseReply(request.Tag, "native-return", 0, true, "modeled phase\n", files.ToArray(), inputs,
                WasmSha256: NativeRuntimePackage.Digest(request.Wasm).Hex, LoaderSha256: NativeRuntimePackage.Digest(request.Loader).Hex)
                { BootstrapSha256 = biber ? NativeCompilerBiberPins.BiberDataHash : null };
            result = Mutate?.Invoke(request, result) ?? result;
            if (biber && HoldBiber) { Pending=result; Entered.TrySetResult(); return Release.Task; }
            return Task.FromResult(result);
        }
        public void RequestTermination(string job, long epoch) => Stops++;
    }

    [BiberFixtureTheory]
    [InlineData(NativeCompilerEngine.PdfLaTeX)] [InlineData(NativeCompilerEngine.LaTeXDvi)]
    [InlineData(NativeCompilerEngine.XeLaTeX)] [InlineData(NativeCompilerEngine.LuaLaTeX)]
    public void Current_BCF_selects_only_referenced_files_and_volatile_audit_does_not_loop(NativeCompilerEngine engine)
        => LifecycleTestHost.Run(async()=>
        {
            await using var f=await CompilationTestWorkspace.Open(); Prepare(f); var context=await Context(f,engine,"biber");
            var model=new Model(engine);var run=Start(model,context,engine);var result=(await run.Result)!;
            Assert.Equal(CompileOutputStatus.Succeeded,result.Status);Assert.Equal(1,model.BackendPasses);Assert.Equal(3,model.TexPasses);
            Assert.Equal(1,model.Calls.Count(c=>c.Tool=="biber")); Assert.NotNull(CompileOutputEnvelope.Create(result,Budget).Envelope!.TryAccept(context).Output);
            Assert.True(f.Workspace.EditText(f.Main,"changed capture"));Assert.Null(CompileOutputEnvelope.Create(result,Budget).Envelope!.TryAccept(await Context(f,engine,"biber")).Output);
        });

    [BiberFixtureTheory] [InlineData("none")] [InlineData("bibtex")]
    public void Explicit_none_and_BibTeX_keep_BCF_unavailable_and_old_BibTeX_executable(string backend)
        => LifecycleTestHost.Run(async()=>
        {
            await using var f=await CompilationTestWorkspace.Open();Prepare(f);var model=new Model(NativeCompilerEngine.PdfLaTeX,backend);
            var result=(await Start(model,await Context(f,NativeCompilerEngine.PdfLaTeX,backend),NativeCompilerEngine.PdfLaTeX).Result)!;
            Assert.Equal(CompileOutputStatus.Succeeded,result.Status);Assert.DoesNotContain(model.Calls,c=>c.Tool=="biber");
            Assert.Equal(backend=="bibtex"?1:0,model.BackendPasses);
        });

    [BiberFixtureFact]
    public void Changed_current_BCF_reruns_backend_once_and_missing_current_BCF_never_reuses_prior_control()
        => LifecycleTestHost.Run(async()=>
        {
            await using var f=await CompilationTestWorkspace.Open();Prepare(f);var context=await Context(f,NativeCompilerEngine.PdfLaTeX,"biber");
            var changed=new Model(NativeCompilerEngine.PdfLaTeX){ChangedBcf=true};
            Assert.Equal(CompileOutputStatus.Succeeded,(await Start(changed,context,NativeCompilerEngine.PdfLaTeX).Result)!.Status);Assert.Equal(2,changed.BackendPasses);
            var missing=new Model(NativeCompilerEngine.PdfLaTeX){MissingBcfPass=2};var result=(await Start(missing,context,NativeCompilerEngine.PdfLaTeX).Result)!;
            Assert.Equal(CompileOutputStatus.Failed,result.Status);Assert.Equal("missing-native-bcf",Assert.Single(result.Diagnostics!).Code);Assert.Equal(1,missing.BackendPasses);
        });

    [BiberFixtureTheory] [InlineData("both")] [InlineData("unknown")]
    public void Multiple_or_unavailable_backend_descriptors_refuse_before_dispatch(string backend)
        => LifecycleTestHost.Run(async()=>
        {
            await using var f=await CompilationTestWorkspace.Open();Prepare(f);var model=new Model(NativeCompilerEngine.PdfLaTeX);
            var result=(await Start(model,await Context(f,NativeCompilerEngine.PdfLaTeX,backend),NativeCompilerEngine.PdfLaTeX).Result)!;
            Assert.Equal(CompileOutputStatus.Unsupported,result.Status);Assert.Empty(model.Calls);
        });

    [BiberFixtureTheory] [InlineData("malformed")] [InlineData("empty-bbl")] [InlineData("foreign-tag")]
    [InlineData("wrong-module")] [InlineData("wrong-bootstrap")] [InlineData("old-profile")]
    [InlineData("aggregate-quota")]
    public void Invalid_control_or_backend_reply_never_becomes_an_eligible_PDF(string mode)
        => LifecycleTestHost.Run(async()=>
        {
            await using var f=await CompilationTestWorkspace.Open();Prepare(f);var model=new Model(NativeCompilerEngine.PdfLaTeX){Mutate=(request,reply)=>
                mode=="malformed"&&request.Tag.Tool=="latex"?reply with{Files=reply.Files.Select(file=>file.Name.EndsWith(".bcf")?file with{Bytes=Bytes("<broken>")}:file).ToArray()}:
                request.Tag.Tool!="biber"?reply:mode switch{
                    "empty-bbl"=>reply with{Files=reply.Files.Select(file=>file.Name.EndsWith(".bbl")?file with{Bytes=[]}:file).ToArray()},
                    "foreign-tag"=>reply with{Tag=reply.Tag with{Nonce="foreign"}},
                    "wrong-bootstrap"=>reply with{BootstrapSha256=new string('0',64)},
                    "old-profile"=>reply with{Tag=reply.Tag with{Profile=NativeUnicodeCompilerContract.Profile}},
                    "aggregate-quota"=>reply with{Files=reply.Files.Select(file=>file with{Bytes=new byte[4*1024*1024+1]}).ToArray()},
                    _=>reply with{WasmSha256=new string('0',64)}}};
            var result=(await Start(model,await Context(f,NativeCompilerEngine.PdfLaTeX,"biber"),NativeCompilerEngine.PdfLaTeX).Result)!;
            Assert.Equal(CompileOutputStatus.Failed,result.Status);Assert.Null(result.Payload);
        });

    [BiberFixtureFact]
    public void Mixed_or_tampered_component_metadata_refuses_before_input_bytes_are_touched()
    {
        var pins=LocalRuntimeSelection.ParsePins(File.ReadAllBytes(Path.Combine(BiberFixture.Root!,"pins.json"))).ToArray();
        foreach(var name in new[]{"biber.js","biber.wasm","biber.data","semantic-engine.js","semantic-engine.wasm","pdflatex.fmt","latex-dvi.fmt"})
        {
            var altered=pins.Select(p=>p.Name==name?p with{Sha256=new string('0',64)}:p).ToArray();
            Assert.Equal(NativeRuntimeIssue.TechnicalHashMismatch,NativeRuntimePackage.Create(new NoInputs(pins.Length),altered,
                LocalRuntimeSelection.LimitsFor(P),P).Issue);
        }
        Assert.NotNull(NativeRuntimePackage.Create(new NoInputs(pins.Length),pins,
            LocalRuntimeSelection.LimitsFor(NativeUnicodeCompilerContract.Profile),NativeUnicodeCompilerContract.Profile).Issue);
    }

    private sealed class NoInputs(int count):IReadOnlyList<NativeResourceInput>
    {
        public int Count=>count;
        public NativeResourceInput this[int index]=>throw new Exception("Untrusted bytes touched before closed metadata admission");
        public IEnumerator<NativeResourceInput> GetEnumerator()=>throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    }

    [BiberFixtureFact]
    public void Generated_BCF_collision_refuses_before_native_dispatch()
        => LifecycleTestHost.Run(async()=>
        {
            await using var f=await CompilationTestWorkspace.Open();Prepare(f);Assert.True(f.Workspace.CreateFile("my thesis.bcf","untrusted supplied control"));
            var model=new Model(NativeCompilerEngine.PdfLaTeX);var result=(await Start(model,await Context(f,NativeCompilerEngine.PdfLaTeX,"biber"),NativeCompilerEngine.PdfLaTeX).Result)!;
            Assert.Equal(CompileOutputStatus.Failed,result.Status);Assert.Empty(model.Calls);Assert.Null(result.Payload);
        });

    [BiberFixtureFact]
    public void Exact_semantic_unsupported_frame_preserves_actual_zero_status_and_returns_no_PDF()
        => LifecycleTestHost.Run(async()=>
        {
            await using var f=await CompilationTestWorkspace.Open();Prepare(f);var model=new Model(NativeCompilerEngine.PdfLaTeX){Mutate=(request,reply)=>reply with
                {Outcome="font-semantics-unsupported",NativeReturn=0,Files=[]}};
            var result=(await Start(model,await Context(f,NativeCompilerEngine.PdfLaTeX,"biber"),NativeCompilerEngine.PdfLaTeX).Result)!;
            Assert.Equal(CompileOutputStatus.Unsupported,result.Status);Assert.Equal("font-semantics-unsupported",Assert.Single(result.Diagnostics!).Code);Assert.Null(result.Payload);
        });

    [BiberFixtureFact]
    public void Cancelling_held_backend_retains_actual_foreign_slot_until_settlement()
        => LifecycleTestHost.Run(async()=>
        {
            await using var f=await CompilationTestWorkspace.Open();Prepare(f);var model=new Model(NativeCompilerEngine.PdfLaTeX){HoldBiber=true};
            var run=Start(model,await Context(f,NativeCompilerEngine.PdfLaTeX,"biber"),NativeCompilerEngine.PdfLaTeX);
            await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));run.Worker.RequestTermination(run.Job,1,CompileLifecycleReason.Cancelled);
            Assert.Equal(CompileOutputStatus.Cancelled,(await run.Result)!.Status);Assert.Equal(1,run.Factory.Operations.Active);
            model.Release.SetResult(model.Pending!);await run.Factory.Operations.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(0,run.Factory.Operations.Active);
        });
}

internal static class BiberFixture
{
    internal static string? Root=>Environment.GetEnvironmentVariable("BIBER_COMPILER_RUNTIME_DIR");
    private static readonly Lazy<NativeRuntimePackage> Loaded=new(()=>{
        var pins=LocalRuntimeSelection.ParsePins(File.ReadAllBytes(Path.Combine(Root!,"pins.json"))).ToArray();
        var input=pins.Select(p=>new NativeResourceInput(p.Name,File.ReadAllBytes(Path.Combine(Root!,"files",p.Name)))).ToArray();
        return Assert.IsType<NativeRuntimePackage>(NativeRuntimePackage.Create(input,pins,LocalRuntimeSelection.LimitsFor(NativeBiberCompilerContract.Profile),NativeBiberCompilerContract.Profile).Package);});
    internal static NativeRuntimePackage Package=>Loaded.Value;
}
public sealed class BiberFixtureFactAttribute:FactAttribute
{public BiberFixtureFactAttribute(){if(!File.Exists(Path.Combine(BiberFixture.Root??"","pins.json")))Skip="Requires exact P13 BIBER_COMPILER_RUNTIME_DIR; modeled transport is not native proof.";}}
public sealed class BiberFixtureTheoryAttribute:TheoryAttribute
{public BiberFixtureTheoryAttribute(){if(!File.Exists(Path.Combine(BiberFixture.Root??"","pins.json")))Skip="Requires exact P13 BIBER_COMPILER_RUNTIME_DIR; modeled transport is not native proof.";}}
