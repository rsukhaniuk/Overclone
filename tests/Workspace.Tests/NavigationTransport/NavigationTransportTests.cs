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

namespace Workspace.Tests.NavigationTransport;

// MODELED phase transport with actual complete provisioned P8 package bytes.
// These checks are not native execution, browser isolation or rendered navigation.
public sealed class NavigationTransportTests(ITestOutputHelper output)
{
    private const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalCompilerNavigation;
    private static readonly CompileOutputBudget Budget = new(8388608, 65536, 32, 128, 4096, 8192);
    internal static string? Root => Environment.GetEnvironmentVariable("NAVIGATION_RUNTIME_DIR");
    private static readonly Lazy<NativeRuntimePackage> Fixture = new(() =>
    {
        var pins = LocalRuntimeSelection.ParsePins(File.ReadAllBytes(Path.Combine(Root!, "pins.json"))).ToArray();
        Assert.Equal(Profile, LocalRuntimeSelection.ProfileFor(pins));
        var inputs = pins.Select(p => new NativeResourceInput(p.Name, File.ReadAllBytes(Path.Combine(Root!, "files", p.Name)))).ToArray();
        return Assert.IsType<NativeRuntimePackage>(NativeRuntimePackage.Create(inputs, pins, LocalRuntimeSelection.LimitsFor(Profile), Profile).Package);
    });
    private static NativeRuntimePackage Package => Fixture.Value;
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static readonly byte[] Sidecar = Bytes("{\"schemaVersion\":1,\"pages\":[{\"page\":1,\"status\":\"available\",\"reason\":\"none\",\"writerToPdfRaw\":[1,0,0,-1,7,800],\"writerPageBoxBp\":[0,0,600,800]}]}");

    [Fact]
    public void Closed_profile_aware_phases_do_not_reinterpret_old_overloads()
    {
        Assert.Equal(new[] { 6, 7, 8, 9 }, Enum.GetValues<NativeCompilerEngine>().Select(NativeCompilerSuiteContract.Phase));
        Assert.Equal(new[] { 6, 12, 8, 14 }, Enum.GetValues<NativeCompilerEngine>().Select(e => NativeCompilerSuiteContract.Phase(Profile, e)));
        foreach (var e in Enum.GetValues<NativeCompilerEngine>())
        {
            var p = NativeCompilerSuiteContract.CreatePlan(e, profile: Profile); Assert.True(p.IsValid);
            Assert.True(NativeCompilerSuiteContract.MatchesOptions(NativeCompilerSuiteContract.SelectionOptions(Profile, e), Profile, e));
            foreach (int bad in new[] { 0, 1, 7, 9, 10, 11, 13, 15, 99 })
                Assert.False((p with { PhaseProtocolVersion = bad }).IsValid);
            Assert.False((p with { Profile = (NativeRuntimeProfile)10 }).IsValid);
            if (e is NativeCompilerEngine.LaTeXDvi or NativeCompilerEngine.LuaLaTeX)
                Assert.False(NativeCompilerSuiteContract.MatchesOptions(NativeCompilerSuiteContract.SelectionOptions(e), Profile, e));
        }
        Assert.False(NativeRuntimeProfiles.Known((NativeRuntimeProfile)10));
        Assert.False(NativeRuntimeProfiles.Known((NativeRuntimeProfile)int.MaxValue));
        Assert.Equal(0, NativeCompilerSuiteContract.Phase((NativeRuntimeProfile)10, NativeCompilerEngine.LuaLaTeX));
        Assert.True(NativeRuntimeProfiles.HasBibliographyCarry(Profile));
        Assert.Equal(8, (int)Profile); Assert.Equal(7, (int)NativeRuntimeProfile.LocalCompilerBibliography);
    }

