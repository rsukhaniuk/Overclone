using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Client.Compilation.Assets;
using Client.Compilation.Native;

namespace Client.Compilation.Provisioning;

/// <summary>Only technical public metadata. Never compiler approval.</summary>
public static class LocalRuntimeSelection
{
    public const int MaxPinsBytes = 262144;
    public const int MaxMetadataBytes = 1048576;
    public const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalLatex2023Authoring;
    public static readonly NativeRuntimeLimits Limits = new(512, 33554432, 67108864, Profile);
    public static NativeRuntimeLimits LimitsFor(NativeRuntimeProfile profile)
        => profile is Profile or NativeRuntimeProfile.LocalLatex2023SyncTeX or NativeRuntimeProfile.LocalLatex2023Dvi
            ? new(512, 33554432, 67108864, profile) : throw Invalid();

    public static NativeRuntimeProfile ProfileFor(IEnumerable<NativeResourcePin> pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        // Bound enumeration even for an untrusted lazy input; no arbitrary third closure.
        var owned = pins.Take(NativeRuntimeProfiles.DviResourceCount + 1).ToArray();
        if (owned.Length is not (NativeRuntimeProfiles.AuthoringResourceCount or NativeRuntimeProfiles.SyncTeXResourceCount or NativeRuntimeProfiles.DviResourceCount)
            || owned.Any(p => p is null)) throw Invalid();
        var profile = owned.Length switch
        {
            NativeRuntimeProfiles.AuthoringResourceCount => Profile,
            NativeRuntimeProfiles.SyncTeXResourceCount => NativeRuntimeProfile.LocalLatex2023SyncTeX,
            NativeRuntimeProfiles.DviResourceCount => NativeRuntimeProfile.LocalLatex2023Dvi,
            _ => throw Invalid()
        };
        if (NativeRuntimePackage.ResourceIdentity(owned).Hex != NativeRuntimeProfiles.ClosureHash(profile)) throw Invalid();
        return profile;
    }
    public static readonly Client.RuntimeAssets.RuntimeAssetBudget Budget = new(
        new(512, 256, 33554432, 67108864, 1048576, 256, 256), 1048576, 64, 128, 30000);

    public static ImmutableArray<NativeResourcePin> ParsePins(byte[] json)
    {
        if (json.Length > MaxPinsBytes) throw Invalid();
        using var doc = JsonDocument.Parse(json, new() { MaxDepth = 8 });
        return Pins(doc.RootElement, false).Select(r => r.Pin).ToImmutableArray();
    }
    private static ImmutableArray<InstalledNativeResource> Pins(JsonElement array, bool ids)
    {
        if (array.ValueKind != JsonValueKind.Array
            || array.GetArrayLength() is not (NativeRuntimeProfiles.AuthoringResourceCount or NativeRuntimeProfiles.SyncTeXResourceCount or NativeRuntimeProfiles.DviResourceCount)) throw Invalid();
        var result = ImmutableArray.CreateBuilder<InstalledNativeResource>(array.GetArrayLength());
        var names = new HashSet<string>(StringComparer.Ordinal); var assetIds = new HashSet<Guid>(); long total = 0;
        foreach (var item in array.EnumerateArray())
        {
            Fields(item, ids ? ["assetId", "name", "length", "sha256"] : ["name", "length", "sha256"]);
            string name = Text(item, "name", 200), hash = Text(item, "sha256", 64);
            if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
                || name == NativeRuntimePackage.NativeFormatName || !names.Add(name) || !Hex(hash)) throw Invalid();
            if (!item.GetProperty("length").TryGetInt32(out int length) || length < 0 || length > Limits.MaxFileBytes
                || length > Limits.MaxTotalBytes - total) throw Invalid();
            total += length;
            var id = ids ? Guid.ParseExact(Text(item, "assetId", 32), "N") : Guid.NewGuid();
            if (id == Guid.Empty || !assetIds.Add(id)) throw Invalid();
            result.Add(new(new(id), "runtime/" + name, new(name, length, hash)));
        }
        var resources = result.ToImmutable();
        _ = ProfileFor(resources.Select(r => r.Pin));
        return resources;
    }
    public static AssetInventory Inventory(ImmutableArray<InstalledNativeResource> resources)
    {
        var inputs = resources.Select(r => new DeclaredAssetInput { AssetId = r.AssetId, RelativePath = r.RelativePath,
            DeclaredLength = r.Pin.Length, DeclaredHash = new(r.Pin.Sha256), Version = "local-authoring-resources-v1" }).ToArray();
        return AssetInventoryFactory.Create(new(inputs), Budget.Inventory).Inventory ?? throw Invalid();
    }
    public static ImmutableArray<InstalledNativeResource> Resources(ImmutableArray<NativeResourcePin> pins)
        => pins.Select(p => new InstalledNativeResource(AssetId.New(), "runtime/" + p.Name, p)).ToImmutableArray();
    public static string Encode(InstalledNativeRuntimeMapping mapping)
    {
        var text = JsonSerializer.Serialize(new { version = 1, installationId = mapping.InstallationId.ToString("N"),
            inventoryDigest = mapping.InventoryDigest, profile = (int)mapping.Profile,
            resources = mapping.Resources.Select(r => new { assetId = r.AssetId.ToString(), name = r.Pin.Name, length = r.Pin.Length, sha256 = r.Pin.Sha256 }) });
        _ = Decode(text); return text;
    }
    public static InstalledNativeRuntimeMapping Decode(string text)
    {
        if (text.Length > MaxMetadataBytes || Encoding.UTF8.GetByteCount(text) > MaxMetadataBytes) throw Invalid();
        using var doc = JsonDocument.Parse(text, new() { MaxDepth = 8 }); var root = doc.RootElement;
        Fields(root, ["version", "installationId", "inventoryDigest", "profile", "resources"]);
        if (root.GetProperty("version").GetInt32() != 1) throw Invalid();
        var profile = (NativeRuntimeProfile)root.GetProperty("profile").GetInt32();
        _ = LimitsFor(profile);
        var id = Guid.ParseExact(Text(root, "installationId", 32), "N"); var digest = Text(root, "inventoryDigest", 64);
        if (id == Guid.Empty || !Hex(digest)) throw Invalid();
        var resources = Pins(root.GetProperty("resources"), true);
        if (ProfileFor(resources.Select(r => r.Pin)) != profile || Inventory(resources).Identity.Digest.Hex != digest) throw Invalid();
        return new(id, digest, resources, profile);
    }
    private static string Text(JsonElement item, string field, int max)
    { var value = item.GetProperty(field).GetString(); return value is not null && value.Length <= max ? value : throw Invalid(); }
    private static void Fields(JsonElement item, string[] fields)
    {
        if (item.ValueKind != JsonValueKind.Object) throw Invalid(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in item.EnumerateObject()) if (!fields.Contains(p.Name, StringComparer.Ordinal) || !seen.Add(p.Name)) throw Invalid();
        if (seen.Count != fields.Length) throw Invalid();
    }
    private static bool Hex(string s) => s.Length == 64 && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static FormatException Invalid() => new("Select the exact supported 497-, 499- or 507-resource pins.json and matching files.");
}
