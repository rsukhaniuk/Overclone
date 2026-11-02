using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Client.Compilation.Assets;
using Client.Compilation.Native;
using Client.RuntimeAssets;

namespace Client.Compilation.Provisioning;

/// <summary>Only technical public metadata. Never compiler approval.</summary>
public static class LocalRuntimeSelection
{
    public const int MaxPinsBytes = 262144, MaxMetadataBytes = 1048576;
    public const int SuiteMaxPinsBytes = 2 * 1024 * 1024, SuiteMaxMetadataBytes = 4 * 1024 * 1024;
    public const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalLatex2023Authoring;
    public static readonly NativeRuntimeLimits Limits = new(512, 33554432, 67108864, Profile);
    public static NativeRuntimeLimits LimitsFor(NativeRuntimeProfile profile) => profile switch
    {
        Profile or NativeRuntimeProfile.LocalLatex2023SyncTeX or NativeRuntimeProfile.LocalLatex2023Dvi => new(512, 33554432, 67108864, profile),
        NativeRuntimeProfile.LocalCompilerSuite or NativeRuntimeProfile.LocalCompilerCompatibility or NativeRuntimeProfile.LocalCompilerBibliography or NativeRuntimeProfile.LocalCompilerNavigation or NativeRuntimeProfile.LocalCompilerXeNavigation or NativeRuntimeProfile.LocalCompilerGraphicsNavigation => new(NativeCompilerSuiteContract.Files, NativeCompilerSuiteContract.FileBytes, NativeCompilerSuiteContract.TotalBytes, profile),
        _ => throw Invalid()
    };
    public static bool SupportedSuiteCount(int count) => count is > 0 and <= NativeCompilerSuiteContract.Files
        && (NativeRuntimeProfiles.SuitePinsReady(NativeRuntimeProfile.LocalCompilerSuite) && count == NativeRuntimeProfiles.InstalledCount(NativeRuntimeProfile.LocalCompilerSuite)
            || NativeRuntimeProfiles.SuitePinsReady(NativeRuntimeProfile.LocalCompilerCompatibility) && count == NativeRuntimeProfiles.InstalledCount(NativeRuntimeProfile.LocalCompilerCompatibility)
            || NativeRuntimeProfiles.SuitePinsReady(NativeRuntimeProfile.LocalCompilerBibliography) && count == NativeRuntimeProfiles.InstalledCount(NativeRuntimeProfile.LocalCompilerBibliography)
            || NativeRuntimeProfiles.SuitePinsReady(NativeRuntimeProfile.LocalCompilerNavigation) && count == NativeRuntimeProfiles.InstalledCount(NativeRuntimeProfile.LocalCompilerNavigation)
            || NativeRuntimeProfiles.SuitePinsReady(NativeRuntimeProfile.LocalCompilerXeNavigation) && count == NativeRuntimeProfiles.InstalledCount(NativeRuntimeProfile.LocalCompilerXeNavigation)
            || NativeRuntimeProfiles.SuitePinsReady(NativeRuntimeProfile.LocalCompilerGraphicsNavigation) && count == NativeRuntimeProfiles.InstalledCount(NativeRuntimeProfile.LocalCompilerGraphicsNavigation));
    public static bool SupportedCount(int count) => count is NativeRuntimeProfiles.AuthoringResourceCount or NativeRuntimeProfiles.SyncTeXResourceCount or NativeRuntimeProfiles.DviResourceCount
        || SupportedSuiteCount(count);
    public static NativeRuntimeProfile ProfileFor(IEnumerable<NativeResourcePin> pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        var owned = pins.Take(NativeCompilerSuiteContract.Files + 1).ToArray();
        if (!SupportedCount(owned.Length) || owned.Any(p => p is null)) throw Invalid();
        bool suiteCount = SupportedSuiteCount(owned.Length);
        var limits = LimitsFor(suiteCount ? NativeRuntimeProfile.LocalCompilerSuite : Profile);
        var names = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var pin in owned)
        {
            if (!NativeRuntimePackage.Name(pin.Name, limits.Profile) || !names.Add(pin.Name) || !Hex(pin.Sha256)
                || pin.Length < 0 || pin.Length > limits.MaxFileBytes || pin.Length > limits.MaxTotalBytes - total) throw Invalid();
            total += pin.Length;
        }
        var closure = NativeRuntimePackage.ResourceIdentity(owned).Hex;
        foreach (var profile in new[] { Profile, NativeRuntimeProfile.LocalLatex2023SyncTeX, NativeRuntimeProfile.LocalLatex2023Dvi, NativeRuntimeProfile.LocalCompilerSuite, NativeRuntimeProfile.LocalCompilerCompatibility, NativeRuntimeProfile.LocalCompilerBibliography, NativeRuntimeProfile.LocalCompilerNavigation, NativeRuntimeProfile.LocalCompilerXeNavigation, NativeRuntimeProfile.LocalCompilerGraphicsNavigation })
            if ((!NativeRuntimeProfiles.IsSuite(profile) || NativeRuntimeProfiles.SuitePinsReady(profile))
                && owned.Length == NativeRuntimeProfiles.InstalledCount(profile) && closure == NativeRuntimeProfiles.ClosureHash(profile)
                && (!NativeRuntimeProfiles.IsSuite(profile) || total == NativeRuntimeProfiles.SuiteTotalBytes(profile)
                    && NativeRuntimeProfiles.MatchesSuiteComponents(profile, owned.ToDictionary(p => p.Name, StringComparer.Ordinal)))) return profile;
        throw Invalid();
    }
    public static readonly RuntimeAssetBudget Budget = new(new(512, 256, 33554432, 67108864, 1048576, 256, 256), 1048576, 64, 128, 30000);
    public static readonly RuntimeAssetBudget SuiteBudget = new(new(NativeCompilerSuiteContract.Files, 256, NativeCompilerSuiteContract.FileBytes,
        NativeCompilerSuiteContract.TotalBytes, SuiteMaxMetadataBytes, 256, 256), SuiteMaxMetadataBytes, 64, 128, 30000);
    public static RuntimeAssetBudget BudgetFor(NativeRuntimeProfile profile) => NativeRuntimeProfiles.IsSuite(profile) ? SuiteBudget : Budget;
    public static ImmutableArray<NativeResourcePin> ParsePins(byte[] json)
    {
        if (json.Length > SuiteMaxPinsBytes) throw Invalid();
        using var doc = JsonDocument.Parse(json, new() { MaxDepth = 8 });
        var pins = Pins(doc.RootElement, false).Select(r => r.Pin).ToImmutableArray();
        if (!NativeRuntimeProfiles.IsSuite(ProfileFor(pins)) && json.Length > MaxPinsBytes) throw Invalid();
        return pins;
    }
    private static ImmutableArray<InstalledNativeResource> Pins(JsonElement array, bool ids)
    {
        if (array.ValueKind != JsonValueKind.Array || !SupportedCount(array.GetArrayLength())) throw Invalid();
        bool suite = SupportedSuiteCount(array.GetArrayLength());
        var limits = LimitsFor(suite ? NativeRuntimeProfile.LocalCompilerSuite : Profile);
        var result = ImmutableArray.CreateBuilder<InstalledNativeResource>(array.GetArrayLength());
        var names = new HashSet<string>(StringComparer.Ordinal); var assetIds = new HashSet<Guid>(); long total = 0;
        foreach (var item in array.EnumerateArray())
        {
            Fields(item, ids ? ["assetId", "name", "length", "sha256"] : ["name", "length", "sha256"]);
            string name = Text(item, "name", 200), hash = Text(item, "sha256", 64);
            if (!NativeRuntimePackage.Name(name, suite ? NativeRuntimeProfile.LocalCompilerSuite : Profile)
                || name == NativeRuntimePackage.NativeFormatName || !names.Add(name) || !Hex(hash)) throw Invalid();
            if (!item.GetProperty("length").TryGetInt32(out int length) || length < 0 || length > limits.MaxFileBytes
                || length > limits.MaxTotalBytes - total) throw Invalid();
            total += length;
            var id = ids ? Guid.ParseExact(Text(item, "assetId", 32), "N") : Guid.NewGuid();
            if (id == Guid.Empty || !assetIds.Add(id)) throw Invalid();
            result.Add(new(new(id), "runtime/" + name, new(name, length, hash)));
        }
        var resources = result.ToImmutable();
        var profile = ProfileFor(resources.Select(r => r.Pin));
        if (NativeRuntimeProfiles.IsSuite(profile) && (total != NativeRuntimeProfiles.SuiteTotalBytes(profile)
            || !NativeRuntimeProfiles.MatchesSuiteComponents(profile, resources.AsEnumerable().ToDictionary(r => r.Pin.Name, r => r.Pin, StringComparer.Ordinal)))) throw Invalid();
        return resources;
    }
    public static AssetInventory Inventory(ImmutableArray<InstalledNativeResource> resources)
    {
        var profile = ProfileFor(resources.Select(r => r.Pin));
        var inputs = resources.Select(r => new DeclaredAssetInput { AssetId = r.AssetId, RelativePath = r.RelativePath,
            DeclaredLength = r.Pin.Length, DeclaredHash = new(r.Pin.Sha256), Version = "local-authoring-resources-v1" }).ToArray();
        return AssetInventoryFactory.Create(new(inputs), BudgetFor(profile).Inventory).Inventory ?? throw Invalid();
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
        if (text.Length > SuiteMaxMetadataBytes || Encoding.UTF8.GetByteCount(text) > SuiteMaxMetadataBytes) throw Invalid();
        using var doc = JsonDocument.Parse(text, new() { MaxDepth = 8 }); var root = doc.RootElement;
        Fields(root, ["version", "installationId", "inventoryDigest", "profile", "resources"]);
        if (root.GetProperty("version").GetInt32() != 1) throw Invalid();
        var profile = (NativeRuntimeProfile)root.GetProperty("profile").GetInt32();
        _ = LimitsFor(profile);
        if (!NativeRuntimeProfiles.IsSuite(profile) && (text.Length > MaxMetadataBytes || Encoding.UTF8.GetByteCount(text) > MaxMetadataBytes)) throw Invalid();
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
    private static bool Hex(string? s) => s is { Length: 64 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static FormatException Invalid() => new("Select an exact supported compiler resource closure. Compiler suite is unavailable until final application pins are ready.");
}
