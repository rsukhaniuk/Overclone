using System.Collections.Immutable;
using Client.Workspace.Domain;
using Client.Workspace.Transfer;
using Xunit;

namespace Workspace.Tests.Drive;

public sealed class ArchiveExportTests
{
    [Fact]
    public async Task SharedBuilderRoundtrip_ExactManifestBytesAndReceiptCompatibility()
    {
        await using var fixture = await CompilationTestWorkspace.Open();
        var revision = fixture.Workspace.CurrentRevision!;
        var source = new ExportSource(revision, (hash, ct) => Task.FromResult(fixture.Repo.Content[hash].ToArray()));
        var transfer = new ZipTransferService(fixture.Repo);
        var archive = await transfer.BuildZipAsync(source, default);
        var stage = await transfer.StageZipImportAsync(archive.Bytes, "roundtrip", default);
        Assert.True(stage.Valid); Assert.Empty(stage.Staged.ValidationErrors);
        Assert.Equal(revision.Id, archive.CapturedRevision);
        Assert.Equal(revision.Files.Length, archive.FileCount);
        Assert.Equal(archive.Bytes.LongLength, archive.Receipt.ByteLength);
        foreach (var file in revision.Files)
        {
            var actual = Assert.Single(stage.Staged.Files, f => f.RelativePath == file.RelativePath);
            Assert.Equal(fixture.Repo.Content[file.ContentHash], actual.Bytes);
            Assert.Equal(file.ContentHash, actual.Hash);
            // The compilation fixture includes extensionless text. ZIP import
            // deliberately classifies such paths as binary; bytes still match.
            Assert.Equal(file.HasBom && ProjectPathValidator.MediaKindForPath(file.RelativePath) == "text", actual.HasBom);
        }
        // No test-only third ZIP writer: receipt delegates to the shared builder.
        var receipt = await transfer.ExportZipAsync(source, default);
        Assert.Equal(archive.Receipt, receipt);
        Assert.Equal(0, fixture.Repo.Publishes);
    }

    [Fact]
    public async Task ByteExportStaysOnCapturedRevisionAfterLaterDraftEdits()
    {
        await using var fixture = await CompilationTestWorkspace.Open();
        var source = await fixture.Workspace.CaptureExportSourceAsync(default);
        var original = fixture.Repo.Content[source.Revision.FindFile(fixture.Main)!.ContentHash].ToArray();
        fixture.Workspace.EditText(fixture.Main, "later unsaved edit — must not enter captured ZIP🙂");
        var state = fixture.State();
        var transfer = new ZipTransferService(fixture.Repo);
        var archive = await transfer.BuildZipAsync(source, default);
        var stage = await transfer.StageZipImportAsync(archive.Bytes, "captured", default);
        Assert.True(stage.Valid);
        var path = source.Revision.FindFile(fixture.Main)!.RelativePath;
        Assert.Equal(original, Assert.Single(stage.Staged.Files, file => file.RelativePath == path).Bytes);
        Assert.Equal(source.Revision.Id, archive.CapturedRevision);
        Assert.Equal(source.Revision.Files.Length, stage.Staged.Files.Length);
        Assert.Equal(state, fixture.State()); Assert.True(fixture.Workspace.IsDirty);
        Assert.Equal(0, fixture.Repo.Publishes);
    }

    [Fact]
    public async Task BuilderRetainsExistingSizeAndCancellationGuards()
    {
        await using var fixture = await CompilationTestWorkspace.Open();
        var transfer = new ZipTransferService(fixture.Repo);
        var revision = fixture.Workspace.CurrentRevision!;
        var file = revision.Files[0] with { ByteLength = ArchiveLimits.SingleFile + 1 };
        var oversized = revision with { Files = ImmutableArray.Create(file) };
        var reads = 0;
        Task<byte[]> Read(SHA256 hash, CancellationToken ct) { reads++; return Task.FromResult(fixture.Repo.Content[hash]); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => transfer.BuildZipAsync(new(oversized, Read), default));
        Assert.Equal(0, reads);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer.BuildZipAsync(new(revision, Read), cancelled.Token));
        Assert.Equal(0, reads);
        await Assert.ThrowsAsync<InvalidDataException>(() => transfer.BuildZipAsync(
            new(revision, (hash, ct) => Task.FromResult(new byte[10000])), default));
    }
}
