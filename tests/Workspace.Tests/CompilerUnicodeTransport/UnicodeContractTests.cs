using System.Collections;
using System.Text.Json;
using Client.Compilation;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Xunit;

namespace Workspace.Tests.CompilerUnicodeTransport;

// Real closed package admission; modeled transport tests are separate from native proof.
public sealed class UnicodeContractTests
{
    private const NativeRuntimeProfile P = NativeUnicodeCompilerContract.Profile;

    [Fact]
    public void Exact_profile_matrix_keeps_reserved_and_future_profiles_closed()
    {
        Assert.Equal(12, (int)P);
        Assert.Equal(new[] { 6, 15, 21, 22 }, Enum.GetValues<NativeCompilerEngine>().Select(e => NativeCompilerSuiteContract.Phase(P, e)));
        Assert.Equal(new[] { 6, 7, 8, 9 }, Enum.GetValues<NativeCompilerEngine>().Select(NativeCompilerSuiteContract.Phase));
        foreach (var e in Enum.GetValues<NativeCompilerEngine>())
        {
            var plan = NativeCompilerSuiteContract.CreatePlan(e, profile: P);
            Assert.True(plan.IsValid);
            foreach (var phase in Enumerable.Range(0, 25).Where(n => n != plan.EffectivePhaseVersion))
                Assert.False((plan with { PhaseProtocolVersion = phase }).IsValid);
            Assert.True(NativeCompilerSuiteContract.MatchesOptions(NativeCompilerSuiteContract.SelectionOptions(P, e), P, e));
            Assert.False(NativeCompilerSuiteContract.MatchesOptions(NativeCompilerSuiteContract.SelectionOptions(P, e).Concat(
                NativeCompilerSuiteContract.SelectionOptions(P, e)), P, e));
            Assert.Equal(e != NativeCompilerEngine.PdfLaTeX,
                NativeNavigationContract.IsMapped(P, plan.EffectivePhaseVersion, e));
            Assert.False(NativeNavigationContract.IsMapped(P, 23, e));
        }
        foreach (var p in new[] { (NativeRuntimeProfile)11, (NativeRuntimeProfile)20, (NativeRuntimeProfile)int.MaxValue })
        {
            Assert.False(NativeRuntimeProfiles.Known(p)); Assert.False(NativeRuntimeProfiles.IsSuite(p));
            Assert.Equal(0, NativeCompilerSuiteContract.Phase(p, NativeCompilerEngine.XeLaTeX));
            Assert.Throws<FormatException>(() => LocalRuntimeSelection.LimitsFor(p));
            Assert.Throws<ArgumentOutOfRangeException>(() => NativeCompilerComponents.For(p, NativeCompilerEngine.XeLaTeX));
        }
        Assert.True(NativeRuntimeProfiles.HasBibliographyCarry(P));
        Assert.Equal(4186, NativeRuntimeProfiles.InstalledCount(P));
        Assert.Equal(229018228, NativeRuntimeProfiles.SuiteTotalBytes(P));
        Assert.Equal("local-compiler-unicode-v1", NativeRuntimeProfiles.IdentityName(P));
        Assert.Equal(NativeCompilerUnicodePins.ClosureHash, NativeRuntimeProfiles.ClosureHash(P));
        Assert.True(LocalRuntimeSelection.LimitsFor(P).IsValid);
        Assert.Equal(6, NativeRuntimeProfiles.ProtocolVersion(P));
    }

