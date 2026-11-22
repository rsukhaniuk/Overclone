namespace Client.Compilation.Native;

/// <summary>LuaLaTeX aliases for the licensed fonts in the preceding pinned suite.</summary>
internal static class NativeCompilerLuaFontPins
{
    internal const int ResourceCount = 5164, TotalBytes = 279547086, ArchiveLength = 280920967;
    internal const string ClosureHash = "fa1cda24675d3907b57c22cb2bb7143a928639fa964b7a75afd9b2c4b12492a0";
    internal const string IdentityName = "local-compiler-luatex-compatible-fonts-v1";
    internal const string RuntimeIdentity = "382c1f851ba3d3de852d1a7fbea499820a7a68de08bbdcb333eaf4d841afd0ec";
    internal const string ArchiveSha256 = "e57560fadc39d8e5cdd3bd89267ad2d6dd7fc3a50105b7bc21b59ab5cdbf5c74";

    internal static bool IsAlias(string name) => name is "Arial.fontspec" or "TimesNewRoman.fontspec" or "TeXGyreTermes.fontspec";

    internal static bool MatchesComponents(IReadOnlyDictionary<string, NativeResourcePin> pins)
        => pins.Count == ResourceCount && pins.Values.Sum(pin => (long)pin.Length) == TotalBytes
            && NativeRuntimePackage.ResourceIdentity(pins.Values).Hex == ClosureHash
            && NativeCompilerCompatibleFontPins.MatchesComponents(pins.Where(pair => !IsAlias(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
}
