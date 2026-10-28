namespace Client.Compilation.Native;

/// <summary>Root integration pin hook. Never derive trust from selected metadata.
/// Replace only with reviewed, real assembled component facts and closure.</summary>
public static class NativeCompilerSuitePins
{
    public static bool Ready => true;
    public const int ResourceCount = 3677;
    public const int TotalBytes = 187630023;
    public const string ClosureHash = "b58c8f79ce22a516f69f0a66211aab0c6298d904012229c7c063dcee36d927db";
    public const int ConverterLoaderLength = 12606, ConverterWasmLength = 685925;
    public const string ConverterLoaderHash = "dc31543194583c105228dc791040c2f09afd7aa64311ce254b215c9fcd24e54a";
    public const string ConverterWasmHash = "52085c656ec2680150519d39ecd8588326e123652d72294e7e19eedd0ec0171c";
    public const string IcuResourceName = "icudt68l.dat";
    public const string XeCatalogResourceName = "xetexfontlist.txt";
    public const int XeLoaderLength = 69490, XeWasmLength = 3285298, XeFormatLength = 33847140;
    public const string XeLoaderHash = "4ca80d7ed06d550632766dc3629f394f7f41b586d96682e98b87ca8fe8c72255", XeWasmHash = "b02ff092694071320785486618aa0de7df2d12fae7d3e92c5a32c5cda5980243", XeFormatHash = "0601a5dec935457c9d3e06150c8ffb1037b8befef3cbf5cb4793dc1f4876fce5";
    public const int LuaLoaderLength = 189950, LuaWasmLength = 5172330, LuaFormatLength = 12436466;
    public const string LuaLoaderHash = "b82efbe8297f3e9403a94b4b9183145bfb17cda1d47ed8cb3aa9455eec561e97", LuaWasmHash = "e765161b9d6d3d17d444ebf919a80c71e33f6cbb7cb9609e09ba3b3fb3b2e1ff", LuaFormatHash = "b0bc0668519081b2415e7e504b86805bd17e88b91d3185e6ef7566ac62f3d097";
    public static bool MatchesComponents(IReadOnlyDictionary<string, NativeResourcePin> pins)
    {
        return Ready && Fixed("xetex.js", XeLoaderLength, XeLoaderHash) && Fixed("xetex.wasm", XeWasmLength, XeWasmHash)
        && Fixed("xelatex.fmt", XeFormatLength, XeFormatHash)
        && Fixed("luatex.js", LuaLoaderLength, LuaLoaderHash) && Fixed("luatex.wasm", LuaWasmLength, LuaWasmHash)
        && Fixed("lualatex.fmt", LuaFormatLength, LuaFormatHash)
        && Fixed("dvi-converter.js", ConverterLoaderLength, ConverterLoaderHash)
        && Fixed("dvi-converter.wasm", ConverterWasmLength, ConverterWasmHash);
        bool Fixed(string name, int length, string hash) => pins.TryGetValue(name, out var pin)
            && pin.Length == length && pin.Sha256 == hash;
    }
}
