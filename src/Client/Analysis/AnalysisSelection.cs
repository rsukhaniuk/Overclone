using System.Text.Json;

namespace Client.Analysis;

/// <summary>Immutable finite selection. A wire profile is still only ID/version;
/// configuration is a separate sibling. No project-settings persistence.</summary>
public readonly record struct AnalysisSelection(AnalysisProfile Profile, AnalysisConfigurationDto? Configuration)
{
    public static AnalysisSelection EngineStructural() => new(AnalysisProfile.EngineStructural(), null);
    public static AnalysisSelection Knu(AnalysisWorkType workType) => new(new(AnalysisCatalog.KnuId, AnalysisCatalog.Version), new(workType));
    public bool IsValid => AnalysisCatalog.IsValid(Profile.Id, Profile.Version, Configuration);
}

/// <summary>Identity/metadata allowlist, NOT another LaTeX/numeric rule engine.
/// Metadata is generated from accepted Rust public output and embedded offline.</summary>
public static class AnalysisCatalog
{
    public const string KnuId = "knu-csc-2025-source";
    public const string Version = "1.0.0";
    public const string Indent = "KNU-SRC-PARINDENT-001";
    public const string Heading = "KNU-SRC-HEADING-DOT-001";
    public const string Fix = "KNU-FIX-PARINDENT-LITERAL-001";
    public const string Engine = "ENGINE-STRUCT-001";
    public const int MaxDerivedIdBytes = 256; // 128-byte file ID + rule + byte offset
    private static readonly Lazy<JsonElement> Metadata = new(() =>
    {
        using var stream = typeof(AnalysisCatalog).Assembly.GetManifestResourceStream("Client.Analysis.knu-profile-metadata.json")
            ?? throw new InvalidOperationException("Bundled analysis catalog is missing.");
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.Clone();
    });
    internal static JsonElement ExpectedMetadata => Metadata.Value;
    public static bool IsKnown(string? id, string? version)
        => (id == "engine-structural" && version == "0") || (id == KnuId && version == Version);
    public static bool IsValid(string? id, string? version, AnalysisConfigurationDto? configuration)
        => id == "engine-structural" && version == "0" ? configuration is null
        : id == KnuId && version == Version && configuration is not null && Enum.IsDefined(configuration.WorkType);
    public static bool IsTex(string path) => path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase);
}
