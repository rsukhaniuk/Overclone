using System.Text;
using Client.Compilation;
using Client.Compilation.Native;
using Client.Compilation.SyncTeX;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;
using static Workspace.Tests.SyncTeXNavigation.NavigationFixture;

namespace Workspace.Tests.NativeOrigins;

public sealed class OriginNavigationBindingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Query_restriction_comes_from_trusted_composition_and_exact_accepted_bundle(bool spoofBundle) => Run(async () =>
    {
        var profile = NativeThesisCompilerContract.Profile;
        var identity = Client.Workspace.Domain.SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
            $"native-runtime-profile-v1\n{NativeRuntimeProfiles.IdentityName(profile)}\n{NativeRuntimeProfiles.ClosureHash(profile)}")));
        var bundle = new CompilationDescriptor("local-compiler-thesis", "1", spoofBundle ? new(new string('0', 64)) : identity);
        await using var fixture = await Open(engine: "pdftex", nativeProfile: profile, bundle: bundle);
        await fixture.Compile();
        var result = await fixture.Policy.ForwardAsync(fixture.Main, 0);
        if (spoofBundle)
        {
            Assert.Null(result);
            Assert.Empty(fixture.Query.Requests);
        }
        else
        {
            Assert.NotNull(result);
            var binding = Assert.IsType<SyncTeXRuntimeBinding>(Assert.Single(fixture.Query.Requests).RuntimeBinding);
            Assert.Equal(profile, binding.Profile); Assert.Equal(bundle, binding.Bundle);
            Assert.Equal(NativeRuntimeProfiles.SyncTeXQueryWasmHash, binding.WasmSha256);
            await fixture.Change("edit");
            Assert.False(fixture.Policy.IsCurrent(result));
            Assert.Null(await fixture.Policy.ForwardAsync(fixture.Main, 0));
            Assert.Single(fixture.Query.Requests);
        }
    });
}
