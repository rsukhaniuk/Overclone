using System.Text;

namespace Client.Pdf.Preview;

internal sealed record PdfReaderGlyph(string Text, PdfPreviewRect? Box);
internal sealed record PdfReaderLink(PdfPreviewRect Box, int? PageIndex, string? Address);
internal sealed record PdfReaderHighlight(PdfPreviewRect Box, bool IsSource, string? Title = null);

internal sealed record PdfReaderPage(PdfReaderGlyph[] Glyphs, PdfReaderLink[] Links, int Rotation)
{
    internal static PdfReaderPage From(PdfPageInspection page, PdfActionInspection? actions)
    {
        var glyphs = new List<PdfReaderGlyph>();
        foreach (var glyph in page.Glyphs.Take(12_000))
        {
            if (!Rune.TryCreate(glyph.UnicodeValue, out var rune)) continue;
            PdfPreviewRect? box = glyph.DisplayBox is { } display && glyph.BoxStatus == "available"
                ? Normalize(display.Left, display.Top, display.Right, display.Bottom, page.NativeDisplaySize.X, page.NativeDisplaySize.Y)
                : null;
            // Only native text with located glyphs and native separators is selectable.
            if (box is not null || Rune.IsWhiteSpace(rune)) glyphs.Add(new(rune.ToString(), box));
        }

        var links = new List<PdfReaderLink>();
        if (actions?.Availability == PdfActionAvailability.Available)
        {
            foreach (var action in actions.Links)
            {
                if (action.RawRectangle is not { } raw || LinkBox(raw, page) is not { } box) continue;
                if (action.NativeType is null or 1 && action.Destination is { Status: "available", PageIndex: { } index })
                    links.Add(new(box, index, null));
                else if (action.NativeType == 3 && action.Uri is { Status: "available", Value: { } address }
                    && Uri.TryCreate(address, UriKind.Absolute, out var target) && target.Scheme is "http" or "https"
                    && target.Host.Length != 0 && target.UserInfo.Length == 0)
                    links.Add(new(box, null, target.AbsoluteUri));
            }
        }
        return new(glyphs.ToArray(), links.ToArray(), page.Rotation);
    }

    private static PdfPreviewRect? LinkBox(PdfBox raw, PdfPageInspection page)
    {
        var crop = page.EffectiveBox;
        double l = raw.Left - crop.Left, r = raw.Right - crop.Left;
        double t = crop.Top - raw.Top, b = crop.Top - raw.Bottom;
        double w = crop.Right - crop.Left, h = crop.Top - crop.Bottom;
        return page.Rotation switch
        {
            0 => Normalize(l, t, r, b, w, h),
            90 => Normalize(h - b, l, h - t, r, h, w),
            180 => Normalize(w - r, h - b, w - l, h - t, w, h),
            270 => Normalize(t, w - r, b, w - l, h, w),
            _ => null
        };
    }

    private static PdfPreviewRect? Normalize(double left, double top, double right, double bottom, double width, double height)
    {
        if (width <= 0 || height <= 0 || !double.IsFinite(left + top + right + bottom + width + height)) return null;
        double l = Math.Clamp(left / width, 0, 1), t = Math.Clamp(top / height, 0, 1);
        double r = Math.Clamp(right / width, 0, 1), b = Math.Clamp(bottom / height, 0, 1);
        return r > l && b > t ? new(l, t, r - l, b - t) : null;
    }
}
