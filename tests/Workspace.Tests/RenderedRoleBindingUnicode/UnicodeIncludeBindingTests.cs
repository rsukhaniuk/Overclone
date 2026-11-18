using System.Text;
using System.Text.Json.Nodes;
using Client.Analysis;
using Client.Analysis.RenderedReview;
using Client.Compilation;
using Client.Compilation.Native;
using Client.Compilation.Presentation;
using Client.Compilation.SyncTeX;
using Client.Pdf;
using Xunit;
using Existing = Workspace.Tests.RenderedRoleBinding.RoleBindingTests;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.RenderedRoleBindingUnicode;

public sealed class NativeUnicodeRoleFactAttribute : FactAttribute
{
    public NativeUnicodeRoleFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RENDERED_ROLE_BINDING_UNICODE_FIXTURE")))
            Skip = "Exact separately executed P16/final-E1v2 Unicode/include and genuine Rust fixture not supplied.";
    }
}

// Genuine separately executed native/Rust observations replayed through real
// managed capture/adapter/coordinator/binding. Browser IO/compile ports modeled.
// These are truthful refusal regressions, not positive role issuance claims.
public sealed class UnicodeIncludeBindingTests
{
    [NativeUnicodeRoleFact]
    public void Native_root_and_executed_include_have_exact_Ukrainian_text_and_bidirectional_map_correlations()
    {
        var folder = Folder; var data = JsonNode.Parse(File.ReadAllBytes(Path.Combine(folder, "native-fixture.json")))!;
        Assert.Equal("actual-native-correlation-pass-source-role-still-unsupported", data["status"]!.GetValue<string>());
        var compileReceipt = JsonNode.Parse(File.ReadAllBytes(Path.Combine(folder, "native01/receipt.json")))!;
        Assert.Equal(0, compileReceipt["nativeReturn"]!.GetValue<int>());
        Assert.Equal(NativeCompilerComponents.For(NativeInputBudgetContract.Profile, NativeCompilerEngine.PdfLaTeX).WasmHash,
            compileReceipt["candidate"]!["wasm"]!.GetValue<string>());
        // A fresh pdfTeX run stamps its actual PDF creation time. Correlate
        // the captured bytes with both receipts and pin the original source.
        Assert.Equal(compileReceipt["outputs"]!["pdf"]!["sha256"]!.GetValue<string>(), data["pdf"]!["sha256"]!.GetValue<string>());
        Assert.Equal(data["pdf"]!["sha256"]!.GetValue<string>(), PdfHashes.Of(File.ReadAllBytes(Path.Combine(folder, "native01/output.pdf"))));
        Assert.Equal("bd5e42f22541a65196076922ff0714367774a7e83bd7be5ec291ad8129e7d80a", data["map"]!["sha256"]!.GetValue<string>());
        Assert.Equal(data["map"]!["sha256"]!.GetValue<string>(), PdfHashes.Of(File.ReadAllBytes(Path.Combine(folder, "native01/raw.synctex"))));
        foreach (var (name, pin) in new[] { ("тези/my thesis.tex", "418d074133ae66f81720b67baab662f89e20f4e4244f33684a10d7ae50ed34ab"),
                     ("chapter.tex", "84b2e1d55ebd3edcdde2be2c1280915008d533e4df358631095b5abe551c16c5") })
        {
            Assert.Equal(pin, compileReceipt["inputs"]![name]!["sha256"]!.GetValue<string>());
            Assert.Equal(pin, PdfHashes.Of(File.ReadAllBytes(Path.Combine(folder, "project", name))));
        }
        Assert.Equal("8b715ed561ec644757f305ddad0a99ae728c6705f9868e96ccb53c8c9143c4b3", data["pdfRuntime"]!["wasm"]!.GetValue<string>());
        Assert.Equal(PdfHashes.Of(File.ReadAllBytes(Path.Combine(folder, "response.json"))), data["rustResponse"]!.GetValue<string>());
        var rows = data["rows"]!.AsArray(); Assert.Equal(2, rows.Count);
        foreach (var (index, file, literal, line) in new[] {
            (0, "тези/my thesis.tex", "Іван і Ірина", 7), (1, "chapter.tex", "Інші вимірювання", 3) })
        {
            var row = rows[index]!; Assert.Equal(file, row["file"]!.GetValue<string>());
            Assert.Equal(literal, row["literal"]!.GetValue<string>()); Assert.Equal(line, row["line"]!.GetValue<int>());
            Assert.Equal(line, row["forward"]!["matches"]![0]!["line"]!.GetValue<int>());
            Assert.Equal(2, row["inverses"]!.AsArray().Count);
            foreach (var inverse in row["inverses"]!.AsArray())
            {
                var match = inverse!["reply"]!["matches"]![0]!;
                Assert.Equal(line, match["line"]!.GetValue<int>());
                Assert.Contains(match["file"]!.GetValue<string>(), new[] { file, "./" + file, "/work/" + file });
            }
            var native = data["pages"]![row["page"]!.GetValue<int>() - 1]!["text"]!["characters"]!.AsArray();
            string text = string.Concat(native.Skip(row["start"]!.GetValue<int>()).Take(row["end"]!.GetValue<int>() - row["start"]!.GetValue<int>())
                .Select(c => new Rune(c!["unicode"]!.GetValue<int>()).ToString()));
            Assert.Equal(literal, text); Assert.Contains('І', text); Assert.Contains('і', text);
        }
    }

