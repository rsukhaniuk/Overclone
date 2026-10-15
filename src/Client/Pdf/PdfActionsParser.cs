using System.Text;
using System.Text.Json;

namespace Client.Pdf;

internal static class PdfActionsParser
{
    private static void Require(bool condition) { if (!condition) throw new JsonException("Passive action schema/semantics."); }
    private static string Text(JsonElement value) => value.GetString() ?? throw new JsonException();
    internal static PdfActionInspection Parse(PdfWireReply wire, PdfInspectRequest request)
    {
        bool called = wire.NativeCalls.Contains("e1_capabilities") || wire.NativeCalls.Contains("e1_actions");
        if (!request.InspectActions)
        {
            Require(wire.ActionState == "not-requested" && !called && !wire.HasCapabilitiesApi && !wire.HasActionsApi && wire.CapabilitiesJson is null && wire.ActionsJson is null);
            return new(PdfActionAvailability.NotRequested, null, []);
        }
        if (wire.ActionState == "unsupported")
        {
            Require((!wire.HasActionsApi || !wire.HasCapabilitiesApi) && !called && wire.CapabilitiesJson is null && wire.ActionsJson is null);
            return new(PdfActionAvailability.Unsupported, null, []);
        }
        Require(wire.ActionState == "available" && wire.HasActionsApi && wire.HasCapabilitiesApi && wire.NativeCalls.Contains("e1_capabilities") && wire.NativeCalls.Contains("e1_actions") && wire.CapabilitiesJson is not null && wire.ActionsJson is not null);
        using var cap = JsonDocument.Parse(wire.CapabilitiesJson!); using var data = JsonDocument.Parse(wire.ActionsJson!);
        PdfWireParser.Unique(cap.RootElement); PdfWireParser.Unique(data.RootElement);
        var c = cap.RootElement; PdfWireParser.Keys(c, "schemaVersion", "abiVersion", "actions", "coordinateSpace", "physicalMeasurement", "maxLinks", "maxFieldBytesIncludingNul", "actionExecution");
        Require(c.GetProperty("schemaVersion").GetInt32() == 1 && c.GetProperty("abiVersion").GetInt32() == 1 && c.GetProperty("actions").GetBoolean() && !c.GetProperty("actionExecution").GetBoolean() && c.GetProperty("maxLinks").GetInt32() == 128 && c.GetProperty("maxFieldBytesIncludingNul").GetInt32() == 4096);
        Units(c); var d = data.RootElement; PdfWireParser.Keys(d, "schemaVersion", "coordinateSpace", "physicalMeasurement", "count", "links"); Units(d);
        Require(d.GetProperty("schemaVersion").GetInt32() == 1); int count = d.GetProperty("count").GetInt32(); var array = d.GetProperty("links");
        Require(count is >= 0 and <= 128 && array.ValueKind == JsonValueKind.Array && array.GetArrayLength() == count);
        var links = new PdfPassiveAction[count]; int i = 0;
        foreach (var link in array.EnumerateArray())
        {
            PdfWireParser.Keys(link, "index", "rectangle", "action", "uri", "filePath", "destination"); Require(link.GetProperty("index").GetInt32() == i);
            var rect = link.GetProperty("rectangle"); PdfWireParser.Keys(rect, "status", "raw"); string rectStatus = Text(rect.GetProperty("status")); var raw = rect.GetProperty("raw");
            Require(rectStatus is "available" or "invalid" or "unavailable"); Require((rectStatus == "available") == (raw.ValueKind != JsonValueKind.Null));
            PdfBox? box = raw.ValueKind == JsonValueKind.Null ? null : PdfWireParser.Box(raw);
            var action = link.GetProperty("action"); PdfWireParser.Keys(action, "status", "nativeType", "typeStatus");
            string status = Text(action.GetProperty("status")), typeStatus = Text(action.GetProperty("typeStatus")); var type = action.GetProperty("nativeType");
            uint? kind = type.ValueKind == JsonValueKind.Null ? null : type.GetUInt32();
            Require(status == (kind is null ? "absent" : "available") && typeStatus == (kind is null ? "absent" : kind is >= 1 and <= 5 ? "known" : "unknown"));
            var uri = Field(link.GetProperty("uri"), kind == 3); var file = Field(link.GetProperty("filePath"), kind is 2 or 4);
            var target = link.GetProperty("destination"); PdfWireParser.Keys(target, "status", "source", "pageIndex");
            string destStatus = Text(target.GetProperty("status")); string? source = target.GetProperty("source").GetString(); var page = target.GetProperty("pageIndex"); int? pageIndex = page.ValueKind == JsonValueKind.Null ? null : page.GetInt32();
            if (kind is 2 or 5) Require(destStatus == "not-local" && source is null && pageIndex is null);
            else if (kind == 1) Require(destStatus is "available" or "unavailable" or "invalid" && source == "action-api");
            else if (kind is null) Require(destStatus == "not-applicable" && source is null || destStatus is "available" or "invalid" && source == "link-api");
            else Require(destStatus == "not-applicable" && source is null && pageIndex is null);
            Require(destStatus == "available" ? pageIndex is >= 0 && pageIndex < wire.PageCount : pageIndex is null);
            links[i] = new(i, rectStatus, box, status, kind, typeStatus, uri, file, new(destStatus, source, pageIndex)); i++;
        }
        return new(PdfActionAvailability.Available, new(1, 128, 4096, "raw-default-user-space", "indeterminate", false), links);
    }
    private static void Units(JsonElement value) => Require(Text(value.GetProperty("coordinateSpace")) == "raw-default-user-space" && Text(value.GetProperty("physicalMeasurement")) == "indeterminate");
    private static PdfActionField Field(JsonElement value, bool applicable)
    {
        PdfWireParser.Keys(value, "status", "nativeBytes", "value"); string status = Text(value.GetProperty("status")); var b = value.GetProperty("nativeBytes"); int? bytes = b.ValueKind == JsonValueKind.Null ? null : b.GetInt32(); string? text = value.GetProperty("value").GetString();
        if (!applicable) Require(status == "not-applicable" && bytes is null && text is null);
        else
        {
            Require(bytes is >= 0 and <= 4096);
            if (status == "available") Require(text is not null && PdfHashes.ValidUtf16(text) && Encoding.UTF8.GetByteCount(text) + 1 == bytes);
            else Require(text is null && (status == "unavailable" && bytes == 0 || status is "invalid-utf8" or "invalid-native-string" && bytes > 0));
        }
        return new(status, bytes, text);
    }
}
