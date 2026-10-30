using System.Text;
using Client.Compilation.Lifecycle;

namespace Client.Compilation.Native;

/// <summary>Unregistered adapter for the EXISTING B3 port. C# alone selects finite
/// passes/tools and constructs raw output using the ORIGINAL context. B3 itself
/// runs B2a Create/TryAccept and freshness; this class never constructs acceptance.</summary>
public sealed class NativeCompilationWorkerFactory : ICompilationWorkerFactory
{
    private readonly INativeRuntimeProvider? _provider;
    private readonly INativePhaseTransport? _transport;
    private readonly NativeCompilePlan _plan;
    public NativeForeignOperations Operations { get; }
    public event Action<NativePhaseObservation>? PhaseObserved;
    public NativeCompilationWorkerFactory(INativeRuntimeProvider? provider = null, INativePhaseTransport? transport = null,
        NativeCompilePlan? plan = null, NativeForeignOperations? operations = null)
    {
        _provider = provider; _transport = transport; _plan = plan ?? new(true, 4, TimeSpan.FromSeconds(25));
        if (!_plan.IsValid) throw new ArgumentOutOfRangeException(nameof(plan));
        Operations = operations ?? new(3);
    }
    public ICompilationWorker Create(CompileJobId jobId, long epoch) => new Worker(this, jobId, epoch);
    private void Observe(NativePhaseObservation observation)
    {
        try { PhaseObserved?.Invoke(observation); }
        catch (Exception error) when (error is not OutOfMemoryException) { /* observer cannot change ownership */ }
    }
    private static bool HasLogWord(ReadOnlySpan<char> text, ReadOnlySpan<char> word)
    {
        // Package names such as rerunfilecheck.sty are input records, not a
        // request for another pass. Keep genuine standalone warning words.
        int offset = 0;
        while (offset <= text.Length - word.Length)
        {
            int found = text[offset..].IndexOf(word, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;
            int start = offset + found, end = start + word.Length;
            if ((start == 0 || !Identifier(text[start - 1])) && (end == text.Length || !Identifier(text[end]))) return true;
            offset = end;
        }
        return false;
        static bool Identifier(char value) => char.IsLetterOrDigit(value) || value == '_';
    }
    private sealed class Worker(NativeCompilationWorkerFactory factory, CompileJobId id, long epoch) : ICompilationWorker
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly object _dispatch = new();
        private int _started, _terminated;
        public void RequestTermination(CompileJobId jobId, long workerEpoch, CompileLifecycleReason reason)
        {
            if (jobId != id || workerEpoch != epoch) return;
            lock (_dispatch)
            {
                if (Interlocked.Exchange(ref _terminated, 1) != 0) return;
                try { factory._transport?.RequestTermination(id.Value.ToString("N"), epoch); }
                finally { try { _ = _stop.CancelAsync(); } catch (ObjectDisposedException) { } }
            }
        }
        public async Task<CompileOutputEnvelopeInput?> ExecuteAsync(CompileJobId jobId, CompileRequestContext context,
            CompileOutputBudget limits, CancellationToken cancellationToken)
        {
            if (context is null || limits is null || !limits.IsValid || jobId != id || context.WorkerEpoch != epoch
                || Interlocked.Exchange(ref _started, 1) != 0) return null;
            var log = new StringBuilder();
            CompileOutputEnvelopeInput Finish(CompileOutputStatus status, string code, byte[]? payload = null, byte[]? map = null) => new()
            {
                RequestId = context.RequestId,
                SnapshotIdentity = context.SnapshotIdentity,
                InputContentDigest = context.InputContentDigest,
                WorkerEpoch = context.WorkerEpoch,
                Status = status,
                Payload = payload,
                ClaimedLength = payload?.Length ?? 0,
                ClaimedHash = payload is null ? null : NativeRuntimePackage.Digest(payload),
                SourceMapPayload = map,
                SourceMapping = map is null ? null : new(NativeRuntimePackage.Digest(map), map.Length),
                OutputFormat = context.OutputFormat,
                OutputAdapter = context.OutputAdapter,
                LogText = log.ToString(),
                Diagnostics = [new(code, code == "converged" ? "Native passes reached the declared convergence checks." : "No new native artifact is available.")]
            };
            using var timeout = new CancellationTokenSource(factory._plan.JobDeadline);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token, timeout.Token);
            var token = linked.Token;
            void Check() { token.ThrowIfCancellationRequested(); if (Volatile.Read(ref _terminated) != 0) throw new OperationCanceledException(); }
            try
            {
                Check();
                bool suite = NativeRuntimeProfiles.IsSuite(factory._plan.Profile);
                bool bibliographyCarry = NativeRuntimeProfiles.HasBibliographyCarry(factory._plan.Profile);
                var engine = factory._plan.Engine;
                bool xe = suite && engine == NativeCompilerEngine.XeLaTeX;
                bool lua = suite && engine == NativeCompilerEngine.LuaLaTeX;
                bool dviOutput = factory._plan.DviOutput;
                bool convertingOutput = dviOutput || xe;
                bool sourceMapping = factory._plan.EffectivePhaseVersion == NativeSyncTeXContract.Version;
                bool selectedEntry = suite || dviOutput || sourceMapping || factory._plan.EffectivePhaseVersion == NativeSelectedEntryContract.Version;
                bool v4 = selectedEntry || factory._plan.EffectivePhaseVersion == NativeAuthoringV4Contract.Version;
                if (v4 && !(suite ? NativeCompilerSuiteContract.MatchesOptions(context.Snapshot.Selection.Options, engine)
                    : dviOutput ? NativeLatexDviContract.MatchesOptions(context.Snapshot.Selection.Options)
                    : sourceMapping ? NativeSyncTeXContract.MatchesOptions(context.Snapshot.Selection.Options)
                    : selectedEntry ? NativeSelectedEntryContract.MatchesOptions(context.Snapshot.Selection.Options)
                    : NativeAuthoringV4Contract.MatchesOptions(context.Snapshot.Selection.Options)))
                    return Finish(CompileOutputStatus.Unsupported, "unsupported-selection");
                if (v4 && context.Snapshot.Files.Length > NativeAuthoringV4Contract.ProjectFiles)
                    return Finish(CompileOutputStatus.Failed, "v4-project-count-limit");
                if (factory._provider is null || factory._transport is null) return Finish(CompileOutputStatus.Unsupported, "runtime-unavailable");
                var runtime = await factory.Operations.RunAsync(factory._provider.GetAsync, token).ConfigureAwait(false);
                Check();
                if (runtime is null) return Finish(CompileOutputStatus.Unsupported, "runtime-unavailable");
                var snapshot = context.Snapshot;
                var runtimeLengths = (suite ? runtime.SuiteFileLengths(engine) : runtime.FileLengthsFor(dviOutput)).ToArray();
                bool selectionTools = v4 || factory._plan.UseSelectionTools;
                bool runBibtex = selectionTools
                    ? snapshot.Selection.Tools.Contains(runtime.Bibtex)
                    : factory._plan.BibtexRequired;
                if (runtime.Profile != factory._plan.Profile
                    || NativeRuntimeProfiles.Modern(runtime.Profile) && (snapshot.Selection.Bundles.Length != 1 || snapshot.Selection.Bundles[0] != runtime.Bundle)
                    || context.OutputFormat is not null && context.OutputFormat != NativeRuntimePackage.Pdf
                    || context.OutputAdapter is not null && context.OutputAdapter != (suite ? runtime.AdapterFor(engine) : dviOutput ? runtime.DviOutputAdapter : runtime.OutputAdapter)
                    || snapshot.Selection.Engine != (suite ? runtime.EngineFor(engine) : dviOutput ? runtime.DviEngine : runtime.Engine) || !snapshot.Selection.Bundles.Contains(runtime.Bundle)
                    || !selectionTools && factory._plan.BibtexRequired && !snapshot.Selection.Tools.Contains(runtime.Bibtex)
                    || selectionTools && snapshot.Selection.Tools.Any(tool => tool != runtime.Bibtex)
                    || !v4 && snapshot.Selection.Options.Length != 0)
                    return Finish(CompileOutputStatus.Unsupported, "unsupported-selection");
                var main = snapshot.Files.SingleOrDefault(f => f.FileId == snapshot.MainFileId);
                NativeSelectedEntry? entry = null;
                if (selectedEntry)
                {
                    if (main is null) return Finish(CompileOutputStatus.Unsupported, "unsupported-main-entry");
                    if (NativeSelectedEntryContract.ValidateEntry(main.RelativePath) is { } unsupportedEntry)
                        return Finish(CompileOutputStatus.Unsupported, unsupportedEntry);
                    // A supported entry with an invalid derived output namespace is a
                    // failed project admission, not an unsupported compiler selection.
                    string? entryError;
                    entry = suite ? NativeCompilerSuiteContract.CreateEntry(main.RelativePath, snapshot.Files.Select(f => f.RelativePath), engine, out entryError)
                        : NativeSelectedEntryContract.Create(main.RelativePath, snapshot.Files.Select(f => f.RelativePath), out entryError, dviOutput);
                    if (entry is null) return Finish(entryError == "xetex-non-bmp-main-unsupported" ? CompileOutputStatus.Unsupported : CompileOutputStatus.Failed, entryError!);
                    if (bibliographyCarry)
                    {
                        entry = NativeBibliographyContract.ExtendEntry(runtime.Profile, entry);
                        var invalid = NativeBibliographyContract.ValidateProjectNames(snapshot.Files.Select(f => f.RelativePath))
                            ?? NativeBibliographyContract.ValidateNamespace(entry, runtimeLengths.Select(f => f.Name)
                                .Concat(snapshot.Files.Select(f => f.RelativePath)), []);
                        if (invalid is not null) return Finish(CompileOutputStatus.Failed, invalid);
                    }
                }
                else if (main?.RelativePath != "main.tex") return Finish(CompileOutputStatus.Unsupported, "unsupported-main-entry");
                bool IsState(string name) => entry?.IsState(name) ?? NativeAuthoringV4Contract.IsState(name);
                string pdfName = entry?.Pdf ?? "main.pdf", auxName = entry?.Aux ?? "main.aux", bblName = entry?.Bbl ?? "main.bbl";
                if (suite && snapshot.Files.Any(f => f.RelativePath.Equals(NativeCompilerSuiteContract.ResourcePrefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                    || f.RelativePath.StartsWith(NativeCompilerSuiteContract.ResourcePrefix, StringComparison.OrdinalIgnoreCase)))
                    return Finish(CompileOutputStatus.Failed, "reserved-runtime-namespace");
                if (suite && snapshot.Files.Any(f => new[] { NativeRuntimePackage.FormatResourceName, NativeRuntimePackage.NativeFormatName,
                    "latex-dvi.fmt", "xelatex.fmt", "lualatex.fmt" }.Contains(f.RelativePath, StringComparer.OrdinalIgnoreCase)))
                    return Finish(CompileOutputStatus.Failed, "reserved-format-input");
                if (!suite && snapshot.Files.Any(f => f.RelativePath == NativeRuntimePackage.FormatResourceName
                    || dviOutput && f.RelativePath == "latex-dvi.fmt"))
                    return Finish(CompileOutputStatus.Failed, "reserved-format-input");
                if (snapshot.Files.Length > factory._plan.MaxPhaseFiles - runtimeLengths.Length) throw new NativeResourceLimitException();
                var outputs = entry is not null ? entry.Outputs.ToArray()
                    : v4 ? NativeAuthoringV4Contract.SelectedOutputs(snapshot.Files.Select(f => f.RelativePath))
                    : new[] { "main.pdf", "main.aux", "main.bbl", "main.blg", "main.log" }
                    .Concat(snapshot.Files.Where(f => f.RelativePath.EndsWith(".tex", StringComparison.Ordinal) && f.RelativePath != "main.tex")
                        .Select(f => f.RelativePath[..^4] + ".aux")).Distinct(StringComparer.Ordinal).ToArray();
                if (outputs.Length > (v4 ? NativeAuthoringV4Contract.Outputs : 64)
                    || snapshot.Files.Any(f => outputs.Contains(f.RelativePath, StringComparer.Ordinal)))
                    return Finish(CompileOutputStatus.Failed, "generated-input-collision");
                var generated = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                string? previous = null, lastBibtexInput = null; int stable = 0, ordinal = 0;
                for (int pass = 1; pass <= factory._plan.MaxLatexPasses; pass++)
                {
                    var latex = await Run("latex");
                    if (latex is null) return Finish(CompileOutputStatus.Failed, "invalid-native-reply");
                    if (latex.Value.Reply.Outcome != "native-return" || latex.Value.Reply.NativeReturn != 0)
                        return Finish(latex.Value.Reply.Outcome == "transport-timeout" ? CompileOutputStatus.TimedOut : CompileOutputStatus.Failed, "native-latex-failed");
                    var files = latex.Value.Files;
                    if (suite && (latex.Value.Reply.Log.Contains("Please (re)run Biber", StringComparison.OrdinalIgnoreCase)
                        || latex.Value.Reply.Log.Contains("Please rerun Biber", StringComparison.OrdinalIgnoreCase)))
                        return Finish(CompileOutputStatus.Unsupported, "biber-unavailable");
                    if (lua && (!files.TryGetValue(pdfName, out var luaPdf) || luaPdf.Length < 5 || !luaPdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8)))
                        return Finish(CompileOutputStatus.Failed, "missing-native-pdf");
                    Carry(files);
                    string? bibtexInput = null;
                    if (bibliographyCarry && runBibtex)
                    {
                        var invalid = NativeBibliographyContract.ControlInputIssue(entry!, generated,
                            snapshot.Files.Select(f => f.RelativePath), runtimeLengths.Select(f => f.Name));
                        if (invalid is not null) return Finish(CompileOutputStatus.Failed, invalid);
                        bibtexInput = NativeBibliographyContract.Fingerprint(entry!, generated, snapshot, runtime.Identity.Hex, runtime.Bibtex);
                    }
                    if (runBibtex && (bibliographyCarry ? bibtexInput != lastBibtexInput : pass == 1))
                    {
                        if (!generated.TryGetValue(auxName, out var aux) || selectedEntry && aux.Length == 0)
                            return Finish(CompileOutputStatus.Failed, "missing-native-aux");
                        var bib = await Run("bibtex");
                        if (bib is null) return Finish(CompileOutputStatus.Failed, "invalid-native-reply");
                        if (bib.Value.Reply.Outcome != "native-return" || bib.Value.Reply.NativeReturn != 0 || !bib.Value.Files.TryGetValue(bblName, out var bbl) || bbl.Length == 0)
                            return Finish(bib.Value.Reply.Outcome == "transport-timeout" ? CompileOutputStatus.TimedOut : CompileOutputStatus.Failed, "native-bibtex-failed");
                        if (bibliographyCarry)
                        {
                            // BibTeX cannot delete/reclassify the TeX control state.
                            // Actual ports may return just BBL/BLG or unchanged seeded
                            // outputs; preserve the prior AUX/BLX in either case.
                            foreach (var file in bib.Value.Files.Where(p => IsState(p.Key) && p.Key != bblName))
                                if (!generated.TryGetValue(file.Key, out var prior) || !prior.AsSpan().SequenceEqual(file.Value))
                                    return Finish(CompileOutputStatus.Failed, "bibliography-control-reclassified");
                            generated[bblName] = bbl;
                            ValidateState(generated.Select(f => (f.Key, f.Value.Length)));
                            lastBibtexInput = bibtexInput;
                            previous = null; stable = 0;
                        }
                        else Carry(bib.Value.Files);
                        continue;
                    }
                    var fingerprint = v4
                        ? string.Join("\n", outputs.Where(IsState).Order(StringComparer.Ordinal)
                            .Select(name => name + ":" + (generated.TryGetValue(name, out var state) ? NativeRuntimePackage.Digest(state).Hex : "absent")))
                        : string.Join("\n", generated.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + ":" + NativeRuntimePackage.Digest(x.Value).Hex));
                    stable = previous == fingerprint ? stable + 1 : 0; previous = fingerprint;
                    bool warnings = HasLogWord(latex.Value.Reply.Log, "undefined")
                        || HasLogWord(latex.Value.Reply.Log, "rerun")
                        || latex.Value.Reply.Log.Contains("Label(s) may have changed", StringComparison.Ordinal);
                    if (stable >= 1 && pass >= (runBibtex ? 3 : 2) && !warnings)
                    {
                        Check();
                        if (convertingOutput)
                        {
                            // Only this final successful pass authorizes conversion. DVI is
                            // never state, and a compiler-supplied PDF cannot substitute for it.
                            if (!files.TryGetValue(entry!.JobName + (xe ? ".xdv" : ".dvi"), out var dvi) || dvi.Length == 0
                                || xe && (dvi.Length < 2 || dvi[0] != 247 || dvi[1] != 7))
                                return Finish(CompileOutputStatus.Failed, xe ? "missing-native-xdv" : "missing-native-dvi");
                            var conversion = await Run("dvipdfmx", dvi);
                            Check();
                            if (conversion is null) return Finish(CompileOutputStatus.Failed, "invalid-dvi-reply");
                            if (conversion.Value.Reply.Outcome != "native-return" || conversion.Value.Reply.NativeReturn != 0)
                                return Finish(conversion.Value.Reply.Outcome == "transport-timeout" ? CompileOutputStatus.TimedOut : CompileOutputStatus.Failed, "native-dvi-conversion-failed");
                            if (!conversion.Value.Files.TryGetValue(pdfName, out var converted) || converted.Length < 5
                                || !converted.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
                                return Finish(CompileOutputStatus.Failed, "missing-converted-pdf");
                            return Finish(CompileOutputStatus.Succeeded, "converged", converted.ToArray());
                        }
                        if (!files.TryGetValue(pdfName, out var pdf) || pdf.Length < 5 || !pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) continue;
                        // Retain only this final LaTeX reply's map. Earlier maps
                        // never enter carried state or survive a failed pass.
                        var map = latex.Value.Reply.SourceMapPayload;
                        return Finish(CompileOutputStatus.Succeeded, "converged", pdf.ToArray(), map?.ToArray());
                    }
                }
                return Finish(CompileOutputStatus.Failed, "pass-limit-unknown");

                void ValidateState(IEnumerable<(string Name, int Length)> state)
                {
                    var entries = state.ToArray();
                    if (entries.Length > NativeAuthoringV4Contract.CarriedFiles
                        || entries.Length + runtimeLengths.Length + snapshot.Files.Length > factory._plan.MaxPhaseFiles
                        || entries.Any(f => f.Length > factory._plan.ProjectFileBytes)
                        || entries.Sum(f => (long)f.Length) > NativeAuthoringV4Contract.StateBytes
                        || entries.Sum(f => (long)f.Length) + runtimeLengths.Sum(f => (long)f.Length)
                            + snapshot.Files.Sum(f => f.ByteLength) > factory._plan.MaxInputBytes
                        || suite && entries.Sum(f => (long)f.Length) + snapshot.Files.Sum(f => f.ByteLength) > 32 * 1024 * 1024)
                        throw new NativeAuthoringV4Exception("v4-state-budget");
                }
                void Carry(Dictionary<string, byte[]> files)
                {
                    if (v4)
                    {
                        // Complete observed replacement, including absence/deletion; no accumulated high-water state.
                        var next = files.Where(x => IsState(x.Key)).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
                        ValidateState(next.Select(f => (f.Key, f.Value.Length)));
                        generated = next;
                    }
                    else foreach (var pair in files.Where(x => x.Key.EndsWith(".aux", StringComparison.Ordinal) || x.Key == "main.bbl")) generated[pair.Key] = pair.Value;
                }
                async Task<(NativePhaseReply Reply, Dictionary<string, byte[]> Files)?> Run(string tool, byte[]? finalDvi = null)
                {
                    Check();
                    bool converting = tool == "dvipdfmx";
                    string[] phaseOutputs = converting ? [pdfName] : outputs;
                    string[]? resourceNames = null;
                    string[]? generatedNames = suite ? (converting ? [] : generated.Keys.Order(StringComparer.Ordinal).ToArray()) : null;
                    var phaseLengths = (suite ? runtime.SuiteFileLengths(tool == "bibtex" ? NativeCompilerEngine.PdfLaTeX : engine) : runtime.FileLengthsFor(dviOutput)).ToArray();
                    Dictionary<string, byte[]> all;
                    if (converting)
                    {
                        if (!convertingOutput || entry is null || finalDvi is null) return null;
                        var resources = runtime.ConverterResourceLengths.Select(f => (Name: suite ? NativeCompilerSuiteContract.ResourcePrefix + f.Name : f.Name, f.Length)).ToArray();
                        string intermediate = entry.JobName + (xe ? ".xdv" : ".dvi");
                        var project = snapshot.Files.Select(f => (Name: f.RelativePath, Length: f.ByteLength)).ToArray();
                        // All budgets and namespace checks precede ANY byte copies.
                        if (resources.Length + project.Length + 1 > NativeLatexDviContract.Files
                            || resources.Any(f => f.Length > NativeLatexDviContract.FileBytes || (suite ? f.Name[NativeCompilerSuiteContract.ResourcePrefix.Length..] : f.Name).Contains('/'))
                            || resources.Sum(f => (long)f.Length) > NativeLatexDviContract.ResourceBytes
                            || project.Any(f => f.Length > NativeLatexDviContract.FileBytes)
                            || finalDvi.Length > NativeLatexDviContract.FileBytes
                            || project.Sum(f => f.Length) + finalDvi.Length > NativeLatexDviContract.ProjectBytes)
                            throw new NativeAuthoringV4Exception("dvi-resource-limit");
                        var conversionInputs = resources.Select(f => f.Name).Concat(project.Select(f => f.Name)).Append(intermediate);
                        var invalid = bibliographyCarry
                            ? NativeSelectedEntryContract.ValidateNamespace(entry with { Outputs = [pdfName], State = [] }, conversionInputs, [])
                            : NativeAuthoringV4Contract.ValidateNamespace(conversionInputs, phaseOutputs, []);
                        if (invalid is not null) throw new NativeAuthoringV4Exception(invalid);
                        resourceNames = resources.Select(f => f.Name).Order(StringComparer.Ordinal).ToArray();
                        all = runtime.CopyConverterResources().ToDictionary(f => suite ? NativeCompilerSuiteContract.ResourcePrefix + f.Name : f.Name, f => f.Bytes, StringComparer.Ordinal);
                        foreach (var file in snapshot.Files) all.Add(file.RelativePath, file.ToArray());
                        all.Add(intermediate, finalDvi.ToArray());
                    }
                    else
                    {
                        if (v4)
                        {
                            var inputNames = phaseLengths.Select(f => f.Name).Concat(snapshot.Files.Select(f => f.RelativePath));
                            var invalid = bibliographyCarry ? NativeBibliographyContract.ValidateNamespace(entry!, inputNames, generated.Keys)
                                : entry is null ? NativeAuthoringV4Contract.ValidateNamespace(inputNames, outputs, generated.Keys)
                                : NativeSelectedEntryContract.ValidateNamespace(entry, inputNames, generated.Keys);
                            if (invalid is not null) throw new NativeAuthoringV4Exception(invalid);
                            ValidateState(generated.Select(f => (f.Key, f.Value.Length)));
                        }
                        if (phaseLengths.Length + snapshot.Files.Length + generated.Count > factory._plan.MaxPhaseFiles) throw new NativeResourceLimitException();
                        var lengths = phaseLengths.Concat(snapshot.Files.Select(f => (f.RelativePath, checked((int)f.ByteLength))))
                            .Concat(generated.Select(f => (f.Key, f.Value.Length))).ToArray();
                        // Names are mapped in FileLengths already. Reject runtime,
                        // source or generated conflicts BEFORE any byte clones/hashes.
                        if (lengths.Select(f => f.Item1).Distinct(StringComparer.Ordinal).Count() != lengths.Length)
                            throw new InvalidDataException("Mapped phase destination collision.");
                        if (lengths.Length > factory._plan.MaxPhaseFiles || lengths.Any(f => f.Item2 > factory._plan.MaxFileBytes)
                            || snapshot.Files.Any(f => f.ByteLength > factory._plan.ProjectFileBytes)
                            || generated.Values.Any(f => f.Length > factory._plan.ProjectFileBytes)
                            || lengths.Sum(f => (long)f.Item2) > factory._plan.MaxInputBytes) throw new NativeResourceLimitException();
                        if (suite)
                        {
                            int resourceCap = tool == "bibtex" ? NativeCompilerSuiteContract.TotalBytes : lua ? 96 * 1024 * 1024 : xe ? 160 * 1024 * 1024 : NativeCompilerSuiteContract.TotalBytes;
                            int resourceCount = tool == "latex" && lua ? 4608 : NativeCompilerSuiteContract.Files;
                            if (phaseLengths.Length > resourceCount || phaseLengths.Sum(f => (long)f.Length) > resourceCap
                                || snapshot.Files.Sum(f => f.ByteLength) + generated.Sum(f => (long)f.Value.Length) > 32 * 1024 * 1024)
                                throw new NativeResourceLimitException();
                            resourceNames = phaseLengths.Select(f => f.Name).Order(StringComparer.Ordinal).ToArray();
                        }
                        all = (suite ? runtime.CopySuiteFiles(tool == "bibtex" ? NativeCompilerEngine.PdfLaTeX : engine) : runtime.CopyFiles(dviOutput))
                            .ToDictionary(f => f.Name, f => f.Bytes, StringComparer.Ordinal);
                        foreach (var file in snapshot.Files)
                        {
                            if (all.ContainsKey(file.RelativePath)) throw new InvalidDataException("Runtime/project collision.");
                            all.Add(file.RelativePath, file.ToArray());
                        }
                        foreach (var file in generated) all.Add(file.Key, file.Value.ToArray());
                        if (all.Count > factory._plan.MaxPhaseFiles || all.Values.Any(b => b.Length > factory._plan.MaxFileBytes)
                            || all.Values.Sum(b => (long)b.Length) > factory._plan.MaxInputBytes) throw new NativeResourceLimitException();
                    }
                    var tag = new NativePhaseTag(id.Value.ToString("N"), context.RequestId.ToString(), epoch,
                        context.SnapshotIdentity.CaptureId, context.InputContentDigest.Hex, ++ordinal, tool, Guid.NewGuid().ToString("N"), context.SnapshotIdentity,
                        factory._plan.EffectivePhaseVersion, runtime.Profile,
                        runtime.Profile == NativeRuntimeProfile.Legacy ? null : runtime.Identity.Hex,
                        suite ? NativeCompilerSuiteContract.EngineId(engine) : dviOutput ? "latex-dvi" : null);
                    var sent = all.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new NativeInputFact(x.Key, x.Value.Length, NativeRuntimePackage.Digest(x.Value).Hex)).ToArray();
                    string component = converting ? "dvi-converter" : suite && tool == "latex" && xe ? "xetex" : suite && tool == "latex" && lua ? "luatex" : "engine";
                    var selectedComponent = suite && !converting
                        ? NativeCompilerComponents.For(runtime.Profile, tool == "bibtex" ? NativeCompilerEngine.PdfLaTeX : engine) : null;
                    string loaderHash = converting ? runtime.ConverterLoaderHash
                        : selectedComponent?.LoaderHash ?? NativeRuntimeProfiles.CompilerLoaderHash(runtime.Profile);
                    string wasmHash = converting ? runtime.ConverterWasmHash
                        : selectedComponent?.WasmHash ?? NativeRuntimeProfiles.CompilerWasmHash(runtime.Profile);
                    var toolEntry = converting ? entry!.JobName + (xe ? ".xdv" : ".dvi") : entry is null ? "main.tex" : tool == "latex" ? entry.LatexEntry : entry.BibtexEntry;
                    var request = new NativePhaseRequest(tag, toolEntry, all.Select(x => new NativeFileTransfer(x.Key, x.Value)).ToArray(),
                        phaseOutputs, runtime.Copy(component + ".js"), runtime.Copy(component + ".wasm"),
                        Math.Min(limits.MaxLogBytes, 256 * 1024), converting || lua ? Math.Min(limits.MaxPayloadBytes, NativeLatexDviContract.PdfBytes) : limits.MaxPayloadBytes,
                        entry?.SelectedPath, resourceNames, generatedNames);
                    var reply = await factory.Operations.RunAsync(() => { lock (_dispatch) { Check(); return factory._transport.ExecuteAsync(request); } }, token).ConfigureAwait(false);
                    Check();
                    if (reply is { Outcome: "transport-timeout", NativeReturn: null, Files.Length: 0 } && reply.Tag == tag)
                    {
                        factory.Observe(new(tag, sent, reply)); return (reply, new Dictionary<string, byte[]>(StringComparer.Ordinal));
                    }
                    if (reply is null || reply.Tag != tag || reply.WasmSha256 != wasmHash || reply.LoaderSha256 != loaderHash
                        || reply.Files is null || reply.Inputs is null || reply.Log is null || reply.Inputs.Length != sent.Length
                        || !reply.Inputs.OrderBy(x => x.Name, StringComparer.Ordinal).SequenceEqual(sent) || reply.Files.Length > phaseOutputs.Length
                        || reply.Log.Length > request.MaxLogBytes || Encoding.UTF8.GetByteCount(reply.Log) > request.MaxLogBytes) return null;
                    if (reply.Outcome == "native-return" && (!reply.Entered || reply.NativeReturn is null)
                        || reply.Outcome == "wasm-abort" && (!reply.Entered || reply.NativeReturn is not null)) return null;
                    if (reply.SourceMapPayload is { } mapPayload
                        && (!sourceMapping || tool != "latex" || reply.Outcome != "native-return" || reply.NativeReturn != 0
                            || mapPayload.Length is < 1 or > NativeSyncTeXContract.MaxMapBytes)) return null;
                    long total = 0; var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var f in reply.Files)
                    {
                        if (f is null || f.Bytes is null || !phaseOutputs.Contains(f.Name, StringComparer.Ordinal) || !names.Add(f.Name)
                            || f.Bytes.Length > request.MaxOutputBytes || f.Bytes.Length > 2L * limits.MaxPayloadBytes - total
                            || lua && f.Name == entry!.JobName + ".log" && f.Bytes.Length > request.MaxLogBytes) return null;
                        total += f.Bytes.Length;
                    }
                    if (v4 && !converting) ValidateState(reply.Files.Where(f => IsState(f.Name)).Select(f => (f.Name, f.Bytes.Length)));
                    var owned = reply.Files.ToDictionary(f => f.Name, f => f.Bytes.ToArray(), StringComparer.Ordinal);
                    if (Encoding.UTF8.GetByteCount(log.ToString()) + Encoding.UTF8.GetByteCount(reply.Log) > limits.MaxLogBytes) throw new NativeResourceLimitException();
                    log.Append(reply.Log);
                    reply = reply with { SourceMapPayload = reply.SourceMapPayload?.ToArray() };
                    var observation = reply with { Files = owned.Select(x => new NativeFileTransfer(x.Key, x.Value.ToArray())).ToArray(), Inputs = reply.Inputs.ToArray(),
                        SourceMapPayload = reply.SourceMapPayload?.ToArray() };
                    factory.Observe(new(tag, sent, observation));
                    return (reply, owned);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || Volatile.Read(ref _terminated) != 0)
            { return Finish(timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !_stop.IsCancellationRequested ? CompileOutputStatus.TimedOut : CompileOutputStatus.Cancelled, "native-wait-cancelled"); }
            catch (OperationCanceledException) { return Finish(CompileOutputStatus.Failed, "foreign-cancellation"); }
            catch (NativeAuthoringV4Exception error) { return Finish(CompileOutputStatus.Failed, error.Code); }
            catch (NativeResourceLimitException) { return Finish(CompileOutputStatus.Failed, "foreign-resource-limit"); }
            catch (Exception error) when (error is not OutOfMemoryException) { return Finish(CompileOutputStatus.Failed, "native-transport-error"); }
            finally { _stop.Dispose(); }
        }
    }
}
