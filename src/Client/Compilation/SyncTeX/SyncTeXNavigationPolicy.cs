using System.Collections.Immutable;
using Client.Compilation.Lifecycle;
using Client.Compilation.Presentation;
using Client.Editing;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Transfer;

namespace Client.Compilation.SyncTeX;

/// <summary>Read-only UI-owner policy. Borrows all three owners. Accepted map bytes
/// establish correlation, not map semantics; only the upstream query supplies matches.</summary>
public sealed class SyncTeXNavigationPolicy(WorkspaceCoordinator workspace, CompileWorkspaceSession session,
    ISyncTeXQuery queries) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public async Task<SyncTeXNavigationResult?> ForwardAsync(FileId file, int sourceUtf16Caret,
        CancellationToken cancellationToken = default)
    {
        var authority = Capture(SyncTeXDirection.Forward, cancellationToken);
        if (authority is null) return null;
        var input = authority.Snapshot.Files.FirstOrDefault(f => f.FileId == file);
        if (input is null || !TrySource(input, out var source)
            || !SourceTools.ValidRange(source, new(sourceUtf16Caret, sourceUtf16Caret))) return null;
        int line = 1;
        for (int i = 0; i < sourceUtf16Caret; i++)
            if (source[i] == '\r') { line++; if (i + 1 < source.Length && source[i + 1] == '\n') i++; }
            else if (source[i] == '\n') line++;
        var result = await QueryAsync(authority, new(authority.Output.ToSourceMapPayloadArray()!,
            authority.Output.SourceMapping!.Hash.Hex, SyncTeXDirection.Forward, input.RelativePath, line), cancellationToken);
        return result is not null && IsCurrent(result, cancellationToken) ? result : null;
    }

    /// <summary>Visible upstream bp, top-left coordinates. Renderer crop, rotation,
    /// DOM conversion and page-count validation are the caller's responsibility.</summary>
    public Task<SyncTeXNavigationResult?> InverseAsync(int oneBasedPage, double h, double v,
        CancellationToken cancellationToken = default)
    {
        if (oneBasedPage is < 1 or > 512 || !Coordinate(h) || !Coordinate(v))
            return Task.FromResult<SyncTeXNavigationResult?>(null);
        var authority = Capture(SyncTeXDirection.Inverse, cancellationToken);
        return authority is null ? Task.FromResult<SyncTeXNavigationResult?>(null)
            : QueryAsync(authority, new(authority.Output.ToSourceMapPayloadArray()!,
                authority.Output.SourceMapping!.Hash.Hex, SyncTeXDirection.Inverse,
                Page: oneBasedPage, H: h, V: v), cancellationToken);
    }

    private SyncTeXNavigationResult? Capture(SyncTeXDirection direction, CancellationToken token)
    {
        if (_disposed || token.IsCancellationRequested || session.DisplayedOutput is not { HasSourceMapPayload: true } output
            || session.LastTicket is not { } ticket) return null;
        var result = new SyncTeXNavigationResult(this, output, ticket, direction, token);
        return IsCurrent(result, token) ? result : null;
    }

    public bool IsCurrent(SyncTeXNavigationResult result, CancellationToken cancellationToken = default)
    {
        if (_disposed || cancellationToken.IsCancellationRequested || result.Cancellation.IsCancellationRequested
            || !ReferenceEquals(result.Owner, this) || session.Disposed
            || !ReferenceEquals(session.DisplayedOutput, result.Output)
            || !ReferenceEquals(session.LastTicket, result.Ticket)
            || !ReferenceEquals(result.Ticket.Snapshot, result.Snapshot)
            || !result.Ticket.Completion.IsCompletedSuccessfully) return false;
        var completed = result.Ticket.Completion.Result;
        if (completed.Reason != CompileLifecycleReason.Succeeded || !ReferenceEquals(completed.Output, result.Output)
            || !ReferenceEquals(completed.Snapshot, result.Snapshot)) return false;
        var identity = result.Snapshot.Identity;
        var state = session.State;
        return state is { Disposed: false, IsCurrent: true, Active: null, Pending: null }
            && ReferenceEquals(state.LastSuccessful, result.Output)
            && result.Output.Status == CompileOutputStatus.Succeeded && result.Output.HasSourceMapPayload
            && result.Output.SourceMapping is not null
            && session.SelectionVersion == identity.SelectionVersion
            && workspace.IsOpen && workspace.Recovery is null
            && workspace.Project?.Id == identity.ProjectId && workspace.SessionId == identity.SessionId
            && workspace.WorkingGeneration == identity.WorkingGeneration
            && workspace.CurrentRevision?.Id == identity.BaseHeadRevisionId;
    }

    private async Task<SyncTeXNavigationResult?> QueryAsync(SyncTeXNavigationResult authority,
        SyncTeXQueryRequest request, CancellationToken token)
    {
        if (!IsCurrent(authority, token)) return null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        SyncTeXQueryOutcome outcome;
        // Foreign ports receive only the caller token. The private lifetime
        // token cancels our wait, so Dispose never runs foreign cancellation
        // callbacks or disposes a borrowed query/storage owner.
        try { outcome = await ObserveBorrowed(queries.QueryAsync(request, token)).WaitAsync(linked.Token); }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
        if (!IsCurrent(authority, token) || outcome is not { Status: SyncTeXQueryStatus.Completed }
            || outcome.Matches is null || outcome.Matches.Count is < 1 or > 32) return null;
        var matches = ImmutableArray.CreateBuilder<SyncTeXNavigationMatch>();
        foreach (var match in outcome.Matches)
        {
            if (match is null || match.Line < 1 || match.Page is < 1 or > 512 || match.Tag < 1
                || match.Column is <= 0 || match.Point is null || match.Box is null || !Geometry(match)) continue;
            var file = Bind(authority.Snapshot, match.File);
            if (file is null || !TrySource(file, out var source) || LineRange(source, match.Line) is not { } range) continue;
            matches.Add(new(match, file, range));
        }
        if (matches.Count == 0 || !IsCurrent(authority, token)) return null;
        return new(this, authority.Output, authority.Ticket, authority.Direction, authority.Cancellation,
            matches.ToImmutable(), outcome.Truncated);
    }

    private static bool Coordinate(double value) => double.IsFinite(value) && value is >= -1000000 and <= 1000000;
    private static bool Dimension(double value) => Coordinate(value) && value >= 0;
    private static bool Geometry(SyncTeXMatch match) => Coordinate(match.Point.H) && Coordinate(match.Point.V)
        && Coordinate(match.Box.H) && Coordinate(match.Box.V) && Dimension(match.Box.Width)
        && Dimension(match.Box.Height) && Dimension(match.Box.Depth);

    // WaitAsync cancellation does not settle a borrowed noncooperative operation.
    // Observe the ACTUAL task, not just the cancelled wait wrapper, without
    // retaining the policy/workspace or running its continuation on the UI owner.
    private static Task<T> ObserveBorrowed<T>(Task<T> operation)
    {
        if (operation.IsCompleted) _ = operation.Exception;
        else _ = operation.ContinueWith(static settled => { _ = settled.Exception; },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return operation;
    }

    // Exact spellings only. No filesystem operations, basename fallback, URI
    // processing, case folding, Unicode normalization or traversal resolution.
    private static CompilationInputFile? Bind(CompilationInputSnapshot snapshot, string? name)
    {
        if (name is null) return null;
        CompilationInputFile? found = null;
        foreach (var file in snapshot.Files)
            if (name == file.RelativePath || name == "./" + file.RelativePath || name == "/work/" + file.RelativePath)
            {
                if (found is not null) return null;
                found = file;
            }
        return found;
    }

    private static bool TrySource(CompilationInputFile file, out string source)
    {
        source = "";
        // Noncaptured runtime resources and generated outputs cannot bind. A
        // project-local .sty/.cls is source, not a runtime resource alias.
        return file.MediaKind == "text" && ProjectPathValidator.MediaKindForPath(file.RelativePath) == "text"
            && ProjectPathValidator.Validate(file.RelativePath) is null
            && TextContent.TryDecode(file.ToArray(), out source, out var bom) && bom == file.HasBom
            && SourceTools.ValidText(source, out _);
    }

    internal static SourceRange? LineRange(string source, int oneBasedLine)
    {
        if (oneBasedLine < 1) return null;
        int line = 1, start = 0;
        for (int i = 0; i <= source.Length; i++)
        {
            if (i < source.Length && source[i] is not ('\r' or '\n')) continue;
            if (line == oneBasedLine) return new(start, i);
            if (i == source.Length) return null;
            if (source[i] == '\r' && i + 1 < source.Length && source[i + 1] == '\n') i++;
            start = i + 1; line++;
        }
        return null;
    }

    private WorkingFile? Target(SyncTeXNavigationMatch match) => workspace.Files.FirstOrDefault(f =>
        !f.Deleted && f.Id == match.File.FileId && f.RelativePath == match.File.RelativePath
        && f.MediaKind == "text" && f.HasBom == match.File.HasBom);

    public async Task<SyncTeXNavigationSelection?> PrepareSourceAsync(SyncTeXNavigationResult result, int matchIndex,
        CancellationToken cancellationToken = default)
    {
        if (!IsCurrent(result, cancellationToken) || matchIndex < 0 || matchIndex >= result.Matches.Length) return null;
        var match = result.Matches[matchIndex];
        if (Target(match) is not { } file) return null;
        var content = file.Content; var dirty = file.DirtyContent; var isNew = file.IsNew;
        var durableHash = file.DurableHash; var pending = file.PendingTextBytes; var imported = file.ImportedBytes;
        byte[] bytes;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try { bytes = await ObserveBorrowed(workspace.GetFileBytesAsync(file, cancellationToken)).WaitAsync(linked.Token); }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
        if (!IsCurrent(result, cancellationToken) || !ReferenceEquals(Target(match), file)
            || file.Content != content || file.DirtyContent != dirty || file.IsNew != isNew
            || file.DurableHash != durableHash || !ReferenceEquals(file.PendingTextBytes, pending)
            || !ReferenceEquals(file.ImportedBytes, imported)
            || !bytes.AsSpan().SequenceEqual(match.File.ByteSpan)
            || SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes)) != match.File.Hash
            || !TextContent.TryDecode(bytes, out var source, out var bom) || bom != match.File.HasBom
            || source != file.Content || SourceTools.ToTextarea(source, match.SourceRange) is not { } dom) return null;
        var selection = new SyncTeXNavigationSelection(this, result, match, file, source, dom, cancellationToken);
        return IsCurrent(selection, cancellationToken) ? selection : null;
    }

    /// <summary>Call synchronously immediately before atomic file/full-value DOM
    /// selection, and again after its await. Never implies DOM success.</summary>
    public bool IsCurrent(SyncTeXNavigationSelection selection, CancellationToken cancellationToken = default,
        bool requireActiveFile = false)
    {
        if (!ReferenceEquals(selection.Owner, this) || selection.Cancellation.IsCancellationRequested
            || !IsCurrent(selection.Result, cancellationToken) || Target(selection.Match) is not { } file
            || !ReferenceEquals(file, selection.WorkingFile) || file.Content != selection.Source
            || file.DirtyContent != selection.DirtyContent || file.IsNew != selection.IsNew
            || file.DurableHash != selection.DurableHash || !ReferenceEquals(file.PendingTextBytes, selection.PendingBytes)
            || !ReferenceEquals(file.ImportedBytes, selection.ImportedBytes)
            || (requireActiveFile && workspace.ActiveFileId != file.Id)) return false;
        // Preserve the diagnostic policy's final mutable-import preimage guard.
        return file.PendingTextBytes is not null || !file.IsNew || file.DirtyContent || file.ImportedBytes is null
            || file.ImportedBytes.AsSpan().SequenceEqual(selection.Match.File.ByteSpan);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); _lifetime.Dispose();
    }
}

