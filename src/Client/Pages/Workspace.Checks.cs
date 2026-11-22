using Client.Analysis;
using Client.Checks;
using Client.Compilation.Diagnostics;
using Client.Workspace.Domain;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Client.Pages;

public partial class Workspace
{
    [Inject] private CheckReportStore CheckReports { get; set; } = default!;

    private enum ChecksSection { Source, Pdf, Ai, Saved, Review }

    private static readonly ChecksSection[] CheckSections =
        [ChecksSection.Source, ChecksSection.Pdf, ChecksSection.Ai, ChecksSection.Saved, ChecksSection.Review];

    private ChecksSection _checksSection;
    private bool _checksOpen, _focusChecks, _loadingChecks, _hasOlderChecks, _refreshChecksAfterRead;
    private ElementReference _checksHeading;
    private Guid _checksSession;
    private string? _checkStorageNotice;
    private string? _sourceStorageNotice, _lastStoredSourceSnapshot;
    private List<CheckReport> _savedChecks = [];
    private readonly Dictionary<string, CheckReport> _pendingCheckWrites = [];
    private Guid? _lastSavedCompile;
    private RecordedAnalysis? _requestedCheck;

    private sealed record RecordedAnalysis(Guid Session, long Generation, long Selection, string? PreviousSnapshot);

    private IEnumerable<ChecksSection> VisibleCheckSections => CheckSections.Where(section =>
        section != ChecksSection.Review || _checksSection == section || CurrentReview.Preview is not null || CurrentReview.Action is not null);

    private int PendingCheckCount => _pendingCheckWrites.Values.Count(report => report.ProjectId == Coordinator.Project?.Id.ToString());
    private static string ChecksId(ChecksSection section) => section.ToString().ToLowerInvariant();
    private static string ChecksLabel(ChecksSection section) => section switch
    {
        ChecksSection.Source => "Source",
        ChecksSection.Pdf => "PDF",
        ChecksSection.Ai => "AI review",
        ChecksSection.Saved => "Saved reports",
        ChecksSection.Review => "Review changes",
        _ => throw new ArgumentOutOfRangeException(nameof(section)),
    };

    private void OpenChecks(ChecksSection section)
    {
        if (!Coordinator.IsOpen) return;
        _focusChecks = !_checksOpen;
        _checksOpen = true;
        _checksSection = section;
        if (section == ChecksSection.Saved) QueueCheckWork(() => ReadChecksAsync(older: false));
    }

    private void ShowEditor()
    {
        _checksOpen = false;
        _focusChecks = false;
    }

    private async Task ShowCheckedSourceAsync()
    {
        if (!_checksOpen) return;
        ShowEditor();
        _narrowPdf = false;
        await AwaitDiagnosticRender();
    }

    private async Task ShowCheckedPdfAsync()
    {
        if (!_checksOpen) return;
        ShowEditor();
        _showOutput = true;
        _narrowPdf = true;
        await AwaitDiagnosticRender();
    }

    private void InitializeChecks()
    {
        _checksSession = Coordinator.SessionId;
        Coordinator.WorkspaceChanged += OnCheckWorkspaceChanged;
        CompileSession.Changed += OnCheckCompilationChanged;
    }

    private void DisposeChecks()
    {
        Coordinator.WorkspaceChanged -= OnCheckWorkspaceChanged;
        CompileSession.Changed -= OnCheckCompilationChanged;
        _pendingCheckWrites.Clear();
    }

    private void OnCheckWorkspaceChanged() => QueueCheckWork(() =>
    {
        if (_checksSession != Coordinator.SessionId)
        {
            _checksSession = Coordinator.SessionId;
            _requestedCheck = null;
            _sourceStorageNotice = _lastStoredSourceSnapshot = null;
            _lastSavedCompile = null;
            _savedChecks.Clear();
            _loadingChecks = _hasOlderChecks = _refreshChecksAfterRead = false;
            _checkStorageNotice = PendingCheckCount > 0 ? "Some reports still need to be saved in this browser." : null;
            _checksSection = ChecksSection.Source;
            ShowEditor();
            StateHasChanged();
        }
        return Task.CompletedTask;
    });

    private void QueueCheckWork(Func<Task> work)
    {
        if (!_pageDisposed)
            _ = InvokeAsync(() => _pageDisposed ? Task.CompletedTask : work())
                .ContinueWith(ObserveTaskFault, TaskScheduler.Default);
    }

    private void RequestRecordedAnalysis()
    {
        _lastStoredSourceSnapshot = null;
        _sourceStorageNotice = "Saving the result when this check completes…";
        _requestedCheck = new(Coordinator.SessionId, Coordinator.WorkingGeneration, AnalysisCoord.CurrentSelectionVersion,
            (AnalysisCoord.State as AnalysisState.Completed)?.Report.Result.SnapshotId);
        AnalysisCoord.RequestReanalyze();
    }

