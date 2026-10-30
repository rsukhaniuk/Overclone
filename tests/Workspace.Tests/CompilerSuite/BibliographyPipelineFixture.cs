using System.Text;
using Client.Compilation;
using Client.Compilation.Lifecycle;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Xunit;

namespace Workspace.Tests.CompilerSuite;

// Real pinned runtime input; all phase replies below are MODELS, not execution.
internal static class BibliographyPipelineFixture
{
    internal const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalCompilerBibliography;
    internal static readonly CompileOutputBudget Budget = new(8388608, 65536, 32, 128, 4096, 8192);
    private static readonly Lazy<NativeRuntimePackage> Current = new(() => Load(Profile, "COMPILER_BIBLIOGRAPHY_RUNTIME_DIR"));
    private static readonly Lazy<NativeRuntimePackage> Legacy = new(() => Load(NativeRuntimeProfile.LocalCompilerCompatibility, "COMPILER_COMPATIBILITY_RUNTIME_DIR"));
    internal static NativeRuntimePackage Package(NativeRuntimeProfile profile = Profile) => profile switch
    {
        NativeRuntimeProfile.LocalCompilerBibliography => Current.Value,
        NativeRuntimeProfile.LocalCompilerCompatibility => Legacy.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };
    private static NativeRuntimePackage Load(NativeRuntimeProfile profile, string variable)
    {
        var directory = Environment.GetEnvironmentVariable(variable)!;
        var pins = LocalRuntimeSelection.ParsePins(File.ReadAllBytes(Path.Combine(directory, "pins.json"))).ToArray();
        var files = pins.Select(p => new NativeResourceInput(p.Name, File.ReadAllBytes(Path.Combine(directory, "files", p.Name)))).ToArray();
        return Assert.IsType<NativeRuntimePackage>(NativeRuntimePackage.Create(files, pins, LocalRuntimeSelection.LimitsFor(profile), profile).Package);
    }
    internal static async Task<CompileRequestContext> Context(CompilationTestWorkspace workspace, NativeCompilerEngine engine,
        bool bib = true, NativeRuntimeProfile profile = Profile)
    {
        var package = Package(profile);
        workspace.Selection.SetSelection(CompilationSelection.Create(new(workspace.Main, package.EngineFor(engine), [package.Bundle],
            bib ? [package.Bibtex] : [], NativeCompilerSuiteContract.SelectionOptions(engine)), 65536).Selection!);
        return CompileRequestContext.Create(await workspace.Snapshot(new(64, 8388608, 16777216, 131072)), 1, 65536,
            NativeRuntimePackage.Pdf, package.AdapterFor(engine)).Context!;
    }
    internal static async Task Prepare(CompilationTestWorkspace workspace, string root = "Україна/my thesis.tex")
    {
        Assert.True(workspace.Workspace.RenameFile(workspace.Main, root));
        Assert.True(workspace.Workspace.CreateFile("chapters/part.tex", "Included synthetic source."));
        Assert.True(workspace.Workspace.CreateFile("references.bib", "@book{entry,author={Synthetic Author},title={Model},year={2025}}"));
        await Task.CompletedTask;
    }
    internal static NativeCompilationWorkerFactory Factory(Model model, NativeCompilerEngine engine,
        NativeRuntimeProfile profile = Profile, int passes = 6) => new(new FixedNativeRuntimeProvider(Package(profile)), model,
            NativeCompilerSuiteContract.CreatePlan(engine, passes, profile: profile));
    internal static async Task<CompileOutputEnvelopeInput> Execute(CompileRequestContext context, Model model,
        NativeCompilerEngine engine, NativeRuntimeProfile profile = Profile, int passes = 6)
    {
        var factory = Factory(model, engine, profile, passes); var id = new CompileJobId(Guid.NewGuid());
        var result = await factory.Create(id, 1).ExecuteAsync(id, context, Budget, default)!.WaitAsync(TimeSpan.FromSeconds(30));
        await factory.Operations.WhenIdle;
        return Assert.IsType<CompileOutputEnvelopeInput>(result);
    }
    internal static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    internal static string Aux(string job, bool biblatex = true, string version = "1") =>
        "\\relax\n\\bibstyle{" + (biblatex ? "biblatex" : "plain") + "}\n\\bibdata{" +
        (biblatex ? job.Replace(' ', '_') + "-blx," : "") + "references}\n\\citation{entry}\n%aux-version:" + version + "\n";
    internal static byte[] Blx(string version = "1") => Bytes("@Comment{$ biblatex control file $}\n@Control{biblatex-control,options={3.9:0:0}}\n%version:" + version + "\n");

