using System.Text;
using Client.Workspace.Transfer;

namespace Client.Compilation.Native;

/// <summary>Trusted opt-in phase algorithm, not a new vendor/profile or registration.</summary>
public static class NativeAuthoringV4Contract
{
    public const int Version = 4, ProjectFiles = 64, PhaseFiles = 768, Outputs = 128, CarriedFiles = 128, NewNodes = 128;
    public const int StateBytes = 8 * 1024 * 1024;
    public static CompilationOption SelectionOption => new("overclone-native-phase-contract", "4");
    public static CompilationOption[] SelectionOptions => [SelectionOption];
    public static NativeCompilePlan CreatePlan(int maxLatexPasses = 6, TimeSpan? deadline = null) => new(false,
        maxLatexPasses, deadline ?? TimeSpan.FromSeconds(90), PhaseFiles, 64 * 1024 * 1024, 32 * 1024 * 1024,
        UseSelectionTools: true, Profile: NativeRuntimeProfile.LocalLatex2023Authoring, PhaseProtocolVersion: Version);

    public static bool MatchesOptions(IEnumerable<CompilationOption> options) => options.SequenceEqual(SelectionOptions);
    public static string[] SelectedOutputs(IEnumerable<string> projectNames) =>
        new[] { "main.pdf", "main.aux", "main.bbl", "main.blg", "main.log", "main.toc", "main.lof", "main.lot", "main.out" }
        .Concat(projectNames.Where(n => n.EndsWith(".tex", StringComparison.Ordinal) && n != "main.tex")
            .Select(n => n[..^4] + ".aux")).ToArray();
    public static bool IsState(string name) => name.EndsWith(".aux", StringComparison.Ordinal)
        || name is "main.bbl" or "main.toc" or "main.lof" or "main.lot" or "main.out";

    // Same NFC + ASCII case fold as the defensive worker. No name rewriting.
    private static string Key(string name) => string.Concat(name.Normalize(NormalizationForm.FormC)
        .Select(c => c is >= 'a' and <= 'z' ? (char)(c - 32) : c));
    public static string? ValidatePath(string name)
    {
        try
        {
            if (name != ProjectPathValidator.Normalize(name) || ProjectPathValidator.Validate(name) is not null
                || name.StartsWith('\ufeff') || name.EndsWith('\ufeff')) return "v4-invalid-path";
            // Includes the /work/ prefix and terminating NUL in the native256-byte lookup buffer.
            if (new UTF8Encoding(false, true).GetByteCount(name) > 249) return "v4-native-path-limit";
            return null;
        }
        catch (ArgumentException) { return "v4-invalid-path"; }
    }
    public static string? ValidateNamespace(IEnumerable<string> inputs, IReadOnlyList<string> outputs, IEnumerable<string> carried)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in inputs.Concat(outputs))
        {
            if (ValidatePath(name) is { } invalid) return invalid;
            var key = Key(name);
            if (!keys.Add(key)) return "v4-namespace-collision";
        }
        foreach (var key in keys)
            for (int i = key.IndexOf('/'); i >= 0; i = key.IndexOf('/', i + 1))
                if (keys.Contains(key[..i])) return "v4-namespace-collision";
        // Outputs and their own carried input intentionally coincide, never runtime/source.
        var states = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in carried)
            if (!IsState(name) || !outputs.Contains(name, StringComparer.Ordinal) || !states.Add(name)) return "v4-invalid-state";
        return null;
    }
}

internal sealed class NativeAuthoringV4Exception(string code) : Exception(code)
{
    public string Code { get; } = code;
}
