using System.Text.Json;
using Client.Workspace;
using Client.Compilation;
using Client.Compilation.Lifecycle;

namespace AuthoringBrowserHarness;

// TEST measurement only; no replacement workspace, owner context or native calls.
public sealed class OwnerMeasurements
{
    private readonly List<object> _rows = [];
    public event Action? Changed;
    public void Record(string stage, WorkspaceCoordinator workspace, string? outcome = null)
    {
        if (_rows.Count >= 96) throw new InvalidOperationException("Finite owner measurement cap.");
        var context = SynchronizationContext.Current;
        _rows.Add(new { stage, context = context?.GetType().AssemblyQualifiedName, contextId = context is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(context),
            thread = Environment.CurrentManagedThreadId, workspace.WorkingGeneration, workspace.SessionId, open = workspace.IsOpen, head = workspace.CurrentRevision?.Id.ToString(), outcome });
        Changed?.Invoke();
    }
    public string Json => JsonSerializer.Serialize(_rows);
    public async Task ProbeB3(WorkspaceCoordinator workspace)
    {
        Record("b3-create-entry", workspace);
        try
        {
            await using var lifecycle = new CompilationLifecycleService(workspace, new CompilationSelectionState(), new Absent(), new(2, 1000), TimeProvider.System);
            _ = lifecycle.State; Record("b3-created", workspace);
        }
        catch (InvalidOperationException error) { Record("b3-create-refused", workspace, error.Message); }
    }
    private sealed class Absent : ICompilationWorkerFactory
    { public ICompilationWorker? Create(CompileJobId id, long epoch) => null; }
}
