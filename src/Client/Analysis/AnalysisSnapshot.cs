using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Client.Workspace.Domain;

namespace Client.Analysis;

// Immutable in-memory analysis-snapshot domain (M2a-2a). This is C# orchestration
// metadata only — NOT a competing Rust revision id and NOT an extra wire
// requirement. The snapshot is captured without forcing a save and without
// mutating storage; a draft (unsaved) snapshot is a valid analysis input.
// Snapshot bytes are privately owned (ImmutableArray<byte>); binary assets are
// excluded from parsing and kept as metadata-only entries (never read/hashed or
// deep-copied for analysis).

/// <summary>Immutable profile reference. The first real profile is the
/// non-normative engine-structural check (id "engine-structural", version "0");
/// no KNU rules in M2a.</summary>
public readonly record struct AnalysisProfile(string Id, string Version)
{
    public static AnalysisProfile EngineStructural() => new("engine-structural", "0");
}

/// <summary>Resolved analysis limits. Defaults mirror the Rust hard defaults
/// exactly; a request/test may LOWER the input caps (bytes/files/total/result)
/// but never raise them. The enforcement caps (tokens/diagnostics/braceDepth)
/// are enforced by the Rust core during lexing and are carried at their defaults
/// here unless a test lowers them.</summary>
public sealed record AnalysisLimits(
    long MaxBytesPerFile,
    int MaxFiles,
    long MaxTotalBytes,
    long MaxResultBytes,
    int MaxTokensPerFile,
    int MaxTokensPerProject,
    int MaxDiagnosticsPerProject,
    int MaxBraceDepth)
{
    public const long DMaxBytesPerFile = Client.Workspace.Patching.WorkspaceTextLimits.FileBytes;
    public const int DMaxFiles = Client.Workspace.Patching.WorkspaceTextLimits.FileCount;
    public const long DMaxTotalBytes = Client.Workspace.Patching.WorkspaceTextLimits.ProjectBytes;
    public const long DMaxResultBytes = 4L * 1024 * 1024;
    public const int DMaxTokensPerFile = 20000;
    public const int DMaxTokensPerProject = 100000;
    public const int DMaxDiagnosticsPerProject = 1000;
    public const int DMaxBraceDepth = 256;

    /// <summary>Bounded opaque identity IDs and relative paths (match Rust).</summary>
    public const int MaxIdBytes = 128;
    public const int MaxPathBytes = 1024;

    public static AnalysisLimits Defaults() => new(
        DMaxBytesPerFile, DMaxFiles, DMaxTotalBytes, DMaxResultBytes,
        DMaxTokensPerFile, DMaxTokensPerProject, DMaxDiagnosticsPerProject, DMaxBraceDepth);

    /// <summary>Lower the four INPUT caps from a request; never raise above the
    /// hard defaults. Enforcement caps are kept at their defaults (lowered only
    /// if the request provides a smaller value).</summary>
    public static AnalysisLimits Resolve(AnalysisLimitsDto? request)
    {
        var d = Defaults();
        if (request is null) return d;
        return new AnalysisLimits(
            LowerLong(d.MaxBytesPerFile, request.MaxBytesPerFile),
            LowerInt(d.MaxFiles, request.MaxFiles),
            LowerLong(d.MaxTotalBytes, request.MaxTotalBytes),
            LowerLong(d.MaxResultBytes, request.MaxResultBytes),
            LowerInt(d.MaxTokensPerFile, request.MaxTokensPerFile),
            LowerInt(d.MaxTokensPerProject, request.MaxTokensPerProject),
            LowerInt(d.MaxDiagnosticsPerProject, request.MaxDiagnosticsPerProject),
            LowerInt(d.MaxBraceDepth, request.MaxBraceDepth));

        // Zero is allowed (a zero cap rejects everything). Negative or missing
        // values resolve to the hard default; the factory rejects negatives at
        // entry as InvalidInput before reaching here.
        static long LowerLong(long hard, long? v) => v is { } x && x >= 0 && x < hard ? x : hard;
        static int LowerInt(int hard, int? v) => v is { } x && x >= 0 && x < hard ? x : hard;
    }
}

