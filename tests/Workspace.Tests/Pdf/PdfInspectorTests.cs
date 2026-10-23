using System.Buffers.Binary;
using System.Text.Json;
using Client.Pdf;
using Microsoft.JSInterop;
using Xunit;

namespace Workspace.Tests.Pdf;

// Managed contract/lifetime evidence ONLY. Actual native acceptance uses the
// independently published Blazor browser host, not these fake interop objects.
public sealed class PdfInspectorTests
{
    private static PdfRuntimePackage Package() { byte[] a = [47, 47, 32, 116], b = [0, 97, 115, 109, 1, 0, 0, 0]; return new(a, b, PdfHashes.Of(a), PdfHashes.Of(b)); }
    private sealed class Provider(PdfRuntimePackage? package) : IPdfRuntimeProvider
    { public ValueTask<PdfRuntimePackage?> GetAsync(CancellationToken ct) => ValueTask.FromResult(package); }
    private static PdfInspectRequest Request(PdfDocument? doc = null, PdfInspectionBudget? budget = null) => new(doc ?? new([37, 80, 68, 70]), 0, budget ?? new(), "І", true, 1, 1);
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task Invalid_budgets_are_rejected_before_import(int field)
    {
        var b = new PdfInspectionBudget(); b = field switch
        {
            0 => b with { InputBytes = 0 },
            1 => b with { Pages = 513 },
            2 => b with { TextUnits = 0 },
            3 => b with { SearchHits = 257 },
            4 => b with { RasterPixels = 0 },
            5 => b with { OutputBytes = 0 },
            6 => b with { LiveHandles = 65 },
            _ => b with { DeadlineMs = 30001 }
        };
        var js = new Runtime(); await using var port = new JsPdfInspector(js, new Provider(Package()));
        Assert.Equal(PdfInspectionStatus.InvalidInput, (await port.InspectAsync(Request(budget: b))).Status); Assert.Equal(0, js.Imports);
    }
    [Fact]
    public async Task Missing_runtime_never_imports_or_dispatches()
    {
        var js = new Runtime(); await using var port = new JsPdfInspector(js, new Provider(null));
        var result = await port.InspectAsync(Request()); Assert.Equal(PdfInspectionStatus.Unavailable, result.Status); Assert.Equal("RuntimeAbsent", result.Code); Assert.Equal(0, js.Imports);
    }
    [Fact]
    public async Task Wrong_runtime_digest_is_unavailable_before_import()
    {
        var package = Package(); var js = new Runtime(); await using var port = new JsPdfInspector(js, new Provider(new(package.CopyLoader(), package.CopyWasm(), new('0', 64), package.WasmSha256)));
        Assert.Equal("RuntimeMismatch", (await port.InspectAsync(Request())).Code); Assert.Equal(0, js.Imports);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Wrong_wire_version_request_document_or_epoch_cannot_publish(int field)
    {
        var js = new Runtime(); js.Module.Channel.Transform = w => { switch (field) { case 0: w.Version = 1; break; case 1: w.RequestId = "foreign"; break; case 2: w.DocumentSha256 = new('0', 64); break; default: w.Epoch++; break; } return w; };
        await using var port = new JsPdfInspector(js, new Provider(Package())); var r = await port.InspectAsync(Request()); Assert.Equal(PdfInspectionStatus.InvalidResponse, r.Status); Assert.Null(r.Page);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Malformed_native_schema_duplicates_geometry_or_raster_are_rejected(int field)
    {
        var js = new Runtime(); js.Module.Channel.Transform = w =>
        {
            switch (field)
            {
                case 0: w.FactsJson = w.FactsJson!.Replace("\"rotation\":0", "\"rotation\":0,\"rotation\":90"); break;
                case 1: w.TextJson = w.TextJson!.Replace("\"characterCount\":2", "\"characterCount\":true"); break;
                case 2: w.FactsJson = w.FactsJson!.Replace("[100,100]", "[\"NaN\",100]"); break;
                default: w.Raster![0] = 0; break;
            }
            return w;
        };
        await using var port = new JsPdfInspector(js, new Provider(Package())); Assert.Equal(PdfInspectionStatus.InvalidResponse, (await port.InspectAsync(Request())).Status);
    }
    [Fact]
    public async Task Document_and_runtime_copy_caller_arrays_before_any_await()
    {
        byte[] bytes = [37, 80, 68, 70]; var document = new PdfDocument(bytes); var expected = document.Sha256;
        var runtime = Package(); var loader = runtime.CopyLoader(); var wasm = runtime.CopyWasm(); var owned = new PdfRuntimePackage(loader, wasm, runtime.LoaderSha256, runtime.WasmSha256);
        var js = new Runtime { ImportGate = Signal<IJSObjectReference>() }; await using var port = new JsPdfInspector(js, new Provider(owned));
        var task = port.InspectAsync(Request(document)); bytes[0] = 0; loader[0] = 0; wasm[0] = 7;
        try { js.ImportGate.SetResult(js.Module); var result = await task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(PdfInspectionStatus.Completed, result.Status); Assert.Equal(expected, result.DocumentSha256); Assert.Equal(37, js.Module.Channel.Document![0]); Assert.True(owned.IsValid); }
        finally { js.ImportGate.TrySetResult(js.Module); }
    }
    [Fact]
    public async Task Completed_page_owns_raster_and_native_call_copies()
    {
        var js = new Runtime(); PdfWireReply? wire = null; js.Module.Channel.Transform = w => wire = w;
        await using var port = new JsPdfInspector(js, new Provider(Package())); var r = await port.InspectAsync(Request());
        Assert.Equal(PdfInspectionStatus.Completed, r.Status); Assert.Equal("Іі", r.Page!.Text); Assert.Equal(1030, r.Page.Utf16Units[0]); Assert.Single(r.Page.SearchHits); Assert.Equal("native/raw PDF page units", r.Page.CoordinateUnits); Assert.Null(r.Page.PhysicalSizeInPdfPoints); Assert.StartsWith("indeterminate", r.Page.PhysicalSizeStatus);
        wire!.Raster![16] = 99; wire.NativeCalls[0] = "changed"; var copy = r.Page.CopyBgra(); Assert.Equal(1, copy[0]); copy[0] = 55; Assert.Equal(1, r.Page.CopyBgra()[0]); Assert.Equal("e1_version", r.NativeCalls![0]);
    }
    [Fact]
    public async Task Native_failure_preserves_status_error_and_never_previous_page()
    {
        var js = new Runtime(); await using var port = new JsPdfInspector(js, new Provider(Package())); Assert.NotNull((await port.InspectAsync(Request())).Page);
        js.Module.Channel.Transform = w => Failure(w, "native-failure", 5, 3);
        var result = await port.InspectAsync(Request(new([0, 1, 2]))); Assert.Equal(PdfInspectionStatus.NativeFailure, result.Status); Assert.Equal(5, result.NativeStatus); Assert.Equal(3u, result.NativeError); Assert.True(result.EnteredNative); Assert.Null(result.Page);
    }
    [Fact]
    public async Task Precancelled_call_never_imports()
    {
        var js = new Runtime(); using var ct = new CancellationTokenSource(); ct.Cancel(); await using var port = new JsPdfInspector(js, new Provider(Package()));
        Assert.Equal(PdfInspectionStatus.Cancelled, (await port.InspectAsync(Request(), ct.Token)).Status); Assert.Equal(0, js.Imports);
    }
    [Fact]
    public async Task Cancel_during_import_does_not_dispatch_stale_call_or_poison_next_call()
    {
        var js = new Runtime { ImportGate = Signal<IJSObjectReference>() }; using var ct = new CancellationTokenSource(); await using var port = new JsPdfInspector(js, new Provider(Package()));
        var first = port.InspectAsync(Request(), ct.Token); ct.Cancel();
        try { Assert.Equal(PdfInspectionStatus.Cancelled, (await first.WaitAsync(TimeSpan.FromSeconds(3))).Status); js.ImportGate.SetResult(js.Module); Assert.Equal(PdfInspectionStatus.Completed, (await port.InspectAsync(Request())).Status); Assert.Equal(1, js.Module.Channel.Inspects); }
        finally { js.ImportGate.TrySetResult(js.Module); }
    }
    [Fact]
    public async Task Dispose_during_import_is_prompt_and_late_module_is_released_once()
    {
        var js = new Runtime { ImportGate = Signal<IJSObjectReference>() }; var port = new JsPdfInspector(js, new Provider(Package())); var first = port.InspectAsync(Request());
        await port.DisposeAsync(); Assert.Equal(PdfInspectionStatus.Disposed, (await first.WaitAsync(TimeSpan.FromSeconds(3))).Status);
        try { js.ImportGate.SetResult(js.Module); await js.Module.Released.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(1, js.Module.Releases); Assert.Equal(0, js.Module.Creates); Assert.Equal(PdfInspectionStatus.Disposed, (await port.InspectAsync(Request())).Status); }
        finally { js.ImportGate.TrySetResult(js.Module); await port.DisposeAsync(); }
    }
    [Fact]
    public async Task Dispose_during_channel_creation_cleans_late_channel_without_dispatch()
    {
        var js = new Runtime(); js.Module.CreateGate = Signal<IJSObjectReference>(); var port = new JsPdfInspector(js, new Provider(Package())); var first = port.InspectAsync(Request());
        await port.DisposeAsync(); Assert.Equal(PdfInspectionStatus.Disposed, (await first.WaitAsync(TimeSpan.FromSeconds(3))).Status);
        try { js.Module.CreateGate.SetResult(js.Module.Channel); await js.Module.Released.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(1, js.Module.Channel.Disposals); Assert.Equal(1, js.Module.Channel.Releases); Assert.Equal(0, js.Module.Channel.Inspects); }
        finally { js.Module.CreateGate.TrySetResult(js.Module.Channel); await port.DisposeAsync(); }
    }
    [Fact]
    public async Task Rejected_import_is_not_cached()
    {
        var js = new Runtime { FailImports = 1 }; await using var port = new JsPdfInspector(js, new Provider(Package()));
        Assert.Equal(PdfInspectionStatus.TransportError, (await port.InspectAsync(Request())).Status); Assert.Equal(PdfInspectionStatus.Completed, (await port.InspectAsync(Request())).Status); Assert.Equal(2, js.Imports);
    }
    [Fact]
    public async Task Busy_admission_is_bounded_and_cancel_settles_then_recovers()
    {
        var js = new Runtime(); js.Module.Channel.Hold = Signal<PdfWireReply>(); await using var port = new JsPdfInspector(js, new Provider(Package())); using var ct = new CancellationTokenSource();
        var first = port.InspectAsync(Request(), ct.Token);
        try
        {
            await js.Module.Channel.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(PdfInspectionStatus.Busy, (await port.InspectAsync(Request())).Status); ct.Cancel();
            Assert.Equal(PdfInspectionStatus.Cancelled, (await first.WaitAsync(TimeSpan.FromSeconds(3))).Status);
            Assert.True(js.Module.Channel.Cancels > 0, "explicit transport cancel must not be removed by inline WaitAsync cleanup");
            Assert.True(js.Module.Channel.Hold.Task.IsCompletedSuccessfully, "JS operation must settle, not only the .NET wait");
        }
        finally { js.Module.Channel.Hold.TrySetResult(Failure(js.Module.Channel.Last!, "cancelled")); }
        js.Module.Channel.Hold = null;
        Assert.Equal(PdfInspectionStatus.Completed, (await port.InspectAsync(Request())).Status); Assert.Equal(2, js.Module.Channel.Inspects);
    }

    private static PdfWireReply Failure(PdfWireReply w, string status, int? native = null, uint error = 0) => new()
    { Version = 2, LoaderSha256 = w.LoaderSha256, WasmSha256 = w.WasmSha256, RequestId = w.RequestId, DocumentSha256 = w.DocumentSha256, Epoch = w.Epoch, Status = status, NativeStatus = native, NativeError = error, EnteredNative = native.HasValue ? true : null, NativeCalls = ["e1_open", "e1_reset"] };
    private sealed class Runtime : IJSRuntime
    {
        internal readonly Module Module = new(); internal TaskCompletionSource<IJSObjectReference>? ImportGate; internal int Imports, FailImports;
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => InvokeAsync<T>(id, CancellationToken.None, args);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args)
        {
            Assert.Equal("import", id); Assert.Equal("./js/pdf-port-bridge.js", args![0]); Imports++;
            return new(Convert<T>());
        }
        private async Task<T> Convert<T>() { if (FailImports-- > 0) throw new JSException("controlled import"); return (T)(object)(ImportGate is null ? Module : await ImportGate.Task); }
    }
    private sealed class Module : IJSObjectReference
    {
        internal readonly Channel Channel = new(); internal TaskCompletionSource<IJSObjectReference>? CreateGate; internal int Creates, Releases; internal readonly TaskCompletionSource<bool> Released = Signal<bool>();
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => InvokeAsync<T>(id, CancellationToken.None, args);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args) { Assert.Equal("createPdfPort", id); Creates++; return new(Convert<T>()); }
        private async Task<T> Convert<T>() => (T)(object)(CreateGate is null ? Channel : await CreateGate.Task);
        public ValueTask DisposeAsync() { Releases++; Released.TrySetResult(true); return ValueTask.CompletedTask; }
    }
    private sealed class Channel : IJSObjectReference
    {
        internal Func<PdfWireReply, PdfWireReply>? Transform; internal TaskCompletionSource<PdfWireReply>? Hold; internal readonly TaskCompletionSource<bool> Entered = Signal<bool>();
        internal byte[]? Document; internal PdfWireReply? Last; internal int Inspects, Disposals, Releases, Cancels;
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => InvokeAsync<T>(id, CancellationToken.None, args);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args) => new(Invoke<T>(id, args));
        private async Task<T> Invoke<T>(string id, object?[]? args)
        {
            if (id == "inspect")
            {
                Inspects++; using var json = JsonDocument.Parse((string)args![0]!); var m = json.RootElement; Document = ((byte[])args[1]!).ToArray();
                byte[] raster = new byte[20]; BinaryPrimitives.WriteUInt32LittleEndian(raster, 0x31424752); for (int i = 1; i < 4; i++) BinaryPrimitives.WriteUInt32LittleEndian(raster.AsSpan(i * 4), i == 3 ? 4u : 1u); raster[16] = 1; raster[19] = 255;
                var w = new PdfWireReply
                {
                    Version = 2,
                    LoaderSha256 = m.GetProperty("loaderSha256").GetString()!,
                    WasmSha256 = m.GetProperty("wasmSha256").GetString()!,
                    ActionState = "not-requested",
                    RequestId = m.GetProperty("requestId").GetString()!,
                    DocumentSha256 = m.GetProperty("documentSha256").GetString()!,
                    Epoch = m.GetProperty("epoch").GetInt64(),
                    Status = "completed",
                    NativeStatus = 0,
                    EnteredNative = true,
                    PageCount = 1,
                    FactsJson = "{\"media\":[0,0,100,100],\"crop\":null,\"cropDefaulted\":true,\"effective\":[0,0,100,100],\"rotation\":0,\"displayPoints\":[100,100],\"nativeWidthHeight\":[100,100],\"sourceMapping\":\"unsupported\"}",
                    TextJson = "{\"text\":\"Іі\",\"utf16\":[1030,1110],\"characterCount\":2,\"characters\":[{\"index\":0,\"unicode\":1030,\"origin\":[1,2],\"box\":[1,2,3,4],\"displayBox\":[1,96,3,98],\"boxStatus\":\"available\",\"font\":\"Test\",\"fontStatus\":\"available\"},{\"index\":1,\"unicode\":1110,\"origin\":[3,2],\"box\":[3,2,5,4],\"displayBox\":[3,96,5,98],\"boxStatus\":\"available\",\"font\":\"Test\",\"fontStatus\":\"available\"}],\"sourceMapping\":\"unsupported\"}",
                    SearchJson = "[{\"start\":0,\"count\":1}]",
                    Raster = raster,
                    NativeCalls = ["e1_version", "e1_open", "e1_page", "e1_facts", "e1_text", "e1_search", "e1_raster", "e1_reset"]
                };
                Last = w; Entered.TrySetResult(true); if (Hold is not null) w = await Hold.Task; return (T)(object)(Transform?.Invoke(w) ?? w);
            }
            if (id == "cancel") { Cancels++; Hold?.TrySetResult(Failure(Last!, "cancelled")); return default!; }
            if (id == "dispose") { Disposals++; Hold?.TrySetResult(Failure(Last!, "disposed")); return default!; }
            throw new InvalidOperationException("unexpected interop operation");
        }
        public ValueTask DisposeAsync() { Releases++; return ValueTask.CompletedTask; }
    }
}
