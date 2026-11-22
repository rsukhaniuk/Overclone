namespace Client.Compilation.Native;

/// <summary>XeTeX catalog aliases for the existing licensed Liberation files.</summary>
public static class NativeCompilerCompatibleFontPins
{
    public const int ResourceCount = 5161, TotalBytes = 279546484, ArchiveLength = 280919552;
    public const string ClosureHash = "b221a3d40d6e73d45d4bbb5f2f0de7d1ef6050e3a6a40e70fc884d873828c236";
    internal const string IdentityName = "local-compiler-xetex-compatible-fonts-v1";
    public const string RuntimeIdentity = "f6a4fd79dfe6453b58b64d3a2322fcb4f359f5d3fa0cedadc90abdb3ac9297dc";
    public const string ArchiveSha256 = "783e236a6490bbea153016c81fbe76c2885c8b72d48f672fcbd1726def9eb987";
    private const string Notice = "notices/xetex-compatible-fonts/aliases.txt";
    private const string Catalog = "xetexfontlist.txt";

    internal static bool HasXeAliases(string identity) => identity is RuntimeIdentity or NativeCompilerLuaFontPins.RuntimeIdentity;

    internal static bool NoticeName(string? name) => name == Notice;

    internal static bool IsCompatibilityFont(string name) => name is
        "LiberationSerif-Regular.ttf" or "LiberationSerif-Bold.ttf" or "LiberationSerif-Italic.ttf" or "LiberationSerif-BoldItalic.ttf"
        or "LiberationSans-Regular.ttf" or "LiberationSans-Bold.ttf" or "LiberationSans-Italic.ttf" or "LiberationSans-BoldItalic.ttf";

    internal static bool MatchesComponents(IReadOnlyDictionary<string, NativeResourcePin> pins)
    {
        if (pins.Count != ResourceCount || pins.Values.Sum(pin => (long)pin.Length) != TotalBytes
            || NativeRuntimePackage.ResourceIdentity(pins.Values).Hex != ClosureHash) return false;
        var parent = new Dictionary<string, NativeResourcePin>(pins, StringComparer.Ordinal);
        parent.Remove(Notice);
        parent[Catalog] = new(Catalog, 13375, "c3daa14b49932a78e110368ed1b053b274de3884994125a43d4c1fea28aae286");
        return NativeCompilerTermesPins.MatchesComponents(parent);
    }
}
