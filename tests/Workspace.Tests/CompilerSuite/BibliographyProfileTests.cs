using System.Collections;
using System.Text.Json;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Xunit;

namespace Workspace.Tests.CompilerSuite;

public sealed class BibliographyProfileTests
{
    private const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalCompilerBibliography;

    [Fact]
    public void Executable_upgrade_is_explicit_and_old_profiles_keep_their_abi_and_adapter()
    {
        Assert.Equal(7, (int)Profile);
        Assert.True(NativeRuntimeProfiles.HasBibliographyCarry(Profile));
        foreach (var profile in new[] { NativeRuntimeProfile.LocalCompilerSuite, NativeRuntimeProfile.LocalCompilerCompatibility })
        {
            var old = NativeCompilerComponents.For(profile, NativeCompilerEngine.LuaLaTeX);
            Assert.Equal(2, old.NativeAbiVersion);
            Assert.Equal(NativeCompilerSuitePins.LuaWasmHash, old.WasmHash);
            Assert.Equal(NativeCompilerSuiteContract.LuaOutputAdapter, old.OutputAdapter);
            Assert.False(NativeRuntimeProfiles.HasBibliographyCarry(profile));
        }
        var lua = NativeCompilerComponents.For(Profile, NativeCompilerEngine.LuaLaTeX);
        Assert.Equal(3, lua.NativeAbiVersion);
        Assert.Equal(NativeCompilerBibliographyPins.LuaWasmHash, lua.WasmHash);
        Assert.NotEqual(NativeCompilerSuiteContract.LuaOutputAdapter, lua.OutputAdapter);
        Assert.Equal(NativeCompilerCompatibilityPins.LuaFormatHash, lua.FormatHash);
        foreach (var engine in Enum.GetValues<NativeCompilerEngine>())
        {
            var next = NativeCompilerComponents.For(Profile, engine);
            var previous = NativeCompilerComponents.For(NativeRuntimeProfile.LocalCompilerCompatibility, engine);
            Assert.Equal(previous.FormatHash, next.FormatHash);
            if (engine != NativeCompilerEngine.LuaLaTeX) Assert.Equal(previous, next);
            Assert.True(NativeCompilerSuiteContract.CreatePlan(engine, profile: Profile).IsValid);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeCompilerComponents.For((NativeRuntimeProfile)int.MaxValue, NativeCompilerEngine.LuaLaTeX));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeCompilerComponents.For(Profile, (NativeCompilerEngine)int.MaxValue));
    }

    [Fact]
    public void Unknown_profile_rejects_before_touching_untrusted_counts()
    {
        var unknown = (NativeRuntimeProfile)int.MaxValue;
        var result = NativeRuntimePackage.Create(new Unreadable<NativeResourceInput>(), new Unreadable<NativeResourcePin>(),
            LocalRuntimeSelection.LimitsFor(Profile) with { Profile = unknown }, unknown);
        Assert.Equal(NativeRuntimeIssue.InvalidLimits, result.Issue);
    }

    [BibliographyProfileFixtureFact]
    public void Actual_closed_inventory_selects_profile7_and_roundtrips_its_metadata()
    {
        var pins = Pins();
        Assert.Equal(3703, pins.Length);
        Assert.Equal(NativeCompilerBibliographyPins.TotalBytes, pins.Sum(p => (long)p.Length));
        Assert.Equal(Profile, LocalRuntimeSelection.ProfileFor(pins));
        var resources = LocalRuntimeSelection.Resources([.. pins]);
        var inventory = LocalRuntimeSelection.Inventory(resources);
        var mapping = new InstalledNativeRuntimeMapping(Guid.NewGuid(), inventory.Identity.Digest.Hex, resources, Profile);
        var encoded = LocalRuntimeSelection.Encode(mapping);
        Assert.Equal(encoded, LocalRuntimeSelection.Encode(LocalRuntimeSelection.Decode(encoded)));
        Assert.Throws<FormatException>(() => LocalRuntimeSelection.Encode(mapping with { Profile = NativeRuntimeProfile.LocalCompilerCompatibility }));
    }

    [BibliographyProfileFixtureFact]
    public void Old_lua_executable_or_wrong_bst_never_enters_new_profile()
    {
        var pins = Pins();
        foreach (string name in new[] { "luatex.wasm", "biblatex.bst" })
        {
            var bad = pins.Select(p => p.Name == name ? p with { Sha256 = name == "luatex.wasm"
                ? NativeCompilerSuitePins.LuaWasmHash : new string('0', 64) } : p).ToArray();
            Assert.Throws<FormatException>(() => LocalRuntimeSelection.ProfileFor(bad));
        }
    }

    [BibliographyProfileFixtureFact]
    public void Genuine_component_bytes_bind_the_new_lua_engine_and_adapter()
    {
        var pins = Pins();
        var inputs = pins.Select(p => new NativeResourceInput(p.Name, File.ReadAllBytes(Path.Combine(Root!, "files", p.Name)))).ToArray();
        var result = NativeRuntimePackage.Create(inputs, pins, LocalRuntimeSelection.LimitsFor(Profile), Profile);
        var package = Assert.IsType<NativeRuntimePackage>(result.Package);
        Assert.Equal("abi3", package.EngineFor(NativeCompilerEngine.LuaLaTeX).Version);
        Assert.Equal(NativeCompilerBibliographyPins.LuaWasmHash, package.EngineFor(NativeCompilerEngine.LuaLaTeX).Hash.Hex);
        Assert.Equal(NativeCompilerComponents.For(Profile, NativeCompilerEngine.LuaLaTeX).OutputAdapter,
            package.AdapterFor(NativeCompilerEngine.LuaLaTeX));
        Assert.Equal("3", package.Bundle.Version);
    }

    private static string? Root => Environment.GetEnvironmentVariable("COMPILER_BIBLIOGRAPHY_RUNTIME_DIR");
    private static NativeResourcePin[] Pins() => LocalRuntimeSelection.ParsePins(File.ReadAllBytes(Path.Combine(Root!, "pins.json"))).ToArray();
    private sealed class Unreadable<T> : IReadOnlyList<T>
    {
        public int Count => throw new InvalidOperationException("Invalid profile inspected input");
        public T this[int index] => throw new InvalidOperationException();
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

public sealed class BibliographyProfileFixtureFactAttribute : FactAttribute
{
    public BibliographyProfileFixtureFactAttribute()
    {
        if (!File.Exists(Path.Combine(Environment.GetEnvironmentVariable("COMPILER_BIBLIOGRAPHY_RUNTIME_DIR") ?? "", "pins.json")))
            Skip = "Requires explicit real profile7 runtime fixture; not native/browser execution evidence.";
    }
}
