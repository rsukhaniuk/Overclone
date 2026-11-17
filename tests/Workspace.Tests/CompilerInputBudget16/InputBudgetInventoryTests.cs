using System.Globalization;
using System.Text;
using System.Text.Json;
using Client.Compilation.Native;
using Xunit;

namespace Workspace.Tests.CompilerInputBudget16;

public sealed class InputBudgetInventoryTests
{
    [InputBudgetFixtureFact]
    public void Actual_declared_inventory_projects_back_to_exact_parent_including_original_catalogue()
    {
        var pins = InputBudgetFixture.Pins; var dictionary = pins.ToDictionary(p => p.Name, StringComparer.Ordinal);
        Assert.Equal(NativeCompilerInputBudgetPins.ResourceCount, pins.Length);
        Assert.Equal(NativeCompilerInputBudgetPins.TotalBytes, pins.Sum(p => (long)p.Length));
        Assert.Equal(NativeCompilerInputBudgetPins.ClosureHash, Closure(pins));
        Assert.Equal(NativeCompilerInputBudgetPins.Ready, NativeCompilerInputBudgetPins.MatchesComponents(dictionary));
        foreach (var p in NativeCompilerInputBudgetPins.AdditionalResources) Assert.Equal(p, dictionary[p.Name]);
        var catalog = dictionary[NativeCompilerSuitePins.XeCatalogResourceName];
        Assert.Equal(NativeCompilerInputBudgetPins.XeCatalogLength, catalog.Length); Assert.Equal(NativeCompilerInputBudgetPins.XeCatalogHash, catalog.Sha256);
        foreach (var p in NativeCompilerInputBudgetPins.AdditionalResources) dictionary.Remove(p.Name);
        Restore("dvi-map-converter.js", NativeCompilerOriginPins.DviConverterLoaderLength, NativeCompilerOriginPins.DviConverterLoaderHash);
        Restore("dvi-map-converter.wasm", NativeCompilerOriginPins.DviConverterWasmLength, NativeCompilerOriginPins.DviConverterWasmHash);
        Restore("xdv-map-converter.js", NativeCompilerOriginPins.XeConverterLoaderLength, NativeCompilerOriginPins.XeConverterLoaderHash);
        Restore("xdv-map-converter.wasm", NativeCompilerOriginPins.XeConverterWasmLength, NativeCompilerOriginPins.XeConverterWasmHash);
        Restore(NativeCompilerSuitePins.XeCatalogResourceName, NativeCompilerInputBudgetPins.ParentXeCatalogLength, NativeCompilerInputBudgetPins.ParentXeCatalogHash);
        Assert.Equal(NativeCompilerOriginPins.ResourceCount, dictionary.Count); Assert.Equal(NativeCompilerOriginPins.TotalBytes, dictionary.Values.Sum(p => (long)p.Length));
        Assert.Equal(NativeCompilerOriginPins.ClosureHash, Closure(dictionary.Values)); Assert.True(NativeCompilerOriginPins.MatchesComponents(dictionary));
        void Restore(string name, int bytes, string hash) => dictionary[name] = new(name, bytes, hash);
    }
    [InputBudgetFixtureTheory]
    [InlineData("missing")][InlineData("extra")][InlineData("font-alias")][InlineData("catalogue")]
    [InlineData("old-converter")][InlineData("mixed-loader")][InlineData("unrelated-parent-resource")]
    public void Wrong_closures_aliases_and_mixed_components_never_admit(string corruption)
    {
        var pins = InputBudgetFixture.Pins.ToDictionary(p => p.Name, StringComparer.Ordinal);
        switch (corruption)
        {
            case "missing": pins.Remove("XITS-Regular.otf"); break;
            case "extra": pins.Add("invented.sty", new("invented.sty", 1, new string('0', 64))); break;
            case "font-alias": var face = pins["XITS-Regular.otf"]; pins.Remove(face.Name); pins.Add("TimesNewRoman.otf", face with { Name = "TimesNewRoman.otf" }); break;
            case "catalogue": pins["xetexfontlist.txt"] = new("xetexfontlist.txt", NativeCompilerInputBudgetPins.ParentXeCatalogLength, NativeCompilerInputBudgetPins.ParentXeCatalogHash); break;
            case "old-converter": pins["xdv-map-converter.wasm"] = new("xdv-map-converter.wasm", NativeCompilerOriginPins.XeConverterWasmLength, NativeCompilerOriginPins.XeConverterWasmHash); break;
            case "mixed-loader": pins["dvi-map-converter.js"] = new("dvi-map-converter.js", NativeCompilerOriginPins.DviConverterLoaderLength, NativeCompilerOriginPins.DviConverterLoaderHash); break;
            default: var article = pins["article.cls"]; pins[article.Name] = article with { Sha256 = new string('0', 64) }; break;
        }
        Assert.NotEqual(NativeCompilerInputBudgetPins.ClosureHash, Closure(pins.Values));
        Assert.False(NativeCompilerInputBudgetPins.MatchesComponents(pins));
    }
    [InputBudgetReadyFixtureFact]
    public void Ready_exact_fixture_loads_without_changing_parent_engine_or_new_converter_bindings()
    {
        var pins = InputBudgetFixture.Pins;
        var limits = new NativeRuntimeLimits(NativeCompilerSuiteContract.Files, NativeCompilerSuiteContract.FileBytes, NativeCompilerSuiteContract.TotalBytes, NativeInputBudgetContract.Profile);
        var loaded = NativeRuntimePackage.Create(pins.Select(p => new NativeResourceInput(p.Name, File.ReadAllBytes(Path.Combine(InputBudgetFixture.Root!, "files", p.Name)))).ToArray(), pins, limits, NativeInputBudgetContract.Profile);
        Assert.Null(loaded.Issue); var package = Assert.IsType<NativeRuntimePackage>(loaded.Package);
        Assert.Equal("local-compiler-input-budget", package.Bundle.Id); Assert.True(package.HasBiber);
        foreach (var engine in Enum.GetValues<NativeCompilerEngine>())
        {
            Assert.Equal(NativeInputBudgetContract.Component(engine).OutputAdapter, package.AdapterFor(engine));
            Assert.Equal(NativeOriginCompilerContract.Component(engine).WasmHash, package.EngineFor(engine).Hash.Hex);
        }
    }
    private static string Closure(IEnumerable<NativeResourcePin> pins) => NativeRuntimePackage.Digest(Encoding.UTF8.GetBytes(string.Join("\n", pins.OrderBy(p => p.Name, StringComparer.Ordinal)
        .Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Name}\t{p.Length}\t{p.Sha256}"))))).Hex;
}
internal static class InputBudgetFixture
{
    public static string? Root => Environment.GetEnvironmentVariable("INPUT_BUDGET_COMPILER_RUNTIME_DIR");
    public static bool Available => File.Exists(Path.Combine(Root ?? "", "pins.json"));
    // This reads untrusted test metadata only. Runtime admission remains gated
    // by NativeRuntimePackage.Create and the root-owned Ready decision.
    public static NativeResourcePin[] Pins
    {
        get
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root!, "pins.json")));
            return json.RootElement.EnumerateArray().Select(p => new NativeResourcePin(p.GetProperty("name").GetString()!, p.GetProperty("length").GetInt32(), p.GetProperty("sha256").GetString()!)).ToArray();
        }
    }
}
public sealed class InputBudgetFixtureFactAttribute : FactAttribute
{ public InputBudgetFixtureFactAttribute() { if (!InputBudgetFixture.Available) Skip = "Requires exact INPUT_BUDGET_COMPILER_RUNTIME_DIR metadata; no runtime acceptance inferred."; } }
public sealed class InputBudgetFixtureTheoryAttribute : TheoryAttribute
{ public InputBudgetFixtureTheoryAttribute() { if (!InputBudgetFixture.Available) Skip = "Requires exact INPUT_BUDGET_COMPILER_RUNTIME_DIR metadata; no runtime acceptance inferred."; } }
public sealed class InputBudgetReadyFixtureFactAttribute : FactAttribute
{ public InputBudgetReadyFixtureFactAttribute() { if (!NativeCompilerInputBudgetPins.Ready || !InputBudgetFixture.Available) Skip = "Requires root-reviewed Ready P16 and exact INPUT_BUDGET_COMPILER_RUNTIME_DIR; no test readiness bypass."; } }
