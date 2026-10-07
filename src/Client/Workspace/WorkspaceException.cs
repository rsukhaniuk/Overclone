namespace Client.Workspace;

/// <summary>
/// Thrown when the workspace cannot proceed due to a typed, user-presentable
/// storage or capability condition. SafeMessage never includes secrets or full
/// provider payloads.
/// </summary>
public sealed class WorkspaceException(string code, string safeMessage, Exception? inner = null)
    : Exception(safeMessage, inner)
{
    public string Code { get; } = code;
}
