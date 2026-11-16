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
        // The renderer reports actual unrotated PDF boxes. Only the typed
        // native matrix can relate a nonzero MediaBox to writer coordinates.
        if (g.Rotation is < 0 or > 3
            || new[] { m.Left, m.Bottom, m.Right, m.Top, e.Left, e.Bottom, e.Right, e.Top, g.Display.X, g.Display.Y }.Any(v => !double.IsFinite(v))
            || m.Right <= m.Left || m.Top <= m.Bottom || e.Left < m.Left || e.Bottom < m.Bottom
            || e.Right > m.Right || e.Top > m.Top || e.Right <= e.Left || e.Top <= e.Bottom
            || g.Display.X <= 0 || g.Display.Y <= 0) return false;
        double w = e.Right - e.Left, h = e.Top - e.Bottom;
        return Math.Abs(g.Display.X - (g.Rotation % 2 == 0 ? w : h)) <= .001
            && Math.Abs(g.Display.Y - (g.Rotation % 2 == 0 ? h : w)) <= .001;
    }

    private static bool Legacy(PdfPreviewGeometry geometry) => Valid(geometry)
        && geometry.Media.Left == 0 && geometry.Media.Bottom == 0;

    public static SyncTeXPoint? Inverse(PdfPreviewGeometry geometry, double x, double y)
    {
        if (!Legacy(geometry) || !double.IsFinite(x + y) || x is < 0 or > 1 || y is < 0 or > 1) return null;
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
        if (!Legacy(geometry) || !double.IsFinite(box.H + box.V + box.Width + box.Height + box.Depth)
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

    /// <summary>Convert a measured view click through the same native writer
    /// transform as its map. The caller must establish live artifact ownership.</summary>
    public static SyncTeXPoint? Inverse(PdfPreviewGeometry geometry, SourceMapPageTransform? page, double x, double y)
    {
        if (!Valid(geometry) || !Transform(page, out var matrix, out var domain)
            || !matrix.TryInverse(out var inverse) || !double.IsFinite(x + y)
            || x is < 0 or > 1 || y is < 0 or > 1) return null;
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
        var writer = Apply(inverse, raw.X, raw.Y);
        const double rounding = 1e-7;
        if (!double.IsFinite(writer.X + writer.Y)
            || writer.X < domain.Left - rounding || writer.X > domain.Right + rounding
            || writer.Y < domain.Bottom - rounding || writer.Y > domain.Top + rounding) return null;
        return new(Math.Clamp(writer.X, domain.Left, domain.Right), Math.Clamp(writer.Y, domain.Bottom, domain.Top));
    }

    /// <summary>Transform all baseline-box corners before crop and display
    /// rotation. Writer offsets and scale are applied exactly once.</summary>
    public static PdfPreviewRect? Highlight(PdfPreviewGeometry geometry, SourceMapPageTransform? page, SyncTeXBox box)
    {
        if (!Valid(geometry) || !Transform(page, out var matrix, out var domain)
            || !double.IsFinite(box.H + box.V + box.Width + box.Height + box.Depth)
            || box.Width < 0 || box.Height < 0 || box.Depth < 0) return null;
        double l = Math.Max(box.H, domain.Left), r = Math.Min(box.H + box.Width, domain.Right),
            t = Math.Max(box.V - box.Height, domain.Bottom), b = Math.Min(box.V + box.Depth, domain.Top);
        if (r <= l || b <= t) return null;
        var raw = new[] { Apply(matrix, l, t), Apply(matrix, r, t), Apply(matrix, l, b), Apply(matrix, r, b) };
        if (raw.Any(p => !double.IsFinite(p.X + p.Y))) return null;
        var e = geometry.Effective;
        l = Math.Max(raw.Min(p => p.X), e.Left); r = Math.Min(raw.Max(p => p.X), e.Right);
        b = Math.Max(raw.Min(p => p.Y), e.Bottom); t = Math.Min(raw.Max(p => p.Y), e.Top);
        if (r <= l || t <= b) return null;
        var display = new[] { Display(geometry, l, b), Display(geometry, r, b),
            Display(geometry, l, t), Display(geometry, r, t) };
        l = display.Min(p => p.X); r = display.Max(p => p.X);
        t = display.Min(p => p.Y); b = display.Max(p => p.Y);
        return new(l / geometry.Display.X, t / geometry.Display.Y,
            (r - l) / geometry.Display.X, (b - t) / geometry.Display.Y);
    }

    private static bool Transform(SourceMapPageTransform? page, out SourceMapMatrix matrix, out SourceMapWriterBox domain)
    {
        matrix = default; domain = default;
        if (page is not { Status: "available", Reason: "none", WriterToPdfRaw: { } m, WriterPageBoxBp: { } d }
            || !m.TryInverse(out _)) return false;
        matrix = m; domain = d; return true;
    }

    private static PdfPoint Apply(SourceMapMatrix m, double h, double v) => new(m.A * h + m.C * v + m.E, m.B * h + m.D * v + m.F);

    private static PdfPoint Display(PdfPreviewGeometry g, double rawX, double rawY)
    {
        var e = g.Effective;
        double x = rawX - e.Left, y = rawY - e.Bottom, w = e.Right - e.Left, h = e.Top - e.Bottom;
        return g.Rotation switch
        { 0 => new(x, h - y), 1 => new(y, x), 2 => new(w - x, y), _ => new(h - y, w - x) };
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
