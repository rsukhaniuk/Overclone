using System.Collections.Immutable;
using Client.Analysis;
using Client.Workspace.Domain;

namespace Client.Editing.Assistance;

public enum AuthoringAssistContext { Command, Environment, Reference, Citation, InputPath, Definition, Outline }
public enum AuthoringAssistStatus { Completed, Failed, Cancelled, TimedOut, Busy, Disposed, Unavailable, InvalidInput, InvalidResponse }
public sealed record AuthoringAssistRequest(AnalysisSnapshot Snapshot, string RequestId, string RootFileId,
    string FileId, long CaretByte, AuthoringAssistContext Context, string Query, string CatalogVersion = "1.0");
public sealed record AuthoringAssistReply(AuthoringAssistStatus Status, string RequestId, long WorkerEpoch = 0, string? ResponseJson = null);
public interface IAuthoringAssistProvider : IAsyncDisposable
{
    // ACTUAL preparation + transport task: never a cancelled proxy wait.
    Task<AuthoringAssistReply> EvaluateAsync(AuthoringAssistRequest request, CancellationToken cancellationToken = default);
}
public sealed record AuthoringAssistSpan(long StartByte, long EndByte);
public sealed record AuthoringAssistDefinition(string FileId, AuthoringAssistSpan Span);
public sealed record AuthoringAssistCandidate(string Id, string Kind, string Label, string Detail, string InsertText,
    AuthoringAssistSpan ReplaceSpan, int CaretOffsetUtf16, bool RequiresReview, string Origin, AuthoringAssistDefinition? Definition);
public sealed record AuthoringAssistOutline(string Kind, string Label, string FileId, AuthoringAssistSpan Span, int? ParentIndex);
public sealed record AuthoringAssistPackage(string Name, string Options, string Status);
public sealed record AuthoringAssistResult(string SnapshotId, string RootFileId, string FileId, long CaretByte,
    string CatalogVersion, bool Complete, ImmutableArray<string> Limitations, ImmutableArray<AuthoringAssistCandidate> Candidates,
    ImmutableArray<AuthoringAssistOutline> Outline, ImmutableArray<AuthoringAssistPackage> LoadedPackages);
public sealed record AuthoringAssistParsed(AuthoringAssistResult? Result, string? ErrorCode);

/// <summary>Wire correlation/byte validation only, never workspace mutation authority.
/// The explicit byte map also lets managed tests consume the exact opaque-ID Rust vectors.</summary>
public sealed record AuthoringAssistExpectation(string RequestId, string SnapshotId, string RootFileId, string FileId,
    long CaretByte, AuthoringAssistContext Context, string Query, string CatalogVersion,
    IReadOnlyDictionary<string, ImmutableArray<byte>> Files,
    IReadOnlyDictionary<string, string>? Paths = null);

public sealed record AuthoringAssistEditorRead(FileId FileId, string ExpectedTextareaText, long Intent);
public sealed record AuthoringAssistDom(FileId FileId, string Value, SourceRange Selection, long Intent);
public interface IAuthoringAssistEditor
{
    long Intent { get; }
    bool IsComposing { get; }
    // Called synchronously BEFORE capturing any authority. The Workspace host
    // cancels competing selection intents here, not after an old caret was captured.
    bool BeginRequest();
    // Root checks exact file/value atomically through existing editor-select glue.
    // Return the actual import/read/render task, not WaitAsync(ct).
    Task<AuthoringAssistDom?> ReadAsync(AuthoringAssistEditorRead request);
    bool TryApply(AuthoringAssistInsertion insertion);
    Task<bool> NavigateAsync(AuthoringAssistNavigation navigation);
}
public sealed class AuthoringAssistInsertion
{
    private readonly Func<bool> _current;
    internal AuthoringAssistInsertion(SourceToolSnapshot before, SourceEdit edit, Func<bool> current)
    { Before = before; Edit = edit; _current = current; }
    public SourceToolSnapshot Before { get; }
    public SourceEdit Edit { get; }
    public bool IsCurrent() => _current();
}
public sealed class AuthoringAssistNavigation
{
    private readonly Func<bool> _current;
    internal AuthoringAssistNavigation(CapturedFile file, AuthoringAssistSpan span, SpanMappingResult mapping, Func<bool> current)
    { File = file; Span = span; Mapping = mapping; _current = current; }
    public CapturedFile File { get; }
    public AuthoringAssistSpan Span { get; }
    public SpanMappingResult Mapping { get; }
    public bool IsCurrent() => _current();
}
public sealed class AuthoringAssistReport
{
    internal AuthoringAssistReport(AuthoringAssistRequest request, AuthoringAssistResult result, long workerEpoch)
    { Request = request; Result = result; WorkerEpoch = workerEpoch; }
    public AuthoringAssistRequest Request { get; }
    public AuthoringAssistResult Result { get; }
    public long WorkerEpoch { get; }
}
