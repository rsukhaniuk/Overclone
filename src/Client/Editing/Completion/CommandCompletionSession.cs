using Client.Workspace;
using Client.Workspace.Domain;

namespace Client.Editing.Completion;

/// <summary>UI-owned proposal authority only, no IO/model mutation. The page owns
/// actual DOM reads, settlement admission and its ordinary guarded edit path.</summary>
public sealed class CommandCompletionSession(WorkspaceCoordinator workspace, IWorkspaceLocks locks) : IDisposable
{
    private bool _disposed;
    private Request? _request;
    private Proposal? _proposal;
    public Request? PendingRequest => _request;
    public Proposal? CurrentProposal => _proposal;

    public Request? Begin(long intent)
    {
        Clear();
        var snapshot = SourceToolSnapshot.Capture(workspace, intent);
        if (_disposed || snapshot is null || !snapshot.IsCurrent(workspace, intent, true)
            || workspace.Project is null || workspace.LockId is not { } id || !Owned(id)) return null;
        return _request = new(snapshot, workspace.Project.Id, id, workspace.ActiveFile!);
    }
    private bool Owned(string id)
    {
        try { return locks.IsOwnedLocally(id, "oop-project:" + workspace.Project!.Id); }
        catch (Exception e) when (e is not OutOfMemoryException) { return false; }
    }
    public bool IsCurrent(Request request, long intent) => !_disposed && ReferenceEquals(_request, request)
        && workspace.Project?.Id == request.Project && workspace.LockId == request.Lock
        && ReferenceEquals(workspace.ActiveFile, request.FileObject) && Owned(request.Lock)
        && request.Snapshot.IsCurrent(workspace, intent, true);
    public bool IsCurrent(Proposal proposal, long intent) => ReferenceEquals(_proposal, proposal) && IsCurrent(proposal.Request, intent);

    public Proposal? Propose(Request request, SourceRange domCaret, long intent, out CompletionMatchState state)
    {
        state = CompletionMatchState.Unavailable;
        if (!IsCurrent(request, intent) || !SourceTools.TryFromTextarea(request.Snapshot.Text, domCaret, out var sourceCaret)) return null;
        var matches = LatexCommandCompletion.Suggest(request.Snapshot.Text, sourceCaret); state = matches.State;
        if (state != CompletionMatchState.Ready || matches.Prefix is not { } prefix) return null;
        return _proposal = new(request, domCaret, sourceCaret, prefix, matches.Commands);
    }
    public SourceEdit? Consume(Proposal proposal, string commandId, SourceRange actualDomCaret, long intent, long maxBytes)
    {
        if (!IsCurrent(proposal, intent) || actualDomCaret != proposal.DomCaret) return null;
        var edit = LatexCommandCompletion.Transform(proposal.Request.Snapshot.Text, proposal.Prefix, commandId, maxBytes);
        if (edit is not null)
        {
            proposal.ConsumedEdit = edit;
            Clear(); // Captured buttons are one-shot, even if the later model edit refuses.
        }
        return edit;
    }
    public bool IsPostEditCurrent(Proposal proposal, SourceToolSnapshot after, long intent) => !_disposed
        && proposal.ConsumedEdit is { } edit && proposal.Request.Snapshot.Intent == intent
        && after.Session == proposal.Request.Snapshot.Session && after.File == proposal.Request.Snapshot.File
        && after.Generation == proposal.Request.Snapshot.Generation + 1 && after.Text == edit.Text
        && after.IsCurrent(workspace, intent, true) && workspace.Project?.Id == proposal.Request.Project
        && workspace.LockId == proposal.Request.Lock && ReferenceEquals(workspace.ActiveFile, proposal.Request.FileObject)
        && Owned(proposal.Request.Lock);

    public bool Dismiss(Proposal proposal)
    {
        if (!ReferenceEquals(_proposal, proposal)) return false;
        Clear(); return true;
    }
    public void Clear() { _proposal = null; _request = null; }
    public void Dispose() { _disposed = true; Clear(); }

    public sealed class Request
    {
        internal Request(SourceToolSnapshot snapshot, ProjectId project, string lockId, WorkingFile file)
        { Snapshot = snapshot; Project = project; Lock = lockId; FileObject = file; }
        public SourceToolSnapshot Snapshot { get; }
        internal ProjectId Project { get; }
        internal string Lock { get; }
        internal WorkingFile FileObject { get; }
    }
    public sealed class Proposal
    {
        internal Proposal(Request request, SourceRange dom, SourceRange source, SourceRange prefix, IReadOnlyList<LatexCommand> commands)
        { Request = request; DomCaret = dom; SourceCaret = source; Prefix = prefix; Commands = commands; }
        public Request Request { get; }
        public SourceRange DomCaret { get; }
        public SourceRange SourceCaret { get; }
        public SourceRange Prefix { get; }
        public IReadOnlyList<LatexCommand> Commands { get; }
        internal SourceEdit? ConsumedEdit { get; set; }
    }
}
