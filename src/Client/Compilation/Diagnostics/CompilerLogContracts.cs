using System.Collections.Immutable;
using Client.Workspace.Domain;

namespace Client.Compilation.Diagnostics;

/// <summary>Caller policy, never an implicit product limit. Work/output are conservative
/// modeled units/retained bytes, not CPU timing, serialization size or peak RSS.</summary>
public sealed record CompilerLogBudget(int MaxFiles, long MaxSourceBytes, int MaxMetadataBytes,
    int MaxLogBytes, int MaxLineBytes, int MaxLogLines, int MaxDiagnostics, int MaxMessageBytes,
    int MaxContextDepth, int MaxSourceLines, long MaxWorkUnits, long MaxOutputBytes)
{
    public bool IsValid => MaxFiles > 0 && MaxFiles <= Array.MaxLength && MaxSourceBytes > 0
        && MaxMetadataBytes > 0 && MaxLogBytes > 0 && MaxLineBytes > 0 && MaxLogLines > 0
        && MaxDiagnostics > 0 && MaxContextDepth > 0 && MaxSourceLines > 0 && MaxMessageBytes > 0
        && MaxMetadataBytes <= Array.MaxLength && MaxLogBytes <= Array.MaxLength
        && MaxLineBytes <= Array.MaxLength && MaxDiagnostics <= Array.MaxLength
        && MaxWorkUnits > 0 && MaxOutputBytes > 0;
}

public enum CompilerLogIssue { InvalidBudget, InvalidSnapshot, FileCountLimit, SourceByteLimit, MetadataLimit, InvalidLogUtf8, InvalidLogControl }
[Flags]
public enum CompilerLogFlags
{
    None = 0, CallerIncomplete = 1, LogByteLimit = 2, LineByteLimit = 4, LineCountLimit = 8,
    ResourceLimit = 16, UnknownContent = 32, UncertainContext = 64, IncompleteDiagnostic = 128, EmptyLog = 256,
    UnsupportedBanner = 512
}
public enum CompilerDiagnosticSeverity { Error, Warning, Unknown }
public enum CompilerLocationIssue { None, MissingLine, InvalidLineNumber, UnknownFile, UncertainContext, OutOfRange, InvalidSourceUtf8, BinarySource, ResourceLimit }
public enum CompilerDiagnosticCode { MissingFile, Encoding, Metric, Unicode, Error, Fatal, Reference, Citation, Rerun, Warning, Unknown }
public enum CompilerLocationPrecision { Line }
public enum CompilerLocationBasis { ExplicitFileLine, BalancedNarrowContext }

/// <summary>Half-open exact UTF-8 bytes, BOM inclusive; excludes newline/BOM from
/// selected content. UTF-16 indices are derived, BOM-stripped source indices,
/// NOT DOM-normalized columns. No token column, edit or navigation is inferred.</summary>
public sealed class CompilerLineLocation
{
    internal CompilerLineLocation(CompilationInputFile file, int line, int start, int end, int utf16Start, int utf16End, bool direct)
    { FileId = file.FileId; RelativePath = file.RelativePath; FileHash = file.Hash; Line = line; StartByte = start; EndByte = end; SourceUtf16Start = utf16Start; SourceUtf16End = utf16End; Basis = direct ? CompilerLocationBasis.ExplicitFileLine : CompilerLocationBasis.BalancedNarrowContext; }
    public CompilerLocationPrecision Precision => CompilerLocationPrecision.Line;
    public CompilerLocationBasis Basis { get; }
    public FileId FileId { get; }
    public string RelativePath { get; }
    public SHA256 FileHash { get; }
    public int Line { get; }
    public long StartByte { get; }
    public long EndByte { get; }
    public int SourceUtf16Start { get; }
    public int SourceUtf16End { get; }
}

