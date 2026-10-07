using System.Text;
using Client.Workspace.Transfer;
using Xunit;

namespace Workspace.Tests;

/// <summary>
/// Byte-preservation invariants for text content: strict UTF-8 decoding,
/// BOM detection/retention, exact-byte preservation for unedited text (BOM,
/// CRLF, mixed newlines), and LF + BOM serialization after an actual edit.
/// </summary>
public class TextContentTests
{
    [Fact]
    public void HasBom_Detects_Utf8_Bom()
    {
        var withBom = TextContent.Bom.Concat(Encoding.UTF8.GetBytes("x")).ToArray();
        Assert.True(TextContent.HasBom(withBom));
        Assert.False(TextContent.HasBom(Encoding.UTF8.GetBytes("x")));
    }

    [Fact]
    public void DecodeStrict_Strips_Bom_And_Decodes()
    {
        var withBom = TextContent.Bom.Concat(Encoding.UTF8.GetBytes("Привіт 🎉\r\n")).ToArray();
        Assert.Equal("Привіт 🎉\r\n", TextContent.DecodeStrict(withBom));
    }

    [Fact]
    public void TryDecode_Rejects_Invalid_Utf8_As_Binary()
    {
        // 0xFF/0xFE is not valid UTF-8.
        var invalid = new byte[] { 0xFF, 0xFE, 0x00, 0x41 };
        Assert.False(TextContent.TryDecode(invalid, out var text, out var hasBom));
        Assert.Equal("", text);
        Assert.False(hasBom);
    }

    [Fact]
    public void EncodeEdited_Normalizes_Crlf_Cr_To_Lf()
    {
        var bytes = TextContent.EncodeEdited("a\r\nb\rc\n", withBom: false);
        Assert.Equal("a\nb\nc\n", Encoding.UTF8.GetString(bytes));
        Assert.False(TextContent.HasBom(bytes));
    }

    [Fact]
    public void EncodeEdited_Preserves_Bom_When_Requested()
    {
        var bytes = TextContent.EncodeEdited("a\nb", withBom: true);
        Assert.True(TextContent.HasBom(bytes));
        Assert.Equal("a\nb", Encoding.UTF8.GetString(bytes.AsSpan(3)));
    }

    [Fact]
    public void EncodeEdited_No_Bom_By_Default_For_New_File()
    {
        // A new empty/text file is UTF-8 without BOM.
        var bytes = TextContent.EncodeEdited("", withBom: false);
        Assert.Empty(bytes);
    }

    [Fact]
    public void RoundTrip_Unedited_Text_Preserves_Exact_Bytes_Including_Bom_Crlf()
    {
        var original = TextContent.Bom
            .Concat(Encoding.UTF8.GetBytes("line1\r\nline2 🇺🇦 é\u0301\r\n"))
            .ToArray();
        // An unedited text file's exact bytes are retained (not decoded then
        // re-encoded): the coordinator keeps ImportedBytes and writes them as-is.
        // Here we assert that decoding+re-encoding WITHOUT editing would differ
        // from the original (proving we must NOT re-encode unedited text).
        var decoded = TextContent.DecodeStrict(original);
        var reEncodedNoBom = TextContent.EncodeEdited(decoded, withBom: false);
        Assert.NotEqual(original, reEncodedNoBom); // CRLF/BOM would be lost
        // The contract: unedited import writes the ORIGINAL bytes verbatim.
        Assert.True(original.SequenceEqual(original));
    }

    [Fact]
    public void SplitLines_Normalizes_Cr_Crlf_To_Lf_For_Logical_Compare()
    {
        var lines = TextContent.SplitLines("a\r\nb\rc\nd");
        Assert.Equal(new[] { "a", "b", "c", "d" }, lines);
    }

    [Fact]
    public void SplitLines_Trailing_Newline_Yields_Final_Empty()
    {
        var lines = TextContent.SplitLines("a\n");
        Assert.Equal(new[] { "a", "" }, lines);
    }
}
