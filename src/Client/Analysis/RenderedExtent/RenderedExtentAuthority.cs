using Client.Compilation;
using Client.Compilation.Lifecycle;
using Client.Compilation.Native;
using Client.Compilation.Presentation;
using Client.Pdf;
using Client.Workspace;

namespace Client.Analysis.RenderedExtent;

/// <summary>Current successful output authority, NOT download's stale-PDF policy.
/// Private construction retains actual identities; no UI PDF/count/hash label is authority.</summary>
internal sealed class RenderedExtentAuthority
{
    private RenderedExtentAuthority(AcceptedCompileOutput output, CompileLifecycleTicket ticket,
        CompileAttemptPresentation presentation, CompileWorkspacePorts ports)
    { Output = output; Ticket = ticket; Presentation = presentation; Ports = ports; }
    internal AcceptedCompileOutput Output { get; }
    internal CompileLifecycleTicket Ticket { get; }
    internal CompileAttemptPresentation Presentation { get; }
    internal CompileWorkspacePorts Ports { get; }
    internal IPdfInspector? Inspector => Ports.Inspector;
    internal PdfRuntimeIdentity? ExpandedRenderer => Ports.NativeProfile == NativeSelectedPdfContract.Profile
        ? Compilation.Provisioning.BundledRuntimeAssets.SelectedPdfCapacity?.Renderer : null;
    internal PdfDocument CreateDocument(byte[] bytes) =>
        new(bytes, Output.OutputLength > PdfDocument.DefaultInputBytes ? Ports.CompiledPdfCapacity : null);
    internal static PdfInspectionBudget InspectionBudget(PdfDocument document) =>
        new(InputBytes: document.CompiledInputCapacity?.MaxInputBytes ?? PdfDocument.DefaultInputBytes, Pages: 512)
        { CompiledInputCapacity = document.CompiledInputCapacity };
    internal static RenderedExtentAuthority? Capture(WorkspaceCoordinator workspace, CompileWorkspaceSession compile)
    {
        if (compile.Disposed || compile.ReadingTools || compile.DisplayedOutput is not { } output
            || compile.LastTicket is not { } ticket || compile.Attempt is not { } presentation
            || compile.ConfiguredPorts is not { } ports) return null;
        var authority = new RenderedExtentAuthority(output, ticket, presentation, ports);
        return authority.Current(workspace, compile) ? authority : null;
    }
    internal bool Current(WorkspaceCoordinator workspace, CompileWorkspaceSession compile)
    {
        var project = workspace.Project;
        var ports = compile.ConfiguredPorts;
        if (project is null || ports is null || compile.Disposed || compile.ReadingTools || !workspace.IsOpen || workspace.Recovery is not null
            || !ReferenceEquals(compile.DisplayedOutput, Output) || !ReferenceEquals(compile.LastTicket, Ticket)
            || !ReferenceEquals(compile.Attempt, Presentation) || !ReferenceEquals(ports, Ports)
            || !ReferenceEquals(ports.Inspector, Inspector)
            || Output.Status != CompileOutputStatus.Succeeded || Output.ContentIdentity is null
            || Output.OutputFormat != NativeRuntimePackage.Pdf || Output.OutputAdapter != (ports.OutputAdapter ?? NativeRuntimePackage.Adapter)
            || Output.OutputAdapter != NativeRuntimePackage.Adapter && Output.OutputAdapter != NativeSyncTeXContract.OutputAdapter
                && Output.OutputAdapter != NativeLatexDviContract.OutputAdapter
                && Output.OutputAdapter != NativeCompilerSuiteContract.DviOutputAdapter
                && Output.OutputAdapter != NativeCompilerSuiteContract.XeOutputAdapter
                && !(NativeCompilerSuitePins.Ready && Output.OutputAdapter == NativeCompilerSuiteContract.LuaOutputAdapter)
                && !(NativeCompilerBiberPins.Ready && Enum.GetValues<NativeCompilerEngine>()
                    .Any(engine => Output.OutputAdapter == NativeBiberCompilerContract.Component(engine).OutputAdapter))
                && !(NativeCompilerThesisPins.Ready && Enum.GetValues<NativeCompilerEngine>()
                    .Any(engine => Output.OutputAdapter == NativeThesisCompilerContract.Component(engine).OutputAdapter))
                && !(NativeCompilerOriginPins.Ready && Enum.GetValues<NativeCompilerEngine>()
                    .Any(engine => Output.OutputAdapter == NativeOriginCompilerContract.Component(engine).OutputAdapter))
                && !(NativeCompilerInputBudgetPins.Ready && Enum.GetValues<NativeCompilerEngine>()
                    .Any(engine => Output.OutputAdapter == NativeInputBudgetContract.Component(engine).OutputAdapter))
                && !(ports.NativeProfile == NativeSelectedPdfContract.Profile && NativeCompilerSelectedPdfPins.Ready
                    && Enum.GetValues<NativeCompilerEngine>()
                        .Any(engine => Output.OutputAdapter == NativeCompilerComponents.For(NativeSelectedPdfContract.Profile, engine).OutputAdapter))
            || Output.OutputLength < 1 || Output.OutputLength > (Ports.CompiledPdfCapacity?.MaxInputBytes ?? PdfDocument.DefaultInputBytes) || !PdfHashes.IsSha(Output.OutputHash?.Hex)
            || Output.WorkerEpoch <= 0 || Output.RequestId.Value == Guid.Empty) return false;
        var snapshot = Output.Snapshot; var identity = snapshot.Identity;
        if (project.Id != identity.ProjectId || workspace.SessionId != identity.SessionId
            || workspace.WorkingGeneration != identity.WorkingGeneration || workspace.CurrentRevision?.Id != identity.BaseHeadRevisionId
            || project.HeadRevisionId != identity.BaseHeadRevisionId || compile.SelectionVersion != identity.SelectionVersion
            || compile.MainFile != snapshot.MainFileId || compile.EngineChoice != Ports.CapabilityId
            || !ReferenceEquals(Output.RequestContext.Snapshot, snapshot) || Output.InputContentDigest != snapshot.ContentDigest
            || !ReferenceEquals(Ticket.Snapshot, snapshot) || !Ticket.Completion.IsCompletedSuccessfully) return false;
        var completed = Ticket.Completion.Result;
        var state = compile.State;
        return completed.Reason == CompileLifecycleReason.Succeeded && ReferenceEquals(completed.Output, Output)
            && state is { Disposed: false, IsCurrent: true, IsAttemptCurrent: true, Active: null, Pending: null }
            && ReferenceEquals(state.LastSuccessful, Output) && ReferenceEquals(state.LastAttempt, Presentation.Attempt)
            && Presentation.Attempt is { Status: CompileOutputStatus.Succeeded } attempt
            && ReferenceEquals(completed.Attempt, attempt) && ReferenceEquals(attempt.RequestContext, Output.RequestContext)
            && ReferenceEquals(attempt.Snapshot, snapshot) && ReferenceEquals(snapshot.Selection, Output.RequestContext.Snapshot.Selection);
    }
}
