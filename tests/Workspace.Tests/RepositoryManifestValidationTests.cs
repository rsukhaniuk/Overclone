using System.Collections.Immutable;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Storage;
using Client.Workspace.Transfer;
using Microsoft.JSInterop;
using Xunit;

namespace Workspace.Tests;

/// <summary>
/// Persistence invariant: the real repository (<see cref="WorkspaceStorage"/>)
/// validates <em>every</em> manifest it is asked to commit — selected head and
/// additional manifests — with the shared single-path rules (traversal, absolute,
/// forbidden segment, backslash, empty, control character, malformed Unicode,
/// length/depth) AND the tree-level rules (duplicate NFC/ordinal-case-insensitive
/// paths and file/directory prefix conflicts) BEFORE any storage work. An invalid
/// manifest must return a structured failure and must never reach JS interop, content
/// writes, checkpoint writes or a head change — it must never throw. The repository
/// does not trust the caller to have validated input. A fake <see cref="IJSRuntime"/>
/// that fails the test if invoked proves no interop ran.
/// </summary>
public class RepositoryManifestValidationTests
{
    /// <summary>An IJSRuntime that records every call and throws, so any reach of
    /// interop fails the calling test. The validation must return before this is
    /// ever consulted.</summary>
    private sealed class ThrowingJSRuntime : IJSRuntime
    {
        public int Invocations { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Invocations++;
            throw new InvalidOperationException(
                $"JS interop reached for invalid manifest: {identifier}");
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Invocations++;
            throw new InvalidOperationException(
                $"JS interop reached for invalid manifest: {identifier}");
        }
    }

