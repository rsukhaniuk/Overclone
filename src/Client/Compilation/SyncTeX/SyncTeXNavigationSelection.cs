using Client.Editing;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Patching;

namespace Client.Compilation.SyncTeX;

/// <summary>Verified read-only source preimage and DOM-normalized range. The UI
/// must explicitly choose the file and use its atomic file/full-value guard;
/// possession of a selection does not authorize an edit or prove DOM success.</summary>
public sealed class SyncTeXNavigationSelection
{
    internal SyncTeXNavigationSelection(SyncTeXNavigationPolicy owner, SyncTeXNavigationResult result,
        SyncTeXNavigationMatch match, WorkingFile file, string source, SourceRange dom, CancellationToken cancellation)
    {
        Owner = owner; Result = result; Match = match; WorkingFile = file; Source = source; TextareaRange = dom;
        Cancellation = cancellation; DirtyContent = file.DirtyContent; IsNew = file.IsNew;
        DurableHash = file.DurableHash; PendingBytes = file.PendingTextBytes; ImportedBytes = file.ImportedBytes;
    }
    internal SyncTeXNavigationPolicy Owner { get; }
    internal WorkingFile WorkingFile { get; }
    internal CancellationToken Cancellation { get; }
    internal bool DirtyContent { get; }
    internal bool IsNew { get; }
    internal SHA256? DurableHash { get; }
    internal ExactTextBytes? PendingBytes { get; }
    internal byte[]? ImportedBytes { get; }
    public SyncTeXNavigationResult Result { get; }
    public SyncTeXNavigationMatch Match { get; }
    public CompilationInputFile File => Match.File;
    public string Source { get; }
    public string ExpectedTextareaText => SourceTools.TextareaText(Source);
    public SourceRange SourceRange => Match.SourceRange;
    public SourceRange TextareaRange { get; }
}
