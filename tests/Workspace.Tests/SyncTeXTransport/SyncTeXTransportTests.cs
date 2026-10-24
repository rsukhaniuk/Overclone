using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Client.Compilation.SyncTeX;
using Microsoft.JSInterop;
using Xunit;

namespace Workspace.Tests.SyncTeXTransport;

// Fake interop validates the real managed transport, NOT WASM/browser acceptance.
public sealed class SyncTeXTransportTests
{
    private const string Valid = "{\"schema\":1,\"direction\":\"forward\",\"coordinateSpace\":\"synctex-visible-bp-top-left\",\"retainedCount\":1,\"truncated\":false,\"matches\":[{\"file\":\"розділ/🙂.tex\",\"tag\":1,\"line\":99,\"column\":null,\"page\":1,\"point\":{\"h\":10,\"v\":20},\"box\":{\"h\":10,\"v\":20,\"width\":3,\"height\":4,\"depth\":0}}]}";
    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    private static SyncTeXRuntimePackage Package()
    {
        byte[] loader = Encoding.UTF8.GetBytes("// trusted fixture"), wasm = [0, 97, 115, 109, 1, 0, 0, 0];
        return new(loader, wasm, Hash(loader), Hash(wasm));
    }
    private static SyncTeXQueryRequest Request()
    {
        var map = Encoding.UTF8.GetBytes("SyncTeX Version:1\nsynthetic; parsing belongs to native\n");
        return new(map, Hash(map), SyncTeXDirection.Forward, "розділ/🙂.tex", 100);
    }
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Provider(Func<CancellationToken, ValueTask<SyncTeXRuntimePackage?>> get) : ISyncTeXRuntimeProvider
    {
        internal int Calls;
        public ValueTask<SyncTeXRuntimePackage?> GetAsync(CancellationToken token) { Calls++; return get(token); }
    }
    private static Provider Ready() => new(_ => ValueTask.FromResult<SyncTeXRuntimePackage?>(Package()));
    private sealed class Reference : IJSObjectReference
    {
        internal Func<string, object?[]?, Task<object?>> Call = (_, _) => Task.FromResult<object?>(null);
        internal int Releases;
        internal readonly TaskCompletionSource<bool> Released = Signal<bool>();
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => InvokeAsync<T>(id, CancellationToken.None, args);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken token, object?[]? args) => new(Convert<T>(id, args));
        private async Task<T> Convert<T>(string id, object?[]? args) { var result = await Call(id, args); return result is null ? default! : (T)result; }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Releases); Released.TrySetResult(true); return ValueTask.CompletedTask; }
    }
    private sealed class Runtime : IJSRuntime
    {
        internal readonly Reference Module = new(), Channel = new();
        internal readonly TaskCompletionSource<bool> Imported = Signal<bool>(), Entered = Signal<bool>();
        internal TaskCompletionSource<IJSObjectReference>? ImportHold, CreateHold;
        internal TaskCompletionSource<SyncTeXWireReply>? QueryHold;
        internal Func<SyncTeXWireReply, SyncTeXWireReply>? Transform;
        internal TaskCompletionSource<object?>? CancelHold, DisposeHold;
        internal int Imports, Creates, Queries, Cancels, Disposals;
        internal byte[]? Map;
        internal string? Id;
        internal Runtime()
        {
            Module.Call = async (id, _) =>
            {
                Assert.Equal("createSyncTeXQueryPort", id); Creates++;
                return CreateHold is null ? Channel : await CreateHold.Task;
            };
            Channel.Call = async (id, args) =>
            {
                if (id == "query")
                {
                    Queries++; using var metadata = JsonDocument.Parse((string)args![0]!);
                    Id = metadata.RootElement.GetProperty("requestId").GetString();
                    Assert.Equal(15000, metadata.RootElement.GetProperty("deadlineMs").GetInt32());
                    Map = ((byte[])args[1]!).ToArray();
                    var wire = new SyncTeXWireReply { Version = 1, RequestId = Id, Status = "native", NativeStatus = 0, Json = Encoding.UTF8.GetBytes(Valid) };
                    var hold = QueryHold;
                    Entered.TrySetResult(true);
                    if (hold is not null) wire = await hold.Task;
                    return Transform?.Invoke(wire) ?? wire;
                }
                if (id == "cancel")
                {
                    Assert.Equal(Id, args![0]); Cancels++;
                    return CancelHold is null ? null : await CancelHold.Task;
                }
                if (id == "dispose") { Disposals++; return DisposeHold is null ? null : await DisposeHold.Task; }
                throw new InvalidOperationException("unexpected interop call");
            };
        }
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => InvokeAsync<T>(id, CancellationToken.None, args);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken token, object?[]? args) => new(Import<T>(id, args));
        private async Task<T> Import<T>(string id, object?[]? args)
        {
            Assert.Equal("import", id); Assert.Equal("./js/synctex-query-bridge.js", args![0]);
            Imports++; Imported.TrySetResult(true);
            return (T)(object)(ImportHold is null ? Module : await ImportHold.Task);
        }
    }
    [Fact]
    public async Task Copies_map_before_await_and_returns_owned_read_only_results()
    {
        var js = new Runtime { ImportHold = Signal<IJSObjectReference>() };
        await using var query = new JsSyncTeXQuery(js, Ready());
        var request = Request(); var expected = request.Map.ToArray();
        byte[]? response = null;
        js.Transform = w => { response = w.Json; return w; };
        var task = query.QueryAsync(request); request.Map[0] = 0;
        await js.Imported.Task.WaitAsync(TimeSpan.FromSeconds(3)); js.ImportHold.SetResult(js.Module);
        var result = await task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(SyncTeXQueryStatus.Completed, result.Status); Assert.Equal(expected, js.Map);
        Assert.Equal(99, result.Matches[0].Line); Assert.Equal("розділ/🙂.tex", result.Matches[0].File);
        response![0] = 0;
        Assert.Throws<NotSupportedException>(() => ((IList<SyncTeXMatch>)result.Matches).Clear());
        Assert.Equal(20, result.Matches[0].Box.V); Assert.Equal(1, js.Disposals); Assert.Equal(1, js.Module.Releases);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)] [InlineData(12)] [InlineData(13)]
    [InlineData(14)] [InlineData(15)] [InlineData(16)] [InlineData(17)] [InlineData(18)]
    public async Task Invalid_requests_never_call_provider_or_import(int variant)
    {
        var request = Request();
        request = variant switch
        {
            0 => request with { Map = [] }, 1 => request with { Map = new byte[1048576] },
            2 => request with { MapSha256 = request.MapSha256.ToUpperInvariant() }, 3 => request with { MapSha256 = new('0', 64) },
            4 => request with { File = "../main.tex" }, 5 => request with { File = "/main.tex" },
            6 => request with { File = "a\\b.tex" }, 7 => request with { File = "a\u0085.tex" },
            8 => request with { File = "a\ud800.tex" }, 9 => request with { File = new('я', 513) },
            10 => request with { Line = 0 }, 11 => request with { Page = 513 },
            12 => request with { H = double.NaN }, 13 => request with { V = double.PositiveInfinity },
            14 => request with { H = 1000001 }, 15 => request with { Direction = (SyncTeXDirection)99 },
            16 => request with { Direction = SyncTeXDirection.Inverse, File = null, Line = 0, Page = 0 },
            17 => request with { File = "a//b.tex" }, _ => request with { File = "C:main.tex" }
        };
        var js = new Runtime(); var provider = Ready(); await using var query = new JsSyncTeXQuery(js, provider);
        Assert.Equal(SyncTeXQueryStatus.InvalidInput, (await query.QueryAsync(request)).Status);
        Assert.Equal(0, provider.Calls); Assert.Equal(0, js.Imports);
    }
    [Fact]
    public async Task Native_owns_canonical_map_gate_and_absolute_result_names_remain_data()
    {
        var js = new Runtime(); js.Transform = w => { w.Json = Encoding.UTF8.GetBytes(Valid.Replace("розділ/🙂.tex", "/private/absolute.tex")); return w; };
        await using var query = new JsSyncTeXQuery(js, Ready());
        byte[] bytes = [0, 1, 2]; var request = Request() with { Map = bytes, MapSha256 = Hash(bytes) };
        var result = await query.QueryAsync(request);
        Assert.Equal(SyncTeXQueryStatus.Completed, result.Status); Assert.Equal("/private/absolute.tex", result.Matches[0].File);
        Assert.Equal(bytes, js.Map); // Mock accepts it: production native rejects invalid magic/NUL.
    }
    public static IEnumerable<object[]> BadJson()
    {
        yield return ["{}"];
        yield return [Valid.Replace("\"schema\":1", "\"schema\":1,\"schema\":1")];
        yield return [Valid.Replace("\"schema\":1", "\"schema\":2")];
        yield return [Valid.Replace("forward", "inverse")];
        yield return [Valid.Replace("synctex-visible-bp-top-left", "dom-pixels")];
        yield return [Valid.Replace("\"retainedCount\":1", "\"retainedCount\":0")];
        yield return [Valid.Replace("\"retainedCount\":1", "\"retainedCount\":2")];
        yield return [Valid.Replace("\"retainedCount\":1", "\"retainedCount\":33")];
        yield return [Valid.Replace("\"truncated\":false", "\"truncated\":1")];
        yield return [Valid.Replace("\"tag\":1", "\"tag\":0")];
        yield return [Valid.Replace("\"line\":99", "\"line\":2147483648")];
        yield return [Valid.Replace("\"column\":null", "\"column\":0")];
        yield return [Valid.Replace("\"page\":1", "\"page\":513")];
        yield return [Valid.Replace("\"h\":10", "\"h\":1e309")];
        yield return [Valid.Replace("\"depth\":0", "\"depth\":-1")];
        yield return [Valid.Replace("розділ/🙂.tex", "bad\\u0000name")];
        yield return [Valid.Replace("розділ/🙂.tex", new string('я', 513))];
        yield return [Valid.Replace("\"depth\":0", "\"depth\":0,\"unknown\":1")];
        yield return [Valid.Replace("розділ/🙂.tex", "bad\\uD800name")];
    }
    [Theory, MemberData(nameof(BadJson))]
    public async Task Rejects_untrusted_json_structure_utf8_names_counts_and_geometry(string json)
    {
        var js = new Runtime(); js.Transform = w => { w.Json = Encoding.UTF8.GetBytes(json); return w; };
        await using var query = new JsSyncTeXQuery(js, Ready());
        var result = await query.QueryAsync(Request()); Assert.Equal(SyncTeXQueryStatus.InvalidResponse, result.Status); Assert.Empty(result.Matches);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public async Task Rejects_malformed_wire_and_native_status(int variant)
    {
        var js = new Runtime(); js.Transform = w =>
        {
            switch (variant)
            {
                case 0: w.Version = 2; break; case 1: w.RequestId = new('a', 32); break;
                case 2: w.NativeStatus = -9; break; case 3: w.NativeStatus = null; break;
                case 4: w.Json = new byte[32769]; break; case 5: w.Json = [0xff]; break;
                default: w.NativeStatus = 1; break; // Empty MUST NOT have output.
            }
            return w;
        };
        await using var query = new JsSyncTeXQuery(js, Ready());
        Assert.Equal(SyncTeXQueryStatus.InvalidResponse, (await query.QueryAsync(Request())).Status);
    }
    [Theory]
    [InlineData(1, SyncTeXQueryStatus.NoMatches)] [InlineData(-1, SyncTeXQueryStatus.Failed)] [InlineData(-8, SyncTeXQueryStatus.Failed)]
    public async Task Native_empty_and_error_are_fixed_codes_without_results(int status, SyncTeXQueryStatus expected)
    {
        var js = new Runtime(); js.Transform = w => { w.NativeStatus = status; w.Json = null; return w; };
        await using var query = new JsSyncTeXQuery(js, Ready()); var result = await query.QueryAsync(Request());
        Assert.Equal(expected, result.Status); Assert.Empty(result.Matches); Assert.DoesNotContain("private", result.Code);
    }
    [Fact]
    public async Task Missing_mismatched_and_throwing_providers_do_not_import()
    {
        var js = new Runtime(); var package = Package();
        Provider[] providers = [new(_ => ValueTask.FromResult<SyncTeXRuntimePackage?>(null)),
            new(_ => ValueTask.FromResult<SyncTeXRuntimePackage?>(new(package.CopyLoader(), package.CopyWasm(), new('0', 64), package.WasmSha256))),
            new(_ => throw new InvalidOperationException("SECRET provider text"))];
        foreach (var provider in providers)
        {
            await using var query = new JsSyncTeXQuery(js, provider); var result = await query.QueryAsync(Request());
            Assert.Contains(result.Status, new[] { SyncTeXQueryStatus.Unavailable, SyncTeXQueryStatus.Failed });
            Assert.DoesNotContain("SECRET", result.Code);
        }
        Assert.Equal(0, js.Imports);
    }
    [Fact]
    public async Task Cancelled_noncooperative_provider_holds_admission_until_actual_settle()
    {
        var hold = Signal<SyncTeXRuntimePackage?>(); var entered = Signal<bool>();
        var provider = new Provider(_ => { entered.TrySetResult(true); return new(hold.Task); });
        var js = new Runtime(); await using var query = new JsSyncTeXQuery(js, provider); using var ct = new CancellationTokenSource();
        var first = query.QueryAsync(Request(), ct.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); ct.Cancel();
        Assert.Equal(SyncTeXQueryStatus.Cancelled, (await first.WaitAsync(TimeSpan.FromSeconds(3))).Status);
        Assert.Equal(SyncTeXQueryStatus.Busy, (await query.QueryAsync(Request())).Status);
        hold.SetResult(Package());
        Assert.Equal(SyncTeXQueryStatus.Completed, (await Retry(query)).Status); Assert.Equal(1, js.Imports);
    }
    [Fact]
    public async Task Cancelled_import_holds_admission_and_late_module_never_creates_worker_port()
    {
        var js = new Runtime { ImportHold = Signal<IJSObjectReference>() };
        await using var query = new JsSyncTeXQuery(js, Ready()); using var ct = new CancellationTokenSource();
        var first = query.QueryAsync(Request(), ct.Token); await js.Imported.Task.WaitAsync(TimeSpan.FromSeconds(3)); ct.Cancel();
        Assert.Equal(SyncTeXQueryStatus.Cancelled, (await first.WaitAsync(TimeSpan.FromSeconds(3))).Status);
        Assert.Equal(SyncTeXQueryStatus.Busy, (await query.QueryAsync(Request())).Status);
        js.ImportHold.SetResult(js.Module); await js.Module.Released.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, js.Creates); Assert.Equal(0, js.Queries);
    }
    [Fact]
    public async Task Cancelled_noncooperative_query_and_cancel_interop_both_hold_admission()
    {
        var js = new Runtime { QueryHold = Signal<SyncTeXWireReply>(), CancelHold = Signal<object?>() };
        await using var query = new JsSyncTeXQuery(js, Ready()); using var ct = new CancellationTokenSource();
        var first = query.QueryAsync(Request(), ct.Token); await js.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); ct.Cancel();
        Assert.Equal(SyncTeXQueryStatus.Cancelled, (await first.WaitAsync(TimeSpan.FromSeconds(3))).Status);
        await js.Module.Released.Task.WaitAsync(TimeSpan.FromSeconds(3)); // disposal did not wait for noncooperative cancel/query
        Assert.Equal(1, js.Cancels); Assert.Equal(1, js.Disposals);
        Assert.Equal(SyncTeXQueryStatus.Busy, (await query.QueryAsync(Request())).Status);
        js.QueryHold.SetResult(new() { Version = 1, RequestId = js.Id, Status = "native", NativeStatus = 0, Json = Encoding.UTF8.GetBytes(Valid) });
        Assert.Equal(SyncTeXQueryStatus.Busy, (await query.QueryAsync(Request())).Status); // cancel still unretired
        js.CancelHold.SetResult(null); js.QueryHold = null;
        Assert.Equal(SyncTeXQueryStatus.Completed, (await Retry(query)).Status);
    }
    [Fact]
    public async Task Disposal_is_prompt_during_channel_creation_and_late_port_is_closed()
    {
        var js = new Runtime { CreateHold = Signal<IJSObjectReference>() };
        var query = new JsSyncTeXQuery(js, Ready()); var first = query.QueryAsync(Request());
        await js.Imported.Task.WaitAsync(TimeSpan.FromSeconds(3));
        // Ensure creation has started (the asynchronous import continuation is separately scheduled).
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (Volatile.Read(ref js.Creates) == 0) await Task.Delay(1, timeout.Token);
        await query.DisposeAsync(); Assert.Equal(SyncTeXQueryStatus.Disposed, (await first.WaitAsync(TimeSpan.FromSeconds(3))).Status);
        js.CreateHold.SetResult(js.Channel); await js.Module.Released.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, js.Queries); Assert.Equal(1, js.Disposals); Assert.Equal(1, js.Channel.Releases);
        Assert.Equal(SyncTeXQueryStatus.Disposed, (await query.QueryAsync(Request())).Status);
    }
    [Fact]
    public async Task Noncooperative_dispose_interop_keeps_admission_after_public_cancellation()
    {
        var js = new Runtime { QueryHold = Signal<SyncTeXWireReply>(), DisposeHold = Signal<object?>() };
        await using var query = new JsSyncTeXQuery(js, Ready()); using var ct = new CancellationTokenSource();
        var first = query.QueryAsync(Request(), ct.Token); await js.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); ct.Cancel();
        Assert.Equal(SyncTeXQueryStatus.Cancelled, (await first.WaitAsync(TimeSpan.FromSeconds(3))).Status);
        js.QueryHold.SetException(new JSException("SECRET late interop fault"));
        Assert.Equal(SyncTeXQueryStatus.Busy, (await query.QueryAsync(Request())).Status);
        js.DisposeHold.SetResult(null); js.QueryHold = null;
        Assert.Equal(SyncTeXQueryStatus.Completed, (await Retry(query)).Status);
    }
    [Fact]
    public async Task Throwing_channel_creation_and_cleanup_never_expose_exception_text()
    {
        var js = new Runtime(); js.Module.Call = (_, _) => throw new JSException("SECRET create fault");
        await using var query = new JsSyncTeXQuery(js, Ready());
        var failed = await query.QueryAsync(Request());
        Assert.Equal(SyncTeXQueryStatus.Failed, failed.Status); Assert.Equal("TransportFailed", failed.Code);
        Assert.Equal(1, js.Module.Releases); Assert.Equal(0, js.Queries);
        js.Module.Call = (_, _) => Task.FromResult<object?>(js.Channel);
        js.Channel.Call = (_, _) => throw new InvalidOperationException("SECRET query/dispose fault");
        failed = await query.QueryAsync(Request());
        Assert.Equal(SyncTeXQueryStatus.Failed, failed.Status); Assert.Equal("TransportFailed", failed.Code);
        Assert.Equal(1, js.Channel.Releases); Assert.Equal(2, js.Module.Releases);
    }
    [Fact]
    public async Task Inverse_and_full_32_match_truncated_output_are_accepted_without_reinterpretation()
    {
        using var json = JsonDocument.Parse(Valid);
        string match = json.RootElement.GetProperty("matches")[0].GetRawText().Replace("\"column\":null", "\"column\":3");
        string resultJson = "{\"schema\":1,\"direction\":\"inverse\",\"coordinateSpace\":\"synctex-visible-bp-top-left\",\"retainedCount\":32,\"truncated\":true,\"matches\":[" + string.Join(",", Enumerable.Repeat(match, 32)) + "]}";
        var js = new Runtime(); js.Transform = w => { w.Json = Encoding.UTF8.GetBytes(resultJson); return w; };
        await using var query = new JsSyncTeXQuery(js, Ready());
        var result = await query.QueryAsync(Request() with { Direction = SyncTeXDirection.Inverse, File = null, Line = 0, Page = 512, H = -1000000, V = 1000000 });
        Assert.Equal(SyncTeXQueryStatus.Completed, result.Status); Assert.Equal(32, result.Matches.Count);
        Assert.True(result.Truncated); Assert.Equal(3, result.Matches[31].Column);
    }
    [Fact]
    public async Task Precancelled_request_never_calls_provider_or_imports()
    {
        var js = new Runtime(); var provider = Ready(); await using var query = new JsSyncTeXQuery(js, provider);
        using var ct = new CancellationTokenSource(); ct.Cancel();
        Assert.Equal(SyncTeXQueryStatus.Cancelled, (await query.QueryAsync(Request(), ct.Token)).Status);
        Assert.Equal(0, provider.Calls); Assert.Equal(0, js.Imports);
    }
    [Fact]
    public async Task Deadline_returns_with_noncooperative_provider_and_keeps_admission()
    {
        var hold = Signal<SyncTeXRuntimePackage?>();
        await using var query = new JsSyncTeXQuery(new Runtime(), new Provider(_ => new(hold.Task)));
        var result = await query.QueryAsync(Request()).WaitAsync(TimeSpan.FromSeconds(18));
        Assert.Equal(SyncTeXQueryStatus.TimedOut, result.Status);
        Assert.Equal(SyncTeXQueryStatus.Busy, (await query.QueryAsync(Request())).Status);
        hold.SetException(new InvalidOperationException("SECRET late provider fault"));
    }
    private static async Task<SyncTeXQueryOutcome> Retry(JsSyncTeXQuery query)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (true)
        {
            var result = await query.QueryAsync(Request());
            if (result.Status != SyncTeXQueryStatus.Busy) return result;
            await Task.Delay(1, timeout.Token);
        }
    }
}
