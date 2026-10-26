using System.Text;
using Client.Workspace.Patching;

namespace Client.Editing;

/// <summary>Half-open UTF-16 offsets in exact source text (not normalized DOM text).</summary>
public readonly record struct SourceRange(int Start, int End);
public sealed record SourceEdit(string Text, SourceRange Selection);
public sealed record SourceMatches(int Count, int Ordinal, SourceRange? Selection);
public enum LineTool { Indent, Unindent, ToggleComment }

/// <summary>Literal, bounded, pure source tools. No storage, DOM or editing policy.</summary>
public static class SourceTools
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static bool ValidText(string? text, out int bytes)
    {
        bytes = 0;
        if (text is null || text.Length > WorkspaceTextLimits.FileBytes) return false;
        try { bytes = Utf8.GetByteCount(text); }
        catch (EncoderFallbackException) { return false; }
        return bytes <= WorkspaceTextLimits.FileBytes;
    }

    public static bool ValidRange(string text, SourceRange range) =>
        range.Start >= 0 && range.End >= range.Start && range.End <= text.Length &&
        Boundary(text, range.Start) && Boundary(text, range.End);

    private static bool Boundary(string text, int offset) => offset == 0 || offset == text.Length ||
        !(char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset])) &&
        !(text[offset - 1] == '\r' && text[offset] == '\n');

    public static string TextareaText(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>Translate a browser textarea range back to exact source offsets.</summary>
    public static bool TryFromTextarea(string text, SourceRange dom, out SourceRange source)
    {
        source = default;
        if (!ValidText(text, out _) || dom.Start < 0 || dom.End < dom.Start) return false;
        int position = 0, start = -1, end = -1;
        for (int i = 0; i <= text.Length; i++, position++)
        {
            if (position == dom.Start) start = i;
            if (position == dom.End) { end = i; break; }
            if (i < text.Length && text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
        }
        source = new(start, end);
        return ValidRange(text, source);
    }

    public static SourceRange? ToTextarea(string text, SourceRange source)
    {
        if (!ValidText(text, out _) || !ValidRange(text, source)) return null;
        int start = source.Start, end = source.End;
        for (int i = 0; i < source.End; i++)
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            { if (i < source.Start) start--; end--; i++; }
        return new(start, end);
    }

    private static StringComparison Comparison(bool matchCase) => matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    // Non-overlapping matches, in source order. No match list proportional to file size.
    private static IEnumerable<SourceRange> Matches(string text, string query, bool matchCase)
    {
        for (int from = 0; from <= text.Length - query.Length;)
        {
            int at = text.IndexOf(query, from, Comparison(matchCase));
            if (at < 0) yield break;
            var range = new SourceRange(at, at + query.Length);
            if (ValidRange(text, range)) { yield return range; from = range.End; }
            else from = at + 1;
        }
    }

    public static SourceMatches? Find(string text, SourceRange selection, string query, bool matchCase, bool previous = false)
    {
        if (!ValidText(text, out _) || !ValidRange(text, selection) || !ValidText(query, out _)) return null;
        if (query.Length == 0) return new(0, 0, null);
        int count = 0, ordinal = 0;
        SourceRange? first = null, last = null, chosen = null;
        foreach (var match in Matches(text, query, matchCase))
        {
            count++; first ??= match; last = match;
            if (previous ? match.End <= selection.Start : chosen is null && match.Start >= selection.End)
            { chosen = match; ordinal = count; }
        }
        if (chosen is null && count > 0) { chosen = previous ? last : first; ordinal = previous ? count : 1; }
        return new(count, ordinal, chosen);
    }

    public static SourceEdit? Replace(string text, SourceRange selection, string query, string replacement,
        bool matchCase, bool all, long maxBytes = WorkspaceTextLimits.FileBytes)
    {
        if (!ValidText(text, out int bytes) || !ValidRange(text, selection) || !ValidText(query, out _) ||
            !ValidText(replacement, out int replacementBytes) || query.Length == 0) return null;
        long outputBytes = bytes;
        int count = 0;
        if (all)
        {
            foreach (var match in Matches(text, query, matchCase))
            {
                // Case-insensitive matches need not have the query's UTF-8 length.
                outputBytes += replacementBytes - Utf8.GetByteCount(text.AsSpan(match.Start, match.End - match.Start));
                count++;
            }
        }
        else
        {
            if (!text.AsSpan(selection.Start, selection.End - selection.Start).Equals(query.AsSpan(), Comparison(matchCase))) return null;
            outputBytes += replacementBytes - Utf8.GetByteCount(text.AsSpan(selection.Start, selection.End - selection.Start));
            count = 1;
        }
        if (count == 0 || outputBytes > Math.Min(maxBytes, WorkspaceTextLimits.FileBytes) || outputBytes < 0) return null;
        // All output allocation occurs only after the final byte budget is proven.
        var builder = new StringBuilder((int)outputBytes);
        int cursor = 0, selectedStart = selection.Start, selectedEnd = selection.End;
        foreach (var match in all ? Matches(text, query, matchCase) : new[] { selection })
        {
            builder.Append(text, cursor, match.Start - cursor);
            selectedStart = builder.Length;
            builder.Append(replacement);
            selectedEnd = builder.Length;
            cursor = match.End;
        }
        builder.Append(text, cursor, text.Length - cursor);
        return new(builder.ToString(), new(selectedStart, selectedEnd));
    }

    public static SourceEdit? Lines(string text, SourceRange selection, LineTool tool, long maxBytes = WorkspaceTextLimits.FileBytes)
    {
        if (!ValidText(text, out int bytes) || !ValidRange(text, selection) || !Enum.IsDefined(tool)) return null;
        int anchor = selection.Start;
        // A caret on the terminal empty sentinel belongs to the last real line.
        if (anchor == text.Length && anchor > 0 && IsNewline(text[anchor - 1]))
        { anchor--; if (anchor > 0 && text[anchor] == '\n' && text[anchor - 1] == '\r') anchor--; }
        int first = anchor;
        while (first > 0 && !IsNewline(text[first - 1])) first--;
        // A nonempty range ending at the next line start excludes that line.
        int last = selection.End > selection.Start ? selection.End - 1 : anchor;
        int stop = Math.Max(first, last);
        while (stop < text.Length && !IsNewline(text[stop])) stop++;
        if (stop < text.Length && text[stop++] == '\r' && stop < text.Length && text[stop] == '\n') stop++;
        bool uncomment = tool == LineTool.ToggleComment;
        for (int line = first; line < stop || line == first;)
        {
            if (line >= text.Length || text[line] != '%') uncomment = false;
            int next = NextLine(text, line, stop); if (next <= line || next >= stop) break; line = next;
        }
        int Removed(int line) => tool == LineTool.Unindent
            ? line < text.Length && text[line] == '\t' ? 1 : line < text.Length && text[line] == ' ' ? line + 1 < text.Length && text[line + 1] == ' ' ? 2 : 1 : 0
            : uncomment ? 1 : 0;
        string added = tool == LineTool.Indent ? "  " : tool == LineTool.ToggleComment && !uncomment ? "%" : "";
        long outputBytes = bytes;
        for (int line = first; line < stop || line == first;)
        {
            outputBytes += added.Length - Removed(line);
            int next = NextLine(text, line, stop); if (next <= line || next >= stop) break; line = next;
        }
        if (outputBytes > Math.Min(maxBytes, WorkspaceTextLimits.FileBytes) || outputBytes < 0) return null;
        var builder = new StringBuilder((int)outputBytes);
        builder.Append(text, 0, first);
        for (int line = first; line < stop || line == first;)
        {
            int next = NextLine(text, line, stop), removed = Removed(line);
            builder.Append(added).Append(text, line + removed, next - line - removed);
            if (next <= line || next >= stop) break; line = next;
        }
        int end = builder.Length;
        builder.Append(text, stop, text.Length - stop);
        return new(builder.ToString(), new(first, end));
    }

    private static bool IsNewline(char c) => c is '\r' or '\n';
    private static int NextLine(string text, int start, int stop)
    {
        int i = start;
        while (i < stop && !IsNewline(text[i])) i++;
        if (i < stop && text[i++] == '\r' && i < stop && text[i] == '\n') i++;
        return i;
    }
}