    [Fact]
    public void Actual_ABI_and_adapter_facts_are_distinct_not_old_Lua_or_converter_identity()
    {
        var lua = NativeNavigationContract.Component(NativeCompilerEngine.LuaLaTeX);
        Assert.Equal(5, lua.NativeAbiVersion); Assert.Equal("abi5", lua.EngineVersion);
        Assert.Equal(NativeCompilerNavigationPins.LuaWasmHash, lua.WasmHash);
        Assert.NotEqual(NativeCompilerBibliographyPins.LuaWasmHash, lua.WasmHash);
        var dvi = NativeNavigationContract.Component(NativeCompilerEngine.LaTeXDvi);
        Assert.Equal("dvi-map-engine.js", dvi.LoaderName); Assert.Equal(1, dvi.NativeAbiVersion);
        Assert.Equal("geometry-abi2", dvi.OutputAdapter.Version);
        Assert.Equal(NativeCompilerNavigationPins.ConverterWasmHash, dvi.OutputAdapter.Hash!.Value.Hex);
        Assert.False(NativeNavigationContract.Component(NativeCompilerEngine.XeLaTeX).SupportsSourceMap);
    }

    [NavigationFixtureFact]
    public void Full_trusted_closure_binds_all_components_and_private_resource_bytes()
    {
        var pins = LocalRuntimeSelection.ParsePins(File.ReadAllBytes(Path.Combine(Root!, "pins.json"))).ToArray();
        Assert.Equal(3707, pins.Length); Assert.Equal(191844694L, pins.Sum(p => (long)p.Length));
        Assert.True(NativeCompilerNavigationPins.MatchesComponents(pins.ToDictionary(p => p.Name, StringComparer.Ordinal)));
        var changed = pins.ToArray(); changed[0] = changed[0] with { Sha256 = new('0', 64) };
        Assert.Throws<FormatException>(() => LocalRuntimeSelection.ProfileFor(changed));
        var copy = Package.Copy("luatex.wasm"); copy.AsSpan().Fill(0);
        Assert.Equal(NativeCompilerNavigationPins.LuaWasmHash, NativeRuntimePackage.Digest(Package.Copy("luatex.wasm")).Hex);
        Assert.Equal("local-compiler-navigation", Package.Bundle.Id);
        Assert.Equal(NativeCompilerNavigationPins.DviWasmHash, Package.EngineFor(NativeCompilerEngine.LaTeXDvi).Hash.Hex);
        Assert.Equal("abi5", Package.EngineFor(NativeCompilerEngine.LuaLaTeX).Version);
    }

    private static void Prepare(CompilationTestWorkspace f, bool bib = false)
    {
        Assert.True(f.Workspace.RenameFile(f.Main, "тези/my thesis.tex"));
        Assert.Null(NativeSelectedEntryContract.ValidateEntry(f.Workspace.Files.Single(file => file.Id == f.Main).RelativePath));
        if (bib) Assert.True(f.Workspace.CreateFile("references.bib", "@book{entry,author={Synthetic Author},title={Model},year={2025}}"));
    }

