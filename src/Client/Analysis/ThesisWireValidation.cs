using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Workspace.Transfer;

namespace Client.Analysis;

/// <summary>Closed protocol/capture consistency only. Rust owns scholarly and
/// literal predicates; this code never infers missing sections or PDF facts.</summary>
internal static class ThesisWireValidation
{
    private static void Need(bool ok) => KnuWireValidation.Require(ok, "Invalid thesis source protocol or capture.");
    private static readonly HashSet<string> Limitations = new((
        "ambiguousAppendixHeading bibitemOutsideBibliography bibliographyBackendNotEvaluated bibliographyStyleExecutionUnsupported " +
        "captionOutsideFloat classContextUncertain conditionalExecutionUnsupported customClass cyclicInclude declarationDefaultUnsupported " +
        "declarationEffectUncertain documentContextUncertain duplicateLabel dynamicBibliography dynamicCaption dynamicCitation " +
        "dynamicCitationOrBibliography dynamicConstruct dynamicDeclaration dynamicEnvironment dynamicHeading dynamicOrUnsafeBibliography " +
        "dynamicOrUnsafeInclude dynamicPackage dynamicReference dynamicReferenceOrCitation groupedExecutionUnsupported incompleteConditional " +
        "incompleteDocumentBoundaries incompleteScan macroOrPackageExecutionUnsupported malformedBibliography missingBibliography missingInclude " +
        "nestedFloat nonTextBibliography nonTextInclude nonTextOrNonTexInclude optionalEnvironmentSyntax outsideDocumentExecution " +
        "projectPackageExecutionUnsupported redefinitionUncertain repeatedInclude unbalancedEnvironment unclosedEnvironment unknownHeadingRole " +
        "unknownPackage unsupportedContext unsupportedEnvironment").Split(' '), StringComparer.Ordinal);
    private static readonly HashSet<string> EventKinds = new((
        "title abstract contents abbreviations introduction body conclusions references appendix appendixMode unknownHeadingRole " +
        "cite bibitem bibliographyStart bibliographyKey externalBibliography floatCaptionCount captionTitle").Split(' '), StringComparer.Ordinal);
    private const string TitleReason = "semantic-content-and-signatures-unavailable", AbstractReason = "semantic-abstract-classification-unavailable";
    internal static void Fields(JsonElement e, string required)
    {
        Need(e.ValueKind == JsonValueKind.Object);
        var fields = required.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Need(e.EnumerateObject().Count() == fields.Length && fields.All(f => e.TryGetProperty(f, out _)));
    }
    private static void Items(JsonElement e, int max, Action<JsonElement> item)
    { Need(e.ValueKind == JsonValueKind.Array && e.GetArrayLength() <= max); foreach (var v in e.EnumerateArray()) item(v); }
    private static void Text(JsonElement e, string name, int max = 512, bool empty = false)
    { var v = e.GetProperty(name); Need(v.ValueKind == JsonValueKind.String); KnuWireValidation.Text(v.GetString(), max, empty); }
    private static void SpanShape(JsonElement e)
    { Fields(e, "startByte endByte"); foreach (var n in e.EnumerateObject()) Need(n.Value.TryGetInt64(out var v) && v >= 0 && v <= AnalysisLimits.DMaxBytesPerFile); }
    internal static void Shape(JsonElement root)
    {
        try { ShapeCore(root); }
        catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { throw new JsonException("Malformed thesis source response."); }
    }
    private static void ShapeCore(JsonElement root)
    {
        Fields(root, "protocolVersion requestId status payload");
        var p = root.GetProperty("payload");
        Fields(p, "snapshotId profile facts diagnostics ruleResults aggregateOutcome coverage unavailableCapabilities configuration knu referenceConfiguration thesis");
        Fields(p.GetProperty("profile"), "id version");
        KnuWireValidation.Configuration(p.GetProperty("configuration"));
        ReferenceWireValidation.Configuration(p.GetProperty("referenceConfiguration"));
        Items(p.GetProperty("facts").GetProperty("files"), AnalysisLimits.DMaxFiles, f =>
        { Need(f.GetProperty("byteLength").TryGetInt64(out var n) && n >= 0 && n <= AnalysisLimits.DMaxBytesPerFile); });
        var profileVersion = p.GetProperty("profile").GetProperty("version").GetString()!;
        Need(JsonElement.DeepEquals(p.GetProperty("knu").GetProperty("metadata"), AnalysisCatalog.MetadataFor(profileVersion)));
        var t = p.GetProperty("thesis");
        Fields(t, "schemaVersion catalogId catalogVersion guidelineSha256 rootFileId workType domain globalCoverage renderedCompliance completeLiteralScan limitations closureFileIds events ruleResults unavailableCells workTypeDependentTitle bibliographyMode firstLiteralCitationKeys");
        foreach (var name in new[] { "catalogId", "catalogVersion", "guidelineSha256", "rootFileId", "workType", "domain", "globalCoverage", "renderedCompliance", "workTypeDependentTitle", "bibliographyMode" }) Text(t, name, 128);
        Need(t.GetProperty("catalogVersion").GetString() == ThesisCatalog.VersionForProfile(profileVersion));
        Need(t.GetProperty("schemaVersion").TryGetInt32(out var schema) && schema == 1);
        Need(t.GetProperty("completeLiteralScan").ValueKind is JsonValueKind.True or JsonValueKind.False);
        foreach (var (name, max, bytes) in new[] { ("limitations",64,64), ("closureFileIds",5000,128), ("firstLiteralCitationKeys",1000,512) })
            Items(t.GetProperty(name), max, v => { Need(v.ValueKind == JsonValueKind.String); KnuWireValidation.Text(v.GetString(), bytes); });
        Items(t.GetProperty("events"), 1000, e =>
        {
            Fields(e, "kind fileId span text appendix"); Text(e,"kind",64); Text(e,"fileId",128); Text(e,"text",512,true);
            SpanShape(e.GetProperty("span")); Need(e.GetProperty("appendix").ValueKind is JsonValueKind.True or JsonValueKind.False);
        });
        Items(t.GetProperty("ruleResults"), 5, r =>
        {
            Fields(r,"ruleId ruleVersion domain supportState outcome reason method knownCounterexample fixPolicy severity citation evidenceIndexes");
            foreach (var name in new[] { "ruleId","ruleVersion","domain","supportState","outcome","reason","method","fixPolicy","severity" }) Text(r,name,128);
            Need(r.GetProperty("knownCounterexample").ValueKind is JsonValueKind.True or JsonValueKind.False);
            Fields(r.GetProperty("citation"), "section printedPages pdfPages"); Text(r.GetProperty("citation"),"section",128);
            foreach (var pages in new[]{"printedPages","pdfPages"}) Items(r.GetProperty("citation").GetProperty(pages),58,v=>Need(v.TryGetInt32(out var page)&&page is >=1 and <=58));
            Items(r.GetProperty("evidenceIndexes"),1000,v=>Need(v.TryGetInt32(out var index)&&index is >=0 and <1000));
        });
        Items(t.GetProperty("unavailableCells"),2,c=>{Fields(c,"ruleId reason"); Text(c,"ruleId",128);Text(c,"reason",128);});
    }

