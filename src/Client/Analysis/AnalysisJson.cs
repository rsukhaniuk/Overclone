using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Client.Workspace.Domain;

namespace Client.Analysis;

// One JSON configuration + pure request-packing / response-parsing helpers for
// the canonical v1 analyzeSource envelope. NO JS runtime calls in this
// foundation task. The serializer mirrors the ACCEPTED Rust serde schema in
// rust/src/analysis/dto.rs (camelCase field names; enum values as
// lowerCamelCase STRINGS via a STRICT string-enum converter that REJECTS numeric
// enum tokens; non-nullable reference/value constructor parameters are REQUIRED
// so a missing mandatory field throws rather than silently defaulting to
// Pass/completed — matching Rust's serde defaults while preserving genuinely
// optional omitted fields). WorkingGeneration is a .NET long serialized as a
// JSON integer literal so the exact text is preserved even above JS's 2^53.
//
// ParseResponse is a protocol boundary, not merely JSON deserialization: it
// bounds the response JSON to the hard limit, requires protocolVersion == 1 and
// a terminal status with its status-appropriate body, validates that required
// nested objects/lists/identity/outcome fields are structurally present, and
// rejects a completed payload {} with a typed parse failure rather than
// substituting an empty Completed result.

public static class AnalysisJson
{
    /// <summary>Hard upper bound on a response JSON document (matches the Rust
    /// result cap). Checked before deserialization to bound the JSON graph.</summary>
    public const int MaxResponseBytes = (int)AnalysisLimits.DMaxResultBytes;

