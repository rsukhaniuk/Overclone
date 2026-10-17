using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Client.Workspace.Domain;
using Hash = Client.Workspace.Domain.SHA256;

namespace Client.Compilation.History.Index;

public static class HistoryIndexCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static bool ValidKey(HistoricalArtifactKey? key, ProjectId project) => key is not null && project.Value != Guid.Empty
        && key.ProjectId == project && key.RecordId != Guid.Empty && Hex(key.ContentDigest.Hex) && Hex(key.StorageInventoryDigest.Hex);
    private static bool Hex(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static HistoryIndexSnapshot Decode(ProjectId expected, HistoryIndexRead read, HistoryIndexBudget budget)
    {
        if (!budget.IsValid || expected.Value == Guid.Empty) throw new HistoryIndexException(HistoryIndexStatus.InvalidInput);
        try
        {
            if (!long.TryParse(read.Revision, NumberStyles.None, CultureInfo.InvariantCulture, out long token) || token < 0
                || token.ToString(CultureInfo.InvariantCulture) != read.Revision) Bad();
            if (read.Json is null)
            {
                if (token != 0) Bad();
                return new(expected, 0, []);
            }
            if (read.Json.Length > budget.MaxJsonBytes || Utf8.GetByteCount(read.Json) > budget.MaxJsonBytes)
                throw new HistoryIndexException(HistoryIndexStatus.LimitExceeded);
            using var doc = JsonDocument.Parse(read.Json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement; Keys(root, "version", "projectId", "revision", "entries");
            if (!root.GetProperty("version").TryGetInt32(out int version)) Bad();
            if (version != 1) throw new HistoryIndexException(HistoryIndexStatus.UnsupportedSchema);
            var project = Id(root.GetProperty("projectId"));
            if (project != expected.Value) throw new HistoryIndexException(HistoryIndexStatus.WrongProject);
            if (!root.GetProperty("revision").TryGetInt64(out long revision) || revision < 1 || revision != token) Bad();
            var entries = root.GetProperty("entries");
            if (entries.ValueKind != JsonValueKind.Array) Bad();
            if (entries.GetArrayLength() > budget.MaxEntries) throw new HistoryIndexException(HistoryIndexStatus.Full);
            var ids = new HashSet<Guid>(); var builder = ImmutableArray.CreateBuilder<HistoricalArtifactKey>(entries.GetArrayLength());
            foreach (var entry in entries.EnumerateArray())
            {
                Keys(entry, "recordId", "contentDigest", "storageDigest"); var id = Id(entry.GetProperty("recordId"));
                if (!ids.Add(id)) Bad();
                string? content = entry.GetProperty("contentDigest").GetString(), storage = entry.GetProperty("storageDigest").GetString();
                if (!Hex(content) || !Hex(storage)) Bad();
                builder.Add(new(id, expected, new Hash(content!), new Hash(storage!)));
            }
            return new(expected, revision, builder.MoveToImmutable());
        }
        catch (HistoryIndexException) { throw; }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or OverflowException or FormatException)
        { throw new HistoryIndexException(HistoryIndexStatus.Corrupt); }
    }
    public static string Encode(HistoryIndexSnapshot state, HistoryIndexBudget budget)
    {
        if (!budget.IsValid || state.Project.Value == Guid.Empty || state.Revision < 1 || state.Entries.IsDefault)
            throw new HistoryIndexException(HistoryIndexStatus.InvalidInput);
        if (state.Entries.Length > budget.MaxEntries) throw new HistoryIndexException(HistoryIndexStatus.Full);
        var ids = new HashSet<Guid>();
        foreach (var key in state.Entries)
            if (!ValidKey(key, state.Project) || !ids.Add(key.RecordId)) throw new HistoryIndexException(HistoryIndexStatus.InvalidInput);
        using var stream = new LimitedStream(budget.MaxJsonBytes);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteNumber("version", 1); writer.WriteString("projectId", state.Project.Value.ToString("N")); writer.WriteNumber("revision", state.Revision);
            writer.WriteStartArray("entries");
            foreach (var key in state.Entries)
            {
                writer.WriteStartObject(); writer.WriteString("recordId", key.RecordId.ToString("N")); writer.WriteString("contentDigest", key.ContentDigest.Hex);
                writer.WriteString("storageDigest", key.StorageInventoryDigest.Hex); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return Utf8.GetString(stream.ToArray());
    }
    private static Guid Id(JsonElement element)
    {
        string? value = element.GetString();
        if (!Guid.TryParseExact(value, "N", out Guid id) || id == Guid.Empty || value != id.ToString("N")) Bad();
        return id;
    }
    private static void Keys(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) Bad();
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) if (!expected.Contains(property.Name, StringComparer.Ordinal) || !found.Add(property.Name)) Bad();
        if (found.Count != expected.Length) Bad();
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Bad() => throw new HistoryIndexException(HistoryIndexStatus.Corrupt);
    private sealed class LimitedStream(int maximum) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        private void Check(int count) { if (count > maximum - Length) throw new HistoryIndexException(HistoryIndexStatus.LimitExceeded); }
    }
}
