using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Client.Analysis;

/// <summary>Immutable finite selection. A wire profile is still only ID/version;
/// configuration is a separate sibling. No project-settings persistence.</summary>
public readonly record struct AnalysisSelection(AnalysisProfile Profile, AnalysisConfigurationDto? Configuration,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AnalysisReferenceConfigurationDto? ReferenceConfiguration = null)
{
    public AnalysisSelection(AnalysisProfile profile, AnalysisConfigurationDto? configuration) : this(profile, configuration, null) { }
    public void Deconstruct(out AnalysisProfile profile, out AnalysisConfigurationDto? configuration)
    { profile = Profile; configuration = Configuration; }
    public static AnalysisSelection SourceReferences(string? rootFileId) => new(new(AnalysisCatalog.ReferenceId, AnalysisCatalog.ReferenceVersion), null,
        rootFileId is null ? null : new(rootFileId));
    public static AnalysisSelection EngineStructural() => new(AnalysisProfile.EngineStructural(), null);
    public static AnalysisSelection Knu(AnalysisWorkType workType) => new(new(AnalysisCatalog.KnuId, AnalysisCatalog.Version), new(workType));
    public static AnalysisSelection KnuLatest(AnalysisWorkType workType) => new(new(AnalysisCatalog.KnuId, AnalysisCatalog.LatestVersion), new(workType));
    public static AnalysisSelection KnuWithMarginFixes(AnalysisWorkType workType) => new(new(AnalysisCatalog.KnuId, AnalysisCatalog.MarginFixVersion), new(workType));
    public static AnalysisSelection KnuWithGeometryPackage(AnalysisWorkType workType) => new(new(AnalysisCatalog.KnuId, AnalysisCatalog.GeometryPackageVersion), new(workType));
    public static AnalysisSelection KnuWithFormatting(AnalysisWorkType workType) => new(new(AnalysisCatalog.KnuId, AnalysisCatalog.FormattingVersion), new(workType));
    public static AnalysisSelection KnuWithThesis(AnalysisWorkType workType, string? rootFileId) => new(new(AnalysisCatalog.KnuId, AnalysisCatalog.ThesisVersion), new(workType), rootFileId is null ? null : new(rootFileId));
    public static AnalysisSelection KnuWithCitationNotes(AnalysisWorkType workType, string? rootFileId) => new(new(AnalysisCatalog.KnuId, AnalysisCatalog.CitationNotesVersion), new(workType), rootFileId is null ? null : new(rootFileId));
    public bool IsValid => AnalysisCatalog.IsValid(Profile.Id, Profile.Version, Configuration, ReferenceConfiguration);
}

