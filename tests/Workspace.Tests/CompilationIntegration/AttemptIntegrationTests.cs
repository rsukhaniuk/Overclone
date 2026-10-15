using System.Collections;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Client.Compilation;
using Client.Compilation.Diagnostics;
using Client.Compilation.Lifecycle;
using Client.Compilation.Presentation;
using Client.Workspace.Domain;
using Xunit;
using static Workspace.Tests.CompilationTestWorkspace;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.CompilationIntegration;

// ALL transport replies/logs are synthetic. Workspace, B1, B2a, lifecycle and D1
// are actual public APIs. No engine, browser or native protocol is exercised.
public sealed class AttemptIntegrationTests
{
    private static readonly CompilerLogBudget Parser = new(32, 1024 * 1024, 65536,
        65536, 4096, 1000, 128, 1024, 32, 10000, 10_000_000, 2_000_000);
    private static readonly CompileAttemptPresentationBudget Budget = new(65536, 131072, Parser);
    private const string Error = "entry:2: LaTeX Error: synthetic known error";
    private const string Warning = "entry:1: LaTeX Warning: missing";
    // Independently derived byte counts: Error is 43 ASCII chars (43 bytes),
    // Warning is 31 ASCII chars (31 bytes), each control char is 1 UTF-8 byte.
    private const int ErrorBytes = 43;
    private const int WarningBytes = 31;
    private static CompilerLogReport Report(ValidatedCompileAttempt attempt, CompileAttemptPresentationBudget? budget = null)
    {
        var view = CompileAttemptPresenter.Present(attempt, budget ?? Budget);
        Assert.Null(view.Issue); Assert.False(view.IsCancelled); Assert.Same(attempt, view.Attempt);
        var report = Assert.IsType<CompilerLogParseResult.Parsed>(view.ParserResult).Report;
        Assert.Same(attempt.Snapshot, report.Snapshot); Assert.True(report.Flags.HasFlag(CompilerLogFlags.CallerIncomplete));
        return report;
    }

