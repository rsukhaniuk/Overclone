using Client.Editing;
using Client.Workspace.Patching;
using Xunit;

namespace Workspace.Tests.Editing;

public sealed class SourceToolsTests
{
    [Theory]
    [InlineData("one\ntwo\nthree", 0, 4, "  one\ntwo\nthree", 0, 6)]
    [InlineData("one\r\ntwo\r\nthree", 1, 5, "  one\r\ntwo\r\nthree", 0, 7)]
    [InlineData("one\r\ntwo\r\n", 5, 5, "one\r\n  two\r\n", 5, 12)]
    [InlineData("one\n", 4, 4, "  one\n", 0, 6)]
    [InlineData("", 0, 0, "  ", 0, 2)]
    [InlineData("\n", 0, 1, "  \n", 0, 3)]
    [InlineData("а🙂\r\nб\nв\rг", 1, 8, "  а🙂\r\n  б\n  в\rг", 0, 15)]
    public void IndentOnlyIntersectingFullLines(string text, int start, int end, string expected, int selectedStart, int selectedEnd)
    {
        var result = Assert.IsType<SourceEdit>(SourceTools.Lines(text, new(start, end), LineTool.Indent));
        Assert.Equal(expected, result.Text);
        Assert.Equal(new(selectedStart, selectedEnd), result.Selection);
        var restored = Assert.IsType<SourceEdit>(SourceTools.Lines(result.Text, result.Selection, LineTool.Unindent));
        Assert.Equal(text, restored.Text);
    }

    [Theory]
    [InlineData("  a\n b\n\tc\nd", "a\nb\nc\nd")]
    [InlineData("   a\r\n\t b\r\n", " a\r\n b\r\n")]
    [InlineData("\n\n", "\n\n")]
    public void UnindentUpToTwoSpacesOrOneTab(string text, string expected)
    {
        Assert.Equal(expected, SourceTools.Lines(text, new(0, text.Length), LineTool.Unindent)!.Text);
    }

    [Theory]
    [InlineData("текст🙂\r\nдалі\r\n")]
    [InlineData("a\n\nb\n")]
    [InlineData("  a\rb\nlast")]
    [InlineData("")]
    public void CommentRoundtripPreservesEveryUnrelatedCodeUnit(string text)
    {
        var commented = SourceTools.Lines(text, new(0, text.Length), LineTool.ToggleComment)!;
        Assert.StartsWith("%", commented.Text);
        Assert.Equal(text, SourceTools.Lines(commented.Text, commented.Selection, LineTool.ToggleComment)!.Text);
    }

    [Theory]
    [InlineData("%a\n%b", "a\nb")]
    [InlineData("%a\nb", "%%a\n%b")]
    [InlineData("  %a\n b", "%  %a\n% b")]
    public void CommentUsesColumnZeroAndAllLinesMustAlreadyBeCommented(string text, string expected)
    {
        Assert.Equal(expected, SourceTools.Lines(text, new(0, text.Length), LineTool.ToggleComment)!.Text);
    }

    [Theory]
    [InlineData("a\r\nУкраїна🙂\r\nz", 2, 11, 3, 12)]
    [InlineData("a\rb\nc", 2, 3, 2, 3)]
    [InlineData("🙂\r\n", 2, 3, 2, 4)]
    [InlineData("🙂\r\n", 3, 3, 4, 4)]
    [InlineData("", 0, 0, 0, 0)]
    public void DomToExactSourceRoundtrip(string text, int domStart, int domEnd, int sourceStart, int sourceEnd)
    {
        var dom = new SourceRange(domStart, domEnd);
        Assert.True(SourceTools.TryFromTextarea(text, dom, out var source));
        Assert.Equal(new(sourceStart, sourceEnd), source);
        Assert.Equal(dom, SourceTools.ToTextarea(text, source));
    }

