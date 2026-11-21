using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Xunit;

namespace Workspace.Tests.CompilationNative;

public sealed class NativePdfOrigins20Tests
{
    private const NativeRuntimeProfile Current = NativeRuntimeProfile.LocalCompilerPdfOrigins;
    private const NativeRuntimeProfile Parent = NativeRuntimeProfile.LocalCompilerDviGraphics;

    [Fact]
    public async Task Direct_geometry_is_admitted_only_for_the_exact_pdftex_cell()
    {
        Assert.Equal(NativeCompilerPdfOriginPins.TotalBytes, LocalRuntimeSelection.LimitsFor(Current).MaxTotalBytes);
        Assert.NotNull(BundledRuntimeAssets.SelectedPdfCapacity);
        Assert.True(NativeNavigationContract.IsMapped(Current, 23, NativeCompilerEngine.PdfLaTeX));
        Assert.False(NativeNavigationContract.IsMapped(Parent, 23, NativeCompilerEngine.PdfLaTeX));
        Assert.False(NativeNavigationContract.IsDirectPdfMapped(Current, 6, NativeCompilerEngine.PdfLaTeX));
        Assert.False(NativeNavigationContract.IsDirectPdfMapped(Current, 23, NativeCompilerEngine.LaTeXDvi));
        var pdf = NativeCompilerComponents.For(Current, NativeCompilerEngine.PdfLaTeX);
        var old = NativeCompilerComponents.For(Parent, NativeCompilerEngine.PdfLaTeX);
        Assert.Equal(pdf.FormatHash, old.FormatHash);
        Assert.NotEqual(pdf.OutputAdapter, old.OutputAdapter);
        Assert.Equal(old.QueryWasmHash, pdf.QueryWasmHash);
        foreach (var engine in Enum.GetValues<NativeCompilerEngine>().Where(e => e != NativeCompilerEngine.PdfLaTeX))
            Assert.Equal(NativeCompilerComponents.For(Parent, engine), NativeCompilerComponents.For(Current, engine));
        Assert.True(NativeSelectedPdfContract.MatchesRuntime(Current, NativeCompilerPdfOriginPins.RuntimeIdentity));
        Assert.False(NativeSelectedPdfContract.MatchesRuntime(Current, NativeCompilerDviGraphicsPins.RuntimeIdentity));
        await using var ordinary = new ZipRuntimeArchive();
        await Assert.ThrowsAsync<InvalidDataException>(() => ordinary.PrepareAsync(NativeCompilerPdfOriginPins.ArchiveLength,
            _ => throw new InvalidOperationException("A different archive profile must refuse before opening the stream."), default));
    }

    [PinnedArchiveFact]
    public async Task Exact_inventory_loads_the_pinned_direct_producer()
    {
        var root = Environment.GetEnvironmentVariable("PDFTEX_ORIGIN20_RUNTIME_DIR")!;
        var pins = LocalRuntimeSelection.ParsePins(await File.ReadAllBytesAsync(Path.Combine(root, "pins.json")));
        var package = Assert.IsType<NativeRuntimePackage>(NativeRuntimePackage.Create(pins.Select(pin => new NativeResourceInput(pin.Name,
            File.ReadAllBytes(Path.Combine(root, "files", pin.Name)))).ToArray(), pins, LocalRuntimeSelection.LimitsFor(Current), Current).Package);
        Assert.Equal(NativeCompilerPdfOriginPins.RuntimeIdentity, package.Identity.Hex);
        Assert.Equal(NativeCompilerComponents.For(Current, NativeCompilerEngine.PdfLaTeX).WasmHash, package.Engine.Hash.Hex);
    }

    private sealed class PinnedArchiveFact : FactAttribute
    {
        public PinnedArchiveFact()
        {
            if (!File.Exists(Path.Combine(Environment.GetEnvironmentVariable("PDFTEX_ORIGIN20_RUNTIME_DIR") ?? "", "pins.json")))
                Skip = "The exact P20 inventory is not provided.";
        }
    }
}
