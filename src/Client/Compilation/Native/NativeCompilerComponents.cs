namespace Client.Compilation.Native;

/// <summary>Per-profile executable/format facts. A format-only upgrade must never
/// accidentally select the adapter hash of a different executable.</summary>
public sealed record NativeCompilerComponent(string LoaderName, int LoaderLength, string LoaderHash,
    string WasmName, int WasmLength, string WasmHash, string FormatName, int FormatLength, string FormatHash,
    string EngineVersion, int? NativeAbiVersion, CompileOutputDescriptor OutputAdapter,
    string QueryLoaderHash, string QueryWasmHash, bool SupportsSourceMap);

public static class NativeCompilerComponents
{
    public static NativeCompilerComponent For(NativeRuntimeProfile profile, NativeCompilerEngine engine)
    {
        if (!NativeRuntimeProfiles.IsSuite(profile)) throw new ArgumentOutOfRangeException(nameof(profile));
        if (profile == NativeBiberCompilerContract.Profile) return NativeBiberCompilerContract.Component(engine);
        if (profile == NativeUnicodeCompilerContract.Profile) return NativeUnicodeCompilerContract.Component(engine);
        if (profile == NativeGraphicsNavigationContract.Profile) return NativeGraphicsNavigationContract.Component(engine);
        if (profile == NativeXeNavigationContract.Profile) return NativeXeNavigationContract.Component(engine);
        if (profile == NativeNavigationContract.Profile) return NativeNavigationContract.Component(engine);
        bool expanded = profile is NativeRuntimeProfile.LocalCompilerCompatibility or NativeRuntimeProfile.LocalCompilerBibliography;
        bool bibliography = profile == NativeRuntimeProfile.LocalCompilerBibliography;
        var converter = new Client.Workspace.Domain.SHA256(NativeCompilerSuitePins.ConverterWasmHash);
        return engine switch
        {
            NativeCompilerEngine.PdfLaTeX => Component("engine", NativeRuntimeProfiles.SyncTeXCompilerLoaderLength,
                NativeRuntimeProfiles.SyncTeXCompilerLoaderHash, NativeRuntimeProfiles.SyncTeXCompilerWasmLength,
                NativeRuntimeProfiles.SyncTeXCompilerWasmHash, "pdflatex.fmt",
                expanded ? NativeCompilerCompatibilityPins.PdfFormatLength : NativeRuntimeProfiles.SyncTeXFormatLength,
                expanded ? NativeCompilerCompatibilityPins.PdfFormatHash : NativeRuntimeProfiles.SyncTeXFormatHash,
                NativeRuntimePackage.EngineVersion, null,
                new("native-pdftex", "n11-synctex-v1", new(NativeRuntimeProfiles.SyncTeXCompilerWasmHash)), true),
            NativeCompilerEngine.LaTeXDvi => Component("engine", NativeRuntimeProfiles.SyncTeXCompilerLoaderLength,
                NativeRuntimeProfiles.SyncTeXCompilerLoaderHash, NativeRuntimeProfiles.SyncTeXCompilerWasmLength,
                NativeRuntimeProfiles.SyncTeXCompilerWasmHash, "latex-dvi.fmt",
                expanded ? NativeCompilerCompatibilityPins.DviFormatLength : NativeRuntimeProfiles.DviFormatLength,
                expanded ? NativeCompilerCompatibilityPins.DviFormatHash : NativeRuntimeProfiles.DviFormatHash,
                NativeRuntimePackage.EngineVersion, null, new("native-latex-dvi", "dvi-v1", converter), false),
            NativeCompilerEngine.XeLaTeX => Component("xetex", NativeCompilerSuitePins.XeLoaderLength,
                NativeCompilerSuitePins.XeLoaderHash, NativeCompilerSuitePins.XeWasmLength, NativeCompilerSuitePins.XeWasmHash,
                "xelatex.fmt", expanded ? NativeCompilerCompatibilityPins.XeFormatLength : NativeCompilerSuitePins.XeFormatLength,
                expanded ? NativeCompilerCompatibilityPins.XeFormatHash : NativeCompilerSuitePins.XeFormatHash,
                "native-v1", null, new("native-xetex", "xdv-v1", converter), false),
            NativeCompilerEngine.LuaLaTeX => Component("luatex",
                bibliography ? NativeCompilerBibliographyPins.LuaLoaderLength : NativeCompilerSuitePins.LuaLoaderLength,
                bibliography ? NativeCompilerBibliographyPins.LuaLoaderHash : NativeCompilerSuitePins.LuaLoaderHash,
                bibliography ? NativeCompilerBibliographyPins.LuaWasmLength : NativeCompilerSuitePins.LuaWasmLength,
                bibliography ? NativeCompilerBibliographyPins.LuaWasmHash : NativeCompilerSuitePins.LuaWasmHash,
                "lualatex.fmt", expanded ? NativeCompilerCompatibilityPins.LuaFormatLength : NativeCompilerSuitePins.LuaFormatLength,
                expanded ? NativeCompilerCompatibilityPins.LuaFormatHash : NativeCompilerSuitePins.LuaFormatHash,
                bibliography ? "abi3" : "abi2", bibliography ? 3 : 2,
                new("native-luatex", bibliography ? "abi3" : "abi2",
                    new(bibliography ? NativeCompilerBibliographyPins.LuaWasmHash : NativeCompilerSuitePins.LuaWasmHash)), false),
            _ => throw new ArgumentOutOfRangeException(nameof(engine))
        };
    }

    private static NativeCompilerComponent Component(string stem, int loaderLength, string loaderHash,
        int wasmLength, string wasmHash, string format, int formatLength, string formatHash,
        string version, int? abi, CompileOutputDescriptor adapter, bool mapping) =>
        new(stem + ".js", loaderLength, loaderHash, stem + ".wasm", wasmLength, wasmHash,
            format, formatLength, formatHash, version, abi, adapter,
            NativeRuntimeProfiles.SyncTeXQueryLoaderHash, NativeRuntimeProfiles.SyncTeXQueryWasmHash, mapping);
}
