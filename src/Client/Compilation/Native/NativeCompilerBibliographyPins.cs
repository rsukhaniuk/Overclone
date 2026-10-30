namespace Client.Compilation.Native;

/// <summary>Closed ABI3 Lua/biblatex upgrade. Profiles0-6 retain their exact bytes.
/// Technical identity only; not source/license or isolation clearance.</summary>
public static class NativeCompilerBibliographyPins
{
    // The exact local ABI3 pair and closed data inventory are independently reviewed.
    // Application/browser acceptance is a separate gate recorded in verification.
    public static bool Ready => true;
    public const int ResourceCount = 3703, TotalBytes = 189047750;
    public const string ClosureHash = "947c4d2f0b203b394b8e10f992cf240782ee757b10a2640704a80aba74b8edae";
    public const int LuaLoaderLength = 192040, LuaWasmLength = 5183488, BstLength = 65484;
    public const string LuaLoaderHash = "c2d81fd216070379b5c3559713508b0d9e439ede1669c74d865a1c2ddda8ca53";
    public const string LuaWasmHash = "71155dff296f4741f6d711f758bdebe25acc82f1ecd4a26034fc4b4a288c1153";
    public const string BstHash = "c290df6e27d9265172db9816a87063c09365c84e9f0eea4c656b28237db9bd05";

    public static bool MatchesComponents(IReadOnlyDictionary<string, NativeResourcePin> pins)
    {
        return Ready
        && Fixed("engine.js", NativeRuntimeProfiles.SyncTeXCompilerLoaderLength, NativeRuntimeProfiles.SyncTeXCompilerLoaderHash)
        && Fixed("engine.wasm", NativeRuntimeProfiles.SyncTeXCompilerWasmLength, NativeRuntimeProfiles.SyncTeXCompilerWasmHash)
        && Fixed("synctex-query.js", NativeRuntimeProfiles.SyncTeXQueryLoaderLength, NativeRuntimeProfiles.SyncTeXQueryLoaderHash)
        && Fixed("synctex-query.wasm", NativeRuntimeProfiles.SyncTeXQueryWasmLength, NativeRuntimeProfiles.SyncTeXQueryWasmHash)
        && Fixed("xetex.js", NativeCompilerSuitePins.XeLoaderLength, NativeCompilerSuitePins.XeLoaderHash)
        && Fixed("xetex.wasm", NativeCompilerSuitePins.XeWasmLength, NativeCompilerSuitePins.XeWasmHash)
        && Fixed("luatex.js", LuaLoaderLength, LuaLoaderHash)
        && Fixed("luatex.wasm", LuaWasmLength, LuaWasmHash)
        && Fixed("dvi-converter.js", NativeCompilerSuitePins.ConverterLoaderLength, NativeCompilerSuitePins.ConverterLoaderHash)
        && Fixed("dvi-converter.wasm", NativeCompilerSuitePins.ConverterWasmLength, NativeCompilerSuitePins.ConverterWasmHash)
        && Fixed("pdflatex.fmt", NativeCompilerCompatibilityPins.PdfFormatLength, NativeCompilerCompatibilityPins.PdfFormatHash)
        && Fixed("latex-dvi.fmt", NativeCompilerCompatibilityPins.DviFormatLength, NativeCompilerCompatibilityPins.DviFormatHash)
        && Fixed("xelatex.fmt", NativeCompilerCompatibilityPins.XeFormatLength, NativeCompilerCompatibilityPins.XeFormatHash)
        && Fixed("lualatex.fmt", NativeCompilerCompatibilityPins.LuaFormatLength, NativeCompilerCompatibilityPins.LuaFormatHash)
        && Fixed("biblatex.bst", BstLength, BstHash);

        bool Fixed(string name, int length, string hash) =>
            pins.TryGetValue(name, out var pin) && pin.Length == length && pin.Sha256 == hash;
    }
}
