using System.Globalization;
using System.Text;

namespace Client.Compilation.Download;

public static class PdfDownloadFilename
{
    /// <summary>Portable basename only, not a path. At most 80 UTF-16 stem units;
    /// preserve whole Unicode scalars, reject controls/punctuation and DOS devices.</summary>
    public static string Create(string? projectName)
    {
        var name = projectName ?? "";
        if (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        var stem = new StringBuilder();
        foreach (var rune in name.EnumerateRunes())
        {
            if (stem.Length + rune.Utf16SequenceLength > 80) break;
            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsLetter(rune) || Rune.IsNumber(rune) || category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                || rune.Value is ' ' or '-' or '_') stem.Append(rune.ToString());
            else if (stem.Length > 0 && stem[^1] != '_') stem.Append('_');
        }
        var value = stem.ToString().Trim(' ', '-', '_');
        var upper = value.ToUpperInvariant();
        if (value.Length == 0 || upper is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$"
            || upper.Length == 4 && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal))
                && (upper[3] is >= '0' and <= '9' or '¹' or '²' or '³')) value = "document";
        return value + ".pdf";
    }
}
