using System.Net;
using System.Reflection;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Client.RuntimeAssets;
using Xunit;

namespace Workspace.Tests.CompilationNative;

public sealed class NativeDviGraphics19Tests
{
    private const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalCompilerDviGraphics;
    private const NativeRuntimeProfile Parent = NativeRuntimeProfile.LocalCompilerCoursework;

    [Fact]
    public async Task Default_admits_exact_P19_without_expanding_ordinary_import()
    {
        Assert.Equal(Profile, BundledRuntimeAssets.CompilerProfile);
        Assert.NotNull(BundledRuntimeAssets.SelectedPdfCapacity);
        Assert.Equal(NativeCompilerDviGraphicsPins.TotalBytes, LocalRuntimeSelection.LimitsFor(Profile).MaxTotalBytes);
        foreach (var engine in Enum.GetValues<NativeCompilerEngine>())
        {
            var plan = NativeCompilerSuiteContract.CreatePlan(engine, profile: Profile);
            Assert.True(plan.IsValid);
            Assert.Equal(NativeCompilerSuiteContract.CreatePlan(engine, profile: Parent), plan with { Profile = Parent });
            var prior = NativeCompilerComponents.For(Parent, engine);
            var current = NativeCompilerComponents.For(Profile, engine);
            if (engine == NativeCompilerEngine.LaTeXDvi)
            {
                Assert.Equal(NativeCompilerDviGraphicsPins.DviConverterWasmHash, current.OutputAdapter.Hash?.Hex);
                Assert.NotEqual(prior.OutputAdapter, current.OutputAdapter);
                Assert.Equal(prior, current with { OutputAdapter = prior.OutputAdapter });
            }
            else Assert.Equal(prior, current);
        }
        Assert.True(NativeSelectedPdfContract.MatchesRuntime(Profile, NativeCompilerDviGraphicsPins.RuntimeIdentity));
        Assert.False(NativeSelectedPdfContract.MatchesRuntime(Profile, NativeCompilerCourseworkPins.RuntimeIdentity));
        Assert.False(NativeSelectedPdfContract.MatchesRuntime(Parent, NativeCompilerDviGraphicsPins.RuntimeIdentity));
        await using var ordinary = new ZipRuntimeArchive();
        await Assert.ThrowsAsync<InvalidDataException>(() => ordinary.PrepareAsync(NativeCompilerDviGraphicsPins.ArchiveLength,
            _ => throw new InvalidOperationException("Wrong archive profile cannot open a stream."), default));
    }

    [DviGraphicsFixtureFact]
    public async Task Authenticated_archive_changes_only_the_DVI_projection()
    {
        var root = Environment.GetEnvironmentVariable("COURSEWORK18_RUNTIME_DIR")!;
        var parentPins = LocalRuntimeSelection.ParsePins(await File.ReadAllBytesAsync(Path.Combine(root, "pins.json")));
        var parent = Assert.IsType<NativeRuntimePackage>(NativeRuntimePackage.Create(parentPins.Select(pin => new NativeResourceInput(pin.Name,
            File.ReadAllBytes(Path.Combine(root, "files", pin.Name)))).ToArray(), parentPins, LocalRuntimeSelection.LimitsFor(Parent), Parent).Package);
        using var handler = new ArchiveHandler(Environment.GetEnvironmentVariable("DVI_GRAPHICS19_ARCHIVE")!);
        await using var resources = new BundledRuntimeAssets(new HttpClient(handler) { BaseAddress = new("http://localhost/") });
        var installed = await resources.ReadCompilerAsync();
        Assert.Equal(RuntimeAssetStatus.Ready, installed.Store.Status);
        var runtime = Assert.IsType<NativeRuntimePackage>(installed.Package);
        Assert.Equal(NativeCompilerDviGraphicsPins.RuntimeIdentity, runtime.Identity.Hex);
        Assert.Equal(["/bundled-runtime/compiler-suite.zip"], handler.Requests);
        foreach (var engine in new[] { NativeCompilerEngine.PdfLaTeX, NativeCompilerEngine.LuaLaTeX, NativeCompilerEngine.XeLaTeX })
            Assert.Equal(Projection(parent, engine, "SuiteFileLengths"), Projection(runtime, engine, "SuiteFileLengths"));
        var before = Projection(parent, NativeCompilerEngine.LaTeXDvi, "SuiteFileLengths");
        var after = Projection(runtime, NativeCompilerEngine.LaTeXDvi, "SuiteFileLengths");
        var additions = after.Except(before).ToArray();
        Assert.Equal(204, additions.Length);
        Assert.Contains(("__oop_runtime__/color.cfg", 1219), additions);
        Assert.Contains(("__oop_runtime__/times.sty", 856), additions);
        Assert.Contains(additions, f => f.Name == "__oop_runtime__/tcrm1200.tfm");
        Assert.Equal(new[] { ("__oop_runtime__/color.cfg", 1213) }, before.Except(after).ToArray());
        Assert.Equal(202,
            Projection(runtime, NativeCompilerEngine.LaTeXDvi, "ConverterResourceLengthsFor")
                .Except(Projection(parent, NativeCompilerEngine.LaTeXDvi, "ConverterResourceLengthsFor")).Count());
        Assert.Equal(Projection(parent, NativeCompilerEngine.XeLaTeX, "ConverterResourceLengthsFor"),
            Projection(runtime, NativeCompilerEngine.XeLaTeX, "ConverterResourceLengthsFor"));
    }

    private static (string Name, int Length)[] Projection(NativeRuntimePackage package, NativeCompilerEngine engine, string member) =>
        ((IEnumerable<(string Name, int Length)>)typeof(NativeRuntimePackage)
            .GetMethod(member, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(package, [engine])!)
        .OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();

    private sealed class ArchiveHandler(string file) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(file)) });
        }
    }

    private sealed class DviGraphicsFixtureFactAttribute : FactAttribute
    {
        public DviGraphicsFixtureFactAttribute()
        {
            if (!File.Exists(Path.Combine(Environment.GetEnvironmentVariable("COURSEWORK18_RUNTIME_DIR") ?? "", "pins.json"))
                || !File.Exists(Environment.GetEnvironmentVariable("DVI_GRAPHICS19_ARCHIVE")))
                Skip = "Explicit exact P18 inventory and P19 compiler archive required.";
        }
    }
}
