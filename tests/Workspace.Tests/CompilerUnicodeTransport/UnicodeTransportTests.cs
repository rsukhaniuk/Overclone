using System.Text;
using Client.Compilation;
using Client.Compilation.Lifecycle;
using Client.Compilation.History;
using Client.Compilation.Native;
using Client.Compilation.SyncTeX;
using Client.Compilation.Presentation;
using Client.RuntimeAssets;
using Workspace.Tests.CompilationNative;
using Workspace.Tests.CompilationLifecycle;
using Workspace.Tests.CompilerSuite;
using Xunit;

namespace Workspace.Tests.CompilerUnicodeTransport;

// Actual inventory/package bytes, MODELED native replies. Genuine native tests are separate.
public sealed class UnicodeTransportTests
{
    private const NativeRuntimeProfile P = NativeUnicodeCompilerContract.Profile;
    private static readonly CompileOutputBudget Budget = new(8388608, 65536, 32, 128, 4096, 8192);
    private static NativeRuntimePackage Package => UnicodeFixture.Package;
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static readonly byte[] Geometry = Bytes("{\"schemaVersion\":1,\"pages\":[{\"page\":1,\"status\":\"available\",\"reason\":\"none\",\"writerToPdfRaw\":[1,0,0,-1,3,800],\"writerPageBoxBp\":[0,0,600,800]}]}");

    private static void Prepare(CompilationTestWorkspace fixture, NativeCompilerEngine engine, bool bib)
    {
        Assert.True(fixture.Workspace.RenameFile(fixture.Main, engine == NativeCompilerEngine.XeLaTeX ? "тези/розділ 😀.tex" : "тези/my thesis.tex"));
        if (bib) Assert.True(fixture.Workspace.CreateFile("references.bib", "@book{entry,title={Model},author={Synthetic Author},publisher={Synthetic},year={2025}}"));
    }
    private static async Task<CompileRequestContext> Context(CompilationTestWorkspace fixture, NativeCompilerEngine engine, bool bib)
    {
        fixture.Selection.SetSelection(CompilationSelection.Create(new(fixture.Main, Package.EngineFor(engine), [Package.Bundle],
            bib ? [Package.Bibtex] : [], NativeCompilerSuiteContract.SelectionOptions(P, engine)), 65536).Selection!);
        return CompileRequestContext.Create(await fixture.Snapshot(new(64, 8388608, 16777216, 131072)), 1, 65536,
            NativeRuntimePackage.Pdf, Package.AdapterFor(engine)).Context!;
    }

