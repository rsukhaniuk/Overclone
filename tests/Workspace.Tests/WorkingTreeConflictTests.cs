using Client.Workspace;
using Client.Workspace.Transfer;
using Xunit;

namespace Workspace.Tests;

/// <summary>
/// Validates the working-tree path-conflict detector used after restore reconcile
/// and at the save commit boundary: duplicate paths (NFC + ordinal case-insensitive) and
/// file/directory prefix conflicts must be surfaced, never silently overwritten/dropped.
/// </summary>
public class WorkingTreeConflictTests
{
    private static List<string>? Conflicts(params string[] paths)
        => ProjectTreeValidator.FindPathConflicts(paths);

    [Fact]
    public void NoConflicts_ForDistinct_Paths()
    {
        Assert.Null(Conflicts("a.tex", "b.tex", "dir/c.tex"));
    }

    [Fact]
    public void Duplicate_Exact_Path_Detected()
    {
        var c = Conflicts("a.tex", "a.tex");
        Assert.NotNull(c);
        Assert.Contains("Duplicate path", c![0]);
    }

    [Fact]
    public void Duplicate_Case_And_NFC_Collision_Detected()
    {
        // MAIN.tex / main.tex collide; café.tex / café.tex (decomposed) collide.
        var c = Conflicts("MAIN.tex", "main.tex", "café.tex", "caf" + "e\u0301" + ".tex");
        Assert.NotNull(c);
        Assert.Equal(2, c!.Count);
    }

    [Fact]
    public void File_Directory_Prefix_Conflict_Detected()
    {
        // A file "a" cannot also be the directory ancestor of "a/b".
        var c = Conflicts("a", "a/b.tex");
        Assert.NotNull(c);
        Assert.Contains("prefix conflict", c!.Last());
    }

    [Fact]
    public void Distinct_Files_Under_Nonconflicting_Ancestor_Are_Fine()
    {
        // "chapters/intro.tex" and "chapters/refs.tex" share ancestor "chapters", which is
        // NOT itself a file in the tree — no conflict.
        Assert.Null(Conflicts("chapters/intro.tex", "chapters/refs.tex"));
    }

    [Fact]
    public void File_Under_Existing_File_Is_Conflict()
    {
        // If "chapters" is itself a FILE, "chapters/intro.tex" under it is a conflict.
        var c = Conflicts("chapters", "chapters/intro.tex");
        Assert.NotNull(c);
        Assert.Contains("prefix conflict", c!.Last());
    }
}
