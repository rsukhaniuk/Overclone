namespace Client.Editing.Highlighting;

public enum LexicalKind { Text, Command, Comment, Group, Math, Verbatim }
public enum HighlightStatus { Ready, TextLimit, TokenLimit, LineLimit, InvalidUnicode }

/// <summary>Half-open UTF-16 source range. Includes every original code unit,
/// including CR/LF; neither surrogate pairs nor CRLF are split.</summary>
public readonly record struct LexicalSpan(int Start, int Length, LexicalKind Kind);
public sealed record HighlightDocument(string Source, HighlightStatus Status,
    IReadOnlyList<LexicalSpan> Spans, int LineCount)
{
    public bool Ready => Status == HighlightStatus.Ready;
}

/// <summary>Bounded lexical decoration only, not a TeX parser or validity check.
/// One forward cursor consumes scalars/newlines; no regex/backtracking. Exceeding
/// any budget discards all tokens, never truncates or changes the source.</summary>
public static class LatexHighlighter
{
    public const int MaxTextLength = 128 * 1024;
    public const int MaxTokens = 4096;
    public const int MaxLines = 4096;
    private static readonly (string Open, string Close)[] VerbatimPairs =
    {
        ("\\begin{verbatim}", "\\end{verbatim}"),
        ("\\begin{verbatim*}", "\\end{verbatim*}")
    };

    public static HighlightDocument Scan(string? source)
    {
        if (source is null) return new("", HighlightStatus.InvalidUnicode, Array.Empty<LexicalSpan>(), 0);
        // Check before allocating a scanner/list or a normalized display copy.
        if (source.Length > MaxTextLength) return new(source, HighlightStatus.TextLimit, Array.Empty<LexicalSpan>(), 0);
        return new Scanner(source).Run();
    }

    private sealed class Scanner(string source)
    {
        private readonly List<LexicalSpan> _spans = new(Math.Min(source.Length, 128));
        private int _at, _lines = 1;
        private HighlightStatus _status = HighlightStatus.Ready;
        private bool Running => _status == HighlightStatus.Ready && _at < source.Length;
        private bool At(string literal) => source.AsSpan(_at).StartsWith(literal.AsSpan(), StringComparison.Ordinal);
        private static bool Letter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
        private static bool Special(char c) => c is '\\' or '%' or '{' or '}' or '$';
        private static bool Newline(char c) => c is '\r' or '\n';

        // Every consumed source scalar/newline passes here, including inside
        // comments/verbatim. Invalid UTF-16 anywhere triggers plain fallback.
        private void Take()
        {
            char c = source[_at++];
            if (c == '\r')
            {
                if (_at < source.Length && source[_at] == '\n') _at++;
                _lines++;
            }
            else if (c == '\n') _lines++;
            else if (char.IsHighSurrogate(c))
            {
                if (_at < source.Length && char.IsLowSurrogate(source[_at])) _at++;
                else _status = HighlightStatus.InvalidUnicode;
            }
            else if (char.IsLowSurrogate(c)) _status = HighlightStatus.InvalidUnicode;
            if (_lines > MaxLines) _status = HighlightStatus.LineLimit;
        }

        private void Literal(string text)
        {
            int stop = _at + text.Length;
            while (Running && _at < stop) Take();
        }

        private void Add(int start, LexicalKind kind)
        {
            if (_status != HighlightStatus.Ready) return;
            if (_spans.Count >= MaxTokens) { _status = HighlightStatus.TokenLimit; return; }
            _spans.Add(new(start, _at - start, kind));
        }

        private LexicalKind Backslash()
        {
            // Only these literal standard environments are recognized. Custom
            // environments/macros/catcodes intentionally are not interpreted.
            foreach (var pair in VerbatimPairs)
            {
                if (!At(pair.Open)) continue;
                Literal(pair.Open);
                while (Running && !At(pair.Close)) Take();
                if (Running) Literal(pair.Close);
                return LexicalKind.Verbatim;
            }
            Take(); // backslash
            if (!Running) return LexicalKind.Command;
            if (!Letter(source[_at]))
            {
                bool math = source[_at] is '(' or ')' or '[' or ']';
                Take(); // one control symbol (a whole scalar, never half an emoji)
                return math ? LexicalKind.Math : LexicalKind.Command;
            }
            int name = _at;
            while (Running && Letter(source[_at])) Take();
            bool verb = source.AsSpan(name, _at - name).SequenceEqual("verb".AsSpan());
            if (!verb || !Running) return LexicalKind.Command;
            if (source[_at] == '*') Take();
            if (!Running || char.IsWhiteSpace(source[_at])) return LexicalKind.Command;
            int delimiterAt = _at;
            Take();
            int width = _at - delimiterAt;
            while (Running && !Newline(source[_at]))
            {
                bool closing = _at + width <= source.Length &&
                    source.AsSpan(_at, width).SequenceEqual(source.AsSpan(delimiterAt, width));
                Take();
                if (closing) break;
            }
            // An unterminated inline verb stops at the line boundary; do not
            // misleadingly color its percent/dollar/group characters meanwhile.
            return LexicalKind.Verbatim;
        }

        public HighlightDocument Run()
        {
            while (Running)
            {
                int start = _at;
                LexicalKind kind;
                switch (source[_at])
                {
                    case '\\': kind = Backslash(); break;
                    case '%':
                        kind = LexicalKind.Comment;
                        while (Running && !Newline(source[_at])) Take();
                        break;
                    case '{': case '}': kind = LexicalKind.Group; Take(); break;
                    case '$':
                        kind = LexicalKind.Math;
                        Take();
                        if (Running && source[_at] == '$') Take();
                        break;
                    default:
                        kind = LexicalKind.Text;
                        do { Take(); } while (Running && !Special(source[_at]));
                        break;
                }
                Add(start, kind);
            }
            return new(source, _status,
                _status == HighlightStatus.Ready ? _spans.AsReadOnly() : Array.Empty<LexicalSpan>(),
                _status == HighlightStatus.Ready ? _lines : 0);
        }
    }
}

/// <summary>One-document cache: unrelated renders reuse tokenization by exact
/// text (reference fast path). It never retains a project-wide source history.</summary>
public sealed class HighlightCache
{
    private HighlightDocument? _current;
    public HighlightDocument Get(string source)
    {
        if (_current is not null && (ReferenceEquals(_current.Source, source) ||
            string.Equals(_current.Source, source, StringComparison.Ordinal))) return _current;
        return _current = LatexHighlighter.Scan(source);
    }
}
