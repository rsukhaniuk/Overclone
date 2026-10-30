using System.Text.Json;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Xunit;

namespace Workspace.Tests.CompilerSuite;

// Real package bytes and trust admission. Browser runs establish actual compilation.
public sealed class ExpandedSuiteFixtureTests
{
    private const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalCompilerCompatibility;
    private static string Directory => Environment.GetEnvironmentVariable("COMPILER_COMPATIBILITY_RUNTIME_DIR")!;
    private static NativeResourcePin[] Pins() => LocalRuntimeSelection.ParsePins(File.ReadAllBytes(Path.Combine(Directory, "pins.json"))).ToArray();
    private static byte[] Json(NativeResourcePin[] pins) => JsonSerializer.SerializeToUtf8Bytes(pins,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    [ExpandedSuiteFixtureFact]
    public async Task Expanded_archive_binds_all_bytes_and_round_trips_its_distinct_profile()
    {
        var pins = Pins();
        Assert.Equal(Profile, LocalRuntimeSelection.ProfileFor(pins));
        Assert.Equal(NativeRuntimeProfiles.InstalledCount(Profile), pins.Length);
        Assert.Equal(NativeCompilerCompatibilityPins.TotalBytes, pins.Sum(p => p.Length));
        var input = pins.Select(p => new NativeResourceInput(p.Name, File.ReadAllBytes(Path.Combine(Directory, "files", p.Name)))).ToArray();
        var package = Assert.IsType<NativeRuntimePackage>(NativeRuntimePackage.Create(input, pins, LocalRuntimeSelection.LimitsFor(Profile), Profile).Package);
        Assert.Equal("2", package.Bundle.Version);
        Assert.Equal(Profile, package.Profile);
        foreach (string name in new[] { "hycolor.sty", "uniquecounter.sty", "pzdr.tfm" })
            Assert.Contains(pins, p => p.Name == name);
        var resources = LocalRuntimeSelection.Resources([.. pins]);
        var mapping = new InstalledNativeRuntimeMapping(Guid.NewGuid(), LocalRuntimeSelection.Inventory(resources).Identity.Digest.Hex, resources, Profile);
        Assert.Equal(mapping.Profile, LocalRuntimeSelection.Decode(LocalRuntimeSelection.Encode(mapping)).Profile);
        await using var archive = new ZipRuntimeArchive();
        var path = Path.Combine(Directory, "compiler-suite.zip");
        await archive.PrepareAsync(new FileInfo(path).Length, _ => Task.FromResult<Stream>(File.OpenRead(path)), default);
        Assert.Equal(pins.Length, archive.Files.Count);
        Assert.Equal(1, archive.PinsStreamsOpened);
        var format = archive.Files.Single(f => f.Name == "xelatex.fmt");
        var bytes = await format.Read(default);
        Assert.Equal(format.Length, bytes.Length);
        Assert.Equal(NativeCompilerCompatibilityPins.XeFormatHash, NativeRuntimePackage.Digest(bytes).Hex);
        // Copy mutation cannot alter the application-owned genuine format.
        var copy = package.Copy("pdflatex.fmt"); copy[0] ^= 1;
        Assert.Equal(NativeCompilerCompatibilityPins.PdfFormatHash, NativeRuntimePackage.Digest(package.Copy("pdflatex.fmt")).Hex);
    }

    [ExpandedSuiteFixtureFact]
    public void Tampered_package_font_or_format_pins_reject_before_input_access()
    {
        var pins = Pins();
        foreach (string name in new[] { "hycolor.sty", "hyphen.cfg", "pdflatex.fmt", "latex-dvi.fmt", "xelatex.fmt", "lualatex.fmt" })
        {
            Assert.Contains(pins, p => p.Name == name);
            var changed = pins.Select(p => p.Name == name ? p with { Sha256 = new string('0', 64) } : p).ToArray();
            Assert.Throws<FormatException>(() => LocalRuntimeSelection.ParsePins(Json(changed)));
            var result = NativeRuntimePackage.Create(new SuiteContractTests.NoAccess(pins.Length), changed, LocalRuntimeSelection.LimitsFor(Profile), Profile);
            Assert.Null(result.Package);
            Assert.Equal(NativeRuntimeIssue.TechnicalHashMismatch, result.Issue);
        }
        Assert.Throws<FormatException>(() => LocalRuntimeSelection.ParsePins(Json(pins[..^1])));
        Assert.Throws<FormatException>(() => LocalRuntimeSelection.ParsePins(Json(pins.Append(new("unreviewed.sty", 0, new('0', 64))).ToArray())));
    }
}

public sealed class ExpandedSuiteFixtureFactAttribute : FactAttribute
{
    public ExpandedSuiteFixtureFactAttribute()
    {
        if (!NativeCompilerCompatibilityPins.Ready || !File.Exists(Path.Combine(Environment.GetEnvironmentVariable("COMPILER_COMPATIBILITY_RUNTIME_DIR") ?? "", "pins.json")))
            Skip = "Reviewed expanded compiler-suite fixture unavailable; no invented input or native substitute.";
    }
}
