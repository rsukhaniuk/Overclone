using System.Collections.ObjectModel;

namespace Client.Pdf.Preview;

public enum PdfPreviewStatus { Empty, Loading, Available, Unavailable, Busy, Cancelled, TimedOut, Error, Disposed }
public enum PdfPreviewSearchStatus { Empty, Scanning, Complete, Partial, Error, Cancelled }
public enum PdfDisplayProvenance { ProvidedPdf, CallerSaysHistorical, CallerSaysCurrent }
public sealed record PdfPreviewPolicy(int InputBytes = 8388608, int MaxPixels = 1000000, int MaxRgbaBytes = 4000000,
    int MaxSearchPages = 32, int MaxHits = 128, int MaxHighlightBoxes = 1024, int DeadlineMs = 25000)
{
    public bool IsValid => InputBytes is > 0 and <= 8388608 && MaxPixels is > 0 and <= 4000000
        && MaxRgbaBytes is > 0 and <= 16000000 && MaxSearchPages is > 0 and <= 32 && MaxHits is > 0 and <= 256
        && MaxHighlightBoxes is > 0 and <= 4096 && DeadlineMs is >= 1 and <= 30000;
}
public readonly record struct PdfPreviewRect(double Left, double Top, double Width, double Height);
public sealed record PdfPreviewHit(int PageIndex, int StartCharacterIndex, int CharacterCount,
    string GlyphText, string GeometryStatus, ReadOnlyCollection<PdfPreviewRect> NormalizedBoxes);
public sealed record PdfPreviewSearch(PdfPreviewSearchStatus Status, string Query, int ScannedPages, int TotalPages,
    ReadOnlyCollection<PdfPreviewHit> Hits, string Detail)
{
    public static PdfPreviewSearch Empty => new(PdfPreviewSearchStatus.Empty, "", 0, 0, Array.AsReadOnly(Array.Empty<PdfPreviewHit>()), "No search requested.");
}
public sealed class PdfPreviewFrame
{
    private readonly byte[] _rgba;
    internal PdfPreviewFrame(long generation, string hash, int page, int width, int height, byte[] ownedRgba)
    { Generation = generation; DocumentSha256 = hash; PageIndex = page; Width = width; Height = height; _rgba = ownedRgba; }
    public long Generation { get; }
    public string DocumentSha256 { get; }
    public int PageIndex { get; }
    public int Width { get; }
    public int Height { get; }
    public byte[] CopyRgba() => (byte[])_rgba.Clone();
}
public sealed record PdfPreviewState(long Generation, PdfPreviewStatus Status, string Detail, string? DocumentSha256,
    string DisplayName, PdfDisplayProvenance Provenance, int PageIndex, int PageCount, int ZoomPercent, int FitWidth,
    string Text, ReadOnlyCollection<int> Utf16Units, ReadOnlyCollection<PdfGlyph> Glyphs,
    PdfPreviewSearch Search, int SelectedHit, PdfPreviewFrame? Frame, string? NativeRequestId, long? NativeEpoch,
    PdfRuntimeIdentity? Runtime)
{
    public static PdfPreviewState Empty => new(0, PdfPreviewStatus.Empty, "Provide an owned PDF and explicitly selected inspector.", null, "", PdfDisplayProvenance.ProvidedPdf,
        0, 0, 100, 360, "", Array.AsReadOnly(Array.Empty<int>()), Array.AsReadOnly(Array.Empty<PdfGlyph>()), PdfPreviewSearch.Empty, -1, null, null, null, null);
    public string SourceNavigationStatus => "Unavailable: no source mapping supplied.";
    public string PhysicalSizeStatus => "Indeterminate: native/raw units; UserUnit not independently established.";
    public string Compatibility => "Native inspection: at most32 pages,100000 text units/page; configured search/raster limits may be narrower. Caller provenance is display metadata, not compilation acceptance.";
}

