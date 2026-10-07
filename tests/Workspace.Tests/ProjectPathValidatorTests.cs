using Client.Workspace.Transfer;
using Xunit;

namespace Workspace.Tests;

/// <summary>
/// Path validation policy shared by ZIP, folder and workspace file commands.
/// Covers unsafe paths, size/depth limits, forbidden segments, device names,
/// NFC + ordinal case-insensitive duplicate/prefix-conflict detection.
/// </summary>
public class ProjectPathValidatorTests
{
    [Theory]
    [InlineData("main.tex")]
    [InlineData("chapters/intro.tex")]
    [InlineData("a/b/c/d.tex")]
    [InlineData(".gitignore")]            // ordinary dotfile stays valid
    [InlineData("Україна/розділ.tex")]    // Cyrillic paths
    [InlineData("emoji/🎉.tex")]
    public void Valid_Paths_Accepted(string path)
    {
        var n = ProjectPathValidator.Normalize(path);
        Assert.NotNull(n);
        Assert.Null(ProjectPathValidator.Validate(n!));
    }

    [Theory]
    [InlineData("../escape.tex", PathError.TraversalSegment)]
    [InlineData("a/../../b.tex", PathError.TraversalSegment)]
    [InlineData("a/./b.tex", PathError.TraversalSegment)]
    [InlineData("a//b.tex", PathError.EmptySegment)]
    [InlineData("/abs.tex", PathError.EmptySegment)]           // leading slash → empty first segment (absolute rejected)
    [InlineData("a\\b.tex", PathError.ForbiddenChar)]          // backslash rejected (no silent conversion)
    [InlineData("C:\\Users\\x.tex", PathError.ForbiddenChar)]   // drive + backslash rejected
    [InlineData("CON.tex", PathError.DeviceName)]
    [InlineData("PRN", PathError.DeviceName)]
    [InlineData("COM1", PathError.DeviceName)]
    [InlineData("LPT9.log", PathError.DeviceName)]
    [InlineData("a/b/CON/c.tex", PathError.DeviceName)]
    [InlineData("a:b.tex", PathError.ForbiddenChar)]
    [InlineData("a<b.tex", PathError.ForbiddenChar)]
    [InlineData("a\"b.tex", PathError.ForbiddenChar)]
    [InlineData("a|b.tex", PathError.ForbiddenChar)]
    [InlineData("a?b.tex", PathError.ForbiddenChar)]
    [InlineData("a*b.tex", PathError.ForbiddenChar)]
    [InlineData("a./b.tex", PathError.TrailingDotOrSpace)]     // segment "a." trailing dot
    [InlineData("a /b.tex", PathError.TrailingDotOrSpace)]    // segment "a " trailing space
    [InlineData("a/\x01control.tex", PathError.ControlCharacter)]
    [InlineData(".git/config", PathError.ForbiddenSegment)]
    [InlineData(".svn/entries", PathError.ForbiddenSegment)]
    [InlineData(".ssh/id_rsa", PathError.ForbiddenSegment)]
    [InlineData(".aws/credentials", PathError.ForbiddenSegment)]
    public void Invalid_Paths_Rejected(string path, PathError expected)
    {
        var n = ProjectPathValidator.Normalize(path);
        if (n is null)
        {
            Assert.Equal(PathError.EmptyPath, expected);
            return;
        }
        var err = ProjectPathValidator.Validate(n);
        Assert.Equal(expected, err);
    }

    [Fact]
    public void Depth_Limit_Exceeded()
    {
        var seg = new string('a', 3);
        var path = string.Join('/', Enumerable.Range(0, ArchiveLimits.MaxDepth + 1).Select(_ => seg)) + "/x.tex";
        var n = ProjectPathValidator.Normalize(path);
        Assert.Equal(PathError.DepthExceeded, ProjectPathValidator.Validate(n!));
    }

    [Fact]
    public void Segment_TooLong_Rejected()
    {
        var seg = new string('a', ArchiveLimits.MaxSegmentBytes + 1);
        var n = ProjectPathValidator.Normalize(seg + ".tex");
        Assert.Equal(PathError.SegmentTooLong, ProjectPathValidator.Validate(n!));
    }

