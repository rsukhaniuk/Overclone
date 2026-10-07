using System.Collections.Immutable;
using Client.Workspace.Domain;

namespace Client.Workspace.Transfer;

/// <summary>
/// Shared builder for a single staged file, enforcing the same path/size/content
/// policy used by the ZIP and folder adapters and by workspace file commands.
/// Returns a typed error code on failure, or a <see cref="StagedFile"/> on
/// success. Duplicate/prefix-conflict tracking uses NFC + ordinal
/// case-insensitive comparison keys.
/// </summary>
public static class StagedImportBuilder
{
    public record PathError(string Code, string Message);

    public static PathError? BuildFile(
        string rawPath,
        byte[] bytes,
        HashSet<string> filePathKeys,
        HashSet<string> directoryKeys,
        out StagedFile? staged)
    {
        staged = null;
        var normalized = ProjectPathValidator.Normalize(rawPath);
        if (normalized is null)
            return new PathError("InvalidPath", "Path is empty.");
        if (normalized.EndsWith('/'))
            return new PathError("InvalidPath", "File path ends with a slash.");

        var err = ProjectPathValidator.Validate(normalized);
        if (err is { } ve)
            return new PathError(ve.ToString(), "Path is invalid.");

        if (bytes.LongLength > ArchiveLimits.SingleFile)
            return new PathError("FileTooLarge", "File exceeds the single-file limit.");

        if (ProjectPathValidator.FindConflict(normalized, isDirectory: false, filePathKeys, directoryKeys) is { } dup)
            return new PathError(dup.IsDuplicate ? "DuplicatePath" : "PrefixConflict",
                $"Path conflicts with an existing entry: {dup.Path}");

        var (mediaKind, hasBom) = Classify(bytes, normalized);
        var hash = SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes));
        filePathKeys.Add(ProjectPathValidator.ComparisonKey(normalized));
        staged = new StagedFile(normalized, mediaKind, hasBom, bytes, hash, bytes.Length);
        return null;
    }

    private static (string MediaKind, bool HasBom) Classify(byte[] bytes, string normalizedPath)
    {
        if (ProjectPathValidator.MediaKindForPath(normalizedPath) != "text")
            return ("binary", false);
        if (!TextContent.TryDecode(bytes, out _, out var hasBom))
            return ("binary", false); // invalid UTF-8 → binary asset
        return ("text", hasBom);
    }
}
