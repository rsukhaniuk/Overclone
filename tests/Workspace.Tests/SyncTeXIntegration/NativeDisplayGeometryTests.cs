using System.Text;
using Client.Compilation.SyncTeX;
using Client.Pdf;
using Client.Pdf.Preview;
using Xunit;

namespace Workspace.Tests.SyncTeXIntegration;

// Hand-calculated affine/crop vectors test display composition only. They do not
// establish native execution, PDF physical measurements or live query authority.
public sealed class NativeDisplayGeometryTests
{
    [Theory]
    [InlineData(0, .4375, .5, .4125, .48125, .05, .0375)]
    [InlineData(1, .5, .4375, .48125, .4125, .0375, .05)]
    [InlineData(2, .5625, .5, .5375, .48125, .05, .0375)]
    [InlineData(3, .5, .5625, .48125, .5375, .0375, .05)]
    public void Scale_translation_and_baseline_extents_compose_with_measured_crop_and_all_rotations(
        int rotation, double x, double y, double left, double top, double width, double height)
    {
        // (h,v)=(20,30) -> raw PDF (45,100), with no extra TeX offset.
        var page = Page("[2,0,0,-3,5,190]");
        var point = Assert.IsType<SyncTeXPoint>(SyncTeXDisplayGeometry.Inverse(Geometry(rotation), page, x, y));
        Assert.Equal(20, point.H, 8); Assert.Equal(30, point.V, 8);
        // Baseline box -> raw x=43..47, y=97..103.
        var rect = SyncTeXDisplayGeometry.Highlight(Geometry(rotation), page, new(19, 30, 2, 1, 1))!.Value;
        Assert.Equal(left, rect.Left, 8); Assert.Equal(top, rect.Top, 8);
        Assert.Equal(width, rect.Width, 8); Assert.Equal(height, rect.Height, 8);
    }

    [Fact]
    public void Reflected_sheared_box_uses_all_four_corners_before_clipping()
    {
        // [h,v]=[20..24,29..32] -> x=[9..16], y=[83..93].
        // Both extrema use opposite corners: using only one diagonal loses them.
        var page = Page("[-1,1,1,-2,4,127]");
        var rect = SyncTeXDisplayGeometry.Highlight(Geometry(0), page, new(20, 30, 4, 1, 2))!.Value;
        Assert.Equal(0, rect.Left, 8); Assert.Equal(87.0 / 160, rect.Top, 8);
        Assert.Equal(6.0 / 80, rect.Width, 8); Assert.Equal(10.0 / 160, rect.Height, 8);
    }

    [Fact]
    public void Writer_domain_and_effective_crop_limit_actions_without_inventing_a_position()
    {
        var page = Page("[2,0,0,-3,5,190]");
        var rect = SyncTeXDisplayGeometry.Highlight(Geometry(0), page, new(1, 30, 4, 1, 1))!.Value;
        Assert.Equal(0, rect.Left); Assert.Equal(.0625, rect.Width, 8);
        Assert.Null(SyncTeXDisplayGeometry.Highlight(Geometry(0), page, new(-8, 30, 1, 1, 1)));
        var narrow = Page("[2,0,0,-3,5,190]", "[0,0,1,1]");
        Assert.Null(SyncTeXDisplayGeometry.Inverse(Geometry(0), narrow, .4375, .5));
        Assert.Null(SyncTeXDisplayGeometry.Highlight(Geometry(0), narrow, new(19, 30, 2, 1, 1)));
    }

    [Fact]
    public void Unavailable_geometry_and_bad_measurements_remain_unavailable()
    {
        var unavailable = SourceMapGeometryCodec.ParseNativeSidecar(Encoding.UTF8.GetBytes(
            """{"schemaVersion":1,"pages":[{"page":1,"status":"unsupported","reason":"writer-origin-unavailable","writerToPdfRaw":null,"writerPageBoxBp":null}]}"""))
            .Sidecar!.Pages[0];
        var page = Page("[2,0,0,-3,5,190]"); var g = Geometry(0);
        foreach (var p in new SourceMapPageTransform?[] { null, unavailable })
        {
            Assert.Null(SyncTeXDisplayGeometry.Inverse(g, p, .5, .5));
            Assert.Null(SyncTeXDisplayGeometry.Highlight(g, p, new(19, 30, 2, 1, 1)));
        }
        foreach (var bad in new[] { g with { Media = new(11, 0, 100, 200) }, g with { Display = new(81, 160) }, g with { Rotation = 4 } })
        {
            Assert.Null(SyncTeXDisplayGeometry.Inverse(bad, page, .5, .5));
            Assert.Null(SyncTeXDisplayGeometry.Highlight(bad, page, new(19, 30, 2, 1, 1)));
        }
        Assert.Null(SyncTeXDisplayGeometry.Inverse(g, page, double.NaN, .5));
        Assert.Null(SyncTeXDisplayGeometry.Inverse(g, page, .5, 1.01));
        Assert.Null(SyncTeXDisplayGeometry.Highlight(g, page, new(19, 30, -2, 1, 1)));
    }

    private static SourceMapPageTransform Page(string matrix, string domain = "[-10,-10,100,100]") =>
        SourceMapGeometryCodec.ParseNativeSidecar(Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"pages\":[{\"page\":1,\"status\":\"available\",\"reason\":\"none\",\"writerToPdfRaw\":"
            + matrix + ",\"writerPageBoxBp\":" + domain + "}]}" )).Sidecar!.Pages[0];

    private static PdfPreviewGeometry Geometry(int rotation) => new(new(0, 0, 100, 200), new(10, 20, 90, 180), rotation,
        rotation % 2 == 0 ? new PdfPoint(80, 160) : new PdfPoint(160, 80));
}
