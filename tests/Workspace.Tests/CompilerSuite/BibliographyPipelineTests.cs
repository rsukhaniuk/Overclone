using System.Text;
using Client.Compilation;
using Client.Compilation.Lifecycle;
using Client.Compilation.Native;
using Workspace.Tests.CompilationLifecycle;
using Xunit;
using static Workspace.Tests.CompilerSuite.BibliographyPipelineFixture;

namespace Workspace.Tests.CompilerSuite;

// Policy/unit tests below are portable. Fixture tests use REAL pinned bytes but
// MODELED replies. No test in this file establishes native/browser acceptance.
public sealed class BibliographyPipelineTests
{
    private static NativeSelectedEntry Entry(NativeCompilerEngine engine = NativeCompilerEngine.PdfLaTeX,
        string root = "Україна/my thesis.tex")
    {
        var entry = NativeCompilerSuiteContract.CreateEntry(root, [root, "chapters/part.tex"], engine, out var error);
        Assert.Null(error);
        return NativeBibliographyContract.ExtendEntry(Profile, Assert.IsType<NativeSelectedEntry>(entry));
    }
    private static Dictionary<string, byte[]> State(NativeSelectedEntry entry) => new(StringComparer.Ordinal)
    {
        [entry.Aux] = Bytes(Aux(entry.JobName)),
        ["chapters/part.aux"] = "\\relax\n"u8.ToArray(),
        [NativeBibliographyContract.BlxName(entry)] = Blx()
    };

    [Theory]
    [InlineData(NativeCompilerEngine.PdfLaTeX)] [InlineData(NativeCompilerEngine.LaTeXDvi)]
    [InlineData(NativeCompilerEngine.XeLaTeX)] [InlineData(NativeCompilerEngine.LuaLaTeX)]
    public void Only_profile7_adds_one_exact_selected_control_name(NativeCompilerEngine engine)
    {
        const string root = "Україна/и\u0306 e\u0301.tex";
        var original = NativeCompilerSuiteContract.CreateEntry(root, [root], engine, out var error)!;
        Assert.Null(error);
        foreach (var profile in Enumerable.Range(0, 7).Select(i => (NativeRuntimeProfile)i))
            Assert.Same(original, NativeBibliographyContract.ExtendEntry(profile, original));
        var current = NativeBibliographyContract.ExtendEntry(Profile, original);
        Assert.Equal(original.SelectedPath, current.SelectedPath);
        Assert.Equal(original.LatexEntry, current.LatexEntry);
        Assert.Equal(original.BibtexEntry, current.BibtexEntry);
        Assert.Equal(original.Outputs.Append(original.JobName.Replace(' ', '_') + "-blx.bib"), current.Outputs);
        Assert.Equal(original.State.Append(original.JobName.Replace(' ', '_') + "-blx.bib"), current.State);
        if (original.JobName.Contains(' ')) Assert.DoesNotContain(original.JobName + "-blx.bib", current.Outputs);
        Assert.Equal(engine == NativeCompilerEngine.LuaLaTeX ? "result" : "и\u0306 e\u0301", current.JobName);
        Assert.DoesNotContain(current.Outputs, n => n.EndsWith(".bcf", StringComparison.Ordinal) || n.EndsWith(".xml", StringComparison.Ordinal));
        Assert.False(NativeRuntimeProfiles.HasBibliographyCarry((NativeRuntimeProfile)int.MaxValue));
        var plan = NativeCompilerSuiteContract.CreatePlan(engine, profile: Profile);
        Assert.True(plan.IsValid); Assert.Equal(6, plan.MaxLatexPasses);
        Assert.Equal(TimeSpan.FromSeconds(90), plan.JobDeadline);
        Assert.Equal(TimeSpan.FromSeconds(120), NativeCompilerSuiteContract.LifecycleDeadline);
        Assert.Equal(8388608, plan.ProjectFileBytes);
    }

