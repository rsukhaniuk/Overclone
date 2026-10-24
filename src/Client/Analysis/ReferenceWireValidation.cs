using System.Globalization;
using System.Text.Json;

namespace Client.Analysis;

/// <summary>Strict finite wire/capture validation. No include traversal, key matching,
/// TeX execution or reference-rule recomputation lives here.</summary>
internal static class ReferenceWireValidation
{
    private static void Require(bool condition, string message) => KnuWireValidation.Require(condition, message);
    private static void Fields(JsonElement e, string required, string optional = "")
    {
        Require(e.ValueKind == JsonValueKind.Object, "Expected reference protocol object.");
        var needed = required.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var allowed = needed.Concat(optional.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToHashSet(StringComparer.Ordinal);
        foreach (var field in needed) Require(e.TryGetProperty(field, out _), "Missing reference protocol field.");
        foreach (var field in e.EnumerateObject()) Require(allowed.Contains(field.Name), "Extra reference protocol field.");
    }
    private static void Items(JsonElement e, int count, Action<JsonElement> check)
    {
        Require(e.ValueKind == JsonValueKind.Array && e.GetArrayLength() <= count, "Reference collection limit.");
        foreach (var item in e.EnumerateArray()) check(item);
    }
    internal static AnalysisReferenceConfigurationDto Configuration(JsonElement e)
    {
        Fields(e, "rootFileId");
        var root = e.GetProperty("rootFileId");
        Require(root.ValueKind == JsonValueKind.String, "Invalid root ID.");
        string? id;
        try { id = root.GetString(); }
        catch (InvalidOperationException) { throw new JsonException("Invalid root ID encoding."); }
        Require(AnalysisIdentity.IsValidOpaqueId(id, AnalysisLimits.MaxIdBytes), "Invalid root ID.");
        return new(id!);
    }
    internal static void Shape(JsonElement root)
    {
        Fields(root, "protocolVersion requestId status payload");
        var p = root.GetProperty("payload");
        Fields(p, "snapshotId profile referenceConfiguration facts diagnostics ruleResults aggregateOutcome coverage unavailableCapabilities");
        Fields(p.GetProperty("profile"), "id version"); Configuration(p.GetProperty("referenceConfiguration"));
        Fields(p.GetProperty("facts"), "files");
        Items(p.GetProperty("facts").GetProperty("files"), AnalysisLimits.DMaxFiles, f =>
        {
            Fields(f, "fileId byteLength hasBom newlineKind truncated braceDepthExceeded tokenCount engineOutcome tokens");
            Items(f.GetProperty("tokens"), AnalysisLimits.DMaxTokensPerFile, t => Fields(t, "kind startByte endByte", "text note"));
        });
        Items(p.GetProperty("diagnostics"), AnalysisLimits.DMaxDiagnosticsPerProject, d =>
        {
            Fields(d, "id ruleId origin severity fileId message", "span primaryLocation");
            if (d.TryGetProperty("span", out var s) && s.ValueKind != JsonValueKind.Null) Fields(s, "startByte endByte");
            if (d.TryGetProperty("primaryLocation", out var l) && l.ValueKind != JsonValueKind.Null) Fields(l, "line column");
        });
        Items(p.GetProperty("ruleResults"), 3, r =>
        {
            Fields(r, "ruleId origin outcome perFile limitations", "source evidenceIds");
            foreach (var name in new[] { "source", "evidenceIds" })
                Require(!r.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null, "Reference rules cannot carry source/fix evidence.");
            Items(r.GetProperty("perFile"), AnalysisLimits.DMaxFiles, f =>
            { Fields(f, "fileId outcome diagnosticIds"); Items(f.GetProperty("diagnosticIds"), AnalysisLimits.DMaxDiagnosticsPerProject, _ => { }); });
            Items(r.GetProperty("limitations"), r.GetProperty("ruleId").GetString() == AnalysisCatalog.Engine ? AnalysisLimits.DMaxFiles + 4 : 10, _ => { });
        });
        var coverage = p.GetProperty("coverage");
        Fields(coverage, "factKinds unsupported truncatedFiles diagnosticsTruncated resultTruncated");
        Items(coverage.GetProperty("factKinds"), 32, _ => { }); Items(coverage.GetProperty("unsupported"), 64, _ => { });
        Items(coverage.GetProperty("truncatedFiles"), AnalysisLimits.DMaxFiles, _ => { });
        Items(p.GetProperty("unavailableCapabilities"), 2, _ => { });
    }
    internal static void Request(AnalysisRequestPayload p)
    {
        Require(p.Configuration is null && p.ReferenceConfiguration is not null && p.Files is not null, "Invalid reference configuration/files.");
        Require(p.Files!.Count <= AnalysisLimits.DMaxFiles, "Reference file count limit.");
        var ids = new HashSet<string>(StringComparer.Ordinal); var paths = new HashSet<string>(StringComparer.Ordinal);
        bool root = false;
        foreach (var f in p.Files!)
        {
            Require(AnalysisIdentity.IsValidOpaqueId(f.FileId, AnalysisLimits.MaxIdBytes) && ids.Add(f.FileId)
                && ReferenceChecks.IsCanonicalPath(f.RelativePath) && paths.Add(f.RelativePath)
                && KnuWireValidation.IsHash(f.Sha256), "Invalid or duplicate reference descriptor.");
            if (f.FileId == p.ReferenceConfiguration!.RootFileId)
            { Require(f.MediaKind == "text" && AnalysisCatalog.IsTex(f.RelativePath), "Root must be .tex text."); root = true; }
        }
        Require(root, "Root is not captured.");
    }
    internal static void ValidateCapture(AnalysisSnapshot snapshot)
    {
        Require(snapshot.Selection.IsValid, "Invalid reference selection.");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in snapshot.Files) Require(ReferenceChecks.IsCanonicalPath(file.RelativePath) && paths.Add(file.RelativePath)
            && KnuWireValidation.IsHash(file.Sha256.ToString()), "Invalid reference path/hash.");
        var root = snapshot.Files.Where(f => f.FileId.ToString() == snapshot.ReferenceConfiguration!.RootFileId).ToArray();
        Require(root.Length == 1 && AnalysisCatalog.IsTex(root[0].RelativePath), "Reference root is not captured .tex text.");
    }
    internal static RuleOutcome Aggregate(IEnumerable<RuleOutcome> values)
    {
        var outcomes = values.ToHashSet();
        return outcomes.Contains(RuleOutcome.Indeterminate) ? RuleOutcome.Indeterminate
            : outcomes.Contains(RuleOutcome.Unavailable) ? RuleOutcome.Unavailable
            : outcomes.Contains(RuleOutcome.Fail) ? RuleOutcome.Fail
            : outcomes.Contains(RuleOutcome.Pass) ? RuleOutcome.Pass : RuleOutcome.NotApplicable;
    }
    internal static void Validate(AnalysisResultDto r)
    {
        Require(AnalysisCatalog.IsValid(r.Profile.Id, r.Profile.Version, r.Configuration, r.ReferenceConfiguration) && r.Knu is null, "Invalid reference profile/configuration.");
        Require(r.RuleResults.Count == 3 && r.RuleResults.Select(x => x.RuleId).ToHashSet().SetEquals(new[] { AnalysisCatalog.Engine, AnalysisCatalog.Labels, AnalysisCatalog.References }), "Missing/duplicate/foreign reference rules.");
        Require(!r.Coverage.ResultTruncated && r.Facts.Files.Count <= AnalysisLimits.DMaxFiles
            && r.Facts.Files.Sum(f => (long)f.TokenCount) <= AnalysisLimits.DMaxTokensPerProject
            && r.Facts.Files.All(f => f.ByteLength <= AnalysisLimits.DMaxBytesPerFile)
            && r.Facts.Files.Sum(f => f.ByteLength) <= AnalysisLimits.DMaxTotalBytes, "Reference result exceeds limits.");
        Require(r.UnavailableCapabilities.SequenceEqual(new[] { "pdfFacts", "pdfChecks" }), "Invalid unavailable capabilities.");
        var facts = r.Facts.Files.ToDictionary(f => f.FileId, StringComparer.Ordinal);
        Require(facts.ContainsKey(r.ReferenceConfiguration!.RootFileId), "Root facts missing.");
        foreach (var f in facts.Values)
        {
            Require(f.ByteLength <= AnalysisLimits.DMaxBytesPerFile && f.TokenCount <= AnalysisLimits.DMaxTokensPerFile, "Reference file limit.");
            foreach (var t in f.Tokens)
            { if (t.Text is not null) KnuWireValidation.Text(t.Text, 4096, true); if (t.Note is not null) KnuWireValidation.Text(t.Note, 128); }
        }
        var rules = r.RuleResults.ToDictionary(x => x.RuleId, StringComparer.Ordinal);
        var reachable = rules[AnalysisCatalog.Labels].PerFile.Select(x => x.FileId).ToArray();
        Require(reachable.Contains(r.ReferenceConfiguration!.RootFileId) && reachable.SequenceEqual(rules[AnalysisCatalog.References].PerFile.Select(x => x.FileId)), "Inconsistent reachable identities/order.");
        // Existing Rust lexical scope excludes metadata-only non-text facts.
        // Facts carry no mediaKind: accept that omission signal ONLY as NA,
        // zero tokens, and no scan/depth flags. Never omit a scanned fact.
        Require(r.Facts.Files.Where(f => f.EngineOutcome == RuleOutcome.NotApplicable)
            .All(f => f.TokenCount == 0 && f.Tokens.Count == 0 && !f.Truncated && !f.BraceDepthExceeded), "Invalid metadata-only structural facts.");
        var structuralFacts = r.Facts.Files.Where(f => f.EngineOutcome != RuleOutcome.NotApplicable);
        Require(rules[AnalysisCatalog.Engine].PerFile.Select(f => f.FileId).SequenceEqual(structuralFacts.Select(f => f.FileId))
            && rules[AnalysisCatalog.Engine].PerFile.All(f => f.Outcome == facts[f.FileId].EngineOutcome), "Structural rule/facts coverage mismatch.");
        var literalFacts = r.Facts.Files.SelectMany(f => f.Tokens.Where(t => (t.Kind is TokenKind.Label or TokenKind.Reference) && t.Text is not null && t.Note is null)
            .Select(t => (File: f.FileId, t.Kind, t.StartByte, t.EndByte))).ToHashSet();
        var diagnostics = r.Diagnostics.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rr in r.RuleResults)
        {
            Require(rr.Origin == "engine" && rr.Source is null && rr.EvidenceIds is null, "Foreign origin/metadata.");
            bool reference = rr.RuleId != AnalysisCatalog.Engine;
            Require(rr.Limitations.Distinct(StringComparer.Ordinal).Count() == rr.Limitations.Count, "Duplicate limitation.");
            foreach (var code in rr.Limitations) KnuWireValidation.Text(code, 512, true);
            if (reference) Require(rr.Limitations.Count <= 10 && rr.Limitations.All(ReferenceChecks.IsLimitation)
                && rr.Limitations.Contains("sourceIntentOnly") && rr.Limitations.Contains("macroExpansionUnsupported"), "Invalid reference limitations.");
            bool uncertain = reference && rr.Limitations.Any(ReferenceChecks.Observed);
            Require(!reference || uncertain || reachable.All(id => !facts[id].Truncated && !facts[id].BraceDepthExceeded), "Missing incomplete-scan qualification.");
            Require(rr.Outcome == Aggregate(rr.PerFile.Select(x => x.Outcome)), "Inconsistent rule aggregate.");
            foreach (var pf in rr.PerFile)
            {
                Require(facts.ContainsKey(pf.FileId), "Unknown per-file identity.");
                Require(!reference || (uncertain ? pf.Outcome == RuleOutcome.Indeterminate
                    : pf.DiagnosticIds.Count > 0 ? pf.Outcome == RuleOutcome.Fail
                    : pf.Outcome is RuleOutcome.Pass or RuleOutcome.NotApplicable), "Inconsistent reference outcome/uncertainty.");
                Require(rr.RuleId != AnalysisCatalog.References || !uncertain || pf.DiagnosticIds.Count == 0, "Missing-reference warning under uncertainty.");
                foreach (var id in pf.DiagnosticIds)
                {
                    Require(listed.Add(id) && diagnostics.TryGetValue(id, out var d) && d.FileId == pf.FileId && d.RuleId == rr.RuleId, "Duplicate/dangling diagnostic reference.");
                }
            }
        }
        Require(listed.SetEquals(diagnostics.Keys), "Unlisted diagnostic.");
        foreach (var d in r.Diagnostics)
        {
            KnuWireValidation.Text(d.Id, AnalysisCatalog.MaxDerivedIdBytes);
            KnuWireValidation.Text(d.Message, 4096);
            Require(d.Severity is "error" or "warning" or "unknown", "Invalid diagnostic severity.");
            Require(d.Origin == "engine" && rules.ContainsKey(d.RuleId), "Foreign diagnostic origin/rule.");
            if (d.RuleId == AnalysisCatalog.Engine) continue;
            Require(d.Severity == "warning" && d.Message == (d.RuleId == AnalysisCatalog.Labels ? ReferenceChecks.DuplicateMessage : ReferenceChecks.MissingMessage), "Invalid reference severity/message.");
            Require(d.Span is { } s && s.EndByte > s.StartByte && d.PrimaryLocation is not null, "Missing reference location/span.");
            var span = d.Span!;
            Require(d.Id == d.RuleId + ":" + d.FileId + ":" + span.StartByte.ToString(CultureInfo.InvariantCulture), "Invalid derived diagnostic identity.");
            Require(literalFacts.Contains((d.FileId, d.RuleId == AnalysisCatalog.Labels ? TokenKind.Label : TokenKind.Reference, span.StartByte, span.EndByte)), "Diagnostic has no matching literal argument fact.");
        }
        Require(r.AggregateOutcome == Aggregate(r.RuleResults.Select(x => x.Outcome)), "Inconsistent whole result outcome.");
        Require(r.Coverage.TruncatedFiles.Distinct().Count() == r.Coverage.TruncatedFiles.Count && r.Coverage.TruncatedFiles.All(facts.ContainsKey), "Invalid coverage identities.");
        foreach (var s in r.Coverage.FactKinds.Concat(r.Coverage.Unsupported)) KnuWireValidation.Text(s, 512, true);
    }
    internal static void Snapshot(AnalysisResultDto result, AnalysisSnapshot snapshot)
    {
        Require(result.ReferenceConfiguration == snapshot.ReferenceConfiguration, "Root configuration echo mismatch.");
        if (snapshot.Profile.Id != AnalysisCatalog.ReferenceId) return;
        ValidateCapture(snapshot);
        Require(result.Facts.Files.Select(f => f.FileId).SequenceEqual(snapshot.Files.Select(f => f.FileId.ToString())), "Captured facts/order mismatch.");
        // The C# packer sends only eligible captured text, never binary bodies.
        // Raw metadata-only omissions must not hide any such captured file.
        Require(result.RuleResults.Single(r => r.RuleId == AnalysisCatalog.Engine).PerFile.Select(f => f.FileId)
            .SequenceEqual(snapshot.Files.Select(f => f.FileId.ToString())), "Captured structural text scope mismatch.");
        var facts = result.Facts.Files.ToDictionary(f => f.FileId, StringComparer.Ordinal);
        var diagnostics = result.Diagnostics.Where(d => d.RuleId != AnalysisCatalog.Engine).GroupBy(d => d.FileId).ToDictionary(g => g.Key, g => g.OrderBy(d => d.Span!.StartByte).ToArray(), StringComparer.Ordinal);
        var reachable = result.RuleResults.Single(r => r.RuleId == AnalysisCatalog.Labels).PerFile.Select(f => f.FileId).ToHashSet(StringComparer.Ordinal);
        Require(result.RuleResults.Single(r => r.RuleId == AnalysisCatalog.Labels).PerFile.Select(f => f.FileId)
            .SequenceEqual(snapshot.Files.Where(f => reachable.Contains(f.FileId.ToString())).Select(f => f.FileId.ToString())), "Reachable order mismatch.");
        foreach (var f in snapshot.Files)
        {
            string id = f.FileId.ToString(); var fact = facts[id];
            Require(fact.ByteLength == f.ByteLength && fact.HasBom == f.HasBom && fact.TokenCount <= snapshot.Limits.MaxTokensPerFile, "Captured metadata/limits mismatch.");
            foreach (var token in fact.Tokens.Where(t => (t.Kind is TokenKind.Label or TokenKind.Reference) && t.Text is not null))
            {
                var encoded = System.Text.Encoding.UTF8.GetBytes(token.Text!);
                Require(encoded.Length == token.EndByte - token.StartByte
                    && f.Bytes.AsSpan().Slice((int)token.StartByte, encoded.Length).SequenceEqual(encoded), "Literal fact text differs from captured bytes.");
            }
            Require(!reachable.Contains(id) || AnalysisCatalog.IsTex(f.RelativePath), "Reachable file is not .tex.");
            if (!diagnostics.TryGetValue(id, out var entries)) continue;
            // One monotonic byte scan per file, no per-diagnostic full-file decode/copy.
            int offset = 0, line = 1, lineStart = 0;
            foreach (var d in entries)
            {
                int start = checked((int)d.Span!.StartByte);
                Require(start >= (f.HasBom ? 3 : 0), "Reference span covers BOM.");
                while (offset < start)
                {
                    byte b = f.Bytes[offset];
                    if (b == '\n' || b == '\r' && (offset + 1 == f.Bytes.Length || f.Bytes[offset + 1] != '\n'))
                    { line++; lineStart = offset + 1; }
                    offset++;
                }
                Require(d.PrimaryLocation == new AnalysisLocationDto(line, start - lineStart), "Reference primary location differs from captured bytes.");
            }
        }
    }
}
