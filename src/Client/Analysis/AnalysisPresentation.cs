namespace Client.Analysis;

/// <summary>Display labels only; no parsing, rule evaluation or fix execution.</summary>
public static class AnalysisPresentation
{
    public static string WorkType(AnalysisWorkType value) => value switch
    {
        AnalysisWorkType.Unspecified => "Unspecified — no degree/work type chosen",
        AnalysisWorkType.Coursework => "Coursework",
        AnalysisWorkType.BachelorQualification => "Bachelor qualification",
        AnalysisWorkType.MasterQualification => "Master qualification",
        _ => "Unknown work type",
    };
    public static string WorkValue(AnalysisWorkType value) => value switch
    {
        AnalysisWorkType.Unspecified => "unspecified", AnalysisWorkType.Coursework => "coursework",
        AnalysisWorkType.BachelorQualification => "bachelorQualification", AnalysisWorkType.MasterQualification => "masterQualification",
        _ => "unknown",
    };
    public static string Limitation(AnalysisLimitation value) => value switch
    {
        AnalysisLimitation.IncompleteEngineScan => "The lexical scan was incomplete; source observations cannot establish an answer.",
        AnalysisLimitation.UnmatchedGroup => "Unmatched grouping makes source interpretation uncertain.",
        AnalysisLimitation.UnknownSemantics => "Unknown command, class, package or macro semantics are not expanded.",
        AnalysisLimitation.UnsupportedContext => "Local, conditional, math or environment context is outside the supported subset.",
        AnalysisLimitation.UnsupportedArgument => "A dynamic or unsupported argument cannot be interpreted safely.",
        AnalysisLimitation.UnsupportedLength => "The indent uses unsupported literal length syntax or units; no conversion is guessed.",
        AnalysisLimitation.ArgumentTooLong => "An argument exceeds the bounded interpretation limit; its value remains unknown.",
        AnalysisLimitation.ConflictingDeclarations => "Conflicting indent declarations prevent a definite indent result.",
        AnalysisLimitation.IncludeNotResolved => "Includes are not resolved or expanded; included project context is uncertain.",
        AnalysisLimitation.ObservationBudget => "The shared observation emission budget was exhausted.",
        AnalysisLimitation.LookaheadBudget => "The bounded source inspection budget was exhausted.",
        AnalysisLimitation.DiagnosticBudget => "The diagnostic output budget was exhausted; findings are incomplete.",
        AnalysisLimitation.NoSupportedObservation => "No supported source observation is available for this check.",
        _ => "Unsupported analysis context.",
    };
}
