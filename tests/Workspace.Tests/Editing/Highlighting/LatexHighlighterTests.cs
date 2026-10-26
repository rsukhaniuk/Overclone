using Client.Editing.Highlighting;
using Xunit;

namespace Workspace.Tests.Editing.Highlighting;

public sealed class LatexHighlighterTests
{
    private static string Text(HighlightDocument document, LexicalSpan span) => document.Source.Substring(span.Start, span.Length);

    [Theory]
    [InlineData("")]
    [InlineData("plain Україна 🙂")]
    [InlineData("a\r\nb\rc\n")]
    [InlineData("\\section{Вступ🙂}\n% коментар\n$x$ $$y$$ \\(z\\) \\[q\\]")]
    [InlineData("\\% \\$ \\{ \\} \\\\%comment\n")]
    [InlineData("\\verb|a%${🙂}| rest\r\n")]
    [InlineData("\\begin{verbatim}\r\na%${🙂}\\x\r\n\\end{verbatim}\n")]
    [InlineData("\\begin{verbatim*} $%{}\n\\end{verbatim*}")]
    [InlineData("\\begin{verbatim} never closed %${")]
    [InlineData("<img src=x onerror=alert(1)>\\textbf{<script>text</script>}")]
    [InlineData("\uFFFD is valid Unicode")]
    public void SpansCoverExactSourceWithoutSplittingScalarOrCrLf(string source)
    {
        var result = LatexHighlighter.Scan(source);
        Assert.True(result.Ready);
        Assert.Same(source, result.Source);
        Assert.Equal(source, string.Concat(result.Spans.Select(span => Text(result, span))));
        int offset = 0;
        foreach (var span in result.Spans)
        {
            Assert.Equal(offset, span.Start);
            Assert.True(span.Length > 0);
            offset += span.Length;
            if (offset < source.Length)
            {
                Assert.False(char.IsHighSurrogate(source[offset - 1]) && char.IsLowSurrogate(source[offset]));
                Assert.False(source[offset - 1] == '\r' && source[offset] == '\n');
            }
        }
        Assert.Equal(source.Length, offset);
    }

    [Theory]
    [InlineData("\\section", LexicalKind.Command)]
    [InlineData("\\alpha", LexicalKind.Command)]
    [InlineData("\\%", LexicalKind.Command)]
    [InlineData("\\$", LexicalKind.Command)]
    [InlineData("\\{", LexicalKind.Command)]
    [InlineData("\\}", LexicalKind.Command)]
    [InlineData("\\🙂", LexicalKind.Command)]
    [InlineData("\\", LexicalKind.Command)]
    [InlineData("{", LexicalKind.Group)]
    [InlineData("}", LexicalKind.Group)]
    [InlineData("$", LexicalKind.Math)]
    [InlineData("$$", LexicalKind.Math)]
    [InlineData("\\(", LexicalKind.Math)]
    [InlineData("\\)", LexicalKind.Math)]
    [InlineData("\\[", LexicalKind.Math)]
    [InlineData("\\]", LexicalKind.Math)]
    [InlineData("% коментар 🙂", LexicalKind.Comment)]
    [InlineData("текст🙂", LexicalKind.Text)]
    public void ExpectedLexicalClass(string source, LexicalKind expected)
    {
        var result = LatexHighlighter.Scan(source);
        var span = Assert.Single(result.Spans);
        Assert.Equal(expected, span.Kind);
        Assert.Equal(source, Text(result, span));
    }

