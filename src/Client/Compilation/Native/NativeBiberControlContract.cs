using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Xml;

namespace Client.Compilation.Native;

public sealed record NativeBiberControl(string BcfName, ImmutableArray<string> Sources);

/// <summary>Bounded admission of genuine current-pass BCF, not a bibliography
/// interpreter. Biber owns bibliography semantics; only captured local sources
/// can cross the worker boundary. No XML entities, URLs or glob expansion.</summary>
public static class NativeBiberControlContract
{
    public const string Namespace = "https://sourceforge.net/projects/biblatex";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static string BcfName(NativeSelectedEntry entry) => entry.JobName + ".bcf";
    public static string RunXmlName(NativeSelectedEntry entry) => entry.JobName + ".run.xml";

    public static NativeBiberControl? Read(NativeSelectedEntry entry, IReadOnlyDictionary<string, byte[]> generated,
        IEnumerable<string> projectNames, out string? issue)
    {
        issue = null;
        var name = BcfName(entry);
        if (!generated.TryGetValue(name, out var bytes) || bytes.Length == 0)
        { issue = "missing-native-bcf"; return null; }
        if (bytes.Length > NativeAuthoringV4Contract.StateBytes)
        { issue = "biber-input-quota"; return null; }
        var captured = projectNames.ToHashSet(StringComparer.Ordinal);
        var sources = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            using var text = new StringReader(Utf8.GetString(bytes));
            using var reader = XmlReader.Create(text, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = NativeAuthoringV4Contract.StateBytes,
                IgnoreComments = true, IgnoreWhitespace = true
            });
            if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "controlfile"
                || reader.NamespaceURI != Namespace || reader.GetAttribute("version") != "3.10"
                || reader.GetAttribute("bltxversion") != "3.19")
            { issue = "incompatible-biber-control"; return null; }
            int nodes = 0;
            while (reader.Read())
            {
                if (++nodes > 100000 || reader.Depth > 64 || reader.AttributeCount > 64)
                { issue = "biber-input-quota"; return null; }
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "datasource") continue;
                if (reader.NamespaceURI != Namespace || reader.GetAttribute("type") != "file"
                    || reader.GetAttribute("glob") is { } glob && glob != "false")
                { issue = "unsupported-biber-datasource"; return null; }
                // Subtree disposal leaves the outer cursor at the element end,
                // so adjacent datasource records are never skipped.
                string source;
                using (var subtree = reader.ReadSubtree())
                { subtree.Read(); source = subtree.ReadElementContentAsString(); }
                if (source.Length == 0 || source.Length > 1024 || source != source.Trim()
                    || NativeBibliographyContract.ValidateProjectNames([source]) is not null
                    || source == name || source == RunXmlName(entry)
                    || source.IndexOfAny(['*', '?']) >= 0)
                { issue = "unsupported-biber-datasource"; return null; }
                if (!captured.Contains(source))
                { issue = "missing-biber-datasource"; return null; }
                sources.Add(source);
                if (sources.Count > 63) { issue = "biber-input-quota"; return null; }
            }
        }
        catch (Exception error) when (error is XmlException or DecoderFallbackException or ArgumentException)
        { issue = "invalid-biber-control"; return null; }
        if (sources.Count == 0) { issue = "missing-biber-datasource"; return null; }
        return new(name, sources.ToImmutableArray());
    }

    public static string Fingerprint(NativeSelectedEntry entry, NativeBiberControl control,
        IReadOnlyDictionary<string, byte[]> generated, CompilationInputSnapshot snapshot,
        string runtimeIdentity, CompilationDescriptor biber)
    {
        var fields = new List<string> { "native-biber-input-v1", runtimeIdentity,
            biber.Id, biber.Version, biber.Hash.Hex,
            snapshot.Selection.Engine.Id, snapshot.Selection.Engine.Version, snapshot.Selection.Engine.Hash.Hex,
            entry.SelectedPath, entry.JobName, control.BcfName,
            NativeRuntimePackage.Digest(generated[control.BcfName]).Hex };
        // Only exact referenced source hashes participate. BBL, BLG, AUX and
        // run.xml cannot make a backend repeatedly trigger on its own output.
        foreach (var name in control.Sources)
        {
            var source = snapshot.Files.Single(f => f.RelativePath == name);
            fields.Add(name); fields.Add(source.ByteLength.ToString(CultureInfo.InvariantCulture)); fields.Add(source.Hash.Hex);
        }
        var canonical = new StringBuilder();
        foreach (var field in fields) canonical.Append(Utf8.GetByteCount(field).ToString(CultureInfo.InvariantCulture)).Append(':').Append(field);
        return NativeRuntimePackage.Digest(Utf8.GetBytes(canonical.ToString())).Hex;
    }
}
