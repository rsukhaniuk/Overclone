using System.Net;
using System.Text;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Client.Compilation.SyncTeX;
using Client.RuntimeAssets;
using Xunit;

namespace Workspace.Tests.CompilationNative;

public sealed class NativeDviPageLabels21Tests
{
    private const NativeRuntimeProfile Current = NativeRuntimeProfile.LocalCompilerPageLabels;
    private const NativeRuntimeProfile Parent = NativeRuntimeProfile.LocalCompilerPdfOrigins;

    [Fact]
    public void Page_label_upgrade_keeps_the_other_engine_descriptors_and_direct_geometry()
    {
        Assert.Equal(Current, BundledRuntimeAssets.CompilerProfile);
        Assert.True(NativeNavigationContract.IsDirectPdfMapped(Current, 23, NativeCompilerEngine.PdfLaTeX));
        Assert.True(NativeNavigationContract.IsMapped(Current, 24, NativeCompilerEngine.LaTeXDvi));
        Assert.True(NativeSelectedPdfContract.MatchesRuntime(Current, NativeCompilerPageLabelPins.RuntimeIdentity));
        Assert.True(NativeSelectedPdfContract.MatchesRuntime(Current, NativeCompilerTermesPins.RuntimeIdentity));
        Assert.True(NativeSelectedPdfContract.MatchesRuntime(Current, NativeCompilerCompatibleFontPins.RuntimeIdentity));
        Assert.False(NativeSelectedPdfContract.MatchesRuntime(Current, NativeCompilerPdfOriginPins.RuntimeIdentity));
        foreach (var engine in Enum.GetValues<NativeCompilerEngine>())
        {
            var previous = NativeCompilerComponents.For(Parent, engine);
            var current = NativeCompilerComponents.For(Current, engine);
            if (engine == NativeCompilerEngine.LaTeXDvi)
            {
                Assert.Equal(NativeCompilerPageLabelPins.DviConverterWasmHash, current.OutputAdapter.Hash?.Hex);
                Assert.NotEqual(previous.OutputAdapter, current.OutputAdapter);
                Assert.Equal(previous, current with { OutputAdapter = previous.OutputAdapter });
            }
            else Assert.Equal(previous, current);
        }
    }

    [PinnedArchiveFact]
    public async Task Exact_bundled_archive_binds_the_converter_and_named_font_catalog()
    {
        using var handler = new ArchiveHandler(Environment.GetEnvironmentVariable("BUNDLED_TERMES_ARCHIVE")!);
        await using var resources = new BundledRuntimeAssets(new HttpClient(handler) { BaseAddress = new("http://localhost/") });
        var installed = await resources.ReadCompilerAsync();
        Assert.Equal(RuntimeAssetStatus.Ready, installed.Store.Status);
        var package = Assert.IsType<NativeRuntimePackage>(installed.Package);
        Assert.True(NativeSelectedPdfContract.MatchesRuntime(Current, package.Identity.Hex));
        Assert.NotEqual(NativeCompilerCompatibleFontPins.RuntimeIdentity, package.Identity.Hex);
        Assert.Equal(package.Bundle, SyncTeXRuntimePackage.FromCompiler(package).CompilerBinding?.Bundle);
        Assert.Equal(NativeCompilerPageLabelPins.DviConverterWasmHash, package.AdapterFor(NativeCompilerEngine.LaTeXDvi).Hash?.Hex);
        var catalog = Encoding.UTF8.GetString(package.Copy("xetexfontlist.txt"));
        foreach (var face in new[] { "regular", "bold", "italic", "bolditalic" })
        {
            var filename = $"texgyretermes-{face}.otf";
            Assert.Contains($"/tex/{filename}\n0\n1\nTeX Gyre Termes\n", catalog, StringComparison.Ordinal);
            Assert.NotEmpty(package.Copy(filename));
        }
        foreach (var (family, alias) in new[] { ("Serif", "Times New Roman"), ("Sans", "Arial") })
        foreach (var face in new[] { "Regular", "Bold", "Italic", "BoldItalic" })
        {
            var filename = $"Liberation{family}-{face}.ttf";
            Assert.Contains($"/tex/{filename}\n0\n2\nLiberation {family}\n{alias}\n", catalog, StringComparison.Ordinal);
            Assert.NotEmpty(package.Copy(filename));
        }
        foreach (var (name, font) in new[] { ("TimesNewRoman", "LiberationSerif-Regular.ttf"),
            ("Arial", "LiberationSans-Regular.ttf"), ("TeXGyreTermes", "texgyretermes-regular.otf") })
            Assert.Contains(font, Encoding.ASCII.GetString(package.Copy(name + ".fontspec")), StringComparison.Ordinal);
        var editableCopy = package.Copy("xstring.sty");
        var firstByte = editableCopy[0];
        editableCopy[0] ^= 1;
        Assert.Equal(firstByte, package.Copy("xstring.sty")[0]);
        Assert.Equal("/bundled-runtime/compiler-suite.zip", handler.Request);
    }

    [PinnedArchiveFact]
    public async Task Exact_size_corrupt_bundled_archive_is_rejected()
    {
        using var handler = new ArchiveHandler(Environment.GetEnvironmentVariable("BUNDLED_TERMES_ARCHIVE")!, corrupt: true);
        await using var resources = new BundledRuntimeAssets(new HttpClient(handler) { BaseAddress = new("http://localhost/") });
        var installed = await resources.ReadCompilerAsync();
        Assert.Equal(RuntimeAssetStatus.Corrupt, installed.Store.Status);
        Assert.Null(installed.Package);
    }

    private sealed class ArchiveHandler(string archive, bool corrupt = false) : HttpMessageHandler
    {
        public string? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request.RequestUri!.AbsolutePath;
            if (!corrupt) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(archive)) });
            var bytes = File.ReadAllBytes(archive);
            bytes[0] ^= 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    private sealed class PinnedArchiveFact : FactAttribute
    {
        public PinnedArchiveFact()
        {
            if (!File.Exists(Environment.GetEnvironmentVariable("BUNDLED_TERMES_ARCHIVE")))
                Skip = "The exact bundled Termes archive is not provided.";
        }
    }
}