    [Fact]
    public void Successful_A_then_failed_B_exposes_current_findings_but_historical_A_bytes() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var a = service.Request(Options); (await workers.Next()).Succeed(); var success = await a.Completion; await service.WhenIdle;
        var artifact = success.Output!; var oldAttempt = success.Attempt!; var oldBytes = artifact.ToPayloadArray();
        Assert.True(service.State.IsCurrent); Assert.True(service.State.IsAttemptCurrent);
        var b = service.Request(Options); var worker = await workers.Next();
        Assert.False(service.State.IsCurrent); Assert.False(service.State.IsAttemptCurrent);
        var state = f.State(); int locks = f.Lock.Acquires, commits = f.Repo.Commits;
        var entries = new List<CompileDiagnosticEntry> { new("native-code", "bounded message") };
        worker.Reply.SetResult(worker.Output(CompileOutputStatus.Failed) with { LogText = Error, Diagnostics = entries });
        var result = await b.Completion; await service.WhenIdle;
        Assert.Equal(CompileLifecycleReason.CompilerFailed, result.Reason); Assert.Null(result.Output);
        var attempt = Assert.IsType<ValidatedCompileAttempt>(result.Attempt);
        Assert.Same(worker.Context, attempt.RequestContext); Assert.Same(b.Snapshot, attempt.Snapshot);
        Assert.Equal(b.JobId, attempt.JobId); Assert.Equal(CompileOutputStatus.Failed, attempt.Status);
        Assert.Same(Error, attempt.LogText); entries.Clear(); Assert.Single(attempt.Diagnostics);
        Assert.Same(attempt, service.State.LastAttempt); Assert.True(service.State.IsAttemptCurrent);
        Assert.Same(artifact, service.State.LastSuccessful); Assert.False(service.State.IsCurrent);
        Assert.Equal(oldBytes, artifact.ToPayloadArray()); Assert.NotSame(oldAttempt, attempt);
        var report = Report(attempt); var d = Assert.Single(report.Diagnostics); var location = Assert.IsType<CompilerLineLocation>(d.Location);
        Assert.Equal("TEX005", d.StableCode); Assert.Equal(CompilerDiagnosticSeverity.Error, d.Severity);
        Assert.Equal(CompilerLocationBasis.ExplicitFileLine, location.Basis);
        Assert.Equal(f.Main, location.FileId); Assert.Equal("entry", location.RelativePath);
        Assert.Equal(2, location.Line); Assert.Equal(23, location.StartByte); Assert.Equal(27, location.EndByte);
        Assert.Equal(11, location.SourceUtf16Start); Assert.Equal(15, location.SourceUtf16End);
        Assert.Equal(attempt.Snapshot.Files.Single(x => x.FileId == f.Main).Hash, location.FileHash);
        Assert.Equal(state, f.State()); Assert.Equal(locks, f.Lock.Acquires); Assert.Equal(commits, f.Repo.Commits);
        await Task.Yield();
    });

    [Theory]
    [InlineData(CompileOutputStatus.Succeeded, CompileLifecycleReason.Succeeded)]
    [InlineData(CompileOutputStatus.Failed, CompileLifecycleReason.CompilerFailed)]
    [InlineData(CompileOutputStatus.Cancelled, CompileLifecycleReason.WorkerCancelled)]
    [InlineData(CompileOutputStatus.TimedOut, CompileLifecycleReason.WorkerTimedOut)]
    [InlineData(CompileOutputStatus.Unsupported, CompileLifecycleReason.Unsupported)]
    public void Every_accepted_status_keeps_exact_metadata_without_inferring_status_from_parser(CompileOutputStatus status, CompileLifecycleReason reason) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var job = service.Request(Options); var worker = await workers.Next();
        worker.Reply.SetResult(worker.Output(status) with { LogText = Error });
        var result = await job.Completion; await service.WhenIdle;
        Assert.Equal(reason, result.Reason); Assert.Equal(status, result.Attempt!.Status);
        Assert.Equal(status == CompileOutputStatus.Succeeded, result.Output is not null);
        Assert.Equal(CompileLogCompleteness.Unknown, result.Attempt.LogCompleteness);
        Assert.Equal(CompilerDiagnosticSeverity.Error, Assert.Single(Report(result.Attempt).Diagnostics).Severity);
        Assert.Equal(reason, (await job.Completion).Reason); // parser error cannot change terminal
        await Task.Yield();
    });

    [Theory]
    [InlineData("request")]
    [InlineData("epoch")]
    [InlineData("digest")]
    [InlineData("snapshot")]
    [InlineData("hash")]
    [InlineData("duplicate-code")]
    [InlineData("nul")]
    public void Invalid_or_foreign_response_cannot_manufacture_metadata(string kind) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var job = service.Request(Options); var w = await workers.Next(); var raw = w.Output() with { LogText = Error };
        raw = kind switch
        {
            "request" => raw with { RequestId = CompileRequestId.New() },
            "epoch" => raw with { WorkerEpoch = raw.WorkerEpoch + 1 },
            "digest" => raw with { InputContentDigest = new SHA256(new string('b', 64)) },
            "snapshot" => raw with { SnapshotIdentity = raw.SnapshotIdentity with { SessionId = Guid.NewGuid() } },
            "hash" => raw with { ClaimedHash = new SHA256(new string('b', 64)) },
            "duplicate-code" => raw with { Diagnostics = new[] { new CompileDiagnosticEntry("same", "one"), new CompileDiagnosticEntry("same", "two") } },
            _ => raw with { LogText = Error + "\0" }
        };
        w.Reply.SetResult(raw); var result = await job.Completion; await service.WhenIdle;
        Assert.Equal(CompileLifecycleReason.InvalidOutput, result.Reason); Assert.Null(result.Attempt);
        Assert.Null(result.Output); Assert.Null(service.State.LastAttempt); Assert.False(service.State.IsAttemptCurrent);
        await Task.Yield();
    });

    [Theory]
    [InlineData("cancel", CompileLifecycleReason.Cancelled)]
    [InlineData("timeout", CompileLifecycleReason.TimedOut)]
    [InlineData("dispose", CompileLifecycleReason.Disposed)]
    [InlineData("transport", CompileLifecycleReason.TransportError)]
    [InlineData("capture", CompileLifecycleReason.CaptureUnavailable)]
    public void Local_terminal_never_adopts_a_late_failed_reply_or_exception_text(string mode, CompileLifecycleReason reason) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers(); var clock = new Clock();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), clock);
        if (mode == "capture") f.Repo.ReadOverride = _ => throw new IOException("secret exception text");
        var job = service.Request(Options);
        Worker? worker = null;
        if (mode != "capture")
        {
            worker = await workers.Next();
            if (mode == "cancel") service.Cancel();
            if (mode == "timeout") clock.Advance(Options.Deadline);
            if (mode == "dispose") await service.DisposeAsync();
            if (mode == "transport") worker.Reply.SetException(new IOException("secret exception text"));
        }
        var result = await job.Completion; await service.WhenIdle;
        if (worker is not null && mode != "transport") worker.Reply.SetResult(worker.Output(CompileOutputStatus.Failed) with { LogText = Error });
        Assert.Equal(reason, result.Reason); Assert.Null(result.Attempt); Assert.Null(result.Output);
        Assert.Same(result, await job.Completion); Assert.Null(service.State.LastAttempt); Assert.False(service.State.IsAttemptCurrent);
        Assert.DoesNotContain("secret", result.SafeMessage);
        await Task.Yield();
    });

    [Theory]
    [InlineData("cancel")]
    [InlineData("dispose")]
    [InlineData("edit")]
    [InlineData("intent")]
    [InlineData("timeout")]
    public void Reentrant_transition_during_B2a_validation_cannot_publish_losing_metadata(string mode) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers(); var clock = new Clock(); using var caller = new CancellationTokenSource();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), clock);
        var initial = service.Request(Options); (await workers.Next()).Succeed(); var previous = (await initial.Completion).Attempt!; await service.WhenIdle;
        var ticket = service.Request(Options, caller.Token); var w = await workers.Next(); CompileLifecycleTicket? newer = null;
        var metadata = new ReentrantEntries(() =>
        {
            if (mode == "cancel") caller.Cancel();
            if (mode == "dispose") _ = service.DisposeAsync();
            if (mode == "edit") f.Workspace.EditText(f.Main, "new dirty source");
            if (mode == "intent") newer = service.Request(Options);
            if (mode == "timeout") clock.Advance(Options.Deadline);
        });
        w.Reply.SetResult(w.Output(CompileOutputStatus.Failed) with { LogText = Error, Diagnostics = metadata });
        var result = await ticket.Completion;
        Assert.True(metadata.Reached); Assert.Null(result.Attempt); Assert.Null(result.Output);
        Assert.Equal(mode switch
        {
            "cancel" => CompileLifecycleReason.Cancelled,
            "dispose" => CompileLifecycleReason.Disposed,
            "timeout" => CompileLifecycleReason.TimedOut,
            _ => CompileLifecycleReason.Superseded
        }, result.Reason);
        Assert.Same(previous, service.State.LastAttempt); Assert.False(service.State.IsAttemptCurrent);
        if (newer is not null) service.Cancel();
        await service.WhenIdle;
        await Task.Yield();
    });

    [Theory]
    [InlineData("edit")]
    [InlineData("ABA")]
    [InlineData("close")]
    [InlineData("reopen")]
    [InlineData("project")]
    [InlineData("dispose")]
    public void Historical_attempt_retains_old_snapshot_hash_and_never_retargets(string mode) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var j = service.Request(Options); var w = await workers.Next(); w.Reply.SetResult(w.Output(CompileOutputStatus.Failed) with { LogText = Error });
        var old = (await j.Completion).Attempt!; await service.WhenIdle; var hash = old.InputContentDigest;
        var line = Assert.Single(Report(old).Diagnostics).Location!;
        if (mode == "edit") f.Workspace.EditText(f.Main, "new source");
        if (mode == "ABA") { var a = f.Selection.Current.Selection!; f.Selection.SetSelection(Select(f.Main, "B")); f.Selection.SetSelection(a); }
        if (mode == "close") await f.Workspace.CloseAsync(None);
        if (mode == "reopen") await f.Workspace.OpenAsync(f.Repo.Project.Id, None);
        if (mode == "project")
        {
            var bytes = Bytes("new project");
            var published = await f.Workspace.PublishImportAsync(new StagedProject("new",
                System.Collections.Immutable.ImmutableArray.Create(new StagedFile("entry", "text", false, bytes, Hash(bytes), bytes.Length)), []), "new", None);
            await f.Workspace.OpenAsync(published.Project!.Id, None);
        }
        if (mode == "dispose") await service.DisposeAsync();
        Assert.Same(old, service.State.LastAttempt); Assert.False(service.State.IsAttemptCurrent); Assert.False(service.State.IsAttemptCurrent);
        Assert.Equal(hash, old.InputContentDigest); var again = Assert.Single(Report(old).Diagnostics).Location!;
        Assert.Equal(line.FileHash, again.FileHash); Assert.Equal(23, again.StartByte); Assert.Equal(27, again.EndByte);
        await Task.Yield();
    });

    [Fact]
    public void Head_only_save_and_invalid_request_have_distinct_attempt_freshness_effects() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open();
        f.Workspace.EditText(f.Workspace.Files.Single(x => x.RelativePath == "empty.txt").Id, "held save");
        var gate = f.Repo.CommitGate = new(); var saving = f.Workspace.SaveAsync(None); await gate.WaitReached();
        var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var j = service.Request(Options); var w = await workers.Next(); w.Reply.SetResult(w.Output(CompileOutputStatus.Failed) with { LogText = Error });
        var attempt = (await j.Completion).Attempt!; await service.WhenIdle;
        Assert.True(service.State.IsAttemptCurrent);
        Assert.Equal(CompileLifecycleReason.InvalidOptions, (await service.Request(null).Completion).Reason);
        Assert.Equal(CompileLifecycleReason.InvalidOptions, (await service.Request(Options with { OutputBudget = Options.OutputBudget with { MaxPayloadBytes = 0 } }).Completion).Reason);
        Assert.True(service.State.IsAttemptCurrent); Assert.Same(attempt, service.State.LastAttempt);
        var generation = f.Workspace.WorkingGeneration; var epoch = f.Capture.CurrentWorkspaceEpoch;
        gate.Release.SetResult(); await saving;
        Assert.Equal(generation, f.Workspace.WorkingGeneration); Assert.Equal(epoch, f.Capture.CurrentWorkspaceEpoch);
        Assert.False(service.State.IsAttemptCurrent); Assert.Equal(attempt.InputContentDigest, (await f.Snapshot()).ContentDigest);
        Assert.NotEqual(attempt.SnapshotIdentity.BaseHeadRevisionId, f.Workspace.CurrentRevision!.Id);
        await Task.Yield();
    });

    [Fact]
    public void Dirty_BOM_Unicode_source_uses_exact_captured_LF_offsets_without_save() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); Assert.True(f.Workspace.EditText(f.Main, "А🙂\r\nB\r\n\n"));
        var state = f.State(); var locks = f.Lock.Acquires; var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var j = service.Request(Options); var w = await workers.Next();
        w.Reply.SetResult(w.Output(CompileOutputStatus.Failed) with { LogText = "\ufeffentry:2: LaTeX Error: 🙂" });
        var attempt = (await j.Completion).Attempt!; await service.WhenIdle;
        byte[] independent = [239, 187, 191, 208, 144, 240, 159, 153, 130, 10, 66, 10, 10];
        Assert.Equal(independent, attempt.Snapshot.Files.Single(x => x.FileId == f.Main).ByteSpan.ToArray());
        var d = Assert.Single(Report(attempt).Diagnostics); var line = d.Location!;
        Assert.Equal(10, line.StartByte); Assert.Equal(11, line.EndByte); Assert.Equal(4, line.SourceUtf16Start); Assert.Equal(5, line.SourceUtf16End);
        Assert.Equal(CompilerLocationPrecision.Line, line.Precision); Assert.Equal(CompilerLocationBasis.ExplicitFileLine, line.Basis);
        Assert.Equal(state, f.State()); Assert.Equal(locks, f.Lock.Acquires); Assert.Equal(0, f.Repo.Commits);
        await Task.Yield();
    });

    [Theory]
    [InlineData("chapter.tex:1: LaTeX Error: missing", true)]
    [InlineData("unknown.tex:1: LaTeX Error: missing", false)]
    [InlineData("(macro produced filename) ! error", false)]
    public void Full_D1_result_keeps_included_member_or_uncertainty_without_guessing(string log, bool located) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var j = service.Request(Options); var w = await workers.Next(); w.Reply.SetResult(w.Output(CompileOutputStatus.Failed) with { LogText = log });
        var r = Report((await j.Completion).Attempt!); var d = Assert.Single(r.Diagnostics);
        Assert.Equal(located, d.Location is not null);
        if (located) { Assert.Equal("chapter.tex", d.Location!.RelativePath); Assert.Equal(0, d.Location.StartByte); Assert.Equal(7, d.Location.EndByte); }
        else { Assert.NotEqual(CompilerLocationIssue.None, d.LocationIssue); Assert.NotEqual(CompilerLogFlags.None, r.Flags); }
        await Task.Yield();
    });

    [Theory]
    [InlineData("conversion")]
    [InlineData("parser-bytes")]
    [InlineData("parser-work")]
    [InlineData("parser-output")]
    [InlineData("units")]
    public void Tiny_explicit_budgets_reject_large_log_before_conversion_allocation(string mode) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var j = service.Request(Options); var w = await workers.Next(); w.Reply.SetResult(w.Output(CompileOutputStatus.Failed) with { LogText = new string('x', 4096) });
        var attempt = (await j.Completion).Attempt!;
        var budget = mode switch
        {
            "conversion" => Budget with { MaxConversionBytes = 1 },
            "parser-bytes" => Budget with { ParserBudget = Parser with { MaxLogBytes = 1 } },
            "parser-work" => Budget with { ParserBudget = Parser with { MaxWorkUnits = 1 } },
            "parser-output" => Budget with { ParserBudget = Parser with { MaxOutputBytes = 1 } },
            _ => Budget with { MaxConversionUnits = 1 }
        };
        var r = CompileAttemptPresenter.Present(attempt, budget);
        Assert.NotNull(r.Issue); Assert.Null(r.ParserResult); Assert.Equal(0, r.ConvertedBytes);
        Assert.Equal(4096, attempt.LogText.Length); Assert.Equal(CompileLifecycleReason.CompilerFailed, (await j.Completion).Reason);
        await Task.Yield();
    });

    [Fact]
    public void Unknown_empty_truncated_and_invalid_log_interpretations_never_mean_clean() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        foreach (var log in new[] { "", "This is pdfTeX, version synthetic", Error, "entry:1: LaTeX Error: \u202e" })
        {
            var j = service.Request(Options); var w = await workers.Next(); w.Reply.SetResult(w.Output(CompileOutputStatus.Failed) with { LogText = log });
            var attempt = (await j.Completion).Attempt!; await service.WhenIdle;
            var view = CompileAttemptPresenter.Present(attempt, Budget);
            if (log.Contains('\u202e')) Assert.Equal(CompilerLogIssue.InvalidLogControl, Assert.IsType<CompilerLogParseResult.Rejected>(view.ParserResult).Issue);
            else
            {
                var report = Assert.IsType<CompilerLogParseResult.Parsed>(view.ParserResult).Report;
                Assert.True(report.Flags.HasFlag(CompilerLogFlags.CallerIncomplete));
                if (log.Length == 0) Assert.True(report.Flags.HasFlag(CompilerLogFlags.EmptyLog | CompilerLogFlags.UnknownContent));
                var clipped = Assert.IsType<CompilerLogParseResult.Parsed>(CompileAttemptPresenter.Present(attempt, Budget with { ParserBudget = Parser with { MaxLineBytes = 1 } }).ParserResult).Report;
                if (log.Length > 1) { Assert.True(clipped.Flags.HasFlag(CompilerLogFlags.LineByteLimit)); Assert.Empty(clipped.Diagnostics); }
            }
            Assert.Equal(CompileLogCompleteness.Unknown, attempt.LogCompleteness);
            Assert.Equal(CompileLifecycleReason.CompilerFailed, (await j.Completion).Reason);
        }
        await Task.Yield();
    });

    [Fact]
    public void Presentation_validates_budgets_and_honors_caller_cancel_without_forging_reports() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var j = service.Request(Options); var w = await workers.Next(); w.Reply.SetResult(w.Output(CompileOutputStatus.Failed) with { LogText = Error });
        var attempt = (await j.Completion).Attempt!;
        foreach (var budget in new CompileAttemptPresentationBudget?[] { null, Budget with { MaxConversionBytes = 0 }, Budget with { MaxConversionUnits = 0 }, Budget with { ParserBudget = Parser with { MaxFiles = 0 } } })
            Assert.Equal(CompileAttemptPresentationIssue.InvalidBudget, CompileAttemptPresenter.Present(attempt, budget).Issue);
        using var ct = new CancellationTokenSource(); ct.Cancel(); var result = CompileAttemptPresenter.Present(attempt, Budget, ct.Token);
        Assert.True(result.IsCancelled); Assert.Null(result.ParserResult); Assert.Equal(0, result.ConvertedBytes);
        Assert.Equal(CompileAttemptPresentationIssue.MissingAttempt, CompileAttemptPresenter.Present(null, Budget).Issue);
        Assert.Empty(typeof(ValidatedCompileAttempt).GetConstructors());
        Assert.DoesNotContain(typeof(ValidatedCompileAttempt).GetProperties(), p => p.PropertyType == typeof(AcceptedCompileOutput) || p.PropertyType == typeof(byte[]));
        await Task.Yield();
    });

    [Fact]
    public void Timed_out_failure_cannot_replace_newer_current_failed_attempt_or_historical_artifact() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers(); var clock = new Clock();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), clock);
        var a = service.Request(Options); (await workers.Next()).Succeed(); var artifact = (await a.Completion).Output!; await service.WhenIdle;
        var b = service.Request(Options); var old = await workers.Next(); clock.Advance(Options.Deadline);
        var timeout = await b.Completion; await service.WhenIdle; Assert.Equal(CompileLifecycleReason.TimedOut, timeout.Reason); Assert.Null(timeout.Attempt);
        var c = service.Request(Options); var fresh = await workers.Next(); fresh.Reply.SetResult(fresh.Output(CompileOutputStatus.Failed) with { LogText = Error });
        var current = (await c.Completion).Attempt!; await service.WhenIdle;
        old.Reply.SetResult(old.Output(CompileOutputStatus.Failed) with { LogText = "unknown.tex:1: LaTeX Error: OLD" });
        Assert.Same(current, service.State.LastAttempt); Assert.True(service.State.IsAttemptCurrent);
        Assert.Same(artifact, service.State.LastSuccessful); Assert.False(service.State.IsCurrent);
        Assert.Same(timeout, await b.Completion); Assert.Equal(Error, current.LogText);
        await Task.Yield();
    });

    [Fact]
    public void Reentrant_handoff_replacement_keeps_idle_pending_settlement_and_metadata_coherent() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var a = service.Request(Options); var one = await workers.Next(); var b = service.Request(Options); var idle = service.WhenIdle;
        CompileLifecycleTicket? c = null; var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += () =>
        {
            if (c is null && service.State.Active is null && service.State.Pending == b.JobId)
            { c = service.Request(Options); entered.TrySetResult(); }
        };
        one.Reply.SetResult(one.Output(CompileOutputStatus.Failed) with { LogText = Error });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.NotNull(c);
        Assert.True(b.Completion.IsCompleted); Assert.Equal(CompileLifecycleReason.Superseded, (await b.Completion).Reason);
        Assert.Null((await a.Completion).Attempt); Assert.Null((await b.Completion).Attempt); Assert.Null(b.Snapshot);
        Assert.False(idle.IsCompleted); var three = await workers.Next(); Assert.Equal(c.JobId, three.Id);
        three.Reply.SetResult(three.Output(CompileOutputStatus.Failed) with { LogText = Error }); var result = await c.Completion; await idle;
        Assert.Same(result.Attempt, service.State.LastAttempt); Assert.True(service.State.IsAttemptCurrent);
        Assert.Null(service.State.LastSuccessful); Assert.Null(service.State.Pending); Assert.Null(service.State.Active);
        Assert.Equal(2, workers.Created.Count); Assert.Single(Report(result.Attempt!).Diagnostics);
        await Task.Yield();
    });

    [Fact]
    public void Foreign_caller_settlement_reached_during_validation_wins_without_metadata() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers(); using var ct = new CancellationTokenSource();
        using var reached = new ManualResetEventSlim(); Task? foreign = null; bool reachedInValidation = false;
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var j = service.Request(Options, ct.Token); var w = await workers.Next();
        var metadata = new ReentrantEntries(() =>
        {
            foreign = Task.Run(() => { ct.Cancel(); reached.Set(); });
            // A bounded test-only barrier, not a guessed sleep or product wait.
            // Foreign cancellation must settle while owner is still in validation.
            reachedInValidation = reached.Wait(TimeSpan.FromSeconds(10));
        });
        w.Reply.SetResult(w.Output(CompileOutputStatus.Failed) with { LogText = Error, Diagnostics = metadata });
        var result = await j.Completion; Assert.NotNull(foreign); await foreign.WaitAsync(TimeSpan.FromSeconds(10)); await service.WhenIdle;
        Assert.True(reachedInValidation); Assert.Equal(CompileLifecycleReason.Cancelled, result.Reason);
        Assert.Null(result.Attempt); Assert.Null(service.State.LastAttempt); Assert.False(service.State.IsAttemptCurrent);
        await Task.Yield();
    });

    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 7)]
    public void Conversion_limits_preserve_whole_emoji_and_initial_log_BOM(bool bom, int expectedBytes) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var log = bom ? "\ufeff🙂" : "🙂"; var j = service.Request(Options); var w = await workers.Next();
        w.Reply.SetResult(w.Output(CompileOutputStatus.Failed) with { LogText = log }); var attempt = (await j.Completion).Attempt!;
        var tooSmall = CompileAttemptPresenter.Present(attempt, Budget with { MaxConversionBytes = expectedBytes - 1 });
        Assert.Equal(CompileAttemptPresentationIssue.ConversionByteLimit, tooSmall.Issue); Assert.Equal(0, tooSmall.ConvertedBytes); Assert.Null(tooSmall.ParserResult);
        var exact = CompileAttemptPresenter.Present(attempt, Budget with { MaxConversionBytes = expectedBytes });
        Assert.Equal(expectedBytes, exact.ConvertedBytes); Assert.IsType<CompilerLogParseResult.Parsed>(exact.ParserResult);
        Assert.Same(log, attempt.LogText); Assert.Equal(CompileLogCompleteness.Unknown, attempt.LogCompleteness);
        await Task.Yield();
    });

    [Theory]
    [InlineData("lf")]
    [InlineData("crlf")]
    [InlineData("cr")]
    [InlineData("tab")]
    [InlineData("mixed")]
    public void Multiline_log_control_bytes_survive_envelope_and_presenter_for_each_ending(string label) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        // Build explicit complete logs per label with REAL control codepoints.
        // Expected counts are independently literal, not computed from the raw string.
        string raw; int expectedLf, expectedCr, expectedTab, expectedBytes;
        switch (label)
        {
            case "lf":
                raw = Error + "\n" + Warning + "\n";
                expectedLf = 2; expectedCr = 0; expectedTab = 0;
                expectedBytes = ErrorBytes + 1 + WarningBytes + 1; // 76
                break;
            case "crlf":
                raw = Error + "\r\n" + Warning + "\r\n";
                expectedLf = 2; expectedCr = 2; expectedTab = 0;
                expectedBytes = ErrorBytes + 2 + WarningBytes + 2; // 78
                break;
            case "cr":
                // CR-only: uses CR separators and contains ZERO LF.
                raw = Error + "\r" + Warning + "\r";
                expectedLf = 0; expectedCr = 2; expectedTab = 0;
                expectedBytes = ErrorBytes + 1 + WarningBytes + 1; // 76
                break;
            case "tab":
                // TAB inside a message, LF delimiters.
                raw = Error + "\n" + "field\tvalue" + "\n" + Warning + "\n";
                expectedLf = 3; expectedCr = 0; expectedTab = 1;
                expectedBytes = ErrorBytes + 1 + 11 + 1 + WarningBytes + 1; // 88
                break;
            case "mixed":
                // Standalone CR, CRLF, LF, and TAB all present.
                raw = Error + "\t" + "a\r\n" + Warning + "\r" + "b\r\n" + "c\n";
                expectedLf = 3; expectedCr = 3; expectedTab = 1;
                expectedBytes = ErrorBytes + 1 + 1 + 2 + WarningBytes + 1 + 1 + 2 + 1 + 1; // 84
                break;
            default: throw new System.ArgumentException($"unknown ending {label}");
        }
        // Assert literal control-char counts directly from the expected values.
        Assert.Equal(expectedLf, raw.Count(c => c == '\n'));
        Assert.Equal(expectedCr, raw.Count(c => c == '\r'));
        Assert.Equal(expectedTab, raw.Count(c => c == '\t'));
        // CR-only log must contain ZERO LF.
        if (label == "cr") Assert.DoesNotContain("\n", raw);
        var j = service.Request(Options); var w = await workers.Next(); w.Reply.SetResult(w.Output(CompileOutputStatus.Failed) with { LogText = raw });
        var attempt = (await j.Completion).Attempt!; await service.WhenIdle;
        Assert.Equal(CompileOutputStatus.Failed, attempt.Status); Assert.Same(raw, attempt.LogText); Assert.Null((await j.Completion).Output);
        Assert.Equal(CompileLogCompleteness.Unknown, attempt.LogCompleteness);
        var view = CompileAttemptPresenter.Present(attempt, Budget);
        Assert.Null(view.Issue); Assert.False(view.IsCancelled); Assert.Same(attempt, view.Attempt);
        Assert.Equal(expectedBytes, view.ConvertedBytes);
        var report = Assert.IsType<CompilerLogParseResult.Parsed>(view.ParserResult).Report;
        Assert.True(report.Flags.HasFlag(CompilerLogFlags.CallerIncomplete));
        Assert.NotEmpty(report.Diagnostics.Where(d => d.Code == CompilerDiagnosticCode.Error));
        Assert.NotEmpty(report.Diagnostics.Where(d => d.Code == CompilerDiagnosticCode.Warning));
        Assert.All(report.Diagnostics, d => Assert.True(d.LogEndByte > d.LogStartByte));
        Assert.Same(attempt.Snapshot, report.Snapshot);
        await Task.Yield();
    });

    [Theory]
    [InlineData(CompileOutputStatus.Succeeded)]
    [InlineData(CompileOutputStatus.Failed)]
    [InlineData(CompileOutputStatus.Cancelled)]
    [InlineData(CompileOutputStatus.TimedOut)]
    [InlineData(CompileOutputStatus.Unsupported)]
    public void Multiline_log_survives_all_five_native_statuses_artifact_only_on_success(CompileOutputStatus status) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        // Mix all three permitted controls: TAB + CRLF inside the log text.
        // Error(43) + \t(1) + \r\n(2) + Warning(31) + \r\n(2) = 79 bytes.
        var raw = Error + "\t\r\n" + Warning + "\r\n";
        const int MixedBytes = ErrorBytes + 1 + 2 + WarningBytes + 2; // 79
        var j = service.Request(Options); var w = await workers.Next();
        w.Reply.SetResult(w.Output(status) with { LogText = raw });
        var result = await j.Completion; await service.WhenIdle;
        Assert.Equal(status, result.Attempt!.Status); Assert.Same(raw, result.Attempt!.LogText);
        Assert.Equal(CompileLogCompleteness.Unknown, result.Attempt!.LogCompleteness);
        // Only Succeeded publishes an artifact; all non-success statuses have no Output.
        if (status == CompileOutputStatus.Succeeded) Assert.NotNull(result.Output);
        else Assert.Null(result.Output);
        var view = CompileAttemptPresenter.Present(result.Attempt!, Budget);
        Assert.Null(view.Issue); Assert.Equal(MixedBytes, view.ConvertedBytes);
        await Task.Yield();
    });

    [Fact]
    public void Genuine_multiline_native_failure_logs_pass_through_full_envelope_lifecycle_presenter_chain() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        // Exact accepted D1 genuine fixture excerpts: tests/Workspace.Tests/CompilationDiagnostics/Fixtures/*.log
        // are immutable baseline files, not new B4 acquisitions. Transport is modeled here.
        var j = service.Request(Options); var w = await workers.Next();
        var missingClass = File.ReadAllBytes(FixturePath("missing-class.log"));
        w.Reply.SetResult(w.Output(CompileOutputStatus.Failed) with { LogText = Encoding.UTF8.GetString(missingClass) });
        var attempt = (await j.Completion).Attempt!; await service.WhenIdle;
        Assert.Equal(CompileOutputStatus.Failed, attempt.Status); Assert.Null((await j.Completion).Output);
        var report = Assert.IsType<CompilerLogParseResult.Parsed>(CompileAttemptPresenter.Present(attempt, Budget with { ParserBudget = Parser with { MaxLogBytes = 65536 } }).ParserResult).Report;
        Assert.True(report.Flags.HasFlag(CompilerLogFlags.CallerIncomplete));
        var d = Assert.Single(report.Diagnostics, x => x.Code == CompilerDiagnosticCode.MissingFile);
        Assert.Equal(CompilerDiagnosticSeverity.Error, d.Severity);
        Assert.Null(d.Location); Assert.NotEqual(CompilerLocationIssue.None, d.LocationIssue);
        Assert.Same(attempt.Snapshot, report.Snapshot); Assert.Equal("compiler", d.Origin);
        // Reuse the other accepted D1 genuine fixture to prove multiple native errors survive:
        var encoding = File.ReadAllBytes(FixturePath("encoding.log"));
        var j2 = service.Request(Options); var w2 = await workers.Next();
        w2.Reply.SetResult(w2.Output(CompileOutputStatus.Failed) with { LogText = Encoding.UTF8.GetString(encoding) });
        var attempt2 = (await j2.Completion).Attempt!; await service.WhenIdle;
        var report2 = Assert.IsType<CompilerLogParseResult.Parsed>(CompileAttemptPresenter.Present(attempt2, Budget with { ParserBudget = Parser with { MaxLogBytes = 65536 } }).ParserResult).Report;
        Assert.True(report2.Flags.HasFlag(CompilerLogFlags.CallerIncomplete));
        // Independent explicit expected codes for the encoding fixture — NOT a lambda
        // closing over the outer first-fixture variable d. encoding.log produces an
        // Encoding diagnostic (Error severity, null location / explicit location issue).
        var enc = Assert.Single(report2.Diagnostics, d2 => d2.Code == CompilerDiagnosticCode.Encoding);
        Assert.Equal(CompilerDiagnosticSeverity.Error, enc.Severity);
        Assert.Null(enc.Location); Assert.NotEqual(CompilerLocationIssue.None, enc.LocationIssue);
        Assert.DoesNotContain(report2.Diagnostics, d2 => d2.Code == CompilerDiagnosticCode.Unknown);
        Assert.Same(attempt2.Snapshot, report2.Snapshot);
        await Task.Yield();
    });

    [Fact]
    public void Unsafe_controls_and_malformed_unicode_in_log_remain_typed_failures_not_silently_dropped() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        foreach (var ch in new[] { '\0', '\u0001', '\u001b', '\u007f', '\u0085' })
        {
            var j = service.Request(Options); var w = await workers.Next();
            w.Reply.SetResult(w.Output(CompileOutputStatus.Failed) with { LogText = Error + ch });
            Assert.Equal(CompileLifecycleReason.InvalidOutput, (await j.Completion).Reason);
            Assert.Null((await j.Completion).Attempt); Assert.Null(service.State.LastAttempt);
        }
        await service.WhenIdle;
        await Task.Yield();
    });

    [Theory]
    [InlineData("format-id", "tab")]
    [InlineData("format-id", "cr")]
    [InlineData("format-id", "lf")]
    [InlineData("format-version", "tab")]
    [InlineData("format-version", "cr")]
    [InlineData("format-version", "lf")]
    [InlineData("adapter-id", "tab")]
    [InlineData("adapter-id", "cr")]
    [InlineData("adapter-id", "lf")]
    [InlineData("adapter-version", "tab")]
    [InlineData("adapter-version", "cr")]
    [InlineData("adapter-version", "lf")]
    [InlineData("diagnostic-code", "tab")]
    [InlineData("diagnostic-code", "cr")]
    [InlineData("diagnostic-code", "lf")]
    [InlineData("diagnostic-message", "tab")]
    [InlineData("diagnostic-message", "cr")]
    [InlineData("diagnostic-message", "lf")]
    public void Field_isolation_controls_in_metadata_reject_only_logtext_accepts(string field, string charLabel) => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        // Map label to REAL control codepoint: U+0009, U+000D, U+000A.
        char ch = charLabel switch { "tab" => '\t', "cr" => '\r', "lf" => '\n', _ => throw new System.ArgumentException($"unknown char {charLabel}") };
        var j = service.Request(Options); var w = await workers.Next();
        var baseInput = w.Output(CompileOutputStatus.Failed);
        var raw = field switch
        {
            "format-id" => baseInput with { OutputFormat = new($"bad{ch}id", "1.0", null) },
            "format-version" => baseInput with { OutputFormat = new("pdf", $"1{ch}.0", null) },
            "adapter-id" => baseInput with { OutputAdapter = new($"bad{ch}id", "1.0", null) },
            "adapter-version" => baseInput with { OutputAdapter = new("pdf", $"1{ch}.0", null) },
            "diagnostic-code" => baseInput with { Diagnostics = new[] { new CompileDiagnosticEntry($"E{ch}001", "message") } },
            "diagnostic-message" => baseInput with { Diagnostics = new[] { new CompileDiagnosticEntry("E001", $"bad{ch}message") } },
            _ => throw new System.ArgumentException($"unknown field {field}")
        };
        // PUBLIC API: call CompileOutputEnvelope.Create directly with the valid budget.
        var creation = CompileOutputEnvelope.Create(raw, Options.OutputBudget);
        Assert.Null(creation.Envelope);
        Assert.Equal(CompileOutputIssue.InvalidMetadata, creation.Failure);
        // Lifecycle: reply the same raw → InvalidOutput, no attempt.
        w.Reply.SetResult(raw); var result = await j.Completion; await service.WhenIdle;
        Assert.Equal(CompileLifecycleReason.InvalidOutput, result.Reason);
        Assert.Null(result.Attempt); Assert.Null(service.State.LastAttempt);
        Assert.False(service.State.IsAttemptCurrent);
        // The same control char in LogText is accepted — only the log-only whitelist admits it.
        var j2 = service.Request(Options); var w2 = await workers.Next();
        w2.Reply.SetResult(w2.Output(CompileOutputStatus.Failed) with { LogText = Error + ch });
        var attempt2 = (await j2.Completion).Attempt!; await service.WhenIdle;
        Assert.Equal(CompileOutputStatus.Failed, attempt2.Status); Assert.Contains(ch, attempt2.LogText);
        await Task.Yield();
    });

    [Fact]
    public void Log_byte_boundary_with_real_unicode_and_control_characters() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        // alpha (U+03B1) = 2 UTF-8 bytes (0xCE 0xB1), TAB = 1 byte (0x09),
        // CR = 1 byte (0x0D), LF = 1 byte (0x0A). Total = 2+1+1+1 = 5 bytes.
        // Literal expected byte counts, not derived from GetByteCount.
        var log = "\u03b1\t\r\n";
        Assert.Equal(4, log.Length); // 4 chars: alpha, tab, cr, lf
        // Literal byte count: alpha=2 + tab=1 + cr=1 + lf=1 = 5 (not from GetByteCount).
        const int AlphaUtf8 = 2, ControlUtf8 = 1;
        Assert.Equal(AlphaUtf8 + ControlUtf8 + ControlUtf8 + ControlUtf8, 5);
        var j = service.Request(Options); var w = await workers.Next();
        w.Reply.SetResult(w.Output(CompileOutputStatus.Failed) with { LogText = log });
        var attempt = (await j.Completion).Attempt!; await service.WhenIdle;
        Assert.Same(log, attempt.LogText);
        // Presenter: exact cap 5 accepts, cap 4 rejects.
        var exact = CompileAttemptPresenter.Present(attempt, Budget with { MaxConversionBytes = 5 });
        Assert.Null(exact.Issue); Assert.Equal(5, exact.ConvertedBytes);
        Assert.IsType<CompilerLogParseResult.Parsed>(exact.ParserResult);
        var tooSmall = CompileAttemptPresenter.Present(attempt, Budget with { MaxConversionBytes = 4 });
        Assert.Equal(CompileAttemptPresentationIssue.ConversionByteLimit, tooSmall.Issue);
        Assert.Equal(0, tooSmall.ConvertedBytes); Assert.Null(tooSmall.ParserResult);
        // PUBLIC envelope: per-field MaxLogBytes. Literal UTF8 bytes [0xCE,0xB1,0x09,0x0D,0x0A].
        var baseInput = w.Output(CompileOutputStatus.Failed);
        var logBytes = new byte[] { 0xCE, 0xB1, 0x09, 0x0D, 0x0A };
        Assert.Equal(log, Encoding.UTF8.GetString(logBytes));
        var envelopeOk = CompileOutputEnvelope.Create(baseInput with { LogText = log },
            Options.OutputBudget with { MaxLogBytes = 5 });
        Assert.NotNull(envelopeOk.Envelope);
        var envelopeReject = CompileOutputEnvelope.Create(baseInput with { LogText = log },
            Options.OutputBudget with { MaxLogBytes = 4 });
        Assert.Null(envelopeReject.Envelope);
        Assert.Equal(CompileOutputIssue.OverMetadataBudget, envelopeReject.Failure);
        // Aggregate metadata: independently sum canonical encoding bytes (read source, not MetadataByteLength).
        // Domain "overclone-compilation-output-metadata-v1" = 40 ASCII → 4-byte prefix + 40 = 44.
        // Null format/adapter descriptors = 1 boolean byte each. Null mapping = 1 boolean byte.
        // Empty diagnostics = 4-byte Int32 count=0. Log field = 4-byte prefix + content bytes.
        const int DomainPrefix = 4, DomainContent = 40; // read from CompileArtifactCanonical source
        const int NullDescriptor = 1, EmptyDiagnostics = 4, LogPrefix = 4;
        const int ExpectedEmptyMeta = DomainPrefix + DomainContent + NullDescriptor + NullDescriptor + NullDescriptor + EmptyDiagnostics + LogPrefix + 0; // 55
        const int ExpectedFiveMeta = DomainPrefix + DomainContent + NullDescriptor + NullDescriptor + NullDescriptor + EmptyDiagnostics + LogPrefix + 5; // 60
        // Empty log with exact aggregate cap accepts, one less rejects.
        var emptyInput = baseInput with { LogText = "" };
        var emptyOk = CompileOutputEnvelope.Create(emptyInput, Options.OutputBudget with { MaxMetadataBytes = ExpectedEmptyMeta });
        Assert.NotNull(emptyOk.Envelope); Assert.Equal(ExpectedEmptyMeta, emptyOk.Envelope!.MetadataByteLength);
        var emptyReject = CompileOutputEnvelope.Create(emptyInput, Options.OutputBudget with { MaxMetadataBytes = ExpectedEmptyMeta - 1 });
        Assert.Null(emptyReject.Envelope); Assert.Equal(CompileOutputIssue.OverMetadataBudget, emptyReject.Failure);
        // 5-byte log with exact aggregate cap accepts, one less rejects.
        var fiveOk = CompileOutputEnvelope.Create(baseInput with { LogText = log }, Options.OutputBudget with { MaxMetadataBytes = ExpectedFiveMeta });
        Assert.NotNull(fiveOk.Envelope); Assert.Equal(ExpectedFiveMeta, fiveOk.Envelope!.MetadataByteLength);
        var fiveReject = CompileOutputEnvelope.Create(baseInput with { LogText = log }, Options.OutputBudget with { MaxMetadataBytes = ExpectedFiveMeta - 1 });
        Assert.Null(fiveReject.Envelope); Assert.Equal(CompileOutputIssue.OverMetadataBudget, fiveReject.Failure);
        // Changing an empty log to this log adds exactly five metadata bytes (content only, prefix unchanged).
        Assert.Equal(5, ExpectedFiveMeta - ExpectedEmptyMeta);
        await Task.Yield();
    });

    private static string FixturePath(string name, [CallerFilePath] string file = "")
        => Path.Combine(Path.GetDirectoryName(file)!, "..", "CompilationDiagnostics", "Fixtures", name);

    private sealed class ReentrantEntries(Action action) : IReadOnlyList<CompileDiagnosticEntry>
    {
        public bool Reached;
        public int Count { get { if (!Reached) { Reached = true; action(); } return 0; } }
        public CompileDiagnosticEntry this[int index] => throw new IndexOutOfRangeException();
        public IEnumerator<CompileDiagnosticEntry> GetEnumerator() => Enumerable.Empty<CompileDiagnosticEntry>().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
