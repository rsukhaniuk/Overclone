using Client.AI;
using Client.Analysis;
using Client.Editing;
using Client.Editing.Highlighting;
using Client.Workspace;
using Client.Workspace.Domain;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Client.Pages;

public partial class Workspace
{
    [Inject] private AiSessionFactory InlineAiFactory { get; set; } = default!;
    private AiSession _inlineAi = default!;
    private string? _aiSelectionNotice;
    private (string Snapshot, FileId File, HashSet<string> Ids)? _gutterFindings;
    private AiFinding? _gutterAiFinding;
    private (string Snapshot, FileId File, IReadOnlyList<SourceMark> Marks)? _checkMarks;
    private IReadOnlyList<AiFinding>? _markedAiFindings;
    private IReadOnlyList<SourceMark> _aiMarks = [];

    private bool InlineCheckScopeCurrent => _inlineCheckScope
        == (Coordinator.SessionId, Coordinator.WorkingGeneration, AnalysisCoord.CurrentSelectionVersion);

    private AnalysisReport? CurrentMarkedReport => InlineCheckScopeCurrent
        && AnalysisCurrentReport is { } report && Navigator.IsStillCurrent(report) ? report : null;

    private void InitializeInlineAi()
    {
        _inlineAi = InlineAiFactory.Create();
        _inlineAi.Changed += OnInlineAiChanged;
    }

    private void OnInlineAiChanged()
    {
        if (_pageDisposed) return;
        _ = InvokeAsync(() =>
        {
            if (_pageDisposed) return;
            if (!ReferenceEquals(_markedAiFindings, _inlineAi.Findings))
            {
                _markedAiFindings = null;
                _aiMarks = [];
                _gutterAiFinding = null;
            }
            StateHasChanged();
        }).ContinueWith(ObserveTaskFault, TaskScheduler.Default);
    }

    private IReadOnlyList<SourceMark> SourceMarks(WorkingFile file)
    {
        if (file.Content.Length > LatexHighlighter.MaxTextLength) return [];
        if (_reviewLayer == SidebarTab.Ai && _inlineAi.Context is { } context && context.IsCurrent(Coordinator)
            && context.FileId == file.Id && context.Text == file.Content)
        {
            var findings = _inlineAi.Findings;
            if (!ReferenceEquals(findings, _markedAiFindings))
            {
                _markedAiFindings = findings;
                _aiMarks = findings.Select((finding, index) => (finding.Range, Index: index))
                    .Where(item => item.Range.HasValue)
                    .Select(item => new SourceMark(item.Range!.Value, SourceMarkKind.Ai, item.Index.ToString())).ToArray();
            }
            return _aiMarks;
        }

        if (_reviewLayer != SidebarTab.Checks || CurrentMarkedReport is not { } report) return [];
        string snapshot = report.Result.SnapshotId;
        if (_checkMarks is { } cache && cache.Snapshot == snapshot && cache.File == file.Id) return cache.Marks;
        var captured = report.Snapshot.Files.FirstOrDefault(candidate => candidate.FileId == file.Id);
        if (captured is null || captured.HasBom != file.HasBom || captured.RelativePath != file.RelativePath) return [];

        string id = file.Id.ToString();
        var diagnostics = report.Result.Diagnostics.Where(d => d.FileId == id && d.Span is not null).ToArray();
        var ranges = SpanMapping.MapSourceRanges(captured, file.Content, diagnostics.Select(d => d.Span!));
        var marks = diagnostics.Where(d => ranges.ContainsKey(d.Span!))
            .Select(d => new SourceMark(ranges[d.Span!], SourceMarkKind.Check, d.Id)).ToArray();
        _checkMarks = (snapshot, file.Id, marks);
        return marks;
    }

    private void OpenGutterFinding(IReadOnlyList<SourceMark> lineMarks)
    {
        if (Coordinator.ActiveFile is not { } file || lineMarks.Count == 0) return;
        var currentMarks = SourceMarks(file);
        var mark = lineMarks[0];
        if (!currentMarks.Contains(mark)) return;
        _sidebarExpanded = true;
        if (mark.Kind == SourceMarkKind.Check && CurrentMarkedReport is { } report)
        {
            _sidebarTab = SidebarTab.Checks;
            _checksSection = ChecksSection.Source;
            _gutterFindings = (report.Result.SnapshotId, file.Id, lineMarks.Select(item => item.Id).ToHashSet());
        }
        else if (mark.Kind == SourceMarkKind.Ai && int.TryParse(mark.Id, out int index) && index >= 0 && index < _inlineAi.Findings.Count)
        {
            _sidebarTab = SidebarTab.Ai;
            _gutterAiFinding = _inlineAi.Findings[index];
        }
    }

    private IReadOnlyList<AnalysisEngineDiagnosticDto> GutterDiagnostics => _gutterFindings is { } selected
        && Coordinator.ActiveFileId == selected.File && CurrentMarkedReport is { } report && selected.Snapshot == report.Result.SnapshotId
        ? report.Result.Diagnostics.Where(diagnostic => selected.Ids.Contains(diagnostic.Id)).ToArray() : [];

    private async Task PrepareAiSelectionAsync()
    {
        _aiSelectionNotice = null;
        if (_inlineAi.Busy || JournalComposing || Coordinator.ActiveFile is not { Deleted: false, MediaKind: "text" } file
            || Coordinator.CurrentTextPatchIdentity is not { } identity) return;
        var ct = NavCancelAndStart();
        var expected = file.Content;
        bool Current() => !_pageDisposed && !ct.IsCancellationRequested && Coordinator.CurrentTextPatchIdentity == identity
            && Coordinator.ActiveFileId == file.Id && file.Content == expected && !JournalComposing;
        try
        {
            var module = await GetEditorSelectAsync(ct);
            if (module is null || !Current()) return;
            var dom = await module.InvokeAsync<SourceDomRange?>("readSelection", _editorRef, file.Id.ToString(), SourceTools.TextareaText(expected));
            if (!Current()) return;
            if (dom is { Start: int start, End: int end } && start < end
                && SourceTools.TryFromTextarea(expected, new(start, end), out var range))
                await _inlineAi.PrepareAsync(range);
            else _aiSelectionNotice = "Select a passage in the source editor first.";
        }
        catch (JSException) { if (Current()) _aiSelectionNotice = "The editor selection could not be read. Try again."; }
    }

    private async Task SelectAiFindingAsync(AiFinding finding)
    {
        if (_inlineAi.Context is not { } context || finding.Range is not { } exact || JournalComposing
            || SourceTools.ToTextarea(context.Text, exact) is not { } range) return;
        var ct = NavCancelAndStart();
        bool Current() => !_pageDisposed && !ct.IsCancellationRequested && ReferenceEquals(context, _inlineAi.Context)
            && context.IsCurrent(Coordinator) && _inlineAi.Findings.Any(item => ReferenceEquals(item, finding));
        if (!Current()) return;
        var module = await GetEditorSelectAsync(ct);
        if (module is null || !Current()) return;
        await ShowCheckedSourceAsync();
        if (!Current() || JournalComposing) return;
        try
        {
            var ok = await module.InvokeAsync<bool>("trySelectForFind", _editorRef, context.FileId.ToString(),
                SourceTools.TextareaText(context.Text), range.Start, range.End, true);
            if (Current()) _aiSelectionNotice = ok ? null : "Source changed. Prepare fresh context to review again.";
        }
        catch (JSException) { if (Current()) _aiSelectionNotice = "The editor could not open the suggestion. Try again."; }
    }
}
