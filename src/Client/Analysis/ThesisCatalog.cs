using System.Collections.Immutable;
using System.Text.Json;

namespace Client.Analysis;

/// <summary>Integrity-pinned reviewed producer DATA, not a second rule engine.
/// No source acquisition, numeric predicate evaluation or rendered measurements.</summary>
public static class ThesisCatalog
{
    public const string Id = "knu-csc-2025-thesis-support", Version = "1.0.0";
    public const string GuidelineSha256 = "8be4d724ac108488e6b1766322e7fd6b3646412aa2b6de12b6945f48fb380714";
    private const string ResourceSha256 = "c629ac2804d0d8c8f36c8ed7361242d032c39a5789fba6d2ea00f575219678dc";
    private static readonly Lazy<ImmutableArray<JsonElement>> Bundled = new(Load);
    public static ImmutableArray<JsonElement> Rows => Bundled.Value;
    public static ImmutableArray<string> RuleIds { get; } = [
        "KNU-SRC-STRUCTURE-001", "KNU-SRC-CITATIONS-001", "KNU-SRC-BIBLIOGRAPHY-001",
        "KNU-SRC-APPENDIX-001", "KNU-SRC-CAPTION-PRESENCE-001"];
    internal static ImmutableArray<string> Methods { get; } = [
        "literal-structure-order", "literal-citation-key-resolution", "literal-manual-first-use-order",
        "literal-ukrainian-appendix-letter", "literal-caption-title-present"];
    internal static JsonElement SourceRule(string id) => Rows.Single(r => r.GetProperty("ruleId").GetString() == id);
    private static ImmutableArray<JsonElement> Load()
    {
        using var stream = typeof(ThesisCatalog).Assembly.GetManifestResourceStream("Client.Analysis.thesis-support-catalog.json")
            ?? throw new InvalidOperationException("Bundled thesis catalog is missing.");
        if (!stream.CanSeek || stream.Length > 128 * 1024) throw new InvalidOperationException("Bundled thesis catalog size is invalid.");
        using var bytes = new MemoryStream((int)stream.Length); stream.CopyTo(bytes);
        var data = bytes.ToArray();
        if (data.Length > 128 * 1024 || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data)) != ResourceSha256)
            throw new InvalidOperationException("Bundled thesis catalog identity mismatch.");
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        if (root.GetProperty("catalogId").GetString() != Id || root.GetProperty("catalogVersion").GetString() != Version
            || root.GetProperty("guidelineSha256").GetString() != GuidelineSha256 || root.GetProperty("rules").GetArrayLength() != 31)
            throw new InvalidOperationException("Bundled thesis catalog version mismatch.");
        return root.GetProperty("rules").EnumerateArray().Select(r => r.Clone()).ToImmutableArray();
    }
}
