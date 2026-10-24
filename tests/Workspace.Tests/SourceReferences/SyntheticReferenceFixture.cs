using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Client.Analysis;
using Client.Workspace.Domain;

namespace Workspace.Tests.SourceReferences;

/// <summary>SYNTHETIC transport fixture, NOT Rust output or a second rule engine.
/// Predetermined literal-token spans and warnings test only the boundary.
/// Root will supply actual Rust public-entry fixtures separately.</summary>
internal static class SyntheticReferenceFixture
{
    internal const string Key = "розділ🙂";
    internal const string Text = "\\documentclass{article}\r\n\\begin{document}\r\n\\ref{" + Key + "}\r\n\\end{document}\r\n";
    internal static byte[] Bytes => Encoding.UTF8.GetBytes("\ufeff" + Text);
    internal static CapturedFile File(FileId id, string path) => new(id, path, true, Bytes.Length,
        SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(Bytes)), ImmutableArray.CreateRange(Bytes));
    internal static AnalysisSnapshot Snapshot()
    {
        var a = File(FileId.New(), "main.tex"); var b = File(FileId.New(), "chapters/other.tex");
        return new(AnalysisSnapshotId.New(), ProjectId.New(), Guid.NewGuid(), 3, 4, RevisionId.New(),
            new(AnalysisCatalog.ReferenceId, AnalysisCatalog.ReferenceVersion), AnalysisLimits.Defaults(), [a, b], [],
            ReferenceConfiguration: new(a.FileId.ToString()));
    }
    internal static AnalysisResultDto Result(AnalysisSnapshot s, bool warning = true)
    {
        var root = s.Files.Single(f => f.FileId.ToString() == s.ReferenceConfiguration!.RootFileId);
        var id = root.FileId.ToString(); var start = root.Bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Key));
        var diagnosticId = AnalysisCatalog.References + ":" + id + ":" + start.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var facts = s.Files.Select(f => new AnalysisSourceFileFactsDto(f.FileId.ToString(), f.ByteLength, f.HasBom,
            NewlineKind.Crlf, false, false, 1, RuleOutcome.Pass,
            [new(TokenKind.Reference, start, start + Encoding.UTF8.GetByteCount(Key), Key, null)])).ToArray();
        AnalysisRuleResultDto Rule(string rule, bool fail) => new(rule, "engine", fail ? RuleOutcome.Fail : RuleOutcome.NotApplicable,
            [new(id, fail ? RuleOutcome.Fail : RuleOutcome.NotApplicable, fail ? [diagnosticId] : [])],
            ["sourceIntentOnly", "macroExpansionUnsupported"]);
        return new(s.Id.ToString(), new(s.Profile.Id, s.Profile.Version), new(facts),
            warning ? [new(diagnosticId, AnalysisCatalog.References, "engine", "warning", id,
                new(start, start + Encoding.UTF8.GetByteCount(Key)), ReferenceChecks.MissingMessage, new(3, 5))] : [],
            [new(AnalysisCatalog.Engine, "engine", RuleOutcome.Pass, s.Files.Select(f => new AnalysisPerFileOutcomeDto(f.FileId.ToString(), RuleOutcome.Pass, [])).ToArray(), []),
                Rule(AnalysisCatalog.Labels, false), Rule(AnalysisCatalog.References, warning)],
            warning ? RuleOutcome.Fail : RuleOutcome.Pass,
            new([], ["macro expansion", "bibliography", "PDF checks"], [], false, false), ["pdfFacts", "pdfChecks"],
            ReferenceConfiguration: s.ReferenceConfiguration);
    }
    internal static string Reply(AnalysisSnapshot s, string requestId, bool warning = true)
        => JsonSerializer.Serialize(new AnalysisResponseEnvelope(1, requestId, ResponseStatus.Completed, Result(s, warning), null), AnalysisJson.Options);
}
