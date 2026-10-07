using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Client.Workspace.Transfer;

/// <summary>
/// One shared validator for project-relative paths, used by the ZIP and folder
/// adapters and by workspace file commands. It rejects unsafe paths before any
/// project record is created, never silently strips/renames, and uses NFC +
/// ordinal case-insensitive comparison for duplicate/prefix-conflict detection
/// while preserving the original accepted spelling.
/// </summary>
public static partial class ProjectPathValidator
{
    /// <summary>Case-insensitive editable text extensions, without the dot.</summary>
    public static readonly HashSet<string> EditableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "tex", "bib", "cls", "sty", "bst", "txt", "md", "json", "yaml", "yml", "csv",
    };

    /// <summary>Case-insensitive source-control/credential trees excluded from interchange.</summary>
    public static readonly HashSet<string> ForbiddenSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", ".ssh", ".aws",
    };

    // Windows-forbidden filename characters (backslash is rejected here too:
    // project-relative interchange uses forward slashes, per the ZIP standard).
    private const string ForbiddenChars = "<>:\"|?*\\";
    // Windows device basenames (with or without extension) — case-insensitive.
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Matches control characters (C0 + C1 + DEL + other control).
    [GeneratedRegex(@"[\x00-\x1F\x7F-\x9F]")]
    private static partial Regex ControlCharsRegex();

    /// <summary>Normalize a raw user/archive path. Only surrounding
    /// whitespace is trimmed; leading/trailing slashes and backslashes are
    /// NOT silently stripped (absolute/backslash paths are rejected by
    /// <see cref="Validate"/>), and backslashes are NOT converted. Returns
    /// null if the path is empty/whitespace.</summary>
    public static string? Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim();
        return p.Length == 0 ? null : p;
    }

    /// <summary>Validate a single project-relative path. Returns null on success
    /// or a typed error code on failure. <paramref name="normalized"/> must
    /// already be the result of <see cref="Normalize"/> (no leading/trailing
    /// slash, forward slashes).</summary>
    public static PathError? Validate(string normalized)
    {
        if (string.IsNullOrEmpty(normalized)) return PathError.EmptyPath;

        int pathBytes = Encoding.UTF8.GetByteCount(normalized);
        if (pathBytes > ArchiveLimits.MaxPathBytes) return PathError.PathTooLong;

        var segments = normalized.Split('/');
        if (segments.Length > ArchiveLimits.MaxDepth) return PathError.DepthExceeded;

        for (int i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            var err = ValidateSegment(seg);
            if (err is { } e) return e;
            if (ForbiddenSegments.Contains(seg)) return PathError.ForbiddenSegment;
        }
        return null;
    }

    private static PathError? ValidateSegment(string segment)
    {
        if (segment.Length == 0) return PathError.EmptySegment;
        if (segment == "." || segment == "..") return PathError.TraversalSegment;

        int segBytes = Encoding.UTF8.GetByteCount(segment);
        if (segBytes > ArchiveLimits.MaxSegmentBytes) return PathError.SegmentTooLong;

        if (ControlCharsRegex().IsMatch(segment)) return PathError.ControlCharacter;
        if (segment.IndexOfAny(ForbiddenChars.ToCharArray()) >= 0)
            return PathError.ForbiddenChar;

        // Trailing dot or space in a segment is forbidden on Windows.
        if (segment.EndsWith('.') || segment.EndsWith(' '))
            return PathError.TrailingDotOrSpace;

        var basename = segment;
        int dot = basename.IndexOf('.');
        var stem = dot < 0 ? basename : basename.Substring(0, dot);
        if (DeviceNames.Contains(stem) || DeviceNames.Contains(basename))
            return PathError.DeviceName;

        return null;
    }

    /// <summary>NFC-normalize a path for comparison purposes. NFC is applied to
    /// the whole string; comparison is ordinal case-insensitive (ToUpperInvariant
    /// using the invariant culture, which folds case for the Latin/Greek/Cyrillic
    /// ranges that matter for paths without locale surprises).</summary>
    public static string ComparisonKey(string normalizedPath)
    {
        var nfc = normalizedPath.Normalize(NormalizationForm.FormC);
        return nfc.ToUpperInvariant();
    }

    /// <summary>Detect duplicate paths or file/directory-prefix conflicts using
    /// NFC + ordinal case-insensitive matching. <paramref name="filePathKeys"/>
    /// holds comparison keys for accepted files; <paramref name="directoryKeys"/>
    /// holds directory keys WITH a trailing slash. Returns a conflict with
    /// <see cref="PathConflict.IsDuplicate"/> true for an exact name
    /// collision and false for an ancestor/prefix conflict, or null.</summary>
    public static PathConflict? FindConflict(
        string normalizedPath, bool isDirectory,
        HashSet<string> filePathKeys, HashSet<string> directoryKeys)
    {
        var key = ComparisonKey(normalizedPath);
        var dirKey = key + "/";

        if (isDirectory)
        {
            // Duplicate directory, or a file with the same name as this dir.
            if (directoryKeys.Contains(dirKey)) return new PathConflict(normalizedPath, true);
            if (filePathKeys.Contains(key)) return new PathConflict(normalizedPath, true);
            // An ancestor segment is a file → cannot create a dir under a file.
            if (AncestorIsFile(normalizedPath, filePathKeys)) return new PathConflict(normalizedPath, false);
            // (Files living under this directory are fine — a directory is
            // expected to contain files. Declaring "chapters/" AFTER
            // "chapters/intro.tex" is not a conflict.)
        }
        else
        {
            // Duplicate file, or a directory with the same name as this file.
            if (filePathKeys.Contains(key)) return new PathConflict(normalizedPath, true);
            if (directoryKeys.Contains(dirKey)) return new PathConflict(normalizedPath, true);
            // An ancestor segment is a file → cannot create a file under a file.
            if (AncestorIsFile(normalizedPath, filePathKeys)) return new PathConflict(normalizedPath, false);
            // This file would be an ancestor (directory) of an already-accepted
            // file → cannot turn a directory path into a file.
            foreach (var fk in filePathKeys)
                if (fk.StartsWith(dirKey, StringComparison.Ordinal)) return new PathConflict(normalizedPath, false);
            // (A file under an existing directory is allowed, not a conflict.)
        }
        return null;
    }

    /// <summary>True if any ancestor segment of <paramref name="normalizedPath"/>
    /// is present as a file (a file blocks creating any path through it).</summary>
    private static bool AncestorIsFile(string normalizedPath, HashSet<string> filePathKeys)
    {
        var parts = normalizedPath.Split('/');
        var prefix = "";
        for (int i = 0; i < parts.Length - 1; i++)
        {
            prefix = i == 0 ? parts[i] : prefix + "/" + parts[i];
            if (filePathKeys.Contains(ComparisonKey(prefix))) return true;
        }
        return false;
    }

    /// <summary>Classify a path's media kind by extension. Editable extensions
    /// are <c>text</c>; everything else is <c>binary</c>.</summary>
    public static string MediaKindForPath(string normalizedPath)
    {
        var ext = Path.GetExtension(normalizedPath);
        if (ext.Length > 1 && EditableExtensions.Contains(ext.Substring(1)))
            return "text";
        return "binary";
    }
}

/// <summary>A path conflict: the conflicting path and whether it is an
/// exact name collision (duplicate) or an ancestor/prefix conflict.</summary>
public readonly record struct PathConflict(string Path, bool IsDuplicate);

/// <summary>Typed path-validation error codes.</summary>
public enum PathError
{
    EmptyPath,
    PathTooLong,
    DepthExceeded,
    EmptySegment,
    TraversalSegment,
    SegmentTooLong,
    ControlCharacter,
    ForbiddenChar,
    TrailingDotOrSpace,
    DeviceName,
    ForbiddenSegment,
}
