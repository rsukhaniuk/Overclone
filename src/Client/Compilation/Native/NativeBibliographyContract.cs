using System.Globalization;
using System.Text;
using Client.Workspace.Transfer;

namespace Client.Compilation.Native;

/// <summary>Profile7's finite BibTeX control/carry policy. No Biber capability,
/// source rewriting or filename normalization. Older profiles never enter it.</summary>
public static class NativeBibliographyContract
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    // Pinned biblatex3.19 blx-bibtex.def derives only its control basename by
    // replacing spaces. JobName is already unquoted; do not rename the TeX job
    // or admit a second original-space BIB variant. Preserve Unicode verbatim.
    public static string BlxName(NativeSelectedEntry entry) => entry.JobName.Replace(' ', '_') + "-blx.bib";

    public static NativeSelectedEntry ExtendEntry(NativeRuntimeProfile profile, NativeSelectedEntry entry) =>
        NativeRuntimeProfiles.HasBibliographyCarry(profile)
            ? entry with { Outputs = entry.Outputs.Add(BlxName(entry)), State = entry.State.Add(BlxName(entry)) }
            : entry;

    public static string? ValidateProjectNames(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (NativeAuthoringV4Contract.ValidatePath(name) is { } invalid) return invalid;
            var key = ProjectPathValidator.ComparisonKey(name);
            if (key == "__OOP_RUNTIME__" || key.StartsWith("__OOP_RUNTIME__/", StringComparison.Ordinal))
                return "reserved-runtime-namespace";
        }
        return null;
    }

    public static string? ValidateNamespace(NativeSelectedEntry entry, IEnumerable<string> inputs, IEnumerable<string> carried)
    {
        // This extends exactly one generated BIB, not the generated extension set.
        foreach (var name in entry.Outputs)
            if (name.EndsWith(".bib", StringComparison.OrdinalIgnoreCase) && name != BlxName(entry)
                || name.EndsWith(".bcf", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                return "bibliography-invalid-output";
        return NativeSelectedEntryContract.ValidateNamespace(entry, inputs, carried);
    }

    /// <summary>Validate literal native AUX records before a backend call. This
    /// is bounded control-file admission, not a TeX or bibliography interpreter.
    /// Unknown/macro-dependent control names fail rather than inventing inputs.</summary>
    public static string? ControlInputIssue(NativeSelectedEntry entry, IReadOnlyDictionary<string, byte[]> state,
        IEnumerable<string> projectNames, IEnumerable<string> runtimeNames)
    {
        if (!state.TryGetValue(entry.Aux, out var mainAux) || mainAux.Length == 0) return "missing-native-aux";
        var styles = new HashSet<string>(StringComparer.Ordinal);
        var databases = new HashSet<string>(StringComparer.Ordinal);
        var includedAux = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in state.Where(p => p.Key.EndsWith(".aux", StringComparison.Ordinal)))
        {
            if (pair.Value.Length > NativeAuthoringV4Contract.StateBytes) return "v4-state-budget";
            string text;
            try { text = Utf8.GetString(pair.Value); }
            catch (DecoderFallbackException) { return "invalid-bibliography-control"; }
            if (text.Contains('\0')) return "invalid-bibliography-control";
            foreach (var line in text.AsSpan().EnumerateLines())
            {
                var value = line.TrimStart();
                if (ReadRecord(value, "\\bibstyle", ".bst", false, styles) is { } styleError) return styleError;
                if (ReadRecord(value, "\\bibdata", ".bib", true, databases) is { } dataError) return dataError;
                if (ReadRecord(value, "\\@input", ".aux", false, includedAux) is { } auxError) return auxError;
            }
        }
        foreach (var name in includedAux)
            if (!state.TryGetValue(name, out var includedBytes) || includedBytes.Length == 0) return "missing-native-aux";
        if (styles.Count == 0 || databases.Count == 0) return "missing-bibliography-control";
        var project = projectNames.ToHashSet(StringComparer.Ordinal);
        var runtime = runtimeNames.ToHashSet(StringComparer.Ordinal);
        foreach (var name in styles)
            if (!project.Contains(name) && !runtime.Contains(NativeCompilerSuiteContract.ResourcePrefix + name))
                return "missing-bibliography-style";
        bool biblatex = styles.Any(n => n == "biblatex.bst");
        if (biblatex && (!state.TryGetValue(BlxName(entry), out var blx) || blx.Length == 0)) return "missing-native-blx";
        if (biblatex && !databases.Contains(BlxName(entry))) return "unsupported-biblatex-control-name";
        foreach (var name in databases)
        {
            if (name == BlxName(entry))
            {
                if (!state.TryGetValue(name, out var databaseBytes) || databaseBytes.Length == 0) return "missing-native-blx";
            }
            else if (!project.Contains(name) && !runtime.Contains(NativeCompilerSuiteContract.ResourcePrefix + name))
                return "missing-bibliography-database";
        }
        return null;
    }

    private static string? ReadRecord(ReadOnlySpan<char> line, string command, string extension, bool list, HashSet<string> names)
    {
        if (!line.StartsWith(command, StringComparison.Ordinal)) return null;
        line = line[command.Length..];
        if (line.Length < 3 || line[0] != '{') return "invalid-bibliography-control";
        int end = line.IndexOf('}');
        if (end < 1 || line[1..end].Contains('{')) return "invalid-bibliography-control";
        var body = line[1..end];
        while (true)
        {
            int comma = list ? body.IndexOf(',') : -1;
            var item = (comma < 0 ? body : body[..comma]).Trim();
            // The native filename is <=249 UTF8 bytes. Bound syntax overhead
            // before allocating strings or a potentially enormous Split array.
            if (item.Length > 512) return "invalid-bibliography-control";
            // Quotes are native filename syntax, not filesystem bytes. Preserve
            // internal spaces and Unicode exactly; never NFC-rewrite the name.
            int quotes = 0;
            foreach (char value in item) if (value == '"') quotes++;
            if (quotes % 2 != 0) return "invalid-bibliography-control";
            var name = item.ToString().Replace("\"", "", StringComparison.Ordinal).Trim();
            if (name.Length == 0 || name.Contains('{') || name.Contains('}') || name.Contains('\\'))
                return "invalid-bibliography-control";
            if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) name += extension;
            if (ValidateProjectNames([name]) is not null) return "invalid-bibliography-control";
            names.Add(name);
            if (names.Count > NativeCompilerSuiteContract.Files + 192) return "invalid-bibliography-control";
            if (comma < 0) return null;
            body = body[(comma + 1)..];
        }
    }

    /// <summary>Canonical ordered metadata over actual owned byte digests.
    /// Runtime identity binds all verified runtime BIB/BST bytes. BBL/BLG are
    /// deliberately excluded: a backend's own output must not trigger itself.</summary>
    public static string Fingerprint(NativeSelectedEntry entry, IReadOnlyDictionary<string, byte[]> state,
        CompilationInputSnapshot snapshot, string runtimeIdentity, CompilationDescriptor bibtex)
    {
        var fields = new List<string> { "native-bibtex-input-v1", runtimeIdentity,
            bibtex.Id, bibtex.Version, bibtex.Hash.Hex,
            snapshot.Selection.Engine.Id, snapshot.Selection.Engine.Version, snapshot.Selection.Engine.Hash.Hex,
            entry.SelectedPath, entry.JobName };
        var controls = state.Where(p => p.Key.EndsWith(".aux", StringComparison.Ordinal) || p.Key == BlxName(entry))
            .OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        fields.Add("generated-control"); fields.Add(controls.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var pair in controls)
        {
            fields.Add(pair.Key); fields.Add(pair.Value.Length.ToString(CultureInfo.InvariantCulture));
            fields.Add(NativeRuntimePackage.Digest(pair.Value).Hex);
        }
        var sources = snapshot.Files.Where(f => Path.GetExtension(f.RelativePath).Equals(".bib", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(f.RelativePath).Equals(".bst", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToArray();
        fields.Add("project-bibliography"); fields.Add(sources.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var file in sources)
        {
            fields.Add(file.RelativePath); fields.Add(file.ByteLength.ToString(CultureInfo.InvariantCulture)); fields.Add(file.Hash.Hex);
        }
        // Length-prefix every UTF8 field: paths/tool descriptors cannot create
        // ambiguous tuples, irrespective of punctuation or field ordering.
        var canonical = new StringBuilder();
        foreach (var field in fields) canonical.Append(Utf8.GetByteCount(field).ToString(CultureInfo.InvariantCulture)).Append(':').Append(field);
        return NativeRuntimePackage.Digest(Utf8.GetBytes(canonical.ToString())).Hex;
    }
}
