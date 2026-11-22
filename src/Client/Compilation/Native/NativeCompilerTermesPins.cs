namespace Client.Compilation.Native;

/// <summary>Exact Termes resource closure for the P21 engine and wire contract.</summary>
public static class NativeCompilerTermesPins
{
    public const int ResourceCount = 5160, TotalBytes = 279544926, ArchiveLength = 280917655;
    public const string ClosureHash = "52313f3ae8c7d4768cc0c2a6d05077d379072569f5f229bc0f94e1a776317459";
    internal const string IdentityName = "local-compiler-termes-xstring-v1";
    internal const string PreviousRuntimeIdentity = "ffd92f17bb5d64715e7bcf24ea04694af786381f91e3a4668d829cb5f7d48209";
    public const string RuntimeIdentity = "902f858d535c6a4fa273a24bec9c8a0fee7093e67ea6dd8da062bff1e43bbbe5";
    public const string ArchiveSha256 = "f49b42d1f37759fb00dbdc85ba7418d13a26556adedcf6a8e1c2373a96810260";
    private const string Notice = "notices/tex-gyre-termes/copyright.txt";
    private const string Catalog = "xetexfontlist.txt";

    internal static bool NoticeName(string? name) => name == Notice;

    internal static bool MatchesComponents(IReadOnlyDictionary<string, NativeResourcePin> pins)
    {
        if (pins.Count != ResourceCount || pins.Values.Sum(pin => (long)pin.Length) != TotalBytes
            || NativeRuntimePackage.ResourceIdentity(pins.Values).Hex != ClosureHash) return false;
        var parent = new Dictionary<string, NativeResourcePin>(pins, StringComparer.Ordinal);
        parent.Remove(Notice);
        parent.Remove("texgyretermes-regular.otf");
        parent.Remove("texgyretermes-bold.otf");
        parent.Remove("texgyretermes-italic.otf");
        parent.Remove("texgyretermes-bolditalic.otf");
        parent[Catalog] = new(Catalog, 12729, "7e714695c3bf62543445856f69d6690de0b4b5cecec8def6624b64875522768b");
        return NativeCompilerPageLabelPins.MatchesComponents(parent);
    }
}
