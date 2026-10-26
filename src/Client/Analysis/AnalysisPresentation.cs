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
    public static string MarginSide(AnalysisMarginSide value) => value switch
    {
        AnalysisMarginSide.Left => "left",
        AnalysisMarginSide.Right => "right",
        AnalysisMarginSide.Top => "top",
        AnalysisMarginSide.Bottom => "bottom",
        _ => "unknown side",
    };
    public static string Paper(AnalysisPaperSize value) => value switch
    {
        AnalysisPaperSize.A4 => "A4", AnalysisPaperSize.A3 => "A3 (illustration purpose unestablished)",
        AnalysisPaperSize.A5 => "A5", AnalysisPaperSize.B5 => "B5", AnalysisPaperSize.Letter => "Letter",
        AnalysisPaperSize.Legal => "Legal", AnalysisPaperSize.Executive => "Executive", _ => "unknown paper",
    };
    public static string Spacing(AnalysisSpacingSyntax value) => value switch
    {
        AnalysisSpacingSyntax.OneHalfSpacing => "onehalfspacing — conventional setspace intent",
        AnalysisSpacingSyntax.SetStretch => "setstretch — declared nominal factor",
        AnalysisSpacingSyntax.LineSpread => "linespread — declared nominal factor",
        AnalysisSpacingSyntax.BaselineStretch => "baselinestretch renewal — declared nominal factor", _ => "unknown declaration",
    };
    public static string CaptionEnvironment(AnalysisCaptionEnvironment value) => value switch
    { AnalysisCaptionEnvironment.Figure => "figure", AnalysisCaptionEnvironment.Table => "table", _ => "unknown context" };
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
        AnalysisLimitation.UnsupportedGeometry => "The geometry declaration contains unsupported options or values; margin coverage is uncertain.",
        AnalysisLimitation.MissingMarginSides => "A geometry declaration does not supply all four margin sides.",
        AnalysisLimitation.MultipleGeometryDeclarations => "Multiple geometry declarations prevent a definite margin result.",
        AnalysisLimitation.PaperUncertain => "Paper intent is missing, unsupported, repeated or dynamic, or A3 illustration purpose is unestablished; physical sheet geometry is not measured.",
        AnalysisLimitation.LineSpacingUncertain => "Spacing intent is unsupported, dynamic or repeated; required setspace context may be missing. Effective baseline/glyph geometry is not measured.",
        AnalysisLimitation.CaptionUncertain => "Caption syntax or ordinary figure/table ownership is unsupported; starred/short titles, placement arguments and dynamic titles are not guessed.",
        _ => "Unsupported analysis context.",
    };
}