    private static readonly ProjectId Pid = ProjectId.New();
    private static readonly SHA256 Hash =
        SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData("x"u8.ToArray()));

    private static ProjectRevision Revision(params string[] paths)
    {
        var files = paths.Select(p => new ProjectFile(
            FileId.New(), p, "text", Hash, 1, false)).ToImmutableArray();
        return new ProjectRevision(RevisionId.New(), Pid, null, files, DateTimeOffset.UtcNow);
    }

    private static StagedProject Stage(params string[] paths)
    {
        var files = paths.Select(p => new StagedFile(
            p, "text", false, "x"u8.ToArray(), Hash, 1)).ToImmutableArray();
        return new StagedProject("p", files, Array.Empty<ValidationError>());
    }

    private static WorkspaceStorage NewRepo(out ThrowingJSRuntime js)
    {
        js = new ThrowingJSRuntime();
        return new WorkspaceStorage(js);
    }

    // `label` is used meaningfully in every assertion's failure message below
    // (no xUnit1026 unused-parameter warning, no analyzer suppression, no dummy use).

    private static void AssertInvalidManifest(CommitResult r, ThrowingJSRuntime js, string label)
    {
        Assert.True(r.Status == CommitStatus.StorageFailure,
            $"[{label}] expected CommitStatus.StorageFailure, got {r.Status}");
        Assert.True(r.ErrorCode == "InvalidManifest",
            $"[{label}] expected ErrorCode InvalidManifest, got {r.ErrorCode}");
        Assert.True(js.Invocations == 0,
            $"[{label}] expected zero interop invocations, got {js.Invocations}");
    }

    private static void AssertInvalidImport(ImportPublishResult r, ThrowingJSRuntime js, string label)
    {
        Assert.True(!r.Published,
            $"[{label}] expected Published=false, got {r.Published}");
        Assert.True(r.ErrorCode == "InvalidManifest",
            $"[{label}] expected ErrorCode InvalidManifest, got {r.ErrorCode}");
        Assert.True(js.Invocations == 0,
            $"[{label}] expected zero interop invocations, got {js.Invocations}");
    }

    private static void AssertInvalidPure(List<string>? c, string label)
    {
        Assert.True(c is not null,
            $"[{label}] expected a non-null conflict list, got null");
        Assert.True(c!.Any(x => x.Contains("Invalid path")),
            $"[{label}] expected an 'Invalid path' entry, got: {string.Join(" | ", c)}");
    }

    /// <summary>Invalid single paths that must be rejected by the shared
    /// single-path validator at the repository boundary. Each is paired with a
    /// short label used in assertion diagnostics. (Malformed-Unicode surrogates
    /// are covered separately — see <see cref="AllBoundariesRejectMalformedUnicodeSurrogates"/>.)</summary>
    public static IEnumerable<object[]> InvalidPaths()
    {
        yield return new object[] { "../escape.tex", "traversal" };
        yield return new object[] { "/absolute.tex", "absolute" };
        yield return new object[] { ".ssh/key", "forbidden segment" };
        yield return new object[] { "a\\b.tex", "backslash" };
        yield return new object[] { "", "empty path" };
        yield return new object[] { "escape\u0001.tex", "control character" };
        yield return new object[] { new string('a', ArchiveLimits.MaxPathBytes + 1), "path too long" };
    }

    // ---- single-path validation at every public repository boundary ----
    // Each manifest carries the invalid path AND a valid sibling, so the
    // invalid entry is rejected (never repaired) and rejects the whole manifest
    // regardless of a present valid sibling. Every boundary must return
    // InvalidManifest, never throw, never invoke IJSRuntime.

    [Theory]
    [MemberData(nameof(InvalidPaths))]
    public async Task CommitAsync_Rejects_Invalid_SelectedHead_Path(string path, string label)
    {
        var repo = NewRepo(out var js);
        var r = await repo.CommitAsync(ProjectCommit.Of(Revision(path, "valid.tex")), null, default);
        AssertInvalidManifest(r, js, label);
    }

    [Theory]
    [MemberData(nameof(InvalidPaths))]
    public async Task CommitAsync_Rejects_Invalid_Additional_Manifest_Path(string path, string label)
    {
        // The selected head is a valid two-file tree, but an additional
        // (ancestor) manifest carries an invalid path. The repository must
        // validate every manifest, not just the selected head.
        var repo = NewRepo(out var js);
        var batch = new ProjectCommit(
            Revision("only.tex", "other.tex"),
            ImmutableArray.Create(Revision(path, "valid.tex")),
            ImmutableArray<ContentInput>.Empty);
        var r = await repo.CommitAsync(batch, null, default);
        AssertInvalidManifest(r, js, label);
    }

    [Theory]
    [MemberData(nameof(InvalidPaths))]
    public async Task CreateRestoreRevisionAsync_Rejects_Invalid_Path(string path, string label)
    {
        var repo = NewRepo(out var js);
        var r = await repo.CreateRestoreRevisionAsync(
            Pid, RevisionId.New(), Revision(path, "valid.tex"), RevisionId.New(), "user", default);
        AssertInvalidManifest(r, js, label);
    }

    [Theory]
    [MemberData(nameof(InvalidPaths))]
    public async Task PublishImportAsync_Rejects_Invalid_Path(string path, string label)
    {
        var repo = NewRepo(out var js);
        var r = await repo.PublishImportAsync(Stage(path, "valid.tex"), default);
        AssertInvalidImport(r, js, label);
    }

    // ---- malformed Unicode (unpaired surrogates) at every public boundary ----
    // Lone surrogates cannot travel through xUnit Theory data (the runner
    // replaces them with U+FFFD before the test method sees them), so they are
    // constructed inline here. An unpaired high/low surrogate in a directory
    // segment or a leaf segment passes the per-path rules but fails NFC
    // normalization; every public boundary must return InvalidManifest, never
    // throw, never invoke IJSRuntime. Each case carries a valid sibling.

    [Fact]
    public async Task AllBoundariesRejectMalformedUnicodeSurrogates()
    {
        var surrogates = new (string Path, string Label)[]
        {
            ("\uD800/x.tex", "high surrogate directory"),
            ("x/\uD800.tex", "high surrogate leaf"),
            ("\uDC00/y.tex", "low surrogate directory"),
            ("z/\uDC00.tex", "low surrogate leaf"),
        };
        foreach (var (path, label) in surrogates)
        {
            // Pure shared validator: the surrogate is reported (not repaired),
            // the valid sibling is untouched and does not mask it.
            AssertInvalidPure(
                ProjectTreeValidator.FindPathConflicts(new[] { path, "other.tex" }),
                label + " (pure)");

            // CommitAsync — invalid selected head.
            var repo1 = NewRepo(out var js1);
            AssertInvalidManifest(
                await repo1.CommitAsync(ProjectCommit.Of(Revision(path, "valid.tex")), null, default),
                js1, label + " (commit selected head)");

            // CommitAsync — valid selected head, invalid additional (ancestor) manifest.
            var repo2 = NewRepo(out var js2);
            var batch = new ProjectCommit(
                Revision("only.tex", "other.tex"),
                ImmutableArray.Create(Revision(path, "valid.tex")),
                ImmutableArray<ContentInput>.Empty);
            AssertInvalidManifest(
                await repo2.CommitAsync(batch, null, default),
                js2, label + " (commit additional manifest)");

            // CreateRestoreRevisionAsync — invalid restored manifest.
            var repo3 = NewRepo(out var js3);
            AssertInvalidManifest(
                await repo3.CreateRestoreRevisionAsync(
                    Pid, RevisionId.New(), Revision(path, "valid.tex"), RevisionId.New(), "user", default),
                js3, label + " (restore)");

            // PublishImportAsync — invalid staged tree.
            var repo4 = NewRepo(out var js4);
            AssertInvalidImport(
                await repo4.PublishImportAsync(Stage(path, "valid.tex"), default),
                js4, label + " (import)");
        }
    }

    // ---- tree-level (duplicate/prefix) validation at the repository boundary ----

    [Fact]
    public async Task CommitAsync_Rejects_Duplicate_Path_Manifest_Without_Interop()
    {
        var repo = NewRepo(out var js);
        var r = await repo.CommitAsync(ProjectCommit.Of(Revision("a.tex", "a.tex")), null, default);
        AssertInvalidManifest(r, js, "duplicate exact");
    }

    [Fact]
    public async Task CommitAsync_Rejects_Case_And_NFC_Collision_Without_Interop()
    {
        var repo = NewRepo(out var js);
        // MAIN.tex / main.tex collide (ordinal case-insensitive); café (NFC) /
        // café (decomposed, NFD) collide after NFC normalization.
        var head = Revision("MAIN.tex", "main.tex", "caf\u00e9.tex", "cafe\u0301.tex");
        var r = await repo.CommitAsync(ProjectCommit.Of(head), null, default);
        AssertInvalidManifest(r, js, "case/NFC collision");
    }

    [Fact]
    public async Task CommitAsync_Rejects_Prefix_Conflict_Without_Interop()
    {
        var repo = NewRepo(out var js);
        // "a" is a file but also the directory ancestor of "a/b.tex".
        var r = await repo.CommitAsync(ProjectCommit.Of(Revision("a", "a/b.tex")), null, default);
        AssertInvalidManifest(r, js, "prefix conflict");
    }

    [Fact]
    public async Task CreateRestoreRevisionAsync_Rejects_Duplicate_Restored_Manifest()
    {
        var repo = NewRepo(out var js);
        var r = await repo.CreateRestoreRevisionAsync(
            Pid, RevisionId.New(), Revision("a.tex", "a.tex"), RevisionId.New(), "user", default);
        AssertInvalidManifest(r, js, "duplicate restore");
    }

    [Fact]
    public async Task PublishImportAsync_Rejects_Duplicate_Staged_Tree()
    {
        var repo = NewRepo(out var js);
        var r = await repo.PublishImportAsync(Stage("a.tex", "a.tex"), default);
        AssertInvalidImport(r, js, "duplicate import");
    }

    // ---- pure shared-validator checks (no repository) ----

    [Fact]
    public void PureValidator_Valid_Empty_Tree()
    {
        // A project with no files (valid empty project) must not be rejected.
        Assert.Null(ProjectTreeValidator.FindPathConflicts(Enumerable.Empty<string>()));
    }

    [Fact]
    public void PureValidator_Valid_Nested_And_Ukrainian_Unicode()
    {
        // Nested forward-slash paths and Cyrillic (Ukrainian) text are valid; NFC
        // + ordinal case-insensitive comparison must not reject them.
        Assert.Null(ProjectTreeValidator.FindPathConflicts(
            new[] { "main.tex", "chapters/вступ.tex", "chapters/тези.tex", "assets/фіґура.png" }));
    }

    [Fact]
    public void PureValidator_Rejects_Exact_Duplicate()
    {
        var c = ProjectTreeValidator.FindPathConflicts(new[] { "a.tex", "a.tex" });
        Assert.NotNull(c);
        Assert.Contains("Duplicate path", c![0]);
    }

    [Fact]
    public void PureValidator_Rejects_Case_Duplicate()
    {
        var c = ProjectTreeValidator.FindPathConflicts(new[] { "MAIN.tex", "main.tex" });
        Assert.NotNull(c);
        Assert.Contains("Duplicate path", c![0]);
    }

    [Fact]
    public void PureValidator_Rejects_NFC_Duplicate()
    {
        // café (NFC) and café (decomposed, NFD) collide after NFC normalization.
        var c = ProjectTreeValidator.FindPathConflicts(new[] { "caf\u00e9.tex", "cafe\u0301.tex" });
        Assert.NotNull(c);
        Assert.Contains("Duplicate path", c![0]);
    }

    [Fact]
    public void PureValidator_Rejects_Prefix_Conflict()
    {
        // "a" is a file but also the directory ancestor of "a/b.tex".
        var c = ProjectTreeValidator.FindPathConflicts(new[] { "a", "a/b.tex" });
        Assert.NotNull(c);
        Assert.Contains("prefix conflict", c![0]);
    }

    [Theory]
    [MemberData(nameof(InvalidPaths))]
    public void PureValidator_Rejects_Invalid_Single_Path(string path, string label)
    {
        // The invalid path is reported (not repaired); the valid sibling is
        // untouched and does not mask the invalid entry.
        AssertInvalidPure(ProjectTreeValidator.FindPathConflicts(new[] { path, "other.tex" }), label);
    }
}
