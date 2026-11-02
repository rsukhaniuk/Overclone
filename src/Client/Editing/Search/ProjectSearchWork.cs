using System.Collections.Immutable;
using System.Diagnostics;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Patching;

namespace Client.Editing.Search;

internal sealed class ProjectSearchRejected(ProjectSearchStatus status, string notice) : Exception
{ internal ProjectSearchStatus Status { get; } = status; internal string Notice { get; } = notice; }

internal sealed class ProjectSearchSlice(Func<CancellationToken, Task> yield)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _units;
    // Reserve one maximum microstep plus query lookahead: checks occur after
    // bounded CPU steps, so do not wait until the full65536-unit allowance.
    internal bool Due(int units) { _units += units; return _units >= 65536 - 2048 || _clock.Elapsed.TotalMilliseconds >= 8; }
    internal async Task YieldAsync(CancellationToken ct) { await yield(ct); _units = 0; _clock.Restart(); }
}

internal sealed class ProjectSearchFile
{
    internal readonly WorkingFile Live, Frozen;
    internal readonly int Index;
    internal ProjectSearchFile(WorkingFile file, int index) { Live = file; Frozen = file.Clone(); Index = index; }
    internal bool Current => Live.Id == Frozen.Id && Live.RelativePath == Frozen.RelativePath && Live.MediaKind == Frozen.MediaKind
        && Live.HasBom == Frozen.HasBom && Live.Deleted == Frozen.Deleted && Live.IsNew == Frozen.IsNew
        && Live.DirtyContent == Frozen.DirtyContent && Live.DirtyBinary == Frozen.DirtyBinary && Live.TreeChanged == Frozen.TreeChanged
        && Live.DurableHash == Frozen.DurableHash && Live.DurableLength == Frozen.DurableLength
        && (Frozen.MediaKind != "text" || ReferenceEquals(Live.Content, Frozen.Content)
            && ReferenceEquals(Live.PendingTextBytes, Frozen.PendingTextBytes) && ReferenceEquals(Live.ImportedBytes, Frozen.ImportedBytes));
}

internal sealed class ProjectSearchCapture
{
    private readonly WorkspaceCoordinator _workspace;
    private readonly ProjectRevision? _revision;
    private readonly int _workingCount;
    internal ProjectId ProjectId { get; }
    internal Guid SessionId { get; }
    internal long Generation { get; }
    internal RevisionId? Head { get; }
    internal List<ProjectSearchFile> Files { get; } = [];
    internal bool Complete;
    internal int TextFiles, BinaryFiles;
    internal ProjectSearchCapture(WorkspaceCoordinator workspace)
    {
        var project = workspace.Project ?? throw new InvalidOperationException("No project is open.");
        _workspace = workspace; ProjectId = project.Id; SessionId = workspace.SessionId;
        Generation = workspace.WorkingGeneration; Head = project.HeadRevisionId;
        _revision = workspace.CurrentRevision; _workingCount = workspace.Files.Count;
    }
    internal bool FastCurrent => _workspace.Project?.Id == ProjectId && _workspace.SessionId == SessionId
        && _workspace.WorkingGeneration == Generation && _workspace.Project?.HeadRevisionId == Head
        && ReferenceEquals(_workspace.CurrentRevision, _revision) && _workspace.Files.Count == _workingCount;
    internal bool IsCurrent()
    {
        if (!FastCurrent) return false;
        foreach (var file in Files)
            if (file.Index >= _workspace.Files.Count || !ReferenceEquals(_workspace.Files[file.Index], file.Live) || !file.Current) return false;
        // Detect a formerly deleted entry becoming live without an owner event.
        return !Complete || _workspace.Files.Count(f => !f.Deleted) == Files.Count;
    }
}

