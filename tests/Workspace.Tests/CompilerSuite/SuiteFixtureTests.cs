using System.Text.Json;
using Client.Compilation;
using Client.Compilation.Lifecycle;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Workspace.Tests.CompilationLifecycle;
using Xunit;

namespace Workspace.Tests.CompilerSuite;

// Genuine pinned input bytes, MODELED phase replies. Not native/browser acceptance.
public sealed class SuiteFixtureTests
{
    private const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalCompilerSuite;
    [SuiteFixtureFact]
    public async Task Actual_suite_ZIP_preserves_qualified_paths_and_admits_the_genuine_large_Xe_format()
    {
        string file = Path.Combine(Environment.GetEnvironmentVariable("COMPILER_SUITE_RUNTIME_DIR")!, "compiler-suite.zip");
        await using var archive = new ZipRuntimeArchive();
        await archive.PrepareAsync(new FileInfo(file).Length,
            _ => Task.FromResult<Stream>(File.OpenRead(file)), default);
        Assert.Equal(NativeCompilerSuitePins.ResourceCount, archive.Files.Count);
        Assert.Equal(1, archive.PinsStreamsOpened);
        Assert.Equal(0, archive.ResourceStreamsOpened);
        Assert.Contains(archive.Files, f => f.Name.Contains('/', StringComparison.Ordinal));
        var format = archive.Files.Single(f => f.Name == "xelatex.fmt");
        var bytes = await format.Read(default);
        Assert.Equal(NativeCompilerSuitePins.XeFormatLength, bytes.Length);
        Assert.Equal(NativeCompilerSuitePins.XeFormatHash, NativeRuntimePackage.Digest(bytes).Hex);
    }
    [SuiteFixtureFact]
    public async Task Actual_suite_ZIP_installs_and_rechecks_with_a_modeled_storage_backend()
    {
        string file = Path.Combine(Environment.GetEnvironmentVariable("COMPILER_SUITE_RUNTIME_DIR")!, "compiler-suite.zip");
        var storage = new Workspace.Tests.CompilationNative.SetupBackend();
        var selection = new Workspace.Tests.CompilationNative.SetupSelection();
        await using var setup = new NativeRuntimeSetup(new(storage), selection);
        await using var session = new ZipRuntimeSetupSession(setup);
        await session.InstallZipAsync(new FileInfo(file).Length, _ => Task.FromResult<Stream>(File.OpenRead(file)));
        Assert.True(session.State == RuntimeSetupState.Installed,
            $"State={session.State}; completed={setup.Completed}; storage calls={storage.Calls}");
        Assert.Equal(Profile, LocalRuntimeSelection.Decode(selection.Value!).Profile);
        await session.RecheckAsync();
        Assert.Equal(RuntimeSetupState.Installed, session.State);
    }
    private static readonly CompileOutputBudget Budget = new(8388608, 65536, 32, 128, 4096, 8192);
    private static readonly Lazy<(NativeResourcePin[] Pins, NativeRuntimePackage Package)> Fixture = new(() =>
    {
        string root = Environment.GetEnvironmentVariable("COMPILER_SUITE_RUNTIME_DIR")!;
        var pins = LocalRuntimeSelection.ParsePins(File.ReadAllBytes(Path.Combine(root, "pins.json"))).ToArray();
        var input = pins.Select(p => new NativeResourceInput(p.Name, File.ReadAllBytes(Path.Combine(root, "files", p.Name)))).ToArray();
        return (pins, Assert.IsType<NativeRuntimePackage>(NativeRuntimePackage.Create(input, pins, LocalRuntimeSelection.LimitsFor(Profile), Profile).Package));
    });
    private static NativeRuntimePackage Package => Fixture.Value.Package;
    [SuiteFixtureFact]
    public void Complete_inventory_metadata_and_tamper_preflight_bind_the_trusted_closure()
    {
        var (pins, package) = Fixture.Value;
        Assert.Equal(NativeCompilerSuitePins.ResourceCount, pins.Length); Assert.Equal(NativeCompilerSuitePins.TotalBytes, pins.Sum(p => p.Length));
        Assert.Equal(Profile, LocalRuntimeSelection.ProfileFor(pins)); Assert.Equal(Profile, package.Profile);
        var resources = LocalRuntimeSelection.Resources([.. pins]); var inventory = LocalRuntimeSelection.Inventory(resources);
        var mapping = new InstalledNativeRuntimeMapping(Guid.NewGuid(), inventory.Identity.Digest.Hex, resources, Profile);
        var text = LocalRuntimeSelection.Encode(mapping); Assert.Equal(text, LocalRuntimeSelection.Encode(LocalRuntimeSelection.Decode(text)));
        Assert.True(LocalRuntimeSelection.BudgetFor(Profile).IsValid);
        foreach (var changed in new[]
        {
            pins.Select((p,i) => i == 0 ? p with { Sha256 = new('0',64) } : p).ToArray(),
            pins.Select((p,i) => i == 0 ? p with { Name = "../escape.lua" } : p).ToArray(),
            pins.Select((p,i) => i == 0 ? p with { Length = 67108865 } : p).ToArray(),
            pins.Select((p,i) => i == 0 ? p with { Name = NativeRuntimePackage.NativeFormatName } : p).ToArray()
        })
        {
            Assert.Throws<FormatException>(() => LocalRuntimeSelection.ParsePins(JsonSerializer.SerializeToUtf8Bytes(changed, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })));
            Assert.Null(NativeRuntimePackage.Create(new SuiteContractTests.NoAccess(pins.Length), changed, LocalRuntimeSelection.LimitsFor(Profile), Profile).Package);
        }
        var input = pins.Select(p => new NativeResourceInput(p.Name, package.Copy(p.Name))).ToArray();
        input[0].Bytes[0] ^= 1;
        Assert.Null(NativeRuntimePackage.Create(input, pins, LocalRuntimeSelection.LimitsFor(Profile), Profile).Package);
    }
    private static async Task<CompileRequestContext> Context(CompilationTestWorkspace f, NativeCompilerEngine engine, bool bib)
    {
        f.Selection.SetSelection(CompilationSelection.Create(new(f.Main, Package.EngineFor(engine), [Package.Bundle], bib ? [Package.Bibtex] : [],
            NativeCompilerSuiteContract.SelectionOptions(engine)), 65536).Selection!);
        return CompileRequestContext.Create(await f.Snapshot(new(64, 8388608, 16777216, 131072)), 1, 65536,
            NativeRuntimePackage.Pdf, Package.AdapterFor(engine)).Context!;
    }
    private static NativeCompilationWorkerFactory Factory(Model transport, NativeCompilerEngine engine) => new(new FixedNativeRuntimeProvider(Package), transport,
        NativeCompilerSuiteContract.CreatePlan(engine));
    private static async Task<CompileOutputEnvelopeInput> Execute(CompileRequestContext context, Model model, NativeCompilerEngine engine)
    {
        var factory = Factory(model, engine); var id = new CompileJobId(Guid.NewGuid());
        var result = await factory.Create(id, 1).ExecuteAsync(id, context, Budget, default)!.WaitAsync(TimeSpan.FromSeconds(30));
        await factory.Operations.WhenIdle; return Assert.IsType<CompileOutputEnvelopeInput>(result);
    }
    private sealed class Model : INativePhaseTransport
    {
        public List<NativePhaseRequest> Calls { get; } = [];
        public Func<NativePhaseRequest, NativePhaseReply, NativePhaseReply>? Mutate;
        public TaskCompletionSource<NativePhaseReply>? Hold;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Stops;
        public Task<NativePhaseReply> ExecuteAsync(NativePhaseRequest request)
        {
            Calls.Add(request); var reply = Reply(request);
            if (Hold is not null && request.Tag.Ordinal == 2) { Entered.TrySetResult(); return Hold.Task; }
            return Task.FromResult(Mutate?.Invoke(request, reply) ?? reply);
        }
        public NativePhaseReply Reply(NativePhaseRequest r)
        {
            bool converter = r.Tag.Tool == "dvipdfmx", bib = r.Tag.Tool == "bibtex";
            bool xe = r.Tag.Version == 8, lua = r.Tag.Version == 9;
            string job = r.Outputs.Single(n => n.EndsWith(".pdf", StringComparison.Ordinal))[..^4];
            var files = new List<NativeFileTransfer>();
            if (converter) files.Add(new(job + ".pdf", "%PDF-modeled-converted"u8.ToArray()));
            else if (bib)
            {
                files.AddRange(r.Files.Where(f => r.GeneratedInputNames!.Contains(f.Name) && f.Name != job + ".bbl"));
                files.Add(new(job + ".bbl", [2]));
            }
            else
            {
                files.Add(new(job + ".aux", [1])); files.Add(new("chapters/part.aux", [3]));
                files.AddRange(r.Files.Where(f => f.Name == job + ".bbl"));
                files.Add(xe ? new(job + ".xdv", [247, 7, (byte)Calls.Count])
                    : r.Tag.Version == 7 ? new(job + ".dvi", [247, 2, (byte)Calls.Count]) : new(job + ".pdf", "%PDF-modeled-engine"u8.ToArray()));
            }
            return new(r.Tag, "native-return", 0, true, "modeled phase", files.ToArray(),
                r.Files.Select(f => new NativeInputFact(f.Name, f.Bytes.Length, NativeRuntimePackage.Digest(f.Bytes).Hex)).ToArray(),
                WasmSha256: converter ? NativeCompilerSuitePins.ConverterWasmHash : bib ? NativeRuntimeProfiles.SyncTeXCompilerWasmHash
                    : xe ? NativeCompilerSuitePins.XeWasmHash : lua ? NativeCompilerSuitePins.LuaWasmHash : NativeRuntimeProfiles.SyncTeXCompilerWasmHash,
                LoaderSha256: converter ? NativeCompilerSuitePins.ConverterLoaderHash : bib ? NativeRuntimeProfiles.SyncTeXCompilerLoaderHash
                    : xe ? NativeCompilerSuitePins.XeLoaderHash : lua ? NativeCompilerSuitePins.LuaLoaderHash : NativeRuntimeProfiles.SyncTeXCompilerLoaderHash);
        }
        public void RequestTermination(string job, long epoch) { Stops++; }
    }
    [SuiteFixtureTheory]
    [InlineData(NativeCompilerEngine.PdfLaTeX, false)] [InlineData(NativeCompilerEngine.LaTeXDvi, false)]
    [InlineData(NativeCompilerEngine.XeLaTeX, false)] [InlineData(NativeCompilerEngine.XeLaTeX, true)]
    [InlineData(NativeCompilerEngine.LuaLaTeX, false)] [InlineData(NativeCompilerEngine.LuaLaTeX, true)]
    public void Finite_passes_role_projection_and_original_capture_are_preserved(NativeCompilerEngine engine, bool bib) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); Assert.True(f.Workspace.RenameFile(f.Main, "тези/my thesis.tex"));
        Assert.True(f.Workspace.CreateFile("chapters/part.tex", "part")); Assert.True(f.Workspace.CreateFile("geometry.sty", "project override"));
        var context = await Context(f, engine, bib); var t = new Model(); var result = await Execute(context, t, engine);
        Assert.Equal(CompileOutputStatus.Succeeded, result.Status); Assert.Null(result.SourceMapPayload);
        int latexCount = bib ? 3 : 2; bool converting = engine is NativeCompilerEngine.LaTeXDvi or NativeCompilerEngine.XeLaTeX;
        Assert.Equal(latexCount, t.Calls.Count(r => r.Tag.Tool == "latex")); Assert.Equal(bib ? 1 : 0, t.Calls.Count(r => r.Tag.Tool == "bibtex"));
        Assert.Equal(converting ? 1 : 0, t.Calls.Count(r => r.Tag.Tool == "dvipdfmx"));
        foreach (var request in t.Calls)
        {
            Assert.Equal(Profile, request.Tag.Profile); Assert.Equal(Package.Identity.Hex, request.Tag.RuntimeIdentity);
            Assert.Equal(NativeCompilerSuiteContract.Phase(engine), request.Tag.Version); Assert.Equal(NativeCompilerSuiteContract.EngineId(engine), request.Tag.EngineId);
            Assert.NotNull(request.RuntimeResourceNames); Assert.NotNull(request.GeneratedInputNames);
            Assert.All(request.RuntimeResourceNames!, n => Assert.StartsWith(NativeCompilerSuiteContract.ResourcePrefix, n));
            Assert.All(request.GeneratedInputNames!, n => Assert.DoesNotContain(n, request.RuntimeResourceNames!));
            Assert.Equal("тези/my thesis.tex", request.SelectedRoot);
            Assert.Equal("project override", System.Text.Encoding.UTF8.GetString(request.Files.Single(f => f.Name == "geometry.sty").Bytes));
            if (request.Tag.Tool == "dvipdfmx")
            {
                Assert.Empty(request.GeneratedInputNames!); Assert.Equal("my thesis" + (engine == NativeCompilerEngine.XeLaTeX ? ".xdv" : ".dvi"), request.MainEntry);
                Assert.Equal((byte)t.Calls[^2].Tag.Ordinal, request.Files.Single(f => f.Name == request.MainEntry).Bytes[2]);
                Assert.DoesNotContain(request.Files, f => f.Name.EndsWith(".fmt", StringComparison.Ordinal)); continue;
            }
            bool xe = engine == NativeCompilerEngine.XeLaTeX && request.Tag.Tool == "latex";
            bool lua = engine == NativeCompilerEngine.LuaLaTeX && request.Tag.Tool == "latex";
            string fmt = xe ? "xelatex.fmt" : lua ? "lualatex.fmt" : NativeRuntimePackage.NativeFormatName;
            Assert.Single(request.RuntimeResourceNames!, n => n.EndsWith(".fmt", StringComparison.Ordinal));
            Assert.Contains(NativeCompilerSuiteContract.ResourcePrefix + fmt, request.RuntimeResourceNames!);
            Assert.Equal(xe, request.RuntimeResourceNames!.Contains(NativeCompilerSuiteContract.ResourcePrefix + NativeCompilerSuitePins.IcuResourceName));
            Assert.Equal(xe, request.RuntimeResourceNames!.Contains(NativeCompilerSuiteContract.ResourcePrefix + NativeCompilerSuitePins.XeCatalogResourceName));
            Assert.DoesNotContain(request.RuntimeResourceNames!, n => n.EndsWith(".wasm", StringComparison.Ordinal) || n.EndsWith(".js", StringComparison.Ordinal));
            Assert.Contains(NativeCompilerSuiteContract.ResourcePrefix + "geometry.sty", request.RuntimeResourceNames!);
            if (request.Tag.Ordinal > 1 && request.Tag.Tool == "latex") Assert.Contains("chapters/part.aux", request.GeneratedInputNames!);
            if (lua || xe) Assert.Equal(request.SelectedRoot, request.MainEntry);
            if (engine == NativeCompilerEngine.LuaLaTeX)
            {
                Assert.Contains("result.pdf", request.Outputs);
                if (request.Tag.Tool == "bibtex") Assert.Equal("result.tex", request.MainEntry);
            }
        }
        var envelope = CompileOutputEnvelope.Create(result, Budget).Envelope!; Assert.NotNull(envelope.TryAccept(context).Output);
        Assert.True(f.Workspace.EditText(f.Main, "changed")); Assert.Null(envelope.TryAccept(await Context(f, engine, bib)).Output);
    });
    [SuiteFixtureTheory]
    [InlineData(NativeCompilerEngine.XeLaTeX, "tag")] [InlineData(NativeCompilerEngine.LuaLaTeX, "engine")]
    [InlineData(NativeCompilerEngine.XeLaTeX, "input")] [InlineData(NativeCompilerEngine.LuaLaTeX, "loader")]
    [InlineData(NativeCompilerEngine.XeLaTeX, "wasm")] [InlineData(NativeCompilerEngine.LuaLaTeX, "map")]
    [InlineData(NativeCompilerEngine.XeLaTeX, "missing")] [InlineData(NativeCompilerEngine.LuaLaTeX, "missing")]
    [InlineData(NativeCompilerEngine.XeLaTeX, "status")] [InlineData(NativeCompilerEngine.LuaLaTeX, "biber")]
    [InlineData(NativeCompilerEngine.PdfLaTeX, "biber")] [InlineData(NativeCompilerEngine.LaTeXDvi, "biber")]
    [InlineData(NativeCompilerEngine.XeLaTeX, "xdv-magic")] [InlineData(NativeCompilerEngine.LuaLaTeX, "oversize")]
    [InlineData(NativeCompilerEngine.LuaLaTeX, "log-file")] [InlineData(NativeCompilerEngine.LuaLaTeX, "log")]
    public void Wrong_or_missing_replies_never_authorize_an_artifact(NativeCompilerEngine engine, string kind) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "main.tex"); f.Workspace.CreateFile("chapters/part.tex", "part");
        var t = new Model { Mutate = (_, r) => kind switch
        {
            "tag" => r with { Tag = r.Tag with { Nonce = "wrong" } }, "engine" => r with { Tag = r.Tag with { EngineId = "pdftex" } },
            "input" => r with { Inputs = [] }, "loader" => r with { LoaderSha256 = NativeRuntimeProfiles.SyncTeXCompilerLoaderHash },
            "wasm" => r with { WasmSha256 = NativeRuntimeProfiles.SyncTeXCompilerWasmHash }, "map" => r with { SourceMapPayload = [1] },
            "missing" => r with { Files = r.Files.Where(f => !f.Name.EndsWith(".pdf", StringComparison.Ordinal) && !f.Name.EndsWith(".xdv", StringComparison.Ordinal)).ToArray() },
            "status" => r with { NativeReturn = 1 }, "biber" => r with { Log = "Please (re)run Biber on the file" },
            "xdv-magic" => r with { Files = r.Files.Select(f => f.Name.EndsWith(".xdv", StringComparison.Ordinal) ? f with { Bytes = [247, 2, 0] } : f).ToArray() },
            "oversize" => r with { Files = [new("result.pdf", new byte[8388609])] },
            "log-file" => r with { Files = [.. r.Files, new("result.log", new byte[65537])] },
            _ => r with { Log = new string('a', 65537) }
        } };
        var result = await Execute(await Context(f, engine, false), t, engine);
        Assert.NotEqual(CompileOutputStatus.Succeeded, result.Status); Assert.Null(result.Payload); Assert.Null(result.SourceMapPayload);
        Assert.DoesNotContain(t.Calls, r => r.Tag.Tool == "dvipdfmx");
    });
    [SuiteFixtureTheory]
    [InlineData("__oop_runtime__/geometry.sty")] [InlineData("__OOP_RUNTIME__/asset.tex")]
    [InlineData("result.aux")] [InlineData("RESULT.PDF")]
    [InlineData("swiftlatexpdftex.fmt")] [InlineData("PDFLATEX.FMT")]
    [InlineData("xelatex.fmt")] [InlineData("lualatex.fmt")]
    public void Reserved_or_fixed_generated_namespace_refuses_before_dispatch(string collision) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "main.tex"); Assert.True(f.Workspace.CreateFile(collision, "captured"));
        var t = new Model(); var result = await Execute(await Context(f, NativeCompilerEngine.LuaLaTeX, false), t, NativeCompilerEngine.LuaLaTeX);
        Assert.NotEqual(CompileOutputStatus.Succeeded, result.Status); Assert.Empty(t.Calls);
    });
    [SuiteFixtureTheory]
    [InlineData(NativeCompilerEngine.XeLaTeX)] [InlineData(NativeCompilerEngine.LuaLaTeX)]
    public void Cancellation_keeps_actual_operation_admission_until_settlement(NativeCompilerEngine engine) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "main.tex"); f.Workspace.CreateFile("chapters/part.tex", "part");
        var t = new Model { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) }; var factory = Factory(t, engine);
        var id = new CompileJobId(Guid.NewGuid()); var worker = factory.Create(id, 1); var context = await Context(f, engine, false);
        var task = worker.ExecuteAsync(id, context, Budget, default)!;
        await t.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            worker.RequestTermination(id, 1, CompileLifecycleReason.Cancelled);
            var result = await task; Assert.Equal(CompileOutputStatus.Cancelled, result!.Status); Assert.Null(result.Payload);
            Assert.False(factory.Operations.WhenIdle.IsCompleted); Assert.Equal(1, t.Stops);
        }
        finally { t.Hold.SetResult(t.Reply(t.Calls[^1])); await factory.Operations.WhenIdle; }
    });
}
public sealed class SuiteFixtureFactAttribute : FactAttribute
{
    public SuiteFixtureFactAttribute() { if (!NativeCompilerSuitePins.Ready || !File.Exists(Path.Combine(Environment.GetEnvironmentVariable("COMPILER_SUITE_RUNTIME_DIR") ?? "", "pins.json"))) Skip = "Real final compiler-suite fixture/pins unavailable; no native substitute."; }
}
public sealed class SuiteFixtureTheoryAttribute : TheoryAttribute
{
    public SuiteFixtureTheoryAttribute() { if (!NativeCompilerSuitePins.Ready || !File.Exists(Path.Combine(Environment.GetEnvironmentVariable("COMPILER_SUITE_RUNTIME_DIR") ?? "", "pins.json"))) Skip = "Real final compiler-suite fixture/pins unavailable; no native substitute."; }
}
