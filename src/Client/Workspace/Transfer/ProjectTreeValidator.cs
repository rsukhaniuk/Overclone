using System.Globalization;

namespace Client.Workspace.Transfer;

/// <summary>
/// Pure, shared validator for a project tree (manifest): validates every raw
/// relative path with the single-path rules (<see cref="ProjectPathValidator"/>),
/// then detects NFC + ordinal case-insensitive duplicate paths and
/// file/directory prefix conflicts (a file whose path is an ancestor directory
/// of another file). Used by the coordinator (working tree) after restore
/// reconcile and by the repository (manifest commit boundary) so a save/restore
/// that would commit an unsafe or colliding manifest is rejected before any
/// storage work — never silently overwriting, dropping, renaming or repairing a
/// path. This validator owns the tree-level (duplicate/prefix) policy and reuses
/// <see cref="ProjectPathValidator"/> for the per-path (length/depth/control/
/// forbidden-segment/traversal/absolute/backslash/empty) rules without duplicating them.
/// </summary>
public static class ProjectTreeValidator
{
    /// <summary>Detect single-path validation failures, duplicate paths
    /// (NFC + ordinal case-insensitive) and file/directory prefix conflicts (a
    /// file whose path is an ancestor directory of another file) among
    /// <paramref name="paths"/>. Returns a list of conflict descriptions, or
    /// null when the tree is conflict-free. Paths are validated raw — they are
    /// NOT trimmed, stripped of leading slashes, or backslash-rewritten; an
    /// invalid raw path is reported, never silently repaired. An empty
    /// collection is valid; an empty path ENTRY (or any invalid entry) rejects
    /// the entire manifest.</summary>
    public static List<string>? FindPathConflicts(IEnumerable<string> paths)
    {
        // Materialize once: the input may be a deferred LINQ chain that would
        // otherwise be enumerated repeatedly.
        var list = paths as List<string> ?? paths.ToList();
        if (list.Count == 0) return null;
        var conflicts = new List<string>();

        // One validation/keying pass. A path may enter the duplicate map AND the
        // prefix iteration ONLY if it passes the shared single-path rules AND its
        // NFC comparison key computes successfully. Any other path is reported
        // with a structured validation error and skipped from later processing —
        // it is never silently repaired or normalized, and it never reaches an
        // unguarded ComparisonKey call (which previously threw on malformed
        // Unicode in a directory segment).
        var byKey = new Dictionary<string, string>();
        var accepted = new List<string>();
        foreach (var path in list)
        {
            if (ProjectPathValidator.Validate(path) is { } e)
            {
                conflicts.Add($"Invalid path '{path}': {ErrorText(e)}");
                continue;
            }

            string key;
            try { key = ProjectPathValidator.ComparisonKey(path); }
            catch (ArgumentException ex)
            {
                // Malformed Unicode (e.g. an unpaired high/low surrogate in a
                // leaf or directory segment) cannot be NFC-normalized. Report a
                // structured error rather than letting the exception escape.
                conflicts.Add(
                    $"Invalid path '{path}': Unicode normalization failed ({ex.GetType().Name})");
                continue;
            }

            if (byKey.TryGetValue(key, out var other))
                conflicts.Add(
                    $"Duplicate path '{path}' (case/NFC-insensitive collision with '{other}')");
            else
                byKey[key] = path;
            accepted.Add(path);
        }

        // file/directory prefix conflict — only among accepted (valid, keyed)
        // paths. Prefixes are sub-paths of an accepted path, so their comparison
        // keys are safe to compute (a path whose key failed was skipped above and
        // never reaches here).
        foreach (var path in accepted)
        {
            var parts = path.Split('/');
            var prefix = "";
            for (int i = 0; i < parts.Length - 1; i++)
            {
                prefix = i == 0 ? parts[i] : prefix + "/" + parts[i];
                if (byKey.ContainsKey(ProjectPathValidator.ComparisonKey(prefix)))
                {
                    conflicts.Add(
                        $"Path '{path}' is under '{prefix}', which is a file (file/directory prefix conflict)");
                    break;
                }
            }
        }

        return conflicts.Count == 0 ? null : conflicts;
    }

    private static string ErrorText(PathError e) => e switch
    {
        PathError.EmptyPath => "empty path",
        PathError.PathTooLong => "path exceeds the byte-length limit",
        PathError.DepthExceeded => "path exceeds the depth limit",
        PathError.EmptySegment => "empty path segment (absolute or double slash)",
        PathError.TraversalSegment => "traversal segment ('.' or '..')",
        PathError.SegmentTooLong => "segment exceeds the byte-length limit",
        PathError.ControlCharacter => "control character in path",
        PathError.ForbiddenChar => "forbidden character in path (backslash, colon, …)",
        PathError.TrailingDotOrSpace => "trailing dot or space in segment",
        PathError.DeviceName => "Windows device name in segment",
        PathError.ForbiddenSegment => "forbidden source-control/credential segment",
        _ => "invalid path",
    };
}
