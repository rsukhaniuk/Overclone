using System.Collections.Immutable;
using Client.Analysis;
using Client.Workspace.Domain;
using Client.Workspace.Patching;

namespace Client.Review;

public enum ReviewCode { InvalidInput, UnknownFix, Stale, Consumed, Busy, PendingSave, ReadOnly, Cancelled, Conflict, Unavailable, Disposed }
public enum ReviewStatus { Idle, Previewing, Ready, Applying, AppliedUnsaved, AppliedSaved, SaveFailed, Undoing, UndoAppliedUnsaved, UndoSaved, Disposed }
public sealed record ReviewFailure(ReviewCode Code, string SafeMessage);
public sealed record ChangeSetCreation(TextChangeSet? ChangeSet, ReviewFailure? Failure);

public sealed class ProposedTextEdit
{
    internal ProposedTextEdit(TextEditInput e) { StartByte=e.StartByte; EndByte=e.EndByte; Expected=new(e.Expected.Span); Replacement=new(e.Replacement.Span); }
    public long StartByte { get; }
    public long EndByte { get; }
    public OwnedPatchBytes Expected { get; }
    public OwnedPatchBytes Replacement { get; }
}
public sealed class ProposedFileChange
{
    internal ProposedFileChange(FilePatchInput f)
    { FileId=f.FileId; RelativePath=f.RelativePath; ExpectedHash=f.ExpectedHash; ExpectedLength=f.ExpectedLength; Edits=f.Edits.Select(e=>new ProposedTextEdit(e)).ToImmutableArray(); }
    public FileId FileId { get; }
    public string RelativePath { get; }
    public SHA256 ExpectedHash { get; }
    public long ExpectedLength { get; }
    public ImmutableArray<ProposedTextEdit> Edits { get; }
    internal FilePatchInput Input() => new(FileId,RelativePath,ExpectedHash,ExpectedLength,Edits.Select(e=>new TextEditInput(e.StartByte,e.EndByte,e.Expected.ToArray(),e.Replacement.ToArray())).ToArray());
}

/// <summary>Owned proposal, not authorization or a rule finding. Generic callers
/// may propose User/Ai text only. Rule origin is reserved for canonical F1 mapping.
/// Structural/byte semantics are validated centrally by 4a during preview.</summary>
public sealed class TextChangeSet
{
    private TextChangeSet(TextPatchOrigin origin,string explanation,ImmutableArray<ProposedFileChange> files)
    { Id=Guid.NewGuid(); Origin=origin; Explanation=explanation; Files=files; }
    /// <summary>Reuse the canonical worker envelope's 4 MiB metadata budget;
    /// byte payloads retain the separate 8/64 MiB patch limits.</summary>
    public const long MaxMetadataBytes=AnalysisLimits.DMaxResultBytes;
    public Guid Id { get; }
    public TextPatchOrigin Origin { get; }
    public string Explanation { get; }
    public ImmutableArray<ProposedFileChange> Files { get; }
    public static ChangeSetCreation Create(TextPatchOrigin origin,string explanation,IReadOnlyList<FilePatchInput> files)
        => Own(origin,explanation,files,allowRule:false);
    internal static ChangeSetCreation Own(TextPatchOrigin origin,string explanation,IReadOnlyList<FilePatchInput> files,bool allowRule)
    {
        ChangeSetCreation Bad()=>new(null,new(ReviewCode.InvalidInput,"The bounded text proposal is invalid."));
        if (!Enum.IsDefined(origin) || origin==TextPatchOrigin.Rule&&!allowRule || string.IsNullOrWhiteSpace(explanation)
            || !AnalysisIdentity.IsValidOpaqueId(explanation,4096) || files is null || files.Count is 0 or >WorkspaceTextLimits.FileCount) return Bad();
        long expected=0,replacement=0,source=0,metadata=System.Text.Encoding.UTF8.GetByteCount(explanation);
        var ids=new HashSet<FileId>();
        foreach(var f in files)
        {
            if(f is null||!ids.Add(f.FileId)||f.ExpectedLength<0||f.ExpectedLength>WorkspaceTextLimits.FileBytes
                || !AnalysisIdentity.IsValidOpaqueId(f.RelativePath,1024)||f.Edits is null||f.Edits.Count==0||f.Edits.Count>f.ExpectedLength+1) return Bad();
            source+=f.ExpectedLength;metadata+=16+64+sizeof(long)+System.Text.Encoding.UTF8.GetByteCount(f.RelativePath);
            if(source>WorkspaceTextLimits.ProjectBytes||metadata>MaxMetadataBytes)return Bad();
            foreach(var e in f.Edits)
            {
                if(e is null||e.Expected.Length>WorkspaceTextLimits.FileBytes||e.Replacement.Length>WorkspaceTextLimits.FileBytes)return Bad();
                expected+=e.Expected.Length;replacement+=e.Replacement.Length;metadata+=2*sizeof(long)+2*sizeof(int);
                if(expected>WorkspaceTextLimits.ProjectBytes||replacement>WorkspaceTextLimits.ProjectBytes||metadata>MaxMetadataBytes)return Bad();
            }
        }
        // All copying occurs after aggregate preflight; no IReadOnlyList/Memory
        // alias survives. Preparation later validates UTF-8, spans and preimages.
        return new(new(origin,explanation,files.Select(f=>new ProposedFileChange(f)).ToImmutableArray()),null);
    }
}

