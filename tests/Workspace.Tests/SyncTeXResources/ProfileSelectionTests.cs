using System.Collections;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Client.RuntimeAssets;
using Workspace.Tests.CompilationNative;
using Xunit;

namespace Workspace.Tests.SyncTeXResources;

public sealed class ProfileSelectionTests
{
    [Theory]
    [InlineData(NativeRuntimeProfile.Legacy, 378, 1, 715877, NativeRuntimePackage.FormatHash)]
    [InlineData(NativeRuntimeProfile.LocalLatex2023, 481, 2, 22341856, NativeRuntimeProfiles.ModernFormatHash)]
    [InlineData(NativeRuntimeProfile.LocalLatex2023Authoring, 497, 3, 22341856, NativeRuntimeProfiles.ModernFormatHash)]
    [InlineData(NativeRuntimeProfile.LocalLatex2023SyncTeX, 499, 6, 22342013, NativeRuntimeProfiles.SyncTeXFormatHash)]
    [InlineData(NativeRuntimeProfile.LocalLatex2023Dvi, 507, 6, 22342013, NativeRuntimeProfiles.SyncTeXFormatHash)]
    public void Trusted_profiles_keep_exact_counts_protocols_formats_and_engine_pins(NativeRuntimeProfile profile, int count, int protocol, int formatLength, string formatHash)
    {
        Assert.True(NativeRuntimeProfiles.Known(profile));
        Assert.Equal(count, NativeRuntimeProfiles.InstalledCount(profile));
        Assert.Equal(protocol, NativeRuntimeProfiles.ProtocolVersion(profile));
        Assert.Equal(formatLength, NativeRuntimeProfiles.FormatLength(profile));
        Assert.Equal(formatHash, NativeRuntimeProfiles.FormatHash(profile));
        bool sync = profile is NativeRuntimeProfile.LocalLatex2023SyncTeX or NativeRuntimeProfile.LocalLatex2023Dvi;
        Assert.Equal(sync ? 72583 : 72275, NativeRuntimeProfiles.CompilerLoaderLength(profile));
        Assert.Equal(sync ? 1782796 : 1779444, NativeRuntimeProfiles.CompilerWasmLength(profile));
        Assert.Equal(sync ? NativeRuntimeProfiles.SyncTeXCompilerLoaderHash : NativeRuntimePackage.LoaderHash, NativeRuntimeProfiles.CompilerLoaderHash(profile));
        Assert.Equal(sync ? NativeRuntimeProfiles.SyncTeXCompilerWasmHash : NativeRuntimePackage.WasmHash, NativeRuntimeProfiles.CompilerWasmHash(profile));
        Assert.Equal(profile != NativeRuntimeProfile.Legacy, NativeRuntimeProfiles.Modern(profile));
        Assert.Equal(profile == NativeRuntimeProfile.Legacy ? 16777216 : 33554432, NativeRuntimeProfiles.MaxFileBytes(profile));
        Assert.Equal(profile == NativeRuntimeProfile.Legacy ? 33554432 : 67108864, NativeRuntimeProfiles.MaxTotalBytes(profile));
    }

