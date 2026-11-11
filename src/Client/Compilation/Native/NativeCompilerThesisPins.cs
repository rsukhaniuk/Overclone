namespace Client.Compilation.Native;

/// <summary>Closed P14 candidate. Final native closure pins are required before readiness.</summary>
public static class NativeCompilerThesisPins
{
    public static bool Ready => true;
    public const int ResourceCount = 4453, TotalBytes = 261154796;
    public const string ClosureHash = "f0c9ff2492565b273130a4596ea608d646728cacf5dd73294c88e0d03c6e86a6";
    public const int XeLoaderLength = 70018, XeWasmLength = 3302304;
    public const string XeLoaderHash = "3969fa53aa5b3cb42ca3b8cea1adb6e841d83c897f41d2d0b66f352d7d1142f7";
    public const string XeWasmHash = "d2886ff867abc73386b733839f5e0a9b9855723cc3b19aa8686649fd0c3139c8";
    public const int LuaLoaderLength = 193516, LuaWasmLength = 6013393;
    public const string LuaLoaderHash = "0cba8f1f2f5a71d703fe80300521dbc4c21d54d016845fa2181cc77f666bb224";
    public const string LuaWasmHash = "406aa23eb3c799b7c1cc0cbb3fa46ab1681880705ed30de35dd288c14576a4d9";
    public const int LuaHarfResourceLength = 45083;
    public const string LuaHarfResourceHash = "726563b3936eb23badd8fdafdf6e8d901f96e73aa9f3970987ce614bfc9b6b0b";

    public static bool MatchesComponents(IReadOnlyDictionary<string, NativeResourcePin> pins)
    {
        if (!Ready) return false;
        var priorXe = NativeBiberCompilerContract.Component(NativeCompilerEngine.XeLaTeX);
        var previous = new Dictionary<string, NativeResourcePin>(pins, StringComparer.Ordinal)
        {
            ["xetex.js"] = new("xetex.js", priorXe.LoaderLength, priorXe.LoaderHash),
            ["xetex.wasm"] = new("xetex.wasm", priorXe.WasmLength, priorXe.WasmHash),
            ["luatex.js"] = new("luatex.js", NativeCompilerBiberPins.LuaLoaderLength, NativeCompilerBiberPins.LuaLoaderHash),
            ["luatex.wasm"] = new("luatex.wasm", NativeCompilerBiberPins.LuaWasmLength, NativeCompilerBiberPins.LuaWasmHash),
            ["luaotfload-harf-plug.lua"] = new("luaotfload-harf-plug.lua", NativeCompilerBiberPins.LuaHarfResourceLength, NativeCompilerBiberPins.LuaHarfResourceHash)
        };
        return NativeCompilerBiberPins.MatchesComponents(previous)
            && Fixed("xetex.js", XeLoaderLength, XeLoaderHash) && Fixed("xetex.wasm", XeWasmLength, XeWasmHash)
            && Fixed("luatex.js", LuaLoaderLength, LuaLoaderHash) && Fixed("luatex.wasm", LuaWasmLength, LuaWasmHash)
            && Fixed("luaotfload-harf-plug.lua", LuaHarfResourceLength, LuaHarfResourceHash);
        bool Fixed(string name, int length, string hash) => pins.TryGetValue(name, out var pin) && pin.Length == length && pin.Sha256 == hash;
    }
}
