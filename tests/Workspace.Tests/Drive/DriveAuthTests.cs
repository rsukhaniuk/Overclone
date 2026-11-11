using System.Net.Http.Headers;
using System.Text.Json;
using Client.Workspace.Drive;
using Client.Workspace.Transfer;
using Microsoft.JSInterop;
using Xunit;

namespace Workspace.Tests.Drive;

public sealed class DriveAuthTests
{
    internal static readonly DriveConfiguration Config = new("synthetic.apps.googleusercontent.com", "synthetic-public-key", "123");
    private static BrowserDriveAuth.TokenReply Ready(string scope = BrowserDriveAuth.Scope) => new()
    { Code = "ready", Token = "synthetic-memory-token", Scope = scope, ExpiresIn = 3600 };

    [Theory]
    [InlineData("client\n")]
    [InlineData("key\n")]
    [InlineData("project\n")]
    public async Task PublicConfigurationRejectsFinalNewlineBeforeBrowserGesture(string invalid)
    {
        var config = invalid.StartsWith("client", StringComparison.Ordinal)
            ? Config with { ClientId = Config.ClientId + "\n" }
            : invalid.StartsWith("key", StringComparison.Ordinal)
                ? Config with { PickerApiKey = Config.PickerApiKey + "\n" }
                : Config with { ProjectNumber = Config.ProjectNumber + "\n" };
        Assert.False(config.PickerConfigured);
        var js = new Interop(); js.Module.Token = () => Task.FromResult(Ready());
        await using var auth = new BrowserDriveAuth(js);
        if (!config.OAuthConfigured)
        {
            await auth.ConnectAsync(config);
            Assert.Equal(DriveCode.Unconfigured, auth.Status);
            Assert.Empty(js.Calls);
        }
        else
        {
            await auth.ConnectAsync(Config);
            Assert.Equal(DriveCode.Configuration,
                (await Assert.ThrowsAsync<DriveFailure>(() => auth.ChooseZipAsync(config))).Code);
            Assert.DoesNotContain("chooseZip", js.Module.Calls);
        }
    }