    private void CaptureCompletedAnalysis(AnalysisState published)
    {
        if (_requestedCheck is not { } admission || published is AnalysisState.Pending or AnalysisState.Running) return;
        if (published is not AnalysisState.Completed { Report: var report })
        {
            if (ReferenceEquals(published, AnalysisCoord.State))
            {
                _requestedCheck = null;
                _sourceStorageNotice = null;
            }
            return;
        }
        if (report.Result.SnapshotId == admission.PreviousSnapshot) return;

        var snapshot = report.Snapshot;
        if (snapshot.SessionId != admission.Session || snapshot.WorkingGeneration != admission.Generation
            || snapshot.SelectionVersion != admission.Selection)
        {
            if (ReferenceEquals(published, AnalysisCoord.State))
            {
                _requestedCheck = null;
                _sourceStorageNotice = "Source or check settings changed. Run again to save a report for the new scope.";
            }
            return;
        }
        _requestedCheck = null;

        var result = report.Result;
        var title = result.Profile.Id switch
        {
            AnalysisCatalog.KnuId when result.Profile.Version == AnalysisCatalog.ProjectMarginFixVersion => "Project margin repair",
            AnalysisCatalog.KnuId => "FCSC KNU ruleset",
            AnalysisCatalog.ReferenceId => "Source references",
            _ => "Engine checks",
        };
        var names = snapshot.Files.ToDictionary(file => file.FileId.ToString(), file => file.RelativePath);
        var detail = new System.Text.StringBuilder();
        if (snapshot.Configuration is { } configuration)
            detail.AppendLine($"Work type: {AnalysisPresentation.WorkType(configuration.WorkType)}");
        if (snapshot.ReferenceConfiguration is { } scope)
            detail.AppendLine($"Root: {names.GetValueOrDefault(scope.RootFileId, scope.RootFileId)}");
        foreach (var rule in result.RuleResults)
        {
            detail.AppendLine($"{rule.RuleId} · {rule.Outcome}");
            if (rule.Source is { } source)
                detail.AppendLine($"  Guideline §{source.Citation.Section}, pages {string.Join(", ", source.Citation.PrintedPages)}. {source.Expected}");
        }
        if (result.Thesis is { } thesis)
            foreach (var rule in thesis.RuleResults)
                detail.AppendLine($"{rule.RuleId} · {rule.Outcome}. Guideline §{rule.Citation.Section}, pages {string.Join(", ", rule.Citation.PrintedPages)}. {rule.Reason}");
        foreach (var diagnostic in result.Diagnostics)
        {
            var file = names.GetValueOrDefault(diagnostic.FileId, diagnostic.FileId);
            var location = diagnostic.PrimaryLocation is { } point ? $":{point.Line}:{point.Column}" : "";
            detail.AppendLine($"{file}{location} · {diagnostic.RuleId} · {diagnostic.Message}");
        }
        if (IsIncomplete(report)) detail.AppendLine("The supported scan did not cover the entire source.");
        QueueCheckWork(() => SaveCheckAsync(CheckReport.Create(snapshot.ProjectId, "source", title, result.AggregateOutcome.ToString(),
            snapshot.BaseHeadRevisionId, result.SnapshotId, detail.ToString(), "source-" + result.SnapshotId)));
    }

    private void OnCheckCompilationChanged() => QueueCheckWork(async () =>
    {
        if (CompileSession.State is not { IsAttemptCurrent: true }
            || CompileSession.Attempt is not { Attempt: { } attempt } presentation
            || attempt.SnapshotIdentity.SessionId != Coordinator.SessionId || _lastSavedCompile == attempt.JobId.Value) return;
        _lastSavedCompile = attempt.JobId.Value;

        var main = attempt.Snapshot.Files.FirstOrDefault(file => file.FileId == attempt.Snapshot.MainFileId)?.RelativePath;
        var details = new List<string> { $"Main file: {main}" };
        if (presentation.ParserResult is CompilerLogParseResult.Parsed parsed)
        {
            details.AddRange(parsed.Report.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Severity} · {diagnostic.StableCode} · {diagnostic.Location?.RelativePath}{(diagnostic.Location is { } point ? $":{point.Line}" : "")} {diagnostic.SafeMessage}"));
        }
        else details.AddRange(attempt.Diagnostics.Select(diagnostic => $"{diagnostic.Code} · {diagnostic.SafeMessage}"));
        if (details.Count == 1) details.Add("No structured diagnostics were reported for this compile.");
        await SaveCheckAsync(CheckReport.Create(attempt.SnapshotIdentity.ProjectId, "compiler", "Compilation", attempt.Status.ToString(),
            attempt.SnapshotIdentity.BaseHeadRevisionId, attempt.InputContentDigest.Hex, string.Join(Environment.NewLine, details),
            "compiler-" + attempt.JobId.Value.ToString("N")));
    });

