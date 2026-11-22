using System.Buffers.Binary;
using System.Text.Json;
using Client.Pdf;
using Client.Pdf.Preview;

namespace Workspace.Tests.PdfPreview;

// CONTRACT-ONLY synthetic data via the REAL existing wire parser. Not PDF/native evidence.
internal sealed class PreviewInspector : IPdfInspector
{
    internal readonly List<PdfInspectRequest> Calls = [];
    internal Func<PdfInspectRequest, long, Task<PdfInspectionOutcome>>? Handler;
    internal int Active, PeakActive, Disposes, PageCountOverride = 3;
    private long _epoch;
    public async Task<PdfInspectionOutcome> InspectAsync(PdfInspectRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add(request); Active++; PeakActive = Math.Max(PeakActive, Active); long epoch = ++_epoch;
        try { return Handler is null ? Page(request, epoch, PageCountOverride) : await Handler(request, epoch); }
        finally { Active--; }
    }
    public ValueTask DisposeAsync() { Disposes++; return ValueTask.CompletedTask; }
    internal static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static PdfDocument Document(byte marker = 1) => new([37, 80, 68, 70, marker]);
    internal static PdfInspectionOutcome Page(PdfInspectRequest request, long epoch, int pageCount = 3, bool missingBox = false, bool rotate = false,
        int? rotationDegrees = null)
    {
        string id = Guid.NewGuid().ToString("N"); var runtime = new PdfRuntimeIdentity(new('a', 64), new('b', 64));
        int rotation = rotationDegrees ?? (rotate ? 90 : 0);
        var display = rotation is 90 or 270 ? new[] { 200, 100 } : new[] { 100, 200 };
        var chars = new[] { 1030, 128512, 120 }.Select((scalar, index) => new
        {
            index, unicode = scalar, origin = new[] { 10 + index * 20, 20 }, box = missingBox && index == 1 ? null : new[] { 10 + index * 20, 20, 20 + index * 20, 30 },
            displayBox = missingBox && index == 1 ? null : new[] { 10 + index * 20, 20, 20 + index * 20, 30 },
            boxStatus = missingBox && index == 1 ? "indeterminate" : "available", font = "ContractFont", fontStatus = "available"
        }).ToArray();
        object[] hits = request.Search is "І" ? [new { start = 0, count = 1 }] : request.Search is "😀" ? [new { start = 1, count = 1 }] : [];
        byte[]? raster = null;
        if (request.Raster)
        {
            raster = new byte[checked(16 + request.RasterWidth * request.RasterHeight * 4)];
            BinaryPrimitives.WriteUInt32LittleEndian(raster, 0x31424752);
            BinaryPrimitives.WriteUInt32LittleEndian(raster.AsSpan(4), (uint)request.RasterWidth);
            BinaryPrimitives.WriteUInt32LittleEndian(raster.AsSpan(8), (uint)request.RasterHeight);
            BinaryPrimitives.WriteUInt32LittleEndian(raster.AsSpan(12), (uint)request.RasterWidth * 4);
            for (int i = 16; i < raster.Length; i += 4) { raster[i] = 3; raster[i + 1] = 7; raster[i + 2] = 200; raster[i + 3] = 99; }
        }
        var wire = new PdfWireReply
        {
            Version = 2, RequestId = id, DocumentSha256 = request.Document.Sha256, Epoch = epoch,
            LoaderSha256 = runtime.LoaderSha256, WasmSha256 = runtime.WasmSha256, Status = "completed", NativeStatus = 0, EnteredNative = true,
            PageCount = pageCount, ActionState = request.InspectActions ? "unsupported" : "not-requested",
            FactsJson = JsonSerializer.Serialize(new { media = new[] { 0, 0, 100, 200 }, crop = (int[]?)null, cropDefaulted = true, effective = new[] { 0, 0, 100, 200 }, rotation, displayPoints = display, nativeWidthHeight = display, sourceMapping = "unsupported" }),
            TextJson = JsonSerializer.Serialize(new { text = "І😀x", utf16 = new[] { 1030, 55357, 56832, 120 }, characterCount = 3, characters = chars, sourceMapping = "unsupported" }),
            SearchJson = JsonSerializer.Serialize(hits), Raster = raster,
            NativeCalls = ["e1_version", "e1_open", "e1_page", "e1_facts", "e1_text", "e1_search", "e1_raster", "e1_reset"]
        };
        return PdfWireParser.Parse(wire, request, id, epoch, runtime);
    }
    internal static async Task<PdfPreviewFrame> Frame(int generation = 1)
    {
        await using var controller = new PdfPreviewController(new PreviewInspector()); controller.Provide(Document());
        await controller.WhenSettled.WaitAsync(TimeSpan.FromSeconds(3));
        for (int i = 1; i < generation; i++) { controller.Zoom(100 + i); await controller.WhenSettled.WaitAsync(TimeSpan.FromSeconds(3)); }
        return controller.State.Frame ?? throw new InvalidOperationException(controller.State.Detail);
    }
}
