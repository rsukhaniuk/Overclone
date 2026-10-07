using System.Collections.Immutable;
using System.Text;
using Client.Workspace.Domain;
using Client.Workspace.Transfer;

namespace Client.Analysis;

// Pure C# two-stage span mapping (M2a-2a). NO DOM adapter here — the later UI
// calls this and then a tiny JS selectInEditor. Authoritative source is the
// exact snapshot BYTES; SourceText is derived by strict UTF-8 decode (BOM
// stripped, CRLF/lone-CR preserved exactly), and TextareaText is the DOM
// normalization of SourceText (\r\n -> \n length -1, lone \r -> \n in place).
//
// Spans are half-open [startByte, endByte) UTF-8 byte offsets into the
// BOM-inclusive exact bytes, on Unicode-scalar boundaries. A boundary inside
// a UTF-8 continuation byte or a UTF-16 surrogate pair is InvalidSpan. A
// boundary between CR and LF, or a span covering a stripped BOM, is
// NonSelectable (expanded display only). Neither the snapshot bytes nor any
// editor text is modified.
//
// Malformed byte sequences (truncated, overlong, surrogate code-point
// encodings, > U+10FFFF) are rejected as InvalidSpan via the .NET strict
// UTF-8 decoder — they never throw DecoderFallbackException to the caller.
//
// This bounded first slice uses simple correct linear scans over an
// at-most-8-MiB file (a small constant number of scans per selected span); it
// does NOT allocate an int per byte for every file or preprocess 64 MiB into a
// dense table. Mapping is invoked lazily for a selected diagnostic in the
// later UI; no performance guarantees without measurements.

/// <summary>Typed span-mapping result distinguishing an Exact textarea
/// selection from a NonSelectable (expanded display only) span and an
/// InvalidSpan.</summary>
public abstract record SpanMappingResult
{
    private SpanMappingResult() { }

    /// <summary>An exact, selectable textarea range. The normalized DOM
    /// selected substring MUST equal <see cref="ExpectedNormalizedSubstring"/>
    /// (== NormalizeNewlines(SourceText[sourceStart..sourceEnd))).</summary>
    public sealed record Exact(
        FileId FileId,
        long StartByte,
        long EndByte,
        int SourceStart,
        int SourceEnd,
        int TextareaStart,
        int TextareaEnd,
        string ExpectedNormalizedSubstring,
        int Line,
        int Column) : SpanMappingResult;

    /// <summary>The span cannot be selected in the textarea (a boundary sits
    /// between CR and LF, or the span covers a stripped BOM). Show an expanded
    /// display (file, line/column, exact byte span) and do NOT call
    /// setSelectionRange.</summary>
    public sealed record NonSelectable(
        FileId FileId,
        long StartByte,
        long EndByte,
        int Line,
        int Column,
        string Reason) : SpanMappingResult;

    /// <summary>The byte span is not a valid scalar-boundary span (out of
    /// range, reversed, an endpoint inside a UTF-8 continuation byte, malformed
    /// UTF-8, a BOM/byte-length mismatch, or a wrong diagnostic file id).</summary>
    public sealed record InvalidSpan(
        FileId FileId,
        long StartByte,
        long EndByte,
        string Reason) : SpanMappingResult;
}

public static class SpanMapping
{
    /// <summary>Upper bound on the byte length of a single mapped file. Matches
    /// <see cref="AnalysisLimits.DMaxBytesPerFile"/> (8 MiB). Larger byte arrays
    /// are rejected as InvalidSpan before any decoding.</summary>
    public const int MaxFileBytes = 8 * 1024 * 1024;

    /// <summary>File-bound entry the later UI MUST use: map a diagnostic byte
    /// span for <paramref name="expectedFile"/> after verifying the diagnostic's
    /// file id actually belongs to that file. A mismatch (a diagnostic reported
    /// against file A presented with file B) is InvalidSpan — the result never
    /// silently maps the wrong file's bytes. Validates the captured bytes are
    /// initialized, within the 8 MiB bound, byte-length/BOM-consistent, and
    /// strict-valid UTF-8 before deriving any location/string. Invalid bytes
    /// return InvalidSpan (never throw).</summary>
    public static SpanMappingResult Map(
        CapturedFile expectedFile,
        string diagnosticFileId,
        long startByte,
        long endByte)
    {
        if (expectedFile.FileId.ToString() != diagnosticFileId)
            return new SpanMappingResult.InvalidSpan(
                expectedFile.FileId, startByte, endByte,
                "diagnosticFileId does not match the expected file");
        if (expectedFile.Bytes.IsDefault)
            return new SpanMappingResult.InvalidSpan(
                expectedFile.FileId, startByte, endByte, "bytes are not initialized");
        if (expectedFile.ByteLength != expectedFile.Bytes.Length)
            return new SpanMappingResult.InvalidSpan(
                expectedFile.FileId, startByte, endByte,
                "declared byte length does not match the bytes");
        return MapCore(expectedFile.FileId, startByte, endByte, expectedFile.Bytes, expectedFile.HasBom);
    }

