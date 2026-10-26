using System.Text;
using Client.Editing;
using Client.Editing.Completion;
using Client.Editing.Highlighting;
using Xunit;

namespace Workspace.Tests.Editing.Completion;

public sealed class LatexCommandCompletionTests
{
    private static CompletionMatches At(string marked)
    {
        int caret = marked.IndexOf('¦');
        string source = marked.Remove(caret, 1);
        return LatexCommandCompletion.Suggest(source, new(caret, caret));
    }
    [Theory]
    [InlineData(@"\se¦", "section")]
    [InlineData(@"$\te¦$", "textbf,textit")]
    [InlineData(@"\s¦{keep}", "section,subsection,subsubsection")]
    [InlineData(@"\p¦*suffix", "pageref,paragraph")]
    [InlineData(@"\t¦", "textbf,textit,title")]
    public void Prefixes_are_ordinal_and_return_only_fixed_catalog(string marked, string ids)
    {
        var result = At(marked);
        Assert.Equal(CompletionMatchState.Ready, result.State);
        Assert.Equal(ids.Split(','), result.Commands.Select(c => c.Id).ToArray());
        Assert.All(result.Commands, c => { Assert.Equal("\\" + c.Id + "{}", c.Insertion); Assert.False(string.IsNullOrWhiteSpace(c.Label)); });
    }
    [Fact] public void Exactly_sixteen_known_commands_are_available_with_matching_prefixes()
    {
        string[] ids = ["section", "subsection", "subsubsection", "paragraph", "textbf", "textit", "emph", "label", "ref", "pageref", "cite", "footnote", "caption", "title", "author", "date"];
        foreach (string id in ids)
        {
            var result = At("\\" + id + "¦");
            Assert.Equal(CompletionMatchState.Ready, result.State);
            Assert.Contains(result.Commands, c => c.Id == id);
            Assert.All(result.Commands, c => Assert.Contains(c.Id, ids));
        }
        var all = ids.SelectMany(id => At("\\" + id[0] + "¦").Commands.Select(c => c.Id)).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(ids.OrderBy(s => s, StringComparer.Ordinal).ToArray(), all); Assert.Equal(16, all.Length);
    }
    [Theory]
    [InlineData(@"% \se¦")][InlineData(@"\verb|\se¦|")]
    [InlineData(@"\verb*+\se¦+")][InlineData(@"\begin{verbatim}\se¦\end{verbatim}")]
    [InlineData(@"\begin{verbatim*}\se¦\end{verbatim*}")]
    [InlineData(@"\\se¦")][InlineData(@"\%se¦")][InlineData(@"\🙂¦")]
    [InlineData(@"\se¦ction")][InlineData(@"\se ¦")][InlineData(@"\¦")]
    [InlineData(@"\Section¦")][InlineData(@"\SE¦")][InlineData(@"\unknown¦")]
    public void Non_command_end_comment_verbatim_or_control_symbol_has_no_suggestion(string marked)
    {
        var result = At(marked); Assert.Equal(CompletionMatchState.Empty, result.State); Assert.Empty(result.Commands);
    }
    [Fact] public void Noncollapsed_and_invalid_ranges_refuse()
    {
        Assert.Equal(CompletionMatchState.Empty, LatexCommandCompletion.Suggest(@"\sec", new(1, 4)).State);
        Assert.Equal(CompletionMatchState.Unavailable, LatexCommandCompletion.Suggest(@"\sec", new(-1, 2)).State);
        Assert.Equal(CompletionMatchState.Unavailable, LatexCommandCompletion.Suggest("🙂\\se", new(1, 1)).State);
        Assert.Equal(CompletionMatchState.Unavailable, LatexCommandCompletion.Suggest("\r\n\\se", new(1, 1)).State);
        Assert.Equal(CompletionMatchState.Unavailable, LatexCommandCompletion.Suggest("\ud800\\se", new(4, 4)).State);
        Assert.Equal(CompletionMatchState.Unavailable, LatexCommandCompletion.Suggest("\udc00\\se", new(4, 4)).State);
    }
    [Fact] public void Scanner_limits_are_unavailable_not_partial_suggestions()
    {
        string[] over = [new string('a', LatexHighlighter.MaxTextLength) + @"\se",
            new string('\n', LatexHighlighter.MaxLines) + @"\se",
            string.Concat(Enumerable.Repeat("{}", LatexHighlighter.MaxTokens)) + @"\se"];
        foreach (string text in over)
        {
            var result = LatexCommandCompletion.Suggest(text, new(text.Length, text.Length));
            Assert.Equal(CompletionMatchState.Unavailable, result.State); Assert.Empty(result.Commands);
        }
    }
    [Fact] public void Transform_changes_only_prefix_and_preserves_unicode_CRLF_arguments_and_suffix()
    {
        string before = "Україна🙂\r\n\\se{existing}*tail\r\n";
        int start = before.IndexOf('\\'); var prefix = new SourceRange(start, start + 3);
        var edit = Assert.IsType<SourceEdit>(LatexCommandCompletion.Transform(before, prefix, "section"));
        Assert.Equal("Україна🙂\r\n\\section{}{existing}*tail\r\n", edit.Text);
        Assert.Equal(new SourceRange(start + 9, start + 9), edit.Selection);
        Assert.Equal("{}", edit.Text.Substring(edit.Selection.Start - 1, 2));
        Assert.True(SourceTools.ValidRange(edit.Text, edit.Selection));
        Assert.Equal(SourceTools.ToTextarea(edit.Text, edit.Selection), new SourceRange(start + 8, start + 8));
    }
    [Fact] public void Utf8_growth_preflight_uses_exact_unicode_bytes_and_refuses_one_byte_too_small()
    {
        string text = "Україна🙂\r\n\\se{suffix}";
        int start = text.IndexOf('\\'); var prefix = new SourceRange(start, start + 3);
        string expected = "Україна🙂\r\n\\section{}{suffix}";
        int exactBytes = Encoding.UTF8.GetByteCount(expected);
        Assert.True(exactBytes > expected.Length);
        var edit = Assert.IsType<SourceEdit>(LatexCommandCompletion.Transform(text, prefix, "section", exactBytes));
        Assert.Equal(expected, edit.Text);
        Assert.Null(LatexCommandCompletion.Transform(text, prefix, "section", exactBytes - 1));
        Assert.Null(LatexCommandCompletion.Transform(text, prefix, "section", expected.Length));
        Assert.Null(LatexCommandCompletion.Transform(text, prefix, "section", -1));
    }

    [Fact] public void Only_known_matching_ID_exact_prefix_and_growth_budget_can_transform()
    {
        string text = @"\se"; var prefix = new SourceRange(0, text.Length);
        var edit = Assert.IsType<SourceEdit>(LatexCommandCompletion.Transform(text, prefix, "section"));
        int bytes = Encoding.UTF8.GetByteCount(edit.Text);
        Assert.NotNull(LatexCommandCompletion.Transform(text, prefix, "section", bytes));
        Assert.Null(LatexCommandCompletion.Transform(text, prefix, "section", bytes - 1));
        Assert.Null(LatexCommandCompletion.Transform(text, prefix, "section", -1));
        Assert.Null(LatexCommandCompletion.Transform(text, prefix, "textbf"));
        Assert.Null(LatexCommandCompletion.Transform(text, prefix, "Section"));
        Assert.Null(LatexCommandCompletion.Transform(text, prefix, "section{}\\input{other}"));
        Assert.Null(LatexCommandCompletion.Transform(text, new(1, text.Length), "section"));
        Assert.Null(LatexCommandCompletion.Transform(@"\section", new(0, 3), "section"));
    }
}
