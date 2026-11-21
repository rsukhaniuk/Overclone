namespace Client.Compilation.Native;

/// <summary>Closed P16 source-input expansion. Engine/formatter/Biber/query
/// domains delegate P15; all output and native resource limits stay unchanged.</summary>
public static class NativeInputBudgetContract
{
    public const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalCompilerInputBudget;
    public const int InputBudgetAbi = 1, ProjectFileBytes = 2 * 1024 * 1024, ProjectTotalBytes = 16 * 1024 * 1024;
    public const int ConverterProjectBytes = 16 * 1024 * 1024, PdfOutputBytes = 8 * 1024 * 1024;
    public static bool Enabled(NativeRuntimeProfile profile) => profile switch
    {
        Profile => NativeCompilerInputBudgetPins.Ready,
        NativeSelectedPdfContract.Profile => NativeCompilerSelectedPdfPins.Ready,
        NativeRuntimeProfile.LocalCompilerCoursework => NativeCompilerCourseworkPins.Ready,
        NativeRuntimeProfile.LocalCompilerDviGraphics => NativeCompilerDviGraphicsPins.Ready,
        NativeRuntimeProfile.LocalCompilerPdfOrigins => NativeCompilerPdfOriginPins.Ready,
        NativeRuntimeProfile.LocalCompilerPageLabels => NativeCompilerPageLabelPins.Ready,
        _ => false
    };
    public static NativeCompilerComponent Component(NativeCompilerEngine engine)
    {
        var previous = NativeOriginCompilerContract.Component(engine);
        return engine switch
        {
            NativeCompilerEngine.LaTeXDvi => previous with { OutputAdapter = previous.OutputAdapter with
                { Version = previous.OutputAdapter.Version + "-input-budget1", Hash = new(NativeCompilerInputBudgetPins.DviConverterWasmHash) } },
            NativeCompilerEngine.XeLaTeX => previous with { OutputAdapter = previous.OutputAdapter with
                { Version = previous.OutputAdapter.Version + "-input-budget1", Hash = new(NativeCompilerInputBudgetPins.XeConverterWasmHash) } },
            _ => previous
        };
    }
    public static bool NoticeName(string? name) => NativeThesisCompilerContract.NoticeName(name)
        || NativeCompilerInputBudgetPins.AdditionalResources.Any(p => p.Name == name && p.Name.StartsWith("notices/", StringComparison.Ordinal));
}
