namespace Client.Compilation.Native;

/// <summary>Closed opt-in graphics1 candidate. No default or license approval.</summary>
public static class NativeCompilerGraphicsNavigationPins
{
    public static bool Ready => true;
    public const int ResourceCount = 3709, TotalBytes = 192577312;
    public const string ClosureHash = "d91e3ff8d2c7717344d6c954efc8172ac0296223ece425df7ec00aff73db20cf";
    public const int LuaLoaderLength = 193012, LuaWasmLength = 5198606;
    public const string LuaLoaderHash = "6fb762c18ea4719dd2157d5950303a5f498ad511df0665cca54af141ff0882b5";
    public const string LuaWasmHash = "220999882c2ddb4057b6d798f21785fce35accb4c63c0436dfa62af9f3987dbc";
    public const int ConverterLoaderLength = 13026, DviWasmLength = 691182, XeWasmLength = 691189;
    public const string ConverterLoaderHash = "a961144a37546755175dfda0687c22bb79e67ebef495aa84c3922b01af6f2c57";
    public const string DviWasmHash = "53bd8a8a98c509afa8b0e0e5451bf78d7e9d765da1c8f513d9c1a909e9d5270d";
    public const string XeWasmHash = "ca030fe4077fc02338e4d92cdc5c0ef1b89c73bcb629b9d44f59708993fa15ef";
    public static bool MatchesComponents(IReadOnlyDictionary<string, NativeResourcePin> pins)
    {
        var previous = new Dictionary<string, NativeResourcePin>(pins, StringComparer.Ordinal)
        {
            ["luatex.js"] = new("luatex.js", NativeCompilerNavigationPins.LuaLoaderLength, NativeCompilerNavigationPins.LuaLoaderHash),
            ["luatex.wasm"] = new("luatex.wasm", NativeCompilerNavigationPins.LuaWasmLength, NativeCompilerNavigationPins.LuaWasmHash),
            ["dvi-map-converter.js"] = new("dvi-map-converter.js", NativeCompilerNavigationPins.ConverterLoaderLength, NativeCompilerNavigationPins.ConverterLoaderHash),
            ["dvi-map-converter.wasm"] = new("dvi-map-converter.wasm", NativeCompilerNavigationPins.ConverterWasmLength, NativeCompilerNavigationPins.ConverterWasmHash),
            ["xdv-map-converter.js"] = new("xdv-map-converter.js", NativeCompilerXeNavigationPins.ConverterLoaderLength, NativeCompilerXeNavigationPins.ConverterLoaderHash),
            ["xdv-map-converter.wasm"] = new("xdv-map-converter.wasm", NativeCompilerXeNavigationPins.ConverterWasmLength, NativeCompilerXeNavigationPins.ConverterWasmHash)
        };
        return NativeCompilerXeNavigationPins.MatchesComponents(previous)
            && Fixed("luatex.js", LuaLoaderLength, LuaLoaderHash) && Fixed("luatex.wasm", LuaWasmLength, LuaWasmHash)
            && Fixed("dvi-map-converter.js", ConverterLoaderLength, ConverterLoaderHash) && Fixed("dvi-map-converter.wasm", DviWasmLength, DviWasmHash)
            && Fixed("xdv-map-converter.js", ConverterLoaderLength, ConverterLoaderHash) && Fixed("xdv-map-converter.wasm", XeWasmLength, XeWasmHash);
        bool Fixed(string name, int length, string hash) => pins.TryGetValue(name, out var p) && p.Length == length && p.Sha256 == hash;
    }
}
