namespace Client.Compilation.Native;

/// <summary>Exact P21 DVI converter and attribution layered on P20.</summary>
public static class NativeCompilerPageLabelPins
{
    public static bool Ready => NativeCompilerPdfOriginPins.Ready;
    public const int ResourceCount = 5155, TotalBytes = 278965292, ArchiveLength = 280336534;
    public const string ClosureHash = "a46b4eb3f9564d469d7b8e8c6df5a392828180faf4382e9c06651dec0f91ad6b";
    public const string IdentityName = "local-compiler-dvi-page-labels-v1";
    public const string RuntimeIdentity = "eb47ed92ac9efcdc7277c6b3dd1c5861abde63213e440d40aa7c201af6f4d692";
    public const string ArchiveSha256 = "532fc02d7bba80371a02b3477c5e7d35397d366d62cc03eb040689ca30c69b78";
    public const string DviConverterWasmHash = "73c8714f530b6b7821cc2c3b4ba73b82baebc4958ab2b741ae84796f7705efc8";
    private const string Notice = "notices/dvi-page-label21/provenance.txt";

    internal static bool NoticeName(string? name) => name == Notice;

    public static bool MatchesComponents(IReadOnlyDictionary<string, NativeResourcePin> pins)
    {
        if (pins.Count != ResourceCount || pins.Values.Sum(pin => (long)pin.Length) != TotalBytes
            || NativeRuntimePackage.ResourceIdentity(pins.Values).Hex != ClosureHash) return false;
        var parent = pins.Where(pair => !NoticeName(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        const string converter = "dvi-map-converter.wasm";
        parent[converter] = new(converter, 697529, NativeCompilerDviGraphicsPins.DviConverterWasmHash);
        return NativeCompilerPdfOriginPins.MatchesComponents(parent);
    }
}
