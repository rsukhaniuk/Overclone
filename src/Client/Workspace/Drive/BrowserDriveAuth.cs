using System.Net.Http.Headers;
using Microsoft.JSInterop;

namespace Client.Workspace.Drive;

/// <summary>Memory-only token owner. No token appears in public state or diagnostic strings.
/// Importing the local glue and loading Google libraries occur only on explicit actions.</summary>
public sealed class BrowserDriveAuth(IJSRuntime js, TimeProvider? clock = null) : IDriveAuth
{
    public const string Scope = "https://www.googleapis.com/auth/drive.file";
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private IJSObjectReference? _module;
    private Task<IJSObjectReference>? _moduleImport;
    private Task? _disposeTask;
    private Task _clearing = Task.CompletedTask;
    private string? _token;
    private DateTimeOffset _expires;
    private long _generation;
    private bool _busy, _connecting, _disposed;
    private DriveCode _status = DriveCode.Disconnected;
    public event Action? Changed;
    public DriveCode Status
    {
        get
        {
            if (_token is not null && _clock.GetUtcNow() >= _expires)
            {
                _token = null; _status = DriveCode.Expired; _generation++;
                if (_module is not null) _clearing = ClearBrowserAsync(_module);
            }
            return _status;
        }
    }

    public async Task ConnectAsync(DriveConfiguration configuration)
    {
        if (_busy) throw new DriveFailure(DriveCode.Busy);
        if (_disposed) throw new DriveFailure(DriveCode.Disconnected);
        Disconnect();
        if (!configuration.OAuthConfigured)
        { Set(DriveCode.Unconfigured); return; }
        _busy = true; _connecting = true;
        var generation = _generation;
        TokenReply? result = null;
        try
        {
            await _clearing;
            var module = await Module();
            if (_disposed || generation != _generation) return;
            result = await module.InvokeAsync<TokenReply>("connect", configuration.ClientId);
            if (_disposed || generation != _generation) return;
            if (result is null) { Set(DriveCode.Malformed); return; }
            if (result.Code != "ready") { Set(ParseCode(result.Code)); return; }
            if (result.Scope != Scope || result.Token is null || result.Token.Length is < 1 or > 8192 ||
                result.Token.Any(c => c <= 32 || c >= 127) || result.ExpiresIn is < 1 or > 3600)
            { Set(DriveCode.Malformed); return; }
            _token = result.Token;
            // Safety margin: never dispatch using a token at its expiry boundary.
            _expires = _clock.GetUtcNow().AddSeconds(Math.Max(0, result.ExpiresIn - 30));
            Set(DriveCode.Ready);
        }
        catch (JSDisconnectedException) { if (generation == _generation) Set(DriveCode.Disconnected); }
        catch (JSException) { if (generation == _generation) Set(DriveCode.Configuration); }
        finally { if (result is not null) result.Token = ""; _busy = false; _connecting = false; Changed?.Invoke(); }
    }

    public async Task<DriveSelection?> ChooseZipAsync(DriveConfiguration configuration)
    {
        if (_busy) throw new DriveFailure(DriveCode.Busy);
        RequireToken();
        if (!configuration.PickerConfigured) throw new DriveFailure(DriveCode.Configuration);
        var generation = _generation;
        _busy = true;
        try
        {
            var module = await Module();
            if (_disposed || generation != _generation) throw new DriveFailure(DriveCode.Stale);
            RequireToken();
            var result = await module.InvokeAsync<SelectionReply>("chooseZip", configuration.PickerApiKey,
                configuration.ProjectNumber, _token);
            if (generation != _generation || _disposed) throw new DriveFailure(DriveCode.Stale);
            RequireToken();
            if (result is null) throw new DriveFailure(DriveCode.Malformed);
            if (result.Code == "cancelled") return null;
            if (result.Code != "ready") throw new DriveFailure(ParseCode(result.Code));
            var selection = new DriveSelection(result.Id, result.Name);
            if (!selection.Valid) throw new DriveFailure(DriveCode.InvalidSelection);
            return selection;
        }
        catch (JSDisconnectedException) { throw new DriveFailure(DriveCode.Disconnected); }
        catch (JSException) { throw new DriveFailure(DriveCode.Configuration); }
        finally { _busy = false; }
    }

