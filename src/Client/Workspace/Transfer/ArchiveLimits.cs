namespace Client.Workspace.Transfer;

/// <summary>
/// Named implementation limits for portable project interchange. These are
/// engineering guards against unbounded memory/expansion, not institutional
/// document rules. Shared by the ZIP and folder adapters and by workspace file
/// commands via <see cref="ProjectPathValidator"/>.
/// </summary>
public static class ArchiveLimits
{
    /// <summary>Maximum total size of a ZIP input stream before expansion.</summary>
    public const long ZipInputSize = 64L * 1024 * 1024;

    /// <summary>Maximum expanded size of an imported project (ZIP or folder).</summary>
    public const long ExpandedSize = 128L * 1024 * 1024;

    /// <summary>Maximum size of a single file.</summary>
    public const long SingleFile = 32L * 1024 * 1024;

    /// <summary>Maximum number of entries (files + directory declarations),
    /// including directories, in an archive or folder traversal.</summary>
    public const int MaxEntries = 10_000;

    /// <summary>Maximum relative path depth (number of segments).</summary>
    public const int MaxDepth = 32;

    /// <summary>Maximum relative path length in UTF-8 bytes.</summary>
    public const int MaxPathBytes = 1_024;

    /// <summary>Maximum individual segment length in UTF-8 bytes.</summary>
    public const int MaxSegmentBytes = 255;

    /// <summary>Maximum detailed diff content per file (bytes) before a summary view.</summary>
    public const long DiffByteBudget = 1L * 1024 * 1024;

    /// <summary>Maximum detailed diff line count per file before a summary view.</summary>
    public const int DiffLineBudget = 20_000;

    /// <summary>History page size (checkpoints per page, newest first).</summary>
    public const int HistoryPageSize = 50;
}
