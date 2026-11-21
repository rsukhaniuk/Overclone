using System.Collections.Immutable;
using System.Text.Json;
using Client.Workspace.Domain;
using Client.Workspace.Transfer;
using Microsoft.JSInterop;
using Xunit;

namespace Workspace.Tests;

public sealed class FolderTransferServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrecancelledOperationDoesNotOpenPicker(bool export)
    {
        var browser = new Browser();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Start(browser, export, cancellation.Token));
        Assert.Empty(browser.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PickerSettlesBeforeCancelledOperationDisposesItsDirectory(bool export)
    {
        var browser = new Browser { Picker = Signal<IJSObjectReference>() };
        using var cancellation = new CancellationTokenSource();
        var operation = Start(browser, export, cancellation.Token);
        cancellation.Cancel();

        Assert.Equal(new[] { "import", "pickDirectory" }, browser.Calls);
        Assert.False(operation.IsCompleted);
        browser.Picker.SetResult(browser.Directory);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        Assert.Equal(new[] { "import", "pickDirectory", "releaseHandle" }, browser.Calls);
        Assert.Equal(1, browser.Directory.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledImportWaitsForEnumerationOrLastReadAndReleasesAllHandles(bool cancelDuringLastRead)
    {
        var browser = new Browser
        {
            Enumeration = cancelDuringLastRead ? null : Signal<string>(),
            Read = cancelDuringLastRead ? Signal<byte[]>() : null,
        };
        using var cancellation = new CancellationTokenSource();
        var operation = Start(browser, false, cancellation.Token);
        cancellation.Cancel();

        Assert.False(operation.IsCompleted);
        Assert.Equal(cancelDuringLastRead ? "readFileHandleById" : "enumerateTree", browser.Calls.Last());
        Assert.Equal(0, browser.Directory.Disposals);
        browser.Enumeration?.SetResult(Browser.Files);
        browser.Read?.SetResult([42]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(cancelDuringLastRead, browser.Calls.Contains("readFileHandleById"));
        Assert.Equal(new long[] { 42 }, browser.ReleasedFileIds);
        Assert.Equal("releaseHandle", browser.Calls.Last());
        Assert.Equal(1, browser.Directory.Disposals);
    }

    [Fact]
    public async Task FailedEnumerationDisposesDirectoryAndReportsPermissionDenial()
    {
        var browser = new Browser { Enumeration = Signal<string>() };
        var stage = new FolderTransferService(browser).StageImportFromFolderAsync("fixture", default);
        browser.Enumeration.SetException(new JSException("NotAllowedError"));

        var result = await stage;
        Assert.False(result.Valid);
        Assert.Equal("PermissionDenied", Assert.Single(result.Staged.ValidationErrors).Code);
        Assert.Empty(browser.ReleasedFileIds);
        Assert.Equal(1, browser.Directory.Disposals);
    }

    [Fact]
    public async Task CleanupFailureStillDisposesDirectoryAndPreservesStagedBytes()
    {
        var browser = new Browser { FailFileRelease = true };
        var result = await new FolderTransferService(browser).StageImportFromFolderAsync("fixture", default);

        Assert.True(result.Valid);
        Assert.Equal(new byte[] { 42 }, Assert.Single(result.Staged.Files).Bytes);
        Assert.Equal(1, browser.Directory.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportDisposesDirectoryWithoutWriteAfterLastReadCancelsOrLengthMismatch(bool cancel)
    {
        var browser = new Browser();
        using var cancellation = new CancellationTokenSource();
        var source = Source((_, _) =>
        {
            if (cancel) cancellation.Cancel();
            return Task.FromResult(cancel ? new byte[] { 42 } : Array.Empty<byte>());
        });
        var operation = new FolderTransferService(browser).ExportToFolderAsync(source, cancellation.Token);

        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        else
            Assert.Equal("ContentLengthMismatch", Assert.Single((await operation).Failed).Code);
        Assert.Equal(new[] { "import", "pickDirectory", "releaseHandle" }, browser.Calls);
        Assert.Equal(1, browser.Directory.Disposals);
    }

    [Fact]
    public async Task AdmittedExportWaitsForAndReturnsPartialReceiptAfterCancellation()
    {
        var browser = new Browser { Write = Signal<string>() };
        using var cancellation = new CancellationTokenSource();
        var source = Source();
        var operation = new FolderTransferService(browser).ExportToFolderAsync(source, cancellation.Token);
        cancellation.Cancel();

        Assert.Equal("exportToDirectory", browser.Calls.Last());
        Assert.False(operation.IsCompleted);
        Assert.Equal(0, browser.Directory.Disposals);
        browser.Write.SetResult("""
            {"complete":false,"directoryName":"export-test","completed":[],
             "failed":[{"path":"main.tex","code":"WriteFailed","message":"Write was refused."}]}
            """);

        var receipt = await operation;
        Assert.False(receipt.Complete);
        Assert.Equal("export-test", receipt.DirectoryName);
        Assert.Equal("main.tex", Assert.Single(receipt.Failed).Path);
        Assert.Contains("0 written, 1 failed", receipt.PartialNote);
        Assert.Equal(source.Revision.Id, receipt.CapturedRevision);
        Assert.Equal(1, browser.Directory.Disposals);
    }

    private static Task Start(Browser browser, bool export, CancellationToken token)
    {
        var service = new FolderTransferService(browser);
        return export ? service.ExportToFolderAsync(Source(), token) : service.StageImportFromFolderAsync("fixture", token);
    }

    private static ExportSource Source(Func<SHA256, CancellationToken, Task<byte[]>>? read = null)
    {
        var file = new ProjectFile(FileId.New(), "main.tex", "text", new SHA256("fixture"), 1);
        var revision = ProjectRevision.Root(ProjectId.New()) with { Files = ImmutableArray.Create(file) };
        return new ExportSource(revision, read ?? ((_, _) => Task.FromResult(new byte[] { 42 })));
    }

    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Browser : IJSRuntime, IJSObjectReference
    {
        public const string Files = """{"files":[{"path":"main.tex","handleId":42}]}""";
        public readonly List<string> Calls = [];
        public readonly TestDirectory Directory = new();
        public long[] ReleasedFileIds = [];
        public TaskCompletionSource<IJSObjectReference>? Picker;
        public TaskCompletionSource<string>? Enumeration;
        public TaskCompletionSource<byte[]>? Read;
        public TaskCompletionSource<string>? Write;
        public bool FailFileRelease;

        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args)
            => InvokeAsync<T>(identifier, CancellationToken.None, args);

        public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.False(cancellationToken.CanBeCanceled);
            Calls.Add(identifier);
            switch (identifier)
            {
                case "import": return (T)(object)this;
                case "pickDirectory": return (T)(object)(Picker is null ? Directory : await Picker.Task);
                case "enumerateTree":
                    Assert.Same(Directory, args![0]);
                    return JsonSerializer.Deserialize<T>(Enumeration is null ? Files : await Enumeration.Task, JsonSerializerOptions.Web)!;
                case "readFileHandleById":
                    Assert.Equal(42L, Assert.IsType<long>(args![0]));
                    return (T)(object)(Read is null ? new byte[] { 42 } : await Read.Task);
                case "releaseHandles":
                    ReleasedFileIds = Assert.IsType<long[]>(Assert.Single(args!));
                    if (FailFileRelease) throw new JSException("Module disconnected");
                    return default!;
                case "releaseHandle":
                    Assert.Same(Directory, Assert.Single(args!));
                    return default!;
                case "exportToDirectory":
                    Assert.Same(Directory, args![0]);
                    return JsonSerializer.Deserialize<T>(await Write!.Task, JsonSerializerOptions.Web)!;
                default: throw new NotSupportedException(identifier);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestDirectory : IJSObjectReference
    {
        public int Disposals { get; private set; }
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => throw new NotSupportedException();
        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}
