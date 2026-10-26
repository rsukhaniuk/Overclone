using System.Collections.Immutable;
using System.Text;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Patching;

namespace Client.Editing.Search;

public enum ProjectSearchStatus { Ready, Complete, Truncated, EmptyQuery, InvalidInput, InputLimit, NoProject, Stale, Disposed }

/// <summary>Callers may lower, but never raise, the built-in budgets.</summary>
public sealed record ProjectSearchLimits(
    long FileBytes = WorkspaceTextLimits.FileBytes,
    long TotalBytes = WorkspaceTextLimits.ProjectBytes,
    int FileCount = WorkspaceTextLimits.FileCount,
    int ResultsPerFile = 100,
    int TotalResults = 500,
    int QueryUnits = 1024)
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
    {
        File = file; Source = source; HasBom = file.HasBom; FileId = file.Id; RelativePath = file.RelativePath;
        Start = start; End = end; Line = line; Column = column; Snippet = snippet;
    }
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

/// <summary>A validated request, not an editable UI input DTO. Revalidate immediately
/// before selecting a file/range, especially after any await in a navigation handler.</summary>
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

/// <summary>UI-thread-affine, bounded synchronous literal search. Borrows the coordinator;
/// performs no storage reads, saves, source mutations, or asynchronous work.</summary>
public sealed class ProjectSearchSession : IDisposable
{
    private readonly WorkspaceCoordinator _workspace;
    private readonly ProjectSearchLimits _limits;
    private bool _disposed;
    private long _epoch;
    private ProjectId? _project;
    private Guid _session;
    private long _generation;
    private long _searchedEpoch;
    public event Action? Changed;
    public ProjectSearchReport Report { get; private set; } = new(ProjectSearchStatus.Ready,
        "Enter literal text, then choose Search.", [], 0, 0);

    public ProjectSearchSession(WorkspaceCoordinator workspace, ProjectSearchLimits? limits = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _limits = limits ?? new();
        if (!_limits.IsValid) throw new ArgumentOutOfRangeException(nameof(limits));
        _workspace.WorkspaceChanged += Invalidate;
    }

    private ProjectSearchReport Publish(ProjectSearchStatus status, string notice,
        ImmutableArray<ProjectSearchMatch> matches = default, int text = 0, int binary = 0)
    {
        Report = new(status, notice, matches.IsDefault ? [] : matches, text, binary);
        Changed?.Invoke();
        return Report;
    }

    private void Invalidate()
    {
        _epoch++;
        Publish(ProjectSearchStatus.Stale, "Workspace changed. Previous results were cleared; choose Search again.");
    }

    public ProjectSearchReport Search(string? query, bool caseSensitive = true)
    {
        if (_disposed) return Report;
        if (string.IsNullOrEmpty(query)) return Publish(ProjectSearchStatus.EmptyQuery, "Enter nonempty literal find text.");
        if (query.Length > _limits.QueryUnits) return Publish(ProjectSearchStatus.InputLimit, "Find text exceeds the query limit; nothing was searched.");
        if (!TryMeasure(query, out _)) return Publish(ProjectSearchStatus.InvalidInput, "Find text is not well-formed UTF-16; nothing was searched.");
        if (_workspace.Project is not { } project) return Publish(ProjectSearchStatus.NoProject, "Open a project before searching.");
        _project = project.Id; _session = _workspace.SessionId; _generation = _workspace.WorkingGeneration; _searchedEpoch = _epoch;

        // Preflight the complete input. Never silently search a prefix of an oversized
        // or malformed file/project. Binary contents are never inspected.
        int count = 0, binary = 0;
        long total = 0;
        foreach (var file in _workspace.Files)
        {
            if (file.Deleted) continue;
            if (++count > _limits.FileCount) return Publish(ProjectSearchStatus.InputLimit, "Project file-count limit exceeded; nothing was searched.");
            if (file.MediaKind != "text") { binary++; continue; }
            if (file.Content.Length > _limits.FileBytes)
                return Publish(ProjectSearchStatus.InputLimit, "A text file exceeds the input limit; nothing was searched.");
            if (!TryMeasure(file.Content, out var bytes))
                return Publish(ProjectSearchStatus.InvalidInput, "A text file is not well-formed UTF-16; nothing was searched.");
            bytes += file.HasBom ? 3 : 0;
            if (bytes > _limits.FileBytes || (total += bytes) > _limits.TotalBytes)
                return Publish(ProjectSearchStatus.InputLimit, "Text input byte limit exceeded; nothing was searched.");
        }

        var matches = ImmutableArray.CreateBuilder<ProjectSearchMatch>();
        bool truncated = false;
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (var file in _workspace.Files.Where(f => !f.Deleted && f.MediaKind == "text").OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            var source = file.Content;
            int position = 0, perFile = 0, line = 1, column = 1, lineCursor = 0;
            while (position <= source.Length - query.Length)
            {
                int start = source.IndexOf(query, position, comparison);
                if (start < 0) break;
                int end = start + query.Length;
                if (!IsBoundary(source, start) || !IsBoundary(source, end)) { position = start + 1; continue; }
                if (matches.Count == _limits.TotalResults || perFile == _limits.ResultsPerFile) { truncated = true; break; }
                while (lineCursor < start)
                {
                    char c = source[lineCursor++];
                    if (c == '\r') { if (lineCursor < source.Length && source[lineCursor] == '\n') lineCursor++; line++; column = 1; }
                    else if (c == '\n') { line++; column = 1; }
                    else column++;
                }
                matches.Add(new(file, source, start, end, line, column, MakeSnippet(source, start)));
                perFile++; position = end;
            }
            if (truncated && matches.Count == _limits.TotalResults) break;
        }
        return Publish(truncated ? ProjectSearchStatus.Truncated : ProjectSearchStatus.Complete,
            truncated ? "Result limit reached. Listing is incomplete; narrow the find text and Search again."
                : matches.Count == 0 ? "No literal matches in current text files."
                : "Search complete for current text files.",
            matches.ToImmutable(), count - binary, binary);
    }

