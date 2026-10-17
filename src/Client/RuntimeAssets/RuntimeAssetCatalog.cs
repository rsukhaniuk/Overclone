using System.Text;
using System.Text.Json;
using Client.Compilation.Assets;
using Hash = Client.Workspace.Domain.SHA256;

namespace Client.RuntimeAssets;

internal static class RuntimeAssetCatalog
{
    internal static bool HashValid(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static AssetInventory Revalidate(AssetInventory inventory, RuntimeAssetBudget budget)
    {
        if (inventory.AssetCount is < 1 || inventory.AssetCount > budget.Inventory.MaxAssets || inventory.Identity.MetadataByteLength > budget.Inventory.MaxMetadataBytes)
            throw new RuntimeAssetIoException(RuntimeAssetStatus.QuotaExceeded);
        long total = 0;
        // Preflight ALL lengths before any asset byte clone or hash.
        foreach (var a in inventory.Assets)
        {
            if (a.DeclaredLength > budget.Inventory.MaxAssetBytes || a.DeclaredLength > budget.Inventory.MaxTotalBytes - total)
                throw new RuntimeAssetIoException(RuntimeAssetStatus.QuotaExceeded);
            total += a.DeclaredLength;
        }
        var input = new AssetInventoryInput(inventory.Assets.Select(a => new DeclaredAssetInput
        {
            AssetId = a.AssetId,
            RelativePath = a.RelativePath,
            DeclaredLength = a.DeclaredLength,
            DeclaredHash = a.DeclaredHash,
            Version = a.Version,
            MediaKind = a.MediaKind,
            Sources = a.Sources,
            Notices = a.Notices
        }).ToArray());
        var creation = AssetInventoryFactory.Create(input, budget.Inventory);
        if (creation.Inventory is null || creation.Inventory.Identity != inventory.Identity)
            throw new RuntimeAssetIoException(RuntimeAssetStatus.InvalidInput);
        return creation.Inventory;
    }
    internal static string Encode(Guid stage, AssetInventory inventory, int maxBytes)
    {
        using var output = new LimitedStream(maxBytes); using (var writer = new Utf8JsonWriter(output))
        {
            JsonSerializer.Serialize(writer, new
            {
                version = 1,
                installationId = stage.ToString("N"),
                inventoryDigest = inventory.Identity.Digest.Hex,
                assets = inventory.Assets.Select(a => new
                {
                    assetId = a.AssetId.ToString(),
                    relativePath = a.RelativePath,
                    declaredLength = a.DeclaredLength,
                    hash = a.DeclaredHash.Hex,
                    version = a.Version,
                    mediaKind = a.MediaKind,
                    sources = a.Sources.Select(s => new { relation = s.Relation, targetAssetKey = s.TargetAssetKey }),
                    notices = a.Notices.Select(n => new { kind = n.Kind, referencePath = n.ReferencePath, text = n.Text })
                })
            });
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
    internal static AssetInventory Decode(string json, Guid stage, string expectedDigest, RuntimeAssetBudget budget)
    {
        if (json.Length > budget.MaxCatalogBytes || Encoding.UTF8.GetByteCount(json) > budget.MaxCatalogBytes) throw new RuntimeAssetIoException(RuntimeAssetStatus.Corrupt);
        using var document = JsonDocument.Parse(json); Unique(document.RootElement); var root = document.RootElement;
        Keys(root, "version", "installationId", "inventoryDigest", "assets");
        if (root.GetProperty("version").GetInt32() != 1 || root.GetProperty("installationId").GetString() != stage.ToString("N") || root.GetProperty("inventoryDigest").GetString() != expectedDigest)
            throw new RuntimeAssetIoException(RuntimeAssetStatus.Corrupt);
        var assets = root.GetProperty("assets");
        if (assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() is < 1 || assets.GetArrayLength() > budget.Inventory.MaxAssets) throw new RuntimeAssetIoException(RuntimeAssetStatus.Corrupt);
        var inputs = new List<DeclaredAssetInput>();
        foreach (var a in assets.EnumerateArray())
        {
            Keys(a, "assetId", "relativePath", "declaredLength", "hash", "version", "mediaKind", "sources", "notices");
            string id = a.GetProperty("assetId").GetString() ?? "";
            if (!Guid.TryParseExact(id, "N", out var guid)) throw new RuntimeAssetIoException(RuntimeAssetStatus.Corrupt);
            var sources = a.GetProperty("sources"); var notices = a.GetProperty("notices");
            if (sources.ValueKind != JsonValueKind.Array || notices.ValueKind != JsonValueKind.Array || sources.GetArrayLength() > budget.MaxCatalogBytes / 8 || notices.GetArrayLength() > budget.MaxCatalogBytes / 9)
                throw new RuntimeAssetIoException(RuntimeAssetStatus.Corrupt);
            inputs.Add(new()
            {
                AssetId = new(guid),
                RelativePath = a.GetProperty("relativePath").GetString()!,
                DeclaredLength = a.GetProperty("declaredLength").GetInt64(),
                DeclaredHash = new Hash(a.GetProperty("hash").GetString()!),
                Version = a.GetProperty("version").GetString()!,
                MediaKind = a.GetProperty("mediaKind").GetString(),
                Sources = sources.EnumerateArray().Select(s => { Keys(s, "relation", "targetAssetKey"); return new AssetSourceReference(s.GetProperty("relation").GetString()!, s.GetProperty("targetAssetKey").GetString()!); }).ToArray(),
                Notices = notices.EnumerateArray().Select(n => { Keys(n, "kind", "referencePath", "text"); return new AssetNotice(n.GetProperty("kind").GetString()!, n.GetProperty("referencePath").GetString()!, n.GetProperty("text").GetString()); }).ToArray()
            });
        }
        var result = AssetInventoryFactory.Create(new(inputs), budget.Inventory);
        if (result.Inventory is null || result.Inventory.Identity.Digest.Hex != expectedDigest) throw new RuntimeAssetIoException(RuntimeAssetStatus.Corrupt);
        return result.Inventory;
    }
    private static void Keys(JsonElement value, params string[] expected)
    { if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(expected)) throw new JsonException("Asset catalog schema."); }
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        { var names = new HashSet<string>(StringComparer.Ordinal); foreach (var p in value.EnumerateObject()) { if (!names.Add(p.Name)) throw new JsonException("Duplicate asset metadata."); Unique(p.Value); } }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
    private sealed class LimitedStream(int maximum) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        private void Check(int count) { if (count > maximum - Length) throw new RuntimeAssetIoException(RuntimeAssetStatus.QuotaExceeded); }
    }
}
