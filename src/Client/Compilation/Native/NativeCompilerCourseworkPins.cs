namespace Client.Compilation.Native;

/// <summary>Exact P18 inventory and the resource view used by LuaLaTeX.</summary>
public static class NativeCompilerCourseworkPins
{
    public static bool Ready => true;
    public const int ResourceCount = 5149, TotalBytes = 276859993, ArchiveLength = 278229460;
    public const string ClosureHash = "6866d137c2eb4fc4049cbc8f8c294db250c6316d0fdc4d8679f9b2a431ec0d36";
    public const string IdentityName = "local-compiler-coursework-v1";
    public const string RuntimeIdentity = "f4ad5c7f702efc4f90d7a803ad6c697be0b3664b4ca90f837c8e6bd74ca88a40";
    public const string ArchiveSha256 = "1b4d2a2e6e5d3b25fee60cdd921fae20bbd32dd122c0202570d44b677e882a5d";

    private static readonly HashSet<string> LuaOnlyResources = new(StringComparer.Ordinal)
    {
        "DejaVuSans-Bold.ttf",
        "DejaVuSans-BoldOblique.ttf",
        "DejaVuSans-ExtraLight.ttf",
        "DejaVuSans-Oblique.ttf",
        "DejaVuSans.ttf",
        "DejaVuSansCondensed-Bold.ttf",
        "DejaVuSansCondensed-BoldOblique.ttf",
        "DejaVuSansCondensed-Oblique.ttf",
        "DejaVuSansCondensed.ttf",
        "DejaVuSansMono-Bold.ttf",
        "DejaVuSansMono-BoldOblique.ttf",
        "DejaVuSansMono-Oblique.ttf",
        "DejaVuSansMono.ttf",
        "LiberationSans-Bold.ttf",
        "LiberationSans-BoldItalic.ttf",
        "LiberationSans-Italic.ttf",
        "LiberationSans-Regular.ttf",
        "LiberationSerif-Bold.ttf",
        "LiberationSerif-BoldItalic.ttf",
        "LiberationSerif-Italic.ttf",
        "LiberationSerif-Regular.ttf",
        "babelsh.def",
        "colortbl.sty",
        "ctablestack.sty",
        "gloss-english.ldf",
        "gloss-ukrainian.ldf",
        "language.dat.lua",
        "luatex-hyphen.lua",
        "needspace.sty",
        "parskip.sty",
        "pdflscape-nometadata.sty",
        "pdflscape.sty",
        "polyglossia.lua",
        "polyglossia.sty",
        "xpg-cyrillicnumbers.sty"
    };

    private static readonly HashSet<string> Notices = new(StringComparer.Ordinal)
    {
        "notices/coursework18/texlive-latex-extra-copyright.txt",
        "notices/coursework18/texlive-latex-base-copyright.txt",
        "notices/coursework18/texlive-latex-recommended-copyright.txt",
        "notices/coursework18/texlive-luatex-copyright.txt",
        "notices/coursework18/texlive-lang-cyrillic-copyright.txt",
        "notices/coursework18/fonts-dejavu-core-copyright.txt",
        "notices/coursework18/fonts-liberation-copyright.txt",
        "notices/coursework18/fonts-liberation2-copyright.txt"
    };

    internal static bool IsLuaOnlyResource(string name) => LuaOnlyResources.Contains(name);
    internal static bool IsLuaCompatibilityResource(string name) => name is "keyval.tex" or "ragged2e.sty" or "xkeyval.tex" or "xkvutils.tex"
        || IsLuaOnlyResource(name);
    internal static bool NoticeName(string? name) => name is not null && Notices.Contains(name);

    public static bool MatchesComponents(IReadOnlyDictionary<string, NativeResourcePin> pins)
    {
        if (pins.Count != ResourceCount || pins.Values.Sum(pin => (long)pin.Length) != TotalBytes
            || NativeRuntimePackage.ResourceIdentity(pins.Values).Hex != ClosureHash) return false;
        var baseline = pins.Where(pair => !IsLuaOnlyResource(pair.Key) && !NoticeName(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return NativeCompilerSelectedPdfPins.MatchesComponents(baseline);
    }
}