    [Fact]
    public void RealCrLfSelectionFindReplaceAndLineToolsUseExactSource()
    {
        const string source = "перше🙂\r\nціль\r\nостаннє\r\n";
        string dom = SourceTools.TextareaText(source);
        int start = dom.IndexOf("ціль", StringComparison.Ordinal);
        Assert.True(SourceTools.TryFromTextarea(source, new(start, start + 4), out var selection));
        var find = SourceTools.Find(source, new(0, 0), "ціль", true)!;
        Assert.Equal(selection, find.Selection);
        var replaced = SourceTools.Replace(source, selection, "ціль", "нова🙂", true, false)!;
        Assert.Equal("перше🙂\r\nнова🙂\r\nостаннє\r\n", replaced.Text);
        Assert.Equal(new(start, start + 6), SourceTools.ToTextarea(replaced.Text, replaced.Selection));
        Assert.Equal("перше🙂\r\n  ціль\r\nостаннє\r\n", SourceTools.Lines(source, selection, LineTool.Indent)!.Text);
    }

    [Theory]
    [InlineData("abc ABC abc", "abc", false, 0, 0, false, 3, 1, 0, 3)]
    [InlineData("abc ABC abc", "abc", true, 0, 3, false, 2, 2, 8, 11)]
    [InlineData("abc ABC abc", "abc", true, 8, 11, false, 2, 1, 0, 3)]
    [InlineData("abc ABC abc", "abc", false, 0, 0, true, 3, 3, 8, 11)]
    [InlineData("abc ABC abc", "abc", false, 8, 11, true, 3, 2, 4, 7)]
    [InlineData("Україна🙂 Україна🙂", "Україна🙂", true, 0, 0, false, 2, 1, 0, 9)]
    [InlineData("ааа", "аа", true, 0, 0, false, 1, 1, 0, 2)]
    public void LiteralFindWrapsInBothDirections(string text, string query, bool matchCase, int start, int end,
        bool previous, int count, int ordinal, int matchStart, int matchEnd)
    {
        var matches = SourceTools.Find(text, new(start, end), query, matchCase, previous)!;
        Assert.Equal(count, matches.Count);
        Assert.Equal(ordinal, matches.Ordinal);
        Assert.Equal(new(matchStart, matchEnd), matches.Selection);
    }

    [Theory]
    [InlineData("")]
    [InlineData("absent")]
    [InlineData(".*")]
    public void EmptyOrMissingLiteralHasZeroMatches(string query)
    {
        var result = SourceTools.Find("Україна🙂", new(0, 0), query, false)!;
        Assert.Equal(0, result.Count);
        Assert.Equal(0, result.Ordinal);
        Assert.Null(result.Selection);
    }

    [Theory]
    [InlineData("a A a", "a", "🙂", false, "🙂 🙂 🙂")]
    [InlineData("a A a", "a", "", true, " A ")]
    [InlineData("ааа", "аа", "б", true, "ба")]
    [InlineData("one\r\none\r\n", "one", "два", true, "два\r\nдва\r\n")]
    [InlineData("a a", "a", "a a", true, "a a a a")]
    public void ReplaceAllIsLiteralNonRecursiveAndPreservesNewlines(string text, string query, string replacement, bool matchCase, string expected)
    {
        Assert.Equal(expected, SourceTools.Replace(text, new(0, 0), query, replacement, matchCase, true)!.Text);
    }

    [Fact]
    public void ReplaceCurrentRequiresExactSelectedMatch()
    {
        Assert.Null(SourceTools.Replace("a a", new(0, 0), "a", "b", true, false));
        Assert.Null(SourceTools.Replace("a a", new(0, 3), "a", "b", true, false));
        Assert.Null(SourceTools.Replace("a a", new(0, 1), "", "b", true, false));
        var result = SourceTools.Replace("a A", new(2, 3), "a", "🙂", false, false)!;
        Assert.Equal("a 🙂", result.Text);
        Assert.Equal(new(2, 4), result.Selection);
    }

    public static IEnumerable<object[]> InvalidRanges()
    {
        yield return new object[] { "a🙂b", -1, 0 };
        yield return new object[] { "a🙂b", 3, 2 };
        yield return new object[] { "a🙂b", 0, 5 };
        yield return new object[] { "a🙂b", 2, 3 };
        yield return new object[] { "a🙂b", 1, 2 };
        yield return new object[] { "a\r\nb", 2, 3 };
    }

