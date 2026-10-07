using Client.Workspace.Domain;

namespace Client.Workspace.Transfer;

/// <summary>
/// Architectural seam for portable project interchange. Two adapters implement
/// it: <see cref="ZipTransferService"/> (bounded C# ZIP via System.IO.Compression)
/// and <see cref="FolderTransferService"/> (browser directory handles). C# owns
/// all path/size/content validation, archive limits and workflow policy; JS glue
/// only transports bytes. Every import goes into a NEW project; exports bind to
/// an immutable revision and keep external-transfer status separate from local
/// save.
/// </summary>
public interface IProjectTransfer
{
    /// <summary>Stage a ZIP archive for import: validate paths, sizes, counts,
    /// depths and CRC-32, classify text/binary, and produce a preview. Nothing
    /// is published until <see cref="PublishAsync"/> is accepted.</summary>
    Task<TransferStageResult> StageZipImportAsync(byte[] zipBytes, string defaultName, CancellationToken ct);

    /// <summary>Publish a fully validated staged import as a new local project.
    /// Requires an explicit accepted <paramref name="staged"/> and the project
    /// name the user confirmed.</summary>
    Task<ImportPublishResult> PublishAsync(StagedProject staged, string projectName, CancellationToken ct);

    /// <summary>Export the exact bytes of an immutable revision to a bounded ZIP
    /// stream. Only files referenced by the revision manifest are included.
    /// Returns a receipt bound to the captured revision.</summary>
    Task<ExportReceipt> ExportZipAsync(ExportSource source, CancellationToken ct);
}

/// <summary>A staged import preview: the proposed project name (editable), the
/// validated files, and any validation errors. Nothing is published yet.</summary>
public record TransferStageResult(
    bool Valid,
    StagedProject Staged,
    IReadOnlyList<string> ExcludedTrees);

/// <summary>Source for an export: the revision to capture and a way to read each
/// file's exact bytes by content hash. The coordinator supplies the revision;
/// the repository supplies the bytes.</summary>
public record ExportSource(
    ProjectRevision Revision,
    Func<SHA256, CancellationToken, Task<byte[]>> ReadContent);

/// <summary>Receipt for a completed external export, bound to the captured
/// immutable revision. Separate from local-save status.</summary>
public record ExportReceipt(
    RevisionId CapturedRevision,
    long ByteLength,
    int FileCount,
    string? PartialNote = null);
