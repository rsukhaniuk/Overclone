using Client.Pdf;
using Client.Pdf.Preview;

namespace Client.Compilation.SyncTeX;

/// <summary>Convert native writer coordinates to the renderer's measured crop /
/// rotation view. No physical unit or general PDF compliance measurement.</summary>
public static class SyncTeXDisplayGeometry
{
    private static bool Valid(PdfPreviewGeometry g)
    {
        var m = g.Media; var e = g.Effective;
        // The supported writer uses an origin at (0,0). A changed media origin
        // needs independent writer geometry; do not guess from a display box.
        if (m.Left != 0 || m.Bottom != 0 || g.Rotation is < 0 or > 3
            || !double.IsFinite(m.Top + m.Right + e.Left + e.Bottom + e.Right + e.Top + g.Display.X + g.Display.Y)
            || m.Right <= 0 || m.Top <= 0 || e.Left < 0 || e.Bottom < 0 || e.Right > m.Right || e.Top > m.Top
            || e.Right <= e.Left || e.Top <= e.Bottom) return false;
        double w = e.Right - e.Left, h = e.Top - e.Bottom;
        return Math.Abs(g.Display.X - (g.Rotation % 2 == 0 ? w : h)) <= .001
            && Math.Abs(g.Display.Y - (g.Rotation % 2 == 0 ? h : w)) <= .001;
    }

    public static SyncTeXPoint? Inverse(PdfPreviewGeometry geometry, double x, double y)
    {
        if (!Valid(geometry) || !double.IsFinite(x + y) || x is < 0 or > 1 || y is < 0 or > 1) return null;
        var e = geometry.Effective;
        double w = e.Right - e.Left, h = e.Top - e.Bottom;
        x *= geometry.Display.X; y *= geometry.Display.Y;
        var raw = geometry.Rotation switch
        {
            0 => new PdfPoint(x + e.Left, h - y + e.Bottom),
            1 => new PdfPoint(y + e.Left, x + e.Bottom),
            2 => new PdfPoint(w - x + e.Left, y + e.Bottom),
            _ => new PdfPoint(w - y + e.Left, h - x + e.Bottom)
        };
        return new(raw.X, geometry.Media.Top - raw.Y);
    }

    public static PdfPreviewRect? Highlight(PdfPreviewGeometry geometry, SyncTeXBox box)
    {
        if (!Valid(geometry) || !double.IsFinite(box.H + box.V + box.Width + box.Height + box.Depth)
            || box.Width < 0 || box.Height < 0 || box.Depth < 0) return null;
        var corners = new[] { Forward(geometry, box.H, box.V - box.Height),
            Forward(geometry, box.H + box.Width, box.V - box.Height),
            Forward(geometry, box.H, box.V + box.Depth), Forward(geometry, box.H + box.Width, box.V + box.Depth) };
        double l = Math.Clamp(corners.Min(p => p.X), 0, geometry.Display.X),
            r = Math.Clamp(corners.Max(p => p.X), 0, geometry.Display.X),
            t = Math.Clamp(corners.Min(p => p.Y), 0, geometry.Display.Y),
            b = Math.Clamp(corners.Max(p => p.Y), 0, geometry.Display.Y);
        if (r <= l || b <= t) return null;
        return new(l / geometry.Display.X, t / geometry.Display.Y,
            (r - l) / geometry.Display.X, (b - t) / geometry.Display.Y);
    }

    private static PdfPoint Forward(PdfPreviewGeometry g, double h, double v)
    {
        var e = g.Effective;
        double x = h - e.Left, y = g.Media.Top - v - e.Bottom,
            w = e.Right - e.Left, height = e.Top - e.Bottom;
        return g.Rotation switch
        { 0 => new(x, height - y), 1 => new(y, x), 2 => new(w - x, y), _ => new(height - y, w - x) };
    }
}
