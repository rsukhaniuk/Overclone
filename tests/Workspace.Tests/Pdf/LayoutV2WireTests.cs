using Client.Pdf;
using Client.Pdf.Preview;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Workspace.Tests.Pdf;

public sealed class LayoutV2WireTests
{
    private static string ReadFixture(string name)
    {
        using var input = typeof(LayoutV2WireTests).Assembly.GetManifestResourceStream("Workspace.Tests.Fixtures.RenderedReview." + name)!;
        using var reader = new StreamReader(input); return reader.ReadToEnd();
    }
    [Fact]
    public void GenuineNativeNonBmpTextAndSearchPreserveNativeIndexesThroughPreview()
    {
        using var fixture = JsonDocument.Parse(ReadFixture("producer-pdf-unicode-v2.json")); var f = fixture.RootElement;
        var document = new PdfDocument(Convert.FromBase64String(f.GetProperty("pdfBase64").GetString()!));
        Assert.Equal(f.GetProperty("pdfSha256").GetString(), document.Sha256);
        var runtime = new PdfRuntimeIdentity(f.GetProperty("runtime").GetProperty("loaderSha256").GetString()!, f.GetProperty("runtime").GetProperty("wasmSha256").GetString()!);
        var request = new PdfInspectRequest(document, 0, new(), search: "🙂", raster: false);
        string id = new('a', 32);
        var wire = new PdfWireReply { Version = 2, RequestId = id, DocumentSha256 = document.Sha256, Epoch = 1,
            LoaderSha256 = runtime.LoaderSha256, WasmSha256 = runtime.WasmSha256, Status = "completed", NativeStatus = 0, EnteredNative = true, PageCount = 1,
            FactsJson = f.GetProperty("facts").GetRawText(), TextJson = f.GetProperty("text").GetRawText(), SearchJson = f.GetProperty("search").GetRawText(), ActionState = "not-requested",
            NativeCalls = ["e1_version", "e1_open", "e1_page", "e1_facts", "e1_text", "e1_search", "e1_reset"] };
        var parsed = PdfWireParser.Parse(wire, request, id, 1, runtime); Assert.Equal(PdfInspectionStatus.Completed, parsed.Status);
        var page = parsed.Page!; Assert.Equal("🙂", page.Text); Assert.Equal(2, page.Utf16Units.Count);
        Assert.Equal(new[] { 0xd83d, 0xde42 }, page.Utf16Units); Assert.Equal(0x1f642, Assert.Single(page.Glyphs).UnicodeValue);
        var hit = Assert.Single(page.SearchHits); Assert.Equal(0, hit.StartCharacterIndex); Assert.Equal(1, hit.CharacterCount);
        var preview = PdfPreviewPixels.Hit(page, hit, 10); Assert.Equal("🙂", preview.GlyphText); Assert.Equal(1, preview.CharacterCount);
    }
    private static (JsonObject Capabilities, JsonObject Facts, PdfLayoutRequest Request) Fixture()
    {
        var batch = JsonNode.Parse(ReadFixture("producer-pdf-ink-v2.json"))!;
        return (batch["pages"]![0]!["capabilities"]!.AsObject(), batch["pages"]![0]!["facts"]!.AsObject(),
            new PdfLayoutRequest(new PdfDocument([37, 80, 68, 70]), 0, new PdfInspectionBudget(Pages: 512)));
    }
    [Fact]
    public void GenuineNativeV2InkRetainsExplicitRawError()
    {
        var (cap, facts, request) = Fixture(); var (parsed, measured) = PdfLayoutJson.Parse(cap.ToJsonString(), facts.ToJsonString(), request);
        Assert.Equal(2, parsed.SchemaVersion); Assert.Equal(2, measured.SchemaVersion);
        Assert.Equal("embedded-static-font-outline-opaque-fill", parsed.GlyphInkMethod);
        var a = Assert.Single(measured.Characters); Assert.Equal("available", a.InkStatus); Assert.NotNull(a.InkBox);
        Assert.True(a.InkUncertainty!.AbsoluteErrorRaw > 0); Assert.Equal("native-outline-fill-witness", a.InkUncertainty.Method);
    }
    [Theory]
    [InlineData("null")][InlineData("missing")][InlineData("negative")][InlineData("large")][InlineData("method")]
    public void AvailableInkRequiresBoundedKnownUncertainty(string corruption)
    {
        var (cap, facts, request) = Fixture(); var a = facts["characters"]![0]!.AsObject();
        switch (corruption)
        {
            case "null": a["inkUncertainty"] = null; break;
            case "missing": a.Remove("inkUncertainty"); break;
            case "negative": a["inkUncertainty"]!["absoluteErrorRaw"] = -1; break;
            case "large": a["inkUncertainty"]!["absoluteErrorRaw"] = 1000001; break;
            default: a["inkUncertainty"]!["method"] = "nominal-font-size"; break;
        }
        Assert.Throws<JsonException>(() => PdfLayoutJson.Parse(cap.ToJsonString(), facts.ToJsonString(), request));
    }
    [Fact]
    public void V1NeverSilentlyAcceptsV2FieldsAndVersionsMustMatch()
    {
        var (cap, facts, request) = Fixture(); cap["schemaVersion"] = 1; facts["schemaVersion"] = 1;
        Assert.Throws<JsonException>(() => PdfLayoutJson.Parse(cap.ToJsonString(), facts.ToJsonString(), request));
        (cap, facts, request) = Fixture(); facts["schemaVersion"] = 1;
        Assert.Throws<JsonException>(() => PdfLayoutJson.Parse(cap.ToJsonString(), facts.ToJsonString(), request));
    }
}