    [Fact]
    public void Components_replace_only_proved_Xe_Lua_executables_and_Lua_format()
    {
        foreach (var e in Enum.GetValues<NativeCompilerEngine>())
        {
            var old = NativeCompilerComponents.For(NativeGraphicsNavigationContract.Profile, e);
            var next = NativeCompilerComponents.For(P, e);
            Assert.Equal(old.NativeAbiVersion, next.NativeAbiVersion);
            Assert.Equal(old.QueryLoaderHash, next.QueryLoaderHash); Assert.Equal(old.QueryWasmHash, next.QueryWasmHash);
            Assert.Equal(old.SupportsSourceMap, next.SupportsSourceMap);
            if (e is NativeCompilerEngine.PdfLaTeX or NativeCompilerEngine.LaTeXDvi) Assert.Equal(old, next);
            else
            {
                Assert.NotEqual(old.LoaderHash, next.LoaderHash); Assert.NotEqual(old.WasmHash, next.WasmHash);
                if (e == NativeCompilerEngine.XeLaTeX)
                {
                    Assert.Equal("maps-abi1-nonbmp1", next.EngineVersion);
                    Assert.Equal(old.FormatHash, next.FormatHash); Assert.Equal(old.FormatLength, next.FormatLength);
                    Assert.Equal(old.OutputAdapter, next.OutputAdapter);
                }
                else
                {
                    Assert.Equal("abi5-graphics1-harfbuzz1-fonts1", next.EngineVersion);
                    Assert.Equal(NativeCompilerUnicodePins.LuaFormatHash, next.FormatHash);
                    Assert.Equal(12437864, next.FormatLength);
                    Assert.NotEqual(old.OutputAdapter, next.OutputAdapter);
                    Assert.Equal(NativeCompilerUnicodePins.LuaWasmHash, next.OutputAdapter.Hash!.Value.Hex);
                }
            }
        }
    }

    [Fact]
    public void Historical_registry_retains_only_exact_ready_output_identities()
    {
        var old = new[] { NativeSyncTeXContract.OutputAdapter, NativeLatexDviContract.OutputAdapter }
            .Concat(Enumerable.Range(5, 6).SelectMany(p => Enum.GetValues<NativeCompilerEngine>()
                .Select(e => NativeCompilerComponents.For((NativeRuntimeProfile)p, e).OutputAdapter)))
            .Where(adapter => adapter != NativeRuntimePackage.Adapter).Distinct().ToArray();
        var current = Client.Compilation.Presentation.AuthoringWorkspaceRegistration.HistoricalOutputAdapters();
        var added = current.Except(old).ToArray();
        var expected = new[] { NativeUnicodeCompilerContract.Component(NativeCompilerEngine.LuaLaTeX).OutputAdapter }
            .Concat(Enum.GetValues<NativeCompilerEngine>().Select(NativeBiberCompilerContract.Component).Select(c => c.OutputAdapter))
            .Concat(NativeCompilerThesisPins.Ready ? Enum.GetValues<NativeCompilerEngine>().Select(NativeThesisCompilerContract.Component).Select(c => c.OutputAdapter) : [])
            .Concat(NativeCompilerOriginPins.Ready ? Enum.GetValues<NativeCompilerEngine>().Select(NativeOriginCompilerContract.Component).Select(c => c.OutputAdapter) : [])
            .Concat(NativeCompilerInputBudgetPins.Ready ? Enum.GetValues<NativeCompilerEngine>().Select(NativeInputBudgetContract.Component).Select(c => c.OutputAdapter) : [])
            .Concat(NativeCompilerDviGraphicsPins.Ready
                ? [NativeCompilerComponents.For(NativeRuntimeProfile.LocalCompilerDviGraphics, NativeCompilerEngine.LaTeXDvi).OutputAdapter] : [])
            .Except(old).Distinct().ToArray();
        Assert.Equal(NativeCompilerDviGraphicsPins.Ready ? 11 : NativeCompilerInputBudgetPins.Ready ? 10 : NativeCompilerOriginPins.Ready ? 8 : NativeCompilerThesisPins.Ready ? 5 : 4, added.Length); Assert.Equal(expected.Length, added.Length);
        Assert.All(expected, adapter => Assert.Contains(adapter, added));
        Assert.All(old, adapter => Assert.Contains(adapter, current));
        Assert.All(added, adapter => Assert.DoesNotContain(adapter with { Hash = new(new string('0', 64)) }, current));
        Assert.True(current.Count <= Client.Compilation.History.CompileArtifactHistoryStore.MaxAdditionalAdapters);
    }