/// <summary>Unforgeable policy-issued authority, not a PDF-hash freshness claim.</summary>
public sealed class SyncTeXNavigationResult
{
    internal SyncTeXNavigationResult(SyncTeXNavigationPolicy owner, AcceptedCompileOutput output,
        CompileLifecycleTicket ticket, SyncTeXDirection direction, CancellationToken cancellation,
        ImmutableArray<SyncTeXNavigationMatch> matches = default, bool truncated = false)
    {
        Owner = owner; Output = output; Ticket = ticket; Direction = direction; Cancellation = cancellation;
        Matches = matches.IsDefault ? [] : matches; Truncated = truncated;
    }
    internal SyncTeXNavigationPolicy Owner { get; }
    internal CompileLifecycleTicket Ticket { get; }
    internal CancellationToken Cancellation { get; }
    public AcceptedCompileOutput Output { get; }
    public CompilationInputSnapshot Snapshot => Output.Snapshot;
    public SyncTeXDirection Direction { get; }
    public ImmutableArray<SyncTeXNavigationMatch> Matches { get; }
    public bool Truncated { get; }
}

public sealed class SyncTeXNavigationMatch
{
    internal SyncTeXNavigationMatch(SyncTeXMatch native, CompilationInputFile file, SourceRange sourceRange)
    { Native = native; File = file; SourceRange = sourceRange; }
    public SyncTeXMatch Native { get; }
    public CompilationInputFile File { get; }
    /// <summary>Whole actual native line in captured BOM-stripped UTF-16 source.
    /// Never an inferred token/column or a manufactured exact requested line.</summary>
    public SourceRange SourceRange { get; }
    public int Line => Native.Line;
    public int Page => Native.Page;
    public SyncTeXPoint Point => Native.Point;
    public SyncTeXBox Box => Native.Box;
}
