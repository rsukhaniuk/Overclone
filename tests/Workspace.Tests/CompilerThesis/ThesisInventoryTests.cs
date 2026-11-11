using System.Text;
using Client.Compilation;
using Client.Compilation.Lifecycle;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Xunit;

namespace Workspace.Tests.CompilerThesis;

// Exact real package bytes with MODELED phase replies. This proves C# policy,
// ownership and projection; root runs actual compiler/browser acceptance.
public sealed class ThesisInventoryTests
{
    private const NativeRuntimeProfile P = NativeThesisCompilerContract.Profile;
    private static readonly CompileOutputBudget Budget = new(8388608, 65536, 32, 128, 4096, 8192);

    [ThesisFixtureFact]
    public void Final_closed_inventory_roundtrips_and_rejects_old_component_or_notice_substitution()
    {
        var pins = ThesisFixture.Pins;
        Assert.Equal(NativeRuntimeProfiles.InstalledCount(P), pins.Length);
        Assert.Equal(NativeCompilerThesisPins.TotalBytes, pins.Sum(pin => (long)pin.Length));
        Assert.Equal(P, LocalRuntimeSelection.ProfileFor(pins));
        Assert.True(NativeCompilerThesisPins.MatchesComponents(pins.ToDictionary(pin => pin.Name, StringComparer.Ordinal)));
        var resources = LocalRuntimeSelection.Resources([.. pins]);
        var inventory = LocalRuntimeSelection.Inventory(resources);
        var mapping = new InstalledNativeRuntimeMapping(Guid.NewGuid(), inventory.Identity.Digest.Hex, resources, P);
        var encoded = LocalRuntimeSelection.Encode(mapping);
        Assert.Equal(encoded, LocalRuntimeSelection.Encode(LocalRuntimeSelection.Decode(encoded)));
        Assert.Throws<FormatException>(() => LocalRuntimeSelection.Encode(mapping with { Profile = NativeBiberCompilerContract.Profile }));
        var oldXe = NativeBiberCompilerContract.Component(NativeCompilerEngine.XeLaTeX);
        foreach (var name in new[] { "xetex.wasm", "luaotfload-harf-plug.lua", pins.First(pin => pin.Name.StartsWith("notices/thesis-packages/", StringComparison.Ordinal)).Name })
        {
            var changed = pins.Select(pin => pin.Name == name ? pin with { Sha256 = name == "xetex.wasm" ? oldXe.WasmHash : new string('0', 64) } : pin).ToArray();
            Assert.Throws<FormatException>(() => LocalRuntimeSelection.ProfileFor(changed));
        }
        var package = ThesisFixture.Package;
        Assert.Equal(P, package.Profile); Assert.True(package.HasBiber);
        Assert.Equal("local-compiler-thesis", package.Bundle.Id);
        Assert.Equal(NativeRuntimeProfiles.SyncTeXCompilerWasmHash, package.Bibtex.Hash.Hex);
        Assert.Equal(NativeCompilerBiberPins.BiberWasmHash, package.Biber.Hash.Hex);
        foreach (var engine in Enum.GetValues<NativeCompilerEngine>())
            Assert.Equal(NativeCompilerComponents.For(P, engine).OutputAdapter, package.AdapterFor(engine));
    }