    /// <summary>Low-level byte mapper: map a half-open BOM-inclusive byte span
    /// to a textarea selection with the file's BOM derived from the bytes
    /// themselves. The later UI should use the file-bound
    /// <see cref="Map(CapturedFile, string, long, long)"/> overload; this entry
    /// is kept public only for direct unit testing of the byte->source->textarea
    /// mapping. Performs Map A (bytes -> source UTF-16, BOM stripped, CRLF
    /// preserved) then Map B (source UTF-16 -> textarea UTF-16 under DOM
    /// normalization). Returns Exact / NonSelectable / InvalidSpan.</summary>
    public static SpanMappingResult Map(
        FileId file,
        long startByte,
        long endByte,
        ImmutableArray<byte> bytes)
    {
        if (bytes.IsDefault)
            return new SpanMappingResult.InvalidSpan(file, startByte, endByte, "bytes are not initialized");
        return MapCore(file, startByte, endByte, bytes, HasBom(bytes));
    }

    // ---- core mapper (shared by both public entries) ----

    private static SpanMappingResult MapCore(
        FileId file, long startByte, long endByte, ImmutableArray<byte> bytes, bool hasBom)
    {
        if (bytes.IsDefault)
            return new SpanMappingResult.InvalidSpan(file, startByte, endByte, "bytes are not initialized");
        int len = bytes.Length;
        if (len > MaxFileBytes)
            return new SpanMappingResult.InvalidSpan(file, startByte, endByte, "file exceeds the 8 MiB mapping limit");
        if (startByte < 0 || endByte < 0 || startByte > endByte || endByte > len)
            return new SpanMappingResult.InvalidSpan(file, startByte, endByte, "span out of range or reversed");

        // Strict UTF-8 validation via the .NET strict decoder: rejects truncated,
        // overlong, surrogate-code-point and > U+10FFFF sequences. Never throws.
        if (!TextContent.TryDecode(ToByteArray(bytes), out var sourceText, out var decodedBom))
            return new SpanMappingResult.InvalidSpan(file, startByte, endByte, "bytes are not valid UTF-8");
        if (decodedBom != hasBom)
            return new SpanMappingResult.InvalidSpan(file, startByte, endByte, "declared BOM does not match the bytes");

        int bomLen = hasBom ? TextContent.Bom.Length : 0;

        // Scalar-boundary validation. In valid UTF-8 a non-continuation byte is a
        // scalar start; endpoints 1/2 inside a BOM are continuation-byte-shaped.
        if (!IsScalarBoundary(bytes, (int)startByte) || !IsScalarBoundary(bytes, (int)endByte))
            return new SpanMappingResult.InvalidSpan(file, startByte, endByte, "endpoint is not a UTF-8 scalar boundary");

        var (line, column) = LineColumn(bytes, (int)startByte);

        // BOM policy: the BOM is stripped from the textarea, so a span anchored in
        // the BOM region (startByte < bomLen) cannot map to a clean textarea
        // selection. This includes a zero-length caret at byte 0 (the BOM start):
        // it is NonSelectable. A zero-length caret at byte bomLen (the first real
        // byte) maps to source 0 exactly.
        if (bomLen > 0 && startByte < bomLen)
            return new SpanMappingResult.NonSelectable(file, startByte, endByte, line, column, "span covers a stripped BOM");

        // Map A: bytes -> source UTF-16 (BOM stripped).
        if (!TryMapBytesToSource(bytes, bomLen, (int)startByte, (int)endByte, out int sourceStart, out int sourceEnd))
            return new SpanMappingResult.InvalidSpan(file, startByte, endByte, "bytes are not valid UTF-8");

        // A UTF-8 scalar boundary never lands inside a UTF-16 surrogate pair;
        // defend defensively regardless.
        if (IsInsideSurrogatePair(sourceText, sourceStart) || IsInsideSurrogatePair(sourceText, sourceEnd))
            return new SpanMappingResult.InvalidSpan(file, startByte, endByte, "endpoint is inside a UTF-16 surrogate pair");

        // Map B: source UTF-16 -> textarea UTF-16 (DOM \r\n -> \n, lone \r -> \n in place).
        // A boundary between CR and LF is NonSelectable.
        if (IsBetweenCrLf(sourceText, sourceStart) || IsBetweenCrLf(sourceText, sourceEnd))
            return new SpanMappingResult.NonSelectable(file, startByte, endByte, line, column, "boundary is between CR and LF");

        int textareaStart = sourceStart - CountCrlfPairsBefore(sourceText, sourceStart);
        int textareaEnd = sourceEnd - CountCrlfPairsBefore(sourceText, sourceEnd);
        string expected = NormalizeNewlines(sourceText.Substring(sourceStart, sourceEnd - sourceStart));

        return new SpanMappingResult.Exact(
            file, startByte, endByte,
            sourceStart, sourceEnd,
            textareaStart, textareaEnd,
            expected, line, column);
    }

    // ---- Map A helpers ----

