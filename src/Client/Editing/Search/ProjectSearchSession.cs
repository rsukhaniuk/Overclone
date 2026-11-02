using System.Collections.Immutable;
using System.Text;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Patching;

namespace Client.Editing.Search;

// Append only: existing stored/test-visible numeric values keep their meaning.
public enum ProjectSearchStatus { Ready, Complete, Truncated, EmptyQuery, InvalidInput, InputLimit, NoProject, Stale, Disposed, Searching, Cancelled, Busy, Unavailable }

/// <summary>Callers may lower, but never raise, the built-in budgets.</summary>
public sealed record ProjectSearchLimits(long FileBytes = WorkspaceTextLimits.FileBytes,
    long TotalBytes = WorkspaceTextLimits.ProjectBytes, int FileCount = WorkspaceTextLimits.FileCount,
    int ResultsPerFile = 100, int TotalResults = 500, int QueryUnits = 1024)
{
    internal bool IsValid => FileBytes > 0 && FileBytes <= WorkspaceTextLimits.FileBytes
        && TotalBytes > 0 && TotalBytes <= WorkspaceTextLimits.ProjectBytes
        && FileCount > 0 && FileCount <= WorkspaceTextLimits.FileCount
        && ResultsPerFile > 0 && ResultsPerFile <= 100 && TotalResults > 0 && TotalResults <= 500
        && QueryUnits > 0 && QueryUnits <= 1024;
}

/// <summary>Owned immutable display data. Only the session can issue a match.</summary>
public sealed class ProjectSearchMatch
{
    internal ProjectSearchMatch(WorkingFile file, string source, int start, int end, int line, int column, string snippet)
    { File = file; Source = source; HasBom = file.HasBom; FileId = file.Id; RelativePath = file.RelativePath;
        Start = start; End = end; Line = line; Column = column; Snippet = snippet; }
    internal WorkingFile File { get; }
    internal string Source { get; }
    internal bool HasBom { get; }
    public FileId FileId { get; }
    public string RelativePath { get; }
    public int Start { get; }
    public int End { get; }
    public int Line { get; }
    public int Column { get; }
    public string Snippet { get; }
}

public sealed record ProjectSearchReport(ProjectSearchStatus Status, string Notice,
    ImmutableArray<ProjectSearchMatch> Matches, int TextFiles, int BinaryFiles);

/// <summary>Revalidate immediately before navigation, especially after awaits.</summary>
public sealed class ProjectSearchNavigationRequest
{
    internal ProjectSearchNavigationRequest(ProjectSearchSession owner, ProjectSearchMatch match,
        ProjectId projectId, Guid sessionId, long generation, long epoch)
    { Owner = owner; Match = match; ProjectId = projectId; SessionId = sessionId; WorkingGeneration = generation; Epoch = epoch; }
    internal ProjectSearchSession Owner { get; }
    internal ProjectSearchMatch Match { get; }
    internal long Epoch { get; }
    public ProjectId ProjectId { get; }
    public Guid SessionId { get; }
    public long WorkingGeneration { get; }
    public FileId FileId => Match.FileId;
    public string RelativePath => Match.RelativePath;
    public int Start => Match.Start;
    public int End => Match.End;
    public int Line => Match.Line;
    public int Column => Match.Column;
    public bool IsCurrent => Owner.IsCurrent(this);
}

/// <summary>UI-owner-context search; no writer admission, storage reads, saves or
/// mutations. The synchronous compatibility path and yielding path execute the
/// same bounded matching/preflight steps. Cancellation revokes authority now,
/// but Busy is retained until a started scheduler callback actually settles.</summary>
public sealed class ProjectSearchSession : IDisposable
{
    private readonly WorkspaceCoordinator _workspace;
    private readonly ProjectSearchLimits _limits;
    private readonly Func<CancellationToken, Task> _yield;
    private bool _disposed;
    private long _epoch, _searchedEpoch;
    private CancellationTokenSource? _active;
    private ProjectSearchCapture? _capture;
    internal WorkspaceCoordinator Workspace => _workspace;
    internal ProjectSearchCapture? Capture => _capture;
    public bool Busy => _active is not null;
    public event Action? Changed;
    public ProjectSearchReport Report { get; private set; } = new(ProjectSearchStatus.Ready, "Enter literal text, then choose Search.", [], 0, 0);

