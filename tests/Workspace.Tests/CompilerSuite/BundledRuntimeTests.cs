using System.Net;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Client.Compilation.Presentation;
using Client.RuntimeAssets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace Workspace.Tests.CompilerSuite;

public sealed class BundledRuntimeTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Headers.Authorization);
            string path = request.RequestUri!.AbsolutePath;
            Assert.StartsWith("/bundled-runtime/", path);
            Paths.Add(path);
            return respond(request, token);
        }
    }
    private static HttpClient Client(Handler handler) => new(handler) { BaseAddress = new("https://bundled.test/"), Timeout = Timeout.InfiniteTimeSpan };
    private sealed class NoBrowserIo : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new InvalidOperationException("Unexpected browser IO");
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => throw new InvalidOperationException("Unexpected browser IO");
    }
    [Fact]
    public async Task Application_registration_selects_the_bundled_provider_without_touching_user_storage()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected download before opening tools"));
        var assets = new BundledRuntimeAssets(Client(handler));
        await using var services = new ServiceCollection().AddSingleton<IJSRuntime>(new NoBrowserIo())
            .AddAuthoringWorkspace().AddScoped(_ => assets).BuildServiceProvider();
        Assert.True(services.GetRequiredService<LocalInstalledRuntimeProviders>().UsesBundledResources);
        Assert.True(Assert.IsType<LocalCompileWorkspaceConfiguration>(services.GetRequiredService<ICompileWorkspaceConfiguration>()).UsesBundledResources);
        Assert.Empty(handler.Paths);
    }
    [Fact]
    public async Task Missing_assets_retry_without_any_manual_installation_or_storage()
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        await using var assets = new BundledRuntimeAssets(Client(handler));
        Assert.Equal(RuntimeAssetStatus.Missing, (await assets.ReadCompilerAsync()).Store.Status);
        Assert.Equal(RuntimeAssetStatus.Missing, (await assets.ReadCompilerAsync()).Store.Status);
        Assert.Equal(2, handler.Paths.Count);
        Assert.All(handler.Paths, p => Assert.Equal("/bundled-runtime/compiler-suite.zip", p));
    }
    [Fact]
    public async Task Invalid_length_is_refused_and_cannot_publish_a_package()
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) }));
        await using var assets = new BundledRuntimeAssets(Client(handler));
        var result = await assets.ReadCompilerAsync();
        Assert.Null(result.Package); Assert.Equal(RuntimeAssetStatus.Corrupt, result.Store.Status);
        Assert.False(assets.Owns(result.Package));
    }
    [Fact]
    public async Task Cancelled_wait_keeps_one_actual_download_until_its_response_settles()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (_, _) => { entered.TrySetResult(); await release.Task; return new(HttpStatusCode.NotFound); });
        await using var assets = new BundledRuntimeAssets(Client(handler));
        using var cancel = new CancellationTokenSource();
        var first = assets.ReadCompilerAsync(cancel.Token);
        await entered.Task; cancel.Cancel();
        Assert.Equal(RuntimeAssetStatus.Cancelled, (await first).Store.Status);
        var second = assets.ReadCompilerAsync();
        Assert.Single(handler.Paths); Assert.False(second.IsCompleted); Assert.False(assets.WhenSettled.IsCompleted);
        release.SetResult();
        Assert.Equal(RuntimeAssetStatus.Missing, (await second).Store.Status);
        await assets.WhenSettled;
    }
    [SuiteFixtureFact]
    public async Task Genuine_bundled_archive_is_verified_once_and_owned_package_is_shared_without_installer_calls()
    {
        string directory = Environment.GetEnvironmentVariable("COMPILER_SUITE_RUNTIME_DIR")!;
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(File.OpenRead(Path.Combine(directory, "compiler-suite.zip"))) }));
        await using var assets = new BundledRuntimeAssets(Client(handler));
        var first = assets.ReadCompilerAsync(); var second = assets.ReadCompilerAsync();
        var package = Assert.IsType<NativeRuntimePackage>((await first).Package);
        Assert.Same(package, (await second).Package); Assert.Same(package, (await assets.ReadCompilerAsync()).Package);
        Assert.True(assets.Owns(package)); Assert.Equal(NativeRuntimeProfile.LocalCompilerSuite, package.Profile);
        Assert.Single(handler.Paths);
        var wasm = package.Copy("xetex.wasm"); wasm[0] ^= 1;
        Assert.Equal(NativeCompilerSuitePins.XeWasmHash, NativeRuntimePackage.Digest(package.Copy("xetex.wasm")).Hex);
        await assets.DisposeAsync();
        Assert.False(assets.Owns(package));
        Assert.Equal(RuntimeAssetStatus.Disposed, (await assets.ReadCompilerAsync()).Store.Status);
    }
}
