using Client.Workspace.Domain;

namespace Client.Workspace.Transfer;

/// <summary>The same bounded ZIP builder used by the receipt-only transfer API.</summary>
public interface IArchiveExport
{
    Task<ArchiveExport> BuildZipAsync(ExportSource source, CancellationToken ct);
}

/// <summary>Owned completed archive; callers must not mutate Bytes after preview.</summary>
public sealed record ArchiveExport(RevisionId CapturedRevision, byte[] Bytes, int FileCount)
{
    public ExportReceipt Receipt => new(CapturedRevision, Bytes.LongLength, FileCount);
}
