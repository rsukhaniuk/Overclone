using System.Text;

namespace Client.Workspace.Transfer;

/// <summary>
/// Byte-preserving text utilities. Decoding is strict: only valid UTF-8 is
/// accepted as text; invalid bytes mean the file is binary. An optional UTF-8
/// BOM (EF BB BF) is detected and retained. Unedited text preserves exact bytes
/// (BOM, CRLF, mixed newlines); after an actual edit, text is serialized with LF
/// line endings and the file's original BOM presence is preserved.
/// </summary>
public static class TextContent
{
    // Strict UTF-8: no automatic BOM emission, throw on invalid byte sequences.
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };

    /// <summary>True if the bytes begin with a UTF-8 BOM.</summary>
    public static bool HasBom(byte[] bytes)
    {
        return bytes.Length >= 3 &&
               bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    }

    /// <summary>Strictly decode UTF-8 bytes to a string. Throws
    /// <see cref="DecoderFallbackException"/> on invalid bytes. The BOM, if
    /// present, is stripped from the returned string (callers track BOM
    /// presence separately via <see cref="HasBom"/>).</summary>
    public static string DecodeStrict(byte[] bytes)
    {
        var span = HasBom(bytes) ? bytes.AsSpan(3) : bytes.AsSpan();
        return StrictUtf8.GetString(span);
    }

    /// <summary>Try strict decode; returns false for invalid UTF-8 (file is binary).</summary>
    public static bool TryDecode(byte[] bytes, out string text, out bool hasBom)
    {
        hasBom = HasBom(bytes);
        var span = hasBom ? bytes.AsSpan(3) : bytes.AsSpan();
        try
        {
            text = StrictUtf8.GetString(span);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = "";
            return false;
        }
    }

    /// <summary>Encode edited text: LF line endings, with an optional UTF-8 BOM
    /// prepended to match the file's original BOM presence. CRLF/CR in the
    /// textarea string are normalized to LF.</summary>
    public static byte[] EncodeEdited(string text, bool withBom)
    {
        // Normalize CRLF/CR to LF. The textarea itself normalizes newlines for
        // display, but be defensive: an explicit \r or \r\n in the string
        // becomes a single \n.
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var body = StrictUtf8.GetBytes(normalized);
        if (!withBom) return body;
        var result = new byte[Bom.Length + body.Length];
        Bom.CopyTo(result, 0);
        Buffer.BlockCopy(body, 0, result, Bom.Length, body.Length);
        return result;
    }

    /// <summary>Count the strict edited LF+BOM encoding without allocating it.
    /// Shared by analysis and patch/project preflight.</summary>
    public static bool TryCountEditedBytes(string content, bool hasBom, out long length)
    {
        length = hasBom ? 3 : 0;
        var remaining = content.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (System.Text.Rune.DecodeFromUtf16(remaining, out var rune, out int consumed) != System.Buffers.OperationStatus.Done)
                return false;
            length += rune.Utf8SequenceLength;
            remaining = remaining[consumed..];
            if (rune.Value == '\r' && !remaining.IsEmpty && remaining[0] == '\n') remaining = remaining[1..];
        }
        return true;
    }

    /// <summary>Split text into lines for line-based diff. A trailing newline
    /// produces a final empty element so diff round trips faithfully.</summary>
    public static IReadOnlyList<string> SplitLines(string text)
    {
        var lines = new List<string>();
        if (text.Length == 0)
        {
            lines.Add("");
            return lines;
        }
        var sb = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                // Normalize CR/CRLF to LF for comparison of logical lines.
                lines.Add(sb.ToString());
                sb.Clear();
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                continue;
            }
            if (c == '\n')
            {
                lines.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        lines.Add(sb.ToString());
        return lines;
    }
}