/// <summary>Fixed safe text only: no raw source, log message, filename allegation,
/// exception text, secret or markup is echoed. Log range identifies the initiating
/// record in the caller's log; it is not a source span.</summary>
public sealed class CompilerLogDiagnostic
{
    internal CompilerLogDiagnostic(CompilerDiagnosticCode code, CompilerDiagnosticSeverity severity,
        int logStart, int logEnd, CompilerLineLocation? location, CompilerLocationIssue issue)
    { Code = code; Severity = severity; LogStartByte = logStart; LogEndByte = logEnd; Location = location; LocationIssue = issue; }
    public string Origin => "compiler";
    public CompilerDiagnosticCode Code { get; }
    public string StableCode => CompilerLogMessages.Code(Code);
    public string SafeMessage => CompilerLogMessages.Message(Code);
    public CompilerDiagnosticSeverity Severity { get; }
    public int LogStartByte { get; }
    public int LogEndByte { get; }
    public CompilerLineLocation? Location { get; }
    public CompilerLocationIssue LocationIssue { get; }
}

public sealed class CompilerLogReport
{
    internal CompilerLogReport(CompilationInputSnapshot snapshot, CompilerLogFlags flags,
        ImmutableArray<CompilerLogDiagnostic> diagnostics, ImmutableArray<CompileDiagnosticEntry> envelopeEntries,
        int logBytes, int lines, long work, long retained)
    { Snapshot = snapshot; Flags = flags; Diagnostics = diagnostics; EnvelopeEntries = envelopeEntries; ConsumedLogBytes = logBytes; LogLines = lines; WorkUnits = work; RetainedBytes = retained; }
    // Retain actual authority; do not reconstruct a snapshot from alleged log IDs.
    public CompilationInputSnapshot Snapshot { get; }
    public CompilationSnapshotIdentity SnapshotIdentity => Snapshot.Identity;
    public SHA256 InputContentDigest => Snapshot.ContentDigest;
    public CompilerLogFlags Flags { get; }
    public ImmutableArray<CompilerLogDiagnostic> Diagnostics { get; }
    /// <summary>Explicit lossy B2a projection: one fixed code/message per distinct
    /// code (B2a forbids duplicate codes), no severity/location/identity transport.
    /// Full Diagnostics above retain occurrences. No output status is synthesized.</summary>
    public ImmutableArray<CompileDiagnosticEntry> EnvelopeEntries { get; }
    public int ConsumedLogBytes { get; }
    public int LogLines { get; }
    public long WorkUnits { get; }
    public long RetainedBytes { get; }
}

public abstract record CompilerLogParseResult
{
    private CompilerLogParseResult() { }
    public sealed record Parsed(CompilerLogReport Report) : CompilerLogParseResult;
    public sealed record Rejected(CompilerLogIssue Issue) : CompilerLogParseResult;
    public sealed record Cancelled : CompilerLogParseResult;
}

internal static class CompilerLogMessages
{
    internal static string Code(CompilerDiagnosticCode code) => code switch
    {
        CompilerDiagnosticCode.MissingFile => "TEX001",
        CompilerDiagnosticCode.Encoding => "TEX002",
        CompilerDiagnosticCode.Metric => "TEX003",
        CompilerDiagnosticCode.Unicode => "TEX004",
        CompilerDiagnosticCode.Error => "TEX005",
        CompilerDiagnosticCode.Fatal => "TEX006",
        CompilerDiagnosticCode.Reference => "TEX101",
        CompilerDiagnosticCode.Citation => "TEX102",
        CompilerDiagnosticCode.Rerun => "TEX103",
        CompilerDiagnosticCode.Warning => "TEX104",
        _ => "TEX900"
    };
    internal static string Message(CompilerDiagnosticCode code) => code switch
    {
        CompilerDiagnosticCode.MissingFile => "The compiler reported a missing input file.",
        CompilerDiagnosticCode.Encoding => "LaTeX reported a missing or unsupported encoding.",
        CompilerDiagnosticCode.Metric => "The compiler could not load a font metric.",
        CompilerDiagnosticCode.Unicode => "LaTeX inputenc could not represent a Unicode character.",
        CompilerDiagnosticCode.Error => "The compiler reported an error; review the local log.",
        CompilerDiagnosticCode.Fatal => "The compiler reported a fatal stop.",
        CompilerDiagnosticCode.Reference => "LaTeX reported an unresolved reference.",
        CompilerDiagnosticCode.Citation => "LaTeX reported an unresolved citation.",
        CompilerDiagnosticCode.Rerun => "LaTeX requested another pass; convergence is not established.",
        CompilerDiagnosticCode.Warning => "LaTeX or a package reported a warning.",
        _ => "An unrecognized compiler-log record requires local review."
    };
}