    public static readonly JsonSerializerOptions Options = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        // Enum values as lowerCamelCase STRINGS, and REJECT numeric enum tokens
        // (a bare number is not a valid enum spelling).
        o.Converters.Add(new StrictStringEnumConverterFactory());
        return o;
    }

    /// <summary>Serialize the canonical v1 analyzeSource request envelope to
    /// the metadataJson string. The packed byte buffer is built separately by
    /// <see cref="AnalysisRequestPacker.Pack"/>.</summary>
    public static string SerializeRequest(AnalysisRequestEnvelope envelope)
        => JsonSerializer.Serialize(envelope, Options);

    /// <summary>Deserialize and validate a canonical v1 response envelope from
    /// JSON text. Returns a typed Completed/Failed result; rejects malformed,
    /// unknown-version, numeric-enum, missing-required-field, and
    /// status/body-mismatch envelopes with <see cref="JsonException"/> (no
    /// empty-success substitution). A failed envelope may legitimately have no
    /// trusted requestId; a completed envelope must have a bounded trusted
    /// requestId.</summary>
    public static AnalysisResponse ParseResponse(string json)
    {
        if (json is null) throw new JsonException("analysis response is null");
        // Bound the response JSON before deserializing (no unbounded graph).
        if (json.Length > MaxResponseBytes
            || Encoding.UTF8.GetByteCount(json) > MaxResponseBytes)
            throw new JsonException("analysis response exceeds the hard response limit");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("analysis response is not a JSON object");

        // protocolVersion == 1 (an integral number; fractional/huge values are malformed, not 1).
        if (!root.TryGetProperty("protocolVersion", out var pv)
            || !pv.TryGetInt32(out var pvNum) || pvNum != 1)
            throw new JsonException("unsupported or missing protocolVersion");
        if (!root.TryGetProperty("status", out var stEl) || stEl.ValueKind != JsonValueKind.String)
            throw new JsonException("missing or non-string status");
        var status = stEl.GetString();

        return status switch
        {
            "completed" => ValidateCompleted(root),
            "failed" => ValidateFailed(root),
            // Progress/cancelled are non-terminal JS-bridge states (M2a-2b);
            // they are NOT analysis results and are never converted to Completed.
            "progress" => throw new JsonException("progress responses are not analysis results"),
            "cancelled" => throw new JsonException("cancelled responses are not analysis results"),
            _ => throw new JsonException($"unknown response status '{status}'"),
        };
    }

    private static AnalysisResponse.Completed ValidateCompleted(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object)
            throw new JsonException("completed envelope is missing its payload");
        // A completed envelope must carry a bounded trusted requestId.
        if (!root.TryGetProperty("requestId", out var rid) || rid.ValueKind != JsonValueKind.String
            || !AnalysisIdentity.IsValidOpaqueId(rid.GetString(), AnalysisLimits.MaxIdBytes))
            throw new JsonException("completed envelope is missing a valid requestId");

        RequireOpaqueIdentity(p, "snapshotId");
        RequireObject(p, "profile", prof => { RequireOpaqueIdentity(prof, "id"); RequireOpaqueIdentity(prof, "version"); });
        RequireObject(p, "facts", facts => RequireArray(facts, "files", ValidateFileFactsEntry));
        RequireArray(p, "diagnostics", ValidateDiagnosticEntry);
        RequireArray(p, "ruleResults", ValidateRuleResultEntry);
        RequireString(p, "aggregateOutcome", IsKnownOutcome);
        RequireObject(p, "coverage", ValidateCoverage);
        RequireArray(p, "unavailableCapabilities", e => { if (e.ValueKind != JsonValueKind.String) throw new JsonException("unavailableCapabilities must be strings"); });

        // A completed envelope must NOT carry a non-null error (opposite body).
        if (root.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
            throw new JsonException("a completed envelope must not carry an error");

        // Deserialize the now-validated JSON to typed DTOs for invariant checks.
        var env = root.Deserialize<AnalysisResponseEnvelope>(Options)!;
        var payload = env.Payload!;
        var knownFileIds = new HashSet<string>();
        var fileByteLength = new Dictionary<string, long>();
        foreach (var f in payload.Facts.Files)
        {
            if (!knownFileIds.Add(f.FileId))
                throw new JsonException($"duplicate facts fileId {f.FileId}");
            if (f.ByteLength < 0)
                throw new JsonException($"negative byteLength for {f.FileId}");
            fileByteLength[f.FileId] = f.ByteLength;
            // tokenCount must equal the emitted tokens count (Rust: token_count = tokens.len()).
            if (f.TokenCount != f.Tokens.Count)
                throw new JsonException($"tokenCount {f.TokenCount} != tokens length {f.Tokens.Count} for {f.FileId}");
            foreach (var t in f.Tokens)
            {
                if (t.StartByte < 0 || t.EndByte < 0 || t.StartByte > t.EndByte)
                    throw new JsonException($"invalid token span for {f.FileId}");
                if (t.EndByte > f.ByteLength)
                    throw new JsonException($"token span exceeds byteLength for {f.FileId}");
            }
        }
        var diagById = new Dictionary<string, AnalysisEngineDiagnosticDto>();
        var diagIds = new HashSet<string>();
        foreach (var d in payload.Diagnostics)
        {
            if (!diagIds.Add(d.Id)) throw new JsonException($"duplicate diagnostic id {d.Id}");
            if (!knownFileIds.Contains(d.FileId))
                throw new JsonException($"diagnostic {d.Id} references unknown file {d.FileId}");
            // A diagnostic span is bounded by its ASSOCIATED facts file's byteLength.
            if (d.Span is { } s)
            {
                if (s.StartByte < 0 || s.EndByte < 0 || s.StartByte > s.EndByte
                    || s.EndByte > fileByteLength[d.FileId])
                    throw new JsonException($"diagnostic {d.Id} span is invalid or exceeds {d.FileId} byteLength");
            }
            if (d.PrimaryLocation is { } loc && (loc.Line < 1 || loc.Column < 0))
                throw new JsonException($"diagnostic {d.Id} has an invalid primaryLocation");
            diagById[d.Id] = d;
        }
        foreach (var rr in payload.RuleResults)
        {
            var seenPerFile = new HashSet<string>();
            foreach (var pf in rr.PerFile)
            {
                if (!seenPerFile.Add(pf.FileId))
                    throw new JsonException($"ruleResult {rr.RuleId} has a duplicate perFile {pf.FileId}");
                if (!knownFileIds.Contains(pf.FileId))
                    throw new JsonException($"ruleResult {rr.RuleId} references unknown file {pf.FileId}");
                foreach (var did in pf.DiagnosticIds)
                {
                    // The referenced diagnostic must belong to this file AND this rule,
                    // not merely exist globally.
                    if (!diagById.TryGetValue(did, out var dd)
                        || dd.FileId != pf.FileId || dd.RuleId != rr.RuleId)
                        throw new JsonException($"ruleResult {rr.RuleId} perFile {pf.FileId} references a diagnostic {did} that does not belong to this file/rule");
                }
            }
        }

        return new AnalysisResponse.Completed(env.RequestId!, payload);
    }

    private static AnalysisResponse.Failed ValidateFailed(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var e) || e.ValueKind != JsonValueKind.Object)
            throw new JsonException("failed envelope is missing its error");
        RequireString(e, "code");
        RequireString(e, "safeMessage");
        if (!e.TryGetProperty("retryable", out var rt) || rt.ValueKind != JsonValueKind.False && rt.ValueKind != JsonValueKind.True)
            throw new JsonException("failed error is missing retryable");
        // A failed envelope must NOT carry a non-null payload (opposite body).
        if (root.TryGetProperty("payload", out var pl) && pl.ValueKind != JsonValueKind.Null)
            throw new JsonException("a failed envelope must not carry a payload");
        // A failed envelope may legitimately have no trusted requestId (Rust could
        // not recover one). It is a legitimate Failed, NEVER a Completed.
        if (root.TryGetProperty("requestId", out var rid) && rid.ValueKind == JsonValueKind.String
            && !AnalysisIdentity.IsValidOpaqueId(rid.GetString(), AnalysisLimits.MaxIdBytes))
            throw new JsonException("failed envelope has an invalid requestId");
        var env = root.Deserialize<AnalysisResponseEnvelope>(Options)!;
        return new AnalysisResponse.Failed(env.RequestId, env.Error!);
    }

    // ---- structural presence validators (reject numeric enums; preserve optional fields) ----

    private static void ValidateFileFactsEntry(JsonElement f)
    {
        if (f.ValueKind != JsonValueKind.Object) throw new JsonException("a facts.files entry must be an object");
        RequireOpaqueIdentity(f, "fileId");
        RequireNonNegativeNumber(f, "byteLength");
        RequireBool(f, "hasBom");
        RequireString(f, "newlineKind", IsKnownNewline);
        RequireBool(f, "truncated");
        RequireBool(f, "braceDepthExceeded");
        RequireNonNegativeNumber(f, "tokenCount");
        RequireString(f, "engineOutcome", IsKnownOutcome);
        RequireArray(f, "tokens", ValidateTokenEntry);
    }

    private static void ValidateTokenEntry(JsonElement t)
    {
        if (t.ValueKind != JsonValueKind.Object) throw new JsonException("a token entry must be an object");
        RequireString(t, "kind", IsKnownTokenKind);
        RequireNonNegativeNumber(t, "startByte");
        RequireNonNegativeNumber(t, "endByte");
        // text/note are genuinely optional in Rust (omitted when null).
    }

    private static void ValidateDiagnosticEntry(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object) throw new JsonException("a diagnostic entry must be an object");
        RequireNonEmptyString(d, "id");
        RequireNonEmptyString(d, "ruleId");
        RequireString(d, "origin");
        RequireString(d, "severity");
        RequireOpaqueIdentity(d, "fileId");
        RequireString(d, "message");
        // span/primaryLocation are optional: absent or null is allowed; when present
        // and non-null they must be a complete object (no other shape).
        if (d.TryGetProperty("span", out var sp) && sp.ValueKind != JsonValueKind.Null)
        {
            if (sp.ValueKind != JsonValueKind.Object) throw new JsonException("diagnostic span must be an object");
            RequireNonNegativeNumber(sp, "startByte");
            RequireNonNegativeNumber(sp, "endByte");
        }
        if (d.TryGetProperty("primaryLocation", out var loc) && loc.ValueKind != JsonValueKind.Null)
        {
            if (loc.ValueKind != JsonValueKind.Object) throw new JsonException("diagnostic primaryLocation must be an object");
            RequireNonNegativeNumber(loc, "line");
            RequireNonNegativeNumber(loc, "column");
        }
    }

    private static void ValidateRuleResultEntry(JsonElement rr)
    {
        if (rr.ValueKind != JsonValueKind.Object) throw new JsonException("a ruleResults entry must be an object");
        RequireNonEmptyString(rr, "ruleId");
        RequireString(rr, "origin");
        RequireString(rr, "outcome", IsKnownOutcome);
        RequireArray(rr, "perFile", pf =>
        {
            if (pf.ValueKind != JsonValueKind.Object) throw new JsonException("a perFile entry must be an object");
            RequireOpaqueIdentity(pf, "fileId");
            RequireString(pf, "outcome", IsKnownOutcome);
            RequireArray(pf, "diagnosticIds", e => { if (e.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(e.GetString())) throw new JsonException("diagnosticIds must be nonempty strings"); });
        });
        RequireArray(rr, "limitations", e => { if (e.ValueKind != JsonValueKind.String) throw new JsonException("limitations must be strings"); });
    }

    private static void ValidateCoverage(JsonElement c)
    {
        RequireArray(c, "factKinds", e => { if (e.ValueKind != JsonValueKind.String) throw new JsonException("factKinds must be strings"); });
        RequireArray(c, "unsupported", e => { if (e.ValueKind != JsonValueKind.String) throw new JsonException("unsupported must be strings"); });
        RequireArray(c, "truncatedFiles", e => { if (e.ValueKind != JsonValueKind.String) throw new JsonException("truncatedFiles must be strings"); });
        RequireBool(c, "diagnosticsTruncated");
        RequireBool(c, "resultTruncated");
    }

    private static void RequireString(JsonElement o, string name, Func<string, bool>? validate = null)
    {
        if (!o.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            throw new JsonException($"missing or non-string '{name}'");
        // GetString throws InvalidOperationException for an unpaired UTF-16
        // surrogate; the parser is a protocol boundary and surfaces that as a
        // JsonException rather than leaking an unrelated runtime exception.
        string s;
        try { s = el.GetString() ?? throw new JsonException($"missing '{name}'"); }
        catch (InvalidOperationException) { throw new JsonException($"'{name}' contains an unpaired UTF-16 surrogate"); }
        if (validate is { } v && !v(s))
            throw new JsonException($"unknown value for '{name}'");
    }

    /// <summary>Require a string property to be a valid OPAQUE identity:
    /// nonempty, strict-valid UTF-8 (no lone surrogate) and at most
    /// <see cref="AnalysisLimits.MaxIdBytes"/> bytes. Used for the trusted
    /// opaque IDs of the v1 schema (snapshotId, profile id/version, fileId,
    /// diagnostics.fileId, perFile.fileId, requestId). DERIVED ids (a
    /// diagnostic's id is file-id + rule-id + numeric offset) have a different,
    /// larger bound and must NOT be constrained by MaxIdBytes; reject those
    /// only when empty.</summary>
    private static void RequireOpaqueIdentity(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            throw new JsonException($"missing or non-string '{name}'");
        // GetString throws InvalidOperationException for an unpaired UTF-16
        // surrogate (an opaque id must be strict-valid UTF-8); surface as JsonException.
        string s;
        try { s = el.GetString()!; }
        catch (InvalidOperationException) { throw new JsonException($"'{name}' contains an unpaired UTF-16 surrogate"); }
        if (!AnalysisIdentity.IsValidOpaqueId(s, AnalysisLimits.MaxIdBytes))
            throw new JsonException($"'{name}' is not a valid opaque identity (nonempty strict-UTF-8 <= {AnalysisLimits.MaxIdBytes} bytes)");
    }

    /// <summary>Require a string property to be present and nonempty, with an
    /// optional known-value check. Used for DERIVED/semantic ids (a diagnostic's
    /// id, ruleId, perFile diagnosticIds) which have their own (larger) bound
    /// and must not be constrained by the opaque-id byte limit.</summary>
    private static void RequireNonEmptyString(JsonElement o, string name, Func<string, bool>? validate = null)
    {
        RequireString(o, name, validate);
        if (o.GetProperty(name).GetString()!.Length == 0)
            throw new JsonException($"empty '{name}'");
    }

    private static void RequireNumber(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
            throw new JsonException($"missing or non-number '{name}'");
    }

    private static void RequireNonNegativeNumber(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number
            || el.TryGetInt64(out var v) == false || v < 0)
            throw new JsonException($"missing or negative '{name}'");
    }

    private static void RequireBool(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var el) || (el.ValueKind != JsonValueKind.True && el.ValueKind != JsonValueKind.False))
            throw new JsonException($"missing or non-bool '{name}'");
    }

    private static void RequireObject(JsonElement o, string name, Action<JsonElement> validate)
    {
        if (!o.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new JsonException($"missing or non-object '{name}'");
        validate(el);
    }

    private static void RequireArray(JsonElement o, string name, Action<JsonElement> validate)
    {
        if (!o.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
            throw new JsonException($"missing or non-array '{name}'");
        foreach (var item in el.EnumerateArray()) validate(item);
    }

    private static bool IsKnownOutcome(string s)
        => s is "pass" or "fail" or "indeterminate" or "notApplicable" or "unavailable";
    private static bool IsKnownNewline(string s) => s is "lf" or "crlf" or "mixed";
    private static bool IsKnownTokenKind(string s)
        => s is "command" or "braceOpen" or "braceClose" or "group" or "optionalArg"
            or "text" or "comment" or "escape" or "includePath" or "label"
            or "reference" or "verbatim" or "unknown";
}

/// <summary>Typed analysis response: a Completed payload or a Failed error.
/// A completed envelope without a payload, or a failed envelope without an
/// error, is rejected (no empty-success substitution).</summary>
public abstract record AnalysisResponse
{
    private AnalysisResponse() { }

    public sealed record Completed(string RequestId, AnalysisResultDto Result) : AnalysisResponse;
    public sealed record Failed(string? RequestId, AnalysisErrorDto Error) : AnalysisResponse;
}

/// <summary>Pure request-packing helper: from a successful immutable
/// <see cref="AnalysisSnapshot"/>, build the accepted Rust metadataJson string
/// and ONE owned packed byte[] containing the eligible text files' exact bytes
/// (concatenated in files[] order). Excluded binary files are NOT sent.
/// The WHOLE immutable snapshot is preflighted BEFORE any buffer is copied:
/// limits within hard nonnegative bounds, file count <= maxFiles, no duplicate/
/// empty IDs, strict id/path/profile/requestId byte bounds matching Rust,
/// actual f.Bytes.Length == f.ByteLength (nonnegative), individual and total
/// ACTUAL byte caps with checked arithmetic, and initialized immutable
/// arrays. Only then is ONE exactly sized owned byte[] allocated and filled by
/// offsets. Metadata is checked under Rust's 4 MiB JSON cap. Exact hashes come
/// from the trusted immutable captured file (no extra crypto, no full-packet
/// rehash). A forged ByteLength cannot bypass the limits. Invalid inputs throw
/// BEFORE building a packet (never defer an invalid packet to WASM). Failed
/// packing leaves the snapshot's immutable arrays untouched.</summary>
public static class AnalysisRequestPacker
{
    /// <summary>Build the (metadataJson, packedBytes) pair for an
    /// analyzeSource request. Generates a fresh requestId.</summary>
    public static (string MetadataJson, byte[] PackedBytes) Pack(AnalysisSnapshot snapshot)
        => Pack(snapshot, requestId: Guid.NewGuid().ToString("N"));

    /// <summary>Build the (metadataJson, packedBytes) pair with an explicit
    /// requestId (for deterministic tests).</summary>
    public static (string MetadataJson, byte[] PackedBytes) Pack(AnalysisSnapshot snapshot, string requestId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Files.IsDefault)
            throw new InvalidOperationException("snapshot files are not initialized");

        // Validate opaque identity first (BEFORE touching buffers).
        if (!AnalysisIdentity.IsValidOpaqueId(requestId, AnalysisLimits.MaxIdBytes))
            throw new ArgumentException("requestId must be nonempty strict-UTF-8 <=128 bytes", nameof(requestId));
        ValidateProfile(snapshot.Profile);
        ValidateLimits(snapshot.Limits);

        var limits = snapshot.Limits;
        var files = snapshot.Files;
        if (files.Length > limits.MaxFiles)
            throw new InvalidOperationException("file count exceeds the project file limit");

        // ---- preflight ALL descriptors before copying ANY buffer ----
        long totalActual = 0;
        var seenIds = new HashSet<string>(files.Length);
        for (int i = 0; i < files.Length; i++)
        {
            var f = files[i];
            if (f.Bytes.IsDefault)
                throw new InvalidOperationException($"file {f.FileId} bytes are not initialized");
            long actual = f.Bytes.Length;
            if (actual != f.ByteLength)
                throw new InvalidOperationException(
                    $"file {f.FileId} declared byte length {f.ByteLength} != actual {actual}");
            if (actual < 0)
                throw new InvalidOperationException($"file {f.FileId} has negative byte length");
            if (actual > limits.MaxBytesPerFile)
                throw new InvalidOperationException($"file {f.FileId} exceeds the per-file byte limit");
            totalActual = checked(totalActual + actual);
            if (totalActual > limits.MaxTotalBytes)
                throw new InvalidOperationException("packed buffer exceeded the total byte limit");
            if (!AnalysisIdentity.IsValidOpaqueId(f.FileId.ToString(), AnalysisLimits.MaxIdBytes))
                throw new InvalidOperationException($"file {f.FileId} has an invalid fileId");
            if (!AnalysisIdentity.IsValidOpaqueId(f.RelativePath, AnalysisLimits.MaxPathBytes))
                throw new InvalidOperationException($"file {f.FileId} relativePath exceeds the path bound");
            if (!seenIds.Add(f.FileId.ToString()))
                throw new InvalidOperationException($"duplicate fileId {f.FileId}");
        }

        // ---- all valid: allocate ONE exactly sized owned buffer and fill ----
        var packed = new byte[totalActual];
        var descriptors = new AnalysisFileDescriptorDto[files.Length];
        long offset = 0;
        for (int i = 0; i < files.Length; i++)
        {
            var f = files[i];
            f.Bytes.AsSpan().CopyTo(packed.AsSpan((int)offset));
            descriptors[i] = new AnalysisFileDescriptorDto(
                FileId: f.FileId.ToString(),
                RelativePath: f.RelativePath,
                MediaKind: "text",
                HasBom: f.HasBom,
                Sha256: f.Sha256.ToString(),
                ByteOffset: offset,
                ByteLength: f.Bytes.Length);
            offset += f.Bytes.Length;
        }

        var limitsDto = new AnalysisLimitsDto(
            MaxBytesPerFile: limits.MaxBytesPerFile,
            MaxFiles: limits.MaxFiles,
            MaxTotalBytes: limits.MaxTotalBytes,
            MaxResultBytes: limits.MaxResultBytes,
            MaxTokensPerFile: limits.MaxTokensPerFile,
            MaxTokensPerProject: limits.MaxTokensPerProject,
            MaxDiagnosticsPerProject: limits.MaxDiagnosticsPerProject,
            MaxBraceDepth: limits.MaxBraceDepth);

        var payload = new AnalysisRequestPayload(
            SnapshotId: snapshot.Id.ToString(),
            ProjectId: snapshot.ProjectId.ToString(),
            SessionId: snapshot.SessionId.ToString("N"),
            WorkingGeneration: snapshot.WorkingGeneration,
            BaseHeadRevisionId: snapshot.BaseHeadRevisionId?.ToString(),
            Profile: new AnalysisProfileDto(snapshot.Profile.Id, snapshot.Profile.Version),
            Limits: limitsDto,
            Files: descriptors);

        var envelope = new AnalysisRequestEnvelope(
            ProtocolVersion: 1,
            RequestId: requestId,
            Operation: "analyzeSource",
            Payload: payload);

        string metadataJson = JsonSerializer.Serialize(envelope, AnalysisJson.Options);
        if (Encoding.UTF8.GetByteCount(metadataJson) > AnalysisLimits.DMaxResultBytes)
            throw new InvalidOperationException("metadataJson exceeds the 4 MiB JSON cap");
        return (metadataJson, packed);
    }

    private static void ValidateProfile(AnalysisProfile profile)
    {
        if (!AnalysisIdentity.IsValidOpaqueId(profile.Id, AnalysisLimits.MaxIdBytes)
            || !AnalysisIdentity.IsValidOpaqueId(profile.Version, AnalysisLimits.MaxIdBytes))
            throw new ArgumentException("profile id/version must be nonempty strict-UTF-8 <=128 bytes");
    }

    private static void ValidateLimits(AnalysisLimits limits)
    {
        if (limits.MaxBytesPerFile < 0 || limits.MaxBytesPerFile > AnalysisLimits.DMaxBytesPerFile
            || limits.MaxFiles < 0 || limits.MaxFiles > AnalysisLimits.DMaxFiles
            || limits.MaxTotalBytes < 0 || limits.MaxTotalBytes > AnalysisLimits.DMaxTotalBytes
            || limits.MaxResultBytes < 0 || limits.MaxResultBytes > AnalysisLimits.DMaxResultBytes
            || limits.MaxTokensPerFile < 0 || limits.MaxTokensPerFile > AnalysisLimits.DMaxTokensPerFile
            || limits.MaxTokensPerProject < 0 || limits.MaxTokensPerProject > AnalysisLimits.DMaxTokensPerProject
            || limits.MaxDiagnosticsPerProject < 0 || limits.MaxDiagnosticsPerProject > AnalysisLimits.DMaxDiagnosticsPerProject
            || limits.MaxBraceDepth < 0 || limits.MaxBraceDepth > AnalysisLimits.DMaxBraceDepth)
            throw new InvalidOperationException("limits are out of the hard nonnegative bounds");
    }
}

/// <summary>A strict string-enum converter factory: enum values are read/written
/// as lowerCamelCase strings and a numeric JSON token is REJECTED (a bare
/// number is not a valid enum spelling). Used for every analysis enum so a
/// numeric status/outcome can never deserialize to a default enum value.</summary>
file sealed class StrictStringEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;
    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var converterType = typeof(StrictStringEnumConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter)System.Activator.CreateInstance(converterType)!;
    }
}

file sealed class StrictStringEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            throw new JsonException($"Numeric enum value is not allowed for {typeToConvert.Name}.");
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"Enum {typeToConvert.Name} must be a string.");
        var name = reader.GetString();
        foreach (var v in Enum.GetValues<TEnum>())
            if (ToCamel(v.ToString()) == name) return v;
        throw new JsonException($"Unknown enum value '{name}' for {typeToConvert.Name}.");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        => writer.WriteStringValue(ToCamel(value.ToString()));

    private static string ToCamel(string s) => s.Length == 0 ? s
        : char.ToLowerInvariant(s[0]) + s[1..];
}