    [Fact]
    public void Exact_old_and_new_public_closures_select_only_their_own_profile_and_roundtrip_v1_metadata()
    {
        Assert.Equal(NativeRuntimeProfile.LocalLatex2023Authoring, LocalRuntimeSelection.Profile);
        Assert.Equal(new NativeRuntimeLimits(512, 33554432, 67108864, NativeRuntimeProfile.LocalLatex2023Authoring), LocalRuntimeSelection.Limits);
        foreach (var pins in new[] { SyncTeXResourceFixture.OldPins(), SyncTeXResourceFixture.Pins() })
        {
            var profile = LocalRuntimeSelection.ProfileFor(pins.Reverse());
            Assert.Equal(pins.Length == 497 ? NativeRuntimeProfile.LocalLatex2023Authoring : SyncTeXResourceFixture.Profile, profile);
            Assert.Equal(pins, LocalRuntimeSelection.ParsePins(SyncTeXResourceFixture.Json(pins)));
            Assert.Equal(new NativeRuntimeLimits(512, 33554432, 67108864, profile), LocalRuntimeSelection.LimitsFor(profile));
            var resources = LocalRuntimeSelection.Resources(pins.ToImmutableArray());
            var inventory = LocalRuntimeSelection.Inventory(resources);
            var mapping = new InstalledNativeRuntimeMapping(Guid.NewGuid(), inventory.Identity.Digest.Hex, resources, profile);
            var text = LocalRuntimeSelection.Encode(mapping);
            var decoded = LocalRuntimeSelection.Decode(text);
            Assert.Equal(mapping.InstallationId, decoded.InstallationId);
            Assert.Equal(mapping.InventoryDigest, decoded.InventoryDigest);
            Assert.Equal(mapping.Profile, decoded.Profile);
            Assert.Equal(mapping.Resources.ToArray(), decoded.Resources.ToArray());
            Assert.Equal(text, LocalRuntimeSelection.Encode(decoded));
        }
        Assert.Equal(36480484L, SyncTeXResourceFixture.Pins().Sum(p => (long)p.Length));
        var closure = string.Join("\n", SyncTeXResourceFixture.Pins().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => FormattableString.Invariant($"{p.Name}\t{p.Length}\t{p.Sha256}")));
        Assert.Equal(NativeRuntimeProfiles.SyncTeXClosureHash, NativeRuntimePackage.Digest(Encoding.UTF8.GetBytes(closure)).Hex);
        Assert.Equal(SyncTeXResourceFixture.Identity, NativeRuntimePackage.Digest(Encoding.UTF8.GetBytes(
            "native-runtime-profile-v1\nlocal-latex-2023-11-01-p1-synctex\n" + NativeRuntimeProfiles.SyncTeXClosureHash)).Hex);
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(1)] [InlineData(6)] [InlineData(99)]
    public void Local_limits_reject_every_nonlocal_profile(int value)
        => Assert.Throws<FormatException>(() => LocalRuntimeSelection.LimitsFor((NativeRuntimeProfile)value));

    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void Local_limits_support_only_explicit_local_profiles(int value)
        => Assert.Equal(new NativeRuntimeLimits(512, 33554432, 67108864, (NativeRuntimeProfile)value),
            LocalRuntimeSelection.LimitsFor((NativeRuntimeProfile)value));

    internal static IEnumerable<NativeResourcePin[]> BadPins()
    {
        var pins = SyncTeXResourceFixture.Pins();
        yield return pins[..^1];
        yield return [.. pins, pins[0]];
        yield return pins.Select(p => p.Name == "engine.wasm" ? new(p.Name, 1779444, NativeRuntimePackage.WasmHash) : p).ToArray();
        yield return pins.Select(p => p.Name == "pdflatex.fmt" ? new(p.Name, NativeRuntimeProfiles.ModernFormatLength, NativeRuntimeProfiles.ModernFormatHash) : p).ToArray();
        yield return pins.Select(p => p.Name == "synctex-query.wasm" ? p with { Sha256 = new string('0', 64) } : p).ToArray();
        yield return pins.Select(p => p.Name == "synctex-query.js" ? p with { Name = "foreign.js" } : p).ToArray();
        yield return SyncTeXResourceFixture.OldPins().Select(p => p.Name == "engine.js" ? new(p.Name, 72583, NativeRuntimeProfiles.SyncTeXCompilerLoaderHash) : p).ToArray();
    }

    [Fact]
    public async Task Missing_mixed_and_forged_pins_refuse_before_selected_bytes_or_store_IO()
    {
        int reads = 0;
        var backend = new SetupBackend(); var selection = new SetupSelection { Value = "prior" };
        await using var setup = new NativeRuntimeSetup(new(backend), selection);
        foreach (var pins in BadPins())
        {
            Assert.Throws<FormatException>(() => LocalRuntimeSelection.ProfileFor(pins));
            var json = SyncTeXResourceFixture.Json(pins);
            Assert.Throws<FormatException>(() => LocalRuntimeSelection.ParsePins(json));
            await setup.InstallAsync(json, SyncTeXResourceFixture.Descriptors(pins, () => reads++));
            Assert.Equal(RuntimeSetupState.InvalidSelection, setup.State);
        }
        Assert.Equal(0, reads); Assert.Equal(0, backend.Calls); Assert.Equal("prior", selection.Value);
    }

    [Theory]
    [InlineData(2, 3)] [InlineData(3, 2)] [InlineData(3, 1)] [InlineData(2, 99)]
    public async Task Declared_profile_must_match_complete_metadata_closure_before_store_reads(int actual, int declared)
    {
        var pins = actual == 2 ? SyncTeXResourceFixture.OldPins() : SyncTeXResourceFixture.Pins();
        var resources = LocalRuntimeSelection.Resources(pins.ToImmutableArray());
        var mapping = new InstalledNativeRuntimeMapping(Guid.NewGuid(), LocalRuntimeSelection.Inventory(resources).Identity.Digest.Hex, resources, (NativeRuntimeProfile)actual);
        var node = JsonNode.Parse(LocalRuntimeSelection.Encode(mapping))!; node["profile"] = declared;
        string forged = node.ToJsonString();
        Assert.Throws<FormatException>(() => LocalRuntimeSelection.Decode(forged));
        var backend = new SetupBackend(); var selection = new SetupSelection { Value = forged };
        await using var setup = new NativeRuntimeSetup(new(backend), selection);
        await setup.RecheckAsync(); Assert.Equal(RuntimeSetupState.InvalidSelection, setup.State);
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task Invalid_installed_pins_and_package_closure_fail_before_any_resource_access()
    {
        foreach (var pins in BadPins().Where(p => p.Length == 499))
        {
            var result = NativeRuntimePackage.Create(new NeverReadInputs(499), pins,
                LocalRuntimeSelection.LimitsFor(SyncTeXResourceFixture.Profile), SyncTeXResourceFixture.Profile);
            Assert.Null(result.Package); Assert.Equal(NativeRuntimeIssue.TechnicalHashMismatch, result.Issue);
            var resources = LocalRuntimeSelection.Resources(pins.ToImmutableArray());
            var mapping = new InstalledNativeRuntimeMapping(Guid.NewGuid(), new string('a', 64), resources, SyncTeXResourceFixture.Profile);
            var backend = new SetupBackend(); await using var store = new RuntimeAssetStore(backend);
            var read = await new InstalledNativeRuntimeProvider(store, mapping, LocalRuntimeSelection.Budget,
                LocalRuntimeSelection.LimitsFor(SyncTeXResourceFixture.Profile)).ReadPackageAsync();
            Assert.Null(read.Package); Assert.Equal(RuntimeAssetStatus.InvalidInput, read.Store.Status); Assert.Equal(0, backend.Calls);
        }
    }

    private sealed class NeverReadInputs(int count) : IReadOnlyList<NativeResourceInput>
    {
        public int Count => count;
        public NativeResourceInput this[int index] => throw new InvalidOperationException("Untrusted closure touched resource input.");
        public IEnumerator<NativeResourceInput> GetEnumerator() => throw new InvalidOperationException("Unexpected enumeration.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
