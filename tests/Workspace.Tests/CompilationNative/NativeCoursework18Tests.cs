using System.Net;
using System.Reflection;
using System.Text.Json;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Client.RuntimeAssets;
using Xunit;

namespace Workspace.Tests.CompilationNative;

public sealed class NativeCoursework18Tests
{
    private const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalCompilerCoursework;
    private const NativeRuntimeProfile Previous = NativeRuntimeProfile.LocalCompilerSelectedPdf;

    [Fact]
    public void Coursework_quota_adds_archive_capacity_without_widening_phases()
    {
        Assert.Equal(Profile, BundledRuntimeAssets.CompilerProfile);
        Assert.NotNull(BundledRuntimeAssets.SelectedPdfCapacity);
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
        Assert.False(NativeSelectedPdfContract.IsSelectedPdfPhase(tag with { Profile = (NativeRuntimeProfile)19 }));
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
    public async Task Exact_bundled_archive_authenticates_Lua_projection_and_preserves_P17_roles()
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

        using var handler = new ArchiveHandler(Environment.GetEnvironmentVariable("COURSEWORK18_ARCHIVE")!);
        await using var resources = new BundledRuntimeAssets(new HttpClient(handler) { BaseAddress = new("http://localhost/") });
        var installed = await resources.ReadCompilerAsync();
        Assert.Equal(RuntimeAssetStatus.Ready, installed.Store.Status);
        var runtime = Assert.IsType<NativeRuntimePackage>(installed.Package);
        Assert.Equal(Profile, runtime.Profile);
        Assert.Equal(NativeCompilerCourseworkPins.RuntimeIdentity, runtime.Identity.Hex);
        Assert.Equal(new[] { "/bundled-runtime/compiler-suite.zip" }, handler.Requests);

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

    private static (string Name, int Length)[] Projection(NativeRuntimePackage package, NativeCompilerEngine engine) =>
        ((IEnumerable<(string Name, int Length)>)typeof(NativeRuntimePackage)
            .GetMethod("SuiteFileLengths", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(package, [engine])!)
            .OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();

    private static (string Name, int Length)[] ConverterProjection(NativeRuntimePackage package) =>
        ((IEnumerable<(string Name, int Length)>)typeof(NativeRuntimePackage)
            .GetProperty("ConverterResourceLengths", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(package)!)
            .OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();

    private sealed class ArchiveHandler(string file) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            var source = File.OpenRead(file);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) });
        }
    }
}

public sealed class CourseworkFixtureFactAttribute : FactAttribute
{
    public CourseworkFixtureFactAttribute()
    {
        if (!File.Exists(Path.Combine(Environment.GetEnvironmentVariable("SELECTED_PDF17_RUNTIME_DIR") ?? "", "pins.json"))
            || !File.Exists(Path.Combine(Environment.GetEnvironmentVariable("COURSEWORK18_RUNTIME_DIR") ?? "", "coursework18-assembly.json"))
            || !File.Exists(Environment.GetEnvironmentVariable("COURSEWORK18_ARCHIVE")))
            Skip = "Explicit unchanged P17 and exact P18 compiler fixtures required.";
    }
}
