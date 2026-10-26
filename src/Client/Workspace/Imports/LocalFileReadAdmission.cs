using System.Runtime.CompilerServices;

namespace Client.Workspace.Imports;

/// <summary>Single workspace-local actual-read lease shared by additive import
/// and replacement, including across page remounts. Not a browser peak theorem.</summary>
internal sealed class LocalFileReadAdmission
{
    private static readonly ConditionalWeakTable<WorkspaceCoordinator, LocalFileReadAdmission> Admissions = new();
    internal static LocalFileReadAdmission For(WorkspaceCoordinator workspace) => Admissions.GetValue(workspace, _ => new());
    private readonly object _gate = new();
    private object? _owner;
    internal event Action? Changed;
    internal bool Busy { get { lock (_gate) return _owner is not null; } }
    internal bool Enter(object owner)
    {
        Action? changed;
        lock (_gate) { if (_owner is not null) return false; _owner = owner; changed = Changed; }
        changed?.Invoke(); // Other live controls must render Busy on acquisition too.
        return true;
    }
    internal void Release(object owner)
    {
        Action? changed;
        lock (_gate) { if (!ReferenceEquals(_owner, owner)) return; _owner = null; changed = Changed; }
        changed?.Invoke();
    }
}
