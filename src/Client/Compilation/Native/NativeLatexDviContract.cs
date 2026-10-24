namespace Client.Compilation.Native;

/// <summary>Selected-root LaTeX using a genuine DVI format, followed by the fixed
/// native converter. No source rewriting or source/PDF map is supported.</summary>
public static class NativeLatexDviContract
{
    public const int Version = 7;
    public const int ProjectBytes = 8 * 1024 * 1024, ResourceBytes = 96 * 1024 * 1024;
    public const int FileBytes = 32 * 1024 * 1024, Files = 4096, PdfBytes = 8 * 1024 * 1024;
    public static CompileOutputDescriptor OutputAdapter => new("native-latex-dvi", "dvi-v1",
        new(NativeRuntimeProfiles.DviConverterWasmHash));
    public static CompilationOption[] SelectionOptions => [new("overclone-native-phase-contract", "7")];
    public static bool MatchesOptions(IEnumerable<CompilationOption> options) => options.SequenceEqual(SelectionOptions);
    public static NativeCompilePlan CreatePlan(int maxLatexPasses = 6, TimeSpan? deadline = null) =>
        NativeAuthoringV4Contract.CreatePlan(maxLatexPasses, deadline) with
        { Profile = NativeRuntimeProfile.LocalLatex2023Dvi, PhaseProtocolVersion = Version, DviOutput = true };
}
