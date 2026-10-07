namespace Client.Workspace;

/// <summary>
/// Single-writer exclusion per project using the Web Locks API. A second tab
/// that cannot acquire the lock opens read-only. Missing Web Locks support is a
/// capability error for writable projects, never permission to race.
/// </summary>
public interface IWorkspaceLocks : IAsyncDisposable
{
    Task<WorkspaceCapabilities> GetCapabilitiesAsync(CancellationToken ct);
    /// <summary>Acquire without waiting. Returns a non-null lock id on success,
    /// null if another client holds the lock.</summary>
    Task<string?> TryAcquireAsync(string name, CancellationToken ct);
    /// <summary>Wait for the lock to become available (explicit takeover).</summary>
    Task<string> WaitForAsync(string name, CancellationToken ct);
    Task<bool> ReleaseAsync(string lockId, CancellationToken ct);
    Task<bool> IsHeldAsync(string name, CancellationToken ct);
}

public record WorkspaceCapabilities(bool Opfs, bool IndexedDb, bool WebLocks, bool PersistentStorage);
