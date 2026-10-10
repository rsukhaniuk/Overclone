using Client.Review;
using Client.Workspace.Domain;
using Client.Workspace.Patching;
using Xunit;
using static Workspace.Tests.TextPatchPreparationTests;

namespace Workspace.Tests;

public class ReviewPresentationTests
{
    [Fact]
    public void Byte_excerpts_are_scalar_safe_bounded_and_show_exact_newline_BOM_markers()
    {
        byte[] bytes=[239,187,191,..B("Ж🙂\r\nX\rY\n"+new string('Ж',1000))];
        var display=ReviewPresentation.Bytes(bytes);
        Assert.Contains("⟦BOM⟧Ж🙂⟦CR⟧⟦LF⟧",display);Assert.Contains("X⟦CR⟧Y⟦LF⟧",display);
        Assert.Contains("bounded excerpt",display);Assert.DoesNotContain("\ufffd",display);Assert.True(display.Length<1000);
        Assert.Equal("10 mm",ReviewPresentation.Bytes(B("10 mm")));
    }
    [Fact]
    public void Line_display_does_not_encode_or_return_an_unbounded_line_or_treat_markup_as_HTML()
    {
        var input="<img src=x onerror=alert(1)>🙂"+new string('x',1000000);
        var display=ReviewPresentation.Text(input);Assert.StartsWith("<img src=x",display);Assert.Contains("🙂",display);
        Assert.Contains("bounded excerpt",display);Assert.True(display.Length<600);
    }
    [Theory]
    [InlineData(false,false,"change applied in memory; not yet saved")]
    [InlineData(false,true,"change saved")]
    [InlineData(true,false,"undo applied in memory; not yet saved")]
    [InlineData(true,true,"undo saved")]
    public void Action_persistence_text_is_distinct_from_current_workspace_dirtiness(bool undone,bool persisted,string text)
    {
        var id=FileId.New();var before=B("abc");var identity=new TextPatchIdentity(ProjectId.New(),Guid.NewGuid(),0,null);
        var batch=PreparedTextBatch.Prepare(identity,[new(id,"a.tex",Hash(before),3,[new(0,1,B("a"),B("X"))])],
            [new(id,"a.tex","text",3,false,before)]).Batch!;
        var action=new ReviewActionView(Guid.NewGuid(),identity.SessionId,undone,persisted,!undone,batch);
        Assert.Contains(text,ReviewPresentation.Persistence(action));
        Assert.DoesNotContain("workspace",ReviewPresentation.Persistence(action),StringComparison.OrdinalIgnoreCase);
    }
}
