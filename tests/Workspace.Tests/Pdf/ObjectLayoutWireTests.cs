using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Pdf;
using Microsoft.JSInterop;
using Xunit;

namespace Workspace.Tests.Pdf;

public sealed class ObjectLayoutWireTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonElement Capture = LoadCapture();

    [Fact]
    public async Task Genuine_worker_capture_retains_exact_fact_bytes_and_correlated_identity()
    {
        var js = new Interop();
        var package = Package();
        await using var inspector = new JsPdfInspector(js, new Provider(package));
        var request = Request();
        var result = await inspector.InspectObjectLayoutAsync(request);

        Assert.True(js.Request.GetProperty("inspectObjectLayout").GetBoolean());
        Assert.True(js.Request.GetProperty("inspectLayout").GetBoolean());
        Assert.False(js.Request.GetProperty("raster").GetBoolean());
        Assert.Equal(request.Document.Sha256, Capture.GetProperty("pdfSha256").GetString());
        var producer = ProducerReply();
        Assert.Equal("f95ae827b12703999ce1daafebbf6830f5f4d2c14bb7805a8f47abe54772b079", producer.LoaderSha256);
        Assert.Equal("2e2126ead8e00ef7c79d21e3836d66957b19e827523f7c054ca887f3c11ae5b7", producer.WasmSha256);
        Assert.Equal(PdfInspectionStatus.Completed, result.Status);
        Assert.Equal(js.Request.GetProperty("requestId").GetString(), result.RequestId);
        Assert.Equal(js.Request.GetProperty("epoch").GetInt64(), result.WorkerEpoch);
        Assert.Equal(request.Document.Sha256, result.DocumentSha256);
        Assert.Equal(new PdfRuntimeIdentity(package.LoaderSha256, package.WasmSha256), result.Runtime);
        Assert.Equal(0, result.PageIndex);

        var objects = Assert.IsType<PdfObjectLayoutFacts>(result.Facts);
        var text = Assert.IsType<PdfLayoutFacts>(result.LayoutFacts);
        Assert.Equal(producer.ObjectLayoutFactsJson, objects.Json);
        Assert.Equal(Hash(producer.ObjectLayoutFactsJson!), objects.Sha256);
        Assert.Equal(producer.LayoutFactsJson, text.Json);
        Assert.Equal(Hash(producer.LayoutFactsJson!), result.LayoutFactsSha256);
        Assert.Equal(objects.Effective, text.Effective);
        Assert.Equal(2, objects.PageCount);
        Assert.Equal(4, objects.DirectObjectCount);
        Assert.Equal(new PdfDirectObjectCounts(1, 1, 2, 0, 0, 0), objects.ObjectTypeCounts);
        Assert.Collection(objects.Images,
            image =>
            {
                Assert.Equal(1, image.ObjectIndex);
                Assert.Equal("available", image.Status);
                Assert.Equal(new PdfImageMatrix(120, 0, 0, 120, 30, 40), image.Matrix);
                Assert.Equal(new PdfBox(30, 40, 150, 160), image.RawBox);
            },
            image =>
            {
                Assert.Equal(3, image.ObjectIndex);
                Assert.Equal("available", image.Status);
                Assert.Equal(new PdfImageQuad(new(148, 38), new(73, 66), new(91, 131), new(166, 103)), image.Quad);
                Assert.Equal(new PdfBox(73, 38, 166, 131), image.RawBox);
            });
    }

    [Fact]
    public async Task Object_only_and_older_layout_callers_remain_explicit_and_independent()
    {
        var js = new Interop { Modify = StripText };
        await using var inspector = new JsPdfInspector(js, new Provider(Package()));
        var request = Request();
        var result = await inspector.InspectObjectLayoutAsync(request with { IncludeTextLayout = false });
        Assert.True(js.Request.GetProperty("inspectObjectLayout").GetBoolean());
        Assert.False(js.Request.GetProperty("inspectLayout").GetBoolean());
        Assert.Equal(PdfInspectionStatus.Completed, result.Status);
        Assert.NotNull(result.Facts);
        Assert.Null(result.LayoutFacts);
        Assert.Null(result.LayoutFactsSha256);

        js.Modify = wire =>
        {
            wire.ObjectLayoutState = null;
            wire.ObjectLayoutFactsJson = null;
            wire.NativeCalls = wire.NativeCalls[..8].Append("e1_reset").ToArray();
        };
        var layout = await inspector.InspectLayoutAsync(new(request.Document, request.PageIndex, request.Budget));
        Assert.False(js.Request.TryGetProperty("inspectObjectLayout", out _));
        Assert.Equal(PdfInspectionStatus.Completed, layout.Status);
        Assert.Equal(ProducerReply().LayoutFactsJson, layout.Facts!.Json);

        js.Modify = _ => { };
        Assert.Equal(PdfInspectionStatus.InvalidResponse,
            (await inspector.InspectLayoutAsync(new(request.Document, request.PageIndex, request.Budget))).Status);
    }

    [Fact]
    public async Task Unsupported_preflight_returns_no_partial_layout_or_object_facts()
    {
        var js = new Interop { Modify = wire =>
        {
            StripText(wire);
            wire.ObjectLayoutState = "unsupported";
            wire.ObjectLayoutFactsJson = null;
            wire.NativeCalls = wire.NativeCalls[..4].Append("e1_reset").ToArray();
        }};
        await using var inspector = new JsPdfInspector(js, new Provider(Package()));
        var result = await inspector.InspectObjectLayoutAsync(Request());
        Assert.Equal(PdfInspectionStatus.Unavailable, result.Status);
        Assert.Equal("ObjectLayoutApiAbsent", result.Code);
        Assert.Null(result.Facts);
        Assert.Null(result.LayoutCapabilities);
        Assert.Null(result.LayoutFacts);
        Assert.Null(result.LayoutFactsSha256);
    }

    [Fact]
    public async Task Aggregate_cap_is_counted_in_utf8_across_all_three_responses()
    {
        var js = new Interop();
        await using var inspector = new JsPdfInspector(js, new Provider(Package()));
        var producer = ProducerReply();
        int bytes = Encoding.UTF8.GetByteCount(producer.ObjectLayoutFactsJson!)
            + Encoding.UTF8.GetByteCount(producer.LayoutFactsJson!)
            + Encoding.UTF8.GetByteCount(producer.LayoutCapabilitiesJson!);
        var request = Request();
        Assert.Equal(PdfInspectionStatus.Completed, (await inspector.InspectObjectLayoutAsync(
            request with { Budget = request.Budget with { OutputBytes = bytes } })).Status);
        var refused = await inspector.InspectObjectLayoutAsync(
            request with { Budget = request.Budget with { OutputBytes = bytes - 1 } });
        Assert.Equal(PdfInspectionStatus.InvalidResponse, refused.Status);
        Assert.Null(refused.Facts);
        Assert.Null(refused.LayoutFacts);
    }

    [Theory]
    [InlineData("wrong-request")]
    [InlineData("wrong-epoch")]
    [InlineData("wrong-pdf")]
    [InlineData("wrong-artifact")]
    [InlineData("wrong-calls")]
    [InlineData("wrong-frame")]
    [InlineData("wrong-unit")]
    [InlineData("layout-bom")]
    [InlineData("wrong-page-count")]
    [InlineData("wrong-page")]
    [InlineData("unrequested-raster")]
    [InlineData("omitted-text")]
    [InlineData("unsupported-with-facts")]
    [InlineData("failure-with-facts")]
    public async Task Failure_to_correlate_with_exact_request_page_and_calls_disables_the_entire_pair(string alteration)
    {
        var js = new Interop { Modify = wire =>
        {
            switch (alteration)
            {
                case "wrong-request": wire.RequestId = Guid.NewGuid().ToString("N"); break;
                case "wrong-epoch": wire.Epoch++; break;
                case "wrong-pdf": wire.DocumentSha256 = new('0', 64); break;
                case "wrong-artifact": wire.WasmSha256 = new('0', 64); break;
                case "wrong-calls": wire.NativeCalls[7] = "e1_reset"; break;
                case "wrong-frame":
                    EditObjects(wire, o =>
                    {
                        o["media"] = new JsonArray(0, 0, 221, 221);
                        o["effective"] = new JsonArray(0, 0, 221, 221);
                    });
                    break;
                case "wrong-unit":
                    EditObjects(wire, o => o["userUnit"] = new JsonObject
                        { ["status"] = "available", ["value"] = 2, ["method"] = "page-dictionary-userunit" });
                    break;
                case "layout-bom": wire.LayoutFactsJson = "\uFEFF" + wire.LayoutFactsJson; break;
                case "wrong-page-count": EditObjects(wire, o => o["pageCount"] = 3); break;
                case "wrong-page": EditObjects(wire, o => o["pageIndex"] = 1); break;
                case "unrequested-raster": wire.Raster = [0]; break;
                case "omitted-text": StripText(wire); break;
                case "unsupported-with-facts": wire.ObjectLayoutState = "unsupported"; break;
                case "failure-with-facts": wire.Status = "output-limit"; break;
            }
        }};
        await AssertRefused(js);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("malformed")]
    [InlineData("bom")]
    [InlineData("surrogate")]
    [InlineData("unknown-member")]
    [InlineData("old-schema")]
    [InlineData("incomplete")]
    [InlineData("object-cap")]
    [InlineData("type-count")]
    [InlineData("duplicate-ordinal")]
    [InlineData("out-of-order")]
    [InlineData("unknown-reason")]
    [InlineData("nonfinite")]
    [InlineData("quad")]
    [InlineData("bounds")]
    [InlineData("uncertainty")]
    public async Task Malformed_or_inconsistent_object_facts_cannot_publish_even_with_valid_text(string alteration)
    {
        var js = new Interop { Modify = wire =>
        {
            var json = wire.ObjectLayoutFactsJson!;
            wire.ObjectLayoutFactsJson = alteration switch
            {
                "duplicate" => "{\"schemaVersion\":1," + json[1..],
                "malformed" => json[..^1],
                "bom" => "\uFEFF" + json,
                "surrogate" => json + "\uD800",
                "nonfinite" => json.Replace("[120,0,0,120,30,40]", "[1e999,0,0,120,30,40]", StringComparison.Ordinal),
                _ => json
            };
            if (wire.ObjectLayoutFactsJson != json) return;
            EditObjects(wire, o =>
            {
                var images = o["images"]!.AsArray();
                var first = images[0]!.AsObject();
                switch (alteration)
                {
                    case "unknown-member": o["visible"] = true; break;
                    case "old-schema": o["schemaVersion"] = 0; break;
                    case "incomplete": o["scanComplete"] = false; break;
                    case "object-cap": o["directObjectCount"] = 10001; break;
                    case "type-count": o["objectTypeCounts"]!.AsObject()["image"] = 1; break;
                    case "duplicate-ordinal": images[1]!["objectIndex"] = 1; break;
                    case "out-of-order": images[1]!["objectIndex"] = 0; break;
                    case "unknown-reason": first["reason"] = "claimed-visible"; break;
                    case "quad": first["quad"]![0] = new JsonArray(30, 41); break;
                    case "bounds": first["rawBox"]![3] = 161; break;
                    case "uncertainty": first["uncertainty"]!.AsObject()["absoluteErrorRaw"] = 0; break;
                }
            });
        }};
        await AssertRefused(js);
    }

    private static async Task AssertRefused(Interop js)
    {
        await using var inspector = new JsPdfInspector(js, new Provider(Package()));
        var result = await inspector.InspectObjectLayoutAsync(Request());
        Assert.Equal(PdfInspectionStatus.InvalidResponse, result.Status);
        Assert.Null(result.Facts);
        Assert.Null(result.LayoutCapabilities);
        Assert.Null(result.LayoutFacts);
        Assert.Null(result.LayoutFactsSha256);
    }

    private static void StripText(PdfWireReply wire)
    {
        wire.LayoutState = null;
        wire.LayoutCapabilitiesJson = null;
        wire.LayoutFactsJson = null;
        wire.NativeCalls = [.. wire.NativeCalls[..4], .. wire.NativeCalls[8..]];
    }

    private static void EditObjects(PdfWireReply wire, Action<JsonObject> edit)
    {
        var json = JsonNode.Parse(wire.ObjectLayoutFactsJson!)!.AsObject();
        edit(json);
        wire.ObjectLayoutFactsJson = json.ToJsonString();
    }

    private static PdfObjectLayoutRequest Request() =>
        new(new(Convert.FromBase64String(Capture.GetProperty("pdfBase64").GetString()!)), 0, new(), IncludeTextLayout: true);
    private static PdfWireReply ProducerReply() =>
        Capture.GetProperty("nativeWorker").Deserialize<PdfWireReply>(WebJson)!;
    private static string Hash(string value) => PdfHashes.Of(Encoding.UTF8.GetBytes(value));

    private static JsonElement LoadCapture()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".producer-object-layout-v1.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.Clone();
    }

    private static PdfRuntimePackage Package()
    {
        byte[] loader = [47, 47, 32, 116];
        byte[] wasm = [0, 97, 115, 109, 1, 0, 0, 0];
        return new(loader, wasm, PdfHashes.Of(loader), PdfHashes.Of(wasm));
    }

    private sealed class Provider(PdfRuntimePackage package) : IPdfRuntimeProvider
    {
        public ValueTask<PdfRuntimePackage?> GetAsync(CancellationToken cancellationToken) => ValueTask.FromResult<PdfRuntimePackage?>(package);
    }

    // Rebind the recorded native worker envelope to the managed call's identity.
    // Native payload strings and call traces come from the genuine producer.
    private sealed class Interop : IJSRuntime, IJSObjectReference
    {
        internal JsonElement Request { get; private set; }
        internal Action<PdfWireReply>? Modify { get; set; }

        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => InvokeAsync<T>(id, CancellationToken.None, args);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken cancellationToken, object?[]? args)
        {
            if (id is "import" or "createPdfPort") return ValueTask.FromResult((T)(object)this);
            if (id == "dispose") return ValueTask.FromResult(default(T)!);
            if (id != "inspect") throw new InvalidOperationException("Unexpected PDF interop call.");
            using var frame = JsonDocument.Parse((string)args![0]!);
            Request = frame.RootElement.Clone();
            var wire = ProducerReply();
            wire.RequestId = Request.GetProperty("requestId").GetString()!;
            wire.DocumentSha256 = Request.GetProperty("documentSha256").GetString()!;
            wire.Epoch = Request.GetProperty("epoch").GetInt64();
            wire.LoaderSha256 = Request.GetProperty("loaderSha256").GetString()!;
            wire.WasmSha256 = Request.GetProperty("wasmSha256").GetString()!;
            Modify?.Invoke(wire);
            return ValueTask.FromResult((T)(object)wire);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
