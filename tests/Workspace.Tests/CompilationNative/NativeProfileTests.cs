using System.Collections;
using Client.Compilation.Native;
using Xunit;

namespace Workspace.Tests.CompilationNative;

public sealed class NativeProfileTests
{
    [Fact]
    public void Legacy_defaults_constants_and_caps_remain_unchanged()
    {
        Assert.Equal(NativeRuntimeProfile.Legacy, new NativeRuntimeLimits(512, 16777216, 33554432).Profile);
        Assert.True(new NativeRuntimeLimits(512, 16777216, 33554432).IsValid);
        Assert.False(new NativeRuntimeLimits(512, 16777217, 33554432).IsValid);
        Assert.False(new NativeRuntimeLimits(512, 16777216, 33554433).IsValid);
        Assert.Equal(378, InstalledNativeRuntimeProvider.ResourceCount);
        Assert.Equal(715877, NativeRuntimeProfiles.FormatLength(NativeRuntimeProfile.Legacy));
        Assert.Equal("99c77a3e291d0a74bb62152e3a377f848296d442e204afc41108130ac3df8c51", NativeRuntimePackage.FormatHash);
        Assert.False(new NativeCompilePlan(false, 2, TimeSpan.FromSeconds(10), MaxFileBytes: 22341856).IsValid);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    [InlineData(int.MaxValue, false)]
    [InlineData(-1, false)]
    public void Modern_capacity_requires_a_known_explicit_profile(int profile, bool valid)
    {
        var p = (NativeRuntimeProfile)profile;
        Assert.Equal(valid, new NativeRuntimeLimits(512, 33554432, 67108864, p).IsValid);
        var plan = new NativeCompilePlan(false, 3, TimeSpan.FromSeconds(30), MaxInputBytes: 67108864,
            MaxFileBytes: 33554432, Profile: p);
        Assert.Equal(valid, plan.IsValid);
        if (valid) Assert.Equal(8388608, plan.ProjectFileBytes);
    }

    [Fact]
    public void All_profile_counts_pins_and_protocol_pairs_are_explicit()
    {
        Assert.Equal(378,NativeRuntimeProfiles.InstalledCount(NativeRuntimeProfile.Legacy));
        Assert.Equal(481,NativeRuntimeProfiles.InstalledCount(NativeRuntimeProfile.LocalLatex2023));
        Assert.Equal(497,NativeRuntimeProfiles.InstalledCount(NativeRuntimeProfile.LocalLatex2023Authoring));
        Assert.Equal(507,NativeRuntimeProfiles.InstalledCount(NativeRuntimeProfile.LocalLatex2023Dvi));
        Assert.Equal(1,NativeRuntimeProfiles.ProtocolVersion(NativeRuntimeProfile.Legacy));
        Assert.Equal(2,NativeRuntimeProfiles.ProtocolVersion(NativeRuntimeProfile.LocalLatex2023));
        Assert.Equal(3,NativeRuntimeProfiles.ProtocolVersion(NativeRuntimeProfile.LocalLatex2023Authoring));
        Assert.Equal(6,NativeRuntimeProfiles.ProtocolVersion(NativeRuntimeProfile.LocalLatex2023Dvi));
        Assert.Equal(0,NativeRuntimeProfiles.ProtocolVersion((NativeRuntimeProfile)99));
        Assert.Equal("fba51a137028de8370d608b3d90db6430bf62941657e57644f800432695bff0d",NativeRuntimeProfiles.ModernClosureHash);
    }
    [Fact]
    public void Modern_budgets_are_bounded_and_legacy_Create_does_not_infer_profile()
    {
        var modern = new NativeRuntimeLimits(512, 33554432, 67108864, NativeRuntimeProfile.LocalLatex2023);
        Assert.False((modern with { MaxFiles = 513 }).IsValid);
        Assert.False((modern with { MaxFileBytes = 33554433 }).IsValid);
        Assert.False((modern with { MaxTotalBytes = 67108865 }).IsValid);
        Assert.Equal(NativeRuntimeIssue.InvalidLimits, NativeRuntimePackage.Create([], [], modern).Issue);
        Assert.Equal(NativeRuntimeIssue.InvalidLimits, NativeRuntimePackage.Create([], [], modern, (NativeRuntimeProfile)99).Issue);
    }

    [Fact]
    public void Modern_wrong_count_preflight_does_not_touch_input_entries()
    {
        var limits = new NativeRuntimeLimits(512, 33554432, 67108864, NativeRuntimeProfile.LocalLatex2023);
        Assert.Equal(NativeRuntimeIssue.ResourceLimit,
            NativeRuntimePackage.Create(new Untouched(), [], limits, NativeRuntimeProfile.LocalLatex2023).Issue);
    }
    private sealed class Untouched : IReadOnlyList<NativeResourceInput>
    {
        public int Count => 480;
        public NativeResourceInput this[int i] => throw new InvalidOperationException("Preflight accessed a byte entry");
        public IEnumerator<NativeResourceInput> GetEnumerator() => throw new InvalidOperationException("enumerated");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
