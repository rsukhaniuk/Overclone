using System.Collections.Immutable;
using System.Text;
using Client.Compilation.Bibliography;
using Client.Compilation.Lifecycle;
using Client.Compilation.SyncTeX;

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
    private static bool RequiresAnotherPass(string log)
    {
        if (HasLogWord(log, "rerun") || log.Contains("Label(s) may have changed", StringComparison.Ordinal)) return true;
        ReadOnlySpan<char> text = log;
        const string fontShape = "LaTeX Font Warning: Font shape `";
        const string undefined = "' undefined";
        foreach (var range in text.Split('\n'))
        {
            var line = text[range].Trim();
            if (!HasLogWord(line, "undefined")) continue;
            // TeX has already selected a fallback for this exact font warning.
            // Another reference pass cannot repair it. Retain the line in the log.
            if (line.Length > fontShape.Length + undefined.Length
                && line.StartsWith(fontShape, StringComparison.Ordinal) && line.EndsWith(undefined, StringComparison.Ordinal)
                && line[fontShape.Length..^undefined.Length].IndexOfAny('`', '\'') < 0) continue;
            return true;
        }
        return false;
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
            CompileOutputEnvelopeInput Finish(CompileOutputStatus status, string code, byte[]? payload = null, byte[]? map = null,
                SourceMapGeometryArtifact? geometry = null, bool navigationLimited = false) => new()
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
                SourceMapGeometryPayload = geometry?.ToArray(),
                SourceMapGeometry = geometry?.Identity,
                OutputFormat = context.OutputFormat,
                OutputAdapter = context.OutputAdapter,
                LogText = log.ToString(),
                Diagnostics = navigationLimited
                    ? [new(code, "Native passes reached the declared convergence checks."), new(NativeNavigationContract.LimitationCode, NativeNavigationContract.LimitationMessage)]
                    : [new(code, code == "converged" ? "Native passes reached the declared convergence checks." : "No new native artifact is available.")]
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
                bool measuredMapping = NativeNavigationContract.IsMapped(factory._plan.Profile, factory._plan.EffectivePhaseVersion, engine);
                bool directPdfMapping = NativeNavigationContract.IsDirectPdfMapped(factory._plan.Profile, factory._plan.EffectivePhaseVersion, engine);
                bool sourceMapping = measuredMapping || factory._plan.EffectivePhaseVersion == NativeSyncTeXContract.Version
                    || NativeRuntimeProfiles.SupportsBiber(factory._plan.Profile) && engine == NativeCompilerEngine.PdfLaTeX;
                bool selectedEntry = suite || dviOutput || sourceMapping || factory._plan.EffectivePhaseVersion == NativeSelectedEntryContract.Version;
                bool v4 = selectedEntry || factory._plan.EffectivePhaseVersion == NativeAuthoringV4Contract.Version;
                if (v4 && !(suite ? NativeCompilerSuiteContract.MatchesOptions(context.Snapshot.Selection.Options, factory._plan.Profile, engine)
                    : dviOutput ? NativeLatexDviContract.MatchesOptions(context.Snapshot.Selection.Options)
                    : sourceMapping ? NativeSyncTeXContract.MatchesOptions(context.Snapshot.Selection.Options)
                    : selectedEntry ? NativeSelectedEntryContract.MatchesOptions(context.Snapshot.Selection.Options)
                    : NativeAuthoringV4Contract.MatchesOptions(context.Snapshot.Selection.Options)))
                    return Finish(CompileOutputStatus.Unsupported, "unsupported-selection");
                if (v4 && context.Snapshot.Files.Length > NativeRuntimeProfiles.ProjectFileLimit(factory._plan.Profile))
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
                bool runBiber = selectionTools && runtime.HasBiber && snapshot.Selection.Tools.Contains(runtime.Biber);
                string bibliographyTool = runBiber ? "biber" : runBibtex ? "bibtex" : "none";
                if (runtime.Profile != factory._plan.Profile
                    || NativeRuntimeProfiles.Modern(runtime.Profile) && (snapshot.Selection.Bundles.Length != 1 || snapshot.Selection.Bundles[0] != runtime.Bundle)
                    || context.OutputFormat is not null && context.OutputFormat != NativeRuntimePackage.Pdf
                    || context.OutputAdapter is not null && context.OutputAdapter != (suite ? runtime.AdapterFor(engine) : dviOutput ? runtime.DviOutputAdapter : runtime.OutputAdapter)
                    || snapshot.Selection.Engine != (suite ? runtime.EngineFor(engine) : dviOutput ? runtime.DviEngine : runtime.Engine) || !snapshot.Selection.Bundles.Contains(runtime.Bundle)
                    || !selectionTools && factory._plan.BibtexRequired && !snapshot.Selection.Tools.Contains(runtime.Bibtex)
                    || selectionTools && (snapshot.Selection.Tools.Length > 1
                        || snapshot.Selection.Tools.Any(tool => tool != runtime.Bibtex && (!runtime.HasBiber || tool != runtime.Biber)))
                    || !v4 && snapshot.Selection.Options.Length != 0)
                    return Finish(CompileOutputStatus.Unsupported, "unsupported-selection");
                var main = snapshot.Files.SingleOrDefault(f => f.FileId == snapshot.MainFileId);
                NativeSelectedEntry? entry = null;
                if (selectedEntry)
                {
                    if (main is null) return Finish(CompileOutputStatus.Unsupported, "unsupported-main-entry");
                    if (NativeSelectedEntryContract.ValidateEntry(main.RelativePath, factory._plan.Profile, engine) is { } unsupportedEntry)
                        return Finish(CompileOutputStatus.Unsupported, unsupportedEntry);
                    // A supported entry with an invalid derived output namespace is a
                    // failed project admission, not an unsupported compiler selection.
                    string? entryError;
                    entry = suite ? NativeCompilerSuiteContract.CreateEntry(main.RelativePath, snapshot.Files.Select(f => f.RelativePath), engine, out entryError, factory._plan.Profile)
                        : NativeSelectedEntryContract.Create(main.RelativePath, snapshot.Files.Select(f => f.RelativePath), out entryError, dviOutput);
                    if (entry is null) return Finish(entryError == "xetex-non-bmp-main-unsupported" ? CompileOutputStatus.Unsupported : CompileOutputStatus.Failed, entryError!);
                    if (bibliographyCarry)
                    {
                        entry = runBiber ? NativeBiberCompilerContract.ExtendEntry(entry)
                            : NativeBibliographyContract.ExtendEntry(runtime.Profile, entry);
                        var invalid = NativeBibliographyContract.ValidateProjectNames(snapshot.Files.Select(f => f.RelativePath))
                            ?? (runBiber ? NativeBiberCompilerContract.ValidateNamespace(entry, runtimeLengths.Select(f => f.Name)
                                .Concat(snapshot.Files.Select(f => f.RelativePath)), [])
                                : NativeBibliographyContract.ValidateNamespace(entry, runtimeLengths.Select(f => f.Name)
                                .Concat(snapshot.Files.Select(f => f.RelativePath)), []));
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
                if (outputs.Length > (v4 ? NativeRuntimeProfiles.OutputDeclarationLimit(factory._plan.Profile) : 64)
                    || factory._plan.Profile is NativeRuntimeProfile.LocalCompilerPageLabels or NativeRuntimeProfile.LocalCompilerPdfOrigins or NativeRuntimeProfile.LocalCompilerDviGraphics or NativeRuntimeProfile.LocalCompilerCoursework or NativeSelectedPdfContract.Profile or NativeInputBudgetContract.Profile or NativeThesisCompilerContract.Profile or NativeOriginCompilerContract.Profile && lua && !NativeThesisCompilerContract.LuaOutputDeclarationsFit(outputs)
                    || snapshot.Files.Any(f => outputs.Contains(f.RelativePath, StringComparer.Ordinal)))
                    return Finish(CompileOutputStatus.Failed, "generated-input-collision");
                var dviImageSidecars = dviOutput && NativeDviImageSidecarContract.Supports(factory._plan.Profile)
                    ? NativeDviImageSidecarContract.Create(snapshot.Files, outputs, token) : [];
                var generated = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                string? previous = null, lastBibtexInput = null; int stable = 0, ordinal = 0;
                Client.Workspace.Domain.SHA256? producedBibtexBbl = null;
                string? lastBiberInput = null;
                NativeBiberControl? currentBiberControl = null;
                Dictionary<string, byte[]>? currentBiberFiles = null;
                for (int pass = 1; pass <= factory._plan.MaxLatexPasses; pass++)
                {
                    var latex = await Run("latex");
                    if (latex is null) return Finish(CompileOutputStatus.Failed, "invalid-native-reply");
                    if (latex.Value.Reply.Outcome == "font-semantics-unsupported")
                        return Finish(CompileOutputStatus.Unsupported, "font-semantics-unsupported");
                    if (latex.Value.Reply.Outcome != "native-return" || latex.Value.Reply.NativeReturn != 0)
                        return Finish(latex.Value.Reply.Outcome == "transport-timeout" ? CompileOutputStatus.TimedOut : CompileOutputStatus.Failed, "native-latex-failed");
                    var files = latex.Value.Files;
                    if (suite && !runBiber && (latex.Value.Reply.Log.Contains("Please (re)run Biber", StringComparison.OrdinalIgnoreCase)
                        || latex.Value.Reply.Log.Contains("Please rerun Biber", StringComparison.OrdinalIgnoreCase)))
                        return Finish(CompileOutputStatus.Unsupported, runtime.HasBiber ? "biber-not-selected" : "biber-unavailable");
                    if (lua && (!files.TryGetValue(pdfName, out var luaPdf) || luaPdf.Length < 5 || !luaPdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8)))
                        return Finish(CompileOutputStatus.Failed, "missing-native-pdf");
                    Carry(files);
                    if (runBiber)
                    {
                        // Read this exact reply, never carried BCF. BCF/run.xml are
                        // deliberately absent from carry and convergence state.
                        currentBiberControl = NativeBiberControlContract.Read(entry!, files,
                            snapshot.Files.Select(f => f.RelativePath), out var biberIssue);
                        if (currentBiberControl is null) return Finish(CompileOutputStatus.Failed, biberIssue!);
                        currentBiberFiles = files;
                        var biberFingerprint = NativeBiberControlContract.Fingerprint(entry!, currentBiberControl,
                            files, snapshot, runtime.Identity.Hex, runtime.Biber);
                        if (biberFingerprint != lastBiberInput)
                        {
                            var biber = await Run("biber");
                            if (biber is null) return Finish(CompileOutputStatus.Failed, "invalid-native-reply");
                            if (biber.Value.Reply.Outcome != "native-return" || biber.Value.Reply.NativeReturn != 0
                                || !biber.Value.Files.TryGetValue(bblName, out var biberBbl) || biberBbl.Length == 0)
                                return Finish(biber.Value.Reply.Outcome == "transport-timeout" ? CompileOutputStatus.TimedOut : CompileOutputStatus.Failed, "native-biber-failed");
                            generated[bblName] = biberBbl;
                            ValidateState(generated.Select(f => (f.Key, f.Value.Length)));
                            lastBiberInput = biberFingerprint; previous = null; stable = 0;
                            continue;
                        }
                    }
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
                            producedBibtexBbl = bbl.Length <= CompiledBibliographyEvidence.MaxBytes ? NativeRuntimePackage.Digest(bbl) : null;
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
                    if (stable >= 1 && pass >= (runBibtex || runBiber ? 3 : 2) && !RequiresAnotherPass(latex.Value.Reply.Log))
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
                            if (conversion.Value.Reply.Outcome == "font-semantics-unsupported")
                                return Finish(CompileOutputStatus.Unsupported, "font-semantics-unsupported");
                            if (conversion.Value.Reply.Outcome != "native-return" || conversion.Value.Reply.NativeReturn != 0)
                                return Finish(conversion.Value.Reply.Outcome == "transport-timeout" ? CompileOutputStatus.TimedOut : CompileOutputStatus.Failed, "native-dvi-conversion-failed");
                            if (!conversion.Value.Files.TryGetValue(pdfName, out var converted) || converted.Length < 5
                                || !converted.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
                                return Finish(CompileOutputStatus.Failed, "missing-converted-pdf");
                            return Complete(latex.Value.Reply, files, converted.ToArray(), conversion.Value.Reply.SourceMapGeometryPayload, dvi);
                        }
                        if (!files.TryGetValue(pdfName, out var pdf) || pdf.Length < 5 || !pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) continue;
                        // Retain only this final LaTeX reply's map. Earlier maps
                        // never enter carried state or survive a failed pass.
                        return Complete(latex.Value.Reply, files, pdf.ToArray(), latex.Value.Reply.SourceMapGeometryPayload, null);
                    }
                }
                return Finish(CompileOutputStatus.Failed, "pass-limit-unknown");

                CompileOutputEnvelopeInput Complete(NativePhaseReply finalLatex, IReadOnlyDictionary<string, byte[]> files,
                    byte[] pdf, byte[]? sidecar, byte[]? intermediate)
                {
                    var map = finalLatex.SourceMapPayload?.ToArray();
                    var bibliography = entry is not null && producedBibtexBbl is { } produced
                        ? CompiledBibliographyEvidence.Capture(snapshot, finalLatex, entry, files, produced) : null;
                    if (!measuredMapping || directPdfMapping && finalLatex.SourceMapLegacyZeroOrigin == true
                        && map is { Length: > 0 } && sidecar is null)
                        return Finish(CompileOutputStatus.Succeeded, "converged", pdf, map) with { BibliographyEvidence = bibliography };
                    var assembled = NativeNavigationContract.Assemble(engine, finalLatex.Tag, sidecar, map, pdf, intermediate, limits);
                    // Never let optional native data invalidate a usable PDF, or
                    // allow measured engines to fall back to guessed zero origins.
                    var result = assembled.Artifact is { } geometry
                        && (!directPdfMapping || geometry.Geometry.Pages.Any(page => page.Status == "available"))
                        ? Finish(CompileOutputStatus.Succeeded, "converged", pdf, map, geometry)
                        : Finish(CompileOutputStatus.Succeeded, "converged", pdf, navigationLimited: true);
                    return result with { BibliographyEvidence = bibliography };
                }
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
                    bool biberPhase = tool == "biber";
                    var sidecars = tool == "latex" ? dviImageSidecars : [];
                    string[] phaseOutputs = converting ? [pdfName] : biberPhase ? [bblName, entry!.JobName + ".blg"] : outputs;
                    string[]? resourceNames = null;
                    string[]? generatedNames = suite ? (converting ? [] : generated.Keys.Order(StringComparer.Ordinal).ToArray()) : null;
                    var phaseLengths = (suite ? runtime.SuiteFileLengths(tool == "bibtex" ? NativeCompilerEngine.PdfLaTeX : engine) : runtime.FileLengthsFor(dviOutput)).ToArray();
                    Dictionary<string, byte[]> all;
                    if (biberPhase)
                    {
                        if (!runBiber || !runtime.HasBiber || entry is null || currentBiberControl is null || currentBiberFiles is null
                            || !currentBiberFiles.TryGetValue(currentBiberControl.BcfName, out var controlBytes) || controlBytes.Length == 0) return null;
                        var sources = currentBiberControl.Sources.Select(name => snapshot.Files.Single(f => f.RelativePath == name)).ToArray();
                        if (sources.Length + 1 > NativeBiberCompilerContract.InputFiles || controlBytes.Length > NativeBiberCompilerContract.InputBytes
                            || sources.Any(f => f.ByteLength > NativeBiberCompilerContract.InputBytes)
                            || sources.Sum(f => f.ByteLength) + controlBytes.Length > NativeBiberCompilerContract.InputBytes)
                            throw new NativeAuthoringV4Exception("biber-input-quota");
                        var invalid = NativeSelectedEntryContract.ValidateNamespace(entry with { Outputs = phaseOutputs.ToImmutableArray(), State = [] },
                            sources.Select(f => f.RelativePath).Append(currentBiberControl.BcfName), []);
                        if (invalid is not null) throw new NativeAuthoringV4Exception(invalid);
                        resourceNames = []; generatedNames = [currentBiberControl.BcfName];
                        all = sources.ToDictionary(f => f.RelativePath, f => f.ToArray(), StringComparer.Ordinal);
                        all.Add(currentBiberControl.BcfName, controlBytes.ToArray());
                    }
                    else if (converting)
                    {
                        if (!convertingOutput || entry is null || finalDvi is null) return null;
                        var resources = runtime.ConverterResourceLengthsFor(engine).Select(f => (Name: suite ? NativeCompilerSuiteContract.ResourcePrefix + f.Name : f.Name, f.Length)).ToArray();
                        string intermediate = entry.JobName + (xe ? ".xdv" : ".dvi");
                        var project = snapshot.Files.Select(f => (Name: f.RelativePath, Length: f.ByteLength)).ToArray();
                        // All budgets and namespace checks precede ANY byte copies.
                        if (resources.Length + project.Length + 1 > NativeLatexDviContract.Files
                            || resources.Any(f => f.Length > NativeLatexDviContract.FileBytes || (suite ? f.Name[NativeCompilerSuiteContract.ResourcePrefix.Length..] : f.Name).Contains('/'))
                            || resources.Sum(f => (long)f.Length) > NativeLatexDviContract.ResourceBytes
                            || project.Any(f => f.Length > NativeLatexDviContract.FileBytes)
                            || finalDvi.Length > NativeLatexDviContract.FileBytes
                            || project.Sum(f => f.Length) + finalDvi.Length > factory._plan.ConverterProjectBytes)
                            throw new NativeAuthoringV4Exception("dvi-resource-limit");
                        var conversionInputs = resources.Select(f => f.Name).Concat(project.Select(f => f.Name)).Append(intermediate);
                        var invalid = bibliographyCarry
                            ? NativeSelectedEntryContract.ValidateNamespace(entry with { Outputs = [pdfName], State = [] }, conversionInputs, [])
                            : NativeAuthoringV4Contract.ValidateNamespace(conversionInputs, phaseOutputs, []);
                        if (invalid is not null) throw new NativeAuthoringV4Exception(invalid);
                        resourceNames = resources.Select(f => f.Name).Order(StringComparer.Ordinal).ToArray();
                        all = runtime.CopyConverterResources(engine).ToDictionary(f => suite ? NativeCompilerSuiteContract.ResourcePrefix + f.Name : f.Name, f => f.Bytes, StringComparer.Ordinal);
                        foreach (var file in snapshot.Files) all.Add(file.RelativePath, file.ToArray());
                        all.Add(intermediate, finalDvi.ToArray());
                    }
                    else
                    {
                        if (v4)
                        {
                            var inputNames = phaseLengths.Select(f => f.Name).Concat(snapshot.Files.Select(f => f.RelativePath))
                                .Concat(sidecars.Select(f => f.Lease.Name));
                            var invalid = runBiber ? NativeBiberCompilerContract.ValidateNamespace(entry!, inputNames, generated.Keys)
                                : bibliographyCarry ? NativeBibliographyContract.ValidateNamespace(entry!, inputNames, generated.Keys)
                                : entry is null ? NativeAuthoringV4Contract.ValidateNamespace(inputNames, outputs, generated.Keys)
                                : NativeSelectedEntryContract.ValidateNamespace(entry, inputNames, generated.Keys);
                            if (invalid is not null) throw new NativeAuthoringV4Exception(invalid);
                            ValidateState(generated.Select(f => (f.Key, f.Value.Length)));
                        }
                        var lengths = phaseLengths.Concat(snapshot.Files.Select(f => (f.RelativePath, checked((int)f.ByteLength))))
                            .Concat(generated.Select(f => (f.Key, f.Value.Length)))
                            .Concat(sidecars.Select(f => (f.Lease.Name, f.Bytes.Length))).ToArray();
                        // Names are mapped in FileLengths already. Reject runtime,
                        // source or generated conflicts BEFORE any byte clones/hashes.
                        if (lengths.Select(f => f.Item1).Distinct(StringComparer.Ordinal).Count() != lengths.Length)
                            throw new InvalidDataException("Mapped phase destination collision.");
                        if (lengths.Length > factory._plan.MaxPhaseFiles || lengths.Any(f => f.Item2 > factory._plan.MaxFileBytes)
                            || snapshot.Files.Any(f => f.ByteLength > factory._plan.SourceFileBytes)
                            || NativeInputBudgetContract.Enabled(factory._plan.Profile) && snapshot.Files.Sum(f => f.ByteLength) > factory._plan.ProjectTotalBytes
                            || generated.Values.Any(f => f.Length > factory._plan.ProjectFileBytes)
                            || lengths.Sum(f => (long)f.Item2) > factory._plan.MaxInputBytes) throw new NativeResourceLimitException();
                        if (suite)
                        {
                            int resourceCap = tool == "bibtex" ? NativeCompilerSuiteContract.TotalBytes : lua ? factory._plan.Profile is NativeUnicodeCompilerContract.Profile or NativeBiberCompilerContract.Profile or NativeThesisCompilerContract.Profile or NativeOriginCompilerContract.Profile or NativeInputBudgetContract.Profile or NativeSelectedPdfContract.Profile or NativeRuntimeProfile.LocalCompilerPageLabels or NativeRuntimeProfile.LocalCompilerPdfOrigins or NativeRuntimeProfile.LocalCompilerDviGraphics or NativeRuntimeProfile.LocalCompilerCoursework ? NativeUnicodeCompilerContract.LuaResourceBytes : 96 * 1024 * 1024 : xe ? 160 * 1024 * 1024 : NativeCompilerSuiteContract.TotalBytes;
                            int resourceCount = tool == "latex" && lua ? 4608 : NativeCompilerSuiteContract.Files;
                            if (phaseLengths.Length > resourceCount || phaseLengths.Sum(f => (long)f.Length) > resourceCap
                                || snapshot.Files.Sum(f => f.ByteLength) + generated.Sum(f => (long)f.Value.Length)
                                    + sidecars.Sum(f => (long)f.Bytes.Length) > 32 * 1024 * 1024)
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
                        foreach (var file in sidecars) all.Add(file.Lease.Name, file.Bytes.ToArray());
                        if (all.Count > factory._plan.MaxPhaseFiles || all.Values.Any(b => b.Length > factory._plan.MaxFileBytes)
                            || all.Values.Sum(b => (long)b.Length) > factory._plan.MaxInputBytes) throw new NativeResourceLimitException();
                    }
                    var tag = new NativePhaseTag(id.Value.ToString("N"), context.RequestId.ToString(), epoch,
                        context.SnapshotIdentity.CaptureId, context.InputContentDigest.Hex, ++ordinal, tool, Guid.NewGuid().ToString("N"), context.SnapshotIdentity,
                        biberPhase ? NativeBiberCompilerContract.BiberPhase : factory._plan.EffectivePhaseVersion, runtime.Profile,
                        runtime.Profile == NativeRuntimeProfile.Legacy ? null : runtime.Identity.Hex,
                        suite ? NativeCompilerSuiteContract.EngineId(engine) : dviOutput ? "latex-dvi" : null);
                    var sent = all.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new NativeInputFact(x.Key, x.Value.Length, NativeRuntimePackage.Digest(x.Value).Hex)).ToArray();
                    string component = biberPhase ? "biber" : converting ? runtime.ConverterStemFor(engine) : suite && tool == "latex" && xe ? "xetex" : suite && tool == "latex" && lua ? "luatex" : "engine";
                    var selectedComponent = suite && !converting && !biberPhase
                        ? tool == "bibtex" && NativeRuntimeProfiles.SupportsBiber(runtime.Profile) ? NativeBiberCompilerContract.BibtexComponent
                            : runtime.ComponentFor(tool == "bibtex" ? NativeCompilerEngine.PdfLaTeX : engine) : null;
                    if (selectedComponent is not null) component = Path.GetFileNameWithoutExtension(selectedComponent.LoaderName);
                    string loaderHash = biberPhase ? NativeCompilerBiberPins.BiberLoaderHash : converting ? runtime.ConverterLoaderHashFor(engine)
                        : selectedComponent?.LoaderHash ?? NativeRuntimeProfiles.CompilerLoaderHash(runtime.Profile);
                    string wasmHash = biberPhase ? NativeCompilerBiberPins.BiberWasmHash : converting ? runtime.ConverterWasmHashFor(engine)
                        : selectedComponent?.WasmHash ?? NativeRuntimeProfiles.CompilerWasmHash(runtime.Profile);
                    var toolEntry = biberPhase ? currentBiberControl!.BcfName : converting ? entry!.JobName + (xe ? ".xdv" : ".dvi") : entry is null ? "main.tex" : tool == "latex" ? entry.LatexEntry : entry.BibtexEntry;
                    var request = new NativePhaseRequest(tag, toolEntry, all.Select(x => new NativeFileTransfer(x.Key, x.Value)).ToArray(),
                        phaseOutputs, runtime.Copy(component + ".js"), runtime.Copy(component + ".wasm"),
                        Math.Min(limits.MaxLogBytes, 256 * 1024), biberPhase ? Math.Min(limits.MaxPayloadBytes, NativeBiberCompilerContract.OutputBytes)
                            : converting || lua ? Math.Min(limits.MaxPayloadBytes, NativeLatexDviContract.PdfBytes)
                                : NativeSelectedPdfContract.PhaseOutputBytes(tag, limits.MaxPayloadBytes),
                        entry?.SelectedPath, resourceNames, generatedNames, biberPhase ? runtime.Copy("biber.data") : null,
                        NativeRuntimeProfiles.SupportsBiber(runtime.Profile) ? bibliographyTool : null,
                        sidecars.Length == 0 ? null : sidecars.Select(f => f.Lease).ToArray());
                    var reply = await factory.Operations.RunAsync(() => { lock (_dispatch) { Check(); return factory._transport.ExecuteAsync(request); } }, token).ConfigureAwait(false);
                    Check();
                    if (reply is { Outcome: "transport-timeout", NativeReturn: null, Files.Length: 0, SourceMapPayload: null, SourceMapGeometryPayload: null, SourceMapLegacyZeroOrigin: null } && reply.Tag == tag)
                    {
                        factory.Observe(new(tag, sent, reply)); return (reply, new Dictionary<string, byte[]>(StringComparer.Ordinal));
                    }
                    if (reply is null || reply.Tag != tag || reply.WasmSha256 != wasmHash || reply.LoaderSha256 != loaderHash
                        || (biberPhase ? reply.BootstrapSha256 != NativeCompilerBiberPins.BiberDataHash : reply.BootstrapSha256 is not null)
                        || reply.Files is null || reply.Inputs is null || reply.Log is null || reply.Inputs.Length != sent.Length
                        || !reply.Inputs.OrderBy(x => x.Name, StringComparer.Ordinal).SequenceEqual(sent) || reply.Files.Length > phaseOutputs.Length
                        || reply.Log.Length > request.MaxLogBytes || Encoding.UTF8.GetByteCount(reply.Log) > request.MaxLogBytes) return null;
                    if (reply.Outcome == "native-return" && (!reply.Entered || reply.NativeReturn is null)
                        || reply.Outcome == "wasm-abort" && (!reply.Entered || reply.NativeReturn is not null)) return null;
                    if (reply.Outcome == "font-semantics-unsupported" && (!NativeRuntimeProfiles.SupportsBiber(runtime.Profile) || tool != "latex"
                        || engine is not (NativeCompilerEngine.PdfLaTeX or NativeCompilerEngine.LaTeXDvi) || !reply.Entered || reply.NativeReturn != 0
                        || reply.Files.Length != 0 || reply.SourceMapPayload is not null || reply.SourceMapGeometryPayload is not null)) return null;
                    bool successful = reply.Outcome == "native-return" && reply.NativeReturn == 0;
                    bool mapCell = sourceMapping && tool == "latex";
                    bool directPdfCell = directPdfMapping && tool == "latex";
                    bool geometryCell = directPdfCell || measuredMapping && (lua && tool == "latex" || (dviOutput || xe) && converting);
                    // Unexpected fields are a malformed transport frame. Within
                    // the new optional cells, bounded bad data is omitted below.
                    if (reply.SourceMapPayload is not null && (!mapCell || !successful)
                        || reply.SourceMapGeometryPayload is not null && (!geometryCell || !successful)
                        || reply.SourceMapLegacyZeroOrigin is not null && (!directPdfCell || !successful)) return null;
                    if (!measuredMapping && reply.SourceMapPayload is { } oldMap
                        && oldMap.Length is < 1 or > NativeSyncTeXContract.MaxMapBytes) return null;
                    long total = 0; var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var f in reply.Files)
                    {
                        if (f is null || f.Bytes is null || !phaseOutputs.Contains(f.Name, StringComparer.Ordinal) || !names.Add(f.Name)
                            || f.Bytes.Length > (!successful && NativeSelectedPdfContract.Supports(request.Tag.Profile)
                                ? Math.Min(request.MaxOutputBytes, NativeSelectedPdfContract.OtherOutputBytes)
                                : NativeSelectedPdfContract.FileOutputBytes(request, f.Name))
                            || f.Bytes.Length > NativeSelectedPdfContract.PhaseAggregateBytes(request, limits.MaxPayloadBytes) - total
                            || lua && f.Name == entry!.JobName + ".log" && f.Bytes.Length > request.MaxLogBytes) return null;
                        total += f.Bytes.Length;
                    }
                    if (biberPhase && total > request.MaxOutputBytes) return null;
                    if (v4 && !converting) ValidateState(reply.Files.Where(f => IsState(f.Name)).Select(f => (f.Name, f.Bytes.Length)));
                    var owned = reply.Files.ToDictionary(f => f.Name, f => f.Bytes.ToArray(), StringComparer.Ordinal);
                    if (Encoding.UTF8.GetByteCount(log.ToString()) + Encoding.UTF8.GetByteCount(reply.Log) > limits.MaxLogBytes) throw new NativeResourceLimitException();
                    log.Append(reply.Log);
                    var validMap = reply.SourceMapPayload is { Length: > 0 and <= NativeSyncTeXContract.MaxMapBytes } map ? map : null;
                    var validGeometry = reply.SourceMapGeometryPayload is { Length: > 0 and <= SourceMapGeometryCodec.MaxBytes } geometry ? geometry : null;
                    bool legacyZeroOrigin = directPdfCell && reply.SourceMapLegacyZeroOrigin == true
                        && reply.SourceMapGeometryPayload is null && validMap is not null;
                    if (directPdfCell && !legacyZeroOrigin
                        && (reply.SourceMapLegacyZeroOrigin is not null || validMap is null || validGeometry is null))
                    {
                        validMap = null;
                        validGeometry = null;
                    }
                    reply = reply with
                    {
                        SourceMapPayload = validMap?.ToArray(),
                        SourceMapGeometryPayload = validGeometry?.ToArray(),
                        SourceMapLegacyZeroOrigin = legacyZeroOrigin ? true : null
                    };
                    var observation = reply with { Files = owned.Select(x => new NativeFileTransfer(x.Key, x.Value.ToArray())).ToArray(), Inputs = reply.Inputs.ToArray(),
                        SourceMapPayload = reply.SourceMapPayload?.ToArray(), SourceMapGeometryPayload = reply.SourceMapGeometryPayload?.ToArray() };
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