    public void Authorize(HttpRequestMessage request)
    {
        RequireToken();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
    }
    private void RequireToken()
    {
        if (_disposed || Status != DriveCode.Ready || _token is null)
            throw new DriveFailure(Status == DriveCode.Expired ? DriveCode.Expired : DriveCode.Disconnected);
    }
    public void CancelPending()
    {
        if (!_busy) return;
        _generation++;
        if (_module is not null) _clearing = ClearBrowserAsync(_module);
        // Picker cancellation is not a disconnect. Existing authorization stays
        // valid, but the old selection/callback can no longer be returned.
        if (_connecting) { _token = null; _expires = default; Set(DriveCode.Cancelled); }
        else Changed?.Invoke();
    }
    public void Disconnect()
    {
        _generation++; _token = null; _expires = default;
        if (_module is not null) _clearing = ClearBrowserAsync(_module);
        Set(DriveCode.Disconnected);
    }
    private static async Task ClearBrowserAsync(IJSObjectReference module)
    {
        try { await module.InvokeVoidAsync("disconnect"); }
        catch (JSException) { }
        catch (ObjectDisposedException) { }
    }
    private void Set(DriveCode code) { _status = code; Changed?.Invoke(); }
    private Task<IJSObjectReference> Module()
    {
        if (_disposed) throw new DriveFailure(DriveCode.Disconnected);
        // A settled failed local-glue import must not poison future explicit
        // Connect gestures. Keep pending/successful imports singly owned.
        if (_moduleImport is { IsFaulted: true } or { IsCanceled: true })
            _moduleImport = null;
        return _moduleImport ??= ImportModuleAsync();
    }
    private async Task<IJSObjectReference> ImportModuleAsync()
    {
        var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/google-drive-auth.js");
        if (_disposed)
        {
            // Import is owned even when the component leaves before it resolves.
            // No callback/Google library may be started from this late result.
            await DisposeModuleAsync(module);
            throw new DriveFailure(DriveCode.Disconnected);
        }
        _module = module;
        return module;
    }
    private static async Task DisposeModuleAsync(IJSObjectReference module)
    {
        try { await module.DisposeAsync(); }
        catch (JSException) { }
        catch (ObjectDisposedException) { }
    }
    private static DriveCode ParseCode(string code) => code switch
    {
        "offline" => DriveCode.Offline, "denied" => DriveCode.Denied,
        "popup" => DriveCode.Popup, "cancelled" => DriveCode.Cancelled,
        _ => DriveCode.Configuration
    };
    public ValueTask DisposeAsync()
    {
        if (_disposeTask is not null) return new ValueTask(_disposeTask);
        _disposed = true; Disconnect();
        _disposeTask = DisposeCoreAsync();
        return new ValueTask(_disposeTask);
    }
    private async Task DisposeCoreAsync()
    {
        await _clearing;
        if (_moduleImport is not null)
        {
            try { await _moduleImport; }
            catch (DriveFailure) { return; } // late import already disposed itself
            catch (JSException) { return; }
        }
        if (_module is not null)
        {
            await DisposeModuleAsync(_module);
            _module = null;
        }
    }
    // JS interop wire objects are not UI/application state. Never records: generated
    // record ToString would disclose access tokens. No persistence or logging here.
    public sealed class TokenReply
    {
        public string Code { get; set; } = "configuration";
        public string Token { get; set; } = "";
        public string Scope { get; set; } = "";
        public int ExpiresIn { get; set; }
        public override string ToString() => "OAuth reply [redacted]";
    }
    public sealed class SelectionReply
    {
        public string Code { get; set; } = "configuration";
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }
}
