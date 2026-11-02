using Client.Compilation.SyncTeX;

namespace Client.Compilation.Native;

/// <summary>Closed candidate cells and correlation DATA, never execution authority.</summary>
public static class NativeNavigationContract
{
    public const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalCompilerNavigation;
    public const int DviPhase = 12, LuaPhase = 14;
    public const string LimitationCode = "native-navigation-unavailable";
    public const string LimitationMessage = "The PDF is available, but measured source navigation is unavailable for this output.";
    public static bool IsNavigationProfile(NativeRuntimeProfile profile) => profile is Profile or NativeXeNavigationContract.Profile or NativeGraphicsNavigationContract.Profile;
    public static bool IsMapped(NativeRuntimeProfile profile, int phase, NativeCompilerEngine engine) => profile == NativeGraphicsNavigationContract.Profile
        ? engine != NativeCompilerEngine.PdfLaTeX && NativeCompilerSuiteContract.Phase(profile, engine) != 0 && phase == NativeCompilerSuiteContract.Phase(profile, engine)
        : IsNavigationProfile(profile) && (phase == DviPhase && engine == NativeCompilerEngine.LaTeXDvi || phase == LuaPhase && engine == NativeCompilerEngine.LuaLaTeX
            || profile == NativeXeNavigationContract.Profile && phase == NativeXeNavigationContract.Phase && engine == NativeCompilerEngine.XeLaTeX);
    public static NativeCompilerComponent Component(NativeCompilerEngine engine)
    {
        var prior = NativeCompilerComponents.For(NativeRuntimeProfile.LocalCompilerBibliography, engine);
        return engine switch
        {
            NativeCompilerEngine.LaTeXDvi => prior with
            {
                LoaderName = "dvi-map-engine.js", LoaderLength = NativeCompilerNavigationPins.DviLoaderLength, LoaderHash = NativeCompilerNavigationPins.DviLoaderHash,
                WasmName = "dvi-map-engine.wasm", WasmLength = NativeCompilerNavigationPins.DviWasmLength, WasmHash = NativeCompilerNavigationPins.DviWasmHash,
                EngineVersion = "dvi-maps-abi1", NativeAbiVersion = 1, SupportsSourceMap = true,
                OutputAdapter = new("native-latex-dvi", "geometry-abi2", new(NativeCompilerNavigationPins.ConverterWasmHash))
            },
            NativeCompilerEngine.LuaLaTeX => prior with
            {
                LoaderLength = NativeCompilerNavigationPins.LuaLoaderLength, LoaderHash = NativeCompilerNavigationPins.LuaLoaderHash,
                WasmLength = NativeCompilerNavigationPins.LuaWasmLength, WasmHash = NativeCompilerNavigationPins.LuaWasmHash,
                EngineVersion = "abi5", NativeAbiVersion = 5, SupportsSourceMap = true,
                OutputAdapter = new("native-luatex", "abi5", new(NativeCompilerNavigationPins.LuaWasmHash))
            },
            _ => prior
        };
    }
    internal static SourceMapGeometryReadResult Assemble(NativeCompilerEngine engine, NativePhaseTag finalTag,
        byte[]? sidecar, byte[]? map, byte[] pdf, byte[]? intermediate, CompileOutputBudget budget)
        => SourceMapGeometryCodec.AssembleData(engine switch
            { NativeCompilerEngine.LaTeXDvi => "latex-dvi", NativeCompilerEngine.LuaLaTeX => "lualatex", NativeCompilerEngine.XeLaTeX => "xelatex", _ => "" }, 
            checked((uint)finalTag.Ordinal), sidecar, map, pdf, intermediate, budget);
}
