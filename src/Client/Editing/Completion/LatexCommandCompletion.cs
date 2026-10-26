using Client.Editing.Highlighting;
using Client.Workspace.Patching;

namespace Client.Editing.Completion;

public enum CompletionMatchState { Ready, Empty, Unavailable }
public sealed record LatexCommand(string Id, string Label)
{
    public string Insertion => "\\" + Id + "{}";
}
public sealed record CompletionMatches(CompletionMatchState State, SourceRange? Prefix, IReadOnlyList<LatexCommand> Commands);

/// <summary>Explicit lexical prefix completion only. The existing scanner owns
/// command/comment/verbatim recognition and limits; this is not another parser.</summary>
public static class LatexCommandCompletion
{
    private static readonly IReadOnlyList<LatexCommand> Catalog = Array.AsReadOnly(new[]
    {
        new LatexCommand("section", "Section"), new("subsection", "Subsection"), new("subsubsection", "Subsubsection"),
        new("paragraph", "Paragraph"), new("textbf", "Bold text"), new("textit", "Italic text"), new("emph", "Emphasis"),
        new("label", "Label"), new("ref", "Reference"), new("pageref", "Page reference"), new("cite", "Citation"),
        new("footnote", "Footnote"), new("caption", "Caption"), new("title", "Title"), new("author", "Author"), new("date", "Date")
    }.OrderBy(c => c.Id, StringComparer.Ordinal).ToArray());

    public static CompletionMatches Suggest(string text, SourceRange caret)
    {
        if (!SourceTools.ValidText(text, out _) || !SourceTools.ValidRange(text, caret))
            return new(CompletionMatchState.Unavailable, null, Array.Empty<LatexCommand>());
        var scan = LatexHighlighter.Scan(text);
        if (!scan.Ready) return new(CompletionMatchState.Unavailable, null, Array.Empty<LatexCommand>());
        if (caret.Start != caret.End) return new(CompletionMatchState.Empty, null, Array.Empty<LatexCommand>());
        foreach (var token in scan.Spans)
        {
            if (token.Kind != LexicalKind.Command || token.Start + token.Length != caret.End || token.Length < 2
                || text[token.Start] != '\\') continue;
            var letters = text.AsSpan(token.Start + 1, token.Length - 1);
            bool ascii = true;
            foreach (char c in letters) if (c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z')) { ascii = false; break; }
            if (!ascii) continue;
            string prefix = letters.ToString();
            var matches = Catalog.Where(c => c.Id.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            return new(matches.Length == 0 ? CompletionMatchState.Empty : CompletionMatchState.Ready,
                new(token.Start, caret.End), Array.AsReadOnly(matches));
        }
        return new(CompletionMatchState.Empty, null, Array.Empty<LatexCommand>());
    }

    public static SourceEdit? Transform(string text, SourceRange prefix, string commandId, long maxBytes = WorkspaceTextLimits.FileBytes)
    {
        var matches = Suggest(text, new(prefix.End, prefix.End));
        if (matches.State != CompletionMatchState.Ready || matches.Prefix != prefix) return null;
        var command = matches.Commands.FirstOrDefault(c => string.Equals(c.Id, commandId, StringComparison.Ordinal));
        if (command is null || maxBytes < 0 || !SourceTools.ValidText(text, out int inputBytes)) return null;
        string insertion = command.Insertion;
        // Suggest proved backslash + ASCII letters; fixed catalog insertions are
        // ASCII too. UTF16 lengths are therefore EXACT UTF8 bytes for these two
        // pieces only, never for the unrelated Unicode/CRLF source.
        long projected;
        try { projected = checked((long)inputBytes - (prefix.End - prefix.Start) + insertion.Length); }
        catch (OverflowException) { return null; }
        if (projected > Math.Min(maxBytes, WorkspaceTextLimits.FileBytes)) return null;
        // No replacement/slice allocation occurs until the exact growth fits.
        string changed = text[..prefix.Start] + insertion + text[prefix.End..];
        if (!SourceTools.ValidText(changed, out int bytes) || bytes != projected || bytes > maxBytes) return null;
        int inside = prefix.Start + insertion.Length - 1;
        var selection = new SourceRange(inside, inside);
        return SourceTools.ValidRange(changed, selection) ? new(changed, selection) : null;
    }
}