/// <summary>An eligible text file with its exact, privately-owned source bytes.
/// The bytes are an ImmutableArray (not a mutable caller buffer); the SHA-256
/// and length are the actual materialized values (verified for unedited
/// durable content so corrupt-but-valid-UTF8 storage is not silently analyzed
/// as the editor's bytes).</summary>
public sealed record CapturedFile(
    FileId FileId,
    string RelativePath,
    bool HasBom,
    long ByteLength,
    SHA256 Sha256,
    ImmutableArray<byte> Bytes);

/// <summary>A metadata-only excluded file (binary, or otherwise not parsed).
/// The potentially large content is NEVER read, hashed or deep-copied for
/// analysis; only stable id/path/media/reason are kept so the later UI can
/// report coverage honestly.</summary>
public sealed record ExcludedFile(
    FileId FileId,
    string RelativePath,
    string MediaKind,
    string Reason);

/// <summary>The immutable analysis snapshot: opaque id, project/session/
/// generation/LOCAL epoch identity, base durable head, profile, resolved limits
/// and the eligible text files plus metadata-only excluded entries. The local
/// epoch is C# orchestration metadata that catches restore/recovery content
/// changes even when WorkingGeneration/session are unchanged.</summary>
public sealed record AnalysisSnapshot(
    AnalysisSnapshotId Id,
    ProjectId ProjectId,
    Guid SessionId,
    long WorkingGeneration,
    long LocalEpoch,
    RevisionId? BaseHeadRevisionId,
    AnalysisProfile Profile,
    AnalysisLimits Limits,
    ImmutableArray<CapturedFile> Files,
    ImmutableArray<ExcludedFile> Excluded,
    AnalysisConfigurationDto? Configuration = null,
    long SelectionVersion = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AnalysisReferenceConfigurationDto? ReferenceConfiguration = null)
{
    public AnalysisSelection Selection => new(Profile, Configuration, ReferenceConfiguration);
}

/// <summary>Typed capture result. Never an untyped exception or an empty fake
/// snapshot: an all-or-unavailable policy — if any eligible text file's bytes
/// cannot be read/validated, the whole capture is <see cref="Unavailable"/>
/// with the affected file metadata and NO successful snapshot is published.</summary>
public abstract record CaptureResult
{
    private CaptureResult() { }

    public sealed record Captured(AnalysisSnapshot Snapshot) : CaptureResult;
    public sealed record NoProject() : CaptureResult;
    public sealed record Superseded() : CaptureResult;
    public sealed record Cancelled() : CaptureResult;
    /// <summary>The factory was disposed; no capture is performed. A
    /// documented typed invalid-operation result (never a successful capture).</summary>
    public sealed record Disposed() : CaptureResult;

    public sealed record InvalidInput(string SafeMessage, IReadOnlyList<ExcludedFile> AffectedFiles) : CaptureResult;
    public sealed record QuotaExceeded(string SafeMessage, IReadOnlyList<ExcludedFile> AffectedFiles) : CaptureResult;
    public sealed record Unavailable(string SafeMessage, IReadOnlyList<ExcludedFile> AffectedFiles) : CaptureResult;
}

/// <summary>Strict bounds for opaque identity IDs and relative paths, shared by
/// capture validation and request packing (match the Rust 128-byte id / 1024-byte
/// path bounds). A lone UTF-16 surrogate is rejected (strict UTF-8 has no
/// replacement for it); the exact UTF-8 byte length is computed without encoding.</summary>
public static class AnalysisIdentity
{
    /// <summary>True iff <paramref name="s"/> is nonempty, strict-valid UTF-8
    /// (no lone surrogate) and encodes to at most <paramref name="maxBytes"/>
    /// UTF-8 bytes.</summary>
    public static bool IsValidOpaqueId(string? s, int maxBytes)
    {
        if (string.IsNullOrEmpty(s)) return false;
        int bytes = 0, i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < n && char.IsLowSurrogate(s[i + 1])) { bytes += 4; i += 2; }
                else return false; // lone high surrogate -> not strict-valid UTF-8
            }
            else if (char.IsLowSurrogate(c)) return false; // lone low surrogate
            else { bytes += c <= 0x7F ? 1 : c <= 0x7FF ? 2 : 3; i++; }
        }
        return bytes > 0 && bytes <= maxBytes;
    }
}