    private sealed class Model(NativeCompilerEngine engine) : INativePhaseTransport
    {
        public readonly List<NativePhaseTag> Tags = [];
        public Func<NativePhaseRequest, NativePhaseReply, NativePhaseReply>? Mutate;
        public bool Hold;
        public int Stops;
        public NativePhaseReply? Pending;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<NativePhaseReply> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<NativePhaseReply> ExecuteAsync(NativePhaseRequest request)
        {
            Tags.Add(request.Tag);
            Assert.Equal(P, request.Tag.Profile); Assert.Equal(NativeCompilerSuiteContract.Phase(P, engine), request.Tag.Version);
            var pdf = request.Outputs.Single(n => n.EndsWith(".pdf", StringComparison.Ordinal));
            var job = pdf[..^4]; bool convert = request.Tag.Tool == "dvipdfmx", bib = request.Tag.Tool == "bibtex";
            if (engine == NativeCompilerEngine.XeLaTeX) Assert.Equal("тези/розділ 😀.tex", request.SelectedRoot);
            var actual = bib ? NativeCompilerComponents.For(P, NativeCompilerEngine.PdfLaTeX) : NativeCompilerComponents.For(P, engine);
            var expectedHash = convert ? Package.AdapterFor(engine).Hash!.Value.Hex : actual.WasmHash;
            Assert.Equal(expectedHash, NativeRuntimePackage.Digest(request.Wasm).Hex);
            if (!convert && !bib && engine == NativeCompilerEngine.LuaLaTeX)
            {
                Assert.Contains("__oop_runtime__/harfbuzz.lua", request.RuntimeResourceNames!);
                Assert.Equal(NativeCompilerUnicodePins.LuaFormatHash, NativeRuntimePackage.Digest(request.Files.Single(f => f.Name == "__oop_runtime__/lualatex.fmt").Bytes).Hex);
                Assert.True(request.Files.Where(f => request.RuntimeResourceNames!.Contains(f.Name)).Sum(f => (long)f.Bytes.Length) <= NativeUnicodeCompilerContract.LuaResourceBytes);
            }
            List<NativeFileTransfer> files = []; byte[]? map = null, geometry = null;
            if (convert) { files.Add(new(pdf, Bytes("%PDF-modeled unicode converter"))); geometry = Geometry; }
            else if (bib) files.Add(new(job + ".bbl", Bytes("\\begin{thebibliography}{1}\\bibitem{entry} Synthetic.\\end{thebibliography}")));
            else
            {
                bool withBib = request.Files.Any(f => f.Name == "references.bib");
                files.Add(new(job + ".aux", Bytes(withBib ? BibliographyPipelineFixture.Aux(job, biblatex: false) : "\\relax\n")));
                foreach (var bbl in request.Files.Where(f => f.Name == job + ".bbl")) files.Add(new(bbl.Name, bbl.Bytes.ToArray()));
                map = Bytes("modeled current map " + request.Tag.Ordinal);
                if (engine == NativeCompilerEngine.LuaLaTeX) { files.Add(new(pdf, Bytes("%PDF-modeled LuaHB"))); geometry = Geometry; }
                else if (engine == NativeCompilerEngine.PdfLaTeX) files.Add(new(pdf, Bytes("%PDF-modeled pdfTeX")));
                else files.Add(new(job + (engine == NativeCompilerEngine.XeLaTeX ? ".xdv" : ".dvi"), [247, engine == NativeCompilerEngine.XeLaTeX ? (byte)7 : (byte)2, (byte)request.Tag.Ordinal]));
            }
            var reply = new NativePhaseReply(request.Tag, "native-return", 0, true, "modeled exact P12 phase\n", files.ToArray(),
                request.Files.Select(f => new NativeInputFact(f.Name, f.Bytes.Length, NativeRuntimePackage.Digest(f.Bytes).Hex)).ToArray(),
                WasmSha256: NativeRuntimePackage.Digest(request.Wasm).Hex, LoaderSha256: NativeRuntimePackage.Digest(request.Loader).Hex,
                SourceMapPayload: map) { SourceMapGeometryPayload = geometry };
            reply = Mutate?.Invoke(request, reply) ?? reply;
            if (Hold && (convert || engine == NativeCompilerEngine.LuaLaTeX && request.Tag.Ordinal == 2))
            { Pending = reply; Entered.TrySetResult(); return Release.Task; }
            return Task.FromResult(reply);
        }
        public void RequestTermination(string job, long epoch) => Stops++;
    }

    private static (NativeCompilationWorkerFactory Factory, ICompilationWorker Worker, CompileJobId Job, Task<CompileOutputEnvelopeInput?> Result) Start(Model model, CompileRequestContext context, NativeCompilerEngine engine)
    {
        var factory = new NativeCompilationWorkerFactory(new FixedNativeRuntimeProvider(Package), model, NativeCompilerSuiteContract.CreatePlan(engine, profile: P));
        var job = new CompileJobId(Guid.NewGuid()); var worker = Assert.IsAssignableFrom<ICompilationWorker>(factory.Create(job, 1));
        return (factory, worker, job, Assert.IsAssignableFrom<Task<CompileOutputEnvelopeInput?>>(worker.ExecuteAsync(job, context, Budget, default)));
    }

    [UnicodeFixtureTheory]
    [InlineData(NativeCompilerEngine.PdfLaTeX, false)] [InlineData(NativeCompilerEngine.PdfLaTeX, true)]
    [InlineData(NativeCompilerEngine.LaTeXDvi, false)] [InlineData(NativeCompilerEngine.LaTeXDvi, true)]
    [InlineData(NativeCompilerEngine.XeLaTeX, false)] [InlineData(NativeCompilerEngine.XeLaTeX, true)]
    [InlineData(NativeCompilerEngine.LuaLaTeX, false)] [InlineData(NativeCompilerEngine.LuaLaTeX, true)]
    public void Finite_factory_selects_exact_P12_components_and_preserves_revision_bound_output(NativeCompilerEngine engine, bool bib)
        => LifecycleTestHost.Run(async () =>
        {
            await using var fixture = await CompilationTestWorkspace.Open(); Prepare(fixture, engine, bib);
            var context = await Context(fixture, engine, bib); var model = new Model(engine); var run = Start(model, context, engine);
            var result = Assert.IsType<CompileOutputEnvelopeInput>(await run.Result);
            Assert.Equal(CompileOutputStatus.Succeeded, result.Status);
            Assert.Equal(bib ? 1 : 0, model.Tags.Count(t => t.Tool == "bibtex"));
            var accepted = CompileOutputEnvelope.Create(result, Budget).Envelope!.TryAccept(context).Output;
            Assert.NotNull(accepted); Assert.Equal(Package.AdapterFor(engine), accepted.OutputAdapter);
            Assert.NotNull(result.SourceMapPayload);
            if (engine != NativeCompilerEngine.PdfLaTeX)
            {
                var geometry = Assert.IsType<SourceMapGeometryArtifact>(SourceMapGeometryCodec.ParseWrapper(result.SourceMapGeometryPayload).Artifact);
                Assert.Equal((uint)model.Tags.Last(t => t.Tool == "latex").Ordinal, geometry.Geometry.FinalPass);
                Assert.Equal(NativeRuntimePackage.Digest(result.Payload!).Hex, geometry.Geometry.PdfSha256);
            }
            Assert.True(fixture.Workspace.EditText(fixture.Main, "changed Україна 😀"));
            Assert.Null(CompileOutputEnvelope.Create(result, Budget).Envelope!.TryAccept(await Context(fixture, engine, bib)).Output);
        });

