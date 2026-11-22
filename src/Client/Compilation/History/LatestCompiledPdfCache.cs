using System.Collections.Immutable;
using Client.Compilation.Native;
using Client.Compilation.Presentation;
using Client.Pdf;
using Client.Workspace.Domain;
using Microsoft.JSInterop;

namespace Client.Compilation.History;

internal sealed record CachedCompiledPdf(PdfDocument Document, ImmutableArray<ProjectFile> Files);

/// <summary>The last accepted PDF per project. A restored PDF carries no live compile authority.</summary>
public sealed class LatestCompiledPdfCache(IJSRuntime js, ArtifactHistoryBudget budget)
{
    private readonly IReadOnlyList<CompileOutputDescriptor> _adapters = AuthoringWorkspaceRegistration.HistoricalOutputAdapters();

    internal async Task<CachedCompiledPdf?> ReadAsync(ProjectId project)
    {
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/workspace-storage.js");
        var row = await module.InvokeAsync<StoredPdf?>("readLatestCompiledPdf", project.ToString());
        if (row is null) return null;
        if (row.Metadata is not { Length: > 0 } metadata || row.Pdf is not { Length: > 0 } pdf)
            throw new InvalidDataException("Invalid cached PDF record.");
        var content = HistoricalArtifactCodec.Decode(metadata, project, NativeRuntimePackage.Pdf,
            NativeRuntimePackage.Adapter, budget, _adapters);
        var document = new PdfDocument(pdf, pdf.Length > PdfDocument.DefaultInputBytes ? budget.CompiledInputCapacity : null);
        if (content.Metadata.PdfLength != document.Length || content.Metadata.PdfHash.Hex != document.Sha256)
            throw new InvalidDataException("Cached PDF failed integrity verification.");
        return new(document, content.Files);
    }

    internal async Task<bool> SaveAsync(AcceptedCompileOutput output, string lockId, Func<bool> stillOwned)
    {
        if (output.Status != CompileOutputStatus.Succeeded || output.ContentIdentity is null
            || output.OutputLength < 1 || output.OutputLength > budget.MaxPdfBytes) return false;
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/workspace-storage.js");
        if (!stillOwned()) return false;
        // The live accepted output owns and has already verified these bytes.
        var metadata = HistoricalArtifactCodec.Encode(HistoricalArtifactCodec.FromAccepted(output, null), budget.MaxMetadataBytes);
        return await module.InvokeAsync<bool>("writeLatestCompiledPdf", CancellationToken.None, output.SnapshotIdentity.ProjectId.ToString(),
            lockId, metadata, output.ToPayloadArray());
    }

    private sealed record StoredPdf(byte[]? Metadata, byte[]? Pdf);
}