    private static async Task<CompileRequestContext> Context(CompilationTestWorkspace f, NativeCompilerEngine engine, bool bib = false)
    {
        f.Selection.SetSelection(CompilationSelection.Create(new(f.Main, Package.EngineFor(engine), [Package.Bundle],
            bib ? [Package.Bibtex] : [], NativeCompilerSuiteContract.SelectionOptions(Profile, engine)), 65536).Selection!);
        return CompileRequestContext.Create(await f.Snapshot(new(64, 8388608, 16777216, 131072)), 1, 65536,
            NativeRuntimePackage.Pdf, Package.AdapterFor(engine)).Context!;
    }
    private sealed class Model(NativeCompilerEngine engine, bool useBibtex = false) : INativePhaseTransport
    {
        public NativeCompilerEngine Engine { get; } = engine;
        public bool UseBibtex { get; } = useBibtex;
        public List<int> LatexBblOrdinals { get; } = [];
        private static byte[] Bbl() => Bytes("\\begin{thebibliography}{1}\n\\bibitem{entry} Synthetic Author. Model. 2025.\n\\end{thebibliography}\n");
        public List<NativePhaseTag> Tags { get; } = [];
        public List<string> LoaderHashes { get; } = [];
        public byte[]? SelectedDvi, LastMap, LastSidecar;
        public Func<NativePhaseRequest, NativePhaseReply, NativePhaseReply>? Mutate;
        public TaskCompletionSource<NativePhaseReply>? Held;
        public NativePhaseReply? HeldReply;
        public TaskCompletionSource ConversionEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Stops;
        public Task<NativePhaseReply> ExecuteAsync(NativePhaseRequest r)
        {
            Tags.Add(r.Tag); LoaderHashes.Add(NativeRuntimePackage.Digest(r.Loader).Hex);
            var pdfName = r.Outputs.Single(n => n.EndsWith(".pdf", StringComparison.Ordinal)); var job = pdfName[..^4];
            bool conversion = r.Tag.Tool == "dvipdfmx", bib = r.Tag.Tool == "bibtex";
            List<NativeFileTransfer> files = [];
            byte[]? map = null, geometry = null;
            if (conversion)
            {
                SelectedDvi = r.Files.Single(f => f.Name == job + ".dvi").Bytes.ToArray();
                files.Add(new(pdfName, Bytes("%PDF-modeled-conversion"))); geometry = Sidecar.ToArray();
            }
            else if (bib)
            {
                // Exercise the existing literal AUX/BIB/BST admission, not an
                // arbitrary byte pretending to be bibliography control state.
                Assert.True(UseBibtex);
                Assert.Equal(Bytes(BibliographyPipelineFixture.Aux(job, biblatex: false)), r.Files.Single(f => f.Name == job + ".aux").Bytes);
                Assert.Contains(r.Files, f => f.Name == "references.bib");
                Assert.Contains("__oop_runtime__/plain.bst", r.RuntimeResourceNames!);
                files.Add(new(job + ".bbl", Bbl()));
            }
            else
            {
                files.Add(new(job + ".aux", Bytes(UseBibtex ? BibliographyPipelineFixture.Aux(job, biblatex: false) : "\\relax\n")));
                foreach (var bbl in r.Files.Where(f => f.Name == job + ".bbl"))
                {
                    Assert.Equal(Bbl(), bbl.Bytes); LatexBblOrdinals.Add(r.Tag.Ordinal);
                    files.Add(new(bbl.Name, bbl.Bytes.ToArray()));
                }
                if (Engine == NativeCompilerEngine.LaTeXDvi) files.Add(new(job + ".dvi", [247, 2, checked((byte)r.Tag.Ordinal)]));
                else files.Add(new(pdfName, Bytes("%PDF-modeled-Lua")));
                map = Bytes("modeled raw map ordinal " + r.Tag.Ordinal);
                if (Engine == NativeCompilerEngine.LuaLaTeX) geometry = Sidecar.ToArray();
            }
            LastMap = map ?? LastMap; LastSidecar = geometry ?? LastSidecar;
            var reply = new NativePhaseReply(r.Tag, "native-return", 0, true, "modeled pass\n", files.ToArray(),
                r.Files.Select(f => new NativeInputFact(f.Name, f.Bytes.Length, NativeRuntimePackage.Digest(f.Bytes).Hex)).ToArray(),
                WasmSha256: NativeRuntimePackage.Digest(r.Wasm).Hex, LoaderSha256: NativeRuntimePackage.Digest(r.Loader).Hex, SourceMapPayload: map)
                { SourceMapGeometryPayload = geometry };
            reply = Mutate?.Invoke(r, reply) ?? reply;
            if (conversion && Held is not null) { HeldReply = reply; ConversionEntered.TrySetResult(); return Held.Task; }
            return Task.FromResult(reply);
        }
        public void RequestTermination(string job, long epoch) { Stops++; }
    }
    private static NativeCompilationWorkerFactory Factory(Model model) => new(new FixedNativeRuntimeProvider(Package), model,
        NativeCompilerSuiteContract.CreatePlan(model.Engine, profile: Profile));

    private void TraceResult(CompileOutputEnvelopeInput result, Model model)
    {
        output.WriteLine($"status={result.Status}; phases={string.Join(",", model.Tags.Select(t => $"{t.Tool}:{t.Ordinal}"))}");
        output.WriteLine("diagnostics=" + JsonSerializer.Serialize(result.Diagnostics));
        output.WriteLine("log=" + result.LogText);
    }