    [NativeUnicodeRoleFact]
    public void Original_Ukrainian_include_report_is_current_but_semantic_uncertainty_prevents_both_issued_roles() => Run(async () =>
    {
        await using var h = await Open(); var thesis = h.Report.Result.Thesis!;
        Assert.Equal("1.6.0", h.Report.Result.Profile.Version);
        Assert.Equal(2, thesis.ClosureFileIds.Length); Assert.False(thesis.CompleteLiteralScan);
        Assert.Contains("unknownPackage", thesis.Limitations);
        Assert.Contains("macroOrPackageExecutionUnsupported", thesis.Limitations);
        Assert.Contains("outsideDocumentExecution", thesis.Limitations);
        Assert.True((await new SourceNavigator(h.Analysis, h.Work.Workspace, h.Analyzer).VerifyCurrentAsync(h.Report, default)).Ok);
        var indices = Enumerable.Range(0, thesis.Events.Length).Where(i => thesis.Events[i].Kind == "captionTitle").ToArray();
        Assert.Equal(2, indices.Length);
        foreach (var index in indices)
        {
            var e = thesis.Events[index]; var captured = h.Report.Snapshot.Files.Single(f => f.FileId.ToString() == e.FileId);
            var literal = Encoding.UTF8.GetString(captured.Bytes.AsSpan()[(int)e.Span.StartByte..(int)e.Span.EndByte]);
            Assert.Equal(e.Text, literal); Assert.Contains('І', literal); Assert.Contains('і', literal);
            Assert.Contains("🙂", Encoding.UTF8.GetString(captured.Bytes.AsSpan()[..(int)e.Span.StartByte]));
            var result = await h.Binding.BindAsync(new(h.Report, index, index + 1, "bachelorQualification"));
            Assert.Null(result.Value); Assert.Equal("unsupported-or-ambiguous-source-role", result.Code);
        }
        Assert.Equal(0, h.Inspector.Calls); Assert.Equal(0, h.Queries.Calls);
    });

