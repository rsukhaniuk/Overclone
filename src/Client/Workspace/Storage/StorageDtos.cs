using System.Collections.Immutable;
using Client.Workspace.Domain;

namespace Client.Workspace.Storage;

// Stable serialization shapes stored in IndexedDB. IDs and hashes are strings
// to keep the on-disk format independent of the in-memory record-struct layout.
// Property names serialize to camelCase through the Blazor interop defaults.

internal sealed class ProjectRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? HeadRevisionId { get; set; }
    public int SchemaVersion { get; set; } = 1;
}

internal sealed class FileRecord
{
    public string Id { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string MediaKind { get; set; } = "text";
    public string ContentHash { get; set; } = "";
    public long ByteLength { get; set; }
    // Additive v1 field: missing in legacy M1a records, defaults to false.
    public bool HasBom { get; set; }
}

internal sealed class RevisionRecord
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string? ParentRevisionId { get; set; }
    public List<FileRecord> Files { get; set; } = new();
    public string CreatedAt { get; set; } = "";
}

internal sealed class CheckpointRecord
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string RevisionId { get; set; } = "";
    public string Reason { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}

internal sealed class WriteContentResult
{
    public string Hash { get; set; } = "";
    public long ByteLength { get; set; }
    public bool Verified { get; set; }
}

internal sealed class VerifyContentResult
{
    public bool Exists { get; set; }
    public long Length { get; set; }
    public bool HashMatch { get; set; }
}

internal sealed class CommitBatchResult
{
    public bool Ok { get; set; }
    public string? ErrorCode { get; set; }
    public string? ActualHead { get; set; }
}

internal sealed class ImportResult
{
    public bool Published { get; set; }
    public string? ErrorCode { get; set; }
    public string? SafeMessage { get; set; }
}

internal static class Mapping
{
    public static ProjectRecord ToRecord(Project p) => new()
    {
        Id = p.Id.ToString(),
        Name = p.Name,
        HeadRevisionId = p.HeadRevisionId?.ToString(),
        SchemaVersion = p.SchemaVersion,
    };

    public static Project ToDomain(ProjectRecord r) => new(
        ProjectId.Parse(Guid.Parse(r.Id)),
        r.Name,
        r.HeadRevisionId is null ? null : RevisionId.Parse(Guid.Parse(r.HeadRevisionId)),
        r.SchemaVersion);

    public static FileRecord ToRecord(ProjectFile f) => new()
    {
        Id = f.Id.ToString(),
        RelativePath = f.RelativePath,
        MediaKind = f.MediaKind,
        ContentHash = f.ContentHash.Hex,
        ByteLength = f.ByteLength,
        HasBom = f.HasBom,
    };

    public static ProjectFile ToDomain(FileRecord r) => new(
        FileId.Parse(Guid.Parse(r.Id)),
        r.RelativePath,
        r.MediaKind,
        new SHA256(r.ContentHash),
        r.ByteLength,
        r.HasBom);

    public static RevisionRecord ToRecord(ProjectRevision r) => new()
    {
        Id = r.Id.ToString(),
        ProjectId = r.ProjectId.ToString(),
        ParentRevisionId = r.ParentRevisionId?.ToString(),
        Files = r.Files.Select(ToRecord).ToList(),
        CreatedAt = r.CreatedAt.UtcDateTime.ToString("O"),
    };

    public static ProjectRevision ToDomain(RevisionRecord r)
    {
        var files = ImmutableArray.CreateRange(r.Files.Select(ToDomain));
        return new ProjectRevision(
            RevisionId.Parse(Guid.Parse(r.Id)),
            ProjectId.Parse(Guid.Parse(r.ProjectId)),
            r.ParentRevisionId is null ? null : RevisionId.Parse(Guid.Parse(r.ParentRevisionId)),
            files,
            DateTimeOffset.Parse(r.CreatedAt, null, System.Globalization.DateTimeStyles.AssumeUniversal));
    }

    public static CheckpointRecord ToRecord(HistoryCheckpoint c) => new()
    {
        Id = c.Id.ToString(),
        ProjectId = c.ProjectId.ToString(),
        RevisionId = c.RevisionId.ToString(),
        Reason = c.Reason,
        CreatedAt = c.CreatedAt.UtcDateTime.ToString("O"),
    };

    public static HistoryCheckpoint ToDomain(CheckpointRecord r) => new(
        CheckpointId.Parse(Guid.Parse(r.Id)),
        ProjectId.Parse(Guid.Parse(r.ProjectId)),
        RevisionId.Parse(Guid.Parse(r.RevisionId)),
        r.Reason,
        DateTimeOffset.Parse(r.CreatedAt, null, System.Globalization.DateTimeStyles.AssumeUniversal));
}
