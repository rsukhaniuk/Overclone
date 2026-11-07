namespace Client.Compilation.Native;

/// <summary>Only P12 selects the proved non-BMP Xe and genuine LuaHB candidates.</summary>
public static class NativeUnicodeCompilerContract
{
    public const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalCompilerUnicode;
    public const int XePhase = 21, LuaPhase = 22;
    public const int LuaResourceBytes = 128 * 1024 * 1024, LuaWorkBytes = 32 * 1024 * 1024;
    public const int LuaAggregateInputBytes = 160 * 1024 * 1024;
    // Trusted configured ports only. Execution still rebinds plan/profile/package.
    public static bool MatchesXeSelection(CompilationDescriptor engine, IEnumerable<CompilationOption> options)
        => engine == new CompilationDescriptor("xelatex", "maps-abi1-nonbmp1", new(NativeCompilerUnicodePins.XeWasmHash))
            && NativeCompilerSuiteContract.MatchesOptions(options, Profile, NativeCompilerEngine.XeLaTeX);
    public static NativeCompilerComponent Component(NativeCompilerEngine engine)
    {
        var previous = NativeGraphicsNavigationContract.Component(engine);
        return engine switch
        {
            NativeCompilerEngine.XeLaTeX => previous with
            {
                LoaderLength = NativeCompilerUnicodePins.XeLoaderLength, LoaderHash = NativeCompilerUnicodePins.XeLoaderHash,
                WasmLength = NativeCompilerUnicodePins.XeWasmLength, WasmHash = NativeCompilerUnicodePins.XeWasmHash,
                EngineVersion = "maps-abi1-nonbmp1"
            },
            NativeCompilerEngine.LuaLaTeX => previous with
            {
                LoaderLength = NativeCompilerUnicodePins.LuaLoaderLength, LoaderHash = NativeCompilerUnicodePins.LuaLoaderHash,
                WasmLength = NativeCompilerUnicodePins.LuaWasmLength, WasmHash = NativeCompilerUnicodePins.LuaWasmHash,
                FormatLength = NativeCompilerUnicodePins.LuaFormatLength, FormatHash = NativeCompilerUnicodePins.LuaFormatHash,
                EngineVersion = "abi5-graphics1-harfbuzz1-fonts1",
                OutputAdapter = new("native-luatex", "abi5-graphics1-harfbuzz1-fonts1", new(NativeCompilerUnicodePins.LuaWasmHash))
            },
            _ => previous
        };
    }
}