    // A bounded legacy-format view reuses the exact accepted 1.4 guards. It does
    // not widen any legacy version or mutate/replace the original versioned payload.
    internal static JsonDocument LegacyDocument(JsonElement root)
    {
        var node = JsonNode.Parse(root.GetRawText())!.AsObject(); var p = node["payload"]!.AsObject();
        p.Remove("thesis"); p.Remove("referenceConfiguration");
        p["profile"]!["version"] = AnalysisCatalog.FormattingVersion;
        p["knu"]!["metadata"]!["profile"]!["version"] = AnalysisCatalog.FormattingVersion;
        p["unavailableCapabilities"] = new JsonArray("pdfFacts","pdfChecks");
        return JsonDocument.Parse(node.ToJsonString());
    }
    internal static AnalysisResultDto LegacyResult(AnalysisResultDto r)
    {
        var knu = r.Knu ?? throw new JsonException("Missing KNU data.");
        return r with {
            Profile = r.Profile with { Version = AnalysisCatalog.FormattingVersion }, ReferenceConfiguration = null, Thesis = null,
            Knu = knu with { Metadata = knu.Metadata with { Profile = knu.Metadata.Profile with { Version = AnalysisCatalog.FormattingVersion } } },
            UnavailableCapabilities = ImmutableArray.Create("pdfFacts", "pdfChecks"),
        };
    }
    internal static void Validate(AnalysisResultDto r)
    {
        var t = r.Thesis ?? throw new JsonException("Missing thesis source report.");
        Need(AnalysisCatalog.IsValid(r.Profile.Id,r.Profile.Version,r.Configuration,r.ReferenceConfiguration));
        Need(t.SchemaVersion==1 && t.CatalogId==ThesisCatalog.Id && t.CatalogVersion==ThesisCatalog.VersionForProfile(r.Profile.Version) && t.GuidelineSha256==ThesisCatalog.GuidelineSha256);
        Need(t.RootFileId==r.ReferenceConfiguration!.RootFileId && t.WorkType==r.Configuration!.WorkType);
        Need(t.Domain=="sourceObservation" && t.GlobalCoverage=="incomplete" && t.RenderedCompliance=="notAssessed");
        Need(r.Knu is not null && r.Knu.FixDescriptors.IsEmpty);
        Need(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(r.Knu!.Metadata,AnalysisJson.Options),AnalysisCatalog.MetadataFor(r.Profile.Version)));
        Need(r.UnavailableCapabilities.SequenceEqual(new[]{"pdfFacts","pdfChecks","thesisRenderedChecks"}));
        Need(!t.Limitations.IsDefault && !t.ClosureFileIds.IsDefault && !t.Events.IsDefault && !t.RuleResults.IsDefault && !t.UnavailableCells.IsDefault && !t.FirstLiteralCitationKeys.IsDefault);
        Need(t.Limitations.Length<=64 && t.Limitations.Distinct(StringComparer.Ordinal).Count()==t.Limitations.Length && t.Limitations.All(Limitations.Contains));
        Need(t.CompleteLiteralScan==t.Limitations.IsEmpty);
        var closure=t.ClosureFileIds.ToHashSet(StringComparer.Ordinal);
        Need(t.ClosureFileIds.Length is >0 and <=5000 && closure.Count==t.ClosureFileIds.Length && closure.Contains(t.RootFileId));
        var facts=r.Facts.Files.ToDictionary(f=>f.FileId,StringComparer.Ordinal);
        foreach (var id in t.ClosureFileIds) { KnuWireValidation.Text(id,128); Need(facts.ContainsKey(id)); }
        Need(r.Facts.Files.Where(f=>closure.Contains(f.FileId)).Select(f=>f.FileId).SequenceEqual(t.ClosureFileIds));
        Need(t.Events.Length+(long)r.Diagnostics.Count<=AnalysisLimits.DMaxDiagnosticsPerProject);
        foreach (var e in t.Events)
        {
            Need(EventKinds.Contains(e.Kind)); KnuWireValidation.Text(e.Text,512,true); KnuWireValidation.Text(e.FileId,128);
            Need(facts.TryGetValue(e.FileId,out var f)); KnuWireValidation.Span(e.Span,f!.ByteLength);
            Need(e.Kind=="bibliographyKey" || closure.Contains(e.FileId));
        }
        Need(t.RuleResults.Length==5);
        bool appendix=t.Events.Any(e=>e.Appendix && e.Kind is "cite" or "bibitem");
        bool external=t.Events.Any(e=>e.Kind=="externalBibliography");
        for (int i=0;i<5;i++)
        {
            var rr=t.RuleResults[i];
            Need(rr.RuleId==ThesisCatalog.RuleIds[i] && rr.RuleVersion=="1.0.0" && rr.Domain=="sourceObservation"
                && rr.SupportState=="partial" && rr.Method==ThesisCatalog.Methods[i] && rr.FixPolicy=="none" && rr.Severity=="warning");
            Need(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(rr.Citation,AnalysisJson.Options),ThesisCatalog.SourceRule(rr.RuleId,t.CatalogVersion).GetProperty("citation")));
            Need(!rr.EvidenceIndexes.IsDefault && rr.EvidenceIndexes.SequenceEqual(Enumerable.Range(0,t.Events.Length)));
            bool scoped=appendix && i is 1 or 2;
            if (scoped || !t.Limitations.IsEmpty)
                Need(rr.Outcome==RuleOutcome.Indeterminate && rr.Reason==(scoped?"appendix-citation-scope-unsupported":"literal-context-uncertain"));
            else if (external && i==2)
                Need(rr.Outcome==RuleOutcome.Unavailable && rr.Reason=="compiled-bibliography-order-unavailable" && !rr.KnownCounterexample);
            else Need(rr.Outcome switch {
                RuleOutcome.Pass => rr.Reason=="observed-literals-only" && !rr.KnownCounterexample && HasPrerequisiteEvents(t,i),
                RuleOutcome.Fail => rr.Reason=="literal-counterexample" && rr.KnownCounterexample,
                RuleOutcome.Unavailable => rr.Reason=="no-supported-observation" && !rr.KnownCounterexample,
                _ => false,
            });
        }
        Need(t.UnavailableCells.Length==2 && t.UnavailableCells[0]==new AnalysisThesisUnavailableDto("KNU-STRUCTURE-001",TitleReason)
            && t.UnavailableCells[1]==new AnalysisThesisUnavailableDto("KNU-ABSTRACT-001",AbstractReason));
        Need(t.WorkTypeDependentTitle==(t.WorkType switch { AnalysisWorkType.Unspecified=>"unavailable-work-type",AnalysisWorkType.Coursework=>"not-applicable-coursework",_=>"unavailable-title-metadata"}));
        Need(t.BibliographyMode is "none" or "manual" or "external" or "mixed");
        Need(t.BibliographyMode==(external?(t.Events.Any(e=>e.Kind=="bibitem"&&!e.Appendix)?"mixed":"external"):(t.Events.Any(e=>e.Kind=="bibitem"&&!e.Appendix)?"manual":"none")));
        Need(t.FirstLiteralCitationKeys.SequenceEqual(t.Events.Where(e=>e.Kind=="cite"&&!e.Appendix).Select(e=>e.Text).Distinct(StringComparer.Ordinal)));
    }
    // Coverage links only: consume evaluator-labelled roles, do not classify
    // source text, resolve keys, compare order, letters, lengths or typography.
    private static bool HasPrerequisiteEvents(AnalysisThesisDto t,int rule)
    {
        bool Has(string kind)=>t.Events.Any(e=>e.Kind==kind&&!e.Appendix);
        return rule switch {
            0 => Has("title")&&Has("abstract")&&Has("contents")&&Has("introduction")&&Has("body")&&Has("conclusions")
                && (Has("references")||Has("bibliographyStart")||Has("externalBibliography")),
            1 => Has("cite")&&(Has("bibitem")||t.Events.Any(e=>e.Kind=="bibliographyKey")),
            2 => Has("cite")&&Has("bibitem"),
            3 => t.Events.Any(e=>e.Kind=="appendix"),
            4 => t.Events.Any(e=>e.Kind=="captionTitle"),
            _ => false,
        };
    }
    internal static bool ValidLimits(AnalysisLimitsDto? l) => l is null || new (long? Value,long Max)[] {
        (l.MaxBytesPerFile,AnalysisLimits.DMaxBytesPerFile),(l.MaxFiles,AnalysisLimits.DMaxFiles),
        (l.MaxTotalBytes,AnalysisLimits.DMaxTotalBytes),(l.MaxResultBytes,AnalysisLimits.DMaxResultBytes),
        (l.MaxTokensPerFile,AnalysisLimits.DMaxTokensPerFile),(l.MaxTokensPerProject,AnalysisLimits.DMaxTokensPerProject),
        (l.MaxDiagnosticsPerProject,AnalysisLimits.DMaxDiagnosticsPerProject),(l.MaxBraceDepth,AnalysisLimits.DMaxBraceDepth),
    }.All(p=>p.Value is null || p.Value>0 && p.Value<=p.Max);
    internal static void Request(AnalysisRequestEnvelope envelope)
    {
        var p=envelope.Payload;
        Need(envelope.ProtocolVersion==1 && envelope.Operation=="analyzeSource"); KnuWireValidation.Text(envelope.RequestId,128);
        Need(p.WorkingGeneration is >=0 and <=9007199254740991 && p.Limits is not null && ValidLimits(p.Limits));
        foreach(var id in new[]{p.SnapshotId,p.ProjectId,p.SessionId}) KnuWireValidation.Text(id,128);
        if(p.BaseHeadRevisionId is not null) KnuWireValidation.Text(p.BaseHeadRevisionId,128);
        Need(p.Files is not null && p.Configuration is not null && p.ReferenceConfiguration is not null);
        // Existing descriptor/path/root guard, with its engine-only configuration
        // removed in this temporary DATA view only. Original request stays KNU.
        ReferenceWireValidation.Request(p with { Configuration=null });
        var limits=AnalysisLimits.Resolve(p.Limits); long total=0;
        foreach(var f in p.Files!)
        {
            Need(f.MediaKind=="text" && f.ByteOffset>=0 && f.ByteLength>=0 && f.ByteLength<=limits.MaxBytesPerFile
                && !f.RelativePath.StartsWith("__oop_runtime__/",StringComparison.Ordinal));
            Need(f.ByteOffset==total); total=checked(total+f.ByteLength); Need(total<=limits.MaxTotalBytes);
        }
        Need(p.Files.Count<=limits.MaxFiles);
    }
    internal static void ValidateCapture(AnalysisSnapshot s)
    {
        Need(AnalysisCatalog.IsThesis(s.Profile) && s.Selection.IsValid && s.WorkingGeneration is >=0 and <=9007199254740991);
        var l=s.Limits;
        Need(ValidLimits(new(l.MaxBytesPerFile,l.MaxFiles,l.MaxTotalBytes,l.MaxResultBytes,l.MaxTokensPerFile,l.MaxTokensPerProject,l.MaxDiagnosticsPerProject,l.MaxBraceDepth)));
        Need(!s.Files.IsDefault && s.Files.Length<=l.MaxFiles);
        ReferenceWireValidation.ValidateCapture(s);
        long total=0;
        foreach(var f in s.Files)
        {
            Need(!f.RelativePath.StartsWith("__oop_runtime__/",StringComparison.Ordinal) && !f.Bytes.IsDefault && f.ByteLength==f.Bytes.Length && f.ByteLength<=l.MaxBytesPerFile);
            total=checked(total+f.ByteLength);Need(total<=l.MaxTotalBytes);
        }
        // Preflight the whole input before decoding/hashing any privately owned file.
        foreach(var f in s.Files)
        {
            Need(TextContent.TryDecode(f.Bytes.ToArray(),out _,out var bom) && bom==f.HasBom);
            Need(string.Equals(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(f.Bytes.AsSpan())),f.Sha256.ToString(),StringComparison.Ordinal));
        }
    }
    internal static void Snapshot(AnalysisResultDto r,AnalysisSnapshot s)
    {
        ValidateCapture(s); Validate(r);
        var t=r.Thesis!;
        Need(r.SnapshotId==s.Id.ToString() && r.Profile.Id==s.Profile.Id && r.Profile.Version==s.Profile.Version
            && r.Configuration==s.Configuration && r.ReferenceConfiguration==s.ReferenceConfiguration);
        Need(t.Events.Length+(long)r.Diagnostics.Count<=s.Limits.MaxDiagnosticsPerProject);
        var files=s.Files.ToDictionary(f=>f.FileId.ToString(),StringComparer.Ordinal);
        foreach(var id in t.ClosureFileIds)Need(files.TryGetValue(id,out var f)&&AnalysisCatalog.IsTex(f.RelativePath));
        foreach(var e in t.Events)
        {
            Need(files.TryGetValue(e.FileId,out var f)); var captured=f!;
            Need(e.Kind=="bibliographyKey"?captured.RelativePath.EndsWith(".bib",StringComparison.Ordinal):AnalysisCatalog.IsTex(captured.RelativePath));
            ScalarSpan(captured,e.Span);
            var raw=Encoding.UTF8.GetString(captured.Bytes.AsSpan().Slice((int)e.Span.StartByte,(int)(e.Span.EndByte-e.Span.StartByte)));
            bool match=e.Kind switch {
                "title" => raw=="\\maketitle" && e.Text=="maketitle",
                "appendixMode" => raw=="\\appendix" && e.Text=="appendix",
                "contents" when e.Text=="tableofcontents" => raw=="\\tableofcontents",
                "floatCaptionCount" => raw is "figure" or "table" && int.TryParse(e.Text,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var n)
                    && n>=0 && n<=1000 && e.Text==n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "bibliographyStart" => raw=="thebibliography" && e.Text==raw,
                "cite" or "bibitem" or "bibliographyKey" or "captionTitle" or "externalBibliography" => raw==e.Text,
                _ => raw.Trim()==e.Text,
            }; Need(match);
        }
    }
    private static void ScalarSpan(CapturedFile f,AnalysisSpanDto span)
    {
        KnuWireValidation.Span(span,f.Bytes.Length); Need(span.StartByte>=(f.HasBom?3:0));
        bool Boundary(long n)=>n==f.Bytes.Length || (f.Bytes[(int)n]&0xc0)!=0x80;
        Need(Boundary(span.StartByte)&&Boundary(span.EndByte));
    }
}

