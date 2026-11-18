using System.Collections.Immutable;
using System.Text.Json;

namespace Client.Analysis;

/// <summary>Integrity-pinned reviewed producer DATA, not a second rule engine.
/// No source acquisition, numeric predicate evaluation or rendered measurements.</summary>
public static class ThesisCatalog
{
    public const string Id = "knu-csc-2025-thesis-support", Version = "1.0.0";
    public const string CitationNotesVersion = "1.1.0";
    public const string CaptionRolesVersion = "1.2.0";
    public const string GuidelineSha256 = "8be4d724ac108488e6b1766322e7fd6b3646412aa2b6de12b6945f48fb380714";
    private const string ResourceSha256 = "c629ac2804d0d8c8f36c8ed7361242d032c39a5789fba6d2ea00f575219678dc";
    private static readonly Lazy<ImmutableArray<JsonElement>> Bundled = new(() => Load(
        "thesis-support-catalog.json", Version, AnalysisCatalog.ThesisVersion, ResourceSha256));
    private static readonly Lazy<ImmutableArray<JsonElement>> CitationNotes = new(() => Load(
        "thesis-support-catalog-v1_6.json", CitationNotesVersion, AnalysisCatalog.CitationNotesVersion,
        "6d88a14c64cb0696ee9a5419be8cddc2891cfb491a2566bfad0c6d4ec3d8646d"));
    private static readonly Lazy<ImmutableArray<JsonElement>> CaptionRoles = new(() => Load(
        "thesis-support-catalog-v1_7.json", CaptionRolesVersion, AnalysisCatalog.CaptionRolesVersion,
        "50e30f785347c3fc9ff00189d7a51a1f4b0d2bd7ee8f75d2b5d548f414c09707"));
    public static ImmutableArray<JsonElement> Rows => Bundled.Value;
    public static string VersionForProfile(string profileVersion) => profileVersion switch
    {
        AnalysisCatalog.ThesisVersion => Version,
        AnalysisCatalog.CitationNotesVersion => CitationNotesVersion,
        AnalysisCatalog.CaptionRolesVersion => CaptionRolesVersion,
        _ => throw new JsonException("Unknown thesis source profile version."),
    };
    public static ImmutableArray<JsonElement> RowsForCatalog(string catalogVersion) => catalogVersion switch
    {
        Version => Rows,
        CitationNotesVersion => CitationNotes.Value,
        CaptionRolesVersion => CaptionRoles.Value,
        _ => throw new JsonException("Unknown thesis source catalog version."),
    };
    public static ImmutableArray<string> RuleIds { get; } = [
        "KNU-SRC-STRUCTURE-001", "KNU-SRC-CITATIONS-001", "KNU-SRC-BIBLIOGRAPHY-001",
        "KNU-SRC-APPENDIX-001", "KNU-SRC-CAPTION-PRESENCE-001"];
    internal static ImmutableArray<string> Methods { get; } = [
        "literal-structure-order", "literal-citation-key-resolution", "literal-manual-first-use-order",
        "literal-ukrainian-appendix-letter", "literal-caption-title-present"];
    internal static JsonElement SourceRule(string id, string catalogVersion) => RowsForCatalog(catalogVersion).Single(r => r.GetProperty("ruleId").GetString() == id);
    private static ImmutableArray<JsonElement> Load(string resource, string version, string profileVersion, string expectedSha256)
    {
        using var stream = typeof(ThesisCatalog).Assembly.GetManifestResourceStream("Client.Analysis." + resource)
            ?? throw new InvalidOperationException("Bundled thesis catalog is missing.");
        if (!stream.CanSeek || stream.Length > 128 * 1024) throw new InvalidOperationException("Bundled thesis catalog size is invalid.");
        using var bytes = new MemoryStream((int)stream.Length); stream.CopyTo(bytes);
        var data = bytes.ToArray();
        if (data.Length > 128 * 1024 || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data)) != expectedSha256)
            throw new InvalidOperationException("Bundled thesis catalog identity mismatch.");
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        if (root.GetProperty("catalogId").GetString() != Id || root.GetProperty("catalogVersion").GetString() != version
            || root.GetProperty("profileVersion").GetString() != profileVersion
            || root.GetProperty("guidelineSha256").GetString() != GuidelineSha256 || root.GetProperty("rules").GetArrayLength() != 31)
            throw new InvalidOperationException("Bundled thesis catalog version mismatch.");
        return root.GetProperty("rules").EnumerateArray().Select(r => r.Clone()).ToImmutableArray();
    }
}
