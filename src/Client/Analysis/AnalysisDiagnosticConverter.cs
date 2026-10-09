using System.Text.Json;
using System.Text.Json.Serialization;

namespace Client.Analysis;

/// <summary>Rust emits a null primaryLocation for KNU diagnostics. Preserve it
/// without changing legacy engine diagnostic omission behavior or global null
/// policy. Read validation remains in the shared codec.</summary>
internal sealed class AnalysisDiagnosticConverter : JsonConverter<AnalysisEngineDiagnosticDto>
{
    public override AnalysisEngineDiagnosticDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var e = doc.RootElement;
        string S(string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? throw new JsonException("Null diagnostic text.") : throw new JsonException("Missing diagnostic text.");
        var span = e.TryGetProperty("span", out var s) && s.ValueKind != JsonValueKind.Null ? s.Deserialize<AnalysisSpanDto>(options) : null;
        var location = e.TryGetProperty("primaryLocation", out var l) && l.ValueKind != JsonValueKind.Null ? l.Deserialize<AnalysisLocationDto>(options) : null;
        return new(S("id"), S("ruleId"), S("origin"), S("severity"), S("fileId"), span, S("message"), location);
    }
    public override void Write(Utf8JsonWriter writer, AnalysisEngineDiagnosticDto value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id); writer.WriteString("ruleId", value.RuleId);
        writer.WriteString("origin", value.Origin); writer.WriteString("severity", value.Severity);
        writer.WriteString("fileId", value.FileId);
        if (value.Span is not null || value.Origin == "knu") { writer.WritePropertyName("span"); JsonSerializer.Serialize(writer, value.Span, options); }
        writer.WriteString("message", value.Message);
        if (value.PrimaryLocation is not null || value.Origin == "knu") { writer.WritePropertyName("primaryLocation"); JsonSerializer.Serialize(writer, value.PrimaryLocation, options); }
        writer.WriteEndObject();
    }
}