    [UnicodeFixtureTheory]
    [InlineData("old-phase")] [InlineData("future-phase")] [InlineData("old-profile")]
    [InlineData("old-engine")] [InlineData("wrong-loader")]
    public void Wrong_profile_phase_or_module_is_rejected_after_target_dispatch(string mode)
        => LifecycleTestHost.Run(async () =>
        {
            const NativeCompilerEngine engine = NativeCompilerEngine.XeLaTeX;
            await using var fixture = await CompilationTestWorkspace.Open(); Prepare(fixture, engine, false);
            var model = new Model(engine) { Mutate = (request, reply) => request.Tag.Tool != "latex" ? reply : mode switch
            {
                "old-phase" => reply with { Tag = reply.Tag with { Version = 16 } },
                "future-phase" => reply with { Tag = reply.Tag with { Version = 23 } },
                "old-profile" => reply with { Tag = reply.Tag with { Profile = NativeGraphicsNavigationContract.Profile } },
                "old-engine" => reply with { WasmSha256 = NativeCompilerXeNavigationPins.WasmHash },
                _ => reply with { LoaderSha256 = NativeCompilerXeNavigationPins.LoaderHash }
            } };
            var result = Assert.IsType<CompileOutputEnvelopeInput>(await Start(model, await Context(fixture, engine, false), engine).Result);
            Assert.NotEmpty(model.Tags); Assert.Equal(CompileOutputStatus.Failed, result.Status);
            Assert.Equal("invalid-native-reply", Assert.Single(result.Diagnostics!).Code);
            Assert.Null(result.Payload); Assert.Null(result.SourceMapPayload); Assert.Null(result.SourceMapGeometryPayload);
        });

    [UnicodeFixtureTheory]
    [InlineData(NativeCompilerEngine.XeLaTeX)] [InlineData(NativeCompilerEngine.LuaLaTeX)]
    public void Cancellation_retains_actual_operation_slot_until_the_late_reply_settles(NativeCompilerEngine engine)
        => LifecycleTestHost.Run(async () =>
        {
            await using var fixture = await CompilationTestWorkspace.Open(); Prepare(fixture, engine, false);
            var model = new Model(engine) { Hold = true }; var run = Start(model, await Context(fixture, engine, false), engine);
            await Task.WhenAny(run.Result, model.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(run.Result.IsCompleted, "Must reach held final phase");
            run.Worker.RequestTermination(run.Job, 1, CompileLifecycleReason.Cancelled);
            Assert.Equal(CompileOutputStatus.Cancelled, (await run.Result.WaitAsync(TimeSpan.FromSeconds(5)))!.Status);
            Assert.Equal(1, model.Stops); Assert.Equal(1, run.Factory.Operations.Active);
            model.Release.SetResult(Assert.IsType<NativePhaseReply>(model.Pending));
            await run.Factory.Operations.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, run.Factory.Operations.Active);
        });

    [UnicodeFixtureFact]
    public void Emoji_output_collision_is_refused_before_any_native_dispatch()
        => LifecycleTestHost.Run(async () =>
        {
            const NativeCompilerEngine engine = NativeCompilerEngine.XeLaTeX;
            await using var fixture = await CompilationTestWorkspace.Open(); Prepare(fixture, engine, false);
            Assert.True(fixture.Workspace.CreateFile("розділ 😀.xdv", "untrusted supplied output"));
            var model = new Model(engine);
            var result = Assert.IsType<CompileOutputEnvelopeInput>(await Start(model, await Context(fixture, engine, false), engine).Result);
            Assert.Equal(CompileOutputStatus.Failed, result.Status); Assert.Empty(model.Tags); Assert.Null(result.Payload);
        });