    [ThesisFixtureTheory]
    [InlineData(127, true)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public async Task Combined_work_reaches256_but_actual_carry_still_refuses129(int carried, bool succeeds)
    {
        await using var f = await CompilationTestWorkspace.Open();
        foreach (var file in f.Workspace.Files.ToArray()) Assert.True(f.Workspace.DeleteFile(file.Id));
        for (var index = 0; index < 128; index++)
            Assert.True(f.Workspace.CreateFile(index == 0 ? "main.tex" : "chapters/part" + index + ".tex", "Synthetic boundary fixture\n"));
        var package = ThesisFixture.Package;
        f.Selection.SetSelection(CompilationSelection.Create(new(f.Main, package.Engine, [package.Bundle], [],
            NativeCompilerSuiteContract.SelectionOptions(P, NativeCompilerEngine.PdfLaTeX)), 65536).Selection!);
        var snapshot = await f.Snapshot(new(128, 1048576, 8388608, 131072));
        var context = CompileRequestContext.Create(snapshot, 1, 65536, NativeRuntimePackage.Pdf, package.OutputAdapter).Context!;
        var model = new CarryModel(carried);
        var factory = new NativeCompilationWorkerFactory(new FixedNativeRuntimeProvider(package), model,
            NativeCompilerSuiteContract.CreatePlan(NativeCompilerEngine.PdfLaTeX, profile: P));
        var job = new CompileJobId(Guid.NewGuid());
        var result = await factory.Create(job, 1).ExecuteAsync(job, context, Budget, default)!;
        Assert.NotNull(result);
        Assert.Equal(succeeds ? CompileOutputStatus.Succeeded : CompileOutputStatus.Failed, result.Status);
        if (succeeds)
        {
            Assert.Equal(2, model.Calls);
            Assert.Equal(128 + carried, model.MaxWorkFiles);
            Assert.NotNull(CompileOutputEnvelope.Create(result, Budget).Envelope!.TryAccept(context).Output);
        }
        else
        {
            Assert.Equal(1, model.Calls);
            Assert.Equal("v4-state-budget", Assert.Single(result.Diagnostics!).Code);
            Assert.Null(result.Payload);
        }
        Assert.Equal(0, factory.Operations.Active);
    }

    [ThesisFixtureFact]
    public async Task Expanded_runtime_cannot_enter_the_P13_trusted_plan()
    {
        await using var f = await CompilationTestWorkspace.Open();
        Assert.True(f.Workspace.RenameFile(f.Main, "main.tex"));
        var candidate = ThesisFixture.Package;
        f.Selection.SetSelection(CompilationSelection.Create(new(f.Main, candidate.Engine, [candidate.Bundle], [],
            NativeCompilerSuiteContract.SelectionOptions(P, NativeCompilerEngine.PdfLaTeX)), 65536).Selection!);
        var context = CompileRequestContext.Create(await f.Snapshot(), 1, 65536, NativeRuntimePackage.Pdf, candidate.OutputAdapter).Context!;
        // Reuse the exact installed package but require a different trusted plan.
        // It must reject before any transport even when phase23/options coincide.
        var model = new CarryModel(1);
        var factory = new NativeCompilationWorkerFactory(new FixedNativeRuntimeProvider(candidate), model,
            NativeCompilerSuiteContract.CreatePlan(NativeCompilerEngine.PdfLaTeX, profile: NativeBiberCompilerContract.Profile));
        var job = new CompileJobId(Guid.NewGuid());
        var result = await factory.Create(job, 1).ExecuteAsync(job, context, Budget, default)!;
        Assert.Equal(CompileOutputStatus.Unsupported, result!.Status); Assert.Equal(0, model.Calls);
    }

    [ThesisAndBiberFixtureFact]
    public async Task P13_runtime_cannot_gain_expanded_authority_from_matching_phase23()
    {
        await using var f = await CompilationTestWorkspace.Open();
        Assert.True(f.Workspace.RenameFile(f.Main, "main.tex"));
        while (f.Workspace.Files.Count(file => !file.Deleted) < 66)
            Assert.True(f.Workspace.CreateFile("chapter" + f.Workspace.Files.Count + ".tex", "Synthetic runtime-profile guard\n"));
        var previous = Workspace.Tests.CompilerBiber.BiberFixture.Package;
        f.Selection.SetSelection(CompilationSelection.Create(new(f.Main, previous.Engine, [previous.Bundle], [],
            NativeCompilerSuiteContract.SelectionOptions(P, NativeCompilerEngine.PdfLaTeX)), 65536).Selection!);
        var context = CompileRequestContext.Create(await f.Snapshot(new(128, 1048576, 8388608, 131072)), 1, 65536,
            NativeRuntimePackage.Pdf, previous.OutputAdapter).Context!;
        var model = new CarryModel(1);
        var factory = new NativeCompilationWorkerFactory(new FixedNativeRuntimeProvider(previous), model,
            NativeCompilerSuiteContract.CreatePlan(NativeCompilerEngine.PdfLaTeX, profile: P));
        var job = new CompileJobId(Guid.NewGuid());
        var result = await factory.Create(job, 1).ExecuteAsync(job, context, Budget, default)!;
        Assert.Equal(CompileOutputStatus.Unsupported, result!.Status); Assert.Equal(0, model.Calls);
    }

    private sealed class CarryModel(int count) : INativePhaseTransport
    {
        internal int Calls, MaxWorkFiles;
        public Task<NativePhaseReply> ExecuteAsync(NativePhaseRequest request)
        {
            Calls++;
            Assert.Equal(P, request.Tag.Profile); Assert.Equal(23, request.Tag.Version);
            Assert.Equal("latex", request.Tag.Tool); Assert.Equal("none", request.BibliographyTool);
            Assert.Equal(NativeCompilerBiberPins.SemanticWasmHash, NativeRuntimePackage.Digest(request.Wasm).Hex);
            Assert.NotNull(request.RuntimeResourceNames); Assert.NotNull(request.GeneratedInputNames);
            Assert.DoesNotContain(request.RuntimeResourceNames, name => name.Contains("/notices/", StringComparison.Ordinal));
            var work = request.Files.Length - request.RuntimeResourceNames.Length;
            Assert.Equal(128 + request.GeneratedInputNames.Length, work);
            MaxWorkFiles = Math.Max(MaxWorkFiles, work);
            Assert.InRange(request.Outputs.Length, 129, 256);
            var names = request.Outputs.Where(name => name.EndsWith(".aux", StringComparison.Ordinal)).Take(count).ToList();
            if (count > names.Count) names.Add("main.toc");
            Assert.Equal(count, names.Count);
            var outputs = names.Select(name => new NativeFileTransfer(name, "\\relax\n"u8.ToArray())).ToList();
            outputs.Add(new("main.pdf", Encoding.ASCII.GetBytes("%PDF-modeled P14 carry boundary")));
            var inputs = request.Files.OrderBy(file => file.Name, StringComparer.Ordinal)
                .Select(file => new NativeInputFact(file.Name, file.Bytes.Length, NativeRuntimePackage.Digest(file.Bytes).Hex)).ToArray();
            return Task.FromResult(new NativePhaseReply(request.Tag, "native-return", 0, true, "", outputs.ToArray(), inputs,
                WasmSha256: NativeRuntimePackage.Digest(request.Wasm).Hex, LoaderSha256: NativeRuntimePackage.Digest(request.Loader).Hex));
        }
        public void RequestTermination(string job, long epoch) { }
    }
}

internal static class ThesisFixture
{
    internal static string? Root => Environment.GetEnvironmentVariable("THESIS_COMPILER_RUNTIME_DIR");
    internal static NativeResourcePin[] Pins => LocalRuntimeSelection.ParsePins(File.ReadAllBytes(Path.Combine(Root!, "pins.json"))).ToArray();
    private static readonly Lazy<NativeRuntimePackage> Loaded = new(() =>
    {
        var pins = Pins;
        return Assert.IsType<NativeRuntimePackage>(NativeRuntimePackage.Create(pins.Select(pin => new NativeResourceInput(pin.Name,
            File.ReadAllBytes(Path.Combine(Root!, "files", pin.Name)))).ToArray(), pins, LocalRuntimeSelection.LimitsFor(NativeThesisCompilerContract.Profile), NativeThesisCompilerContract.Profile).Package);
    });
    internal static NativeRuntimePackage Package => Loaded.Value;
}

public sealed class ThesisFixtureFactAttribute : FactAttribute
{
    public ThesisFixtureFactAttribute()
    { if (!NativeCompilerThesisPins.Ready || !File.Exists(Path.Combine(ThesisFixture.Root ?? "", "pins.json"))) Skip = "Requires final closed P14 THESIS_COMPILER_RUNTIME_DIR; modeled transport is not native proof."; }
}
public sealed class ThesisFixtureTheoryAttribute : TheoryAttribute
{
    public ThesisFixtureTheoryAttribute()
    { if (!NativeCompilerThesisPins.Ready || !File.Exists(Path.Combine(ThesisFixture.Root ?? "", "pins.json"))) Skip = "Requires final closed P14 THESIS_COMPILER_RUNTIME_DIR; modeled transport is not native proof."; }
}

public sealed class ThesisAndBiberFixtureFactAttribute : FactAttribute
{
    public ThesisAndBiberFixtureFactAttribute()
    {
        if (!NativeCompilerThesisPins.Ready || !File.Exists(Path.Combine(ThesisFixture.Root ?? "", "pins.json"))
            || !File.Exists(Path.Combine(Workspace.Tests.CompilerBiber.BiberFixture.Root ?? "", "pins.json")))
            Skip = "Requires both exact closed P13 BIBER_COMPILER_RUNTIME_DIR and P14 THESIS_COMPILER_RUNTIME_DIR; managed guard only.";
    }
}
