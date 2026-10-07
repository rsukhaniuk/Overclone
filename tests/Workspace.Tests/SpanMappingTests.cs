using System.Collections.Immutable;
using System.Text;
using Client.Analysis;
using Client.Workspace.Domain;
using Xunit;

namespace Workspace.Tests;

/// <summary>
/// Pure C# two-stage span mapping (M2a-2a). Uses the framework's UTF-8 decoder
/// and independent CRLF counting as oracles — NOT the SpanMapping helper compared
/// to itself. Covers 1/2/3/4-byte scalars, emoji surrogate pairs, combining
/// marks, BOM endpoints, CRLF, lone CR, mixed newlines, zero-length/end-of-file,
/// wrong file, reversed/out-of-bounds, invalid UTF boundaries, a span-in-CRLF
/// NonSelectable, and the BOM+CRLF+Ukrainian+emoji unmatched-brace case with
/// hardcoded expected DOM UTF-16 start/end and normalized substring. Verifies the
/// source bytes are unchanged.
/// </summary>
public class SpanMappingTests
{
    private static readonly FileId F = FileId.New();

    // ---- independent oracles (framework decoder + independent counting) ----

    private static string OracleSourceText(byte[] bytes, int bomLen)
        => Encoding.UTF8.GetString(bytes, bomLen, bytes.Length - bomLen);

    private static int OracleSourceOffset(byte[] bytes, int bomLen, int byteOffset)
        => Encoding.UTF8.GetString(bytes, bomLen, byteOffset - bomLen).Length;

    private static int OracleCrlfPairsBefore(string text, int o)
    {
        int c = 0;
        for (int i = 0; i < o && i + 1 < text.Length; i++)
            if (text[i] == '\r' && text[i + 1] == '\n') c++;
        return c;
    }

    private static string OracleNormalize(string s)
        => s.Replace("\r\n", "\n").Replace('\r', '\n');

    private static SpanMappingResult MapBytes(byte[] bytes, long start, long end)
        => SpanMapping.Map(F, start, end, ImmutableArray.Create(bytes));

    // ---- 1/2/3/4-byte scalars ----

    [Fact]
    public void One_Byte_Scalar_Maps_Exactly()
    {
        var bytes = B("ab{c");
        var r = MapBytes(bytes, 2, 3);
        var ex = Assert.IsType<SpanMappingResult.Exact>(r);
        AssertExpected(bytes, 0, 2, 3, ex);
        Assert.Equal("{", ex.ExpectedNormalizedSubstring);
    }

    [Fact]
    public void Two_Byte_Scalar_Maps_Exactly()
    {
        var bytes = B("é{x");
        var r = MapBytes(bytes, 2, 3);
        var ex = Assert.IsType<SpanMappingResult.Exact>(r);
        // é is one UTF-16 char before {.
        AssertExpected(bytes, 0, 1, 2, ex);
        Assert.Equal("{", ex.ExpectedNormalizedSubstring);
    }

    [Fact]
    public void Three_Byte_Scalar_Maps_Exactly()
    {
        var bytes = B("中{x");
        var r = MapBytes(bytes, 3, 4);
        var ex = Assert.IsType<SpanMappingResult.Exact>(r);
        AssertExpected(bytes, 0, 1, 2, ex);
    }

    [Fact]
    public void Four_Byte_Scalar_And_Span_Over_Emoji_Maps_Exactly()
    {
        var bytes = B("🙂{");
        // span over the emoji scalar [0,4)
        var rOver = MapBytes(bytes, 0, 4);
        var exOver = Assert.IsType<SpanMappingResult.Exact>(rOver);
        AssertExpected(bytes, 0, 0, 2, exOver);
        Assert.Equal("🙂", exOver.ExpectedNormalizedSubstring);
        // the { after the emoji [4,5)
        var rBrace = MapBytes(bytes, 4, 5);
        var exBrace = Assert.IsType<SpanMappingResult.Exact>(rBrace);
        AssertExpected(bytes, 0, 2, 3, exBrace);
    }

