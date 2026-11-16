namespace Client.Compilation.Native;

/// <summary>Closed P15 origin/neutral derivatives and area64 query closure.
/// Runtime admission is separate from default adoption; P14 is immutable.</summary>
public static class NativeCompilerOriginPins
{
    public static bool Ready => true;
    public const int ResourceCount = 4453, TotalBytes = 261178860;
    public const string ClosureHash = "e20e5f5e14f2088d4c8e6718566824b9ce0911120be519386f09709f292f5e03";
    public const int QueryLoaderLength = 59432, QueryWasmLength = 191680;
    public const string QueryLoaderHash = "42f9467bb0d03dc488decab6baec00c2a78f4f524dc18a5a1b912ca55cdcaafd";
    public const string QueryWasmHash = "974212d6edbdad96e9733d854e427883a7b16fc09202e8468baa9d3cdda410b8";
    // Source-built origin1/neutral1 components accepted as one exact closure.
    public const int LuaLoaderLength = 193740, LuaWasmLength = 6026492;
    public const string LuaLoaderHash = "6ea9b1b9b32eaddc37492e90f09770345568d79277a14933c3d96e553efd13b9";
    public const string LuaWasmHash = "0720b4da5ea2d99dfc3455d8b15a98c7b22fa7c3880944dc078d4cb1fec7e29a";
    public const int DviConverterLoaderLength = 13396, DviConverterWasmLength = 697299;
    public const string DviConverterLoaderHash = "2aafbfe08a1a5a05d7fdb1980daf56781cadb67fbb16f1444d79898ffe956be9";
    public const string DviConverterWasmHash = "1d2bed683ca42adc712de9c4649b26a6bb7001451cc00915d46b213fe81e304d";
    public const int XeConverterLoaderLength = 13152, XeConverterWasmLength = 696011;
    public const string XeConverterLoaderHash = "c6feeaac32a9d23d03720de9c0d977506d5186b397c9f3ff221343e25ad3dd89";
    public const string XeConverterWasmHash = "182fa124810d48d1dbfe17ee82c3deebdeeb6fec8838c590a2b445389d2224d1";

    public static bool MatchesComponents(IReadOnlyDictionary<string, NativeResourcePin> pins)
    {
        if (!Ready) return false;
        var previous = new Dictionary<string, NativeResourcePin>(pins, StringComparer.Ordinal)
        {
            ["luatex.js"] = new("luatex.js", NativeCompilerThesisPins.LuaLoaderLength, NativeCompilerThesisPins.LuaLoaderHash),
            ["luatex.wasm"] = new("luatex.wasm", NativeCompilerThesisPins.LuaWasmLength, NativeCompilerThesisPins.LuaWasmHash),
            ["dvi-map-converter.js"] = new("dvi-map-converter.js", NativeCompilerBiberPins.ConverterLoaderLength, NativeCompilerBiberPins.ConverterLoaderHash),
            ["dvi-map-converter.wasm"] = new("dvi-map-converter.wasm", NativeCompilerBiberPins.ConverterWasmLength, NativeCompilerBiberPins.ConverterWasmHash),
            ["xdv-map-converter.js"] = new("xdv-map-converter.js", NativeCompilerGraphicsNavigationPins.ConverterLoaderLength, NativeCompilerGraphicsNavigationPins.ConverterLoaderHash),
            ["xdv-map-converter.wasm"] = new("xdv-map-converter.wasm", NativeCompilerGraphicsNavigationPins.XeWasmLength, NativeCompilerGraphicsNavigationPins.XeWasmHash),
            ["synctex-query.js"] = new("synctex-query.js", NativeRuntimeProfiles.SyncTeXQueryLoaderLength, NativeRuntimeProfiles.SyncTeXQueryLoaderHash),
            ["synctex-query.wasm"] = new("synctex-query.wasm", NativeRuntimeProfiles.SyncTeXQueryWasmLength, NativeRuntimeProfiles.SyncTeXQueryWasmHash)
        };
        return NativeCompilerThesisPins.MatchesComponents(previous)
            && Fixed("luatex.js", LuaLoaderLength, LuaLoaderHash) && Fixed("luatex.wasm", LuaWasmLength, LuaWasmHash)
            && Fixed("dvi-map-converter.js", DviConverterLoaderLength, DviConverterLoaderHash) && Fixed("dvi-map-converter.wasm", DviConverterWasmLength, DviConverterWasmHash)
            && Fixed("xdv-map-converter.js", XeConverterLoaderLength, XeConverterLoaderHash) && Fixed("xdv-map-converter.wasm", XeConverterWasmLength, XeConverterWasmHash)
            && Fixed("synctex-query.js", QueryLoaderLength, QueryLoaderHash) && Fixed("synctex-query.wasm", QueryWasmLength, QueryWasmHash);
        bool Fixed(string name, int length, string hash) => pins.TryGetValue(name, out var pin) && pin.Length == length && pin.Sha256 == hash;
    }
}
