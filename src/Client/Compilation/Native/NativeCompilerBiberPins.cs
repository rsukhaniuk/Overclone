namespace Client.Compilation.Native;

/// <summary>Private closed P13 candidate; product acceptance/default adoption is separate.</summary>
public static class NativeCompilerBiberPins
{
    public static bool Ready => true;
    public const int ResourceCount = 4357, TotalBytes = 259559615;
    public const string ClosureHash = "34540765dfd1b5d46666841378221cce0b40204014c4a8c2dc25ddddbe56d1ed";
    public const int BiberLoaderLength = 777719;
    public const string BiberLoaderHash = "e8cece7433da70dc83372d18ce314eea7c911467cdfc9b5cb3a877cf80d1dee7";
    public const int BiberWasmLength = 8698666;
    public const string BiberWasmHash = "f68864e55dafda71bf25e6903f0f9490a7c4fb14302cb3f0650e56dbc36d0201";
    public const int BiberDataLength = 18216642;
    public const string BiberDataHash = "28eaeec67f837530073d1de54ba3ab92a7fc8e96bcd83255eb6aa1a0a037c10b";
    public const int SemanticLoaderLength = 141918;
    public const string SemanticLoaderHash = "735c17494d6ec61c1b2f000f437f343ece9af3258f2f354f11745b7b5836dde2";
    public const int SemanticWasmLength = 1952801;
    public const string SemanticWasmHash = "e3136893e75d5b6b3a7b4297349c28f51423dee2c014a607d9c1cd20f75357c3";
    public const int PdfFormatLength = 22404123;
    public const string PdfFormatHash = "69e67945852ac84675d043b3f7a5ef02adce773e64129427edb8ce0ebdd84deb";
    public const int DviFormatLength = 22404979;
    public const string DviFormatHash = "729d02f1e2cd5b63886cf03520634b8fc319fdabe3ff61dd224134b8baf40605";
    public const int ConverterLoaderLength = 13270;
    public const string ConverterLoaderHash = "2975efeae6dc759ccdce984f68350a250ed8bb8bedaa6584329de5901db23caf";
    public const int ConverterWasmLength = 691713;
    public const string ConverterWasmHash = "4f4e13b462772c15184bf4279b533d598d446fa7a44c0b2bafda276baf91701f";
    public const int LuaLoaderLength = 193350;
    public const string LuaLoaderHash = "3ebd6f09571509648e6ce88de8f1229f4709514f5fbc9a1b1029b8c59a3cba4d";
    public const int LuaWasmLength = 6013352;
    public const string LuaWasmHash = "2eefd3abfec7292f0a912b364693e830bc47792d5c6c56029246ebc3505e2a16";
    public const int LuaFormatLength = 12437864;
    public const string LuaFormatHash = "fed204915ab2eb5b593ee232ae2a484d58e2dc39737b902aefbaf123a2d14e02";
    public const int LuaHarfResourceLength = 44737;
    public const string LuaHarfResourceHash = "b8ee9bf48072f929a2d159e67c16ab575ea0b4056e915b6b368412b45cfbb0cd";

    public static bool MatchesComponents(IReadOnlyDictionary<string, NativeResourcePin> pins)
    {
        var previous = new Dictionary<string, NativeResourcePin>(pins, StringComparer.Ordinal)
        {
            ["dvi-map-converter.js"] = new("dvi-map-converter.js", 13026, "a961144a37546755175dfda0687c22bb79e67ebef495aa84c3922b01af6f2c57"),
            ["dvi-map-converter.wasm"] = new("dvi-map-converter.wasm", 691182, "53bd8a8a98c509afa8b0e0e5451bf78d7e9d765da1c8f513d9c1a909e9d5270d"),
            ["latex-dvi.fmt"] = new("latex-dvi.fmt", 22404397, "fb304aa6c090d9708f3740cd348776d8eb7b3df97b9d4a1edab152ffcf01e907"),
            ["lualatex.fmt"] = new("lualatex.fmt", 12437864, "fed204915ab2eb5b593ee232ae2a484d58e2dc39737b902aefbaf123a2d14e02"),
            ["luaotfload-harf-plug.lua"] = new("luaotfload-harf-plug.lua", 42312, "7fdbe5cb95eabb4e8cebccfa8347e877d7fc4ef87904406c90709a798eb9abd8"),
            ["luatex.js"] = new("luatex.js", 193208, "b66ac597a51b06a97598c95db54329005db54ff5631e461e36f1a841bec86d90"),
            ["luatex.wasm"] = new("luatex.wasm", 6013318, "7122d44f4d63a4626a3bcc55bb6bb404cfb8d78fd5419dda38849e991e23b7c6"),
            ["pdflatex.fmt"] = new("pdflatex.fmt", 22403541, "6372ff155080eec70f023c29b15a1e71cf7c150bf50dce765fad322776e6b6a1"),
        };
        previous.Remove("semantic-engine.js");
        previous.Remove("semantic-engine.wasm");
        previous.Remove("biber.js");
        previous.Remove("biber.wasm");
        previous.Remove("biber.data");
        return NativeCompilerUnicodePins.MatchesComponents(previous)
            && Fixed("biber.js", BiberLoaderLength, BiberLoaderHash)
            && Fixed("biber.wasm", BiberWasmLength, BiberWasmHash)
            && Fixed("biber.data", BiberDataLength, BiberDataHash)
            && Fixed("semantic-engine.js", SemanticLoaderLength, SemanticLoaderHash)
            && Fixed("semantic-engine.wasm", SemanticWasmLength, SemanticWasmHash)
            && Fixed("pdflatex.fmt", PdfFormatLength, PdfFormatHash)
            && Fixed("latex-dvi.fmt", DviFormatLength, DviFormatHash)
            && Fixed("dvi-map-converter.js", ConverterLoaderLength, ConverterLoaderHash)
            && Fixed("dvi-map-converter.wasm", ConverterWasmLength, ConverterWasmHash)
            && Fixed("luatex.js", LuaLoaderLength, LuaLoaderHash)
            && Fixed("luatex.wasm", LuaWasmLength, LuaWasmHash)
            && Fixed("lualatex.fmt", LuaFormatLength, LuaFormatHash)
            && Fixed("luaotfload-harf-plug.lua", LuaHarfResourceLength, LuaHarfResourceHash);
        bool Fixed(string name, int length, string hash) => pins.TryGetValue(name, out var p) && p.Length == length && p.Sha256 == hash;
    }
}