    [UnicodeFixtureFact]
    public void P12_selection_metadata_cannot_upgrade_an_actual_old_profile_plan()
        => LifecycleTestHost.Run(async () =>
        {
            const NativeCompilerEngine engine = NativeCompilerEngine.XeLaTeX;
            await using var fixture = await CompilationTestWorkspace.Open(); Prepare(fixture, engine, false);
            var model = new Model(engine);
            var factory = new NativeCompilationWorkerFactory(new FixedNativeRuntimeProvider(Package), model,
                NativeCompilerSuiteContract.CreatePlan(engine, profile: NativeGraphicsNavigationContract.Profile));
            var job = new CompileJobId(Guid.NewGuid()); var worker = factory.Create(job, 1);
            var result = Assert.IsType<CompileOutputEnvelopeInput>(await worker.ExecuteAsync(job, await Context(fixture, engine, false), Budget, default)!);
            Assert.Equal(CompileOutputStatus.Unsupported, result.Status); Assert.Empty(model.Tags); Assert.Null(result.Payload);
        });

    [UnicodeFixtureFact]
    public void Historical_store_accepts_exact_P12_Lua_and_refuses_forged_or_excess_descriptors()
        => LifecycleTestHost.Run(async () =>
        {
            const NativeCompilerEngine engine = NativeCompilerEngine.LuaLaTeX;
            await using var fixture = await CompilationTestWorkspace.Open(); Prepare(fixture, engine, false);
            var context = await Context(fixture, engine, false);
            var result = Assert.IsType<CompileOutputEnvelopeInput>(await Start(new Model(engine), context, engine).Result);
            var accepted = CompileOutputEnvelope.Create(result, Budget).Envelope!.TryAccept(context).Output!;
            var adapters = AuthoringWorkspaceRegistration.HistoricalOutputAdapters().ToList();
            Assert.Equal(16, adapters.Count);
            var backend = new SetupBackend();
            await using var store = new CompileArtifactHistoryStore(backend, fixture.Repo,
                NativeRuntimePackage.Pdf, NativeRuntimePackage.Adapter, adapters);
            var forgedAdapter = Package.AdapterFor(engine) with { Hash = new(new string('0', 64)) };
            adapters.Clear(); adapters.Add(forgedAdapter); // Caller mutation cannot change the stored whitelist.
            var historyBudget = new ArtifactHistoryBudget(new(32, 1048576, 8388608, 65536), 1048576, 65536,
                new(new(2, 256, 1048576, 2097152, 4096, 256, 256), 16384, 16, 32, 30000));
            var saved = await store.PersistAsync(accepted, null, historyBudget);
            Assert.Equal(RuntimeAssetStatus.Installed, saved.Status);
            var loaded = await store.ReadAsync(saved.Key!, fixture.Repo.Project.Id, null, historyBudget);
            Assert.Equal(RuntimeAssetStatus.Ready, loaded.Status);
            Assert.Equal("HistoricalOnly", loaded.Artifact!.Classification);
            Assert.Equal(accepted.OutputAdapter, loaded.Artifact.ContentIdentity.OutputAdapter);
            var forgedContext = CompileRequestContext.Create(context.Snapshot, 1, 65536, NativeRuntimePackage.Pdf, forgedAdapter).Context!;
            var forged = result with { RequestId = forgedContext.RequestId, OutputAdapter = forgedAdapter };
            var forgedAccepted = CompileOutputEnvelope.Create(forged, Budget).Envelope!.TryAccept(forgedContext).Output!;
            var calls = backend.Calls;
            Assert.Equal(RuntimeAssetStatus.InvalidInput, (await store.PersistAsync(forgedAccepted, null, historyBudget)).Status);
            Assert.Equal(calls, backend.Calls);
            Assert.Throws<ArgumentException>(() => new CompileArtifactHistoryStore(backend, fixture.Repo,
                NativeRuntimePackage.Pdf, NativeRuntimePackage.Adapter,
                Enumerable.Repeat(Package.AdapterFor(engine), CompileArtifactHistoryStore.MaxAdditionalAdapters + 1).ToArray()));
        });
}