    [Theory]
    [InlineData("тези/розділ 😀.tex")]
    [InlineData("😀/тези.tex")]
    [InlineData("тези/\U00010000.tex")]
    [InlineData("тези/\U0010ffff.tex")]
    public void Only_exact_P12_through_P15_Xe_admit_original_supplementary_root(string selected)
    {
        var entry = Assert.IsType<NativeSelectedEntry>(NativeCompilerSuiteContract.CreateEntry(selected, [selected], NativeCompilerEngine.XeLaTeX, out var error, P));
        Assert.Null(error); Assert.Equal(selected, entry.SelectedPath); Assert.Equal(selected, entry.LatexEntry);
        var job = selected[(selected.LastIndexOf('/') + 1)..^4];
        Assert.Equal(job, entry.JobName); Assert.Contains(job + ".xdv", entry.Outputs);
        Assert.NotNull(NativeCompilerSuiteContract.CreateEntry(selected, [selected], NativeCompilerEngine.XeLaTeX, out _, NativeBiberCompilerContract.Profile));
        Assert.NotNull(NativeCompilerSuiteContract.CreateEntry(selected, [selected], NativeCompilerEngine.XeLaTeX, out _, NativeThesisCompilerContract.Profile));
        Assert.NotNull(NativeCompilerSuiteContract.CreateEntry(selected, [selected], NativeCompilerEngine.XeLaTeX, out _, NativeOriginCompilerContract.Profile));
        foreach (var profile in Enumerable.Range(0, 16).Where(n => n is not (12 or 13 or 14 or 15)).Select(n => (NativeRuntimeProfile)n))
            Assert.Null(NativeCompilerSuiteContract.CreateEntry(selected, [selected], NativeCompilerEngine.XeLaTeX, out _, profile));
        Assert.Null(NativeCompilerSuiteContract.CreateEntry(selected, [selected], NativeCompilerEngine.XeLaTeX, out _));
    }

    [Theory]
    [InlineData("../тези 😀.tex")]
    [InlineData("a/../тези 😀.tex")]
    [InlineData("/тези 😀.tex")]
    [InlineData("a//тези 😀.tex")]
    [InlineData("тези 😀\\input.tex")]
    [InlineData("тези 😀{x}.tex")]
    [InlineData("тези 😀\"x.tex")]
    [InlineData("тези 😀.pdf")]
    public void Expanded_root_keeps_project_and_TeX_syntax_refusals(string selected)
        => Assert.Null(NativeCompilerSuiteContract.CreateEntry(selected, [selected], NativeCompilerEngine.XeLaTeX, out _, P));

    [Fact]
    public void Invalid_UTF16_and_old_overload_whitelists_stay_refused()
    {
        foreach (var selected in new[] { "x\ud800.tex", "x\udc00.tex", "x\ud800x.tex", "x\0.tex" })
            Assert.Null(NativeCompilerSuiteContract.CreateEntry(selected, [selected], NativeCompilerEngine.XeLaTeX, out _, P));
        Assert.NotNull(NativeSelectedEntryContract.ValidateEntry("тези/розділ 😀.tex"));
        Assert.Null(NativeSelectedEntryContract.Create("тези/розділ 😀.tex", ["тези/розділ 😀.tex"], out _));
        Assert.NotNull(NativeSelectedEntryContract.ValidateEntry("тези/розділ 😀.tex", P, NativeCompilerEngine.LuaLaTeX));
        Assert.Null(NativeSelectedEntryContract.ValidateEntry("тези/розділ.tex"));
    }