/// <summary>Read-only public capture boundary for canonical source reports.</summary>
public static class ThesisValidation
{
    /// <summary>Expected-request entry for the existing adapter's opt-in branch.
    /// Legacy ParseResponse remains unchanged; failed bodies also stay closed.</summary>
    public static AnalysisResponse ParseResponse(string json, AnalysisSnapshot snapshot, string requestId)
    {
        if (!AnalysisCatalog.IsThesis(snapshot.Profile) || !AnalysisIdentity.IsValidOpaqueId(requestId,128))
            throw new JsonException("Invalid thesis response context.");
        var response=AnalysisJson.ParseResponse(json);
        if (response is AnalysisResponse.Completed completed)
        {
            KnuWireValidation.Require(completed.RequestId==requestId && Encoding.UTF8.GetByteCount(json)<=snapshot.Limits.MaxResultBytes,"Thesis response identity or budget mismatch.");
            ValidateCapturedResult(completed.Result,snapshot);
        }
        else if (response is AnalysisResponse.Failed failed)
        {
            using var doc=JsonDocument.Parse(json);var root=doc.RootElement;
            KnuWireValidation.Require(root.EnumerateObject().All(p=>p.Name is "protocolVersion" or "requestId" or "status" or "error" or "payload"),"Unexpected failure field.");
            ThesisWireValidation.Fields(root.GetProperty("error"),"code safeMessage retryable");
            KnuWireValidation.Require(failed.RequestId is null || failed.RequestId==requestId,"Failure identity mismatch.");
            KnuWireValidation.Require(failed.Error.Code is "InvalidInput" or "UnsupportedProtocol" or "UnsupportedCapability" or "QuotaExceeded" or "InternalError" or "WorkerCrashed" or "Timeout" or "Unavailable" or "Busy","Unknown source failure code.");
        }
        return response;
    }
    public static void ValidateCapturedResult(AnalysisResultDto result,AnalysisSnapshot snapshot)
    {
        if(!AnalysisCatalog.IsThesis(snapshot.Profile))throw new JsonException("Not a thesis source capture.");
        KnuWireValidation.Validate(result);
        KnuWireValidation.Snapshot(result,snapshot);
    }
}
