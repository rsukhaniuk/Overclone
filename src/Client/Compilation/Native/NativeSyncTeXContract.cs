namespace Client.Compilation.Native;

/// <summary>Selected-root authoring with a map sealed by the actual final native
/// LaTeX pass. Older runtime profiles retain their original protocols.</summary>
public static class NativeSyncTeXContract
{
    public const int Version = 6;
    public const int MaxMapBytes = 1048575;
    public static CompileOutputDescriptor OutputAdapter => new("native-pdftex", "n11-synctex-v1",
        new(NativeRuntimeProfiles.SyncTeXCompilerWasmHash));
    public static CompilationOption[] SelectionOptions => [new("overclone-native-phase-contract", "6")];
    public static bool MatchesOptions(IEnumerable<CompilationOption> options) => options.SequenceEqual(SelectionOptions);
    public static NativeCompilePlan CreatePlan(int maxLatexPasses = 6, TimeSpan? deadline = null) =>
        NativeAuthoringV4Contract.CreatePlan(maxLatexPasses, deadline) with
        { Profile = NativeRuntimeProfile.LocalLatex2023SyncTeX, PhaseProtocolVersion = Version };
}
