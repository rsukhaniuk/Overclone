using System.Text.Json;
using Client.Compilation.Native;
using Xunit;

namespace Workspace.Tests.CompilationNative;

public sealed class NativeSelectedPdf17Tests
{
    private const int MiB = 1024 * 1024;
    private static NativePhaseTag Tag => new("job", "request", 1, Guid.Empty, "digest", 1, "latex", "nonce",
        Version: 23, Profile: NativeSelectedPdfContract.Profile, RuntimeIdentity: NativeSelectedPdfContract.RuntimeIdentity, EngineId: "pdftex");

    [Fact]
    public void SelectedSuccessfulPhaseAndFileAreBoundSeparately()
    {
        var tag = Tag;
        var request = new NativePhaseRequest(tag, "chapters/Thesis.TeX", [], ["Thesis.pdf", "Thesis.aux"],
            [], [], 256 * 1024, 12 * MiB, "chapters/Thesis.TeX");
        Assert.Equal(12 * MiB, NativeSelectedPdfContract.PhaseOutputBytes(tag, 12 * MiB));
        Assert.Equal(9 * MiB, NativeSelectedPdfContract.PhaseOutputBytes(tag, 9 * MiB));
        Assert.Equal(12 * MiB, NativeSelectedPdfContract.FileOutputBytes(request, "Thesis.pdf"));
        Assert.Equal(16 * MiB, NativeSelectedPdfContract.PhaseAggregateBytes(request, 12 * MiB));
        foreach (string name in new[] { "thesis.pdf", "chapters/Thesis.pdf", "Thesis.aux", "another.pdf" })
            Assert.Equal(8 * MiB, NativeSelectedPdfContract.FileOutputBytes(request, name));
        foreach (var other in new[] { tag with { Tool = "bibtex" }, tag with { Tool = "biber", Version = 10 },
            tag with { EngineId = "latex-dvi", Version = 24 }, tag with { EngineId = "xelatex", Version = 21 },
            tag with { EngineId = "lualatex", Version = 22 }, tag with { Tool = "dvipdfmx" },
            tag with { RuntimeIdentity = new string('0', 64) }, tag with { Version = 21 } })
        {
            Assert.Equal(8 * MiB, NativeSelectedPdfContract.PhaseOutputBytes(other, 12 * MiB));
            Assert.Equal(8 * MiB, NativeSelectedPdfContract.FileOutputBytes(request with { Tag = other }, "Thesis.pdf"));
        }
        Assert.Equal(8 * MiB, NativeSelectedPdfContract.PhaseOutputBytes(tag with { Profile = NativeInputBudgetContract.Profile }, 8 * MiB));
    }

    [Fact]
    public void PlansReuseTheExactPreviousExecutableAndSourceLimits()
    {
        var profile = NativeSelectedPdfContract.Profile;
        foreach (var engine in Enum.GetValues<NativeCompilerEngine>())
        {
            var plan = NativeCompilerSuiteContract.CreatePlan(engine, profile: profile);
            Assert.True(plan.IsValid);
            Assert.Equal(NativeCompilerSuiteContract.Phase(NativeInputBudgetContract.Profile, engine), plan.EffectivePhaseVersion);
            Assert.Equal(NativeInputBudgetContract.Component(engine), NativeCompilerComponents.For(profile, engine));
            Assert.Equal(128, NativeRuntimeProfiles.ProjectFileLimit(profile));
            Assert.Equal(2 * MiB, plan.SourceFileBytes);
            Assert.Equal(16 * MiB, plan.ProjectTotalBytes);
            Assert.Equal(16 * MiB, plan.ConverterProjectBytes);
        }
    }

    [SelectedPdf17FixtureFact]
    public void ActualP17ClosureAdmitsOnlyItsOwnPinsAndAuthenticatesItsIdentity()
    {
        string root = Environment.GetEnvironmentVariable("SELECTED_PDF17_RUNTIME_DIR")!;
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "pins.json")));
        var pins = json.RootElement.EnumerateArray().Select(p => new NativeResourcePin(
            p.GetProperty("name").GetString()!, p.GetProperty("length").GetInt32(), p.GetProperty("sha256").GetString()!)).ToArray();
        var input = pins.Select(p => new NativeResourceInput(p.Name, File.ReadAllBytes(Path.Combine(root, "files", p.Name)))).ToArray();
        var profile = NativeSelectedPdfContract.Profile;
        var limits = new NativeRuntimeLimits(NativeRuntimeProfiles.MaxFiles(profile), NativeRuntimeProfiles.MaxFileBytes(profile),
            NativeRuntimeProfiles.MaxTotalBytes(profile), profile);
        var runtime = NativeRuntimePackage.Create(input, pins, limits, profile);
        Assert.Equal(NativeSelectedPdfContract.RuntimeIdentity, runtime.Package?.Identity.Hex);
        Assert.Null(runtime.Issue);
        Assert.Equal("local-compiler-selected-pdf", runtime.Package!.Bundle.Id);
        var tampered = pins.Select(p => p.Name == "ectt1000.tfm" ? p with { Sha256 = new string('0', 64) } : p).ToArray();
        Assert.Equal(NativeRuntimeIssue.TechnicalHashMismatch, NativeRuntimePackage.Create(input, tampered, limits, profile).Issue);
        Assert.Equal(NativeRuntimeIssue.ResourceLimit, NativeRuntimePackage.Create(input, pins,
            limits with { Profile = NativeInputBudgetContract.Profile }, NativeInputBudgetContract.Profile).Issue);
    }
}

public sealed class SelectedPdf17FixtureFactAttribute : FactAttribute
{
    public SelectedPdf17FixtureFactAttribute()
    {
        if (!File.Exists(Path.Combine(Environment.GetEnvironmentVariable("SELECTED_PDF17_RUNTIME_DIR") ?? "", "pins.json")))
            Skip = "Explicit selected-PDF P17 byte fixture required.";
    }
}
