using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Pdf;
using Xunit;

namespace Workspace.Tests.Pdf;

public sealed class ActionWireTests
{
    private static readonly PdfRuntimeIdentity Runtime = new(new('a', 64), new('b', 64));
    private static PdfInspectRequest Request(bool actions = true, int output = 16777216) => new(new PdfDocument([37, 80, 68, 70]), 0, new(OutputBytes: output), raster: false, inspectActions: actions);
    private const string Cap = "{\"schemaVersion\":1,\"abiVersion\":1,\"actions\":true,\"coordinateSpace\":\"raw-default-user-space\",\"physicalMeasurement\":\"indeterminate\",\"maxLinks\":128,\"maxFieldBytesIncludingNul\":4096,\"actionExecution\":false}";
    private static JsonObject Link(uint? kind = 2, int? target = null)
    {
        JsonObject Field() => new() { ["status"] = "not-applicable", ["nativeBytes"] = null, ["value"] = null };
        var link = new JsonObject
        {
            ["index"] = 0,
            ["rectangle"] = new JsonObject { ["status"] = "available", ["raw"] = new JsonArray(0, 0, 10, 10) },
            ["action"] = new JsonObject { ["status"] = kind is null ? "absent" : "available", ["nativeType"] = kind, ["typeStatus"] = kind is null ? "absent" : kind is >= 1 and <= 5 ? "known" : "unknown" },
            ["uri"] = Field(),
            ["filePath"] = Field(),
            ["destination"] = new JsonObject { ["status"] = kind is 2 or 5 ? "not-local" : kind == 1 || kind is null ? "available" : "not-applicable", ["source"] = kind == 1 ? "action-api" : kind is null ? "link-api" : null, ["pageIndex"] = target }
        };
        if (kind is 2 or 4) link["filePath"] = new JsonObject { ["status"] = "available", ["nativeBytes"] = 11, ["value"] = "remote.pdf" };
        if (kind == 3) link["uri"] = new JsonObject { ["status"] = "available", ["nativeBytes"] = 1, ["value"] = "" };
        return link;
    }
    private static PdfWireReply Wire(PdfInspectRequest request, params JsonObject[] links)
    {
        var array = new JsonArray(); foreach (var link in links) { link["index"] = array.Count; array.Add(link); }
        return new()
        {
            Version = 2,
            RequestId = "id",
            DocumentSha256 = request.Document.Sha256,
            Epoch = 1,
            LoaderSha256 = Runtime.LoaderSha256,
            WasmSha256 = Runtime.WasmSha256,
            Status = "completed",
            NativeStatus = 0,
            EnteredNative = true,
            PageCount = 2,
            FactsJson = "{\"media\":[0,0,100,100],\"crop\":null,\"cropDefaulted\":true,\"effective\":[0,0,100,100],\"rotation\":0,\"displayPoints\":[100,100],\"nativeWidthHeight\":[100,100],\"sourceMapping\":\"unsupported\"}",
            TextJson = "{\"text\":\"\",\"utf16\":[],\"characterCount\":0,\"characters\":[],\"sourceMapping\":\"unsupported\"}",
            SearchJson = "[]",
            ActionState = "available",
            HasActionsApi = true,
            HasCapabilitiesApi = true,
            CapabilitiesJson = Cap,
            ActionsJson = new JsonObject { ["schemaVersion"] = 1, ["coordinateSpace"] = "raw-default-user-space", ["physicalMeasurement"] = "indeterminate", ["count"] = links.Length, ["links"] = array }.ToJsonString(),
            NativeCalls = ["e1_version", "e1_open", "e1_page", "e1_facts", "e1_text", "e1_capabilities", "e1_actions", "e1_reset"]
        };
    }
    private static PdfInspectionOutcome Parse(PdfWireReply w, PdfInspectRequest r) => PdfWireParser.Parse(w, r, "id", 1, Runtime);
    [Fact]
    public void Supported_zero_is_not_missing_and_default_adds_no_calls()
    {
        var r = Request(); var w = Wire(r); var ok = Parse(w, r); Assert.Equal(PdfActionAvailability.Available, ok.Actions!.Availability); Assert.Empty(ok.Actions.Links); Assert.Equal(Runtime, ok.Runtime);
        w.ActionState = "unsupported"; w.CapabilitiesJson = null; w.ActionsJson = null; w.HasActionsApi = false; w.HasCapabilitiesApi = false; w.NativeCalls = w.NativeCalls.Where(s => s is not "e1_actions" and not "e1_capabilities").ToArray();
        Assert.Equal(PdfActionAvailability.Unsupported, Parse(w, r).Actions!.Availability);
        var notRequested = Request(false); w.ActionState = "not-requested"; Assert.Equal(PdfActionAvailability.NotRequested, Parse(w, notRequested).Actions!.Availability);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Unsupported_protocol_versions_remain_rejected(int version) { var r = Request(); var w = Wire(r); w.Version = version; Assert.Equal(PdfInspectionStatus.InvalidResponse, Parse(w, r).Status); }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Forged_request_epoch_document_or_runtime_is_atomic(int field)
    {
        var r = Request(); var w = Wire(r, Link()); switch (field) { case 0: w.RequestId = "foreign"; break; case 1: w.Epoch++; break; case 2: w.DocumentSha256 = new('0', 64); break; case 3: w.LoaderSha256 = new('0', 64); break; default: w.WasmSha256 = new('0', 64); break; }
        var result = Parse(w, r); Assert.Equal(PdfInspectionStatus.InvalidResponse, result.Status); Assert.Null(result.Page); Assert.Null(result.Actions); Assert.Equal(Runtime, result.Runtime);
    }
    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(0)]
    [InlineData(3)]
    public void Nonlocal_or_unrelated_kind_cannot_publish_local_target(int kind)
    {
        var r = Request(); var link = Link((uint)kind); link["destination"] = new JsonObject { ["status"] = "available", ["source"] = "link-api", ["pageIndex"] = 0 };
        var result = Parse(Wire(r, link), r); Assert.Equal(PdfInspectionStatus.InvalidResponse, result.Status); Assert.Null(result.Actions);
    }
    [Fact]
    public void Local_and_absent_direct_destinations_are_document_bounded()
    {
        var r = Request(); var w = Wire(r, Link(1, 1), Link(null, 1)); Assert.Equal(2, Parse(w, r).Actions!.Links.Count);
        var json = JsonNode.Parse(w.ActionsJson!)!; json["links"]![0]!["destination"]!["pageIndex"] = 2; w.ActionsJson = json.ToJsonString(); Assert.Equal(PdfInspectionStatus.InvalidResponse, Parse(w, r).Status);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Malformed_capability_schema_fields_or_false_support_reject(int field)
    {
        var r = Request(); var w = Wire(r, Link(3)); var j = JsonNode.Parse(w.ActionsJson!)!;
        switch (field) { case 0: w.CapabilitiesJson = Cap.Replace("true", "false"); break; case 1: w.ActionState = "unsupported"; break; case 2: w.ActionsJson = w.ActionsJson!.Replace("\"count\":1", "\"count\":1,\"count\":1"); break; case 3: j["coordinateSpace"] = "physical-points"; break; case 4: j["links"]![0]!["uri"]!["nativeBytes"] = 4097; break; case 5: j["links"]![0]!["uri"]!["value"] = null; break; case 6: j["count"] = 129; break; default: w.NativeCalls = w.NativeCalls.Where(s => s != "e1_actions").ToArray(); break; }
        if (field is >= 3 and <= 6) w.ActionsJson = j.ToJsonString(); Assert.Equal(PdfInspectionStatus.InvalidResponse, Parse(w, r).Status);
    }
    [Fact]
    public void Field_and_count_caps_and_aggregate_output_are_not_truncation()
    {
        var r = Request(); var link = Link(3); link["uri"]!["value"] = new string('x', 4095); link["uri"]!["nativeBytes"] = 4096; Assert.Equal(PdfInspectionStatus.Completed, Parse(Wire(r, link), r).Status);
        var links = Enumerable.Range(0, 128).Select(_ => Link()).ToArray(); var w = Wire(r, links); Assert.Equal(128, Parse(w, r).Actions!.Links.Count);
        var small = Request(output: 1000); Assert.Equal(PdfInspectionStatus.InvalidResponse, Parse(Wire(small, Enumerable.Range(0, 4).Select(_ => Link()).ToArray()), small).Status);
    }
    [Fact]
    public void Available_action_result_is_owned_and_failures_have_no_previous_list()
    {
        var r = Request(); var w = Wire(r, Link()); var result = Parse(w, r); w.ActionsJson = "{}"; w.NativeCalls[5] = "changed";
        Assert.Single(result.Actions!.Links); Assert.Equal("remote.pdf", result.Actions.Links[0].FilePath.Value); Assert.Equal("e1_capabilities", result.NativeCalls![5]);
        Assert.Throws<NotSupportedException>(() => ((IList<PdfPassiveAction>)result.Actions.Links).Clear());
        Assert.Null(Parse(w, r).Actions);
        var failure = new PdfWireReply { Version = 2, RequestId = "id", DocumentSha256 = r.Document.Sha256, Epoch = 1, LoaderSha256 = Runtime.LoaderSha256, WasmSha256 = Runtime.WasmSha256, Status = "native-failure", NativeStatus = 3, EnteredNative = true };
        var bad = Parse(failure, r); Assert.Equal(PdfInspectionStatus.NativeFailure, bad.Status); Assert.Equal(3, bad.NativeStatus); Assert.Null(bad.Page); Assert.Null(bad.Actions);
    }
}