    [Fact]
    public void Complete_namespace_checks_preserve_unique_original_spelling_but_reject_aliases_and_prefixes()
    {
        var entry = Entry(root: "Україна/и\u0306 e\u0301.tex");
        string blx = NativeBibliographyContract.BlxName(entry);
        string[] inputs = [entry.SelectedPath, "chapters/part.tex", "references.bib", "__oop_runtime__/biblatex.bst"];
        Assert.Null(NativeBibliographyContract.ValidateNamespace(entry, inputs, [blx]));
        foreach (var collision in new[] { blx, blx.ToUpperInvariant(), blx.Normalize(NormalizationForm.FormC), blx + "/data.tex" })
        {
            Assert.NotNull(NativeBibliographyContract.ValidateNamespace(entry, inputs.Append(collision), []));
            Assert.NotNull(NativeBibliographyContract.ValidateNamespace(entry, new[] { collision }.Concat(inputs), []));
        }
        foreach (var name in new[] { "other-blx.bib", "nested/" + blx, "../" + blx, entry.JobName + ".bcf", entry.JobName + ".run.xml" })
            Assert.NotNull(NativeBibliographyContract.ValidateNamespace(entry, inputs, [name]));
        Assert.NotNull(NativeBibliographyContract.ValidateNamespace(entry, inputs, [blx, blx]));
        Assert.NotNull(NativeBibliographyContract.ValidateNamespace(entry with { Outputs = entry.Outputs.Add("other.bib") }, inputs, []));
        Assert.NotNull(NativeBibliographyContract.ValidateProjectNames(["../bad.tex"]));
        Assert.Equal("reserved-runtime-namespace", NativeBibliographyContract.ValidateProjectNames(["__OOP_RUNTIME__/captured.bib"]));
    }

    [Theory]
    [InlineData("aux-missing", "missing-native-aux")] [InlineData("aux-empty", "missing-native-aux")]
    [InlineData("blx-missing", "missing-native-blx")] [InlineData("blx-empty", "missing-native-blx")]
    [InlineData("bst-missing", "missing-bibliography-style")] [InlineData("database-missing", "missing-bibliography-database")]
    [InlineData("aux-include-missing", "missing-native-aux")] [InlineData("traversal", "invalid-bibliography-control")]
    [InlineData("utf8", "invalid-bibliography-control")] [InlineData("macro", "invalid-bibliography-control")]
    [InlineData("empty-record", "invalid-bibliography-control")]
    [InlineData("long-record", "invalid-bibliography-control")]
    [InlineData("record-count", "invalid-bibliography-control")]
    public void Missing_or_nonliteral_backend_inputs_fail_safely(string kind, string code)
    {
        var entry = Entry(root: "main.tex"); var state = State(entry);
        string[] runtime = ["__oop_runtime__/biblatex.bst"];
        string[] project = ["references.bib"];
        switch (kind)
        {
            case "aux-missing": state.Remove(entry.Aux); break;
            case "aux-empty": state[entry.Aux] = []; break;
            case "blx-missing": state.Remove(NativeBibliographyContract.BlxName(entry)); break;
            case "blx-empty": state[NativeBibliographyContract.BlxName(entry)] = []; break;
            case "bst-missing": runtime = []; break;
            case "database-missing": project = []; break;
            case "aux-include-missing": state[entry.Aux] = Bytes(Aux(entry.JobName) + "\\@input{absent.aux}\n"); break;
            case "traversal": state[entry.Aux] = Bytes("\\bibstyle{../escape}\n\\bibdata{references}\n"); break;
            case "utf8": state[entry.Aux] = [0xc0, 0xaf]; break;
            case "macro": state[entry.Aux] = Bytes("\\bibstyle{\\unknown}\n\\bibdata{references}\n"); break;
            case "long-record": state[entry.Aux] = Bytes("\\bibstyle{biblatex}\n\\bibdata{" + new string('x', 513) + "}\n"); break;
            case "record-count": state[entry.Aux] = Bytes("\\bibstyle{biblatex}\n\\bibdata{" + string.Join(',', Enumerable.Range(0, NativeCompilerSuiteContract.Files + 193).Select(i => "ref" + i)) + "}\n"); break;
            default: state[entry.Aux] = Bytes("\\bibstyle{}\n\\bibdata{references}\n"); break;
        }
        Assert.Equal(code, NativeBibliographyContract.ControlInputIssue(entry, state, project, runtime));
    }

