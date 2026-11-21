using System.Net;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
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
    public async Task Exact_archive_binds_the_current_converter_descriptor()
    {
        using var handler = new ArchiveHandler(Environment.GetEnvironmentVariable("DVI_PAGE_LABEL21_ARCHIVE")!);
        await using var resources = new BundledRuntimeAssets(new HttpClient(handler) { BaseAddress = new("http://localhost/") });
        var installed = await resources.ReadCompilerAsync();
        Assert.Equal(RuntimeAssetStatus.Ready, installed.Store.Status);
        var package = Assert.IsType<NativeRuntimePackage>(installed.Package);
        Assert.Equal(NativeCompilerPageLabelPins.RuntimeIdentity, package.Identity.Hex);
        Assert.Equal(NativeCompilerPageLabelPins.DviConverterWasmHash, package.AdapterFor(NativeCompilerEngine.LaTeXDvi).Hash?.Hex);
        Assert.Equal("/bundled-runtime/compiler-suite.zip", handler.Request);
    }

    private sealed class ArchiveHandler(string archive) : HttpMessageHandler
    {
        public string? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(archive)) });
        }
    }

    private sealed class PinnedArchiveFact : FactAttribute
    {
        public PinnedArchiveFact()
        {
            if (!File.Exists(Environment.GetEnvironmentVariable("DVI_PAGE_LABEL21_ARCHIVE")))
                Skip = "The exact bundled P21 archive is not provided.";
        }
    }
}
