using System.Text;

namespace Client.Review;

/// <summary>Bounded plain-text display only. No rule, edit or authorization logic.
/// Caps limit DOM work even if a future generic caller supplies a large preview.
/// Full owned bytes remain in the service, not in a truncated replacement.</summary>
public static class ReviewPresentation
{
    public const int MaxFiles=10, MaxEdits=20, MaxLines=50, SnippetBytes=512;
    public static string Bytes(ReadOnlySpan<byte> bytes)
    {
        int end=Math.Min(SnippetBytes,bytes.Length);
        while(end<bytes.Length&&(bytes[end]&0xc0)==0x80)end--;
        return Visible(Encoding.UTF8.GetString(bytes[..end]))+(end<bytes.Length?"\n… bounded excerpt; remaining bytes not displayed":"");
    }
    public static string Text(string text)
    {
        var result=new StringBuilder();int count=0;bool truncated=false;
        foreach(var rune in text.EnumerateRunes())
        {
            if(count+rune.Utf8SequenceLength>SnippetBytes){truncated=true;break;}
            result.Append(rune.ToString());count+=rune.Utf8SequenceLength;
        }
        return Visible(result.ToString())+(truncated?"… bounded excerpt":"");
    }
    private static string Visible(string text)=>text.Replace("\uFEFF","⟦BOM⟧").Replace("\r","⟦CR⟧").Replace("\n","⟦LF⟧\n");
    public static string Persistence(ReviewActionView action)=>action.UndoApplied
        ?action.Persisted?"Reviewed undo saved.":"Reviewed undo applied in memory; not yet saved."
        :action.Persisted?"Reviewed change saved.":"Reviewed change applied in memory; not yet saved.";
}