    internal sealed record Call(NativePhaseTag Tag, string Entry, string? Root, string[] Outputs,
        string[] ResourceNames, Dictionary<string, byte[]> Generated, int TexPass);
    internal sealed class Model : INativePhaseTransport
    {
        internal readonly List<Call> Calls = [];
        internal Func<int, string> AuxVersion = _ => "1", BlxVersion = _ => "1";
        internal Func<int, byte[]?>? TexBbl;
        internal Func<NativePhaseRequest, NativePhaseReply, NativePhaseReply>? Mutate;
        internal bool Biblatex = true, PlainDocument, EchoBackendInputs;
        internal string Log = "(/tex/rerunfilecheck.sty)\nmodeled phase\n";
        internal string? HoldTool;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<NativePhaseReply> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal NativePhaseReply? Pending;
        internal int Stops, TexPasses, BibtexPasses;
        public Task<NativePhaseReply> ExecuteAsync(NativePhaseRequest request)
        {
            if (request.Tag.Tool == "latex") TexPasses++;
            if (request.Tag.Tool == "bibtex") BibtexPasses++;
            var generated = request.Files.Where(f => request.GeneratedInputNames!.Contains(f.Name))
                .ToDictionary(f => f.Name, f => f.Bytes.ToArray(), StringComparer.Ordinal);
            Calls.Add(new(request.Tag, request.MainEntry, request.SelectedRoot, request.Outputs.ToArray(),
                request.RuntimeResourceNames!.ToArray(), generated, TexPasses));
            var reply = Reply(request);
            reply = Mutate?.Invoke(request, reply) ?? reply;
            if (request.Tag.Tool == HoldTool)
            {
                Pending = reply; Entered.TrySetResult(); return Release.Task;
            }
            return Task.FromResult(reply);
        }
        private NativePhaseReply Reply(NativePhaseRequest request)
        {
            bool converter = request.Tag.Tool == "dvipdfmx", bib = request.Tag.Tool == "bibtex";
            string job = request.Outputs.Single(n => n.EndsWith(".pdf", StringComparison.Ordinal))[..^4];
            var files = new List<NativeFileTransfer>();
            if (converter) files.Add(new(job + ".pdf", "%PDF-modeled-converted"u8.ToArray()));
            else if (bib)
            {
                if (EchoBackendInputs) files.AddRange(request.Files.Where(f => request.GeneratedInputNames!.Contains(f.Name) && f.Name != job + ".bbl"));
                files.Add(new(job + ".bbl", Bytes("modeled BBL " + BibtexPasses)));
                files.Add(new(job + ".blg", "modeled BLG"u8.ToArray()));
            }
            else
            {
                files.Add(new(job + ".aux", Bytes(PlainDocument ? "\\relax\n" : Aux(job, Biblatex, AuxVersion(TexPasses)))));
                files.Add(new("chapters/part.aux", "\\relax\n"u8.ToArray()));
                string blx = job.Replace(' ', '_') + "-blx.bib";
                if (Biblatex && !PlainDocument && request.Outputs.Contains(blx)) files.Add(new(blx, Blx(BlxVersion(TexPasses))));
                var bbl = TexBbl is not null ? TexBbl(TexPasses) : request.Files.SingleOrDefault(f => f.Name == job + ".bbl")?.Bytes;
                if (bbl is not null) files.Add(new(job + ".bbl", bbl));
                files.Add(request.Tag.Version == 8 ? new(job + ".xdv", [247, 7, (byte)TexPasses])
                    : request.Tag.Version == 7 ? new(job + ".dvi", [247, 2, (byte)TexPasses]) : new(job + ".pdf", "%PDF-modeled-tex"u8.ToArray()));
            }
            var engine = request.Tag.EngineId switch
            {
                "latex-dvi" => NativeCompilerEngine.LaTeXDvi, "xelatex" => NativeCompilerEngine.XeLaTeX,
                "lualatex" => NativeCompilerEngine.LuaLaTeX, _ => NativeCompilerEngine.PdfLaTeX
            };
            var component = NativeCompilerComponents.For(request.Tag.Profile, bib ? NativeCompilerEngine.PdfLaTeX : engine);
            string loader = converter ? NativeCompilerSuitePins.ConverterLoaderHash : component.LoaderHash;
            string wasm = converter ? NativeCompilerSuitePins.ConverterWasmHash : component.WasmHash;
            Assert.Equal(loader, NativeRuntimePackage.Digest(request.Loader).Hex);
            Assert.Equal(wasm, NativeRuntimePackage.Digest(request.Wasm).Hex);
            return new(request.Tag, "native-return", 0, true, Log, files.ToArray(),
                request.Files.Select(f => new NativeInputFact(f.Name, f.Bytes.Length, NativeRuntimePackage.Digest(f.Bytes).Hex)).ToArray(),
                WasmSha256: wasm, LoaderSha256: loader);
        }
        public void RequestTermination(string job, long epoch) => Stops++;
    }
}

public sealed class BibliographyFixtureFactAttribute : FactAttribute
{
    public BibliographyFixtureFactAttribute()
    {
        if (!File.Exists(Path.Combine(Environment.GetEnvironmentVariable("COMPILER_BIBLIOGRAPHY_RUNTIME_DIR") ?? "", "pins.json")))
            Skip = "Real profile7 runtime fixture unavailable; modeled replies do not permit invented runtime bytes.";
    }
}
public sealed class BibliographyFixtureTheoryAttribute : TheoryAttribute
{
    public BibliographyFixtureTheoryAttribute()
    {
        if (!File.Exists(Path.Combine(Environment.GetEnvironmentVariable("COMPILER_BIBLIOGRAPHY_RUNTIME_DIR") ?? "", "pins.json")))
            Skip = "Real profile7 runtime fixture unavailable; modeled replies do not permit invented runtime bytes.";
    }
}
public sealed class BibliographyLegacyFixtureTheoryAttribute : TheoryAttribute
{
    public BibliographyLegacyFixtureTheoryAttribute()
    {
        if (!File.Exists(Path.Combine(Environment.GetEnvironmentVariable("COMPILER_COMPATIBILITY_RUNTIME_DIR") ?? "", "pins.json")))
            Skip = "Real immutable profile6 runtime fixture unavailable; no synthetic runtime substitute.";
    }
}
