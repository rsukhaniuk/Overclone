using System.Text;
using Client.Workspace.Drive;
using Client.Workspace.Domain;
using Client.Workspace.Transfer;
using Xunit;
using static Workspace.Tests.Drive.DriveRestTests;

namespace Workspace.Tests.Drive;

// Synthetic finite HTTP and auth ports; real workspace, capture, ZIP validation
// and publication seam. Not live-account, browser OAuth or CORS evidence.
public sealed class DriveSessionTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task StageRequiresExplicitAcceptance_NewProjectPreservesDirtyCurrentDraft()
    {
        await using var fixture = await CompilationTestWorkspace.Open();
        fixture.Workspace.EditText(fixture.Main, "unsaved current draft🙂");
        var state = fixture.State();
        var zip = await Archive(fixture);
        var handler = new Handler(_ => Response(zip));
        var auth = new Auth();
        using var rest = new DriveRestClient(new HttpClient(handler), auth);
        var transfer = new ZipTransferService(fixture.Repo);
        using var session = new DriveTransferSession(fixture.Workspace, transfer, transfer, rest, auth);
        Assert.Equal(0, handler.Calls); Assert.Equal(0, fixture.Repo.Publishes);
        await session.ChooseAsync(); Assert.Equal(0, handler.Calls);
        await session.DownloadAndStageAsync();
        Assert.True(session.CanAccept); Assert.Equal(0, fixture.Repo.Publishes);
        Assert.Equal(state, fixture.State());
        var receipt = await session.AcceptImportAsync();
        Assert.True(receipt!.Published); Assert.NotEqual(fixture.Workspace.Project!.Id, receipt.Project!.Id);
        Assert.Equal(1, fixture.Repo.Publishes); Assert.Equal(state, fixture.State());
        Assert.Null(await session.AcceptImportAsync()); Assert.Equal(1, fixture.Repo.Publishes);
    }

    [Fact]
    public async Task FailedDownloadOrInvalidZipNeverPublishesOrMutatesDraft()
    {
        await using var fixture = await CompilationTestWorkspace.Open();
        fixture.Workspace.EditText(fixture.Main, "retain me");
        var state = fixture.State();
        var handler = new Handler(_ => Response(Encoding.UTF8.GetBytes("not a zip")));
        var auth = new Auth();
        using var rest = new DriveRestClient(new HttpClient(handler), auth);
        var transfer = new ZipTransferService(fixture.Repo);
        using var session = new DriveTransferSession(fixture.Workspace, transfer, transfer, rest, auth);
        await session.ChooseAsync(); await session.DownloadAndStageAsync();
        Assert.False(session.ImportPreview!.Valid); Assert.False(session.CanAccept);
        Assert.Null(await session.AcceptImportAsync());
        handler.Next = _ => Task.FromException<HttpResponseMessage>(new HttpRequestException("raw private details"));
        await session.DownloadAndStageAsync();
        Assert.Null(session.ImportPreview); Assert.False(session.CanAccept);
        Assert.Equal(0, fixture.Repo.Publishes); Assert.Equal(state, fixture.State());
        Assert.DoesNotContain("raw", session.Notice);
    }

    [Fact]
    public async Task PrepareSavesLocally_OnlyConfirmationPostsExactCapturedRevision()
    {
        await using var fixture = await CompilationTestWorkspace.Open();
        fixture.Workspace.EditText(fixture.Main, "new revision Україна🙂\r\n");
        byte[]? uploaded = null;
        var handler = new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var parts = Assert.IsType<MultipartContent>(request.Content).ToArray();
            uploaded = await parts[1].ReadAsByteArrayAsync();
            return Response(Encoding.UTF8.GetBytes("{\"id\":\"created\"}"), "application/json");
        });
        var auth = new Auth();
        using var rest = new DriveRestClient(new HttpClient(handler), auth);
        var transfer = new ZipTransferService(fixture.Repo);
        using var session = new DriveTransferSession(fixture.Workspace, transfer, transfer, rest, auth);
        Assert.Equal(0, handler.Calls);
        await session.UploadNewAsync(); Assert.Equal(0, handler.Calls);
        await session.PrepareExportAsync();
        Assert.False(fixture.Workspace.IsDirty); Assert.True(session.CanUpload); Assert.Equal(0, handler.Calls);
        var captured = fixture.Workspace.CurrentRevision!;
        Assert.Equal(captured.Id, session.ExportPreview!.Revision);
        Assert.Equal(captured.ProjectId, session.ExportPreview.ProjectId);
        await session.UploadNewAsync(); Assert.Equal(1, handler.Calls);
        Assert.Null(session.ExportPreview);
        var stage = await transfer.StageZipImportAsync(uploaded!, "verify", default);
        Assert.True(stage.Valid);
        foreach (var file in captured.Files)
        {
            var roundtrip = Assert.Single(stage.Staged.Files, f => f.RelativePath == file.RelativePath);
            Assert.Equal(fixture.Repo.Content[file.ContentHash], roundtrip.Bytes);
            Assert.Equal(file.ContentHash, roundtrip.Hash);
        }
        await session.UploadNewAsync(); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task EditOrSessionChangeInvalidatesPreview_AndFailedLocalSaveNeverUploads()
    {
        await using var fixture = await CompilationTestWorkspace.Open();
        var handler = new Handler(_ => Response([]));
        var auth = new Auth();
        using var rest = new DriveRestClient(new HttpClient(handler), auth);
        var transfer = new ZipTransferService(fixture.Repo);
        using var session = new DriveTransferSession(fixture.Workspace, transfer, transfer, rest, auth);
        await session.PrepareExportAsync(); Assert.True(session.CanUpload);
        fixture.Workspace.EditText(fixture.Main, "later edit");
        Assert.Null(session.ExportPreview); Assert.False(session.CanUpload);
        await session.UploadNewAsync(); Assert.Equal(0, handler.Calls);
        fixture.Repo.FailSave = true;
        await session.PrepareExportAsync(); Assert.Null(session.ExportPreview);
        Assert.True(fixture.Workspace.IsDirty); Assert.Equal(0, handler.Calls);
        fixture.Repo.FailSave = false;
        await session.PrepareExportAsync(); Assert.True(session.CanUpload);
        await fixture.Workspace.OpenAsync(fixture.Repo.Project.Id, default);
        Assert.Null(session.ExportPreview); Assert.False(session.CanUpload);
    }

    [Fact]
    public async Task LateDownloadAfterSessionChangeIsDiscarded_BusyUntilPhysicalSettlement()
    {
        await using var fixture = await CompilationTestWorkspace.Open();
        var zip = await Archive(fixture);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(_ => { reached.TrySetResult(); return response.Task; });
        var auth = new Auth();
        using var rest = new DriveRestClient(new HttpClient(handler), auth);
        var transfer = new ZipTransferService(fixture.Repo);
        using var session = new DriveTransferSession(fixture.Workspace, transfer, transfer, rest, auth);
        await session.ChooseAsync();
        var pending = session.DownloadAndStageAsync();
        await reached.Task.WaitAsync(Wait);
        await fixture.Workspace.OpenAsync(fixture.Repo.Project.Id, default);
        Assert.True(session.Busy);
        await session.ChooseAsync(); Assert.Equal(1, handler.Calls);
        response.SetResult(Response(zip));
        await pending.WaitAsync(Wait);
        Assert.False(session.Busy); Assert.Null(session.Selection); Assert.Null(session.ImportPreview);
        Assert.Null(await session.AcceptImportAsync()); Assert.Equal(0, fixture.Repo.Publishes);
    }

    [Fact]
    public async Task CancelledUploadRemainsOwnedUntilSettlement_ThenReportsUncertainWithoutRetry()
    {
        await using var fixture = await CompilationTestWorkspace.Open();
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(_ => { reached.TrySetResult(); return response.Task; });
        var auth = new Auth();
        using var rest = new DriveRestClient(new HttpClient(handler), auth);
        var transfer = new ZipTransferService(fixture.Repo);
        using var session = new DriveTransferSession(fixture.Workspace, transfer, transfer, rest, auth);
        await session.PrepareExportAsync();
        var pending = session.UploadNewAsync();
        await reached.Task.WaitAsync(Wait);
        session.Cancel();
        Assert.True(session.Busy); Assert.False(session.CanUpload);
        await session.PrepareExportAsync(); Assert.True(session.Busy);
        response.SetResult(Response(Encoding.UTF8.GetBytes("{\"id\":\"maybe_created\"}"), "application/json"));
        await pending.WaitAsync(Wait);
        Assert.False(session.Busy); Assert.Contains("uncertain", session.RemoteNotice);
        Assert.Contains("Check Drive", session.RemoteNotice); Assert.Equal(1, handler.Calls);
        Assert.Null(session.ExportPreview);
        await session.UploadNewAsync(); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DisconnectInvalidatesStagedImport_AndRemountDoesNotReuseOldSession()
    {
        await using var fixture = await CompilationTestWorkspace.Open();
        var zip = await Archive(fixture);
        var handler = new Handler(_ => Response(zip));
        var auth = new Auth();
        using var rest = new DriveRestClient(new HttpClient(handler), auth);
        var transfer = new ZipTransferService(fixture.Repo);
        var session = new DriveTransferSession(fixture.Workspace, transfer, transfer, rest, auth);
        await session.ChooseAsync(); await session.DownloadAndStageAsync(); Assert.True(session.CanAccept);
        session.Disconnect(); Assert.False(session.CanAccept); Assert.Null(await session.AcceptImportAsync());
        session.Dispose();
        using var remount = new DriveTransferSession(fixture.Workspace, transfer, transfer, rest, auth);
        Assert.Null(remount.ImportPreview); Assert.Null(remount.ExportPreview); Assert.False(remount.Busy);
        Assert.Equal(0, fixture.Repo.Publishes);
    }

    [Fact]
    public async Task UnmountRetainsPhysicalOwnerAndRemoteOutcome_WithoutRepaintingNewProject()
    {
        await using var fixture = await CompilationTestWorkspace.Open();
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(_ => response.Task);
        var auth = new Auth();
        using var rest = new DriveRestClient(new HttpClient(handler), auth);
        var transfer = new ZipTransferService(fixture.Repo);
        var old = new DriveTransferSession(fixture.Workspace, transfer, transfer, rest, auth);
        await old.PrepareExportAsync();
        var pending = old.UploadNewAsync();
        Assert.True(old.Busy); old.Dispose();
        await fixture.Workspace.OpenAsync(fixture.Repo.Project.Id, default);
        using var remount = new DriveTransferSession(fixture.Workspace, transfer, transfer, rest, auth);
        await remount.PrepareExportAsync(); await remount.UploadNewAsync();
        Assert.Equal(1, handler.Calls); // rest port rejects another physical owner
        var notice = remount.Notice;
        response.SetResult(Response(Encoding.UTF8.GetBytes("{\"id\":\"late\"}"), "application/json"));
        await pending.WaitAsync(Wait);
        Assert.Equal(notice, remount.Notice); // no old-project view update
        Assert.Contains("uncertain", remount.RemoteNotice); // separate remote ledger
        Assert.Equal(0, fixture.Repo.Publishes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbaDraftEditDoesNotReviveImportOrExportConfirmation(bool import)
    {
        await using var fixture = await CompilationTestWorkspace.Open();
        var handler = new Handler(_ => Response([]));
        var auth = new Auth();
        using var rest = new DriveRestClient(new HttpClient(handler), auth);
        var transfer = new ZipTransferService(fixture.Repo);
        using var session = new DriveTransferSession(fixture.Workspace, transfer, transfer, rest, auth);
        if (import)
        {
            var bytes = await Archive(fixture);
            handler.Next = _ => Task.FromResult(Response(bytes));
            await session.ChooseAsync(); await session.DownloadAndStageAsync();
            Assert.True(session.CanAccept);
        }
        else { await session.PrepareExportAsync(); Assert.True(session.CanUpload); }
        var initial = fixture.Workspace.Files.Single(f => f.Id == fixture.Main).Content!;
        var generation = fixture.Workspace.WorkingGeneration;
        fixture.Workspace.EditText(fixture.Main, initial + "ABA");
        fixture.Workspace.EditText(fixture.Main, initial);
        Assert.True(fixture.Workspace.WorkingGeneration > generation);
        Assert.Equal(initial, fixture.Workspace.Files.Single(f => f.Id == fixture.Main).Content);
        Assert.Null(session.ImportPreview); Assert.Null(session.ExportPreview);
        Assert.False(session.CanAccept); Assert.False(session.CanUpload);
        var calls = handler.Calls;
        Assert.Null(await session.AcceptImportAsync()); await session.UploadNewAsync();
        Assert.Equal(calls, handler.Calls); Assert.Equal(0, fixture.Repo.Publishes);
    }

    [Fact]
    public async Task LocalSaveHeadMovementRefusesStagedImportWithoutDraftGenerationChange()
    {
        await using var fixture = await CompilationTestWorkspace.Open();
        var bytes = await Archive(fixture);
        fixture.Workspace.EditText(fixture.Main, "dirty before stage");
        var handler = new Handler(_ => Response(bytes));
        var auth = new Auth();
        using var rest = new DriveRestClient(new HttpClient(handler), auth);
        var transfer = new ZipTransferService(fixture.Repo);
        using var session = new DriveTransferSession(fixture.Workspace, transfer, transfer, rest, auth);
        await session.ChooseAsync(); await session.DownloadAndStageAsync();
        Assert.True(session.CanAccept);
        var generation = fixture.Workspace.WorkingGeneration;
        var head = fixture.Workspace.CurrentRevision!.Id;
        await fixture.Workspace.SaveAsync(default);
        Assert.NotEqual(head, fixture.Workspace.CurrentRevision!.Id);
        Assert.Equal(generation, fixture.Workspace.WorkingGeneration);
        // A save-only head move need not emit WorkspaceChanged. Acceptance
        // must compare the live full stamp even without an invalidation event.
        Assert.False(session.CanAccept);
        Assert.Null(await session.AcceptImportAsync()); Assert.Equal(0, fixture.Repo.Publishes);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("disconnect")]
    [InlineData("unmount")]
    public async Task CompletedLocalPublicationWithHeldReceiptIsReconciledWithoutRepeatAcceptance(string invalidation)
    {
        await using var fixture = await CompilationTestWorkspace.Open();
        var bytes = await Archive(fixture);
        fixture.Workspace.EditText(fixture.Main, "dirty current draft survives late import🙂");
        var state = fixture.State();
        var handler = new Handler(_ => Response(bytes));
        var auth = new Auth();
        using var rest = new DriveRestClient(new HttpClient(handler), auth);
        var zip = new ZipTransferService(fixture.Repo);
        var transfer = new HeldPublication(zip);
        using var session = new DriveTransferSession(fixture.Workspace, transfer, zip, rest, auth);
        await session.ChooseAsync(); await session.DownloadAndStageAsync();
        var pending = session.AcceptImportAsync();
        var published = await transfer.Published.Task.WaitAsync(Wait);
        Assert.True(published.Published); Assert.Equal(1, fixture.Repo.Publishes);
        Assert.True(session.Busy); Assert.Null(session.ImportPreview);
        if (invalidation == "cancel") session.Cancel();
        else if (invalidation == "disconnect") session.Disconnect();
        else session.Dispose();
        Assert.True(session.Busy); // receipt/transport still physically owned
        using var remount = new DriveTransferSession(fixture.Workspace, transfer, zip, rest, auth);
        Assert.Null(await remount.AcceptImportAsync());
        transfer.Release.TrySetResult();
        Assert.Null(await pending.WaitAsync(Wait)); // late result cannot repaint cancelled view
        Assert.False(session.Busy);
        Assert.Null(await session.AcceptImportAsync()); Assert.Null(await remount.AcceptImportAsync());
        Assert.Equal(1, fixture.Repo.Publishes); Assert.Equal(1, handler.Calls);
        Assert.Equal(state, fixture.State());
        // Reconcile the committed project through the existing project-list seam,
        // not by blindly re-importing after loss of the panel's receipt.
        var projects = await fixture.Repo.ListProjectsAsync(default);
        var recovered = Assert.Single(projects, p => p.Id == published.Project!.Id);
        Assert.NotEqual(fixture.Workspace.Project!.Id, recovered.Id);
        Assert.Equal(published.Head, recovered.HeadRevisionId);
    }

    private sealed class HeldPublication(IProjectTransfer inner) : IProjectTransfer
    {
        internal readonly TaskCompletionSource<ImportPublishResult> Published = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<TransferStageResult> StageZipImportAsync(byte[] bytes, string name, CancellationToken ct)
            => inner.StageZipImportAsync(bytes, name, ct);
        public Task<ExportReceipt> ExportZipAsync(ExportSource source, CancellationToken ct)
            => inner.ExportZipAsync(source, ct);
        public async Task<ImportPublishResult> PublishAsync(StagedProject staged, string name, CancellationToken ct)
        {
            var result = await inner.PublishAsync(staged, name, ct);
            Published.TrySetResult(result);
            await Release.Task; // committed publication cannot be undone by cancellation
            return result;
        }
    }

    private static async Task<byte[]> Archive(CompilationTestWorkspace fixture)
    {
        var source = new ExportSource(fixture.Workspace.CurrentRevision!, (hash, ct) => Task.FromResult(fixture.Repo.Content[hash].ToArray()));
        return (await new ZipTransferService(fixture.Repo).BuildZipAsync(source, default)).Bytes;
    }
}
