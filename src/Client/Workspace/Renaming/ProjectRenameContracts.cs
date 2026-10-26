using System.Buffers;
using System.Globalization;
using System.Text;
using Client.Workspace.Domain;

namespace Client.Workspace.Renaming;

public sealed record ProjectRenameSnapshot(ProjectId Id, string Name, RevisionId? Head, int SchemaVersion)
{
    public static ProjectRenameSnapshot Capture(Project project) => new(project.Id, project.Name, project.HeadRevisionId, project.SchemaVersion);
}
public enum ProjectRenameCode { Applied, Unchanged, InvalidName, Unavailable, Unsupported, WriterUnavailable, Stale, Missing, Cancelled, Failed }
public sealed record ProjectRenameResult(ProjectRenameCode Code, bool WriterReleaseFailed = false)
{
    public bool Succeeded => Code is ProjectRenameCode.Applied or ProjectRenameCode.Unchanged;
}

/// <summary>Rename-only policy. Does not retroactively constrain creation/opening
/// existing projects; names are display metadata, never filesystem identities.</summary>
public static class ProjectRenameName
{
    public const int MaxScalars = 256;
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = "";
        if (value is null) return false;
        var raw = value.AsSpan();
        // Validate BEFORE trimming: leading/trailing controls are not laundered.
        for (int at = 0; at < raw.Length;)
        {
            if (Rune.DecodeFromUtf16(raw[at..], out var rune, out int used) != OperationStatus.Done) return false;
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) return false;
            at += used;
        }
        var trimmed = raw.Trim();
        if (trimmed.IsEmpty) return false;
        int count = 0;
        foreach (var rune in trimmed.EnumerateRunes())
        {
            _ = rune;
            if (++count > MaxScalars) return false;
        }
        normalized = trimmed.ToString(); return true;
    }
}
