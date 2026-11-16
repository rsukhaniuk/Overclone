using Client.Pdf;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Workspace.Tests.Pdf;

public sealed class LayoutWireTests
{
    private static readonly PdfInspectionBudget Budget = new(Pages: 512);
    private static readonly PdfDocument Document = new([37, 80, 68, 70]);
    private const string Capabilities = """
        {"schemaVersion":1,"coordinateSpace":"pdf-raw-unrotated-bottom-left","physicalScale":"available","glyphInk":"unsupported","textStyle":"unsupported","lineGrouping":"unsupported","maxCharacters":100000,"nominalSizeUnit":"raw-user-units","nominalSizeMethod":"pdfium-font-size"}
        """;
    private const string Facts = """
        {"schemaVersion":1,"pageIndex":0,"pageCount":1,"media":[0,0,72,144],"crop":null,"effective":[0,0,72,144],"rotation":0,"userUnit":{"status":"available","value":2,"method":"page-dictionary-userunit"},"characters":[{"index":0,"unicode":128578,"origin":[10,20],"advance":null,"matrix":[1,0,0,1,10,20],"nominalSize":12,"fontName":"Тест","fontFlags":32,"renderMode":0,"inkBox":null,"inkStatus":"unsupported","styleStatus":"unsupported"}],"complete":true,"limitations":["glyph-ink-not-measured"]}
        """;
    [Fact]
    public void ParsedPhysicalScaleDoesNotTurnNominalFontIntoInk()
    {
        var (capabilities, facts) = PdfLayoutJson.Parse(Capabilities, Facts, new(Document, 0, Budget));
        Assert.Equal(2, facts.UserUnit.Value); Assert.Equal("unsupported", capabilities.GlyphInk);
        Assert.Equal(128578, facts.Characters[0].Unicode); Assert.Null(facts.Characters[0].InkBox);
        Assert.Equal("Тест", facts.Characters[0].FontName); Assert.Equal(Facts, facts.Json);
    }
    [Theory]
    [InlineData("complete", "false")]
    [InlineData("pageIndex", "1")]
    [InlineData("rotation", "45")]
    [InlineData("unexpected", "true")]
    public void InvalidPageShapesAreRejected(string property, string value)
    {
        var json = JsonNode.Parse(Facts)!; json[property] = JsonNode.Parse(value);
        Assert.Throws<JsonException>(() => PdfLayoutJson.Parse(Capabilities, json.ToJsonString(), new(Document, 0, Budget)));
    }
    [Fact]
    public void RejectsDuplicateFieldsSurrogatesAndUnprovenInk()
    {
        Assert.Throws<JsonException>(() => PdfLayoutJson.Parse(Capabilities, Facts.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"), new(Document, 0, Budget)));
        var json = JsonNode.Parse(Facts)!; json["characters"]![0]!["unicode"] = 0xd800;
        Assert.Throws<JsonException>(() => PdfLayoutJson.Parse(Capabilities, json.ToJsonString(), new(Document, 0, Budget)));
        json = JsonNode.Parse(Facts)!; json["characters"]![0]!["inkStatus"] = "available";
        json["characters"]![0]!["inkBox"] = JsonNode.Parse("[0,0,1,1]");
        Assert.Throws<JsonException>(() => PdfLayoutJson.Parse(Capabilities, json.ToJsonString(), new(Document, 0, Budget)));
    }
    [Fact]
    public void LayoutReplyIsBoundToActualRequestRuntimeAndEpoch()
    {
        var request = new PdfInspectRequest(Document, 0, Budget, raster: false, rasterWidth: 1, rasterHeight: 1, inspectLayout: true);
        var runtime = new PdfRuntimeIdentity(new('a', 64), new('b', 64)); string id = new('c', 32);
        var wire = new PdfWireReply { Version = 2, RequestId = id, Epoch = 5, DocumentSha256 = Document.Sha256,
            LoaderSha256 = runtime.LoaderSha256, WasmSha256 = runtime.WasmSha256, Status = "completed", NativeStatus = 0,
            EnteredNative = true, PageCount = 1, LayoutState = "available", LayoutCapabilitiesJson = Capabilities, LayoutFactsJson = Facts,
            NativeCalls = ["e1_version", "e1_open", "e1_page", "e1_layout_capabilities", "e1_layout_facts", "e1_reset"] };
        var valid = PdfWireParser.Parse(wire, request, id, 5, runtime);
        Assert.Equal(PdfInspectionStatus.Completed, valid.Status); Assert.Equal(5, valid.WorkerEpoch); Assert.NotNull(valid.Layout?.Facts);
        wire.Epoch = 4; Assert.Equal(PdfInspectionStatus.InvalidResponse, PdfWireParser.Parse(wire, request, id, 5, runtime).Status);
    }
    [Fact]
    public void MissingLayoutExportsLeaveLegacyPreviewUsable()
    {
        var request = new PdfInspectRequest(Document, 0, Budget, raster: false, rasterWidth: 1, rasterHeight: 1, inspectLayout: true);
        var runtime = new PdfRuntimeIdentity(new('a', 64), new('b', 64)); string id = new('c', 32);
        var wire = new PdfWireReply { Version = 2, RequestId = id, Epoch = 1, DocumentSha256 = Document.Sha256,
            LoaderSha256 = runtime.LoaderSha256, WasmSha256 = runtime.WasmSha256, Status = "completed", NativeStatus = 0,
            EnteredNative = true, PageCount = 1, LayoutState = "unsupported", NativeCalls = ["e1_version", "e1_open", "e1_page", "e1_reset"] };
        Assert.Equal(PdfInspectionStatus.Unavailable, PdfWireParser.Parse(wire, request, id, 1, runtime).Status);
    }
}
