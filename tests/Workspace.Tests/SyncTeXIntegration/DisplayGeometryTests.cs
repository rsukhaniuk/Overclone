using Client.Compilation.SyncTeX;
using Client.Pdf;
using Client.Pdf.Preview;
using Xunit;

namespace Workspace.Tests.SyncTeXIntegration;

public sealed class DisplayGeometryTests
{
    [Theory]
    [InlineData(0, .3, 1.0 / 3)]
    [InlineData(1, 2.0 / 3, .3)]
    [InlineData(2, .7, 2.0 / 3)]
    [InlineData(3, 1.0 / 3, .7)]
    public void Measured_crop_and_all_rotations_map_the_same_native_position(int rotation, double x, double y)
    {
        var geometry = Geometry(rotation);
        var point = Assert.IsType<SyncTeXPoint>(SyncTeXDisplayGeometry.Inverse(geometry, x, y));
        Assert.Equal(200, point.H, 8); Assert.Equal(300, point.V, 8);
        var box = Assert.IsType<PdfPreviewRect>(SyncTeXDisplayGeometry.Highlight(geometry, new(190, 300, 20, 10, 5)));
        Assert.InRange(x, box.Left, box.Left + box.Width);
        Assert.InRange(y, box.Top, box.Top + box.Height);
    }

    [Fact]
    public void Baseline_height_depth_and_crop_intersection_are_preserved()
    {
        var box = SyncTeXDisplayGeometry.Highlight(Geometry(0), new(40, 110, 30, 20, 0))!.Value;
        Assert.Equal(0, box.Left); Assert.Equal(0, box.Top);
        Assert.Equal(20.0 / 500, box.Width, 8); Assert.Equal(10.0 / 600, box.Height, 8);
        Assert.Null(SyncTeXDisplayGeometry.Highlight(Geometry(0), new(0, 0, 1, 1, 0)));
    }

    [Fact]
    public void Ambiguous_media_origin_inconsistent_display_and_invalid_clicks_are_refused()
    {
        var g = Geometry(0);
        Assert.Null(SyncTeXDisplayGeometry.Inverse(g with { Media = new(1, 0, 600, 800) }, .5, .5));
        Assert.Null(SyncTeXDisplayGeometry.Inverse(g with { Display = new(501, 600) }, .5, .5));
        Assert.Null(SyncTeXDisplayGeometry.Inverse(g, double.NaN, .5));
        Assert.Null(SyncTeXDisplayGeometry.Inverse(g, -0.01, .5));
        Assert.Null(SyncTeXDisplayGeometry.Highlight(g, new(1, 1, -1, 1, 0)));
    }

    private static PdfPreviewGeometry Geometry(int rotation) => new(new(0, 0, 600, 800), new(50, 100, 550, 700), rotation,
        rotation % 2 == 0 ? new PdfPoint(500, 600) : new PdfPoint(600, 500));
}
