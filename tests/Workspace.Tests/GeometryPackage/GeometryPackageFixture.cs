using System.Text.Json.Nodes;

namespace Workspace.Tests;

// Exact actual Rust public-entry JSON, embedded without reserialization or a
// second copy in C# source. Reuses the existing immutable capture/rebind helper.
internal static class GeometryPackageFixture
{
    internal static KnuFixture Load(string name)
    {
        using var stream = typeof(GeometryPackageFixture).Assembly.GetManifestResourceStream(
            "Workspace.Tests.Fixtures.GeometryPackage." + name + ".json")
            ?? throw new InvalidOperationException("Missing Rust margin fixture.");
        var data = (JsonObject)JsonNode.Parse(stream)!;
        var fixture = KnuFixture.Load("negative");
        fixture.Data.Clear();
        foreach (var pair in data) fixture.Data.Add(pair.Key, pair.Value?.DeepClone());
        return fixture;
    }
}
