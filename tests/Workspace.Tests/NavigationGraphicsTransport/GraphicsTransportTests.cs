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

namespace Workspace.Tests.NavigationGraphicsTransport;

// Actual closed package bytes, MODELED replies. Not native/browser navigation proof.
public sealed class GraphicsTransportTests
{
    private const NativeRuntimeProfile P = NativeRuntimeProfile.LocalCompilerGraphicsNavigation;
    internal static string? Root => Environment.GetEnvironmentVariable("GRAPHICS_NAVIGATION_RUNTIME_DIR");
    private static readonly NativeRuntimeLimits Limits = new(NativeCompilerSuiteContract.Files, NativeCompilerSuiteContract.FileBytes, NativeCompilerSuiteContract.TotalBytes, P);
    private static readonly CompileOutputBudget Budget = new(8388608, 65536, 32, 128, 4096, 8192);
    // Consumer fixture does not depend on root applying provisioning proposals.
    private static NativeResourcePin[] Pins() => JsonSerializer.Deserialize<NativeResourcePin[]>(File.ReadAllBytes(Path.Combine(Root!, "pins.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    private static readonly Lazy<NativeRuntimePackage> Fixture = new(() =>
    {
        var pins = Pins();
        var inputs = pins.Select(p => new NativeResourceInput(p.Name, File.ReadAllBytes(Path.Combine(Root!, "files", p.Name)))).ToArray();
        return Assert.IsType<NativeRuntimePackage>(NativeRuntimePackage.Create(inputs, pins, Limits, P).Package);
    });
    private static NativeRuntimePackage Package => Fixture.Value;
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
    private static readonly byte[] Sidecar = Bytes("{\"schemaVersion\":1,\"pages\":[{\"page\":1,\"status\":\"available\",\"reason\":\"none\",\"writerToPdfRaw\":[1,0,0,-1,3,800],\"writerPageBoxBp\":[0,0,600,800]}]}");

    [Fact]
    public void Closed_new_matrix_and_options_preserve_every_prior_overload()
    {
        Assert.Equal(10, (int)P);
        Assert.Equal(new[] { 6, 7, 8, 9 }, Enum.GetValues<NativeCompilerEngine>().Select(NativeCompilerSuiteContract.Phase));
        foreach (var (profile, phases) in new[] {
            (NativeRuntimeProfile.LocalCompilerNavigation, new[] {6,12,8,14}),
            (NativeRuntimeProfile.LocalCompilerXeNavigation, new[] {6,12,13,14}), (P, new[] {6,15,16,17}) })
            Assert.Equal(phases, Enum.GetValues<NativeCompilerEngine>().Select(e => NativeCompilerSuiteContract.Phase(profile, e)));
        foreach (var e in Enum.GetValues<NativeCompilerEngine>())
        {
            var plan = NativeCompilerSuiteContract.CreatePlan(e, profile: P); Assert.True(plan.IsValid);
            foreach (var phase in Enumerable.Range(0, 20).Where(n => n != plan.EffectivePhaseVersion)) Assert.False((plan with { PhaseProtocolVersion = phase }).IsValid);
            Assert.False((plan with { DviOutput = !plan.DviOutput }).IsValid);
            var options = NativeCompilerSuiteContract.SelectionOptions(P, e);
            Assert.True(NativeCompilerSuiteContract.MatchesOptions(options, P, e));
            Assert.False(NativeCompilerSuiteContract.MatchesOptions(options.Concat(options), P, e));
            Assert.False(NativeCompilerSuiteContract.MatchesOptions(options.Append(new("unknown", "1")), P, e));
            if (e != NativeCompilerEngine.PdfLaTeX) Assert.False(NativeCompilerSuiteContract.MatchesOptions(options));
        }
        foreach (var p in new[] { (NativeRuntimeProfile)11, (NativeRuntimeProfile)int.MaxValue })
        { Assert.False(NativeRuntimeProfiles.Known(p)); Assert.False(NativeRuntimeProfiles.IsSuite(p)); Assert.Equal(0, NativeCompilerSuiteContract.Phase(p, NativeCompilerEngine.XeLaTeX)); }
        Assert.True(NativeRuntimeProfiles.HasBibliographyCarry(P)); Assert.True(Limits.IsValid);
        Assert.Equal("local-compiler-graphics-navigation-v1", NativeRuntimeProfiles.IdentityName(P));
        Assert.Equal(3709, NativeRuntimeProfiles.InstalledCount(P)); Assert.Equal(192577312, NativeRuntimeProfiles.SuiteTotalBytes(P));
        Assert.Equal(NativeCompilerGraphicsNavigationPins.ClosureHash, NativeRuntimeProfiles.ClosureHash(P));
    }

    [Fact]
    public void Graphics_descriptors_keep_base_ABI_and_exact_old_producer_format_query_identity()
    {
        foreach (var e in Enum.GetValues<NativeCompilerEngine>())
        {
            var old = NativeCompilerComponents.For(NativeRuntimeProfile.LocalCompilerXeNavigation, e);
            var next = NativeCompilerComponents.For(P, e);
            Assert.Equal(old.FormatName, next.FormatName); Assert.Equal(old.FormatHash, next.FormatHash); Assert.Equal(old.FormatLength, next.FormatLength);
            Assert.Equal(old.QueryLoaderHash, next.QueryLoaderHash); Assert.Equal(old.QueryWasmHash, next.QueryWasmHash); Assert.Equal(old.NativeAbiVersion, next.NativeAbiVersion);
            Assert.Equal(old.SupportsSourceMap, next.SupportsSourceMap);
            if (e != NativeCompilerEngine.LuaLaTeX) { Assert.Equal(old.LoaderHash, next.LoaderHash); Assert.Equal(old.WasmHash, next.WasmHash); }
            if (e == NativeCompilerEngine.PdfLaTeX) Assert.Equal(old, next);
            else { Assert.NotEqual(old.OutputAdapter, next.OutputAdapter); Assert.EndsWith("-graphics1", next.OutputAdapter.Version); }
        }
        var retained = Client.Compilation.Presentation.AuthoringWorkspaceRegistration.HistoricalOutputAdapters();
        Assert.Equal(NativeCompilerPageLabelPins.Ready ? 25 : NativeCompilerPdfOriginPins.Ready ? 24 : NativeCompilerDviGraphicsPins.Ready ? 23 : NativeCompilerInputBudgetPins.Ready ? 22 : NativeCompilerOriginPins.Ready ? 20 : 17, retained.Count); Assert.Equal(retained.Count, retained.Distinct().Count());
        Assert.Contains(NativeThesisCompilerContract.Component(NativeCompilerEngine.LuaLaTeX).OutputAdapter, retained);
        Assert.True(retained.Count <= Client.Compilation.History.CompileArtifactHistoryStore.MaxAdditionalAdapters);
        foreach (var e in Enum.GetValues<NativeCompilerEngine>()) Assert.Contains(NativeCompilerComponents.For(P, e).OutputAdapter, retained);
        Assert.Equal("abi5-graphics1", NativeCompilerComponents.For(P, NativeCompilerEngine.LuaLaTeX).EngineVersion);
        Assert.Equal(NativeCompilerGraphicsNavigationPins.XeWasmHash, NativeCompilerComponents.For(P, NativeCompilerEngine.XeLaTeX).OutputAdapter.Hash!.Value.Hex);
        Assert.Equal(NativeCompilerGraphicsNavigationPins.DviWasmHash, NativeCompilerComponents.For(P, NativeCompilerEngine.LaTeXDvi).OutputAdapter.Hash!.Value.Hex);
    }

    [GraphicsFixtureFact]
    public void Closed_full_inventory_and_all_six_changes_bind_before_any_untrusted_input()
    {
        var pins = Pins(); var dictionary = pins.ToDictionary(p => p.Name, StringComparer.Ordinal);
        Assert.Equal(3709, pins.Length); Assert.Equal(192577312L, pins.Sum(p => (long)p.Length));
        Assert.True(NativeCompilerGraphicsNavigationPins.MatchesComponents(dictionary)); Assert.False(NativeCompilerXeNavigationPins.MatchesComponents(dictionary));
        foreach (var name in new[] { "luatex.js", "luatex.wasm", "dvi-map-converter.js", "dvi-map-converter.wasm", "xdv-map-converter.js", "xdv-map-converter.wasm", "article.cls" })
        {
            Assert.Contains(name, dictionary.Keys);
            var bad = pins.Select(p => p.Name == name ? p with { Sha256 = new('0', 64) } : p).ToArray();
            Assert.Equal(NativeRuntimeIssue.TechnicalHashMismatch, NativeRuntimePackage.Create(new NoInputs(pins.Length), bad, Limits, P).Issue);
        }
        Assert.Equal("local-compiler-graphics-navigation", Package.Bundle.Id); Assert.Equal("1", Package.Bundle.Version);
        foreach (var e in Enum.GetValues<NativeCompilerEngine>()) Assert.Equal(NativeCompilerComponents.For(P, e).OutputAdapter, Package.AdapterFor(e));
        var copy = Package.Copy("luatex.wasm"); copy.AsSpan().Fill(0); Assert.Equal(NativeCompilerGraphicsNavigationPins.LuaWasmHash, NativeRuntimePackage.Digest(Package.Copy("luatex.wasm")).Hex);
    }
    private sealed class NoInputs(int count) : IReadOnlyList<NativeResourceInput>
    {
        public int Count => count; public NativeResourceInput this[int index] => throw new Exception("Untrusted input read too early");
        public IEnumerator<NativeResourceInput> GetEnumerator() => throw new NotSupportedException(); IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private static void Prepare(CompilationTestWorkspace f, bool bib)
    {
        Assert.True(f.Workspace.RenameFile(f.Main, "тези/my thesis.tex"));
        Assert.True(f.Workspace.CreateFile("розділи/part one.tex", "% Україна 🙂 synthetic source\n"));
        if (bib) Assert.True(f.Workspace.CreateFile("references.bib", "@book{entry,title={Model},author={Synthetic Author},publisher={Synthetic Press},year={2025}}"));
    }
    private static async Task<CompileRequestContext> Context(CompilationTestWorkspace f, NativeCompilerEngine engine, bool bib)
    {
        f.Selection.SetSelection(CompilationSelection.Create(new(f.Main, Package.EngineFor(engine), [Package.Bundle], bib ? [Package.Bibtex] : [], NativeCompilerSuiteContract.SelectionOptions(P, engine)), 65536).Selection!);
        return CompileRequestContext.Create(await f.Snapshot(new(64,8388608,16777216,131072)), 1, 65536, NativeRuntimePackage.Pdf, Package.AdapterFor(engine)).Context!;
    }
    private sealed class Model(NativeCompilerEngine engine, bool bib) : INativePhaseTransport
    {
        public List<NativePhaseTag> Tags { get; } = [];
        public Func<NativePhaseRequest, NativePhaseReply, NativePhaseReply>? Mutate;
        public byte[]? FinalIntermediate;
        public bool Hold;
        public int Stops;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<NativePhaseReply> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public NativePhaseReply? Pending;
        public Task<NativePhaseReply> ExecuteAsync(NativePhaseRequest r)
        {
            Tags.Add(r.Tag); Assert.Equal(P, r.Tag.Profile); Assert.Equal(NativeCompilerSuiteContract.Phase(P, engine), r.Tag.Version);
            Assert.DoesNotContain(r.GeneratedInputNames!, n => n.EndsWith(".synctex", StringComparison.Ordinal) || n.EndsWith(".xdv", StringComparison.Ordinal) || n.EndsWith(".dvi", StringComparison.Ordinal));
            var pdf = r.Outputs.Single(n => n.EndsWith(".pdf", StringComparison.Ordinal)); var job = pdf[..^4];
            bool convert = r.Tag.Tool == "dvipdfmx"; List<NativeFileTransfer> files = []; byte[]? map = null, geo = null;
            string ext = engine == NativeCompilerEngine.XeLaTeX ? ".xdv" : ".dvi";
            if (convert)
            {
                FinalIntermediate = r.Files.Single(f => f.Name == job + ext).Bytes.ToArray();
                Assert.DoesNotContain(r.Files, f => f.Name == job + ".aux" || f.Name == job + ".bbl");
                Assert.Equal(Package.AdapterFor(engine).Hash!.Value.Hex, NativeRuntimePackage.Digest(r.Wasm).Hex);
                files.Add(new(pdf, Bytes("%PDF-modeled graphics converter"))); geo = Sidecar.ToArray();
            }
            else if (r.Tag.Tool == "bibtex")
            {
                Assert.True(bib); Assert.Contains(r.Files, f => f.Name == "references.bib"); Assert.Contains("__oop_runtime__/plain.bst", r.RuntimeResourceNames!);
                Assert.Equal(NativeRuntimeProfiles.SyncTeXCompilerWasmHash, NativeRuntimePackage.Digest(r.Wasm).Hex);
                files.Add(new(job + ".bbl", Bytes("\\begin{thebibliography}{1}\n\\bibitem{entry} Synthetic Author. Model. 2025.\n\\end{thebibliography}\n")));
            }
            else
            {
                Assert.Equal(NativeCompilerComponents.For(P, engine).WasmHash, NativeRuntimePackage.Digest(r.Wasm).Hex);
                files.Add(new(job + ".aux", Bytes(bib ? BibliographyPipelineFixture.Aux(job, biblatex:false) : "\\relax\n")));
                foreach (var bbl in r.Files.Where(f => f.Name == job + ".bbl")) files.Add(new(bbl.Name, bbl.Bytes.ToArray()));
                map = Bytes("modeled map " + r.Tag.Ordinal);
                if (engine == NativeCompilerEngine.LuaLaTeX) { files.Add(new(pdf, Bytes("%PDF-modeled graphics Lua"))); geo = Sidecar.ToArray(); }
                else files.Add(new(job + ext, [247, engine == NativeCompilerEngine.XeLaTeX ? (byte)7 : (byte)2, checked((byte)r.Tag.Ordinal)]));
            }
            var reply = new NativePhaseReply(r.Tag, "native-return", 0, true, "modeled graphics phase\n", files.ToArray(),
                r.Files.Select(f => new NativeInputFact(f.Name, f.Bytes.Length, NativeRuntimePackage.Digest(f.Bytes).Hex)).ToArray(),
                WasmSha256:NativeRuntimePackage.Digest(r.Wasm).Hex, LoaderSha256:NativeRuntimePackage.Digest(r.Loader).Hex, SourceMapPayload:map) { SourceMapGeometryPayload = geo };
            reply = Mutate?.Invoke(r, reply) ?? reply;
            if (Hold && (convert || engine == NativeCompilerEngine.LuaLaTeX && r.Tag.Ordinal == 2)) { Pending = reply; Entered.TrySetResult(); return Release.Task; }
            return Task.FromResult(reply);
        }
        public void RequestTermination(string job, long epoch) => Stops++;
    }
    private static (NativeCompilationWorkerFactory Factory, ICompilationWorker Worker, CompileJobId Id, Task<CompileOutputEnvelopeInput?> Operation) Start(Model m, CompileRequestContext c, NativeCompilerEngine e)
    {
        var factory = new NativeCompilationWorkerFactory(new FixedNativeRuntimeProvider(Package), m, NativeCompilerSuiteContract.CreatePlan(e, profile:P));
        var id = new CompileJobId(Guid.NewGuid()); var worker = Assert.IsAssignableFrom<ICompilationWorker>(factory.Create(id, 1));
        return (factory, worker, id, Assert.IsAssignableFrom<Task<CompileOutputEnvelopeInput?>>(worker.ExecuteAsync(id, c, Budget, default)));
    }
    [GraphicsFixtureTheory]
    [InlineData(NativeCompilerEngine.LaTeXDvi, false)] [InlineData(NativeCompilerEngine.LaTeXDvi, true)]
    [InlineData(NativeCompilerEngine.XeLaTeX, false)] [InlineData(NativeCompilerEngine.XeLaTeX, true)]
    [InlineData(NativeCompilerEngine.LuaLaTeX, false)] [InlineData(NativeCompilerEngine.LuaLaTeX, true)]
    public void Final_map_PDF_geometry_bind_to_final_LaTeX_not_converter(NativeCompilerEngine e, bool bib) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); Prepare(f, bib); var c = await Context(f, e, bib); var m = new Model(e, bib); var run = Start(m, c, e);
        var result = Assert.IsType<CompileOutputEnvelopeInput>(await run.Operation); Assert.Equal(CompileOutputStatus.Succeeded, result.Status);
        var final = m.Tags.Last(t => t.Tool == "latex"); Assert.Equal(bib ? 4 : 2, final.Ordinal); Assert.Equal(bib ? 1 : 0, m.Tags.Count(t => t.Tool == "bibtex"));
        Assert.Equal(Bytes("modeled map " + final.Ordinal), result.SourceMapPayload);
        var wrapper = Assert.IsType<SourceMapGeometryArtifact>(SourceMapGeometryCodec.ParseWrapper(result.SourceMapGeometryPayload).Artifact);
        Assert.Equal((uint)final.Ordinal, wrapper.Geometry.FinalPass); Assert.Equal(NativeCompilerSuiteContract.EngineId(e), wrapper.Geometry.EngineId);
        Assert.Equal(NativeRuntimePackage.Digest(result.Payload!).Hex, wrapper.Geometry.PdfSha256); Assert.Equal(NativeRuntimePackage.Digest(result.SourceMapPayload!).Hex, wrapper.Geometry.MapSha256);
        if (e != NativeCompilerEngine.LuaLaTeX) { Assert.Equal(final.Ordinal + 1, m.Tags[^1].Ordinal); Assert.Equal(final.Ordinal, m.FinalIntermediate![2]); Assert.Equal(NativeRuntimePackage.Digest(m.FinalIntermediate).Hex, wrapper.Geometry.IntermediateSha256); }
        var accepted = CompileOutputEnvelope.Create(result, Budget).Envelope!.TryAccept(c).Output!; Assert.Equal(2, accepted.ContentIdentity!.ArtifactVersion);
        Assert.Equal(Package.AdapterFor(e), accepted.OutputAdapter); Assert.True(f.Workspace.EditText(f.Main, "changed")); Assert.Null(CompileOutputEnvelope.Create(result, Budget).Envelope!.TryAccept(await Context(f,e,bib)).Output);
    });
    [GraphicsFixtureTheory]
    [InlineData(NativeCompilerEngine.LaTeXDvi)] [InlineData(NativeCompilerEngine.XeLaTeX)] [InlineData(NativeCompilerEngine.LuaLaTeX)]
    public void Missing_optional_map_keeps_PDF_with_fixed_limitation(NativeCompilerEngine e) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); Prepare(f,false); var m = new Model(e,false) { Mutate = (r,s) => r.Tag.Tool == "latex" ? s with { SourceMapPayload = null } : s };
        var result = Assert.IsType<CompileOutputEnvelopeInput>(await Start(m, await Context(f,e,false),e).Operation);
        Assert.Equal(CompileOutputStatus.Succeeded,result.Status); Assert.Equal(2,m.Tags.Count(t => t.Tool == "latex")); Assert.NotNull(result.Payload); Assert.Null(result.SourceMapPayload); Assert.Null(result.SourceMapGeometryPayload);
        Assert.Contains(result.Diagnostics!,d => d.Code == NativeNavigationContract.LimitationCode);
    });
    [GraphicsFixtureTheory]
    [InlineData("old-phase")] [InlineData("tag")] [InlineData("module")] [InlineData("failure-data")] [InlineData("bib-map")]
    public void Actual_target_phase_is_reached_and_forged_or_wrong_cell_data_rejects(string mode) => LifecycleTestHost.Run(async () =>
    {
        const NativeCompilerEngine e = NativeCompilerEngine.XeLaTeX; bool bib = mode == "bib-map";
        await using var f = await CompilationTestWorkspace.Open(); Prepare(f,bib); var m = new Model(e,bib) { Mutate = (r,s) =>
            mode == "bib-map" ? r.Tag.Tool == "bibtex" ? s with { SourceMapPayload = [1] } : s
            : r.Tag.Tool != "dvipdfmx" ? s : mode switch {
                "old-phase" => s with { Tag = s.Tag with { Version = 13 } }, "tag" => s with { Tag = s.Tag with { Nonce = "old" } },
                "module" => s with { WasmSha256 = NativeCompilerXeNavigationPins.ConverterWasmHash }, _ => s with { NativeReturn = 1 } } };
        var result = Assert.IsType<CompileOutputEnvelopeInput>(await Start(m,await Context(f,e,bib),e).Operation);
        Assert.Equal(bib ? "bibtex" : "dvipdfmx",m.Tags.Last().Tool); Assert.Equal(CompileOutputStatus.Failed,result.Status);
        Assert.Equal(bib ? "invalid-native-reply" : "invalid-dvi-reply",Assert.Single(result.Diagnostics!).Code);
        Assert.Null(result.Payload); Assert.Null(result.SourceMapPayload); Assert.Null(result.SourceMapGeometryPayload);
    });
    [GraphicsFixtureTheory]
    [InlineData(NativeCompilerEngine.LaTeXDvi)] [InlineData(NativeCompilerEngine.XeLaTeX)] [InlineData(NativeCompilerEngine.LuaLaTeX)]
    public void Cancellation_keeps_actual_slot_until_late_native_settlement(NativeCompilerEngine e) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); Prepare(f,false); var m = new Model(e,false) { Hold = true }; var run = Start(m,await Context(f,e,false),e);
        await Task.WhenAny(run.Operation,m.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(run.Operation.IsCompleted,"Must reach intended held final phase");
        run.Worker.RequestTermination(run.Id,1,CompileLifecycleReason.Cancelled); var result = Assert.IsType<CompileOutputEnvelopeInput>(await run.Operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(CompileOutputStatus.Cancelled,result.Status); Assert.Null(result.SourceMapPayload); Assert.Null(result.SourceMapGeometryPayload); Assert.Equal(1,m.Stops);
        Assert.Equal(1,run.Factory.Operations.Active); Assert.False(run.Factory.Operations.WhenIdle.IsCompleted);
        m.Release.SetResult(Assert.IsType<NativePhaseReply>(m.Pending)); await run.Factory.Operations.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0,run.Factory.Operations.Active);
    });
}
public sealed class GraphicsFixtureFactAttribute : FactAttribute
{ public GraphicsFixtureFactAttribute() { if (!File.Exists(Path.Combine(GraphicsTransportTests.Root ?? "", "pins.json"))) Skip = "Requires exact complete P10 GRAPHICS_NAVIGATION_RUNTIME_DIR; modeled replies, not native proof."; } }
public sealed class GraphicsFixtureTheoryAttribute : TheoryAttribute
{ public GraphicsFixtureTheoryAttribute() { if (!File.Exists(Path.Combine(GraphicsTransportTests.Root ?? "", "pins.json"))) Skip = "Requires exact complete P10 GRAPHICS_NAVIGATION_RUNTIME_DIR; modeled replies, not native proof."; } }