public sealed record ReviewCitation(string RuleId,string Version,string Section,ImmutableArray<int> PrintedPages,ImmutableArray<int> PdfPages);
public sealed record ReviewFilePreview(PreparedTextFile Exact,ImmutableArray<DiffLine> Lines,bool SummaryOnly)
{
    public string DiffScope => "Normalized line presentation only; exact byte spans/preimages/replacements remain authoritative (including newline bytes).";
}
public sealed record ReviewBinding(TextPatchIdentity Workspace,long WorkspaceEpoch,long SelectionVersion,AnalysisSelection Selection,
    AnalysisSnapshotId? SnapshotId,string? RequestId,long? WorkerEpoch);

public sealed class ReviewPreview
{
    internal ReviewPreview(TextChangeSet set,PreparedTextBatch prepared,ReviewBinding binding,AnalysisReport? report,
        AnalysisFixDescriptorDto? fix,ReviewCitation? citation,ImmutableArray<ReviewFilePreview> files)
    { Id=Guid.NewGuid(); Proposal=set; Prepared=prepared; Binding=binding; Report=report; Fix=fix; Citation=citation; Files=files; }
    public Guid Id { get; }
    public TextChangeSet Proposal { get; }
    public PreparedTextBatch Prepared { get; }
    public ReviewBinding Binding { get; }
    public ReviewCitation? Citation { get; }
    public ImmutableArray<ReviewFilePreview> Files { get; }
    internal AnalysisReport? Report { get; }
    internal AnalysisFixDescriptorDto? Fix { get; }
}
public sealed record ReviewActionView(Guid Id,Guid SessionId,bool UndoApplied,bool Persisted,bool CanUndo,PreparedTextBatch Exact);
public sealed record ReviewServiceState(ReviewStatus Status,ReviewPreview? Preview,ReviewActionView? Action,bool Busy,bool WorkspaceDirty,ReviewFailure? Notice);
public sealed record ReviewResult(ReviewStatus Status,ReviewPreview? Preview=null,Guid? ActionId=null,ReviewFailure? Failure=null,bool Changed=false);

public interface IChangeReviewService : IAsyncDisposable
{
    ReviewServiceState State { get; }
    event Action<ReviewServiceState>? Changed;
    Task<ReviewResult> PreviewAsync(TextChangeSet changeSet,CancellationToken ct);
    Task<ReviewResult> PreviewFixAsync(string descriptorId,CancellationToken ct);
    ReviewResult Decline(Guid previewId);
    void Cancel();
    Task<ReviewResult> AcceptAsync(Guid previewId,CancellationToken ct);
    Task<ReviewResult> RetrySaveAsync(Guid actionId,CancellationToken ct);
    Task<ReviewResult> UndoAsync(Guid actionId,CancellationToken ct);
}