public static class PdfPreviewPixels
{
    public static byte[] ToRgba(byte[] bgra, int width, int height, PdfPreviewPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (!policy.IsValid || width <= 0 || height <= 0 || (long)width * height > policy.MaxPixels
            || (long)width * height * 4 > policy.MaxRgbaBytes || (long)width * height * 4 != bgra.Length)
            throw new ArgumentException("Raster budget/shape rejected before allocation.");
        var rgba = new byte[bgra.Length];
        for (int i = 0; i < bgra.Length; i += 4)
        { rgba[i] = bgra[i + 2]; rgba[i + 1] = bgra[i + 1]; rgba[i + 2] = bgra[i]; rgba[i + 3] = bgra[i + 3]; }
        return rgba;
    }
    public static (int Width, int Height)? Dimensions(PdfPoint rawDisplay, int fitWidth, int zoom, PdfPreviewPolicy policy)
    {
        if (!policy.IsValid || fitWidth is < 64 or > 1600 || zoom is < 25 or > 300 || !double.IsFinite(rawDisplay.X)
            || !double.IsFinite(rawDisplay.Y) || rawDisplay.X <= 0 || rawDisplay.Y <= 0) return null;
        double w = Math.Round(fitWidth * zoom / 100.0), h = Math.Round(w * rawDisplay.Y / rawDisplay.X);
        if (w < 1 || h < 1 || w > 16384 || h > 16384 || w * h > policy.MaxPixels || w * h * 4 > policy.MaxRgbaBytes) return null;
        return ((int)w, (int)h);
    }
    // Round independent edges once in C# raster space. CSS percentage left/width
    // quantization can accumulate beyond the native integer-device rounding bound.
    public static PdfPreviewRect RasterBox(PdfPreviewRect box, int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 4000000 || !double.IsFinite(box.Left + box.Top + box.Width + box.Height)
            || box.Left < 0 || box.Top < 0 || box.Width < 0 || box.Height < 0 || box.Left + box.Width > 1.0000000001 || box.Top + box.Height > 1.0000000001)
            throw new ArgumentException("Normalized bounded display box required.");
        double l = Math.Round(Math.Clamp(box.Left, 0, 1) * width, MidpointRounding.AwayFromZero);
        double r = Math.Round(Math.Clamp(box.Left + box.Width, 0, 1) * width, MidpointRounding.AwayFromZero);
        double t = Math.Round(Math.Clamp(box.Top, 0, 1) * height, MidpointRounding.AwayFromZero);
        double b = Math.Round(Math.Clamp(box.Top + box.Height, 0, 1) * height, MidpointRounding.AwayFromZero);
        return new(l, t, r - l, b - t);
    }
    // Hit offsets select native glyph indices, NEVER UTF16 string offsets. A
    // supplementary scalar may occupy2 UTF16 units but only1 native glyph.
    public static PdfPreviewHit Hit(PdfPageInspection page, PdfSearchHit hit, int maxBoxes)
    {
        if (hit.StartCharacterIndex < 0 || hit.CharacterCount <= 0 || (long)hit.StartCharacterIndex + hit.CharacterCount > page.Glyphs.Count)
            throw new ArgumentException("Native hit range rejected.");
        var text = new System.Text.StringBuilder(); var boxes = new List<PdfPreviewRect>(); bool incomplete = false;
        foreach (var glyph in page.Glyphs.Skip(hit.StartCharacterIndex).Take(hit.CharacterCount))
        {
            text.Append(System.Text.Rune.TryCreate(glyph.UnicodeValue, out var rune) ? rune.ToString() : "\uFFFD");
            if (glyph.BoxStatus != "available" || glyph.DisplayBox is not { } b || page.NativeDisplaySize.X <= 0 || page.NativeDisplaySize.Y <= 0 || boxes.Count >= maxBoxes)
            { incomplete = true; continue; }
            // Backend displayBox is already crop/rotation-normalized top-left.
            double l = Math.Clamp(b.Left / page.NativeDisplaySize.X, 0, 1), r = Math.Clamp(b.Right / page.NativeDisplaySize.X, 0, 1);
            double t = Math.Clamp(b.Top / page.NativeDisplaySize.Y, 0, 1), bottom = Math.Clamp(b.Bottom / page.NativeDisplaySize.Y, 0, 1);
            if (!double.IsFinite(l + r + t + bottom) || r <= l || bottom <= t) { incomplete = true; continue; }
            boxes.Add(new(l, t, r - l, bottom - t));
        }
        return new(page.PageIndex, hit.StartCharacterIndex, hit.CharacterCount, text.ToString(), incomplete ? "indeterminate/partial native glyph boxes" : "available native display glyph boxes", boxes.AsReadOnly());
    }
}
