namespace Client.Workspace.Domain;

// Opaque unique identifiers. Revision IDs are NOT timestamps or per-file
// versions; a new revision is created when any file content, the file set or
// project settings change.
public readonly record struct ProjectId(Guid Value)
{
    public static ProjectId New() => new(Guid.NewGuid());
    public static ProjectId Parse(Guid value) => new(value);
    public override string ToString() => Value.ToString("N");
}

public readonly record struct FileId(Guid Value)
{
    public static FileId New() => new(Guid.NewGuid());
    public static FileId Parse(Guid value) => new(value);
    public override string ToString() => Value.ToString("N");
}

public readonly record struct RevisionId(Guid Value)
{
    public static RevisionId New() => new(Guid.NewGuid());
    public static RevisionId Parse(Guid value) => new(value);
    public override string ToString() => Value.ToString("N");
}

public readonly record struct CheckpointId(Guid Value)
{
    public static CheckpointId New() => new(Guid.NewGuid());
    public static CheckpointId Parse(Guid value) => new(value);
    public override string ToString() => Value.ToString("N");
}

/// <summary>Opaque unique id for an in-memory analysis snapshot (M2a). It is
/// C# orchestration metadata only — NOT a competing Rust revision id and NOT an
/// extra wire requirement. Generated at capture so a stale capture can be
/// discarded even when restore/recovery replaced contents without changing
/// <see cref="WorkspaceCoordinator.WorkingGeneration"/> or session.</summary>
public readonly record struct AnalysisSnapshotId(Guid Value)
{
    public static AnalysisSnapshotId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// Lowercased hex SHA-256 of exact UTF-8 bytes. Equality is case-sensitive on
/// the normalized (lowercase) form.
/// </summary>
public readonly record struct SHA256(string Hex)
{
    public static SHA256 FromBytes(byte[] hash)
        => new(Convert.ToHexString(hash).ToLowerInvariant());

    public override string ToString() => Hex;
}
