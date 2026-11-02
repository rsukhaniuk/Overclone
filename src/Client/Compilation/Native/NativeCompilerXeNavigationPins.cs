namespace Client.Compilation.Native;

/// <summary>Exact opt-in P9 candidate, not default adoption or redistribution approval.</summary>
public static class NativeCompilerXeNavigationPins
{
    public static bool Ready => true;
    public const int ResourceCount = 3709, TotalBytes = 192565170;
    public const string ClosureHash = "e82466563a5865fb5c400b3fbda82518f709a78ed40ae6d8289998e75fbeb9f7";
    public const int LoaderLength = 69888, WasmLength = 3302044, FormatLength = 33969814;
    public const string LoaderHash = "a2d90034e871d75d48596b26a5856ac6eed86b6396bd563e610b0ffa7a6be21c";
    public const string WasmHash = "61ef6fe76724c7bd9532f05224d03829e40393c61acd68da0fd0589d8e60c7c2";
    public const string FormatHash = "dc97f676666e6c04f6eb4552c26cfdbff009817bac98dc4ed2325245b48a5637";
    public const int ConverterLoaderLength = 12960, ConverterWasmLength = 690350;
    public const string ConverterLoaderHash = "d79dca5dd87ab27ecb7dcc63cc96171092ea6a57c43054e4cabcf39a0f5c8c72";
    public const string ConverterWasmHash = "10031d2748be3f6fa6ba5bc99fd4ef597f06f66208c5b9a5aeba60d5dc3aecac";
    public static bool MatchesComponents(IReadOnlyDictionary<string, NativeResourcePin> pins)
    {
        var previous = new Dictionary<string, NativeResourcePin>(pins, StringComparer.Ordinal)
        {
            ["xetex.js"] = new("xetex.js", NativeCompilerSuitePins.XeLoaderLength, NativeCompilerSuitePins.XeLoaderHash),
            ["xetex.wasm"] = new("xetex.wasm", NativeCompilerSuitePins.XeWasmLength, NativeCompilerSuitePins.XeWasmHash),
            ["xelatex.fmt"] = new("xelatex.fmt", NativeCompilerCompatibilityPins.XeFormatLength, NativeCompilerCompatibilityPins.XeFormatHash)
        };
        return NativeCompilerNavigationPins.MatchesComponents(previous)
            && Fixed("xetex.js", LoaderLength, LoaderHash) && Fixed("xetex.wasm", WasmLength, WasmHash)
            && Fixed("xelatex.fmt", FormatLength, FormatHash)
            && Fixed("xdv-map-converter.js", ConverterLoaderLength, ConverterLoaderHash)
            && Fixed("xdv-map-converter.wasm", ConverterWasmLength, ConverterWasmHash);
        bool Fixed(string name, int length, string hash) => pins.TryGetValue(name, out var p) && p.Length == length && p.Sha256 == hash;
    }
}
