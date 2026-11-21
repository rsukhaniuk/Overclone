using System.Text;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Client.Compilation.SyncTeX;
using Client.Pdf;
using Client.Pdf.Preview;
using Xunit;

namespace Workspace.Tests.NativeOrigins;

// Consumer contracts and hand-calculated geometry, not native/browser acceptance.
public sealed class OriginProfileTests
{
    private const NativeRuntimeProfile P = NativeOriginCompilerContract.Profile;

    [Fact]
    public void Closed_origin_derivative_preserves_parent_and_capacity_contracts()
    {
        Assert.Equal(15, (int)P);
        Assert.True(NativeRuntimeProfiles.Known(P));
        Assert.True(NativeRuntimeProfiles.SupportsBiber(P));
        Assert.True(LocalRuntimeSelection.LimitsFor(P).IsValid);
        Assert.Equal("local-compiler-origins-v1", NativeRuntimeProfiles.IdentityName(P));
        Assert.Equal(128, NativeRuntimeProfiles.ProjectFileLimit(P));
        Assert.Equal(256, NativeRuntimeProfiles.OutputDeclarationLimit(P));
        Assert.Equal(6, NativeRuntimeProfiles.ProtocolVersion(P));
        Assert.Equal(new[] { 23, 24, 21, 22 }, Enum.GetValues<NativeCompilerEngine>().Select(e => NativeCompilerSuiteContract.Phase(P, e)));
        foreach (var engine in Enum.GetValues<NativeCompilerEngine>())
        {
            var old = NativeCompilerComponents.For(NativeThesisCompilerContract.Profile, engine);
            var next = NativeCompilerComponents.For(P, engine);
            Assert.Equal(old.FormatHash, next.FormatHash);
            Assert.Equal(old.NativeAbiVersion, next.NativeAbiVersion);
            Assert.Equal(NativeCompilerOriginPins.QueryLoaderHash, next.QueryLoaderHash);
            Assert.Equal(NativeCompilerOriginPins.QueryWasmHash, next.QueryWasmHash);
            Assert.Equal(NativeRuntimeProfiles.SyncTeXQueryWasmHash, old.QueryWasmHash);
            Assert.True(NativeCompilerSuiteContract.CreatePlan(engine, profile: P).IsValid);
            Assert.Equal(8448, NativeCompilerSuiteContract.CreatePlan(engine, profile: P).MaxPhaseFiles);
            if (engine != NativeCompilerEngine.LuaLaTeX) Assert.Equal(old.WasmHash, next.WasmHash);
            if (engine == NativeCompilerEngine.PdfLaTeX) Assert.Equal(old.OutputAdapter, next.OutputAdapter);
            else
            {
                Assert.NotEqual(old.OutputAdapter, next.OutputAdapter);
                Assert.EndsWith("-origin1-neutral1", next.OutputAdapter.Version);
            }
        }
        foreach (var unknown in new[] { (NativeRuntimeProfile)11, (NativeRuntimeProfile)20, (NativeRuntimeProfile)int.MaxValue })
        {
            Assert.False(NativeRuntimeProfiles.Known(unknown));
            Assert.Equal(64, NativeRuntimeProfiles.ProjectFileLimit(unknown));
            Assert.Equal(128, NativeRuntimeProfiles.OutputDeclarationLimit(unknown));
        }
        if (!NativeCompilerOriginPins.Ready)
        {
            Assert.Equal(0, NativeRuntimeProfiles.InstalledCount(P));
            Assert.Equal("", NativeRuntimeProfiles.ClosureHash(P));
            Assert.False(NativeCompilerOriginPins.MatchesComponents(new Dictionary<string, NativeResourcePin>()));
            Assert.Equal(NativeRuntimeIssue.Unavailable, NativeRuntimePackage.Create([], [], LocalRuntimeSelection.LimitsFor(P), P).Issue);
        }
    }

    [Theory]
    [InlineData(0, .4375, .5, .4125, .48125, .05, .0375)]
    [InlineData(1, .5, .4375, .48125, .4125, .0375, .05)]
    [InlineData(2, .5625, .5, .5375, .48125, .05, .0375)]
    [InlineData(3, .5, .5625, .48125, .5375, .0375, .05)]
    public void Typed_native_matrix_handles_nonzero_media_crop_scale_and_rotation_once(
        int rotation, double x, double y, double left, double top, double width, double height)
    {
        // Shift media/crop and native raw matrix by (-40,+70). Writer (20,30)
        // remains the same: raw (5,170). No inferred TeX origin or magnification.
        var geometry = new PdfPreviewGeometry(new(-40, 70, 60, 270), new(-30, 90, 50, 250), rotation,
            rotation % 2 == 0 ? new PdfPoint(80, 160) : new PdfPoint(160, 80));
        var page = Page("[2,0,0,-3,-35,260]");
        var point = Assert.IsType<SyncTeXPoint>(SyncTeXDisplayGeometry.Inverse(geometry, page, x, y));
        Assert.Equal(20, point.H, 8); Assert.Equal(30, point.V, 8);
        var rectangle = SyncTeXDisplayGeometry.Highlight(geometry, page, new(19, 30, 2, 1, 1))!.Value;
        Assert.Equal(left, rectangle.Left, 8); Assert.Equal(top, rectangle.Top, 8);
        Assert.Equal(width, rectangle.Width, 8); Assert.Equal(height, rectangle.Height, 8);
        Assert.Null(SyncTeXDisplayGeometry.Inverse(geometry, x, y)); // Legacy lacks a native origin witness.
        Assert.Null(SyncTeXDisplayGeometry.Highlight(geometry, new(19, 30, 2, 1, 1)));
        Assert.Null(SyncTeXDisplayGeometry.Inverse(geometry with { Media = new(-29, 70, 60, 270) }, page, x, y));
        Assert.Null(SyncTeXDisplayGeometry.Inverse(geometry with { Media = new(double.NaN, 70, 60, 270) }, page, x, y));
    }

    [Theory]
    [InlineData("unsupported", "writer-origin-unavailable")]
    [InlineData("unsupported", "out-of-domain")]
    [InlineData("indeterminate", "converter-transform-unavailable")]
    public void Native_faults_never_fall_back_to_box_offsets(string status, string reason)
    {
        var sidecar = SourceMapGeometryCodec.ParseNativeSidecar(Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"pages\":[{\"page\":1,\"status\":\"" + status + "\",\"reason\":\"" + reason
            + "\",\"writerToPdfRaw\":null,\"writerPageBoxBp\":null}]}"));
        Assert.NotNull(sidecar.Sidecar);
        var geometry = new PdfPreviewGeometry(new(-40, 70, 60, 270), new(-30, 90, 50, 250), 0, new(80, 160));
        Assert.Null(SyncTeXDisplayGeometry.Inverse(geometry, sidecar.Sidecar.Pages[0], .5, .5));
        Assert.Null(SyncTeXDisplayGeometry.Highlight(geometry, sidecar.Sidecar.Pages[0], new(19, 30, 2, 1, 1)));
    }

    private static SourceMapPageTransform Page(string matrix) => SourceMapGeometryCodec.ParseNativeSidecar(Encoding.UTF8.GetBytes(
        "{\"schemaVersion\":1,\"pages\":[{\"page\":1,\"status\":\"available\",\"reason\":\"none\",\"writerToPdfRaw\":"
        + matrix + ",\"writerPageBoxBp\":[-10,-10,100,100]}]}" )).Sidecar!.Pages[0];
}
