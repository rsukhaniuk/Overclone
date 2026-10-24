using Client.Compilation.Diagnostics;
using Client.Compilation.Lifecycle;
using Client.Compilation.Presentation;

namespace Client.Compilation.Navigation;

/// <summary>Parser/report authority, never UI-alleged file IDs or reconstructed snapshots.
/// Failed attempts are valid authorities; the last successful PDF is irrelevant.</summary>
public sealed class CompilerSourceNavigationRequest
{
    private readonly CompileWorkspaceSession _session;
    private readonly CompileAttemptPresentation _presentation;
    private readonly CompileLifecycleTicket _ticket;
    private CompilerSourceNavigationRequest(CompileWorkspaceSession session, CompileAttemptPresentation presentation,
        CompileLifecycleTicket ticket, CompilerLogReport report, CompilerLogDiagnostic diagnostic, CompilationInputFile file)
    { _session = session; _presentation = presentation; _ticket = ticket; Report = report; Diagnostic = diagnostic; File = file; }
    public CompilerLogReport Report { get; }
    public CompilerLogDiagnostic Diagnostic { get; }
    public CompilationInputSnapshot Snapshot => Report.Snapshot;
    public CompilerLineLocation Location => Diagnostic.Location!;
    public CompilationInputFile File { get; }

    public static CompilerSourceNavigationRequest? TryCreate(CompileWorkspaceSession session,
        CompilerLogReport report, CompilerLogDiagnostic diagnostic)
    {
        if (session.Disposed || diagnostic.LocationIssue != CompilerLocationIssue.None || diagnostic.Location is not { Line: > 0 } location
            || !report.Diagnostics.Any(item => ReferenceEquals(item, diagnostic))) return null;
        if (session.Attempt is not { ParserResult: CompilerLogParseResult.Parsed parsed, Attempt: { } attempt } presentation
            || !ReferenceEquals(parsed.Report, report) || !ReferenceEquals(attempt.Snapshot, report.Snapshot)
            || session.LastTicket is not { } ticket || !ReferenceEquals(ticket.Snapshot, report.Snapshot)) return null;
        var file = report.Snapshot.Files.FirstOrDefault(f => f.FileId == location.FileId);
        if (file is not { MediaKind: "text" } || file.RelativePath != location.RelativePath || file.Hash != location.FileHash
            || location.StartByte < 0 || location.EndByte < location.StartByte || location.EndByte > file.ByteLength) return null;
        var request = new CompilerSourceNavigationRequest(session, presentation, ticket, report, diagnostic, file);
        return request.HasCurrentAuthority(session) ? request : null;
    }

    internal bool HasCurrentAuthority(CompileWorkspaceSession session)
    {
        if (!ReferenceEquals(_session, session) || session.Disposed
            || !ReferenceEquals(session.Attempt, _presentation) || !ReferenceEquals(session.LastTicket, _ticket)
            || session.SelectionVersion != Snapshot.Identity.SelectionVersion) return false;
        var state = session.State;
        return state is { Disposed: false, IsAttemptCurrent: true }
            && ReferenceEquals(state.LastAttempt, _presentation.Attempt)
            && ReferenceEquals(_ticket.Snapshot, Snapshot);
    }
}