    [NativeUnicodeRoleFact]
    public void Independent_package_free_include_control_does_not_invent_typed_included_context()
    {
        var fixture = Load(Path.Combine(Folder, "controls/ascii-executed-include")); var snapshot = fixture.Snapshot();
        var result = Assert.IsType<AnalysisResponse.Completed>(ThesisValidation.ParseResponse(fixture.Reply(snapshot, "control"), snapshot, "control")).Result;
        Assert.True(result.Thesis!.CompleteLiteralScan); Assert.Empty(result.Thesis.Limitations);
        Assert.Equal(2, result.Thesis.ClosureFileIds.Length);
        var root = result.Knu!.Files.Single(f => f.FileId == snapshot.ReferenceConfiguration!.RootFileId);
        var included = result.Knu.Files.Single(f => f.FileId != snapshot.ReferenceConfiguration!.RootFileId);
        Assert.Contains(AnalysisLimitation.IncludeNotResolved, root.Limitations);
        Assert.Contains(AnalysisLimitation.UnknownSemantics, root.Limitations);
        Assert.Single(root.Observations.Where(o => o.Method == "literal-caption-title"));
        Assert.Contains(AnalysisLimitation.CaptionUncertain, included.Limitations);
        Assert.Empty(included.Observations.Where(o => o.Method == "literal-caption-title"));
        Assert.Contains(result.Thesis.Events, e => e.Kind == "captionTitle" && e.FileId == included.FileId && e.Text == "Included comparison");
    }

    internal static string Folder => Environment.GetEnvironmentVariable("RENDERED_ROLE_BINDING_UNICODE_FIXTURE")!;
    internal static ThesisSource.ThesisFixture Load(string folder, bool captionRoles = false) => new(new JsonObject {
        ["request"] = JsonNode.Parse(File.ReadAllBytes(Path.Combine(folder, captionRoles ? "request-v1_7.json" : "request.json"))),
        ["packedUtf8Hex"] = Convert.ToHexString(File.ReadAllBytes(Path.Combine(folder, "packed.bin"))),
        ["response"] = JsonNode.Parse(File.ReadAllBytes(Path.Combine(folder, captionRoles ? "response-v1_7.json" : "response.json"))) });

    internal static string CaptionRoleReply(ThesisSource.ThesisFixture fixture, AnalysisSnapshot snapshot, string requestId)
    {
        var response = JsonNode.Parse(fixture.Reply(snapshot, requestId))!;
        // The synthetic producer uses f0 for the selected root. The test host
        // assigns its actual workspace IDs and retains every span and literal.
        foreach (var role in response["payload"]!["thesis"]!["captionRoles"]!.AsArray())
        {
            Assert.Equal("f0", role!["contextRootFileId"]!.GetValue<string>());
            role["contextRootFileId"] = snapshot.ReferenceConfiguration!.RootFileId;
        }
        return response.ToJsonString();
    }

    private sealed class Configuration(Existing.Host h, bool captionRoles, string? refusal) : ICompileWorkspaceConfiguration
    {
        public Task<CompileWorkspaceConfigurationResult> OpenAsync()
        {
            var readiness = new CompileReadiness(true, "Ready", "Separately executed Unicode native fixture replay");
            Task<CompileReadiness> CheckAsync() => Task.FromResult(readiness);
            if (!captionRoles) return Task.FromResult(new CompileWorkspaceConfigurationResult(readiness,
                new("roles", "Unicode fixture replay", CompilationTestWorkspace.Descriptor("pdflatex"), [], [],
                    h.Workers, h.ConfiguredInspector, CheckAsync, SelectionOptions: NativeSelectedEntryContract.SelectionOptions)));
            var profile = refusal == "different-profile" ? NativeOriginCompilerContract.Profile : NativeInputBudgetContract.Profile;
            var engineKind = refusal == "different-engine" ? NativeCompilerEngine.LuaLaTeX : NativeCompilerEngine.PdfLaTeX;
            var component = NativeCompilerComponents.For(profile, engineKind);
            var engine = new CompilationDescriptor(engineKind == NativeCompilerEngine.PdfLaTeX ? "pdftex" : "lualatex",
                component.EngineVersion, new(component.WasmHash));
            var identity = NativeRuntimePackage.Digest(Encoding.UTF8.GetBytes(
                $"native-runtime-profile-v1\n{NativeRuntimeProfiles.IdentityName(profile)}\n{NativeRuntimeProfiles.ClosureHash(profile)}"));
            var bundle = new CompilationDescriptor(profile == NativeInputBudgetContract.Profile ? "local-compiler-input-budget" : "local-compiler-origins", "1", identity);
            if (refusal == "different-bundle") bundle = bundle with { Hash = CompilationTestWorkspace.Hash([0xde, 0xad]) };
            return Task.FromResult(new CompileWorkspaceConfigurationResult(readiness, new("roles", "Unicode fixture replay",
                engine, [bundle], [], h.Workers, h.ConfiguredInspector, CheckAsync,
                SelectionOptions: NativeCompilerSuiteContract.SelectionOptions(profile, engineKind),
                OutputAdapter: component.OutputAdapter, NativeProfile: profile)));
        }
    }