    [Fact]
    public void Literal_native_controls_accept_classic_styles_and_exact_project_overrides()
    {
        var entry = Entry(root: "main.tex"); var state = State(entry);
        Assert.Null(NativeBibliographyContract.ControlInputIssue(entry, state, ["references.bib", "biblatex.bst"], []));
        state.Remove(NativeBibliographyContract.BlxName(entry)); state[entry.Aux] = Bytes(Aux(entry.JobName, false));
        Assert.Null(NativeBibliographyContract.ControlInputIssue(entry, state, ["references.bib"], ["__oop_runtime__/plain.bst"]));
    }

    [Fact]
    public void Fingerprint_binds_actual_control_project_style_and_tool_runtime_inputs_but_not_backend_outputs() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open();
        Assert.True(f.Workspace.RenameFile(f.Main, "main.tex"));
        Assert.True(f.Workspace.CreateFile("references.bib", "original database"));
        Assert.True(f.Workspace.CreateFile("plain.bst", "original style"));
        var snapshot = await f.Snapshot(); var entry = Entry(root: "main.tex"); var state = State(entry);
        var tool = CompilationTestWorkspace.Descriptor("bibtex", "modeled-version");
        string Fingerprint(Dictionary<string, byte[]> value) => NativeBibliographyContract.Fingerprint(entry, value, snapshot, new string('1',64), tool);
        string original = Fingerprint(state);
        var reordered = state.Reverse().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        Assert.Equal(original, Fingerprint(reordered));
        reordered[entry.Bbl] = Bytes("changed BBL only"); reordered[entry.JobName + ".blg"] = Bytes("changed BLG only");
        Assert.Equal(original, Fingerprint(reordered));
        reordered[entry.Aux] = Bytes(Aux(entry.JobName, version: "2")); Assert.NotEqual(original, Fingerprint(reordered));
        reordered[entry.Aux] = state[entry.Aux]; reordered[NativeBibliographyContract.BlxName(entry)] = Blx("2");
        Assert.NotEqual(original, Fingerprint(reordered));
        Assert.NotEqual(original, NativeBibliographyContract.Fingerprint(entry, state, snapshot, new string('2',64), tool));
        Assert.NotEqual(original, NativeBibliographyContract.Fingerprint(entry, state, snapshot, new string('1',64), tool with { Version = "different tool" }));
        var database = f.Workspace.Files.Single(p => p.RelativePath == "references.bib");
        Assert.True(f.Workspace.EditText(database.Id, "changed database"));
        var changed = await f.Snapshot();
        Assert.NotEqual(original, NativeBibliographyContract.Fingerprint(entry, state, changed, new string('1',64), tool));
        var style = f.Workspace.Files.Single(p => p.RelativePath == "plain.bst");
        Assert.True(f.Workspace.EditText(style.Id, "changed style"));
        var changedAgain = await f.Snapshot();
        Assert.NotEqual(NativeBibliographyContract.Fingerprint(entry, state, changed, new string('1',64), tool),
            NativeBibliographyContract.Fingerprint(entry, state, changedAgain, new string('1',64), tool));
    });

    [BibliographyFixtureTheory]
    [InlineData(NativeCompilerEngine.PdfLaTeX)] [InlineData(NativeCompilerEngine.LaTeXDvi)]
    [InlineData(NativeCompilerEngine.XeLaTeX)] [InlineData(NativeCompilerEngine.LuaLaTeX)]
    public void Selected_engine_carries_exact_first_control_through_backend_and_later_tex(NativeCompilerEngine engine) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); await Prepare(f);
        var model = new Model(); var context = await Context(f, engine);
        var result = await Execute(context, model, engine);
        Assert.Equal(CompileOutputStatus.Succeeded, result.Status);
        var expected = engine is NativeCompilerEngine.LaTeXDvi or NativeCompilerEngine.XeLaTeX
            ? new[] { "latex", "bibtex", "latex", "latex", "dvipdfmx" } : ["latex", "bibtex", "latex", "latex"];
        Assert.Equal(expected, model.Calls.Select(c => c.Tag.Tool));
        string job = engine == NativeCompilerEngine.LuaLaTeX ? "result" : "my thesis";
        string blx = job.Replace(' ', '_') + "-blx.bib";
        Assert.Contains(blx, model.Calls[0].Outputs); Assert.Empty(model.Calls[0].Generated);
        foreach (var call in model.Calls.Where(c => c.Tag.Tool != "dvipdfmx"))
        {
            Assert.Equal(Profile, call.Tag.Profile); Assert.Equal(NativeCompilerSuiteContract.Phase(engine), call.Tag.Version);
            Assert.Equal(NativeCompilerSuiteContract.EngineId(engine), call.Tag.EngineId);
            Assert.Equal("Україна/my thesis.tex", call.Root);
            Assert.DoesNotContain(call.Generated.Keys, n => call.ResourceNames.Contains(n));
            if (call.Tag.Ordinal > 1) Assert.Equal(Blx(), call.Generated[blx]);
            Assert.Contains(job + ".aux", call.Outputs); Assert.Contains(job + ".pdf", call.Outputs);
            if (job.Contains(' ')) Assert.DoesNotContain(job.Replace(' ', '_') + ".aux", call.Outputs);
            if (call.Tag.Tool == "bibtex") Assert.Equal(job + ".tex", call.Entry);
            else Assert.Equal(engine is NativeCompilerEngine.XeLaTeX or NativeCompilerEngine.LuaLaTeX
                ? "Україна/my thesis.tex" : "\"Україна/my thesis.tex\"", call.Entry);
        }
        Assert.All(model.Calls.Skip(2).Where(c => c.Tag.Tool == "latex"), c => Assert.Contains(job + ".bbl", c.Generated.Keys));
        var envelope = CompileOutputEnvelope.Create(result, Budget).Envelope!;
        Assert.NotNull(envelope.TryAccept(context).Output);
        Assert.True(f.Workspace.EditText(f.Main, "new revision"));
        Assert.Null(envelope.TryAccept(await Context(f, engine)).Output);
    });

    [BibliographyFixtureTheory]
    [InlineData("aux")] [InlineData("blx")]
    public void Changed_control_reruns_backend_once_per_interval_then_converges(string changed) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); await Prepare(f);
        var model = new Model();
        if (changed == "aux") model.AuxVersion = pass => pass == 1 ? "1" : "2";
        else model.BlxVersion = pass => pass == 1 ? "1" : "2";
        var result = await Execute(await Context(f, NativeCompilerEngine.PdfLaTeX), model, NativeCompilerEngine.PdfLaTeX);
        Assert.Equal(CompileOutputStatus.Succeeded, result.Status);
        Assert.Equal(new[] { "latex", "bibtex", "latex", "bibtex", "latex", "latex" }, model.Calls.Select(c => c.Tag.Tool));
    });

    [BibliographyFixtureFact]
    public void Backend_outputs_cannot_trigger_their_own_rerun() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); await Prepare(f);
        var model = new Model { EchoBackendInputs = true, TexBbl = pass => pass < 3 ? Bytes("old BBL") : Bytes("settled BBL") };
        var result = await Execute(await Context(f, NativeCompilerEngine.LuaLaTeX), model, NativeCompilerEngine.LuaLaTeX);
        Assert.Equal(CompileOutputStatus.Succeeded, result.Status);
        Assert.Equal(1, model.BibtexPasses); Assert.Equal(4, model.TexPasses);
    });

    [BibliographyFixtureTheory]
    [InlineData("unstable")] [InlineData("warning")]
    public void Unstable_control_or_real_rerun_words_fail_at_the_existing_cap_without_conversion(string kind) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); await Prepare(f);
        var model = new Model();
        if (kind == "unstable") model.AuxVersion = pass => pass.ToString(System.Globalization.CultureInfo.InvariantCulture);
        else model.Log = "Package rerunfilecheck Warning: Rerun to get outlines right.\n";
        var result = await Execute(await Context(f, NativeCompilerEngine.XeLaTeX), model, NativeCompilerEngine.XeLaTeX);
        Assert.Equal(CompileOutputStatus.Failed, result.Status); Assert.Null(result.Payload);
        Assert.Contains(result.Diagnostics!, d => d.Code == "pass-limit-unknown");
        Assert.Equal(6, model.TexPasses); Assert.InRange(model.BibtexPasses, 1, 6);
        Assert.DoesNotContain(model.Calls, c => c.Tag.Tool == "dvipdfmx");
        for (int i = 1; i < model.Calls.Count; i++)
            Assert.False(model.Calls[i].Tag.Tool == "bibtex" && model.Calls[i-1].Tag.Tool == "bibtex");
    });

    [BibliographyFixtureTheory]
    [InlineData(false)] [InlineData(true)]
    public void Ordinary_authoring_and_classic_bibtex_keep_their_finite_paths(bool bib) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); await Prepare(f);
        var model = new Model { Biblatex = false, PlainDocument = !bib };
        var result = await Execute(await Context(f, NativeCompilerEngine.PdfLaTeX, bib), model, NativeCompilerEngine.PdfLaTeX);
        Assert.Equal(CompileOutputStatus.Succeeded, result.Status);
        Assert.Equal(bib ? 1 : 0, model.BibtexPasses); Assert.Equal(bib ? 3 : 2, model.TexPasses);
    });

    [BibliographyLegacyFixtureTheory]
    [InlineData(NativeCompilerEngine.PdfLaTeX)] [InlineData(NativeCompilerEngine.LuaLaTeX)]
    public void Profile6_keeps_old_outputs_and_one_shot_backend_behavior(NativeCompilerEngine engine) => LifecycleTestHost.Run(async () =>
    {
        const NativeRuntimeProfile old = NativeRuntimeProfile.LocalCompilerCompatibility;
        await using var f = await CompilationTestWorkspace.Open(); await Prepare(f);
        var model = new Model { Biblatex = false, EchoBackendInputs = true, AuxVersion = pass => pass == 1 ? "1" : "2" };
        var result = await Execute(await Context(f, engine, profile: old), model, engine, old);
        Assert.Equal(CompileOutputStatus.Succeeded, result.Status); Assert.Equal(1, model.BibtexPasses);
        Assert.All(model.Calls, c => Assert.DoesNotContain(c.Outputs, n => n.EndsWith("-blx.bib", StringComparison.Ordinal)));
        if (engine == NativeCompilerEngine.LuaLaTeX) Assert.Equal("abi2", Package(old).EngineFor(engine).Version);
    });

    [BibliographyFixtureTheory]
    [InlineData("missing-aux", "missing-native-aux")] [InlineData("empty-aux", "missing-native-aux")]
    [InlineData("missing-blx", "missing-native-blx")] [InlineData("empty-blx", "missing-native-blx")]
    [InlineData("missing-bst", "missing-bibliography-style")] [InlineData("oversize", "invalid-native-reply")]
    [InlineData("state-total", "v4-state-budget")]
    [InlineData("unknown-output", "invalid-native-reply")] [InlineData("duplicate", "invalid-native-reply")]
    [InlineData("wrong-hash", "invalid-native-reply")] [InlineData("wrong-input", "invalid-native-reply")]
    [InlineData("wrong-tag", "invalid-native-reply")] [InlineData("biber", "biber-unavailable")]
    [InlineData("native", "native-latex-failed")]
    public void Malformed_or_missing_first_pass_controls_never_reach_backend(string kind, string code) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); await Prepare(f);
        var model = new Model { Mutate = (request, reply) =>
        {
            string job = request.Outputs.Single(n => n.EndsWith(".pdf", StringComparison.Ordinal))[..^4];
            string target = kind.Contains("aux", StringComparison.Ordinal) ? job + ".aux" : job.Replace(' ', '_') + "-blx.bib";
            return kind switch
            {
                "missing-aux" or "missing-blx" => reply with { Files = reply.Files.Where(x => x.Name != target).ToArray() },
                "empty-aux" or "empty-blx" => reply with { Files = reply.Files.Select(x => x.Name == target ? x with { Bytes = [] } : x).ToArray() },
                "missing-bst" => reply with { Files = reply.Files.Select(x => x.Name == job + ".aux" ? x with { Bytes = Bytes("\\bibstyle{unavailable-style}\n\\bibdata{references}\n") } : x).ToArray() },
                "oversize" => reply with { Files = reply.Files.Select(x => x.Name == job.Replace(' ', '_') + "-blx.bib" ? x with { Bytes = new byte[8388609] } : x).ToArray() },
                "state-total" => reply with { Files = reply.Files.Select(x => x.Name.EndsWith(".aux", StringComparison.Ordinal) || x.Name.EndsWith("-blx.bib", StringComparison.Ordinal)
                    ? x with { Bytes = new byte[3 * 1024 * 1024] } : x).ToArray() },
                "unknown-output" => reply with { Files = [.. reply.Files, new("arbitrary.bib", [1])] },
                "duplicate" => reply with { Files = [.. reply.Files, reply.Files[0]] },
                "wrong-hash" => reply with { WasmSha256 = new string('0',64) },
                "wrong-input" => reply with { Inputs = [] },
                "wrong-tag" => reply with { Tag = reply.Tag with { Nonce = "late" } },
                "biber" => reply with { Log = "Please (re)run Biber on the file\n" },
                _ => reply with { NativeReturn = 1 }
            };
        } };
        var result = await Execute(await Context(f, NativeCompilerEngine.PdfLaTeX), model, NativeCompilerEngine.PdfLaTeX);
        Assert.NotEqual(CompileOutputStatus.Succeeded, result.Status); Assert.Null(result.Payload);
        Assert.Contains(result.Diagnostics!, d => d.Code == code);
        Assert.Equal(0, model.BibtexPasses);
    });

    [BibliographyFixtureTheory]
    [InlineData("empty")] [InlineData("missing")] [InlineData("native")]
    [InlineData("tag")] [InlineData("reclassify")] [InlineData("overlimit")]
    public void Failed_backend_does_not_authorize_a_pdf_or_a_followup_tex(string kind) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); await Prepare(f);
        var model = new Model { EchoBackendInputs = kind == "reclassify", Mutate = (request, reply) =>
        {
            if (request.Tag.Tool != "bibtex") return reply;
            return kind switch
            {
                "empty" => reply with { Files = reply.Files.Select(x => x.Name.EndsWith(".bbl", StringComparison.Ordinal) ? x with { Bytes = [] } : x).ToArray() },
                "missing" => reply with { Files = reply.Files.Where(x => !x.Name.EndsWith(".bbl", StringComparison.Ordinal)).ToArray() },
                "native" => reply with { NativeReturn = 1 },
                "tag" => reply with { Tag = reply.Tag with { Ordinal = 999 } },
                "overlimit" => reply with { Files = reply.Files.Select(x => x.Name.EndsWith(".bbl", StringComparison.Ordinal) ? x with { Bytes = new byte[8388609] } : x).ToArray() },
                _ => reply with { Files = reply.Files.Select(x => x.Name.EndsWith(".aux", StringComparison.Ordinal) ? x with { Bytes = [7] } : x).ToArray() }
            };
        } };
        var result = await Execute(await Context(f, NativeCompilerEngine.LuaLaTeX), model, NativeCompilerEngine.LuaLaTeX);
        Assert.NotEqual(CompileOutputStatus.Succeeded, result.Status); Assert.Null(result.Payload);
        Assert.Equal(1, model.TexPasses); Assert.Equal(1, model.BibtexPasses);
    });

    [BibliographyFixtureTheory]
    [InlineData("my_thesis-blx.bib", "Україна/my thesis.tex")]
    [InlineData("MY_THESIS-BLX.BIB", "Україна/my thesis.tex")]
    [InlineData("my_thesis-blx.bib/nested.tex", "Україна/my thesis.tex")]
    [InlineData("my_thesis-blx.bib", "Україна/my_thesis.tex")]
    [InlineData("__OOP_RUNTIME__/biblatex.bst", "Україна/my thesis.tex")]
    public void Generated_project_collision_fails_before_transport(string collision, string root) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); await Prepare(f, root);
        Assert.True(f.Workspace.CreateFile(collision, "captured project bytes"));
        var model = new Model();
        var context = await Context(f, NativeCompilerEngine.PdfLaTeX); string before = f.State();
        var result = await Execute(context, model, NativeCompilerEngine.PdfLaTeX);
        Assert.NotEqual(CompileOutputStatus.Succeeded, result.Status); Assert.Empty(model.Calls);
        Assert.Equal(before, f.State());
        Assert.Equal("captured project bytes", Encoding.UTF8.GetString(context.Snapshot.Files.Single(p => p.RelativePath == collision).ToArray()));
    });

    [BibliographyFixtureFact]
    public void Original_space_variant_is_not_a_second_generated_alias() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); await Prepare(f);
        Assert.True(f.Workspace.CreateFile("my thesis-blx.bib", "captured ordinary BIB remains project data"));
        var model = new Model();
        var result = await Execute(await Context(f, NativeCompilerEngine.PdfLaTeX), model, NativeCompilerEngine.PdfLaTeX);
        Assert.Equal(CompileOutputStatus.Succeeded, result.Status);
        Assert.All(model.Calls, call => Assert.DoesNotContain("my thesis-blx.bib", call.Outputs));
        Assert.All(model.Calls, call => Assert.DoesNotContain("my thesis-blx.bib", call.Generated.Keys));
    });

    [BibliographyFixtureFact]
    public void Forged_unavailable_biber_selection_never_falls_back_to_bibtex() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); await Prepare(f);
        const NativeCompilerEngine engine = NativeCompilerEngine.LuaLaTeX;
        var package = Package();
        f.Selection.SetSelection(CompilationSelection.Create(new(f.Main, package.EngineFor(engine), [package.Bundle],
            [new("biber", "unavailable", package.Bibtex.Hash)], NativeCompilerSuiteContract.SelectionOptions(engine)), 65536).Selection!);
        var context = CompileRequestContext.Create(await f.Snapshot(new(64, 8388608, 16777216, 131072)), 1, 65536,
            NativeRuntimePackage.Pdf, package.AdapterFor(engine)).Context!;
        var model = new Model(); var result = await Execute(context, model, engine);
        Assert.Equal(CompileOutputStatus.Unsupported, result.Status); Assert.Empty(model.Calls); Assert.Null(result.Payload);
    });

    [BibliographyFixtureTheory]
    [InlineData("cancel")] [InlineData("backend")] [InlineData("root")] [InlineData("revise")]
    public void Terminated_backend_wait_keeps_actual_slot_and_late_result_never_publishes(string change) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); await Prepare(f);
        const NativeCompilerEngine engine = NativeCompilerEngine.LuaLaTeX;
        var context = await Context(f, engine); var model = new Model { HoldTool = "bibtex" };
        var factory = Factory(model, engine); var id = new CompileJobId(Guid.NewGuid()); var worker = factory.Create(id, 1);
        var task = worker.ExecuteAsync(id, context, Budget, default)!;
        await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            if (change == "backend") _ = await Context(f, engine, false);
            if (change == "root") Assert.True(f.Workspace.RenameFile(f.Main, "other.tex"));
            if (change == "revise") Assert.True(f.Workspace.EditText(f.Main, "changed source"));
            worker.RequestTermination(id, 1, CompileLifecycleReason.Cancelled);
            var result = await task;
            Assert.Equal(CompileOutputStatus.Cancelled, result!.Status); Assert.Null(result.Payload);
            Assert.False(factory.Operations.WhenIdle.IsCompleted); Assert.Equal(1, factory.Operations.Active);
            Assert.Equal(1, model.Stops); Assert.Equal(1, model.TexPasses);
        }
        finally { model.Release.TrySetResult(model.Pending!); await factory.Operations.WhenIdle; }
        Assert.Equal(1, model.TexPasses);
    });
}
