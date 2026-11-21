using System.Reflection;
using System.Text.Json;
using Client.Compilation;
using Client.Compilation.Lifecycle;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Workspace.Tests.CompilationLifecycle;
using Xunit;

namespace Workspace.Tests.CompilationNative;

public sealed class NativeCoursework18Tests
{
    private const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalCompilerCoursework;
    private const NativeRuntimeProfile Previous = NativeRuntimeProfile.LocalCompilerSelectedPdf;

    [Fact]
    public void Coursework_quota_adds_archive_capacity_without_widening_phases()
    {
        Assert.Equal(NativeCompilerCourseworkPins.TotalBytes, LocalRuntimeSelection.LimitsFor(Profile).MaxTotalBytes);
        Assert.Equal(NativeCompilerSuiteContract.TotalBytes, LocalRuntimeSelection.LimitsFor(Previous).MaxTotalBytes);
        Assert.False((LocalRuntimeSelection.LimitsFor(Profile) with { MaxTotalBytes = NativeCompilerCourseworkPins.TotalBytes + 1 }).IsValid);
        foreach (var engine in Enum.GetValues<NativeCompilerEngine>())
        {
            var previous = NativeCompilerSuiteContract.CreatePlan(engine, profile: Previous);
            var plan = NativeCompilerSuiteContract.CreatePlan(engine, profile: Profile);
            Assert.True(plan.IsValid);
            Assert.Equal(previous, plan with { Profile = Previous });
            Assert.Equal(previous.SourceFileBytes, plan.SourceFileBytes);
            Assert.Equal(previous.ProjectTotalBytes, plan.ProjectTotalBytes);
            Assert.Equal(previous.ConverterProjectBytes, plan.ConverterProjectBytes);
            Assert.Equal(NativeCompilerComponents.For(Previous, engine), NativeCompilerComponents.For(Profile, engine));
        }
        var tag = new NativePhaseTag("job", "request", 1, Guid.Empty, "digest", 1, "latex", "nonce",
            Version: 23, Profile: Profile, RuntimeIdentity: NativeCompilerCourseworkPins.RuntimeIdentity, EngineId: "pdftex");
        Assert.True(NativeSelectedPdfContract.IsSelectedPdfPhase(tag));
        Assert.True(NativeSelectedPdfContract.IsSelectedPdfPhase(tag with
        {
            Profile = Previous, RuntimeIdentity = NativeSelectedPdfContract.RuntimeIdentity,
        }));
        Assert.False(NativeSelectedPdfContract.IsSelectedPdfPhase(tag with { RuntimeIdentity = NativeSelectedPdfContract.RuntimeIdentity }));
        Assert.False(NativeSelectedPdfContract.IsSelectedPdfPhase(tag with { Profile = Previous }));
        Assert.False(NativeSelectedPdfContract.IsSelectedPdfPhase(tag with { Profile = (NativeRuntimeProfile)22 }));
        Assert.False(NativeSelectedPdfContract.IsSelectedPdfPhase(tag with { EngineId = "lualatex", Version = 22 }));
    }