    private static bool HasBom(ImmutableArray<byte> bytes)
        => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    /// <summary>True iff offset is a Unicode-scalar boundary in valid UTF-8
    /// bytes (mirrors str::is_char_boundary: the byte AT offset must not be a
    /// continuation byte). 0 and len are boundaries; out-of-range is not.</summary>
    private static bool IsScalarBoundary(ImmutableArray<byte> bytes, int offset)
    {
        if (offset == 0 || offset == bytes.Length) return true;
        if (offset < 0 || offset > bytes.Length) return false;
        return (bytes[offset] & 0xC0) != 0x80;
    }

    /// <summary>Scan UTF-8 bytes from bomLen to endByte, counting UTF-16 code
    /// units per scalar (1 for BMP, 2 for supplementary). Records the UTF-16
    /// offset at startByte and endByte. Returns false on invalid UTF-8.</summary>
    private static bool TryMapBytesToSource(
        ImmutableArray<byte> bytes, int bomLen, int startByte, int endByte,
        out int sourceStart, out int sourceEnd)
    {
        sourceStart = -1;
        sourceEnd = 0;
        int bytePos = bomLen;
        int utf16 = 0;

        while (bytePos < endByte)
        {
            // Record the UTF-16 offset at the start boundary before consuming
            // the scalar that begins there.
            if (bytePos == startByte)
                sourceStart = utf16;

            if (bytePos >= bytes.Length)
                return false; // truncated scalar
            byte b = bytes[bytePos];
            int scalarLen;
            uint scalar;
            if (b < 0x80) { scalarLen = 1; scalar = b; }
            else if ((b & 0xE0) == 0xC0) { scalarLen = 2; scalar = (uint)(b & 0x1F); }
            else if ((b & 0xF0) == 0xE0) { scalarLen = 3; scalar = (uint)(b & 0x0F); }
            else if ((b & 0xF8) == 0xF0) { scalarLen = 4; scalar = (uint)(b & 0x07); }
            else return false;

            if (bytePos + scalarLen > bytes.Length)
                return false;
            for (int k = 1; k < scalarLen; k++)
            {
                byte cb = bytes[bytePos + k];
                if ((cb & 0xC0) != 0x80) return false;
                scalar = (scalar << 6) | (uint)(cb & 0x3F);
            }
            int utf16Units = scalar <= 0xFFFF ? 1 : 2;
            utf16 += utf16Units;
            bytePos += scalarLen;
        }

        // After the loop bytePos == endByte and utf16 is the UTF-16 offset at
        // endByte. A scalar boundary is always reached as bytePos (already
        // validated by IsScalarBoundary); if startByte was never hit as a
        // scalar start the input was not a scalar boundary.
        if (startByte == endByte)
            sourceStart = utf16; // zero-length span -> caret at this offset
        else if (sourceStart < 0)
            return false; // startByte was not a scalar boundary
        sourceEnd = utf16;
        return true;
    }

    // ---- Map B helpers ----

    /// <summary>A boundary at offset o is between CR and LF iff the char
    /// before o is CR and the char at o is LF.</summary>
    private static bool IsBetweenCrLf(string text, int o)
        => o > 0 && o < text.Length && text[o - 1] == '\r' && text[o] == '\n';

    /// <summary>Count CRLF pairs (\r immediately followed by \n) whose \r is
    /// before offset o. Lone \r does NOT shift (replaced in place).</summary>
    private static int CountCrlfPairsBefore(string text, int o)
    {
        int count = 0;
        for (int i = 0; i < o && i + 1 < text.Length; i++)
            if (text[i] == '\r' && text[i + 1] == '\n') count++;
        return count;
    }

    private static bool IsInsideSurrogatePair(string text, int o)
        => o > 0 && o < text.Length
            && char.IsLowSurrogate(text[o]) && char.IsHighSurrogate(text[o - 1]);

    /// <summary>DOM newline normalization: \r\n -> \n (length -1), lone \r -> \n
    /// (in place, length unchanged). Matches TextContent.EncodeEdited.</summary>
    public static string NormalizeNewlines(string s)
        => s.Replace("\r\n", "\n").Replace('\r', '\n');

    // ---- display line/column (over the exact BOM-inclusive bytes) ----

    /// <summary>1-based line and 0-based byte column from line start. A CRLF
    /// counts as ONE completed newline only once the whole \r\n pair precedes
    /// offset; a lone \r is a newline at the \r. Display-only.</summary>
    private static (int Line, int Column) LineColumn(ImmutableArray<byte> bytes, int offset)
    {
        int line = 1;
        int lineStart = 0;
        int i = 0;
        while (i < offset)
        {
            byte b = bytes[i];
            if (b == '\n') { line++; lineStart = i + 1; i++; }
            else if (b == '\r')
            {
                if (i + 1 < bytes.Length && bytes[i + 1] == '\n') i++; // defer to \n
                else { line++; lineStart = i + 1; i++; }
            }
            else i++;
        }
        return (line, offset - lineStart);
    }

    private static byte[] ToByteArray(ImmutableArray<byte> bytes)
    {
        var a = new byte[bytes.Length];
        bytes.CopyTo(a);
        return a;
    }
}