    [Fact]
    public async Task NoInteropBeforeGesture_UnconfiguredDeniedAndExactScope()
    {
        var js = new Interop();
        await using var auth = new BrowserDriveAuth(js);
        Assert.Empty(js.Calls);
        Assert.Equal(DriveCode.Disconnected, auth.Status);
        await auth.ConnectAsync(new());
        Assert.Equal(DriveCode.Unconfigured, auth.Status);
        Assert.Empty(js.Calls);
        js.Module.Token = () => Task.FromResult(new BrowserDriveAuth.TokenReply { Code = "denied" });
        await auth.ConnectAsync(Config);
        Assert.Equal(DriveCode.Denied, auth.Status);
        using var denied = new HttpRequestMessage();
        Assert.Equal(DriveCode.Disconnected, Assert.Throws<DriveFailure>(() => auth.Authorize(denied)).Code);
        Assert.Null(denied.Headers.Authorization);
        js.Module.Token = () => Task.FromResult(Ready(BrowserDriveAuth.Scope + " profile"));
        await auth.ConnectAsync(Config);
        Assert.Equal(DriveCode.Malformed, auth.Status);
        js.Module.Token = () => Task.FromResult(Ready());
        await auth.ConnectAsync(Config);
        Assert.Equal(DriveCode.Ready, auth.Status);
        Assert.Equal("https://www.googleapis.com/auth/drive.file", BrowserDriveAuth.Scope);
        using var request = new HttpRequestMessage(); auth.Authorize(request);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "synthetic-memory-token"), request.Headers.Authorization);
        Assert.All(js.Calls, name => Assert.Equal("import", name));
        Assert.All(js.Module.Calls, name => Assert.Contains(name, new[] { "connect", "disconnect" }));
        // Public auth state and wire ToString never expose tokens. There is no
        // persistence port and no token-bearing record-generated ToString.
        Assert.DoesNotContain("synthetic-memory-token", auth.ToString());
        Assert.DoesNotContain("synthetic-memory-token", Ready().ToString());
        Assert.DoesNotContain("synthetic-memory-token", JsonSerializer.Serialize(auth));
        Assert.DoesNotContain(Config.PickerApiKey, Config.ToString());
    }

    [Theory]
    [InlineData("offline", DriveCode.Offline)]
    [InlineData("popup", DriveCode.Popup)]
    [InlineData("configuration", DriveCode.Configuration)]
    [InlineData("busy", DriveCode.Busy)]
    public async Task SafeBrowserStatuses(string reply, DriveCode expected)
    {
        var js = new Interop();
        js.Module.Token = () => Task.FromResult(new BrowserDriveAuth.TokenReply { Code = reply });
        await using var auth = new BrowserDriveAuth(js);
        await auth.ConnectAsync(Config);
        Assert.Equal(expected, auth.Status);
    }

    [Fact]
    public async Task ExpiryAndDisconnectPreventAuthorization_NoAutomaticReconnect()
    {
        var time = new Clock(); var js = new Interop();
        js.Module.Token = () => Task.FromResult(Ready());
        await using var auth = new BrowserDriveAuth(js, time);
        await auth.ConnectAsync(Config);
        time.Now = time.Now.AddHours(1);
        using var expired = new HttpRequestMessage();
        Assert.Equal(DriveCode.Expired, Assert.Throws<DriveFailure>(() => auth.Authorize(expired)).Code);
        Assert.Null(expired.Headers.Authorization);
        Assert.Equal(1, js.Module.Calls.Count(n => n == "connect"));
        await auth.ConnectAsync(Config);
        auth.Disconnect();
        using var disconnected = new HttpRequestMessage();
        Assert.Equal(DriveCode.Disconnected, Assert.Throws<DriveFailure>(() => auth.Authorize(disconnected)).Code);
        Assert.Null(disconnected.Headers.Authorization);
        Assert.Equal(2, js.Module.Calls.Count(n => n == "connect"));
    }

    [Fact]
    public async Task DisconnectWhileConsentPending_LateReplyCannotRestoreToken()
    {
        var js = new Interop();
        var reply = new TaskCompletionSource<BrowserDriveAuth.TokenReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        js.Module.Token = () => reply.Task;
        await using var auth = new BrowserDriveAuth(js);
        var connecting = auth.ConnectAsync(Config);
        auth.Disconnect();
        var second = await Assert.ThrowsAsync<DriveFailure>(() => auth.ConnectAsync(Config));
        Assert.Equal(DriveCode.Busy, second.Code);
        reply.SetResult(Ready());
        await connecting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DriveCode.Disconnected, auth.Status);
        using var request = new HttpRequestMessage();
        Assert.Throws<DriveFailure>(() => auth.Authorize(request));
    }

    [Fact]
    public async Task PickerRequiresConfigurationAndBoundedSelection_NoUrlInput()
    {
        var js = new Interop(); js.Module.Token = () => Task.FromResult(Ready());
        await using var auth = new BrowserDriveAuth(js);
        await auth.ConnectAsync(Config);
        Assert.Equal(DriveCode.Configuration,
            (await Assert.ThrowsAsync<DriveFailure>(() => auth.ChooseZipAsync(new(Config.ClientId)))).Code);
        Assert.DoesNotContain("chooseZip", js.Module.Calls);
        js.Module.Selection = new() { Code = "ready", Id = "https://example.invalid/x", Name = "unsafe.zip" };
        Assert.Equal(DriveCode.InvalidSelection,
            (await Assert.ThrowsAsync<DriveFailure>(() => auth.ChooseZipAsync(Config))).Code);
        js.Module.Selection = new() { Code = "ready", Id = "safe_id-1", Name = "Україна🙂.zip" };
        Assert.Equal(new DriveSelection("safe_id-1", "Україна🙂.zip"), await auth.ChooseZipAsync(Config));
    }

    [Fact]
    public async Task BrowserLoaderBusyRefusesPickerButKeepsExistingAuthorization_OnlyExplicitRetry()
    {
        var js = new Interop(); js.Module.Token = () => Task.FromResult(Ready());
        await using var auth = new BrowserDriveAuth(js);
        await auth.ConnectAsync(Config);
        // JS can cancel its public gesture while retaining a held library load.
        // A later gesture must report Busy, not bad operator configuration.
        js.Module.Selection = new() { Code = "busy" };
        Assert.Equal(DriveCode.Busy,
            (await Assert.ThrowsAsync<DriveFailure>(() => auth.ChooseZipAsync(Config))).Code);
        Assert.Equal(DriveCode.Ready, auth.Status);
        using var request = new HttpRequestMessage(); auth.Authorize(request);
        Assert.Equal("synthetic-memory-token", request.Headers.Authorization!.Parameter);
        Assert.Equal(1, js.Module.Calls.Count(n => n == "chooseZip"));
        js.Module.Selection = new() { Code = "ready", Id = "new_id", Name = "new.zip" };
        Assert.Equal(new DriveSelection("new_id", "new.zip"), await auth.ChooseZipAsync(Config));
        Assert.Equal(2, js.Module.Calls.Count(n => n == "chooseZip"));
        Assert.Equal(1, js.Module.Calls.Count(n => n == "connect"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TokenExpiresAfterPickerBeforeRestDispatch_NoRequestOrUncertainUpload(bool upload)
    {
        var time = new Clock(); var js = new Interop();
        js.Module.Token = () => Task.FromResult(Ready());
        js.Module.Selection = new() { Code = "ready", Id = "selected_id", Name = "selected.zip" };
        await using var auth = new BrowserDriveAuth(js, time);
        await auth.ConnectAsync(Config);
        Assert.NotNull(await auth.ChooseZipAsync(Config));
        // Status was Ready at selection. Dispatch must recheck the real token
        // owner at the exact 30-second safety boundary, not trust that UI state.
        time.Now = time.Now.AddSeconds(3570);
        var handler = new DriveRestTests.Handler(_ => DriveRestTests.Response(new byte[] { 1 }));
        using var rest = new DriveRestClient(new HttpClient(handler), auth);
        var failure = await Assert.ThrowsAsync<DriveFailure>(async () =>
        {
            if (upload) await rest.CreateZipAsync("x.zip", new byte[] { 1 }, default);
            else await rest.DownloadAsync(new("selected_id", "selected.zip"), default);
        });
        Assert.Equal(DriveCode.Expired, failure.Code);
        Assert.False(failure.Uncertain);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(1, js.Module.Calls.Count(n => n == "connect"));
    }

    [Fact]
    public async Task CancelConnectIgnoresHeldLateCallbackAndDoesNotRepaintNotice()
    {
        await using var fixture = await CompilationTestWorkspace.Open();
        var js = new Interop();
        var callback = new TaskCompletionSource<BrowserDriveAuth.TokenReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        js.Module.Token = () => callback.Task;
        await using var auth = new BrowserDriveAuth(js);
        using var rest = new DriveRestClient(new HttpClient(new DriveRestTests.Handler(_ =>
            DriveRestTests.Response(new byte[] { 1 }))), auth);
        var zip = new ZipTransferService(fixture.Repo);
        using var session = new DriveTransferSession(fixture.Workspace, zip, zip, rest, auth);
        session.Configure(Config);
        var connecting = session.ConnectAsync();
        Assert.Contains("connect", js.Module.Calls);
        session.Cancel();
        var cancelledNotice = session.Notice;
        Assert.True(session.Busy); Assert.Equal(DriveCode.Cancelled, auth.Status);
        Assert.Contains("disconnect", js.Module.Calls);
        var late = Ready(); callback.SetResult(late);
        await connecting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.Busy); Assert.Equal(cancelledNotice, session.Notice);
        Assert.Equal(DriveCode.Cancelled, auth.Status); Assert.Empty(late.Token);
        using var request = new HttpRequestMessage();
        Assert.Throws<DriveFailure>(() => auth.Authorize(request));
    }

    [Fact]
    public async Task CancelPickerDiscardsHeldLateSelectionButPreservesValidToken()
    {
        await using var fixture = await CompilationTestWorkspace.Open();
        var js = new Interop(); js.Module.Token = () => Task.FromResult(Ready());
        var callback = new TaskCompletionSource<BrowserDriveAuth.SelectionReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        js.Module.Choose = () => callback.Task;
        await using var auth = new BrowserDriveAuth(js);
        using var rest = new DriveRestClient(new HttpClient(new DriveRestTests.Handler(_ =>
            DriveRestTests.Response(new byte[] { 1 }))), auth);
        var zip = new ZipTransferService(fixture.Repo);
        using var session = new DriveTransferSession(fixture.Workspace, zip, zip, rest, auth);
        session.Configure(Config); await session.ConnectAsync();
        var choosing = session.ChooseAsync();
        Assert.Contains("chooseZip", js.Module.Calls);
        session.Cancel(); var cancelledNotice = session.Notice;
        Assert.True(session.Busy); Assert.Equal(DriveCode.Ready, auth.Status);
        callback.SetResult(new() { Code = "ready", Id = "late_id", Name = "late.zip" });
        await choosing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.Busy); Assert.Null(session.Selection); Assert.Equal(cancelledNotice, session.Notice);
        using var request = new HttpRequestMessage(); auth.Authorize(request);
        Assert.Equal("synthetic-memory-token", request.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task DisposeOwnsLateModuleImport_IdempotentAndNoConsentAfterDispose()
    {
        var js = new Interop();
        var importing = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        js.ImportResult = importing.Task;
        var auth = new BrowserDriveAuth(js);
        var connecting = auth.ConnectAsync(Config);
        var disposing = auth.DisposeAsync().AsTask();
        var secondDispose = auth.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted); Assert.Single(js.Calls);
        importing.SetResult(js.Module);
        await Task.WhenAll(disposing, secondDispose).WaitAsync(TimeSpan.FromSeconds(5));
        var failure = await Assert.ThrowsAsync<DriveFailure>(() => connecting);
        Assert.Equal(DriveCode.Disconnected, failure.Code);
        Assert.Equal(1, js.Module.Disposes); Assert.DoesNotContain("connect", js.Module.Calls);
        await auth.DisposeAsync(); Assert.Equal(1, js.Module.Disposes);
    }

    [Fact]
    public async Task ModuleImportIsReusedAndCompletedModuleIsDisposedOnce()
    {
        var js = new Interop(); js.Module.Token = () => Task.FromResult(Ready());
        var auth = new BrowserDriveAuth(js);
        await auth.ConnectAsync(Config); await auth.ConnectAsync(Config);
        Assert.Single(js.Calls);
        await auth.DisposeAsync(); await auth.DisposeAsync();
        Assert.Equal(1, js.Module.Disposes);
    }

    [Fact]
    public async Task FailedImportRetriesOnlyOnLaterExplicitConnect_ThenReusesSuccessfulModule()
    {
        var js = new Interop();
        js.ImportResult = Task.FromException<IJSObjectReference>(new JSException("synthetic local-glue import failure"));
        js.Module.Token = () => Task.FromResult(Ready());
        var auth = new BrowserDriveAuth(js);
        await auth.ConnectAsync(Config).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DriveCode.Configuration, auth.Status);
        Assert.Single(js.Calls); Assert.DoesNotContain("connect", js.Module.Calls);
        using var unavailable = new HttpRequestMessage();
        Assert.Throws<DriveFailure>(() => auth.Authorize(unavailable));
        Assert.Single(js.Calls); // no hidden import retry or reauthorization
        js.ImportResult = null;
        await auth.ConnectAsync(Config).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DriveCode.Ready, auth.Status); Assert.Equal(2, js.Calls.Count);
        using var authorized = new HttpRequestMessage(); auth.Authorize(authorized);
        Assert.Equal("synthetic-memory-token", authorized.Headers.Authorization!.Parameter);
        await auth.ConnectAsync(Config).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, js.Calls.Count); Assert.Equal(0, js.Module.Disposes);
        await auth.DisposeAsync(); await auth.DisposeAsync();
        Assert.Equal(1, js.Module.Disposes);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Interop : IJSRuntime
    {
        internal readonly List<string> Calls = [];
        internal readonly Module Module = new();
        internal Task<IJSObjectReference>? ImportResult;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, default, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args)
        {
            Calls.Add(identifier);
            Assert.Equal("import", identifier);
            Assert.Equal("./js/google-drive-auth.js", args![0]);
            return ImportResult is null ? ValueTask.FromResult((TValue)(object)Module) : new ValueTask<TValue>(Import<TValue>());
        }
        private async Task<TValue> Import<TValue>() => (TValue)(object)await ImportResult!;
    }
    private sealed class Module : IJSObjectReference
    {
        internal readonly List<string> Calls = [];
        internal Func<Task<BrowserDriveAuth.TokenReply>> Token = () => throw new InvalidOperationException();
        internal BrowserDriveAuth.SelectionReply Selection = new() { Code = "cancelled" };
        internal Func<Task<BrowserDriveAuth.SelectionReply>>? Choose;
        internal int Disposes;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, default, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args)
        {
            Calls.Add(identifier); // identifiers only; never log token-bearing args
            if (identifier == "disconnect") return default!;
            if (identifier == "connect") return (TValue)(object)await Token();
            if (identifier == "chooseZip") return (TValue)(object)(Choose is null ? Selection : await Choose());
            throw new InvalidOperationException();
        }
        public ValueTask DisposeAsync() { Disposes++; return ValueTask.CompletedTask; }
    }
}