    [NavigationFixtureTheory]
    [InlineData(NativeCompilerEngine.LaTeXDvi, false)] [InlineData(NativeCompilerEngine.LaTeXDvi, true)]
    [InlineData(NativeCompilerEngine.LuaLaTeX, false)] [InlineData(NativeCompilerEngine.LuaLaTeX, true)]
    public void Only_final_successful_LaTeX_ordinal_and_owned_intermediate_define_wrapper(NativeCompilerEngine engine, bool bib) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); Prepare(f, bib);
        var context = await Context(f, engine, bib); var model = new Model(engine, bib); var factory = Factory(model);
        factory.PhaseObserved += o => { if (o.Reply.SourceMapPayload is { } map) map.AsSpan().Fill(0); if (o.Reply.SourceMapGeometryPayload is { } geometry) geometry.AsSpan().Fill(0); };
        var id = new CompileJobId(Guid.NewGuid());
        var worker = Assert.IsAssignableFrom<ICompilationWorker>(factory.Create(id, 1));
        var operation = Assert.IsAssignableFrom<Task<CompileOutputEnvelopeInput?>>(worker.ExecuteAsync(id, context, Budget, default));
        var result = Assert.IsType<CompileOutputEnvelopeInput>(await operation);
        TraceResult(result, model);
        Assert.Equal(CompileOutputStatus.Succeeded, result.Status);
        Assert.Equal(bib ? 3 : 2, model.Tags.Count(t => t.Tool == "latex"));
        var final = model.Tags.Last(t => t.Tool == "latex");
        Assert.Equal(bib ? 4 : 2, final.Ordinal);
        Assert.Equal(bib ? new[] { 3, 4 } : Array.Empty<int>(), model.LatexBblOrdinals);
        Assert.Equal(Bytes("modeled raw map ordinal " + final.Ordinal), result.SourceMapPayload);
        var wrapper = SourceMapGeometryCodec.ParseWrapper(result.SourceMapGeometryPayload).Artifact!;
        Assert.Equal((uint)final.Ordinal, wrapper.Geometry.FinalPass);
        Assert.Equal(NativeRuntimePackage.Digest(result.Payload!).Hex, wrapper.Geometry.PdfSha256);
        Assert.Equal(NativeRuntimePackage.Digest(result.SourceMapPayload!).Hex, wrapper.Geometry.MapSha256);
        if (engine == NativeCompilerEngine.LaTeXDvi)
        {
            Assert.True(model.Tags[^1].Ordinal > final.Ordinal); Assert.Equal(final.Ordinal, model.SelectedDvi![2]);
            Assert.Equal(NativeRuntimePackage.Digest(model.SelectedDvi).Hex, wrapper.Geometry.IntermediateSha256);
            Assert.Equal(NativeCompilerNavigationPins.DviLoaderHash, model.LoaderHashes[0]);
            Assert.Equal(NativeCompilerNavigationPins.ConverterLoaderHash, model.LoaderHashes[^1]);
        }
        if (bib) Assert.Equal(NativeRuntimeProfiles.SyncTeXCompilerLoaderHash, model.LoaderHashes[1]);
        model.LastMap!.AsSpan().Fill(0); model.LastSidecar!.AsSpan().Fill(0);
        Assert.Equal(wrapper.Identity, SourceMapGeometryCodec.ParseWrapper(result.SourceMapGeometryPayload).Artifact!.Identity);
        var envelope = CompileOutputEnvelope.Create(result, Budget).Envelope!;
        var accepted = envelope.TryAccept(context).Output!; Assert.Equal(2, accepted.ContentIdentity!.ArtifactVersion);
        Assert.Equal(wrapper.Identity, accepted.SourceMapGeometry);
        Assert.True(f.Workspace.EditText(f.Main, "changed")); var newer = await Context(f, engine, bib);
        Assert.Null(envelope.TryAccept(newer).Output);
    });

    [NavigationFixtureTheory]
    [InlineData(NativeCompilerEngine.LaTeXDvi, 0)] [InlineData(NativeCompilerEngine.LaTeXDvi, 1)]
    [InlineData(NativeCompilerEngine.LaTeXDvi, 2)] [InlineData(NativeCompilerEngine.LaTeXDvi, 3)] [InlineData(NativeCompilerEngine.LaTeXDvi, 4)]
    [InlineData(NativeCompilerEngine.LuaLaTeX, 0)] [InlineData(NativeCompilerEngine.LuaLaTeX, 1)]
    [InlineData(NativeCompilerEngine.LuaLaTeX, 2)] [InlineData(NativeCompilerEngine.LuaLaTeX, 3)] [InlineData(NativeCompilerEngine.LuaLaTeX, 4)]
    public void Optional_geometry_or_map_failure_preserves_PDF_with_fixed_limitation(NativeCompilerEngine engine, int mode) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); Prepare(f); var context = await Context(f, engine);
        var model = new Model(engine) { Mutate = (r, reply) => mode == 4 && r.Tag.Tool == "latex"
            ? reply with { SourceMapPayload = null }
            : !(r.Tag.Tool == "dvipdfmx" || engine == NativeCompilerEngine.LuaLaTeX && r.Tag.Tool == "latex") ? reply : reply with { SourceMapGeometryPayload = mode switch
                { 0 => null, 1 => Bytes("{}"), 2 => new byte[131073], 3 => Bytes("{\"schemaVersion\":1,\"pages\":[]}"), _ => Sidecar } } };
        var factory = Factory(model); var id = new CompileJobId(Guid.NewGuid());
        var worker = Assert.IsAssignableFrom<ICompilationWorker>(factory.Create(id, 1));
        var operation = Assert.IsAssignableFrom<Task<CompileOutputEnvelopeInput?>>(worker.ExecuteAsync(id, context, Budget, default));
        var result = Assert.IsType<CompileOutputEnvelopeInput>(await operation);
        TraceResult(result, model);
        Assert.Equal(CompileOutputStatus.Succeeded, result.Status); Assert.NotNull(result.Payload);
        Assert.Null(result.SourceMapGeometryPayload); Assert.Null(result.SourceMapPayload);
        Assert.Contains(result.Diagnostics!, d => d.Code == NativeNavigationContract.LimitationCode && d.SafeMessage == NativeNavigationContract.LimitationMessage);
        Assert.NotNull(CompileOutputEnvelope.Create(result, Budget).Envelope!.TryAccept(context).Output);
    });

    [NavigationFixtureTheory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void Forged_input_token_module_or_failed_conversion_never_publishes_mapping(int mode) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); Prepare(f); var context = await Context(f, NativeCompilerEngine.LaTeXDvi);
        var model = new Model(NativeCompilerEngine.LaTeXDvi) { Mutate = (r, reply) => r.Tag.Tool != "dvipdfmx" ? reply : mode switch
        {
            0 => reply with { Tag = reply.Tag with { Ordinal = reply.Tag.Ordinal - 1 } },
            1 => reply with { Inputs = reply.Inputs.Select(i => i.Name.EndsWith(".dvi", StringComparison.Ordinal) ? i with { Sha256 = new('0', 64) } : i).ToArray() },
            2 => reply with { LoaderSha256 = NativeCompilerSuitePins.ConverterLoaderHash },
            _ => reply with { NativeReturn = 1, SourceMapGeometryPayload = null }
        } };
        var factory = Factory(model); var id = new CompileJobId(Guid.NewGuid());
        var worker = Assert.IsAssignableFrom<ICompilationWorker>(factory.Create(id, 1));
        var operation = Assert.IsAssignableFrom<Task<CompileOutputEnvelopeInput?>>(worker.ExecuteAsync(id, context, Budget, default));
        var result = Assert.IsType<CompileOutputEnvelopeInput>(await operation);
        TraceResult(result, model);
        Assert.NotEqual(CompileOutputStatus.Succeeded, result.Status);
        Assert.Equal("dvipdfmx", model.Tags.Last().Tool);
        Assert.Equal(mode == 3 ? "native-dvi-conversion-failed" : "invalid-dvi-reply", Assert.Single(result.Diagnostics!).Code);
        Assert.Null(result.SourceMapPayload); Assert.Null(result.SourceMapGeometryPayload); Assert.Null(result.Payload);
    });

    [NavigationFixtureTheory]
    [InlineData("root")] [InlineData("aux")]
    public void Invalid_fixture_root_and_missing_bibliography_records_remain_rejected(string invalid) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open();
        if (invalid == "aux") Prepare(f, bib: true);
        var context = await Context(f, NativeCompilerEngine.LaTeXDvi, bib: invalid == "aux");
        var model = new Model(NativeCompilerEngine.LaTeXDvi, useBibtex: invalid == "aux")
        {
            Mutate = (r, reply) => invalid != "aux" ? reply : reply with
            {
                Files = reply.Files.Select(file => file.Name.EndsWith(".aux", StringComparison.Ordinal) ? file with { Bytes = [1] } : file).ToArray()
            }
        };
        var factory = Factory(model); var id = new CompileJobId(Guid.NewGuid());
        var worker = Assert.IsAssignableFrom<ICompilationWorker>(factory.Create(id, 1));
        var operation = Assert.IsAssignableFrom<Task<CompileOutputEnvelopeInput?>>(worker.ExecuteAsync(id, context, Budget, default));
        var result = Assert.IsType<CompileOutputEnvelopeInput>(await operation);
        TraceResult(result, model);
        Assert.Equal(invalid == "root" ? CompileOutputStatus.Unsupported : CompileOutputStatus.Failed, result.Status);
        Assert.Equal(invalid == "root" ? "selected-entry-extension" : "missing-bibliography-control", Assert.Single(result.Diagnostics!).Code);
        Assert.Equal(invalid == "root" ? 0 : 1, model.Tags.Count);
        Assert.DoesNotContain(model.Tags, tag => tag.Tool == "bibtex" || tag.Tool == "dvipdfmx");
        Assert.Null(result.Payload); Assert.Null(result.SourceMapPayload); Assert.Null(result.SourceMapGeometryPayload);
    });

    [NavigationFixtureFact]
    public void Cancelled_conversion_retires_actual_foreign_call_before_slot_release() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); Prepare(f); var context = await Context(f, NativeCompilerEngine.LaTeXDvi);
        var model = new Model(NativeCompilerEngine.LaTeXDvi) { Held = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var factory = Factory(model); var id = new CompileJobId(Guid.NewGuid());
        var worker = Assert.IsAssignableFrom<ICompilationWorker>(factory.Create(id, 1));
        var operation = Assert.IsAssignableFrom<Task<CompileOutputEnvelopeInput?>>(worker.ExecuteAsync(id, context, Budget, default));
        await Task.WhenAny(operation, model.ConversionEntered.Task).WaitAsync(TimeSpan.FromSeconds(5));
        if (operation.IsCompleted) TraceResult(Assert.IsType<CompileOutputEnvelopeInput>(await operation), model);
        Assert.False(operation.IsCompleted, "Compilation completed before the modeled conversion hold was reached.");
        await model.ConversionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        worker.RequestTermination(id, 1, CompileLifecycleReason.Cancelled);
        var result = Assert.IsType<CompileOutputEnvelopeInput>(await operation.WaitAsync(TimeSpan.FromSeconds(5)));
        TraceResult(result, model);
        Assert.Equal(CompileOutputStatus.Cancelled, result.Status); Assert.Null(result.SourceMapGeometryPayload);
        Assert.Equal(1, model.Stops); Assert.Equal(1, factory.Operations.Active); Assert.False(factory.Operations.WhenIdle.IsCompleted);
        model.Held.SetResult(model.HeldReply!); await factory.Operations.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, factory.Operations.Active); Assert.Null(result.SourceMapPayload);
    });
}
public sealed class NavigationFixtureFactAttribute : FactAttribute
{
    public NavigationFixtureFactAttribute() { if (!Directory.Exists(NavigationTransportTests.Root)) Skip = "Set NAVIGATION_RUNTIME_DIR to exact staged P8 bytes; not native/browser proof."; }
}
public sealed class NavigationFixtureTheoryAttribute : TheoryAttribute
{
    public NavigationFixtureTheoryAttribute() { if (!Directory.Exists(NavigationTransportTests.Root)) Skip = "Set NAVIGATION_RUNTIME_DIR to exact staged P8 bytes; not native/browser proof."; }
}
