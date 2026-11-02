namespace Client.Compilation.Native;

/// <summary>Explicit candidate only. These bytes do not change default adoption.</summary>
public static class NativeCompilerNavigationPins
{
    public static bool Ready => true;
    public const int ResourceCount = 3707, TotalBytes = 191844694;
    public const string ClosureHash = "85448c892b86acdb232ed51079db3a686d2c06bdf703572fccbcdc7fbc49adc0";
    public const int LuaLoaderLength = 192894, LuaWasmLength = 5188347;
    public const string LuaLoaderHash = "7282eb2decde52603417b94d363a6d3258e8ef1451a0d20847bee70084c70a21";
    public const string LuaWasmHash = "1f5b9ca7742693299f1074c47e2a3b91d1df2d6c0381d5c4d1a628965cde542e";
    public const int DviLoaderLength = 141042, DviWasmLength = 1946841;
    public const string DviLoaderHash = "980bcf858794dc4679124df6fbf76a503d3020d96a7081537e9d1a4a5e7bb6f1";
    public const string DviWasmHash = "6ad6db6625d28bdfaf83eba48b44a4202582fd84118c1e8ece3857535722a1b9";
    public const int ConverterLoaderLength = 12960, ConverterWasmLength = 690388;
    public const string ConverterLoaderHash = "d79dca5dd87ab27ecb7dcc63cc96171092ea6a57c43054e4cabcf39a0f5c8c72";
    public const string ConverterWasmHash = "fb6d4da9ee438d66d89a7a0ec36ab7dd243ea2a5c32c4a426179b935e383ca84";
    public static bool MatchesComponents(IReadOnlyDictionary<string, NativeResourcePin> pins)
    {
        // Reuse the exact profile7 component check with only Lua replaced. The
        // full independent closure hash separately binds every resource.
        var legacy = new Dictionary<string, NativeResourcePin>(pins, StringComparer.Ordinal)
        {
            ["luatex.js"] = new("luatex.js", NativeCompilerBibliographyPins.LuaLoaderLength, NativeCompilerBibliographyPins.LuaLoaderHash),
            ["luatex.wasm"] = new("luatex.wasm", NativeCompilerBibliographyPins.LuaWasmLength, NativeCompilerBibliographyPins.LuaWasmHash)
        };
        return NativeCompilerBibliographyPins.MatchesComponents(legacy)
            && Fixed("luatex.js", LuaLoaderLength, LuaLoaderHash) && Fixed("luatex.wasm", LuaWasmLength, LuaWasmHash)
            && Fixed("dvi-map-engine.js", DviLoaderLength, DviLoaderHash) && Fixed("dvi-map-engine.wasm", DviWasmLength, DviWasmHash)
            && Fixed("dvi-map-converter.js", ConverterLoaderLength, ConverterLoaderHash) && Fixed("dvi-map-converter.wasm", ConverterWasmLength, ConverterWasmHash);
        bool Fixed(string name, int length, string hash) => pins.TryGetValue(name, out var p) && p.Length == length && p.Sha256 == hash;
    }
}
