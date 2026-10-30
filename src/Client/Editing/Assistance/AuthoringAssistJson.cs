using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Client.Analysis;
using Client.Workspace.Transfer;

namespace Client.Editing.Assistance;

/// <summary>Frozen authoringAssist v1 transport shape. No TeX/package interpretation.
/// JSON is bounded before parsing, fields are exact/unique and source spans refer
/// only to captured scalar boundaries. Mapping to editor selections is deferred.</summary>
public static class AuthoringAssistJson
{
    public const int ResponseMaxBytes = 262144, MetadataMaxBytes = 4 * 1024 * 1024, PackedMaxBytes = 64 * 1024 * 1024;
    public const string CatalogVersion = "1.0";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    // Actual hash of the deliberately empty captured slice, NOT of an asset.
    private static readonly string EmptySliceSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Array.Empty<byte>())).ToLowerInvariant();
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly HashSet<string> Limitations = new(StringComparer.Ordinal)
    {
        "sourceIntentOnly", "macroExpansionUnsupported", "runtimeResourcesUnverified", "cyclicInclude", "repeatedInclude",
        "nonTextInclude", "incompleteScan", "conditionalExecutionUnsupported", "incompleteConditional", "dynamicOrUnsafeInclude",
        "missingInclude", "dynamicPackage", "unknownPackage", "projectPackageExecutionUnsupported", "declarationDefaultUnsupported",
        "declarationEffectUncertain", "dynamicDeclaration", "redefinitionUncertain", "duplicateLabel", "duplicateBibliographyKey",
        "dynamicReference", "dynamicReferenceOrCitation", "dynamicOrUnsafeBibliography", "missingBibliography", "nonTextBibliography",
        "malformedBibliography", "dynamicBibliography", "dynamicHeading", "dynamicConstruct", "macroExecutionUnsupported",
        "dynamicEnvironment", "fileOutsideRootClosure", "incompatibleCaretContext"
    };
    public static bool Text(string? s, int max, bool empty = true)
    {
        if (s is null || s.Length > max || (!empty && s.Length == 0)) return false;
        try { return Utf8.GetByteCount(s) <= max; } catch (EncoderFallbackException) { return false; }
    }
    public static bool Epoch(long n) => n is > 0 and <= 9007199254740991;
    public static string ContextName(AuthoringAssistContext context) => context switch
    {
        AuthoringAssistContext.Command => "command", AuthoringAssistContext.Environment => "environment",
        AuthoringAssistContext.Reference => "reference", AuthoringAssistContext.Citation => "citation",
        AuthoringAssistContext.InputPath => "inputPath", AuthoringAssistContext.Definition => "definition",
        AuthoringAssistContext.Outline => "outline", _ => throw new ArgumentException("Unknown context.")
    };
    public static AuthoringAssistExpectation Expect(AuthoringAssistRequest r) => new(r.RequestId, r.Snapshot.Id.ToString(),
        r.RootFileId, r.FileId, r.CaretByte, r.Context, r.Query, r.CatalogVersion,
        r.Snapshot.Files.ToDictionary(f => f.FileId.ToString(), f => f.Bytes, StringComparer.Ordinal),
        r.Snapshot.Files.Select(f=>(Id:f.FileId.ToString(),Path:f.RelativePath)).Concat(r.Snapshot.Excluded.Select(f=>(Id:f.FileId.ToString(),Path:f.RelativePath)))
            .ToDictionary(f=>f.Id,f=>f.Path,StringComparer.Ordinal));

    public static (string MetadataJson, byte[] PackedBytes) Pack(AuthoringAssistRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Check(Text(request.RequestId,128,false) && request.CatalogVersion == CatalogVersion
            && Text(request.Query,1024) && Enum.IsDefined(request.Context), "Invalid request.");
        var snap = request.Snapshot with { Limits = request.Snapshot.Limits with { MaxResultBytes = Math.Min(ResponseMaxBytes, request.Snapshot.Limits.MaxResultBytes) } };
        Check(!snap.Files.IsDefault && !snap.Excluded.IsDefault && snap.Files.Length+snap.Excluded.Length<=snap.Limits.MaxFiles,
            "File quota/initialization.");
        Check(snap.Limits.MaxBytesPerFile>0 && snap.Limits.MaxTotalBytes>0 && snap.Limits.MaxFiles>0 && snap.Limits.MaxResultBytes>0
            && snap.Limits.MaxTokensPerFile>0 && snap.Limits.MaxTokensPerProject>0 && snap.Limits.MaxBraceDepth>0 && snap.Limits.MaxDiagnosticsPerProject>0,"Positive limits required.");
        var names=snap.Files.Select(f=>(Id:f.FileId.ToString(),Path:f.RelativePath)).Concat(snap.Excluded.Select(f=>(Id:f.FileId.ToString(),Path:f.RelativePath))).ToArray();
        Check(names.Select(f=>f.Id).Distinct(StringComparer.Ordinal).Count()==names.Length && names.Select(f=>f.Path).Distinct(StringComparer.Ordinal).Count()==names.Length
            && names.All(f=>ReferenceChecks.IsCanonicalPath(f.Path) && !f.Path.StartsWith("__oop_runtime__/",StringComparison.Ordinal)),"Invalid descriptors.");
        Check(snap.Files.Any(f=>f.FileId.ToString()==request.RootFileId && AnalysisCatalog.IsTex(f.RelativePath)),"Invalid root.");
        var expected = Expect(request);
        Check(expected.Files.ContainsKey(request.RootFileId) && expected.Files.ContainsKey(request.FileId), "Missing selected file.");
        Span(expected.FileId, new(request.CaretByte,request.CaretByte), expected.Files);
        var source = expected.Files[request.FileId];
        var queryBytes = Utf8.GetBytes(request.Query);
        Check(request.CaretByte >= queryBytes.Length && source.AsSpan((int)request.CaretByte-queryBytes.Length,queryBytes.Length).SequenceEqual(queryBytes), "Query not observed.");
        Check(request.CaretByte >= (source.Length>=3 && source[0]==239 && source[1]==187 && source[2]==191 ? 3 : 0),"Caret in BOM.");
        Check(snap.WorkingGeneration is >= 0 and <= 9007199254740991 && snap.Limits.MaxResultBytes > 0, "Invalid generation/limit.");
        var (analysisJson, packed) = AnalysisRequestPacker.Pack(snap, request.RequestId);
        using var doc = JsonDocument.Parse(analysisJson);
        var original = doc.RootElement.GetProperty("payload");
        var files = JsonSerializer.Deserialize<List<AnalysisFileDescriptorDto>>(original.GetProperty("files").GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        foreach (var excluded in snap.Excluded)
        {
            Check(excluded.MediaKind=="binary","Unknown excluded media.");
            // ExcludedFile has only metadata. Its descriptor deliberately slices
            // zero bytes at the end of the packed text buffer. No asset hash,
            // length, BOM or content/provisioning claim is inferred here.
            files.Add(new(excluded.FileId.ToString(),excluded.RelativePath,"binary",false,EmptySliceSha256,packed.LongLength,0));
        }
        Check(files.Count <= snap.Limits.MaxFiles && files.Select(f=>f.FileId).Distinct(StringComparer.Ordinal).Count()==files.Count
            && files.Select(f=>f.RelativePath).Distinct(StringComparer.Ordinal).Count()==files.Count
            && files.All(f=>ReferenceChecks.IsCanonicalPath(f.RelativePath)), "Invalid descriptors.");
        var payload = new
        {
            snapshotId = snap.Id.ToString(), projectId = snap.ProjectId.ToString(), sessionId = snap.SessionId.ToString("N"),
            workingGeneration = snap.WorkingGeneration, baseHeadRevisionId = snap.BaseHeadRevisionId?.ToString(),
            rootFileId = request.RootFileId, fileId = request.FileId, caretByte = request.CaretByte,
            context = ContextName(request.Context), query = request.Query, catalogVersion = request.CatalogVersion,
            files, limits = original.GetProperty("limits").Clone()
        };
        string metadata = JsonSerializer.Serialize(new {protocolVersion=1, requestId=request.RequestId, operation="authoringAssist",payload},Camel);
        Check(Text(metadata,MetadataMaxBytes) && packed.Length<=PackedMaxBytes, "Request quota.");
        return (metadata,packed);
    }

    public static AuthoringAssistParsed Parse(string? json, AuthoringAssistExpectation expected)
    {
        try
        {
            Check(Text(json,ResponseMaxBytes,false), "Response quota/encoding.");
            using var doc = JsonDocument.Parse(json!,new JsonDocumentOptions {MaxDepth=32});
            Unique(doc.RootElement);
            var root=doc.RootElement;
            string status=String(root,"status",16);
            Fields(root,status=="completed" ? "protocolVersion requestId status payload" : "protocolVersion requestId status error");
            Check(Long(root,"protocolVersion")==1 && String(root,"requestId",128)==expected.RequestId,"Envelope mismatch.");
            if(status=="failed")
            {
                var error=root.GetProperty("error");Fields(error,"code safeMessage retryable");
                string code=String(error,"code",64);_ = String(error,"safeMessage",512,true);
                Check(code is "InvalidInput" or "UnsupportedProtocol" or "UnsupportedCapability" or "QuotaExceeded"
                    && !Bool(error,"retryable"),"Invalid error.");
                return new(null,code);
            }
            Check(status=="completed","Unknown status.");
            var p=root.GetProperty("payload");
            Fields(p,"snapshotId rootFileId fileId caretByte catalogVersion complete limitations candidates outline loadedPackages");
            Check(String(p,"snapshotId",128)==expected.SnapshotId && String(p,"rootFileId",128)==expected.RootFileId
                && String(p,"fileId",128)==expected.FileId && Long(p,"caretByte")==expected.CaretByte
                && String(p,"catalogVersion",16)==CatalogVersion && expected.CatalogVersion==CatalogVersion,"Echo mismatch.");
            var limitations=Array(p,"limitations",64).Select(v=>Value(v,64)).ToImmutableArray();
            Check(limitations.Distinct(StringComparer.Ordinal).Count()==limitations.Length && limitations.All(Limitations.Contains)
                && new[]{"sourceIntentOnly","macroExpansionUnsupported","runtimeResourcesUnverified"}.All(limitations.Contains),"Unknown/missing limitation.");
            bool complete=Bool(p,"complete");Check(!complete || limitations.Length==3,"False complete.");
            var ids=new HashSet<string>(StringComparer.Ordinal);
            var candidates=Array(p,"candidates",100).Select(c=>
            {
                Fields(c,"id kind label detail insertText replaceSpan caretOffsetUtf16 requiresReview origin definition");
                string id=String(c,"id",128),kind=String(c,"kind",32),origin=String(c,"origin",32);
                string label=String(c,"label",512),detail=String(c,"detail",512,true),insert=String(c,"insertText",512,true);
                Check(ids.Add(id) && KindOrigin(kind,origin) && Applicable(expected.Context,kind),"Candidate kind/origin/context/ID.");
                Check(label.StartsWith(expected.Query,StringComparison.Ordinal),"Candidate query mismatch.");
                Check(kind is "command" or "macro" ? label.All(char.IsAsciiLetter) && insert.StartsWith("\\"+label,StringComparison.Ordinal)
                    : kind=="path" ? insert==label || label.EndsWith(".tex",StringComparison.Ordinal) && insert==label[..^4]
                    : insert==label,"Insertion kind mismatch.");
                var replace=ReadSpan(c.GetProperty("replaceSpan"));Span(expected.FileId,replace,expected.Files);
                Check(replace.EndByte==expected.CaretByte,"Replacement must end at caret.");
                var file=expected.Files[expected.FileId];
                string prefix=expected.Context==AuthoringAssistContext.Command ? "\\"+expected.Query : expected.Query;
                Check(replace.EndByte-replace.StartByte==Utf8.GetByteCount(prefix),"Replacement byte count.");
                string observed=Utf8.GetString(file.AsSpan((int)replace.StartByte,(int)(replace.EndByte-replace.StartByte)));
                Check(observed==prefix,"Replacement is not exact prefix.");
                long caret=Long(c,"caretOffsetUtf16");
                Check(caret>=0 && caret<=insert.Length && SourceTools.ValidRange(insert,new((int)caret,(int)caret)),"Insertion caret.");
                bool review=Bool(c,"requiresReview");
                AuthoringAssistDefinition? definition=null;var d=c.GetProperty("definition");
                if(d.ValueKind!=JsonValueKind.Null)
                {
                    Fields(d,"fileId span");string fileId=String(d,"fileId",128);var span=ReadSpan(d.GetProperty("span"));
                    Span(fileId,span,expected.Files);definition=new(fileId,span);
                    if(origin!="projectPath")Check(span.EndByte-span.StartByte==Utf8.GetByteCount(label)
                        && Utf8.GetString(expected.Files[fileId].AsSpan((int)span.StartByte,(int)(span.EndByte-span.StartByte)))==label,"Definition preimage mismatch.");
                    else Check(span.StartByte==span.EndByte && span.StartByte==(HasBom(expected.Files[fileId])?3:0),"Path definition must be first content caret.");
                }
                Check(origin is "builtin" or "packageCatalog" ? definition is null : origin=="projectPath" || definition is not null,"Definition origin mismatch.");
                if(origin=="projectPath" && expected.Paths is not null)
                    Check(expected.Paths.Values.Contains(label,StringComparer.Ordinal) && (definition is null || expected.Paths.TryGetValue(definition.FileId,out var path) && path==label),"Path target mismatch.");
                if(expected.Context==AuthoringAssistContext.Definition) Check(label==expected.Query && definition is not null,"Definition mismatch.");
                return new AuthoringAssistCandidate(id,kind,label,detail,insert,replace,(int)caret,review,origin,definition);
            }).ToImmutableArray();
            var outline=ImmutableArray.CreateBuilder<AuthoringAssistOutline>();
            foreach(var o in Array(p,"outline",5000))
            {
                Fields(o,"kind label fileId span parentIndex");string file=String(o,"fileId",128),label=String(o,"label",512);
                Check(String(o,"kind",32)=="heading","Outline kind.");var span=ReadSpan(o.GetProperty("span"));Span(file,span,expected.Files);
                Check(span.EndByte-span.StartByte==Utf8.GetByteCount(label)
                    && Utf8.GetString(expected.Files[file].AsSpan((int)span.StartByte,(int)(span.EndByte-span.StartByte)))==label,"Outline preimage mismatch.");
                int? parent=null;var par=o.GetProperty("parentIndex");
                if(par.ValueKind!=JsonValueKind.Null){long n=par.GetInt64();Check(n>=0 && n<outline.Count,"Outline parent.");parent=(int)n;}
                outline.Add(new("heading",label,file,span,parent));
            }
            var packages=Array(p,"loadedPackages",5000).Select(v=>
            {
                Fields(v,"name options status");var name=String(v,"name",128);var options=String(v,"options",512,true);var state=String(v,"status",64);
                Check(state is "catalogKnownResourcesUnverified" or "unknownResourcesUnverified" or "projectLocalUnverified","Package status.");
                return new AuthoringAssistPackage(name,options,state);
            }).ToImmutableArray();
            return new(new(expected.SnapshotId,expected.RootFileId,expected.FileId,expected.CaretByte,CatalogVersion,complete,limitations,candidates,outline.ToImmutable(),packages),null);
        }
        catch(Exception e) when(e is JsonException or ArgumentException or InvalidOperationException or OverflowException or FormatException or KeyNotFoundException)
        { return new(null,"InvalidResponse"); }
    }
    private static bool Applicable(AuthoringAssistContext context,string kind)=>context switch
    {
        AuthoringAssistContext.Command=>kind is "command" or "macro",AuthoringAssistContext.Environment=>kind=="environment",
        AuthoringAssistContext.Reference=>kind=="label",AuthoringAssistContext.Citation=>kind=="citation",AuthoringAssistContext.InputPath=>kind=="path",
        AuthoringAssistContext.Definition=>kind is "macro" or "environment" or "label" or "citation",_=>false
    };
    private static bool KindOrigin(string kind,string origin)=>origin switch
    {
        "builtin" or "packageCatalog"=>kind is "command" or "environment", "projectDeclaration"=>kind is "macro" or "environment",
        "projectLabel"=>kind=="label","projectBibliography"=>kind=="citation","projectPath"=>kind=="path",_=>false
    };
    private static void Span(string file,AuthoringAssistSpan span,IReadOnlyDictionary<string,ImmutableArray<byte>> files)
    {
        Check(files.TryGetValue(file,out var bytes) && !bytes.IsDefault && span.StartByte>=0 && span.EndByte>=span.StartByte
            && span.EndByte<=bytes.Length,"Span range/file.");
        bool Boundary(long n)=>n==bytes.Length || (bytes[(int)n]&0xc0)!=0x80;
        Check(Boundary(span.StartByte)&&Boundary(span.EndByte),"Scalar span.");
    }
    private static bool HasBom(ImmutableArray<byte> bytes)=>bytes.Length>=3 && bytes[0]==239 && bytes[1]==187 && bytes[2]==191;
    private static AuthoringAssistSpan ReadSpan(JsonElement v){Fields(v,"startByte endByte");return new(Long(v,"startByte"),Long(v,"endByte"));}
    private static void Unique(JsonElement v)
    {
        if(v.ValueKind==JsonValueKind.Object){var keys=new HashSet<string>(StringComparer.Ordinal);foreach(var p in v.EnumerateObject()){Check(keys.Add(p.Name),"Duplicate field.");Unique(p.Value);}}
        else if(v.ValueKind==JsonValueKind.Array)foreach(var i in v.EnumerateArray())Unique(i);
        else if(v.ValueKind==JsonValueKind.String)Check(Text(v.GetString(),ResponseMaxBytes),"Invalid Unicode.");
    }
    private static void Fields(JsonElement v,string fields)
    {
        Check(v.ValueKind==JsonValueKind.Object,"Object required.");var allowed=fields.Split(' ');
        Check(v.EnumerateObject().Count()==allowed.Length && v.EnumerateObject().All(p=>allowed.Contains(p.Name,StringComparer.Ordinal)),"Unexpected/missing field.");
    }
    private static IEnumerable<JsonElement> Array(JsonElement p,string name,int max){var a=p.GetProperty(name);Check(a.ValueKind==JsonValueKind.Array && a.GetArrayLength()<=max,"Array quota.");return a.EnumerateArray();}
    private static string Value(JsonElement v,int max,bool empty=false){Check(v.ValueKind==JsonValueKind.String,"String required.");string s=v.GetString()!;Check(Text(s,max,empty),"String quota.");return s;}
    private static string String(JsonElement p,string name,int max,bool empty=false)=>Value(p.GetProperty(name),max,empty);
    private static long Long(JsonElement p,string name)=>p.GetProperty(name).GetInt64();
    private static bool Bool(JsonElement p,string name){var v=p.GetProperty(name);Check(v.ValueKind is JsonValueKind.True or JsonValueKind.False,"Boolean required.");return v.GetBoolean();}
    private static void Check(bool condition,string safe){if(!condition)throw new JsonException(safe);}
}
