using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Transfer;

namespace Client.Analysis;

/// <summary>Immutable transport configuration, independent of compile main.</summary>
public sealed record AnalysisReferenceConfigurationDto(string RootFileId);

/// <summary>Finite transport/display identities only. Rust owns closure and keys.</summary>
public static class ReferenceChecks
{
    public const string DuplicateMessage = "Repeated literal label key in the selected source closure.";
    public const string MissingMessage = "No matching literal label was found in the selected source closure.";
    public static bool CanBeRoot(WorkingFile file) => !file.Deleted && file.MediaKind == "text"
        && IsCanonicalPath(file.RelativePath) && AnalysisCatalog.IsTex(file.RelativePath);
    // Called ONLY on explicit profile choice. Never used to repair stale roots.
    public static WorkingFile? DefaultRoot(IEnumerable<WorkingFile> files, FileId? active)
    {
        var eligible = files.Where(CanBeRoot).ToArray();
        return eligible.FirstOrDefault(f => f.RelativePath == "main.tex")
            ?? eligible.FirstOrDefault(f => f.Id == active) ?? eligible.FirstOrDefault();
    }
    public static bool IsCanonicalPath(string path) => AnalysisIdentity.IsValidOpaqueId(path, AnalysisLimits.MaxPathBytes)
        && path == ProjectPathValidator.Normalize(path) && ProjectPathValidator.Validate(path) is null;
    public static string Limitation(string code) => code switch
    {
        "sourceIntentOnly" => "Source intent only; compiled numbering and PDF layout are not measured.",
        "macroExpansionUnsupported" => "TeX macro expansion is not performed.",
        "unsupportedContext" => "An unsupported command or execution context prevents a complete check.",
        "nonliteralKey" => "A key is not a supported exact literal; no normalized key is guessed.",
        "missingInclude" => "An included project-local text target is unavailable.",
        "repeatedInclude" => "Repeated inclusion prevents an effective execution claim.",
        "includeCycle" => "An include cycle prevents a complete source closure.",
        "unsupportedIncludeSyntax" => "Include syntax or path semantics are unsupported.",
        "budgetExhausted" => "A bounded analysis budget was exhausted.",
        "incompleteScan" => "The source closure could not be scanned completely.",
        _ => "Unknown limitation; this result must not be treated as complete.",
    };
    internal static bool IsLimitation(string code) => code is "sourceIntentOnly" or "macroExpansionUnsupported"
        or "unsupportedContext" or "nonliteralKey" or "missingInclude" or "repeatedInclude" or "includeCycle"
        or "unsupportedIncludeSyntax" or "budgetExhausted" or "incompleteScan";
    internal static bool Observed(string code) => code is not ("sourceIntentOnly" or "macroExpansionUnsupported");
}
