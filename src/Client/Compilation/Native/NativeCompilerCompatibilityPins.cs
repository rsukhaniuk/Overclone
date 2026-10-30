namespace Client.Compilation.Native;

/// <summary>Root-owned promotion hook for the closed expanded package/font suite.
/// Root-reviewed complete closure and genuine four-engine formats.
/// Never infer trust from selected metadata or permit an executable replacement.
/// Executable/converter/query bytes remain the accepted profile5 components.</summary>
public static class NativeCompilerCompatibilityPins
{
    public static bool Ready => true;
    public const int ResourceCount = 3702, TotalBytes = 188969018;
    public const string ClosureHash = "ed990e9baa0ab9776d6cc9f2c9bcd8aed3c1f863cd4c871d7fba633c9fd6a7ac";
    public const int PdfFormatLength = 22403541, DviFormatLength = 22404397, XeFormatLength = 33969792, LuaFormatLength = 12437911;
    public const string PdfFormatHash = "6372ff155080eec70f023c29b15a1e71cf7c150bf50dce765fad322776e6b6a1", DviFormatHash = "fb304aa6c090d9708f3740cd348776d8eb7b3df97b9d4a1edab152ffcf01e907", XeFormatHash = "3cc626d0de5d29633ec713f1392b32d8ffef7dca535bee3d7235a06b7601b65a", LuaFormatHash = "e05a14512add21f7781ff071af6bb4bf8a392269deac02feb5baf990ea4e63dc";

    public static bool MatchesComponents(IReadOnlyDictionary<string, NativeResourcePin> pins)
    {
        return Ready && ValidSize(ResourceCount, NativeCompilerSuiteContract.Files)
            && ValidSize(TotalBytes, NativeCompilerSuiteContract.TotalBytes) && Hex(ClosureHash)
            && Fixed("engine.js", NativeRuntimeProfiles.SyncTeXCompilerLoaderLength, NativeRuntimeProfiles.SyncTeXCompilerLoaderHash)
            && Fixed("engine.wasm", NativeRuntimeProfiles.SyncTeXCompilerWasmLength, NativeRuntimeProfiles.SyncTeXCompilerWasmHash)
            && Fixed("synctex-query.js", NativeRuntimeProfiles.SyncTeXQueryLoaderLength, NativeRuntimeProfiles.SyncTeXQueryLoaderHash)
            && Fixed("synctex-query.wasm", NativeRuntimeProfiles.SyncTeXQueryWasmLength, NativeRuntimeProfiles.SyncTeXQueryWasmHash)
            && Fixed("xetex.js", NativeCompilerSuitePins.XeLoaderLength, NativeCompilerSuitePins.XeLoaderHash)
            && Fixed("xetex.wasm", NativeCompilerSuitePins.XeWasmLength, NativeCompilerSuitePins.XeWasmHash)
            && Fixed("luatex.js", NativeCompilerSuitePins.LuaLoaderLength, NativeCompilerSuitePins.LuaLoaderHash)
            && Fixed("luatex.wasm", NativeCompilerSuitePins.LuaWasmLength, NativeCompilerSuitePins.LuaWasmHash)
            && Fixed("dvi-converter.js", NativeCompilerSuitePins.ConverterLoaderLength, NativeCompilerSuitePins.ConverterLoaderHash)
            && Fixed("dvi-converter.wasm", NativeCompilerSuitePins.ConverterWasmLength, NativeCompilerSuitePins.ConverterWasmHash)
            && Fixed("pdflatex.fmt", PdfFormatLength, PdfFormatHash)
            && Fixed("latex-dvi.fmt", DviFormatLength, DviFormatHash)
            && Fixed("xelatex.fmt", XeFormatLength, XeFormatHash)
            && Fixed("lualatex.fmt", LuaFormatLength, LuaFormatHash);
        bool Fixed(string name, int length, string hash) => length > 0 && Hex(hash)
            && pins.TryGetValue(name, out var pin) && pin.Length == length && pin.Sha256 == hash;
    }
    private static bool ValidSize(int value, int maximum) => value > 0 && value <= maximum;
    private static bool Hex(string value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
