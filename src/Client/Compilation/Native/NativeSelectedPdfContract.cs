namespace Client.Compilation.Native;

/// <summary>Limits for the selected pdfLaTeX result from the admitted P17–P21 closures.</summary>
public static class NativeSelectedPdfContract
{
    public const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalCompilerSelectedPdf;
    // Retain the identity name used by the reproduced native boundary proof.
    internal const string IdentityName = "private-r1-pdfa-official-ec-tc";
    public const string RuntimeIdentity = "41d0bb841b4832771327e44c84ada913e75204e25c5f39d6e52204e7350bf109";
    public const int SelectedPdfBytes = 12 * 1024 * 1024;
    public const int OtherOutputBytes = NativeInputBudgetContract.PdfOutputBytes;

    public static bool Supports(NativeRuntimeProfile? profile) => profile switch
    {
        Profile => NativeCompilerSelectedPdfPins.Ready,
        NativeRuntimeProfile.LocalCompilerCoursework => NativeCompilerCourseworkPins.Ready,
        NativeRuntimeProfile.LocalCompilerDviGraphics => NativeCompilerDviGraphicsPins.Ready,
        NativeRuntimeProfile.LocalCompilerPdfOrigins => NativeCompilerPdfOriginPins.Ready,
        NativeRuntimeProfile.LocalCompilerPageLabels => NativeCompilerPageLabelPins.Ready,
        _ => false
    };

    public static bool MatchesRuntime(NativeRuntimeProfile profile, string? identity) => Supports(profile)
        && (profile switch
        {
            Profile => identity == RuntimeIdentity,
            NativeRuntimeProfile.LocalCompilerCoursework => identity == NativeCompilerCourseworkPins.RuntimeIdentity,
            NativeRuntimeProfile.LocalCompilerDviGraphics => identity == NativeCompilerDviGraphicsPins.RuntimeIdentity,
            NativeRuntimeProfile.LocalCompilerPdfOrigins => identity == NativeCompilerPdfOriginPins.RuntimeIdentity,
            NativeRuntimeProfile.LocalCompilerPageLabels => identity is NativeCompilerPageLabelPins.RuntimeIdentity
                or NativeCompilerTermesPins.RuntimeIdentity or NativeCompilerCompatibleFontPins.RuntimeIdentity or NativeCompilerLuaFontPins.RuntimeIdentity,
            _ => false
        });

    public static bool IsSelectedPdfPhase(NativePhaseTag tag) =>
        tag is { Version: NativeBiberCompilerContract.PdfPhase, EngineId: "pdftex", Tool: "latex" }
        && MatchesRuntime(tag.Profile, tag.RuntimeIdentity);

    public static int PhaseOutputBytes(NativePhaseTag tag, int configuredLimit) => Supports(tag.Profile)
        ? Math.Min(configuredLimit, IsSelectedPdfPhase(tag) ? SelectedPdfBytes : OtherOutputBytes)
        : configuredLimit;

    public static int FileOutputBytes(NativePhaseRequest request, string outputName)
    {
        int ceiling = PhaseOutputBytes(request.Tag, request.MaxOutputBytes);
        if (ceiling <= OtherOutputBytes || !Supports(request.Tag.Profile)) return ceiling;
        if (request.SelectedRoot is not { } root || !root.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            return OtherOutputBytes;
        string leaf = root[(root.LastIndexOf('/') + 1)..^4];
        return leaf.Length > 0 && outputName == leaf + ".pdf" ? ceiling : OtherOutputBytes;
    }

    public static long PhaseAggregateBytes(NativePhaseRequest request, int configuredLimit) =>
        2L * (Supports(request.Tag.Profile) ? Math.Min(OtherOutputBytes, request.MaxOutputBytes) : configuredLimit);
}
