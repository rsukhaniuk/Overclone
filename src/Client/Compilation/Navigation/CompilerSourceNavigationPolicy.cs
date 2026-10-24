using Client.Compilation.Presentation;
using Client.Editing;
using Client.Workspace;
using Client.Workspace.Transfer;

namespace Client.Compilation.Navigation;

/// <summary>Read-only source-line policy on the workspace UI context. Borrows the
/// workspace/session; disposal invalidates prepared selections, never their owners.
/// No DOM success is inferred here: the caller must use the existing atomic JS
/// file/full-value guard and check this policy again after every await.</summary>
public sealed class CompilerSourceNavigationPolicy(WorkspaceCoordinator workspace, CompileWorkspaceSession session) : IDisposable
{
    private bool _disposed;
    public bool IsCurrent(CompilerSourceNavigationRequest request, CancellationToken token = default)
    {
        var identity = request.Snapshot.Identity;
        return !_disposed && !token.IsCancellationRequested && request.HasCurrentAuthority(session)
            && workspace.IsOpen && workspace.Recovery is null
            && workspace.Project?.Id == identity.ProjectId && workspace.SessionId == identity.SessionId
            && workspace.WorkingGeneration == identity.WorkingGeneration
            && workspace.CurrentRevision?.Id == identity.BaseHeadRevisionId
            && Target(request) is not null;
    }
    private WorkingFile? Target(CompilerSourceNavigationRequest request) => workspace.Files.FirstOrDefault(f =>
        !f.Deleted && f.Id == request.File.FileId && f.RelativePath == request.File.RelativePath
        && f.MediaKind == "text" && f.HasBom == request.File.HasBom);

    public async Task<Selection?> PrepareAsync(CompilerSourceNavigationRequest request, CancellationToken token = default)
    {
        if (!IsCurrent(request, token) || Target(request) is not { } file) return null;
        // Actual byte materialization is necessary: a durable hash allegation or
        // a same-line DOM string is insufficient. Recheck authority after IO.
        byte[] bytes;
        try { bytes = await workspace.GetFileBytesAsync(file, token); }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
        if (!IsCurrent(request, token) || !ReferenceEquals(Target(request), file)
            || !bytes.AsSpan().SequenceEqual(request.File.ByteSpan)
            || Client.Workspace.Domain.SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes)) != request.File.Hash
            || !TextContent.TryDecode(bytes, out var source, out var bom) || bom != request.File.HasBom
            || source != file.Content) return null;
        var location = request.Location;
        var range = SourceTools.ToTextarea(source, new(location.SourceUtf16Start, location.SourceUtf16End));
        if (range is not { } dom) return null; // Includes valid empty-line carets.
        var selection = new Selection(this, request, file, source, dom);
        return IsCurrent(selection, token) ? selection : null;
    }

    /// <summary>Final synchronous identity + verified preimage check immediately
    /// before trySelectForFind, and again after its await. Read-only find is allowed
    /// in a read-only workspace; takeover still changes the session and invalidates.</summary>
    public bool IsCurrent(Selection selection, CancellationToken token = default, bool requireActiveFile = false)
    {
        if (!ReferenceEquals(selection.Owner, this) || !IsCurrent(selection.Request, token)
            || Target(selection.Request) is not { } file || !ReferenceEquals(file, selection.WorkingFile)
            || file.Content != selection.Source || file.DirtyContent != selection.DirtyContent
            || file.IsNew != selection.IsNew || file.DurableHash != selection.DurableHash
            || !ReferenceEquals(file.PendingTextBytes, selection.PendingBytes)
            || (requireActiveFile && workspace.ActiveFileId != file.Id)) return false;
        // New unedited imports are backed by a mutable array: compare actual
        // bytes again, rather than trusting its array identity or alleged hash.
        return file.PendingTextBytes is not null || !file.IsNew || file.DirtyContent || file.ImportedBytes is null
            || file.ImportedBytes.AsSpan().SequenceEqual(selection.Request.File.ByteSpan);
    }
    public void Dispose() => _disposed = true;

    public sealed class Selection
    {
        internal Selection(CompilerSourceNavigationPolicy owner, CompilerSourceNavigationRequest request,
            WorkingFile file, string source, SourceRange dom)
        {
            Owner = owner; Request = request; WorkingFile = file; Source = source; TextareaRange = dom;
            DirtyContent = file.DirtyContent; IsNew = file.IsNew; DurableHash = file.DurableHash; PendingBytes = file.PendingTextBytes;
        }
        internal CompilerSourceNavigationPolicy Owner { get; }
        internal WorkingFile WorkingFile { get; }
        internal bool DirtyContent { get; }
        internal bool IsNew { get; }
        internal Client.Workspace.Domain.SHA256? DurableHash { get; }
        internal Client.Workspace.Patching.ExactTextBytes? PendingBytes { get; }
        public CompilerSourceNavigationRequest Request { get; }
        public string Source { get; }
        public string ExpectedTextareaText => SourceTools.TextareaText(Source);
        public SourceRange TextareaRange { get; }
    }
}
