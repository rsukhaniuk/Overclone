using System.Text;
using System.Text.Json;
using Client.Compilation.Native;
using ContentHash = Client.Workspace.Domain.SHA256;

namespace Client.Compilation.Bibliography;

/// <summary>Owned optional evidence from the exact successful native compile.
/// Its lifetime is the accepted output, independently of persisted PDF history.</summary>
internal sealed class CompiledBibliographyEvidence
{
    internal const int MaxBytes = 512 * 1024, MaxFiles = 24;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly byte[] _packed;
    private readonly File[] _sourceFiles, _auxFiles;
    private readonly string _root, _rootAux;
    private readonly int _bblBytes;

    private CompiledBibliographyEvidence(byte[] packed, File[] sourceFiles, File[] auxFiles, string root, string rootAux, int bblBytes)
        => (_packed, _sourceFiles, _auxFiles, _root, _rootAux, _bblBytes) = (packed, sourceFiles, auxFiles, root, rootAux, bblBytes);

    // The factory already verifies the phase tag, implementation hashes and
    // complete input facts before returning any final owned Files/SyncTeX bytes.
    internal static CompiledBibliographyEvidence? Capture(CompilationInputSnapshot snapshot, NativePhaseReply final,
        NativeSelectedEntry entry, IReadOnlyDictionary<string, byte[]> emitted, ContentHash producedBbl)
    {
        if (final.SourceMapPayload is not { Length: > 0 and <= NativeSyncTeXContract.MaxMapBytes } map
            || !emitted.TryGetValue(entry.Bbl, out var bbl) || bbl.Length is < 1 or > MaxBytes
            || !emitted.ContainsKey(entry.Aux) || NativeRuntimePackage.Digest(bbl) != producedBbl
            || !final.Inputs.Any(f => f.Name == entry.Bbl && f.Length == bbl.Length && f.Sha256 == producedBbl.Hex))
            return null;

        // Initial coverage trusts the installed standard class/style bytes.
        // A user .bst can change the meaning of otherwise ordinary TeX controls.
        if (snapshot.Files.Any(f => Path.GetExtension(f.RelativePath).Equals(".bst", StringComparison.OrdinalIgnoreCase)))
            return null;
        var captured = snapshot.Files.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
        var sources = new HashSet<string>(StringComparer.Ordinal);
        bool openedBbl = false;
        string syncTeX;
        try { syncTeX = Utf8.GetString(map); }
        catch (DecoderFallbackException) { return null; }
        if (!syncTeX.StartsWith("SyncTeX Version:1\n", StringComparison.Ordinal)) return null;
        foreach (var line in syncTeX.AsSpan().EnumerateLines())
        {
            if (!line.StartsWith("Input:", StringComparison.Ordinal)) continue;
            var value = line[6..];
            int colon = value.IndexOf(':');
            if (colon < 1 || !uint.TryParse(value[..colon], out uint tag) || tag == 0) return null;
            var name = value[(colon + 1)..];
            if (name.StartsWith("/work/", StringComparison.Ordinal)) name = name[6..];
            else if (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            if (name.SequenceEqual(entry.Bbl)) openedBbl = true;
            if (name.EndsWith(".tex", StringComparison.Ordinal))
            {
                string path = name.ToString();
                if (captured.TryGetValue(path, out var file))
                {
                    if (file.MediaKind != "text" || file.ByteLength == 0) return null;
                    sources.Add(path);
                }
                // The supported literal source recognizes includes only when
                // their exact path is present in this opened-file projection.
            }
            if (name.EndsWith(".cls", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".sty", StringComparison.OrdinalIgnoreCase))
                if (captured.ContainsKey(name.ToString())) return null;
        }
        if (!openedBbl || !sources.Contains(entry.SelectedPath) || sources.Count > MaxFiles) return null;
        var aux = emitted.Where(pair => pair.Key.EndsWith(".aux", StringComparison.Ordinal)).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        if (aux.Length > MaxFiles || aux.Any(pair => pair.Value.Length == 0)) return null;
        var orderedSources = sources.Order(StringComparer.Ordinal).Select(path => captured[path]).ToArray();
        long length = bbl.Length + aux.Sum(pair => (long)pair.Value.Length) + orderedSources.Sum(f => f.ByteLength);
        if (length > MaxBytes) return null;
        var packed = new byte[(int)length];
        int offset = 0;
        foreach (var file in orderedSources) Copy(file.ToArray());
        foreach (var pair in aux) Copy(pair.Value);
        Copy(bbl);
        return new(packed,
            orderedSources.Select(f => new File(f.RelativePath, checked((int)f.ByteLength))).ToArray(),
            aux.Select(pair => new File(pair.Key, pair.Value.Length)).ToArray(), entry.SelectedPath, entry.Aux, bbl.Length);

        void Copy(byte[] source) { source.CopyTo(packed, offset); offset += source.Length; }
    }

    internal (string Metadata, byte[] Bytes) Pack(string requestId) => (
        JsonSerializer.Serialize(new
        {
            protocolVersion = 1, requestId, operation = "compiledBibliography", root = _root, rootAux = _rootAux,
            sourceFiles = _sourceFiles.Select(f => new { name = f.Name, length = f.Length }),
            auxFiles = _auxFiles.Select(f => new { name = f.Name, length = f.Length }),
            bblBytes = _bblBytes, confirmMainWork = true
        }), _packed.ToArray());

    private sealed record File(string Name, int Length);
}