    private async Task SaveCheckAsync(CheckReport report)
    {
        if (_pageDisposed || Coordinator.Project is not { } project || report.ProjectId != project.Id.ToString()) return;
        var session = Coordinator.SessionId;
        try
        {
            if (Coordinator.IsReadOnly || Coordinator.Recovery is not null || Coordinator.LockId is not { } lockId
                || !await CheckReports.SaveAsync(project.Id, lockId, report))
                throw new InvalidOperationException("Report write requires project editing access.");
            _pendingCheckWrites.Remove(report.Id);
            if (session != Coordinator.SessionId || _pageDisposed) return;
            if (report.Kind == "source")
            {
                _lastStoredSourceSnapshot = report.EvidenceIdentity;
                _sourceStorageNotice = "Report saved in this browser.";
            }
            if (PendingCheckCount == 0) _checkStorageNotice = null;
            if (_checksSection == ChecksSection.Saved)
            {
                if (_loadingChecks) _refreshChecksAfterRead = true;
                else await ReadChecksAsync(older: false);
            }
        }
        catch (Exception error) when (error is JSException or InvalidOperationException or InvalidDataException)
        {
            if (_pageDisposed || session != Coordinator.SessionId) return;
            if (report.Kind == "source") _sourceStorageNotice = "The report is ready. Its local save needs attention.";
            if (_pendingCheckWrites.Count < 20 || _pendingCheckWrites.ContainsKey(report.Id))
            {
                _pendingCheckWrites[report.Id] = report;
                _checkStorageNotice = Coordinator.IsReadOnly
                    ? "This tab needs editing access to save reports. Take over editing and retry local storage."
                    : error is InvalidDataException
                        ? "A report exceeded the local storage size limit. Run a smaller check."
                        : "A report could not be saved in this browser. Retry local storage.";
            }
            else _checkStorageNotice = "The local report retry queue is full. The latest result is available in its live check.";
        }
        if (!_pageDisposed) StateHasChanged();
    }

    private async Task ReadChecksAsync(bool older)
    {
        if (_pageDisposed || _loadingChecks || Coordinator.Project is not { } project) return;
        var session = Coordinator.SessionId;
        _loadingChecks = true;
        try
        {
            var (reports, hasMore) = await CheckReports.ReadAsync(project.Id, older ? _savedChecks.LastOrDefault() : null);
            if (_pageDisposed || session != Coordinator.SessionId) return;
            if (older) _savedChecks.AddRange(reports.Where(report => _savedChecks.All(saved => saved.Id != report.Id)));
            else _savedChecks = reports.ToList();
            _hasOlderChecks = hasMore;
            if (PendingCheckCount == 0) _checkStorageNotice = null;
        }
        catch (Exception error) when (error is JSException or InvalidOperationException or InvalidDataException or System.Text.Json.JsonException)
        {
            if (!_pageDisposed && session == Coordinator.SessionId) _checkStorageNotice = "Saved reports could not be opened. Retry local storage.";
        }
        finally
        {
            if (!_pageDisposed && session == Coordinator.SessionId)
            {
                _loadingChecks = false;
                StateHasChanged();
                if (_refreshChecksAfterRead)
                {
                    _refreshChecksAfterRead = false;
                    QueueCheckWork(() => ReadChecksAsync(older: false));
                }
            }
        }
    }

    private async Task RetryCheckStorageAsync()
    {
        var project = Coordinator.Project?.Id.ToString();
        foreach (var report in _pendingCheckWrites.Values.Where(report => report.ProjectId == project).ToArray())
            await SaveCheckAsync(report);
        await ReadChecksAsync(older: false);
    }

    private async Task RemoveCheckAsync(string id)
    {
        if (Coordinator.IsReadOnly || Coordinator.LockId is not { } lockId || Coordinator.Project is not { } project
            || !_savedChecks.Any(report => report.Id == id)) return;
        var session = Coordinator.SessionId;
        try
        {
            if (!await CheckReports.RemoveAsync(project.Id, lockId, id))
                throw new InvalidOperationException("Report deletion requires project editing access.");
            if (session == Coordinator.SessionId) _savedChecks.RemoveAll(report => report.Id == id);
        }
        catch (Exception error) when (error is JSException or InvalidOperationException)
        {
            if (session == Coordinator.SessionId) _checkStorageNotice = "The report could not be removed. Try again with editing access.";
        }
    }
}