    [Fact]
    public void CommandWordsAreAsciiAndEscapedSymbolsDoNotBecomeDelimiters()
    {
        var result = LatexHighlighter.Scan("\\alpha2 \\Привіт \\% \\$ \\{ \\} \\\\%real\r\nnext");
        var words = result.Spans.Where(span => span.Kind == LexicalKind.Command).Select(span => Text(result, span)).ToArray();
        Assert.Equal(new[] { "\\alpha", "\\П", "\\%", "\\$", "\\{", "\\}", "\\\\" }, words);
        Assert.Equal("%real", Text(result, Assert.Single(result.Spans, span => span.Kind == LexicalKind.Comment)));
        Assert.DoesNotContain(result.Spans, span => span.Kind is LexicalKind.Math or LexicalKind.Group);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    public void CommentStopsBeforeEveryNewlineStyle(string newline)
    {
        var result = LatexHighlighter.Scan("% first" + newline + "\\second{}");
        Assert.Equal("% first", Text(result, result.Spans[0]));
        Assert.Equal(LexicalKind.Text, result.Spans[1].Kind);
        Assert.Equal(newline, Text(result, result.Spans[1]));
        Assert.Equal(2, result.LineCount);
        Assert.Contains(result.Spans, span => span.Kind == LexicalKind.Command && Text(result, span) == "\\second");
    }

    [Theory]
    [InlineData("\\verb|%${}\\foo🙂|", "\\after")]
    [InlineData("\\verb*+%${}\\foo🙂+", "\\after")]
    [InlineData("\\verb🙂%${}\\foo🙂", "\\after")]
    [InlineData("\\verb|unterminated %${}", "\n\\after")]
    [InlineData("\\begin{verbatim}\n%${}\\foo🙂\r\n\\end{verbatim}", "\\after")]
    [InlineData("\\begin{verbatim*}\r%${}\\foo🙂\r\\end{verbatim*}", "\\after")]
    public void VerbatimSuppressesMisleadingColorsThenResumes(string verbatim, string suffix)
    {
        var result = LatexHighlighter.Scan(verbatim + suffix);
        Assert.True(result.Ready);
        Assert.Equal(LexicalKind.Verbatim, result.Spans[0].Kind);
        Assert.Equal(verbatim, Text(result, result.Spans[0]));
        Assert.DoesNotContain(result.Spans, span => span.Kind is LexicalKind.Comment or LexicalKind.Math or LexicalKind.Group);
        Assert.Contains(result.Spans, span => span.Kind == LexicalKind.Command && Text(result, span) == "\\after");
    }

    [Fact]
    public void CustomEnvironmentsAndCatcodesAreNotInterpretedAsSemanticPolicy()
    {
        var result = LatexHighlighter.Scan("\\catcode`\\%=12 \\begin{customverbatim}\n%still lexical comment\n\\end{customverbatim}");
        Assert.Contains(result.Spans, span => span.Kind == LexicalKind.Comment);
        Assert.DoesNotContain(result.Spans, span => span.Kind == LexicalKind.Verbatim);
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("a", 1)]
    [InlineData("\n", 2)]
    [InlineData("\r\n", 2)]
    [InlineData("a\r\nb\rc\n", 4)]
    [InlineData("\t\t🙂\r\n\t", 2)]
    public void LogicalLinesIncludeEmptyAndTerminalLines(string source, int count) => Assert.Equal(count, LatexHighlighter.Scan(source).LineCount);

    [Fact]
    public void EachBudgetFailsClosedWithoutTruncatingSource()
    {
        var exact = new string('a', LatexHighlighter.MaxTextLength);
        Assert.True(LatexHighlighter.Scan(exact).Ready);
        var tooLong = exact + "a";
        AssertFallback(tooLong, HighlightStatus.TextLimit);
        var exactTokens = new string('{', LatexHighlighter.MaxTokens);
        Assert.Equal(LatexHighlighter.MaxTokens, LatexHighlighter.Scan(exactTokens).Spans.Count);
        AssertFallback(exactTokens + "}", HighlightStatus.TokenLimit);
        var exactLines = new string('\n', LatexHighlighter.MaxLines - 1);
        Assert.Equal(LatexHighlighter.MaxLines, LatexHighlighter.Scan(exactLines).LineCount);
        AssertFallback(exactLines + "\n", HighlightStatus.LineLimit);
        // A pathological token stream remains linear/bounded, with no partial layer.
        AssertFallback(string.Concat(Enumerable.Repeat("${}%comment\n", 2000)), HighlightStatus.TokenLimit);
    }

    private static void AssertFallback(string source, HighlightStatus expected)
    {
        var result = LatexHighlighter.Scan(source);
        Assert.Equal(expected, result.Status);
        Assert.False(result.Ready);
        Assert.Same(source, result.Source);
        Assert.Empty(result.Spans);
    }

    [Fact]
    public void RuntimeMalformedUnicodeAnywhereFallsBackWithoutThrowing()
    {
        var malformed = new string(new[] { (char)0xD800, 'x' });
        Assert.Equal(0xD800, (int)malformed[0]);
        Assert.Throws<System.Text.EncoderFallbackException>(() => new System.Text.UTF8Encoding(false, true).GetByteCount(malformed));
        foreach (string prefix in new[] { "", "%", "\\", "\\verb|", "\\begin{verbatim}" })
            AssertFallback(prefix + malformed, HighlightStatus.InvalidUnicode);
        AssertFallback(new string(new[] { (char)0xDC00 }), HighlightStatus.InvalidUnicode);
        Assert.Equal(HighlightStatus.InvalidUnicode, LatexHighlighter.Scan(null).Status);
        Assert.True(LatexHighlighter.Scan("\uFFFD").Ready);
    }

    [Fact]
    public void OneDocumentCacheReusesExactTextIncludingLargeFallback()
    {
        var cache = new HighlightCache();
        string source = "\\section{Україна🙂}\r\n";
        var first = cache.Get(source);
        Assert.Same(first, cache.Get(source));
        Assert.Same(first, cache.Get(new string(source.ToCharArray())));
        Assert.NotSame(first, cache.Get(source.Replace("\r\n", "\n")));
        string large = new('a', 8 * 1024 * 1024);
        var plain = cache.Get(large);
        Assert.Equal(HighlightStatus.TextLimit, plain.Status);
        Assert.Same(plain, cache.Get(large));
        Assert.Empty(plain.Spans);
    }
}
