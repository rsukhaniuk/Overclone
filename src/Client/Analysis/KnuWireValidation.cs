using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Client.Workspace.Transfer;

namespace Client.Analysis;

/// <summary>Validates transport structure/consistency, never evaluates LaTeX or
/// computes a numeric rule predicate. Snapshot checks use exact bytes only.</summary>
internal static class KnuWireValidation
{
    private const int MaxFiles = 5000, MaxTokens = 100000, MaxFileTokens = 20000;
    // Rust bounded_argument gates derivation of known values, not unknown spans.
    private const int MaxKnownArgumentBytes = 4096;
    internal static void Require(bool condition, string message)
    { if (!condition) throw new JsonException(message); }

    internal static void NoDuplicates(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in e.EnumerateObject())
            {
                Require(seen.Add(p.Name), "Duplicate protocol field.");
                NoDuplicates(p.Value);
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var item in e.EnumerateArray()) NoDuplicates(item);
    }

    private static void Fields(JsonElement e, string required, string optional = "")
    {
        Require(e.ValueKind == JsonValueKind.Object, "Expected protocol object.");
        var req = required.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var opt = optional.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in req) Require(e.TryGetProperty(name, out _), "Missing protocol field.");
        foreach (var p in e.EnumerateObject())
            Require(req.Contains(p.Name, StringComparer.Ordinal) || opt.Contains(p.Name, StringComparer.Ordinal), "Unexpected protocol field.");
    }
    private static void Items(JsonElement e, int max, Action<JsonElement> check)
    {
        Require(e.ValueKind == JsonValueKind.Array && e.GetArrayLength() <= max, "Invalid or oversized protocol array.");
        foreach (var item in e.EnumerateArray()) check(item);
    }
    private static void SpanShape(JsonElement e) => Fields(e, "startByte endByte");
    private static void ProfileShape(JsonElement e) => Fields(e, "id version");
    internal static AnalysisConfigurationDto Configuration(JsonElement e)
    {
        NoDuplicates(e);
        Fields(e, "workType");
        var c = e.Deserialize<AnalysisConfigurationDto>(AnalysisJson.Options);
        Require(c is not null && Enum.IsDefined(c.WorkType), "Invalid work type.");
        return c!;
    }
    internal static void Shape(JsonElement root)
    {
        if (root.GetProperty("payload").GetProperty("profile").GetProperty("version").GetString() == AnalysisCatalog.ThesisVersion)
        {
            ThesisWireValidation.Shape(root);
            using var legacy = ThesisWireValidation.LegacyDocument(root);
            Shape(legacy.RootElement); // Exact existing 1.4 validation; never broaden old versions.
            return;
        }
        Fields(root, "protocolVersion requestId status payload");
        var p = root.GetProperty("payload");
        Fields(p, "snapshotId profile facts diagnostics ruleResults aggregateOutcome coverage unavailableCapabilities configuration knu");
        ProfileShape(p.GetProperty("profile"));
        var version = p.GetProperty("profile").GetProperty("version").GetString()!;
        var sourceRules = AnalysisCatalog.SourceRulesFor(version);
        var metadata = AnalysisCatalog.MetadataFor(version);
        Configuration(p.GetProperty("configuration"));
        var facts = p.GetProperty("facts"); Fields(facts, "files");
        Items(facts.GetProperty("files"), MaxFiles, f =>
        {
            Fields(f, "fileId byteLength hasBom newlineKind truncated braceDepthExceeded tokenCount engineOutcome tokens");
            Items(f.GetProperty("tokens"), MaxFileTokens, t => Fields(t, "kind startByte endByte", "text note"));
        });
        Items(p.GetProperty("diagnostics"), 1000, d =>
        {
            Fields(d, "id ruleId origin severity fileId span message primaryLocation");
            if (d.GetProperty("span").ValueKind != JsonValueKind.Null) SpanShape(d.GetProperty("span"));
            if (d.GetProperty("primaryLocation").ValueKind != JsonValueKind.Null) Fields(d.GetProperty("primaryLocation"), "line column");
        });
        Items(p.GetProperty("ruleResults"), sourceRules.Length + 1, r =>
        {
            bool engine = r.GetProperty("ruleId").GetString() == AnalysisCatalog.Engine;
            Fields(r, engine ? "ruleId origin outcome perFile limitations" : "ruleId origin outcome perFile limitations source evidenceIds");
            Items(r.GetProperty("perFile"), MaxFiles, f => { Fields(f, "fileId outcome diagnosticIds"); Items(f.GetProperty("diagnosticIds"), 1000, _ => { }); });
            Items(r.GetProperty("limitations"), MaxFiles + 4, _ => { });
            if (!engine)
            {
                var id = r.GetProperty("ruleId").GetString();
                Require(id is not null && sourceRules.Contains(id), "Unknown source rule.");
                var expected = metadata.GetProperty("rules").EnumerateArray().Single(e => e.GetProperty("ruleId").GetString() == id);
                Require(JsonElement.DeepEquals(r.GetProperty("source"), expected), "Invalid source metadata.");
                Items(r.GetProperty("evidenceIds"), MaxTokens, _ => { });
            }
        });
        var coverage = p.GetProperty("coverage");
        Fields(coverage, "factKinds unsupported truncatedFiles diagnosticsTruncated resultTruncated");
        Items(coverage.GetProperty("factKinds"), 32, _ => { });
        Items(coverage.GetProperty("unsupported"), 64, _ => { });
        Items(coverage.GetProperty("truncatedFiles"), MaxFiles, _ => { });
        Items(p.GetProperty("unavailableCapabilities"), 2, _ => { });
        var k = p.GetProperty("knu");
        Fields(k, "metadata engineAggregateOutcome files fixDescriptors");
        Require(JsonElement.DeepEquals(k.GetProperty("metadata"), metadata), "Unknown or inconsistent guideline/profile metadata.");
        Items(k.GetProperty("files"), MaxFiles, f =>
        {
            Fields(f, "fileId eligible complete observationCount observations limitations");
            Items(f.GetProperty("limitations"), version == AnalysisCatalog.FormattingVersion ? 20 : version == AnalysisCatalog.Version ? 14 : 17, _ => { });
            Items(f.GetProperty("observations"), MaxFileTokens, o =>
            {
                Fields(o, "id ruleId commandSpan declarationSpan valueSpan excerpt excerptTruncated method value agrees reason");
                SpanShape(o.GetProperty("commandSpan")); SpanShape(o.GetProperty("declarationSpan")); SpanShape(o.GetProperty("valueSpan"));
                var v = o.GetProperty("value");
                Require(v.ValueKind == JsonValueKind.Object && v.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String, "Missing observation discriminator.");
                switch (v.GetProperty("kind").GetString())
                {
                    case "indentDeclaration":
                        Fields(v, "kind targetSpan length"); SpanShape(v.GetProperty("targetSpan"));
                        if (v.GetProperty("length").ValueKind != JsonValueKind.Null) Fields(v.GetProperty("length"), "numeratorMm denominator unit");
                        break;
                    case "headingTitle": Fields(v, "kind titleKind finalScalar"); break;
                    case "marginDeclaration":
                        Require(version is AnalysisCatalog.LatestVersion or AnalysisCatalog.MarginFixVersion or AnalysisCatalog.GeometryPackageVersion or AnalysisCatalog.FormattingVersion, "Observation is not supported by this profile version.");
                        Fields(v, "kind side length");
                        if (v.GetProperty("length").ValueKind != JsonValueKind.Null) Fields(v.GetProperty("length"), "numeratorMm denominator unit");
                        break;
                    case "paperDeclaration":
                        Require(version == AnalysisCatalog.FormattingVersion, "Observation is not supported by this profile version.");
                        Fields(v, "kind paper"); break;
                    case "lineSpacingDeclaration":
                        Require(version == AnalysisCatalog.FormattingVersion, "Observation is not supported by this profile version.");
                        Fields(v, "kind syntax numerator denominator"); break;
                    case "captionTitle":
                        Require(version == AnalysisCatalog.FormattingVersion, "Observation is not supported by this profile version.");
                        Fields(v, "kind environment finalScalar"); break;
                    default: throw new JsonException("Unknown observation discriminator.");
                }
            });
        });
        Items(k.GetProperty("fixDescriptors"), 1, f =>
        {
            Fields(f, "id version ruleId ruleVersion evidenceId snapshotId projectId sessionId workingGeneration baseHeadRevisionId profile configuration fileId relativePath expectedFileSha256 span expectedOriginal replacement explanation");
            SpanShape(f.GetProperty("span")); ProfileShape(f.GetProperty("profile")); Configuration(f.GetProperty("configuration"));
        });
    }

    internal static void Text(string? s, int maxBytes, bool empty = false)
        => Require(s is not null && (empty && s.Length == 0 || AnalysisIdentity.IsValidOpaqueId(s, maxBytes)), "Invalid or oversized protocol text.");
    private static void Id(string? id) => Text(id, AnalysisLimits.MaxIdBytes);
    private static void Derived(string? id) => Text(id, AnalysisCatalog.MaxDerivedIdBytes);
    private static void Strings(IEnumerable<string> values, int count, int bytes)
    {
        int n = 0;
        foreach (var v in values) { Require(++n <= count, "Oversized string collection."); Text(v, bytes, empty: true); }
    }
    private static void Unique<T>(IEnumerable<T> items) => Require(items.Distinct().Count() == items.Count(), "Duplicate protocol reference.");
    internal static void Span(AnalysisSpanDto s, long length)
        => Require(s.StartByte >= 0 && s.EndByte >= s.StartByte && s.EndByte <= length, "Invalid source span.");
    private static bool SameSpan(AnalysisSpanDto? a, AnalysisSpanDto b) => a == b;
    private static void EqualMetadata(AnalysisSourceRuleDto? source, string rule, string version)
    {
        Require(source is not null, "Missing source rule metadata.");
        var expected = AnalysisCatalog.MetadataFor(version).GetProperty("rules").EnumerateArray().Single(e => e.GetProperty("ruleId").GetString() == rule);
        Require(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(source, AnalysisJson.Options), expected), "Unknown or inconsistent source rule metadata.");
    }
    private static RuleOutcome Aggregate(IEnumerable<RuleOutcome> outcomes)
    {
        static int Rank(RuleOutcome o) => o switch { RuleOutcome.Indeterminate => 4, RuleOutcome.Fail => 3, RuleOutcome.Unavailable => 2, RuleOutcome.Pass => 1, _ => 0 };
        return outcomes.DefaultIfEmpty(RuleOutcome.NotApplicable).MaxBy(Rank);
    }

    private static bool MarginOnly(AnalysisLimitation limitation) => limitation is
        AnalysisLimitation.UnsupportedGeometry or AnalysisLimitation.MissingMarginSides or AnalysisLimitation.MultipleGeometryDeclarations;
    private static bool FormattingOnly(AnalysisLimitation limitation) => limitation is
        AnalysisLimitation.PaperUncertain or AnalysisLimitation.LineSpacingUncertain or AnalysisLimitation.CaptionUncertain;
    private static bool Relevant(AnalysisLimitation limitation, string rule) => limitation switch
    {
        AnalysisLimitation.PaperUncertain => rule == AnalysisCatalog.Paper,
        AnalysisLimitation.LineSpacingUncertain => rule == AnalysisCatalog.LineSpacing,
        AnalysisLimitation.CaptionUncertain => rule == AnalysisCatalog.Caption,
        _ => LegacyRelevant(limitation, rule),
    };
    private static bool LegacyRelevant(AnalysisLimitation limitation, string rule) => MarginOnly(limitation)
        ? rule == AnalysisCatalog.Margins
        : limitation is AnalysisLimitation.UnsupportedLength or AnalysisLimitation.ConflictingDeclarations
            ? rule == AnalysisCatalog.Indent : true;

    // Evidence coverage consistency only: Rust still owns length agreement.
    private static void MarginCoverage(AnalysisKnuDto knu)
    {
        int commandCount = 0;
        foreach (var file in knu.Files.Where(f => f.Eligible))
        {
            var observations = file.Observations.Where(o => o.RuleId == AnalysisCatalog.Margins).ToArray();
            static AnalysisMarginSide Side(AnalysisObservationDto observation) => ((AnalysisMarginValueDto)observation.Value).Side;
            var commands = observations.GroupBy(o => o.CommandSpan).ToArray();
            commandCount += commands.Length;
            Require(observations.Select(Side).Distinct().Count() == 4 || file.Limitations.Contains(AnalysisLimitation.MissingMarginSides), "Missing margin side coverage limitation.");
            if (file.Complete)
                Require(commands.All(c => c.Select(Side).Distinct().Count() == 4) || file.Limitations.Contains(AnalysisLimitation.MissingMarginSides), "Missing per-declaration margin side limitation.");
            Require(!commands.Any(c => c.Count() != c.Select(Side).Distinct().Count()) || file.Limitations.Contains(AnalysisLimitation.MultipleGeometryDeclarations), "Missing duplicate margin side limitation.");
        }
        if (commandCount > 1)
            Require(knu.Files.Where(f => f.Eligible).All(f => f.Limitations.Contains(AnalysisLimitation.MultipleGeometryDeclarations)), "Missing project geometry declaration limitation.");
    }

    internal static void Validate(AnalysisResultDto r)
    {
        if (AnalysisCatalog.IsThesis(new(r.Profile.Id, r.Profile.Version)))
        {
            ThesisWireValidation.Validate(r);
            Validate(ThesisWireValidation.LegacyResult(r));
            return;
        }
        Require(AnalysisCatalog.IsValid(r.Profile.Id, r.Profile.Version, r.Configuration), "Unknown profile/configuration.");
        var k = r.Knu ?? throw new JsonException("Missing KNU data.");
        var sourceRules = AnalysisCatalog.SourceRulesFor(r.Profile.Version);
        Require(k.Files.Length == r.Facts.Files.Count && r.RuleResults.Count == sourceRules.Length + 1, "Missing or extra KNU files/rules.");
        Require(r.Facts.Files.Count <= MaxFiles && r.Facts.Files.Sum(f => (long)f.TokenCount) <= MaxTokens, "Token/file quota exceeded.");
        Require(!r.Coverage.ResultTruncated, "Completed result cannot be byte-truncated.");
        Strings(r.Coverage.FactKinds, 32, 128); Strings(r.Coverage.Unsupported, 64, 256);
        Strings(r.Coverage.TruncatedFiles, MaxFiles, 128); Unique(r.Coverage.TruncatedFiles);
        Require(r.UnavailableCapabilities.SequenceEqual(new[] { "pdfFacts", "pdfChecks" }), "Invalid unavailable capabilities.");
        var facts = r.Facts.Files.ToDictionary(f => f.FileId, StringComparer.Ordinal);
        foreach (var f in facts.Values)
        {
            Require(f.ByteLength <= AnalysisLimits.DMaxBytesPerFile && f.TokenCount <= MaxFileTokens, "File bounds exceeded.");
            foreach (var t in f.Tokens) { if (t.Text is not null) Text(t.Text, 4096, true); if (t.Note is not null) Text(t.Note, 128); }
        }
        Require(r.Facts.Files.Sum(f => f.ByteLength) <= AnalysisLimits.DMaxTotalBytes, "Project byte bound exceeded.");
        Unique(k.Files.Select(f => f.FileId));
        var evidence = new Dictionary<string, (string File, AnalysisObservationDto Observation)>(StringComparer.Ordinal);
        long emissions = r.Facts.Files.Sum(f => (long)f.TokenCount);
        foreach (var f in k.Files)
        {
            Id(f.FileId); Require(facts.ContainsKey(f.FileId), "Observation references unknown file.");
            Require(f.ObservationCount >= 0 && f.ObservationCount == f.Observations.Length, "Observation count mismatch.");
            Require(f.ObservationCount + (long)facts[f.FileId].TokenCount <= MaxFileTokens, "Shared file emissions exceeded.");
            emissions += f.ObservationCount;
            Unique(f.Limitations);
            Require(r.Profile.Version is AnalysisCatalog.LatestVersion or AnalysisCatalog.MarginFixVersion or AnalysisCatalog.GeometryPackageVersion or AnalysisCatalog.FormattingVersion || !f.Limitations.Any(MarginOnly), "Limitation is not supported by this profile version.");
            Require(r.Profile.Version == AnalysisCatalog.FormattingVersion || !f.Limitations.Any(FormattingOnly), "Limitation is not supported by this profile version.");
            Require(f.Eligible || f.Observations.IsEmpty && f.Limitations.IsEmpty, "Noneligible file has source observations.");
            foreach (var o in f.Observations)
            {
                Derived(o.Id); Require(evidence.TryAdd(o.Id, (f.FileId, o)), "Duplicate evidence ID.");
                Require(sourceRules.Contains(o.RuleId), "Foreign observation rule.");
                Require(o.Reason is not { } reasonCode || !MarginOnly(reasonCode) || o.RuleId == AnalysisCatalog.Margins, "Foreign observation limitation.");
                Require(o.Reason is not { } formattingReason || !FormattingOnly(formattingReason) || Relevant(formattingReason, o.RuleId), "Foreign formatting limitation.");
                foreach (var s in new[] { o.CommandSpan, o.DeclarationSpan, o.ValueSpan }) Span(s, facts[f.FileId].ByteLength);
                bool noArgumentSpacing = o.Value is AnalysisSpacingValueDto { Syntax: AnalysisSpacingSyntax.OneHalfSpacing };
                Require(o.CommandSpan.EndByte > o.CommandSpan.StartByte && o.DeclarationSpan.StartByte == o.CommandSpan.StartByte
                    && (noArgumentSpacing ? o.ValueSpan == o.CommandSpan && o.DeclarationSpan == o.CommandSpan : o.CommandSpan.EndByte <= o.ValueSpan.StartByte)
                    && o.ValueSpan.EndByte <= o.DeclarationSpan.EndByte, "Inconsistent observation spans.");
                if (o.Agrees is not null) Require(o.ValueSpan.EndByte > o.ValueSpan.StartByte, "Known evidence has an empty value span.");
                Require(o.Id == f.FileId + ":" + o.RuleId + ":" + o.ValueSpan.StartByte.ToString(CultureInfo.InvariantCulture), "Inconsistent evidence identity.");
                Text(o.Excerpt, 256, true);
                switch (o.Value)
                {
                    case AnalysisIndentValueDto v:
                        Require(o.RuleId == AnalysisCatalog.Indent && o.Method == "literal-setlength", "Observation kind/rule/method mismatch.");
                        Span(v.TargetSpan, facts[f.FileId].ByteLength);
                        Require(v.TargetSpan.StartByte >= o.CommandSpan.EndByte && v.TargetSpan.EndByte <= o.ValueSpan.StartByte, "Invalid target span.");
                        if (v.Length is { } n)
                        {
                            Require(o.ValueSpan.EndByte - o.ValueSpan.StartByte <= MaxKnownArgumentBytes
                                && v.TargetSpan.EndByte - v.TargetSpan.StartByte <= MaxKnownArgumentBytes,
                                "Known indent value/target exceeds producer argument bound.");
                            Require(n.NumeratorMm <= 9999999990UL && n.Denominator is 1 or 10 or 100 or 1000 && n.Unit is "mm" or "cm", "Invalid exact length representation.");
                        }
                        Require((v.Length is null) == (o.Agrees is null) && (v.Length is null) == (o.Reason is not null), "Inconsistent indent evidence.");
                        break;
                    case AnalysisMarginValueDto v:
                        Require(o.RuleId == AnalysisCatalog.Margins && o.Method == "literal-geometry-option" && Enum.IsDefined(v.Side), "Observation kind/rule/method mismatch.");
                        if (v.Length is { } marginLength)
                        {
                            Require(o.ValueSpan.EndByte - o.ValueSpan.StartByte <= MaxKnownArgumentBytes,
                                "Known margin value exceeds producer argument bound.");
                            Require(marginLength.NumeratorMm <= 9999999990UL && marginLength.Denominator is 1 or 10 or 100 or 1000 && marginLength.Unit is "mm" or "cm", "Invalid exact length representation.");
                        }
                        Require((v.Length is null) == (o.Agrees is null) && (v.Length is null) == (o.Reason is not null), "Inconsistent margin evidence.");
                        Require(o.Reason is null or AnalysisLimitation.UnsupportedGeometry, "Invalid margin observation limitation.");
                        break;
                    case AnalysisHeadingValueDto v:
                        Require(o.RuleId == AnalysisCatalog.Heading && o.Method == "literal-heading-argument", "Observation kind/rule/method mismatch.");
                        if (v.FinalScalar is { } scalar)
                        {
                            Require(o.ValueSpan.EndByte - o.ValueSpan.StartByte <= MaxKnownArgumentBytes,
                                "Known heading value exceeds producer argument bound.");
                            Text(scalar, 4); Require(Rune.TryGetRuneAt(scalar, 0, out var rune) && rune.Utf16SequenceLength == scalar.Length, "finalScalar must be one Unicode scalar.");
                        }
                        Require((v.FinalScalar is null) == (o.Agrees is null) && (v.FinalScalar is null) == (o.Reason is not null), "Inconsistent heading evidence.");
                        break;
                    case AnalysisPaperValueDto v:
                        Require(r.Profile.Version == AnalysisCatalog.FormattingVersion && o.RuleId == AnalysisCatalog.Paper && o.Method == "literal-paper-intent", "Observation kind/rule/method mismatch.");
                        Require(v.Paper is null || Enum.IsDefined(v.Paper.Value), "Invalid paper variant.");
                        Require(o.ValueSpan.EndByte - o.ValueSpan.StartByte <= MaxKnownArgumentBytes, "Paper evidence exceeds producer bound.");
                        Require(o.Reason is null or AnalysisLimitation.PaperUncertain, "Foreign paper limitation.");
                        Require((o.Agrees is null) == (o.Reason is not null) && (v.Paper is not null || o.Agrees is null), "Inconsistent paper evidence.");
                        break;
                    case AnalysisSpacingValueDto v:
                        Require(r.Profile.Version == AnalysisCatalog.FormattingVersion && o.RuleId == AnalysisCatalog.LineSpacing && o.Method == "declared-line-spacing-intent" && Enum.IsDefined(v.Syntax), "Observation kind/rule/method mismatch.");
                        if (v.Numerator is not null) Require(o.ValueSpan.EndByte - o.ValueSpan.StartByte <= MaxKnownArgumentBytes, "Spacing evidence exceeds producer bound.");
                        Require(v.Numerator.HasValue == v.Denominator.HasValue && v.Numerator.HasValue == o.Agrees.HasValue && o.Agrees.HasValue == (o.Reason is null), "Inconsistent spacing evidence.");
                        if (v.Numerator is { } numerator) Require(numerator <= 999999999 && v.Denominator is 1 or 2 or 10 or 100 or 1000, "Invalid exact scalar representation.");
                        Require(o.Reason is null or AnalysisLimitation.LineSpacingUncertain, "Foreign spacing limitation.");
                        break;
                    case AnalysisCaptionValueDto v:
                        Require(r.Profile.Version == AnalysisCatalog.FormattingVersion && o.RuleId == AnalysisCatalog.Caption && o.Method == "literal-caption-title" && Enum.IsDefined(v.Environment), "Observation kind/rule/method mismatch.");
                        Require(o.Reason is null or AnalysisLimitation.CaptionUncertain, "Foreign caption limitation.");
                        Require((v.FinalScalar is null) == (o.Agrees is null) && (o.Agrees is null) == (o.Reason is not null), "Inconsistent caption evidence.");
                        if (v.FinalScalar is { } finalScalar)
                        {
                            Require(o.ValueSpan.EndByte - o.ValueSpan.StartByte <= MaxKnownArgumentBytes, "Caption evidence exceeds producer bound.");
                            Text(finalScalar, 4); Require(Rune.TryGetRuneAt(finalScalar, 0, out var finalRune) && finalRune.Utf16SequenceLength == finalScalar.Length, "finalScalar must be one Unicode scalar.");
                        }
                        break;
                    default: throw new JsonException("Missing observation variant.");
                }
                if (o.Reason is { } reason) Require(f.Limitations.Contains(reason), "Missing observation limitation.");
            }
        }
        Require(emissions <= MaxTokens, "Shared project emissions exceeded.");
        if (r.Profile.Version == AnalysisCatalog.FormattingVersion)
        {
            foreach (var (rule, limitation) in new[] { (AnalysisCatalog.Paper, AnalysisLimitation.PaperUncertain), (AnalysisCatalog.LineSpacing, AnalysisLimitation.LineSpacingUncertain) })
                if (k.Files.Sum(f => f.Observations.Count(o => o.RuleId == rule)) > 1)
                    Require(k.Files.Where(f => f.Eligible).All(f => f.Limitations.Contains(limitation)), "Missing repeated declaration uncertainty.");
        }
        if (r.Profile.Version is AnalysisCatalog.LatestVersion or AnalysisCatalog.MarginFixVersion or AnalysisCatalog.GeometryPackageVersion or AnalysisCatalog.FormattingVersion) MarginCoverage(k);
        Unique(r.RuleResults.Select(x => x.RuleId));
        var rules = r.RuleResults.ToDictionary(x => x.RuleId, StringComparer.Ordinal);
        Require(rules.Count == sourceRules.Length + 1 && rules.ContainsKey(AnalysisCatalog.Engine) && sourceRules.All(rules.ContainsKey), "Missing/foreign/duplicate rule results.");
        var engine = rules[AnalysisCatalog.Engine];
        Require(engine.Origin == "engine" && engine.Source is null && engine.EvidenceIds is null && engine.Outcome == k.EngineAggregateOutcome, "Engine result mismatch.");
        Require(engine.PerFile.Select(f => f.FileId).ToHashSet().SetEquals(facts.Values.Where(f => f.EngineOutcome != RuleOutcome.NotApplicable).Select(f => f.FileId)), "Engine file coverage mismatch.");
        foreach (var f in engine.PerFile) Require(f.Outcome == facts[f.FileId].EngineOutcome, "Engine file outcome mismatch.");
        var expectedEngine = engine.PerFile.Any(f => f.Outcome is RuleOutcome.Indeterminate or RuleOutcome.Unavailable) ? RuleOutcome.Indeterminate
            : engine.PerFile.Any(f => f.Outcome == RuleOutcome.Fail) ? RuleOutcome.Fail
            : engine.PerFile.Count > 0 ? RuleOutcome.Pass : RuleOutcome.NotApplicable;
        Require(engine.Outcome == expectedEngine, "Preserved engine aggregate mismatch.");
        foreach (var id in sourceRules)
        {
            var rr = rules[id]; Require(rr.Origin == "knu", "Invalid rule origin."); EqualMetadata(rr.Source, id, r.Profile.Version);
            Require(rr.PerFile.Count == facts.Count && rr.PerFile.Select(f => f.FileId).ToHashSet().SetEquals(facts.Keys), "Rule file coverage mismatch.");
            Require(rr.EvidenceIds is not null, "Missing evidence references."); Unique(rr.EvidenceIds!);
            Require(rr.EvidenceIds!.ToHashSet().SetEquals(evidence.Values.Where(e => e.Observation.RuleId == id).Select(e => e.Observation.Id)), "Rule evidence references mismatch.");
            Require(rr.Outcome == Aggregate(rr.PerFile.Select(f => f.Outcome)), "Rule aggregate mismatch.");
            foreach (var pf in rr.PerFile)
            {
                var f = k.Files.Single(f => f.FileId == pf.FileId);
                Require(f.Eligible || pf.Outcome == RuleOutcome.NotApplicable, "Noneligible rule outcome.");
                Require(!f.Eligible || pf.Outcome != RuleOutcome.NotApplicable, "Eligible rule outcome.");
                var observed = f.Observations.Where(o => o.RuleId == id).ToArray();
                if (pf.Outcome == RuleOutcome.Pass) Require(observed.Length > 0 && observed.All(o => o.Agrees == true), "Pass contradicts evidence.");
                if (pf.Outcome == RuleOutcome.Fail) Require(observed.Any(o => o.Agrees == false), "Fail has no counterexample.");
                if (pf.Outcome == RuleOutcome.Unavailable) Require(observed.Length == 0, "Unavailable contradicts evidence.");
                if (f.Eligible && (id is AnalysisCatalog.Paper or AnalysisCatalog.LineSpacing) && observed.Length == 0)
                    Require(pf.Outcome == RuleOutcome.Indeterminate, "Missing declaration uncertainty lost.");
                var relevant = f.Limitations.Any(l => Relevant(l, id));
                if (f.Eligible && (!f.Complete || relevant)) Require(pf.Outcome == RuleOutcome.Indeterminate, "Coverage uncertainty lost.");
            }
        }
        Require(r.AggregateOutcome == Aggregate(r.RuleResults.Where(x => x.Origin == "knu").Select(x => x.Outcome)), "KNU aggregate mismatch.");
        if (r.Coverage.DiagnosticsTruncated)
            Require(k.Files.Where(f => f.Eligible).All(f => f.Limitations.Contains(AnalysisLimitation.DiagnosticBudget)), "Missing diagnostic coverage uncertainty.");
        foreach (var o in evidence.Values.Where(e => e.Observation.Agrees == false))
            Require(r.Coverage.DiagnosticsTruncated || r.Diagnostics.Any(d => d.Id == o.Observation.Id), "Missing counterexample diagnostic.");
        Require(r.Diagnostics.Count <= 1000, "Diagnostic quota exceeded.");
        foreach (var d in r.Diagnostics)
        {
            Derived(d.Id); Text(d.Message, 4096, true); Require(d.Severity == "warning", "Invalid severity.");
            Require(rules.TryGetValue(d.RuleId, out var rr) && rr.Origin == d.Origin, "Foreign diagnostic rule/origin.");
            var pf = rr!.PerFile.SingleOrDefault(f => f.FileId == d.FileId);
            Require(pf is not null && pf.DiagnosticIds.Contains(d.Id), "Unlinked diagnostic.");
            if (d.Origin == "knu")
            {
                Require(evidence.TryGetValue(d.Id, out var e) && e.File == d.FileId && e.Observation.RuleId == d.RuleId && e.Observation.Agrees == false, "Diagnostic evidence mismatch.");
                var s = e.Observation.ValueSpan;
                var expected = d.RuleId is AnalysisCatalog.Heading or AnalysisCatalog.Caption ? new AnalysisSpanDto(s.EndByte - 1, s.EndByte) : s;
                Require(SameSpan(d.Span, expected) && d.PrimaryLocation is null, "Diagnostic evidence span mismatch.");
            }
        }
        foreach (var rr in r.RuleResults) { Strings(rr.Limitations, MaxFiles + 4, 512); foreach (var pf in rr.PerFile) { Unique(pf.DiagnosticIds); foreach (var id in pf.DiagnosticIds) Derived(id); } }
        foreach (var id in r.Coverage.TruncatedFiles) Require(facts.ContainsKey(id), "Unknown truncated file.");
        Require(k.FixDescriptors.Length <= 1, "Too many fix descriptors.");
        Require(r.Profile.Version != AnalysisCatalog.FormattingVersion || k.FixDescriptors.IsEmpty, "Fix admission is unavailable for profile 1.4.");
        foreach (var f in k.FixDescriptors)
        {
            bool indentFix = f.Id == AnalysisCatalog.Fix;
            var marginFix = AnalysisCatalog.MarginFix(f.Id);
            Require(f.Version == AnalysisCatalog.Version && (indentFix
                ? f.RuleId == AnalysisCatalog.Indent && f.RuleVersion == AnalysisCatalog.Version
                : marginFix is not null && r.Profile.Version is AnalysisCatalog.MarginFixVersion or AnalysisCatalog.GeometryPackageVersion && f.RuleId == AnalysisCatalog.Margins && f.RuleVersion == AnalysisCatalog.MarginRuleVersionFor(r.Profile.Version)), "Unknown fix descriptor.");
            Id(f.ProjectId); Id(f.SessionId); Id(f.SnapshotId); Id(f.FileId); if (f.BaseHeadRevisionId is not null) Id(f.BaseHeadRevisionId);
            Require(f.WorkingGeneration >= 0 && f.SnapshotId == r.SnapshotId && f.Profile == r.Profile && f.Configuration == r.Configuration, "Descriptor identity mismatch.");
            Text(f.RelativePath, 1024);
            Require(ProjectPathValidator.Validate(f.RelativePath) is null && !f.RelativePath.Contains('\\'), "Unsafe descriptor path.");
            Require(IsHash(f.ExpectedFileSha256), "Malformed expected file hash."); Text(f.ExpectedOriginal, 4096); Text(f.Explanation, 4096);
            Require(f.Replacement == (indentFix ? "1.27cm" : marginFix!.Value.Replacement), "Unknown replacement.");
            Require(evidence.TryGetValue(f.EvidenceId, out var e) && e.File == f.FileId && e.Observation.RuleId == f.RuleId && e.Observation.Agrees == false && e.Observation.Reason is null && SameSpan(f.Span, e.Observation.ValueSpan), "Descriptor evidence mismatch.");
            Require(!r.Coverage.DiagnosticsTruncated, "Descriptor contradicts coverage.");
            if (indentFix)
            {
                Require(k.Files.Where(x => x.Eligible).All(x => x.Complete && x.Limitations.All(MarginOnly)), "Descriptor contradicts coverage.");
                Require(evidence.Values.Count(x => x.Observation.RuleId == AnalysisCatalog.Indent) == 1, "Descriptor violates uniqueness.");
            }
            else
            {
                Require(!r.Coverage.ResultTruncated && r.Coverage.TruncatedFiles.Count == 0
                    && k.Files.Where(x => x.Eligible).All(x => x.Complete && x.Limitations.IsEmpty), "Margin descriptor contradicts coverage.");
                var margins = evidence.Values.Where(x => x.Observation.RuleId == AnalysisCatalog.Margins).ToArray();
                Require(margins.Length == 4 && margins.All(x => x.File == f.FileId && x.Observation.Reason is null && x.Observation.Agrees is not null && x.Observation.CommandSpan == e.Observation.CommandSpan
                    && x.Observation.Value is AnalysisMarginValueDto { Length: not null })
                    && margins.Select(x => ((AnalysisMarginValueDto)x.Observation.Value).Side).Distinct().Count() == 4, "Margin descriptor requires one complete unique geometry declaration.");
                Require(e.Observation.Value is AnalysisMarginValueDto selected && selected.Side == marginFix!.Value.Side, "Margin side/fix mismatch.");
                var firstSide = new[] { AnalysisMarginSide.Left, AnalysisMarginSide.Right, AnalysisMarginSide.Top, AnalysisMarginSide.Bottom }
                    .First(side => margins.Any(x => x.Observation.Agrees == false && ((AnalysisMarginValueDto)x.Observation.Value).Side == side));
                Require(firstSide == marginFix!.Value.Side, "Margin fix priority mismatch.");
                var indents = evidence.Values.Where(x => x.Observation.RuleId == AnalysisCatalog.Indent).ToArray();
                Require(!(indents.Length == 1 && indents[0].Observation.Agrees == false
                    && k.Files.Where(x => x.Eligible).All(x => x.Complete && x.Limitations.All(MarginOnly))), "Eligible F1 has priority.");
            }
            Span(f.Span, facts[f.FileId].ByteLength);
        }
    }

    internal static bool IsHash(string? s) => s is { Length: 64 } && s.All(char.IsAsciiHexDigit);

    /// <summary>Correlation with immutable capture, not a new analysis or apply.</summary>
    internal static void Snapshot(AnalysisResultDto result, AnalysisSnapshot snapshot)
    {
        Require(result.Configuration == snapshot.Configuration, "Configuration echo mismatch.");
        Require(result.Diagnostics.Count <= snapshot.Limits.MaxDiagnosticsPerProject
            && result.Facts.Files.Sum(f => (long)f.TokenCount) <= snapshot.Limits.MaxTokensPerProject, "Response exceeds requested emission limits.");
        // KNU validates complete file equality. Engine-only retains its older
        // DTO shape; byte spans still must refer to actual captured bytes.
        var files = snapshot.Files.ToDictionary(f => f.FileId.ToString(), StringComparer.Ordinal);
        foreach (var f in result.Facts.Files)
        {
            Require(files.TryGetValue(f.FileId, out var captured), "Facts reference uncaptured file.");
            if (result.Knu is not null) Require(f.ByteLength == captured!.ByteLength && f.HasBom == captured.HasBom, "Captured file metadata mismatch.");
            foreach (var t in f.Tokens) ScalarSpan(captured!, new(t.StartByte, t.EndByte));
        }
        foreach (var d in result.Diagnostics) { Require(files.TryGetValue(d.FileId, out var f), "Diagnostic reference mismatch."); if (d.Span is { } s) ScalarSpan(f!, s); }
        ReferenceWireValidation.Snapshot(result, snapshot);
        if (AnalysisCatalog.IsThesis(snapshot.Profile)) ThesisWireValidation.Snapshot(result, snapshot);
        if (result.Knu is not { } k) return;
        Require(result.Facts.Files.Select(f => f.FileId).ToHashSet().SetEquals(files.Keys), "Captured file coverage mismatch.");
        Require(result.Facts.Files.Sum(f => (long)f.TokenCount) + k.Files.Sum(f => (long)f.ObservationCount) <= snapshot.Limits.MaxTokensPerProject, "Response exceeds requested shared emissions.");
        foreach (var f in k.Files)
        {
            Require(result.Facts.Files.Single(x => x.FileId == f.FileId).TokenCount + (long)f.ObservationCount <= snapshot.Limits.MaxTokensPerFile, "Response exceeds requested file emissions.");
            var captured = files[f.FileId];
            Require(f.Eligible == AnalysisCatalog.IsTex(captured.RelativePath), "File eligibility mismatch.");
            foreach (var o in f.Observations)
            {
                ScalarSpan(captured, o.CommandSpan); ScalarSpan(captured, o.DeclarationSpan); ScalarSpan(captured, o.ValueSpan);
                if (o.Value is AnalysisIndentValueDto indent) ScalarSpan(captured, indent.TargetSpan);
                var bytes = captured.Bytes.AsSpan()[(int)o.ValueSpan.StartByte..(int)o.ValueSpan.EndByte];
                int end = Math.Min(256, bytes.Length);
                while (end < bytes.Length && (bytes[end] & 0xc0) == 0x80) end--;
                Require(Encoding.UTF8.GetString(bytes[..end]) == o.Excerpt && o.ExcerptTruncated == (end < bytes.Length), "Excerpt does not match immutable source bytes.");
                var scalar = o.Value switch { AnalysisHeadingValueDto heading => heading.FinalScalar, AnalysisCaptionValueDto caption => caption.FinalScalar, _ => null };
                if (scalar is not null)
                {
                    // ParseResponse already requires exactly one Unicode scalar:
                    // encode at most four bytes, never decode the whole value.
                    Require(bytes.EndsWith(Encoding.UTF8.GetBytes(scalar)), "finalScalar does not match source span.");
                }
            }
        }
        foreach (var f in k.FixDescriptors)
        {
            var captured = files[f.FileId];
            Require(f.ProjectId == snapshot.ProjectId.ToString() && f.SessionId == snapshot.SessionId.ToString("N") && f.WorkingGeneration == snapshot.WorkingGeneration && f.BaseHeadRevisionId == snapshot.BaseHeadRevisionId?.ToString(), "Descriptor capture identity mismatch.");
            Require(f.RelativePath == captured.RelativePath && string.Equals(f.ExpectedFileSha256, captured.Sha256.ToString(), StringComparison.OrdinalIgnoreCase), "Descriptor file binding mismatch.");
            ScalarSpan(captured, f.Span);
            var raw = captured.Bytes.AsSpan()[(int)f.Span.StartByte..(int)f.Span.EndByte];
            Require(raw.SequenceEqual(Encoding.UTF8.GetBytes(f.ExpectedOriginal)), "Descriptor expected original mismatch.");
        }
        // No writes, saves, script invocations or proposal execution.
    }
    private static void ScalarSpan(CapturedFile f, AnalysisSpanDto s)
    {
        Span(s, f.Bytes.Length);
        bool Boundary(long n) => n == f.Bytes.Length || (f.Bytes[(int)n] & 0xc0) != 0x80;
        Require(Boundary(s.StartByte) && Boundary(s.EndByte), "Source span splits a UTF-8 scalar.");
    }
}
