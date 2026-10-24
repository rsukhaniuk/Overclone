using Client.Compilation.Download;
using Microsoft.JSInterop;
using Xunit;

namespace Workspace.Tests.CompilationDownload;

public sealed class PdfDownloadBrowserTests
{
    [Theory]
    [InlineData(null, "document.pdf")]
    [InlineData("", "document.pdf")]
    [InlineData("../CON", "document.pdf")]
    [InlineData("aux.pdf", "document.pdf")]
    [InlineData("LPT1", "document.pdf")]
    [InlineData("COM¹", "document.pdf")]
    [InlineData("NUL ", "document.pdf")]
    [InlineData(".hidden", "hidden.pdf")]
    [InlineData("path/to\\draft\r\n", "path_to_draft.pdf")]
    [InlineData("Диплом Київ", "Диплом Київ.pdf")]
    [InlineData("Draft.PDF", "Draft.pdf")]
    public void Portable_basename_is_deterministic(string? input, string expected)
    {
        Assert.Equal(expected, PdfDownloadFilename.Create(input));
        Assert.Equal(PdfDownloadFilename.Create(input), PdfDownloadFilename.Create(input));
    }
    [Fact] public void Unicode_bound_never_splits_scalar_or_emits_surrogates_or_controls()
    {
        var text = new string('a', 79) + "\U00010400" + "suffix";
        Assert.Equal(new string('a', 79) + ".pdf", PdfDownloadFilename.Create(text));
        Assert.Equal("document.pdf", PdfDownloadFilename.Create("\ud800\u0000/\\."));
        Assert.Equal(new string('a', 80) + ".pdf", PdfDownloadFilename.Create(new string('a', 200)));
    }
    [Fact] public async Task Cancellation_during_import_disposes_late_owned_module_without_click()
    {
        var js = new DownloadJs { Hold = true }; using var stop = new CancellationTokenSource();
        var preparing = new PdfDownloadBrowser(js).PrepareAsync(stop.Token).AsTask(); await js.Reached.Task;
        stop.Cancel(); js.Release.TrySetResult(js.Module);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing);
        Assert.Equal(1, js.Module.Disposals); Assert.Equal(0, js.Module.Calls);
    }
    [Fact] public async Task Prepared_module_commit_is_synchronous_and_disposal_idempotent()
    {
        var js = new DownloadJs(); var prepared = await new PdfDownloadBrowser(js).PrepareAsync(default);
        prepared.Download("document.pdf", [37, 80, 68, 70, 0, 255]);
        Assert.Equal("downloadPdf", js.Module.Identifier); Assert.Equal(1, js.Module.Calls);
        await prepared.DisposeAsync(); await prepared.DisposeAsync(); Assert.Equal(1, js.Module.Disposals);
        Assert.Throws<ObjectDisposedException>(() => prepared.Download("document.pdf", [1]));
    }
    [Fact] public async Task Async_only_module_is_rejected_and_disposed_instead_of_queueing_click()
    {
        var module = new AsyncOnlyModule(); var runtime = new AsyncOnlyRuntime(module);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PdfDownloadBrowser(runtime).PrepareAsync(default).AsTask());
        Assert.Equal(1, module.Disposals);
    }
    private sealed class AsyncOnlyRuntime(IJSObjectReference module) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult((TValue)(object)module);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
    private sealed class AsyncOnlyModule : IJSObjectReference
    {
        internal int Disposals;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("No deferred click.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new InvalidOperationException("No deferred click.");
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