    public ProjectSearchNavigationRequest? TryNavigate(ProjectSearchMatch? match)
    {
        if (match is null || !CurrentMatch(match)) return null;
        return new(this, match, _project!.Value, _session, _generation, _searchedEpoch);
    }

    public bool IsCurrent(ProjectSearchNavigationRequest? request) => request is not null
        && ReferenceEquals(request.Owner, this) && request.Epoch == _searchedEpoch
        && request.ProjectId == _project && request.SessionId == _session
        && request.WorkingGeneration == _generation && CurrentMatch(request.Match);

    private bool CurrentMatch(ProjectSearchMatch match)
    {
        if (_disposed || _epoch != _searchedEpoch || _workspace.Project?.Id != _project
            || _workspace.SessionId != _session || _workspace.WorkingGeneration != _generation
            || !Report.Matches.Any(m => ReferenceEquals(m, match))) return false;
        var file = _workspace.Files.FirstOrDefault(f => f.Id == match.FileId && !f.Deleted);
        return file is not null && ReferenceEquals(file, match.File) && file.MediaKind == "text"
            && file.RelativePath == match.RelativePath && file.HasBom == match.HasBom
            && string.Equals(file.Content, match.Source, StringComparison.Ordinal)
            && match.Start >= 0 && match.End > match.Start && match.End <= file.Content.Length
            && IsBoundary(file.Content, match.Start) && IsBoundary(file.Content, match.End);
    }

    private static bool IsBoundary(string text, int index) => index == 0 || index == text.Length
        || !(char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index]))
            && !(text[index - 1] == '\r' && text[index] == '\n');

    private static bool TryMeasure(string text, out long bytes)
    {
        bytes = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (++i == text.Length || !char.IsLowSurrogate(text[i])) return false;
                bytes += 4;
            }
            else if (char.IsLowSurrogate(c)) return false;
            else bytes += c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
        }
        return true;
    }

    // At most 160 display UTF-16 units per result (80,000 total). Escape controls
    // as plain text; Razor separately performs HTML escaping. Never split a scalar.
    private static string MakeSnippet(string source, int start)
    {
        int from = Math.Max(0, start - 32);
        if (!IsBoundary(source, from)) from++;
        var result = new StringBuilder(160);
        if (from > 0) result.Append('…');
        int i = from;
        while (i < source.Length)
        {
            char c = source[i];
            int units = char.IsHighSurrogate(c) ? 2 : 1;
            string piece = c switch
            {
                '\r' => "\\r", '\n' => "\\n", '\t' => "\\t", '\\' => "\\\\",
                _ when char.IsControl(c) => $"\\u{(int)c:X4}",
                _ => source.Substring(i, units)
            };
            if (result.Length + piece.Length > 159) break;
            result.Append(piece); i += units;
        }
        if (i < source.Length) result.Append('…');
        return result.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workspace.WorkspaceChanged -= Invalidate;
        _epoch++;
        Publish(ProjectSearchStatus.Disposed, "Search session disposed.");
        Changed = null;
    }
}
