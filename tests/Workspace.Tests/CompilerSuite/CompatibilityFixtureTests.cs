using System.Text.Json;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Client.RuntimeAssets;
using Workspace.Tests.CompilationNative;
using Xunit;

namespace Workspace.Tests.CompilerSuite;

// Real retained profile5 pin metadata, no fabricated profile6 success or native execution.
public sealed class CompatibilityFixtureTests
{
    private static NativeResourcePin[] OldPins() => LocalRuntimeSelection.ParsePins(File.ReadAllBytes(Path.Combine(
        Environment.GetEnvironmentVariable("COMPILER_SUITE_RUNTIME_DIR")!, "pins.json"))).ToArray();
    private static byte[] Json(NativeResourcePin[] pins) => JsonSerializer.SerializeToUtf8Bytes(pins,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    [SuiteFixtureFact]
    public async Task Original_suite_manual_selection_stays_v1_and_cannot_be_relabelled_as_v2()
    {
        var pins = OldPins();
        Assert.Equal(NativeRuntimeProfile.LocalCompilerSuite, LocalRuntimeSelection.ProfileFor(pins));
        Assert.Equal(3677, pins.Length); Assert.Equal(187630023L, pins.Sum(p => (long)p.Length));
        Assert.True(NativeRuntimeProfiles.MatchesSuiteComponents(NativeRuntimeProfile.LocalCompilerSuite,
            pins.ToDictionary(p => p.Name, StringComparer.Ordinal)));
        var resources = LocalRuntimeSelection.Resources([.. pins]);
        var inventory = LocalRuntimeSelection.Inventory(resources);
        var mapping = new InstalledNativeRuntimeMapping(Guid.NewGuid(), inventory.Identity.Digest.Hex, resources, NativeRuntimeProfile.LocalCompilerSuite);
        var encoded = LocalRuntimeSelection.Encode(mapping);
        Assert.Equal(encoded, LocalRuntimeSelection.Encode(LocalRuntimeSelection.Decode(encoded)));
        Assert.Throws<FormatException>(() => LocalRuntimeSelection.Encode(mapping with { Profile = NativeRuntimeProfile.LocalCompilerCompatibility }));
        Assert.Contains("\"profile\":5", encoded);
        var forged = encoded.Replace("\"profile\":5", "\"profile\":6", StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => LocalRuntimeSelection.Decode(forged));
        var backend = new SetupBackend(); var selection = new SetupSelection { Value = forged };
        await using var providers = new LocalInstalledRuntimeProviders(new(backend), selection, new(new SetupBackend()), new PdfSelection());
        var read = await providers.ReadCompilerAsync();
        Assert.Null(read.Package); Assert.Equal(RuntimeAssetStatus.Corrupt, read.Store.Status);
        Assert.Equal(0, backend.Calls); Assert.Equal(forged, selection.Value); Assert.Equal(0, selection.Writes);
    }

    [SuiteFixtureFact]
    public async Task Old_inventory_never_enters_expanded_profile_or_reads_store_bytes()
    {
        var pins = OldPins(); var profile = NativeRuntimeProfile.LocalCompilerCompatibility;
        var creation = NativeRuntimePackage.Create(new SuiteContractTests.NoAccess(pins.Length), pins, LocalRuntimeSelection.LimitsFor(profile), profile);
        Assert.Null(creation.Package);
        if (!NativeCompilerCompatibilityPins.Ready) Assert.Equal(NativeRuntimeIssue.Unavailable, creation.Issue);
        var backend = new SetupBackend();
        await using var store = new RuntimeAssetStore(backend);
        var mapping = new InstalledNativeRuntimeMapping(Guid.NewGuid(), new string('a', 64), LocalRuntimeSelection.Resources([.. pins]), profile);
        var provider = new InstalledNativeRuntimeProvider(store, mapping, LocalRuntimeSelection.BudgetFor(profile), LocalRuntimeSelection.LimitsFor(profile));
        var read = await provider.ReadPackageAsync();
        Assert.Null(read.Package); Assert.Equal(NativeRuntimeIssue.InvalidInventory, read.Issue);
        Assert.Equal(RuntimeAssetStatus.InvalidInput, read.Store.Status); Assert.Equal(0, backend.Calls);
    }

    [SuiteFixtureFact]
    public void Old_closure_and_all_four_formats_remain_bound_before_resource_access()
    {
        var pins = OldPins(); var profile = NativeRuntimeProfile.LocalCompilerSuite;
        foreach (string name in new[] { "pdflatex.fmt", "latex-dvi.fmt", "xelatex.fmt", "lualatex.fmt", "engine.wasm",
            "xetex.wasm", "luatex.wasm", "dvi-converter.wasm", "synctex-query.wasm", "hyphen.cfg" })
        {
            Assert.Contains(pins, p => p.Name == name);
            var tampered = pins.Select(p => p.Name == name ? p with { Sha256 = new string('0', 64) } : p).ToArray();
            Assert.Throws<FormatException>(() => LocalRuntimeSelection.ParsePins(Json(tampered)));
            var result = NativeRuntimePackage.Create(new SuiteContractTests.NoAccess(pins.Length), tampered, LocalRuntimeSelection.LimitsFor(profile), profile);
            Assert.Null(result.Package); Assert.Equal(NativeRuntimeIssue.TechnicalHashMismatch, result.Issue);
        }
        foreach (var changed in new[] { pins[..^1], pins.Append(new NativeResourcePin("extra.sty", 0, new string('0', 64))).ToArray(),
            pins.Select((p, i) => i == 0 ? p with { Length = p.Length + 1 } : p).ToArray() })
        {
            Assert.Throws<FormatException>(() => LocalRuntimeSelection.ParsePins(Json(changed)));
            Assert.Null(NativeRuntimePackage.Create(new SuiteContractTests.NoAccess(changed.Length), changed,
                LocalRuntimeSelection.LimitsFor(profile), profile).Package);
        }
    }
}