    [Fact]
    public void Path_TooLong_Rejected()
    {
        // 31 segments of 33 bytes + a final file segment: each segment ≤ 255,
        // depth 32 (ok), but total path bytes > 1024 → PathTooLong.
        var seg = new string('a', 33);
        var path = string.Join('/', Enumerable.Range(0, 31).Select(_ => seg)) + "/x.tex";
        var n = ProjectPathValidator.Normalize(path);
        Assert.Equal(PathError.PathTooLong, ProjectPathValidator.Validate(n!));
    }

    [Fact]
    public void ComparisonKey_Is_NFC_CaseInsensitive()
    {
        // NFC-normalized + ordinal-ci: e\u0301 (decomposed) vs é (composed) collide.
        var decomposed = "caf" + "e\u0301" + ".tex";
        var composed = "café.tex";
        Assert.Equal(ProjectPathValidator.ComparisonKey(decomposed),
                      ProjectPathValidator.ComparisonKey(composed));
        // Case-insensitive.
        Assert.Equal(ProjectPathValidator.ComparisonKey("MAIN.tex"),
                      ProjectPathValidator.ComparisonKey("main.tex"));
    }

    [Fact]
    public void FindConflict_Detects_Duplicate_Case_And_NFC()
    {
        var files = new HashSet<string> { ProjectPathValidator.ComparisonKey("MAIN.tex") };
        var dirs = new HashSet<string>();
        // lowercase + same path → conflict
        Assert.NotNull(ProjectPathValidator.FindConflict("main.tex", false, files, dirs));
        // NFC collision
        var existing2 = new HashSet<string> { ProjectPathValidator.ComparisonKey("café.tex") };
        Assert.NotNull(ProjectPathValidator.FindConflict(
            "caf" + "e\u0301" + ".tex", false, existing2, dirs));
    }

    [Fact]
    public void FindConflict_Detects_Directory_Prefix_Conflicts()
    {
        var files = new HashSet<string>();
        var dirs = new HashSet<string>();
        // A file named "a" then a directory "a": file/dir same name → conflict.
        files.Add(ProjectPathValidator.ComparisonKey("a"));
        Assert.NotNull(ProjectPathValidator.FindConflict("a", true, files, dirs));
        // A directory "b" then a file "b": conflict (file == dir name).
        var files2 = new HashSet<string>();
        var dirs2 = new HashSet<string> { ProjectPathValidator.ComparisonKey("b") + "/" };
        Assert.NotNull(ProjectPathValidator.FindConflict("b", false, files2, dirs2));
        // A file "c" is an ancestor of a new file "c/d.tex": conflict.
        var files3 = new HashSet<string> { ProjectPathValidator.ComparisonKey("c") };
        Assert.NotNull(ProjectPathValidator.FindConflict("c/d.tex", false, files3, new HashSet<string>()));
        // A file under a new directory is NOT a conflict (a directory contains
        // files; declaring "d/" after "d/x.tex" is valid, order-independent).
        var files4 = new HashSet<string> { ProjectPathValidator.ComparisonKey("d/x.tex") };
        Assert.Null(ProjectPathValidator.FindConflict("d", true, files4, new HashSet<string>()));
    }

    [Fact]
    public void No_Conflict_For_Distinct_Paths_And_Files_Under_Dirs()
    {
        var files = new HashSet<string> { ProjectPathValidator.ComparisonKey("a.tex") };
        var dirs = new HashSet<string> { ProjectPathValidator.ComparisonKey("b") + "/" };
        Assert.Null(ProjectPathValidator.FindConflict("b.tex", false, files, dirs));
        Assert.Null(ProjectPathValidator.FindConflict("b/c.tex", false, files, dirs)); // file under a dir is fine
        Assert.Null(ProjectPathValidator.FindConflict("a/b.tex", false, files, dirs));   // "a.tex" is not an ancestor of "a/b.tex"
    }

    [Theory]
    [InlineData("main.tex", "text")]
    [InlineData("image.PNG", "binary")]
    [InlineData("doc.pdf", "binary")]
    [InlineData("refs.bib", "text")]
    [InlineData("style.cls", "text")]
    [InlineData("data.csv", "text")]
    [InlineData("noext", "binary")]
    public void MediaKindForPath_Classifies(string path, string expected)
        => Assert.Equal(expected, ProjectPathValidator.MediaKindForPath(path));
}
