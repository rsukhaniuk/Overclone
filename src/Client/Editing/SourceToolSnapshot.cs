using Client.Workspace;
using Client.Workspace.Domain;

namespace Client.Editing;

/// <summary>Read before any interop await. Exact text, session, generation, file and
/// page intent must still match after every await. A file switch invalidates the
/// page intent even when the user subsequently switches back to identical text.</summary>
public sealed record SourceToolSnapshot(Guid Session, long Generation, FileId File, string Text, long Intent)
{
    public static SourceToolSnapshot? Capture(WorkspaceCoordinator workspace, long intent) =>
        workspace.IsOpen && workspace.ActiveFile is { Deleted: false, MediaKind: "text" } file
            ? new(workspace.SessionId, workspace.WorkingGeneration, file.Id, file.Content, intent) : null;

    public bool IsCurrent(WorkspaceCoordinator workspace, long intent, bool editing) =>
        Intent == intent && Session == workspace.SessionId && Generation == workspace.WorkingGeneration &&
        workspace.IsOpen && workspace.ActiveFile is { Deleted: false, MediaKind: "text" } file &&
        file.Id == File && string.Equals(file.Content, Text, StringComparison.Ordinal) &&
        (!editing || !workspace.IsReadOnly && workspace.Recovery is null);
}
