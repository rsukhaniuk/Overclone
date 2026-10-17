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
            CompileOutputEnvelopeInput Finish(CompileOutputStatus status, string code, byte[]? payload = null) => new()
            {
                RequestId = context.RequestId,
                SnapshotIdentity = context.SnapshotIdentity,
                InputContentDigest = context.InputContentDigest,
                WorkerEpoch = context.WorkerEpoch,
                Status = status,
                Payload = payload,
                ClaimedLength = payload?.Length ?? 0,
                ClaimedHash = payload is null ? null : NativeRuntimePackage.Digest(payload),
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
                if (factory._provider is null || factory._transport is null) return Finish(CompileOutputStatus.Unsupported, "runtime-unavailable");
                var runtime = await factory.Operations.RunAsync(factory._provider.GetAsync, token).ConfigureAwait(false);
                Check();
                if (runtime is null) return Finish(CompileOutputStatus.Unsupported, "runtime-unavailable");
                var snapshot = context.Snapshot;
                bool runBibtex = factory._plan.UseSelectionTools
                    ? snapshot.Selection.Tools.Contains(runtime.Bibtex)
                    : factory._plan.BibtexRequired;
                if (context.OutputFormat is not null && context.OutputFormat != NativeRuntimePackage.Pdf
                    || context.OutputAdapter is not null && context.OutputAdapter != NativeRuntimePackage.Adapter
                    || snapshot.Selection.Engine != runtime.Engine || !snapshot.Selection.Bundles.Contains(runtime.Bundle)
                    || !factory._plan.UseSelectionTools && factory._plan.BibtexRequired && !snapshot.Selection.Tools.Contains(runtime.Bibtex)
                    || factory._plan.UseSelectionTools && snapshot.Selection.Tools.Any(tool => tool != runtime.Bibtex)
                    || snapshot.Selection.Options.Length != 0)
                    return Finish(CompileOutputStatus.Unsupported, "unsupported-selection");
                var main = snapshot.Files.SingleOrDefault(f => f.FileId == snapshot.MainFileId);
                if (main?.RelativePath != "main.tex") return Finish(CompileOutputStatus.Unsupported, "unsupported-main-entry");
                if (snapshot.Files.Any(f => f.RelativePath == NativeRuntimePackage.FormatResourceName))
                    return Finish(CompileOutputStatus.Failed, "reserved-format-input");
                if (snapshot.Files.Length > factory._plan.MaxPhaseFiles - runtime.FileLengths.Count()) throw new NativeResourceLimitException();
                var outputs = new[] { "main.pdf", "main.aux", "main.bbl", "main.blg", "main.log" }
                    .Concat(snapshot.Files.Where(f => f.RelativePath.EndsWith(".tex", StringComparison.Ordinal) && f.RelativePath != "main.tex")
                        .Select(f => f.RelativePath[..^4] + ".aux")).Distinct(StringComparer.Ordinal).ToArray();
                if (outputs.Length > 64 || snapshot.Files.Any(f => outputs.Contains(f.RelativePath, StringComparer.Ordinal)))
                    return Finish(CompileOutputStatus.Failed, "generated-input-collision");
                var generated = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                string? previous = null; int stable = 0, ordinal = 0;
                for (int pass = 1; pass <= factory._plan.MaxLatexPasses; pass++)
                {
                    var latex = await Run("latex");
                    if (latex is null) return Finish(CompileOutputStatus.Failed, "invalid-native-reply");
                    if (latex.Value.Reply.Outcome != "native-return" || latex.Value.Reply.NativeReturn != 0)
                        return Finish(latex.Value.Reply.Outcome == "transport-timeout" ? CompileOutputStatus.TimedOut : CompileOutputStatus.Failed, "native-latex-failed");
                    var files = latex.Value.Files;
                    Carry(files);
                    if (pass == 1 && runBibtex)
                    {
                        if (!generated.ContainsKey("main.aux")) return Finish(CompileOutputStatus.Failed, "missing-native-aux");
                        var bib = await Run("bibtex");
                        if (bib is null) return Finish(CompileOutputStatus.Failed, "invalid-native-reply");
                        if (bib.Value.Reply.Outcome != "native-return" || bib.Value.Reply.NativeReturn != 0 || !bib.Value.Files.TryGetValue("main.bbl", out var bbl) || bbl.Length == 0)
                            return Finish(bib.Value.Reply.Outcome == "transport-timeout" ? CompileOutputStatus.TimedOut : CompileOutputStatus.Failed, "native-bibtex-failed");
                        Carry(bib.Value.Files); continue;
                    }
                    var fingerprint = string.Join("\n", generated.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + ":" + NativeRuntimePackage.Digest(x.Value).Hex));
                    stable = previous == fingerprint ? stable + 1 : 0; previous = fingerprint;
                    bool warnings = latex.Value.Reply.Log.Contains("undefined", StringComparison.OrdinalIgnoreCase)
                        || latex.Value.Reply.Log.Contains("Rerun", StringComparison.OrdinalIgnoreCase)
                        || latex.Value.Reply.Log.Contains("Label(s) may have changed", StringComparison.Ordinal);
                    if (stable >= 1 && pass >= (runBibtex ? 3 : 2) && !warnings
                        && files.TryGetValue("main.pdf", out var pdf) && pdf.Length >= 5 && pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
                    {
                        Check();
                        return Finish(CompileOutputStatus.Succeeded, "converged", pdf.ToArray());
                    }
                }
                return Finish(CompileOutputStatus.Failed, "pass-limit-unknown");

                void Carry(Dictionary<string, byte[]> files)
                {
                    foreach (var pair in files.Where(x => x.Key.EndsWith(".aux", StringComparison.Ordinal) || x.Key == "main.bbl")) generated[pair.Key] = pair.Value;
                }
                async Task<(NativePhaseReply Reply, Dictionary<string, byte[]> Files)?> Run(string tool)
                {
                    Check();
                    if (runtime.FileLengths.Count() + snapshot.Files.Length + generated.Count > factory._plan.MaxPhaseFiles) throw new NativeResourceLimitException();
                    var lengths = runtime.FileLengths.Concat(snapshot.Files.Select(f => (f.RelativePath, checked((int)f.ByteLength))))
                        .Concat(generated.Select(f => (f.Key, f.Value.Length))).ToArray();
                    // Names are mapped in FileLengths already. Reject runtime,
                    // source or generated conflicts BEFORE any byte clones/hashes.
                    if (lengths.Select(f => f.Item1).Distinct(StringComparer.Ordinal).Count() != lengths.Length)
                        throw new InvalidDataException("Mapped phase destination collision.");
                    if (lengths.Length > factory._plan.MaxPhaseFiles || lengths.Any(f => f.Item2 > factory._plan.MaxFileBytes)
                        || lengths.Sum(f => (long)f.Item2) > factory._plan.MaxInputBytes) throw new NativeResourceLimitException();
                    var all = runtime.CopyFiles().ToDictionary(f => f.Name, f => f.Bytes, StringComparer.Ordinal);
                    foreach (var file in snapshot.Files)
                    {
                        if (all.ContainsKey(file.RelativePath)) throw new InvalidDataException("Runtime/project collision.");
                        all.Add(file.RelativePath, file.ToArray());
                    }
                    foreach (var file in generated) all.Add(file.Key, file.Value.ToArray());
                    if (all.Count > factory._plan.MaxPhaseFiles || all.Values.Any(b => b.Length > factory._plan.MaxFileBytes)
                        || all.Values.Sum(b => (long)b.Length) > factory._plan.MaxInputBytes) throw new NativeResourceLimitException();
                    var tag = new NativePhaseTag(id.Value.ToString("N"), context.RequestId.ToString(), epoch,
                        context.SnapshotIdentity.CaptureId, context.InputContentDigest.Hex, ++ordinal, tool, Guid.NewGuid().ToString("N"), context.SnapshotIdentity);
                    var sent = all.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new NativeInputFact(x.Key, x.Value.Length, NativeRuntimePackage.Digest(x.Value).Hex)).ToArray();
                    var request = new NativePhaseRequest(tag, "main.tex", all.Select(x => new NativeFileTransfer(x.Key, x.Value)).ToArray(),
                        outputs, runtime.Copy("engine.js"), runtime.Copy("engine.wasm"), Math.Min(limits.MaxLogBytes, 256 * 1024), limits.MaxPayloadBytes);
                    var reply = await factory.Operations.RunAsync(() => { lock (_dispatch) { Check(); return factory._transport.ExecuteAsync(request); } }, token).ConfigureAwait(false);
                    Check();
                    if (reply is { Outcome: "transport-timeout", NativeReturn: null, Files.Length: 0 } && reply.Tag == tag)
                    {
                        factory.Observe(new(tag, sent, reply)); return (reply, new Dictionary<string, byte[]>(StringComparer.Ordinal));
                    }
                    if (reply is null || reply.Tag != tag || reply.WasmSha256 != NativeRuntimePackage.WasmHash || reply.LoaderSha256 != NativeRuntimePackage.LoaderHash || reply.Files is null || reply.Inputs is null || reply.Log is null || reply.Inputs.Length != sent.Length
                        || !reply.Inputs.OrderBy(x => x.Name, StringComparer.Ordinal).SequenceEqual(sent) || reply.Files.Length > outputs.Length
                        || reply.Log.Length > request.MaxLogBytes || Encoding.UTF8.GetByteCount(reply.Log) > request.MaxLogBytes) return null;
                    if (reply.Outcome == "native-return" && (!reply.Entered || reply.NativeReturn is null)
                        || reply.Outcome == "wasm-abort" && (!reply.Entered || reply.NativeReturn is not null)) return null;
                    long total = 0; var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var f in reply.Files)
                    {
                        if (f is null || f.Bytes is null || !outputs.Contains(f.Name, StringComparer.Ordinal) || !names.Add(f.Name)
                            || f.Bytes.Length > limits.MaxPayloadBytes || f.Bytes.Length > 2L * limits.MaxPayloadBytes - total) return null;
                        total += f.Bytes.Length;
                    }
                    var owned = reply.Files.ToDictionary(f => f.Name, f => f.Bytes.ToArray(), StringComparer.Ordinal);
                    if (Encoding.UTF8.GetByteCount(log.ToString()) + Encoding.UTF8.GetByteCount(reply.Log) > limits.MaxLogBytes) throw new NativeResourceLimitException();
                    log.Append(reply.Log);
                    var observation = reply with { Files = owned.Select(x => new NativeFileTransfer(x.Key, x.Value.ToArray())).ToArray(), Inputs = reply.Inputs.ToArray() };
                    factory.Observe(new(tag, sent, observation));
                    return (reply, owned);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || Volatile.Read(ref _terminated) != 0)
            { return Finish(timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !_stop.IsCancellationRequested ? CompileOutputStatus.TimedOut : CompileOutputStatus.Cancelled, "native-wait-cancelled"); }
            catch (OperationCanceledException) { return Finish(CompileOutputStatus.Failed, "foreign-cancellation"); }
            catch (NativeResourceLimitException) { return Finish(CompileOutputStatus.Failed, "foreign-resource-limit"); }
            catch (Exception error) when (error is not OutOfMemoryException) { return Finish(CompileOutputStatus.Failed, "native-transport-error"); }
            finally { _stop.Dispose(); }
        }
    }
}