    [Fact]
    public async Task Ordinary_ZIP_picker_retains_previous_limit()
    {
        await using var archive = new ZipRuntimeArchive();
        bool opened = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => archive.PrepareAsync(NativeCompilerCourseworkPins.ArchiveLength,
            _ => { opened = true; return Task.FromResult<Stream>(Stream.Null); }, default));
        Assert.False(opened);
        Assert.Equal(0, archive.PinsStreamsOpened);
    }

    [CourseworkFixtureFact]
    public async Task Exact_P18_runtime_preserves_its_Lua_and_original_P17_role_projections()
    {
        var previousRoot = Environment.GetEnvironmentVariable("SELECTED_PDF17_RUNTIME_DIR")!;
        var courseworkRoot = Environment.GetEnvironmentVariable("COURSEWORK18_RUNTIME_DIR")!;
        var previousPins = LocalRuntimeSelection.ParsePins(await File.ReadAllBytesAsync(Path.Combine(previousRoot, "pins.json")));
        var newPins = LocalRuntimeSelection.ParsePins(await File.ReadAllBytesAsync(Path.Combine(courseworkRoot, "pins.json")));
        Assert.Equal(Previous, LocalRuntimeSelection.ProfileFor(previousPins));
        Assert.Equal(Profile, LocalRuntimeSelection.ProfileFor(newPins));
        Assert.Throws<FormatException>(() => LocalRuntimeSelection.ProfileFor(newPins
            .Select(pin => pin.Name == "polyglossia.sty" ? pin with { Sha256 = new string('0', 64) } : pin)));
        var previous = NativeRuntimePackage.Create(previousPins.Select(pin => new NativeResourceInput(pin.Name,
            File.ReadAllBytes(Path.Combine(previousRoot, "files", pin.Name)))).ToArray(), previousPins,
            LocalRuntimeSelection.LimitsFor(Previous), Previous);
        Assert.Equal(NativeSelectedPdfContract.RuntimeIdentity, previous.Package?.Identity.Hex);

        var runtime = LoadCourseworkRuntime();
        Assert.Equal(Profile, runtime.Profile);
        Assert.Equal(NativeCompilerCourseworkPins.RuntimeIdentity, runtime.Identity.Hex);

        foreach (var engine in new[] { NativeCompilerEngine.PdfLaTeX, NativeCompilerEngine.LaTeXDvi, NativeCompilerEngine.XeLaTeX })
            Assert.Equal(Projection(previous.Package!, engine), Projection(runtime, engine));
        var earlierLua = Projection(previous.Package!, NativeCompilerEngine.LuaLaTeX);
        var expandedLua = Projection(runtime, NativeCompilerEngine.LuaLaTeX);
        var additions = expandedLua.Except(earlierLua).ToArray();
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(courseworkRoot, "coursework18-assembly.json")));
        var expected = manifest.RootElement.GetProperty("luaCompatibilityNames").EnumerateArray()
            .Select(n => "__oop_runtime__/" + n.GetString()).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, additions.Select(item => item.Name).ToArray());
        Assert.Equal(earlierLua, expandedLua.Except(additions).ToArray());
        Assert.Equal(4373, expandedLua.Length);
        Assert.Equal(128053947L, expandedLua.Sum(item => (long)item.Length));
        Assert.Equal(ConverterProjection(previous.Package!), ConverterProjection(runtime));
    }

    [CourseworkFixtureFact]
    public void Lua_scheduler_preserves_empty_outline_and_waits_for_a_genuine_rerun() => LifecycleTestHost.Run(async () =>
    {
        var runtime = LoadCourseworkRuntime();
        const NativeCompilerEngine engine = NativeCompilerEngine.LuaLaTeX;
        await using var workspace = await CompilationTestWorkspace.Open();
        Assert.True(workspace.Workspace.RenameFile(workspace.Main, "main.tex"));
        workspace.Selection.SetSelection(CompilationSelection.Create(new(workspace.Main, runtime.EngineFor(engine),
            [runtime.Bundle], [], NativeCompilerSuiteContract.SelectionOptions(Profile, engine)), 65536).Selection!);
        var context = CompileRequestContext.Create(await workspace.Snapshot(), 1, 65536, NativeRuntimePackage.Pdf,
            runtime.AdapterFor(engine)).Context!;
        var phase = new OutlineTransport();
        var factory = new NativeCompilationWorkerFactory(new FixedNativeRuntimeProvider(runtime), phase,
            NativeCompilerSuiteContract.CreatePlan(engine, profile: Profile));
        var id = new CompileJobId(Guid.NewGuid());
        var result = await factory.Create(id, 1).ExecuteAsync(id, context,
            new CompileOutputBudget(8388608, 65536, 32, 128, 4096, 8192), CancellationToken.None)!;
        Assert.Equal(CompileOutputStatus.Succeeded, result!.Status);
        Assert.Equal(3, phase.Passes);
    });

    // Modeled replies verify managed carry and stability independently of native rendering.
    private sealed class OutlineTransport : INativePhaseTransport
    {
        public int Passes { get; private set; }

        public Task<NativePhaseReply> ExecuteAsync(NativePhaseRequest request)
        {
            const string outline = "result.out";
            Assert.Equal(++Passes, request.Tag.Ordinal);
            Assert.Contains(outline, request.Outputs);
            var carried = request.Files.SingleOrDefault(file => file.Name == outline);
            if (Passes == 1)
            {
                Assert.Null(carried);
                Assert.DoesNotContain(outline, request.GeneratedInputNames!);
            }
            else
            {
                Assert.NotNull(carried);
                Assert.Empty(carried.Bytes);
                Assert.Contains(outline, request.GeneratedInputNames!);
            }
            var inputs = request.Files.Select(file => new NativeInputFact(file.Name, file.Bytes.Length,
                NativeRuntimePackage.Digest(file.Bytes).Hex)).ToArray();
            return Task.FromResult(new NativePhaseReply(request.Tag, "native-return", 0, true,
                Passes <= 2 ? "Rerun to get outlines right" : "",
                [new("result.pdf", "%PDF-model"u8.ToArray()), new("result.aux", "aux"u8.ToArray()), new(outline, [])],
                inputs, WasmSha256: NativeRuntimePackage.Digest(request.Wasm).Hex,
                LoaderSha256: NativeRuntimePackage.Digest(request.Loader).Hex));
        }

        public void RequestTermination(string job, long epoch) { }
    }

    private static (string Name, int Length)[] Projection(NativeRuntimePackage package, NativeCompilerEngine engine) =>
        ((IEnumerable<(string Name, int Length)>)typeof(NativeRuntimePackage)
            .GetMethod("SuiteFileLengths", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(package, [engine])!)
            .OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();

    private static (string Name, int Length)[] ConverterProjection(NativeRuntimePackage package) =>
        ((IEnumerable<(string Name, int Length)>)typeof(NativeRuntimePackage)
            .GetProperty("ConverterResourceLengths", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(package)!)
            .OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();

    private static NativeRuntimePackage LoadCourseworkRuntime()
    {
        string root = Environment.GetEnvironmentVariable("COURSEWORK18_RUNTIME_DIR")!;
        var pins = LocalRuntimeSelection.ParsePins(File.ReadAllBytes(Path.Combine(root, "pins.json")));
        return Assert.IsType<NativeRuntimePackage>(NativeRuntimePackage.Create(
            pins.Select(pin => new NativeResourceInput(pin.Name, File.ReadAllBytes(Path.Combine(root, "files", pin.Name)))).ToArray(),
            pins, LocalRuntimeSelection.LimitsFor(Profile), Profile).Package);
    }

}

public sealed class CourseworkFixtureFactAttribute : FactAttribute
{
    public CourseworkFixtureFactAttribute()
    {
        if (!File.Exists(Path.Combine(Environment.GetEnvironmentVariable("SELECTED_PDF17_RUNTIME_DIR") ?? "", "pins.json"))
            || !File.Exists(Path.Combine(Environment.GetEnvironmentVariable("COURSEWORK18_RUNTIME_DIR") ?? "", "coursework18-assembly.json")))
            Skip = "Explicit unchanged P17 and exact P18 compiler fixtures required.";
    }
}