    public ProjectSearchSession(WorkspaceCoordinator workspace, ProjectSearchLimits? limits = null,
        Func<CancellationToken, Task>? yieldAsync = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace)); _limits = limits ?? new();
        if (!_limits.IsValid) throw new ArgumentOutOfRangeException(nameof(limits));
        // Blazor WASM timers use a browser timer turn. Task.Yield alone is NOT
        // sufficient: continuously queued microtasks can starve DOM input/paint.
        _yield = yieldAsync ?? (ct => Task.Delay(1, ct));
        _workspace.WorkspaceChanged += Invalidate;
    }

    private ProjectSearchReport Publish(ProjectSearchStatus status, string notice,
        ImmutableArray<ProjectSearchMatch> matches = default, int text = 0, int binary = 0)
    {
        Report = new(status, notice, matches.IsDefault ? [] : matches, text, binary); Notify(); return Report;
    }
    private void Notify()
    { foreach (var observer in Changed?.GetInvocationList().Cast<Action>().ToArray() ?? []) try { observer(); } catch { /* isolate view observers */ } }
    private void Revoke() { _epoch++; _capture = null; _active?.Cancel(); }
    private void Invalidate() { Revoke(); Publish(ProjectSearchStatus.Stale, "Workspace changed. Previous results were cleared; choose Search again."); }
    public void Cancel() { if (_disposed) return; Revoke(); Publish(ProjectSearchStatus.Cancelled, "Search cancelled. Started work must settle before another search can begin."); }

    public ProjectSearchReport Search(string? query, bool caseSensitive = true)
    {
        if (_disposed) return Report;
        bool busy = Busy; Revoke();
        if (busy) return Publish(ProjectSearchStatus.Busy, "Previous search is settling. Choose Search again when it finishes.");
        long epoch = _epoch;
        var work = new ProjectSearchWork(_workspace, _limits, query, caseSensitive);
        try
        {
            foreach (var _ in work.Run()) if (_disposed || _epoch != epoch || work.Capture?.FastCurrent == false) return Report;
            return Complete(work, epoch);
        }
        catch (ProjectSearchRejected e) { return Publish(e.Status, e.Notice); }
    }

    public async Task<ProjectSearchReport> SearchAsync(string? query, bool caseSensitive = true, CancellationToken ct = default)
    {
        if (_disposed) return Report;
        bool busy = Busy; Revoke();
        if (busy) return Publish(ProjectSearchStatus.Busy, "Previous search is settling. Choose Search again when it finishes.");
        long epoch = _epoch;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); _active = cts;
        var work = new ProjectSearchWork(_workspace, _limits, query, caseSensitive);
        bool Current() => !_disposed && epoch == _epoch;
        Publish(ProjectSearchStatus.Searching, "Checking all input, then searching current text files…");
        var slice = new ProjectSearchSlice(_yield);
        try
        {
            foreach (int units in work.Run())
            {
                cts.Token.ThrowIfCancellationRequested();
                if (!Current()) return Report;
                if (work.Capture?.FastCurrent == false) return Publish(ProjectSearchStatus.Stale, "Workspace changed; Search again.");
                if (slice.Due(units))
                {
                    await slice.YieldAsync(cts.Token); // await the ACTUAL finite callback, never a WaitAsync proxy
                    cts.Token.ThrowIfCancellationRequested();
                    if (!Current()) return Report;
                    if (work.Capture?.IsCurrent() == false) return Publish(ProjectSearchStatus.Stale, "Input changed; Search again.");
                }
            }
            cts.Token.ThrowIfCancellationRequested();
            return Current() ? Complete(work, epoch) : Report;
        }
        catch (OperationCanceledException)
        { return Current() ? Publish(ProjectSearchStatus.Cancelled, "Search cancelled; no actionable matches were published.") : Report; }
        catch (ProjectSearchRejected e) { return Current() ? Publish(e.Status, e.Notice) : Report; }
        catch (Exception e) when (e is not OutOfMemoryException)
        { return Current() ? Publish(ProjectSearchStatus.Unavailable, "Search could not finish safely; no actionable matches were published.") : Report; }
        finally { if (ReferenceEquals(_active, cts)) _active = null; cts.Dispose(); Notify(); }
    }

    private ProjectSearchReport Complete(ProjectSearchWork work, long epoch)
    {
        if (work.Capture is not { } capture || !capture.IsCurrent()) return Publish(ProjectSearchStatus.Stale, "Input changed; Search again.");
        _capture = capture; _searchedEpoch = epoch;
        return Publish(work.Truncated ? ProjectSearchStatus.Truncated : ProjectSearchStatus.Complete,
            work.Truncated ? "Result limit reached. Listing is incomplete; narrow the find text and Search again."
                : work.Matches.Count == 0 ? "No literal matches in current text files." : "Search complete for current text files.",
            work.Matches.ToImmutable(), capture.TextFiles, capture.BinaryFiles);
    }

    internal bool IsCurrentReport(ProjectSearchReport report) => !_disposed && _epoch == _searchedEpoch
        && ReferenceEquals(Report, report) && report.Status is ProjectSearchStatus.Complete or ProjectSearchStatus.Truncated
        && _capture?.IsCurrent() == true;
    internal bool CurrentMatch(ProjectSearchMatch match) => IsCurrentReport(Report)
        && Report.Matches.Any(m => ReferenceEquals(m, match)) && ReferenceEquals(match.File.Content, match.Source)
        && SourceTools.ValidRange(match.Source, new(match.Start, match.End));
    public ProjectSearchNavigationRequest? TryNavigate(ProjectSearchMatch? match)
    {
        var capture = _capture;
        return match is null || capture is null || !CurrentMatch(match) ? null
            : new(this, match, capture.ProjectId, capture.SessionId, capture.Generation, _searchedEpoch);
    }
    public bool IsCurrent(ProjectSearchNavigationRequest? request) => request is not null && ReferenceEquals(request.Owner, this)
        && request.Epoch == _searchedEpoch && request.ProjectId == _capture?.ProjectId && request.SessionId == _capture?.SessionId
        && request.WorkingGeneration == _capture?.Generation && CurrentMatch(request.Match);

    internal static bool IsBoundary(string text, int i) => i == 0 || i == text.Length
        || !(char.IsHighSurrogate(text[i - 1]) && char.IsLowSurrogate(text[i])) && !(text[i - 1] == '\r' && text[i] == '\n');
    internal static bool TryMeasure(string text, out long bytes)
    {
        bytes = 0;
        for (int i = 0; i < text.Length;)
        { if (!ProjectSearchWork.MeasureUnit(text, ref i, ref bytes)) return false; }
        return true;
    }
    internal static string MakeSnippet(string source, int start)
    {
        int from = Math.Max(0, start - 32); if (!IsBoundary(source, from)) from++;
        var result = new StringBuilder(160); if (from > 0) result.Append('…'); int i = from;
        while (i < source.Length)
        {
            char c = source[i]; int units = char.IsHighSurrogate(c) ? 2 : 1;
            string piece = c switch { '\r' => "\\r", '\n' => "\\n", '\t' => "\\t", '\\' => "\\\\",
                _ when char.IsControl(c) => $"\\u{(int)c:X4}", _ => source.Substring(i, units) };
            if (result.Length + piece.Length > 159) break; result.Append(piece); i += units;
        }
        if (i < source.Length) result.Append('…'); return result.ToString();
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _workspace.WorkspaceChanged -= Invalidate; Revoke();
        Publish(ProjectSearchStatus.Disposed, "Search session disposed."); Changed = null;
    }
}
