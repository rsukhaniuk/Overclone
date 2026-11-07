namespace Client.Compilation.Native;

/// <summary>Closed explicit Unicode/shaping candidate; no default or release approval.</summary>
public static class NativeCompilerUnicodePins
{
    public static bool Ready => true;
    public const int ResourceCount = 4186, TotalBytes = 229018228;
    public const string ClosureHash = "2ec3569c2e208ff08f4b7cf66cdc2b808820bc6947ef4c93848136e9a21ff73e";
    public const int XeLoaderLength = 69948, XeWasmLength = 3302272;
    public const string XeLoaderHash = "38ba92890c5de6e957254dcda1c1345a75b35dabaa46d77fc4835f3ad4f33601";
    public const string XeWasmHash = "5a08c4d0a2109adc43e087889be208deb71cf4ba55692b41d58da2c8284f1e8e";
    public const int LuaLoaderLength = 193208, LuaWasmLength = 6013318;
    public const string LuaLoaderHash = "b66ac597a51b06a97598c95db54329005db54ff5631e461e36f1a841bec86d90";
    public const string LuaWasmHash = "7122d44f4d63a4626a3bcc55bb6bb404cfb8d78fd5419dda38849e991e23b7c6";
    public const int LuaFormatLength = 12437864;
    public const string LuaFormatHash = "fed204915ab2eb5b593ee232ae2a484d58e2dc39737b902aefbaf123a2d14e02";
    public const int HarfBuzzLuaLength = 3023;
    public const string HarfBuzzLuaHash = "6afe2357e6d96c656a903965010ebb5e8b11a40ca73cdc9c481cd7d44e56b47d";

    public static bool MatchesComponents(IReadOnlyDictionary<string, NativeResourcePin> pins)
    {
        var oldXe = NativeGraphicsNavigationContract.Component(NativeCompilerEngine.XeLaTeX);
        var oldLua = NativeGraphicsNavigationContract.Component(NativeCompilerEngine.LuaLaTeX);
        var previous = new Dictionary<string, NativeResourcePin>(pins, StringComparer.Ordinal)
        {
            ["xetex.js"] = new("xetex.js", oldXe.LoaderLength, oldXe.LoaderHash),
            ["xetex.wasm"] = new("xetex.wasm", oldXe.WasmLength, oldXe.WasmHash),
            ["luatex.js"] = new("luatex.js", oldLua.LoaderLength, oldLua.LoaderHash),
            ["luatex.wasm"] = new("luatex.wasm", oldLua.WasmLength, oldLua.WasmHash),
            ["lualatex.fmt"] = new("lualatex.fmt", oldLua.FormatLength, oldLua.FormatHash)
        };
        previous.Remove("harfbuzz.lua");
        return NativeCompilerGraphicsNavigationPins.MatchesComponents(previous)
            && Fixed("xetex.js", XeLoaderLength, XeLoaderHash) && Fixed("xetex.wasm", XeWasmLength, XeWasmHash)
            && Fixed("luatex.js", LuaLoaderLength, LuaLoaderHash) && Fixed("luatex.wasm", LuaWasmLength, LuaWasmHash)
            && Fixed("lualatex.fmt", LuaFormatLength, LuaFormatHash) && Fixed("harfbuzz.lua", HarfBuzzLuaLength, HarfBuzzLuaHash);
        bool Fixed(string name, int length, string hash) => pins.TryGetValue(name, out var p) && p.Length == length && p.Sha256 == hash;
    }
}