    [UnicodeFixtureFact]
    public void Exact_inventory_admits_P12_and_rejects_all_component_or_closure_tampering_before_input_bytes()
    {
        var pins = UnicodeFixture.Pins();
        Assert.Equal(P, LocalRuntimeSelection.ProfileFor(pins));
        Assert.Equal(4186, pins.Length); Assert.Equal(229018228L, pins.Sum(p => (long)p.Length));
        Assert.True(NativeCompilerUnicodePins.MatchesComponents(pins.ToDictionary(p => p.Name, StringComparer.Ordinal)));
        foreach (var name in new[] { "xetex.js", "xetex.wasm", "luatex.js", "luatex.wasm", "lualatex.fmt", "harfbuzz.lua", "article.cls", "labi0500.tfm", "sfbi0500.pfb" })
        {
            var tampered = pins.Select(p => p.Name == name ? p with { Sha256 = new('0', 64) } : p).ToArray();
            Assert.Equal(NativeRuntimeIssue.TechnicalHashMismatch, NativeRuntimePackage.Create(new NoInputs(pins.Length), tampered, UnicodeFixture.Limits, P).Issue);
            Assert.Throws<FormatException>(() => LocalRuntimeSelection.ProfileFor(tampered));
        }
        var package = UnicodeFixture.Package;
        Assert.Equal(P, package.Profile); Assert.Equal("local-compiler-unicode", package.Bundle.Id);
        foreach (var e in Enum.GetValues<NativeCompilerEngine>())
        {
            Assert.Equal(NativeCompilerComponents.For(P, e).OutputAdapter, package.AdapterFor(e));
            var descriptor = package.EngineFor(e);
            Assert.Equal(NativeCompilerComponents.For(P, e).WasmHash, descriptor.Hash.Hex);
        }
        Assert.Equal(NativeCompilerUnicodePins.HarfBuzzLuaHash, NativeRuntimePackage.Digest(package.Copy("harfbuzz.lua")).Hex);
        var copy = package.Copy("luatex.wasm"); copy.AsSpan().Fill(0);
        Assert.Equal(NativeCompilerUnicodePins.LuaWasmHash, NativeRuntimePackage.Digest(package.Copy("luatex.wasm")).Hex);
    }

    private sealed class NoInputs(int count) : IReadOnlyList<NativeResourceInput>
    {
        public int Count => count;
        public NativeResourceInput this[int index] => throw new Exception("Untrusted input bytes read before closure admission");
        public IEnumerator<NativeResourceInput> GetEnumerator() => throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

internal static class UnicodeFixture
{
    internal static string? Root => Environment.GetEnvironmentVariable("UNICODE_COMPILER_RUNTIME_DIR");
    internal static readonly NativeRuntimeLimits Limits = LocalRuntimeSelection.LimitsFor(NativeUnicodeCompilerContract.Profile);
    internal static NativeResourcePin[] Pins() => JsonSerializer.Deserialize<NativeResourcePin[]>(File.ReadAllBytes(Path.Combine(Root!, "pins.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    private static readonly Lazy<NativeRuntimePackage> Loaded = new(() =>
    {
        var pins = Pins();
        var input = pins.Select(p => new NativeResourceInput(p.Name, File.ReadAllBytes(Path.Combine(Root!, "files", p.Name)))).ToArray();
        return Assert.IsType<NativeRuntimePackage>(NativeRuntimePackage.Create(input, pins, Limits, NativeUnicodeCompilerContract.Profile).Package);
    });
    internal static NativeRuntimePackage Package => Loaded.Value;
}

public sealed class UnicodeFixtureFactAttribute : FactAttribute
{
    public UnicodeFixtureFactAttribute()
    { if (!File.Exists(Path.Combine(UnicodeFixture.Root ?? "", "pins.json"))) Skip = "Requires exact P12 UNICODE_COMPILER_RUNTIME_DIR; transport replies are modeled, not native proof."; }
}
public sealed class UnicodeFixtureTheoryAttribute : TheoryAttribute
{
    public UnicodeFixtureTheoryAttribute()
    { if (!File.Exists(Path.Combine(UnicodeFixture.Root ?? "", "pins.json"))) Skip = "Requires exact P12 UNICODE_COMPILER_RUNTIME_DIR; transport replies are modeled, not native proof."; }
}
