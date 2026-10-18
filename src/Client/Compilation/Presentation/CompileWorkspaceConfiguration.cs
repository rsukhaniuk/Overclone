using Client.Compilation.Lifecycle;
using Client.Pdf;

namespace Client.Compilation.Presentation;

public sealed record CompileReadiness(bool Ready, string Status, string Message);
/// <summary>Trusted application composition only. Never derive executable selection
/// from document text, storage catalog, caller provenance or a matching hash alone.</summary>
public sealed record CompileWorkspacePorts(string CapabilityId, string Label, CompilationDescriptor Engine,
    CompilationDescriptor[] Bundles, CompilationDescriptor[] Tools, ICompilationWorkerFactory Factory,
    IPdfInspector? Inspector, Func<Task<CompileReadiness>> Readiness, IAsyncDisposable? Lifetime = null,
    bool SelectableBibliography = false);
public sealed record CompileWorkspaceConfigurationResult(CompileReadiness Compiler, CompileWorkspacePorts? Ports);
public interface ICompileWorkspaceConfiguration
{
    Task<CompileWorkspaceConfigurationResult> OpenAsync();
}
public sealed class UnavailableCompileWorkspaceConfiguration : ICompileWorkspaceConfiguration
{
    public Task<CompileWorkspaceConfigurationResult> OpenAsync() => Task.FromResult(new CompileWorkspaceConfigurationResult(
        new(false, "Unavailable", "No compiler has been approved and configured. Editing, saving and source analysis remain available."), null));
}
