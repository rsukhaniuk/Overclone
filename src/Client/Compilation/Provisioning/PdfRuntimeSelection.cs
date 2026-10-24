using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Client.Compilation.Assets;
using Client.RuntimeAssets;

namespace Client.Compilation.Provisioning;

public sealed record PdfResourceSelection(string Role, AssetId AssetId, string Name, int Length, string Sha256);
/// <summary>User-declared integrity metadata. NOT trusted executable provenance or runtime adoption.</summary>
public sealed record PdfResourceInstallation(Guid InstallationId, string InventoryDigest, ImmutableArray<PdfResourceSelection> Resources)
{
    public InstalledPdfRuntimeMapping TechnicalMapping()
    {
        var loader = Resources.Single(r => r.Role == "loader"); var wasm = Resources.Single(r => r.Role == "wasm");
        return new(InstallationId, InventoryDigest, loader.AssetId, loader.Length, loader.Sha256, wasm.AssetId, wasm.Length, wasm.Sha256);
    }
}
public static class PdfRuntimeSelection
{
    public const int MaxManifestBytes = 65536, MaxMetadataBytes = 16384;
    public const int MaxLoaderBytes = 1048576, MaxWasmBytes = 16777216, MaxTotalBytes = 17825792;
    public const int PreparationDeadlineMs = 30000;
    public static readonly RuntimeAssetBudget Budget = new(new(2, 256, MaxWasmBytes, MaxTotalBytes, 16384, 256, 256), 65536, 64, 128, 30000);
    public static ImmutableArray<PdfResourceSelection> ParseManifest(byte[] json)
    {
        if (json is null || json.Length > MaxManifestBytes) throw Invalid();
        using var doc = JsonDocument.Parse(json, new() { MaxDepth = 8 }); var root = doc.RootElement;
        Fields(root, ["version", "resources"]);
        if (root.GetProperty("version").GetInt32() != 1) throw Invalid();
        return Resources(root.GetProperty("resources"), false);
    }
    private static ImmutableArray<PdfResourceSelection> Resources(JsonElement array, bool ids)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != 2) throw Invalid();
        var result = ImmutableArray.CreateBuilder<PdfResourceSelection>(2);
        var roles = new HashSet<string>(StringComparer.Ordinal); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var assetIds = new HashSet<Guid>();
        long total = 0;
        foreach (var item in array.EnumerateArray())
        {
            Fields(item, ids ? ["role", "assetId", "name", "length", "sha256"] : ["role", "name", "length", "sha256"]);
            string role = Text(item, "role", 6), name = Text(item, "name", 200), hash = Text(item, "sha256", 64);
            if (role is not "loader" and not "wasm" || !roles.Add(role) || name is "" or "." or ".."
                || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') || !names.Add(name) || !Hex(hash)) throw Invalid();
            if (!item.GetProperty("length").TryGetInt32(out int length)
                || length < (role == "loader" ? 1 : 8) || length > (role == "loader" ? MaxLoaderBytes : MaxWasmBytes)
                || length > MaxTotalBytes - total) throw Invalid();
            total += length;
            var id = ids ? Guid.ParseExact(Text(item, "assetId", 32), "N") : Guid.NewGuid();
            if (id == Guid.Empty || !assetIds.Add(id)) throw Invalid();
            result.Add(new(role, new(id), name, length, hash));
        }
        return result.ToImmutable();
    }
    public static string Path(PdfResourceSelection resource) => "pdf-runtime/" + resource.Role + "/" + resource.Name;
    public static AssetInventory Inventory(ImmutableArray<PdfResourceSelection> resources)
    {
        var entries = resources.Select(r => new DeclaredAssetInput { AssetId = r.AssetId, RelativePath = Path(r), DeclaredLength = r.Length,
            DeclaredHash = new(r.Sha256), Version = "local-pdf-resources-v1" }).ToArray();
        return AssetInventoryFactory.Create(new(entries), Budget.Inventory).Inventory ?? throw Invalid();
    }
    public static string Encode(PdfResourceInstallation selection)
    {
        string text = JsonSerializer.Serialize(new { version = 1, kind = "pdf-resources", installationId = selection.InstallationId.ToString("N"),
            inventoryDigest = selection.InventoryDigest, resources = selection.Resources.Select(r => new { role = r.Role, assetId = r.AssetId.ToString(), name = r.Name, length = r.Length, sha256 = r.Sha256 }) });
        _ = Decode(text); return text;
    }
    public static PdfResourceInstallation Decode(string text)
    {
        if (text is null || text.Length > MaxMetadataBytes || Encoding.UTF8.GetByteCount(text) > MaxMetadataBytes) throw Invalid();
        using var doc = JsonDocument.Parse(text, new() { MaxDepth = 8 }); var root = doc.RootElement;
        Fields(root, ["version", "kind", "installationId", "inventoryDigest", "resources"]);
        if (root.GetProperty("version").GetInt32() != 1 || Text(root, "kind", 16) != "pdf-resources") throw Invalid();
        var id = Guid.ParseExact(Text(root, "installationId", 32), "N"); var digest = Text(root, "inventoryDigest", 64);
        if (id == Guid.Empty || !Hex(digest)) throw Invalid();
        var resources = Resources(root.GetProperty("resources"), true);
        if (Inventory(resources).Identity.Digest.Hex != digest) throw Invalid();
        return new(id, digest, resources);
    }
    private static string Text(JsonElement item, string field, int max)
    { var text = item.GetProperty(field).GetString(); return text is not null && text.Length <= max ? text : throw Invalid(); }
    private static void Fields(JsonElement item, string[] fields)
    {
        if (item.ValueKind != JsonValueKind.Object) throw Invalid(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in item.EnumerateObject()) if (!fields.Contains(field.Name, StringComparer.Ordinal) || !seen.Add(field.Name)) throw Invalid();
        if (seen.Count != fields.Length) throw Invalid();
    }
    private static bool Hex(string text) => text.Length == 64 && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static FormatException Invalid() => new("Select a version 1 PDF resource manifest and its matching loader and WASM files.");
}