/// <summary>Identity/metadata allowlist, NOT another LaTeX/numeric rule engine.
/// Metadata is generated from accepted Rust public output and embedded offline.</summary>
public static class AnalysisCatalog
{
    public const string ReferenceId = "engine-source-references";
    public const string ReferenceVersion = "1.0.0";
    public const string Labels = "ENGINE-LABEL-001";
    public const string References = "ENGINE-REFERENCE-001";
    public const string KnuId = "knu-csc-2025-source";
    public const string Version = "1.0.0";
    public const string LatestVersion = "1.1.0";
    public const string MarginFixVersion = "1.2.0";
    public const string MarginRuleVersion = "1.1.0";
    public const string GeometryPackageVersion = "1.3.0";
    public const string FormattingVersion = "1.4.0";
    public const string ThesisVersion = "1.5.0";
    public const string CitationNotesVersion = "1.6.0";
    public static bool IsThesis(AnalysisProfile profile) => profile.Id == KnuId && profile.Version is ThesisVersion or CitationNotesVersion;
    public static bool RequiresRoot(AnalysisProfile profile) => profile.Id == ReferenceId || IsThesis(profile);
    public const string Paper = "KNU-SRC-PAPER-001";
    public const string LineSpacing = "KNU-SRC-LINE-SPACING-001";
    public const string Caption = "KNU-SRC-CAPTION-DOT-001";
    public const string GeometryPackageRuleVersion = "1.2.0";
    internal static string MarginRuleVersionFor(string version) => version == GeometryPackageVersion ? GeometryPackageRuleVersion : MarginRuleVersion;
    public const string MarginLeftFix = "KNU-FIX-MARGIN-LEFT-LITERAL-001";
    public const string MarginRightFix = "KNU-FIX-MARGIN-RIGHT-LITERAL-001";
    public const string MarginTopFix = "KNU-FIX-MARGIN-TOP-LITERAL-001";
    public const string MarginBottomFix = "KNU-FIX-MARGIN-BOTTOM-LITERAL-001";
    public static bool IsCanonicalFixId(string id) => id is Fix or MarginLeftFix or MarginRightFix or MarginTopFix or MarginBottomFix;
    internal static (AnalysisMarginSide Side, string Replacement)? MarginFix(string id) => id switch
    {
        MarginLeftFix => (AnalysisMarginSide.Left, "25mm"), MarginRightFix => (AnalysisMarginSide.Right, "10mm"),
        MarginTopFix => (AnalysisMarginSide.Top, "20mm"), MarginBottomFix => (AnalysisMarginSide.Bottom, "20mm"), _ => null,
    };
    public const string Margins = "KNU-SRC-MARGINS-001";
    public const string Indent = "KNU-SRC-PARINDENT-001";
    public const string Heading = "KNU-SRC-HEADING-DOT-001";
    public const string Fix = "KNU-FIX-PARINDENT-LITERAL-001";
    public const string Engine = "ENGINE-STRUCT-001";
    public const int MaxDerivedIdBytes = 256; // 128-byte file ID + rule + byte offset
    private static readonly ImmutableArray<string> LegacyRules = [Indent, Heading];
    private static readonly ImmutableArray<string> LatestRules = [Indent, Heading, Margins];
    private static readonly ImmutableArray<string> FormattingRules = [Indent, Heading, Margins, Paper, LineSpacing, Caption];
    private static readonly Lazy<JsonElement> Metadata = new(() => LoadMetadata("knu-profile-metadata.json"));
    private static readonly Lazy<JsonElement> LatestMetadata = new(() => LoadMetadata("knu-profile-v1_1-metadata.json"));
    private static readonly Lazy<JsonElement> MarginFixMetadata = new(() => LoadMetadata("knu-profile-v1_2-metadata.json"));
    private static readonly Lazy<JsonElement> GeometryPackageMetadata = new(() => LoadMetadata("knu-profile-v1_3-metadata.json"));
    private static readonly Lazy<JsonElement> FormattingMetadata = new(() => LoadMetadata("knu-profile-v1_4-metadata.json"));
    private static readonly Lazy<JsonElement> ThesisMetadata = new(() => LoadMetadata("knu-profile-v1_5-metadata.json"));
    private static readonly Lazy<JsonElement> CitationNotesMetadata = new(() => LoadMetadata("knu-profile-v1_6-metadata.json"));
    private static JsonElement LoadMetadata(string name)
    {
        using var stream = typeof(AnalysisCatalog).Assembly.GetManifestResourceStream("Client.Analysis." + name)
            ?? throw new InvalidOperationException("Bundled analysis catalog is missing.");
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.Clone();
    }
    internal static JsonElement ExpectedMetadata => Metadata.Value;
    internal static JsonElement MetadataFor(string version) => version switch
    {
        Version => ExpectedMetadata,
        LatestVersion => LatestMetadata.Value,
        MarginFixVersion => MarginFixMetadata.Value,
        GeometryPackageVersion => GeometryPackageMetadata.Value,
        FormattingVersion => FormattingMetadata.Value,
        ThesisVersion => ThesisMetadata.Value,
        CitationNotesVersion => CitationNotesMetadata.Value,
        _ => throw new JsonException("Unknown KNU profile version."),
    };
    internal static ImmutableArray<string> SourceRulesFor(string version) => version switch
    {
        Version => LegacyRules,
        LatestVersion => LatestRules,
        MarginFixVersion => LatestRules,
        GeometryPackageVersion => LatestRules,
        FormattingVersion => FormattingRules,
        ThesisVersion => FormattingRules,
        CitationNotesVersion => FormattingRules,
        _ => throw new JsonException("Unknown KNU profile version."),
    };
    public static bool IsKnown(string? id, string? version)
        => (id == "engine-structural" && version == "0") || (id == ReferenceId && version == ReferenceVersion)
            || (id == KnuId && version is Version or LatestVersion or MarginFixVersion or GeometryPackageVersion or FormattingVersion or ThesisVersion or CitationNotesVersion);
    public static bool IsValid(string? id, string? version, AnalysisConfigurationDto? configuration,
        AnalysisReferenceConfigurationDto? referenceConfiguration = null)
        => id == KnuId && version is ThesisVersion or CitationNotesVersion
            ? configuration is not null && Enum.IsDefined(configuration.WorkType) && referenceConfiguration is not null
                && AnalysisIdentity.IsValidOpaqueId(referenceConfiguration.RootFileId, AnalysisLimits.MaxIdBytes)
            : id == ReferenceId && version == ReferenceVersion
            ? configuration is null && referenceConfiguration is not null
                && AnalysisIdentity.IsValidOpaqueId(referenceConfiguration.RootFileId, AnalysisLimits.MaxIdBytes)
            : referenceConfiguration is null && (id == "engine-structural" && version == "0" ? configuration is null
                : id == KnuId && version is Version or LatestVersion or MarginFixVersion or GeometryPackageVersion or FormattingVersion && configuration is not null && Enum.IsDefined(configuration.WorkType));
    public static bool IsTex(string path) => path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase);
}