    internal static async Task<Existing.Host> Open(bool captionRoles = false, string? refusal = null)
    {
        var h = new Existing.Host { Work = await CompilationTestWorkspace.Open() };
        h.Data = JsonNode.Parse(File.ReadAllBytes(Path.Combine(Folder, "native-fixture.json")))!.AsObject();
        h.Pdf = File.ReadAllBytes(Path.Combine(Folder, "native01/output.pdf")); h.Map = File.ReadAllBytes(Path.Combine(Folder, "native01/raw.synctex"));
        foreach (var old in h.Work.Workspace.Files.ToArray()) Assert.True(h.Work.Workspace.DeleteFile(old.Id));
        foreach (var name in new[] { "тези/my thesis.tex", "chapter.tex" })
            Assert.True(h.Work.Workspace.CreateFileFromBytes(name, File.ReadAllBytes(Path.Combine(Folder, "project", name))));
        if (refusal == "binary-shadow")
            Assert.True(h.Work.Workspace.CreateFileFromBytes("local/UKRAINEB.ldf", Encoding.UTF8.GetBytes("% local override")));
        h.Main = h.Work.Workspace.Files.Single(f => !f.Deleted && f.RelativePath == "тези/my thesis.tex").Id;
        h.Chapter = h.Work.Workspace.Files.Single(f => !f.Deleted && f.RelativePath == "chapter.tex").Id;
        h.Inspector = new(h); h.ConfiguredInspector = h.Inspector; h.Queries = new(h);
        h.Session = new(h.Work.Workspace, new Configuration(h, captionRoles, refusal)); var owner = SynchronizationContext.Current!;
        h.Session.AttachRenderer(action => { var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.Post(_ => { try { action(); done.SetResult(); } catch (Exception e) { done.SetException(e); } }, null); return done.Task; });
        await h.Session.RefreshToolsAsync(); h.Session.SelectMain(h.Main); h.Session.SelectEngine("roles");
        h.Navigation = new(h.Work.Workspace, h.Session, h.Queries); var fixture = Load(Folder, captionRoles);
        h.Js = new KnuProtocolTests.FixtureJs((metadata, requestId) => {
            var snapshot = h.Analyzer.Captured.Single(s => s.Id.ToString() == JsonNode.Parse(metadata)!["payload"]!["snapshotId"]!.GetValue<string>());
            return captionRoles ? CaptionRoleReply(fixture, snapshot, requestId) : fixture.Reply(snapshot, requestId); });
        h.Analyzer = new(new RustSourceAnalyzer(h.Js));
        h.Analysis = new(h.Work.Workspace, h.Analyzer, delayAsync: (_, ct) => Task.Delay(Timeout.Infinite, ct));
        var completed = new TaskCompletionSource<AnalysisReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Analysis.Changed += state => { if (state is AnalysisState.Completed c) completed.TrySetResult(c.Report); };
        h.Analysis.SetSelection(captionRoles ? AnalysisSelection.KnuWithCaptionRoles(AnalysisWorkType.BachelorQualification, h.Main.ToString())
            : AnalysisSelection.KnuWithCitationNotes(AnalysisWorkType.BachelorQualification, h.Main.ToString()));
        h.Report = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10)); await h.Compile(); h.Inspector.Calls = 0;
        h.Binding = new(h.Work.Workspace, h.Session, new(h.Analysis, h.Work.Workspace, h.Analyzer), h.Navigation);
        return h;
    }
}