    [Theory]
    [MemberData(nameof(InvalidRanges))]
    public void InvalidRangesFailWithoutAnEdit(string text, int start, int end)
    {
        var range = new SourceRange(start, end);
        Assert.Null(SourceTools.Lines(text, range, LineTool.Indent));
        Assert.Null(SourceTools.Find(text, range, "a", false));
        Assert.Null(SourceTools.Replace(text, range, "a", "b", false, true));
    }

    [Fact]
    public void RuntimeUnpairedSurrogatesFailWithoutAnEdit()
    {
        // Do not transport malformed strings as xUnit theory data: discovery /
        // execution serialization can replace the unpaired code unit with U+FFFD.
        var high = new string(new[] { (char)0xD800, 'x' });
        var low = new string(new[] { 'x', (char)0xDC00 });
        Assert.Equal(0xD800, (int)high[0]);
        Assert.Equal(0xDC00, (int)low[1]);
        Assert.True(char.IsHighSurrogate(high[0]) && !char.IsLowSurrogate(high[1]));
        Assert.True(char.IsLowSurrogate(low[1]) && !char.IsHighSurrogate(low[0]));
        var strictUtf8 = new System.Text.UTF8Encoding(false, true);
        foreach (var text in new[] { high, low })
        {
            Assert.DoesNotContain('\uFFFD', text);
            Assert.Throws<System.Text.EncoderFallbackException>(() => strictUtf8.GetByteCount(text));
            Assert.False(SourceTools.ValidText(text, out _));
            var range = new SourceRange(0, 0);
            Assert.Null(SourceTools.Lines(text, range, LineTool.Indent));
            Assert.Null(SourceTools.Find(text, range, "x", false));
            Assert.Null(SourceTools.Replace(text, range, "x", "y", false, true));
            Assert.False(SourceTools.TryFromTextarea(text, range, out _));
        }
    }

    [Fact]
    public void LiteralReplacementCharacterIsValidText()
    {
        var text = new string(new[] { (char)0xFFFD, 'x' });
        Assert.Equal(0xFFFD, (int)text[0]);
        Assert.True(SourceTools.ValidText(text, out int bytes));
        Assert.Equal(4, bytes);
        Assert.Equal("  " + text, SourceTools.Lines(text, new(0, 0), LineTool.Indent)!.Text);
        Assert.Equal(new SourceRange(0, 1), SourceTools.Find(text, new(0, 0), "\uFFFD", true)!.Selection);
        Assert.Equal("yx", SourceTools.Replace(text, new(0, 1), "\uFFFD", "y", true, false)!.Text);
        Assert.True(SourceTools.TryFromTextarea(text, new(0, 1), out var source));
        Assert.Equal(new SourceRange(0, 1), source);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(0, 1)]
    [InlineData(0, 5)]
    [InlineData(-1, 0)]
    public void InvalidDomOffsetsFail(int start, int end) => Assert.False(SourceTools.TryFromTextarea("🙂\r\n", new(start, end), out _));

    [Fact]
    public void MalformedSearchAndReplacementAreRejected()
    {
        Assert.Null(SourceTools.Find("a", new(0, 0), "\uD800", true));
        Assert.Null(SourceTools.Replace("a", new(0, 1), "a", "\uDC00", true, false));
        Assert.Null(SourceTools.Replace("a", new(0, 0), "", "", true, true));
    }

    [Fact]
    public void GrowthIsPreflightedInUtf8BeforeOutputAllocation()
    {
        Assert.Null(SourceTools.Replace("яяя", new(0, 0), "я", "🙂", true, true, 11));
        Assert.Equal("🙂🙂🙂", SourceTools.Replace("яяя", new(0, 0), "я", "🙂", true, true, 12)!.Text);
        Assert.Null(SourceTools.Replace("aaaaaaaa", new(0, 0), "a", new string('b', 1024 * 1024 + 1), true, true));
        Assert.Null(SourceTools.Lines("я", new(0, 1), LineTool.Indent, 3));
        Assert.Equal("  я", SourceTools.Lines("я", new(0, 1), LineTool.Indent, 4)!.Text);
        Assert.Null(SourceTools.Replace("a", new(0, 1), "a", "b", true, false, -1));
        Assert.Null(SourceTools.Lines(new string('a', (int)WorkspaceTextLimits.FileBytes), new(0, 0),
            LineTool.Indent, WorkspaceTextLimits.FileBytes + 1));
    }
}