/// <summary>One shared incremental algorithm for synchronous compatibility and
/// async UI scheduling. Every yield is a bounded CPU step, not a partial result.
/// BCL ordinal matching runs on <=512 candidate starts plus query.Length-1
/// lookahead. Global source position preserves non-overlap/dedup across windows.</summary>
internal sealed class ProjectSearchWork(WorkspaceCoordinator workspace, ProjectSearchLimits limits, string? query, bool caseSensitive)
{
    internal ProjectSearchCapture? Capture;
    internal readonly ImmutableArray<ProjectSearchMatch>.Builder Matches = ImmutableArray.CreateBuilder<ProjectSearchMatch>();
    internal bool Truncated;
    internal IEnumerable<int> Run()
    {
        if (string.IsNullOrEmpty(query)) throw new ProjectSearchRejected(ProjectSearchStatus.EmptyQuery, "Enter nonempty literal find text.");
        if (query.Length > limits.QueryUnits) throw new ProjectSearchRejected(ProjectSearchStatus.InputLimit, "Find text exceeds the query limit; nothing was searched.");
        if (!ProjectSearchSession.TryMeasure(query, out _)) throw new ProjectSearchRejected(ProjectSearchStatus.InvalidInput, "Find text is not well-formed UTF-16; nothing was searched.");
        if (workspace.Project is null) throw new ProjectSearchRejected(ProjectSearchStatus.NoProject, "Open a project before searching.");
        Capture = new(workspace);
        long total = 0;
        // Stream metadata and preflight EVERY text scalar before searching any
        // file, including files after the first matching/capped file.
        for (int index = 0; index < workspace.Files.Count; index++)
        {
            var live = workspace.Files[index];
            if (live.Deleted) { yield return 1024; continue; }
            if (Capture.Files.Count == limits.FileCount) throw new ProjectSearchRejected(ProjectSearchStatus.InputLimit, "Project file-count limit exceeded; nothing was searched.");
            var image = new ProjectSearchFile(live, index); var file = image.Frozen;
            Capture.Files.Add(image);
            if (file.MediaKind != "text") { Capture.BinaryFiles++; yield return 1024; continue; }
            Capture.TextFiles++;
            if (file.Content.Length > limits.FileBytes || file.RelativePath.Length > 1024)
                throw new ProjectSearchRejected(ProjectSearchStatus.InputLimit, "A text file exceeds the input limit; nothing was searched.");
            long bytes = file.HasBom ? 3 : 0; int position = 0;
            while (position < file.Content.Length)
            {
                int from = position, end = Math.Min(position + 512, file.Content.Length);
                while (position < end)
                    if (!MeasureUnit(file.Content, ref position, ref bytes))
                        throw new ProjectSearchRejected(ProjectSearchStatus.InvalidInput, "A text file is not well-formed UTF-16; nothing was searched.");
                if (bytes > limits.FileBytes) throw new ProjectSearchRejected(ProjectSearchStatus.InputLimit, "Text input byte limit exceeded; nothing was searched.");
                yield return position - from;
            }
            if (bytes > limits.FileBytes || (total += bytes) > limits.TotalBytes) throw new ProjectSearchRejected(ProjectSearchStatus.InputLimit, "Text input byte limit exceeded; nothing was searched.");
            // Charge metadata too, so thousands of empty files yield during
            // preflight rather than waiting until matching/sorting begins.
            yield return 1024;
        }
        Capture.Complete = true;
        // Incremental stable merge sort avoids a blocking OrderBy over up to
        //5000 long paths. No per-character tables or per-match source copies.
        var sorted = Capture.Files.Where(f => f.Frozen.MediaKind == "text").ToArray();
        var scratch = new ProjectSearchFile[sorted.Length];
        for (int width = 1; width < sorted.Length; width *= 2)
        {
            for (int from = 0; from < sorted.Length; from += 2 * width)
            {
                int middle = Math.Min(from + width, sorted.Length), end = Math.Min(from + 2 * width, sorted.Length), a = from, b = middle;
                for (int dest = from; dest < end; dest++)
                {
                    bool left = b == end || a < middle && StringComparer.Ordinal.Compare(sorted[a].Frozen.RelativePath, sorted[b].Frozen.RelativePath) <= 0;
                    var chosen = left ? sorted[a++] : sorted[b++]; scratch[dest] = chosen;
                    yield return Math.Max(1, chosen.Frozen.RelativePath.Length);
                }
            }
            (sorted, scratch) = (scratch, sorted);
        }
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (var image in sorted)
        {
            var file = image.Frozen; var source = file.Content;
            int position = 0, perFile = 0, line = 1, column = 1, lineCursor = 0;
            while (position <= source.Length - query.Length)
            {
                int candidateEnd = Math.Min(source.Length - query.Length + 1, position + 512);
                int windowEnd = candidateEnd + query.Length - 1;
                int local = source.AsSpan(position, windowEnd - position).IndexOf(query.AsSpan(), comparison);
                int start = local < 0 ? candidateEnd : position + local;
                // Line accounting must yield too: the first match may be at the
                // end of an8MiB file. Columns count UTF16 units, not scalars.
                while (lineCursor < start)
                {
                    int before = lineCursor, end = Math.Min(start, lineCursor + 512);
                    while (lineCursor < end)
                    {
                        char c = source[lineCursor++];
                        if (c == '\r') { if (lineCursor < source.Length && source[lineCursor] == '\n') lineCursor++; line++; column = 1; }
                        else if (c == '\n') { line++; column = 1; } else column++;
                    }
                    yield return lineCursor - before;
                }
                if (local < 0) { position = candidateEnd; yield return 512; continue; }
                int finish = start + query.Length;
                if (!ProjectSearchSession.IsBoundary(source, start) || !ProjectSearchSession.IsBoundary(source, finish))
                { position = start + 1; yield return 1; continue; }
                if (Matches.Count == limits.TotalResults || perFile == limits.ResultsPerFile)
                { Truncated = true; break; }
                Matches.Add(new(image.Live, source, start, finish, line, column, ProjectSearchSession.MakeSnippet(source, start)));
                perFile++; position = finish; yield return query.Length;
            }
            if (Truncated && Matches.Count == limits.TotalResults) break;
            yield return 1;
        }
    }

    internal static bool MeasureUnit(string text, ref int at, ref long bytes)
    {
        char c = text[at++];
        if (char.IsHighSurrogate(c)) { if (at == text.Length || !char.IsLowSurrogate(text[at++])) return false; bytes += 4; }
        else if (char.IsLowSurrogate(c)) return false;
        else bytes += c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
        return true;
    }
}
