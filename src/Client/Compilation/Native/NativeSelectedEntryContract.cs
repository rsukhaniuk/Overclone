using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Client.Workspace.Transfer;

namespace Client.Compilation.Native;

/// <summary>Version 5: selected source path, project-CWD job outputs, basename BibTeX entry.
/// No source rewriting, jobname flag, chdir or native runtime change.</summary>
public sealed record NativeSelectedEntry(string SelectedPath, string LatexEntry, string BibtexEntry, string JobName,
    ImmutableArray<string> Outputs, ImmutableArray<string> State)
{
    public string Pdf => JobName + ".pdf";
    public string Aux => JobName + ".aux";
    public string Bbl => JobName + ".bbl";
    public bool IsState(string name) => State.Contains(name, StringComparer.Ordinal);
}

public static class NativeSelectedEntryContract
{
    public const int Version = 5;
    public static CompilationOption SelectionOption => new("overclone-native-phase-contract", "5");
    public static CompilationOption[] SelectionOptions => [SelectionOption];
    public static bool MatchesOptions(IEnumerable<CompilationOption> options) => options.SequenceEqual(SelectionOptions);
    public static NativeCompilePlan CreatePlan(int maxLatexPasses = 6, TimeSpan? deadline = null) =>
        NativeAuthoringV4Contract.CreatePlan(maxLatexPasses, deadline) with { PhaseProtocolVersion = Version };

    public static string? ValidateEntry(string name)
        => ValidateEntry(name, NativeRuntimeProfile.Legacy, NativeCompilerEngine.PdfLaTeX);
    public static string? ValidateEntry(string name, NativeRuntimeProfile profile, NativeCompilerEngine engine)
    {
        if (NativeAuthoringV4Contract.ValidatePath(name) is { } invalid) return invalid;
        if (!name.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)) return "selected-entry-extension";
        // bootstrapcmd receives a literal filename, quoted for directory or leaf spaces.
        // Reject TeX metacharacters, quotes and malformed UTF-16, never repair.
        foreach (var rune in name.EnumerateRunes())
        {
            if (rune.Value is '/' or '.' or '_' or '-' or ' ') continue;
            // Exact native capability/profile only. Path validation above still
            // refuses malformed UTF-16 and unsafe project/native boundaries.
            if (profile is NativeUnicodeCompilerContract.Profile or NativeBiberCompilerContract.Profile or NativeThesisCompilerContract.Profile or NativeInputBudgetContract.Profile or NativeOriginCompilerContract.Profile && engine == NativeCompilerEngine.XeLaTeX
                && rune.Value > 0xffff) continue;
            if (!Rune.IsLetterOrDigit(rune) && Rune.GetUnicodeCategory(rune) is not
                (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)) return "selected-entry-syntax";
        }
        var leaf = name[(name.LastIndexOf('/') + 1)..];
        if (leaf.Length <= 4 || leaf[0] is '.' or '-' or ' ' || leaf[^5] == ' ') return "selected-entry-syntax";
        return null;
    }

    public static NativeSelectedEntry? Create(string selected, IEnumerable<string> projectNames, out string? error, bool dviOutput = false)
        => Create(selected, projectNames, out error, dviOutput, NativeRuntimeProfile.Legacy, NativeCompilerEngine.PdfLaTeX);
    public static NativeSelectedEntry? Create(string selected, IEnumerable<string> projectNames, out string? error,
        bool dviOutput, NativeRuntimeProfile profile, NativeCompilerEngine engine)
    {
        error = ValidateEntry(selected, profile, engine);
        if (error is not null) return null;
        var leaf = selected[(selected.LastIndexOf('/') + 1)..];
        var job = leaf[..^4];
        var outputs = new[] { ".pdf", ".aux", ".bbl", ".blg", ".log", ".toc", ".lof", ".lot", ".out" }
            .Concat(dviOutput ? [".dvi"] : Array.Empty<string>()).Select(ext => job + ext)
            .Concat(projectNames.Where(n => n != selected && n.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                .Select(n => n[..^4] + ".aux")).ToImmutableArray();
        // Do not coalesce generated aliases: two distinct producers must not share an output.
        error = ValidateNames([], outputs);
        if (error is not null) return null;
        var state = outputs.Where(n => !n.EndsWith(".pdf", StringComparison.Ordinal)
            && !n.EndsWith(".blg", StringComparison.Ordinal) && !n.EndsWith(".log", StringComparison.Ordinal)
            && !n.EndsWith(".dvi", StringComparison.Ordinal)).ToImmutableArray();
        return new(selected, selected.Contains(' ') ? "\"" + selected + "\"" : selected, leaf, job, outputs, state);
    }

    public static string? ValidateNamespace(NativeSelectedEntry entry, IEnumerable<string> inputs, IEnumerable<string> carried)
    {
        if (ValidateNames(inputs, entry.Outputs) is { } invalid) return invalid;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return carried.Any(n => !entry.IsState(n) || !seen.Add(n)) ? "selected-entry-invalid-state" : null;
    }

    private static string? ValidateNames(IEnumerable<string> inputs, IReadOnlyList<string> outputs)
    {
        var names = inputs.ToArray();
        if (NativeAuthoringV4Contract.ValidateNamespace(names, outputs, []) is { } invalid) return invalid;
        // Preserve the workspace's stronger Unicode case/NFC alias refusal as well.
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names.Concat(outputs))
            if (!keys.Add(ProjectPathValidator.ComparisonKey(name))) return "selected-entry-namespace-collision";
        foreach (var key in keys)
            for (int i = key.IndexOf('/'); i >= 0; i = key.IndexOf('/', i + 1))
                if (keys.Contains(key[..i])) return "selected-entry-namespace-collision";
        return null;
    }
}