    [Fact]
    public void Emoji_Surrogate_Pairs_Maps_Exactly()
    {
        // 🇺🇦 = U+1F1FA U+1F1E6 (two supplementary scalars, 4 UTF-16 units).
        var bytes = B("🇺🇦{");
        var r = MapBytes(bytes, 8, 9);
        var ex = Assert.IsType<SpanMappingResult.Exact>(r);
        AssertExpected(bytes, 0, 4, 5, ex);
    }

    [Fact]
    public void Combining_Mark_Maps_Exactly()
    {
        var bytes = B("e\u0301{");
        var r = MapBytes(bytes, 3, 4);
        var ex = Assert.IsType<SpanMappingResult.Exact>(r);
        // e (1 char) + combining acute (1 char) before { => source offset 2.
        AssertExpected(bytes, 0, 2, 3, ex);
    }

    // ---- BOM endpoints ----

    [Fact]
    public void Bom_Endpoints_One_And_Two_Are_Invalid()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a' };
        Assert.IsType<SpanMappingResult.InvalidSpan>(MapBytes(bytes, 1, 1));
        Assert.IsType<SpanMappingResult.InvalidSpan>(MapBytes(bytes, 2, 2));
    }

    [Fact]
    public void Bom_Covering_Span_Is_NonSelectable()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a' };
        // [0,3) covers the stripped BOM -> NonSelectable; [0,0) zero-length at BOM start too.
        Assert.IsType<SpanMappingResult.NonSelectable>(MapBytes(bytes, 0, 3));
        Assert.IsType<SpanMappingResult.NonSelectable>(MapBytes(bytes, 0, 0));
    }

    [Fact]
    public void After_Bom_First_Char_Maps_Exactly()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', (byte)'{' };
        var r = MapBytes(bytes, 4, 5);
        var ex = Assert.IsType<SpanMappingResult.Exact>(r);
        AssertExpected(bytes, 3, 1, 2, ex);
        Assert.Equal("{", ex.ExpectedNormalizedSubstring);
        // zero-length at byte 3 (after BOM) -> caret at source 0
        var r0 = MapBytes(bytes, 3, 3);
        var ex0 = Assert.IsType<SpanMappingResult.Exact>(r0);
        Assert.Equal(0, ex0.SourceStart);
        Assert.Equal(0, ex0.SourceEnd);
    }

    // ---- CRLF / lone CR / mixed ----

    [Fact]
    public void Crlf_Span_Maps_With_Normalized_Substring()
    {
        var bytes = B("a\r\nb");
        var r = MapBytes(bytes, 1, 3); // \r\n
        var ex = Assert.IsType<SpanMappingResult.Exact>(r);
        AssertExpected(bytes, 0, 1, 3, ex);
        Assert.Equal("\n", ex.ExpectedNormalizedSubstring); // \r\n -> \n
    }

    [Fact]
    public void Boundary_Between_Cr_And_Lf_Is_NonSelectable()
    {
        var bytes = B("a\r\nb");
        // endByte 2 lands at the \n (boundary between \r and \n) -> NonSelectable
        Assert.IsType<SpanMappingResult.NonSelectable>(MapBytes(bytes, 1, 2));
    }

    [Fact]
    public void Lone_Cr_Maps_With_Normalized_Substring()
    {
        var bytes = B("a\rb");
        var r = MapBytes(bytes, 1, 2); // \r
        var ex = Assert.IsType<SpanMappingResult.Exact>(r);
        AssertExpected(bytes, 0, 1, 2, ex);
        Assert.Equal("\n", ex.ExpectedNormalizedSubstring); // lone \r -> \n in place
    }

    [Fact]
    public void Mixed_Newlines_Map_Individually()
    {
        var bytes = B("a\r\nb\rc");
        // CRLF at bytes 1-2 (source 1-3 -> textarea 1-2), lone CR at bytes 4 (source 4 -> textarea 3)
        var rCrlf = MapBytes(bytes, 1, 3);
        var exCrlf = Assert.IsType<SpanMappingResult.Exact>(rCrlf);
        AssertExpected(bytes, 0, 1, 3, exCrlf);

        var rCr = MapBytes(bytes, 4, 5);
        var exCr = Assert.IsType<SpanMappingResult.Exact>(rCr);
        AssertExpected(bytes, 0, 4, 5, exCr);
        Assert.Equal("\n", exCr.ExpectedNormalizedSubstring);
    }

    // ---- zero-length / end-of-file ----

    [Fact]
    public void Zero_Length_And_End_Of_File_Map_To_Caret()
    {
        var bytes = B("ab");
        var rEnd = MapBytes(bytes, 2, 2);
        var exEnd = Assert.IsType<SpanMappingResult.Exact>(rEnd);
        Assert.Equal(2, exEnd.SourceStart);
        Assert.Equal(2, exEnd.SourceEnd);
        Assert.Equal("", exEnd.ExpectedNormalizedSubstring);

        var rStart = MapBytes(bytes, 0, 0);
        var exStart = Assert.IsType<SpanMappingResult.Exact>(rStart);
        Assert.Equal(0, exStart.SourceStart);
        Assert.Equal(0, exStart.SourceEnd);
    }

    // ---- wrong file / reversed / out-of-bounds / invalid UTF boundary ----

    [Fact]
    public void Result_Echoes_FileId()
    {
        var bytes = B("a{");
        var fid = FileId.New();
        var r = SpanMapping.Map(fid, 1, 2, ImmutableArray.Create(bytes));
        Assert.Equal(fid, ((SpanMappingResult.Exact)r).FileId);
    }

    [Fact]
    public void Reversed_Span_Is_Invalid()
        => Assert.IsType<SpanMappingResult.InvalidSpan>(MapBytes(B("ab"), 2, 1));

    [Fact]
    public void Out_Of_Bounds_Span_Is_Invalid()
        => Assert.IsType<SpanMappingResult.InvalidSpan>(MapBytes(B("ab"), 0, 100));

    [Fact]
    public void Endpoint_Inside_Multi_Byte_Scalar_Is_Invalid()
    {
        var bytes = B("é{"); // é = C3 A9 (2 bytes); byte 1 is a continuation byte
        Assert.IsType<SpanMappingResult.InvalidSpan>(MapBytes(bytes, 1, 1));
    }

    [Fact]
    public void Source_Bytes_Are_Unchanged_By_Mapping()
    {
        var bytes = B("a\r\n🇺🇦{");
        var saved = bytes.ToArray();
        _ = MapBytes(bytes, bytes.Length - 1, bytes.Length);
        Assert.Equal(saved, bytes);
    }

    // ---- the BOM + CRLF + Ukrainian + emoji unmatched-brace case ----

    [Fact]
    public void Bom_Crlf_Ukrainian_Emoji_Unmatched_Brace_Maps()
    {
        // BOM + "Привіт" + CRLF + "🇺🇦" + "{"  (the { is AFTER the first CRLF).
        var bytes = new byte[]
        {
            0xEF, 0xBB, 0xBF,                       // BOM
            0xD0, 0x9F, 0xD1, 0x80, 0xD0, 0xB8, 0xD0, 0xB2, 0xD1, 0x96, 0xD1, 0x82, // "Привіт" (12 bytes)
            (byte)'\r', (byte)'\n',                // CRLF
            0xF0, 0x9F, 0x87, 0xBA, 0xF0, 0x9F, 0x87, 0xA6, // "🇺🇦" (8 bytes)
            (byte)'{',                              // unmatched brace
        };
        int bomLen = 3;
        int braceByte = bytes.Length - 1;

        var r = MapBytes(bytes, braceByte, braceByte + 1);
        var ex = Assert.IsType<SpanMappingResult.Exact>(r);

        // Independent oracle.
        var src = OracleSourceText(bytes, bomLen);
        int oss = OracleSourceOffset(bytes, bomLen, braceByte);
        int ose = OracleSourceOffset(bytes, bomLen, braceByte + 1);
        int ots = oss - OracleCrlfPairsBefore(src, oss);
        int ote = ose - OracleCrlfPairsBefore(src, ose);
        string oexpected = OracleNormalize(src.Substring(oss, ose - oss));

        Assert.Equal((oss, ose), (ex.SourceStart, ex.SourceEnd));
        Assert.Equal((ots, ote), (ex.TextareaStart, ex.TextareaEnd));
        Assert.Equal(oexpected, ex.ExpectedNormalizedSubstring);

        // Hardcoded reference values (DOM UTF-16 start/end + normalized substring).
        Assert.Equal(12, ex.SourceStart);
        Assert.Equal(13, ex.SourceEnd);
        Assert.Equal(11, ex.TextareaStart);
        Assert.Equal(12, ex.TextareaEnd);
        Assert.Equal("{", ex.ExpectedNormalizedSubstring);

        // Source bytes unchanged.
        var saved = bytes.ToArray();
        _ = MapBytes(bytes, braceByte, braceByte + 1);
        Assert.Equal(saved, bytes);
    }

    // ---- Area 4 reproduducers: strict UTF-8 / malformed bytes / file-bound / BOM policy ----

    private static CapturedFile File(byte[] bytes, bool hasBom = false, long? byteLength = null)
    {
        var b = bytes;
        return new CapturedFile(F, "f.tex", hasBom, byteLength ?? b.LongLength,
            Client.Workspace.Domain.SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(b)),
            ImmutableArray.Create(b));
    }

    [Fact]
    public void Invalid_Utf8_Bytes_Return_InvalidSpan_Not_Throw()
    {
        // A lone 0xFF is not valid UTF-8; must return InvalidSpan, not throw DecoderFallbackException.
        var r = SpanMapping.Map(F, 0, 1, ImmutableArray.Create((byte)0xFF));
        var inv = Assert.IsType<SpanMappingResult.InvalidSpan>(r);
        Assert.Equal("bytes are not valid UTF-8", inv.Reason);
    }

    [Theory]
    [InlineData(new byte[] { 0xC3 })]               // truncated 2-byte sequence
    [InlineData(new byte[] { 0xC0, 0xAF })]         // overlong encoding of U+002F
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })]   // surrogate code point U+D800
    [InlineData(new byte[] { 0xF4, 0x90, 0x80, 0x80 })] // > U+10FFFF (U+110000)
    public void Malformed_Byte_Sequences_Return_InvalidSpan(byte[] bytes)
        => Assert.IsType<SpanMappingResult.InvalidSpan>(SpanMapping.Map(F, 0, bytes.Length, ImmutableArray.Create(bytes)));

    [Fact]
    public void Default_Bytes_Return_InvalidSpan()
    {
        ImmutableArray<byte> def = default;
        Assert.IsType<SpanMappingResult.InvalidSpan>(SpanMapping.Map(F, 0, 0, def));
    }

    [Fact]
    public void Oversize_Bytes_Return_InvalidSpan()
    {
        var big = new byte[SpanMapping.MaxFileBytes + 1];
        Assert.IsType<SpanMappingResult.InvalidSpan>(SpanMapping.Map(F, 0, 0, ImmutableArray.Create(big)));
    }

    [Fact]
    public void File_Bound_Wrong_FileId_Returns_InvalidSpan()
    {
        // Pass file B's bytes with a diagnostic reported against file A: InvalidSpan, NOT a silent echo of the supplied id.
        var fileA = new CapturedFile(F, "a.tex", false, 0,
            Client.Workspace.Domain.SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(Array.Empty<byte>())),
            ImmutableArray<byte>.Empty);
        var r = SpanMapping.Map(fileA, diagnosticFileId: "deadbeef", 0, 0);
        var inv = Assert.IsType<SpanMappingResult.InvalidSpan>(r);
        Assert.Equal(F, inv.FileId);
        Assert.Equal("diagnosticFileId does not match the expected file", inv.Reason);
    }

    [Fact]
    public void File_Bound_Matching_Id_Maps_Exact()
    {
        var bytes = B("a{");
        var f = File(bytes);
        var r = SpanMapping.Map(f, f.FileId.ToString(), 1, 2);
        var ex = Assert.IsType<SpanMappingResult.Exact>(r);
        Assert.Equal("{", ex.ExpectedNormalizedSubstring);
    }

    [Fact]
    public void File_Bound_ByteLength_Mismatch_Returns_InvalidSpan()
    {
        var bytes = B("a{");
        var forged = new CapturedFile(F, "a.tex", false, ByteLength: 99,
            Client.Workspace.Domain.SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes)),
            ImmutableArray.Create(bytes));
        var r = SpanMapping.Map(forged, forged.FileId.ToString(), 1, 2);
        Assert.IsType<SpanMappingResult.InvalidSpan>(r);
    }

    [Fact]
    public void File_Bound_Bom_Mismatch_Returns_InvalidSpan()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a' };
        // Claim HasBom=false but the bytes start with a BOM.
        var forged = new CapturedFile(F, "a.tex", HasBom: false, ByteLength: bytes.LongLength,
            Client.Workspace.Domain.SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes)),
            ImmutableArray.Create(bytes));
        var r = SpanMapping.Map(forged, forged.FileId.ToString(), 3, 4);
        Assert.IsType<SpanMappingResult.InvalidSpan>(r);
    }

    [Fact]
    public void Bom_Zero_Length_At_Byte0_Is_NonSelectable()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a' };
        // byte 0 (BOM start), zero-length -> NonSelectable (covers stripped BOM).
        Assert.IsType<SpanMappingResult.NonSelectable>(MapBytes(bytes, 0, 0));
        // [0,3) covering the whole BOM -> NonSelectable.
        Assert.IsType<SpanMappingResult.NonSelectable>(MapBytes(bytes, 0, 3));
    }

    [Fact]
    public void Bom_Byte1_And_Byte2_Are_Invalid()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a' };
        Assert.IsType<SpanMappingResult.InvalidSpan>(MapBytes(bytes, 1, 1));
        Assert.IsType<SpanMappingResult.InvalidSpan>(MapBytes(bytes, 2, 2));
    }

    [Fact]
    public void Bom_Byte3_Zero_Length_Maps_To_Source0()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a' };
        // byte 3 (after BOM), zero-length -> Exact caret at source 0.
        var r = MapBytes(bytes, 3, 3);
        var ex = Assert.IsType<SpanMappingResult.Exact>(r);
        Assert.Equal(0, ex.SourceStart);
        Assert.Equal(0, ex.SourceEnd);
    }

    [Fact]
    public void Bom_End_Zero_Length_Maps_To_End()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a' };
        var r = MapBytes(bytes, bytes.Length, bytes.Length);
        var ex = Assert.IsType<SpanMappingResult.Exact>(r);
        Assert.Equal(1, ex.SourceStart);
        Assert.Equal(1, ex.SourceEnd);
    }

    [Fact]
    public void Negative_And_Reversed_Endpoints_Are_Invalid()
    {
        var bytes = B("ab");
        Assert.IsType<SpanMappingResult.InvalidSpan>(MapBytes(bytes, -1, 0));
        Assert.IsType<SpanMappingResult.InvalidSpan>(MapBytes(bytes, 1, 0));
    }

    [Fact]
    public void Empty_Bytes_Zero_Length_Maps_To_Caret0()
    {
        var bytes = Array.Empty<byte>();
        var r = MapBytes(bytes, 0, 0);
        var ex = Assert.IsType<SpanMappingResult.Exact>(r);
        Assert.Equal(0, ex.SourceStart);
    }

    // ---- helper: assert an Exact result against the independent oracle ----

    private static void AssertExpected(byte[] bytes, int bomLen, int srcStart, int srcEnd, SpanMappingResult.Exact ex)
    {
        var src = OracleSourceText(bytes, bomLen);
        int oss = OracleSourceOffset(bytes, bomLen, (int)ex.StartByte);
        int ose = OracleSourceOffset(bytes, bomLen, (int)ex.EndByte);
        Assert.Equal((oss, ose), (ex.SourceStart, ex.SourceEnd));
        Assert.Equal(oss - OracleCrlfPairsBefore(src, oss), ex.TextareaStart);
        Assert.Equal(ose - OracleCrlfPairsBefore(src, ose), ex.TextareaEnd);
        Assert.Equal(OracleNormalize(src.Substring(oss, ose - oss)), ex.ExpectedNormalizedSubstring);
    }

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
}
