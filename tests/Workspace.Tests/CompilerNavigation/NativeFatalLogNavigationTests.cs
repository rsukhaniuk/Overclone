using System.Text;
using Client.Compilation.Diagnostics;
using Client.Compilation.Navigation;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.CompilerNavigation;

// EXACT preserved native failure LOG, not fabricated file:line output. The
// replay below uses synthetic worker/storage ports with real workspace,
// snapshot, lifecycle, presenter, parser and navigation policy. This is NOT
// native re-execution or actual DOM selection/browser acceptance.
public sealed class NativeFatalLogNavigationTests
{
    private const string NativeLog =
        "This is pdfTeX, Version 3.14159265-2.6-1.40.21 (SwiftLaTeX PDFTeX 0.3.0) (preloaded format=swiftlatexpdftex)\n" +
        "entering extended mode\n" +
        "(main.tex\n" +
        "LaTeX2e <2023-11-01> patch level 1\n" +
        "L3 programming layer <2024-01-22>\n" +
        "(article.cls\n" +
        "Document Class: article 2023/05/17 v1.4n Standard LaTeX document class\n" +
        "(size10.clo)) (l3backend-pdftex.def)\n" +
        "No file main.aux.\n" +
        "(chapter.tex\n" +
        "! Undefined control sequence.\n" +
        "l.3 \\overcloneUndefinedCommand\n" +
        "                               % ^^d0^^9a^^d0^^b8^^d1^^97^^d0^^b2 ^^f0^^9f^^...\n" +
        "!  ==> Fatal error occurred, no output PDF file produced!\n" +
        "Transcript written on main.log.\n";
    private const string Fatal = "!  ==> Fatal error occurred, no output PDF file produced!\n";
    private const string Footer = "Transcript written on main.log.\n";
    private const string Error = "! Undefined control sequence.\n";
    private const string Echo = "l.3 \\overcloneUndefinedCommand\n";
    // Explicit captured source fixture: Unicode comments + CRLF. No source
    // reconstruction from the escaped/shortened native comment echo is claimed.
    private const string Main = "\\documentclass[10pt]{article}\r\n\\begin{document}\r\n\\input{chapter}\r\n\\end{document}\r\n";
    private const string Chapter = "% Україна 😀\r\n% Київ 🙂\r\n\\overcloneUndefinedCommand % Київ 😀\r\n";
    private static readonly CompilerLogBudget Budget = new(32, 8388608, 65536, 65536, 8192,
        1000, 128, 4096, 32, 10000, 10000000, 2097152);

    private static async Task<CompilerSourceNavigationTests.Fixture> Open(bool bom = false)
    {
        var f = await CompilerSourceNavigationTests.Fixture.Open();
        foreach (var file in f.Workspace.Files.Where(file => file.RelativePath is "main.tex" or "chapter.tex").ToArray())
            Assert.True(f.Workspace.DeleteFile(file.Id));
        Assert.True(f.Workspace.CreateFileFromBytes("main.tex", Encoding.UTF8.GetBytes(Main)));
        var chapter = Encoding.UTF8.GetBytes(Chapter);
        if (bom) chapter = [239, 187, 191, .. chapter];
        Assert.True(f.Workspace.CreateFileFromBytes("chapter.tex", chapter));
        f.Session.SelectMain(f.Workspace.Files.Single(file => !file.Deleted && file.RelativePath == "main.tex").Id);
        return f;
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void Actual_stopped_fatal_log_recovers_only_primary_line_with_honest_scope(bool bom) => Run(async () =>
    {
        Assert.Equal(594, Encoding.UTF8.GetByteCount(NativeLog));
        Assert.Equal("4a57bed620133690c2b48672c4598d1aa496881af877b75e237db70a5efba1d4",
            CompilationTestWorkspace.Hash(Encoding.UTF8.GetBytes(NativeLog)).Hex);
        await using var f = await Open(bom);
        await f.Fail(NativeLog); // Current FAILED attempt; not an injected parser result.
        Assert.Null(f.Session.DisplayedOutput);
        var report = f.Report;
        Assert.True(report.Flags.HasFlag(CompilerLogFlags.CallerIncomplete)); // Presenter passes false.
        Assert.True(report.Flags.HasFlag(CompilerLogFlags.UnknownContent));
        Assert.True(report.Flags.HasFlag(CompilerLogFlags.UncertainContext)); // Stack remains open!
        var primary = Assert.Single(report.Diagnostics.Where(d => d.Code == CompilerDiagnosticCode.Error));
        var location = Assert.IsType<CompilerLineLocation>(primary.Location);
        Assert.Equal(CompilerLocationBasis.NarrowFatalContext, location.Basis);
        Assert.Equal(CompilerLocationIssue.None, primary.LocationIssue);
        Assert.Equal("chapter.tex", location.RelativePath); Assert.Equal(3, location.Line);
        Assert.Equal(25, location.SourceUtf16Start); Assert.Equal(61, location.SourceUtf16End);
        Assert.All(report.Diagnostics.Where(d => !ReferenceEquals(d, primary)), d => Assert.Null(d.Location));
        var request = Assert.IsType<CompilerSourceNavigationRequest>(CompilerSourceNavigationRequest.TryCreate(f.Session, report, primary));
        var before = f.Storage.State(); int workers = f.Config.Workers.Created.Count;
        var selection = Assert.IsType<CompilerSourceNavigationPolicy.Selection>(await f.Policy.PrepareAsync(request));
        Assert.Equal(23, selection.TextareaRange.Start); Assert.Equal(59, selection.TextareaRange.End);
        Assert.Equal("\\overcloneUndefinedCommand % Київ 😀",
            selection.ExpectedTextareaText[selection.TextareaRange.Start..selection.TextareaRange.End]);
        Assert.Equal(before, f.Storage.State()); Assert.Equal(workers, f.Config.Workers.Created.Count);
        Assert.True(f.Workspace.SelectFile(request.File.FileId));
        Assert.True(f.Policy.IsCurrent(selection, requireActiveFile: true));
        f.Session.SelectEngine("xelatex");
        Assert.False(f.Policy.IsCurrent(selection)); // New basis does NOT bypass freshness policy.
    });

    // Synthetic renamed variants of the preserved log, not new native executions.
    [Theory][InlineData("thesis.tex")][InlineData("dir/thesis.tex")][InlineData("теза.tex")]
    public void Selected_root_identity_and_leaf_log_are_required_for_fatal_tail(string root) => Run(async () =>
    {
        await using var f = await Open();
        var main = f.Workspace.Files.Single(file => !file.Deleted && file.RelativePath == "main.tex");
        Assert.True(f.Workspace.DeleteFile(main.Id));
        Assert.True(f.Workspace.CreateFileFromBytes(root, Encoding.UTF8.GetBytes(Main)));
        f.Storage.Selection.SetSelection(CompilationTestWorkspace.Select(f.Workspace.Files.Single(file => !file.Deleted && file.RelativePath == root).Id));
        var snapshot = await f.Storage.Snapshot();
        var job = root[(root.LastIndexOf('/') + 1)..^4];
        var log = NativeLog.Replace("(main.tex\n", "(" + root + "\n", StringComparison.Ordinal)
            .Replace("Transcript written on main.log.", "Transcript written on " + job + ".log.", StringComparison.Ordinal);
        CompilerLogReport Parse(string value) => Assert.IsType<CompilerLogParseResult.Parsed>(
            CompilerLogParser.Parse(snapshot, Encoding.UTF8.GetBytes(value), Budget, logIsComplete: false)).Report;
        var error = Assert.Single(Parse(log).Diagnostics.Where(d => d.Code == CompilerDiagnosticCode.Error));
        Assert.Equal(CompilerLocationBasis.NarrowFatalContext, error.Location!.Basis);
        Assert.Equal("chapter.tex", error.Location.RelativePath);
        Assert.All(Parse(log.Replace("Transcript written on " + job + ".log.", "Transcript written on main.log.", StringComparison.Ordinal)).Diagnostics, d => Assert.Null(d.Location));
        Assert.All(Parse(log.Replace("(" + root + "\n", "(chapter.tex\n", StringComparison.Ordinal)).Diagnostics, d => Assert.Null(d.Location));
    });

    private static string Variant(string variant) => variant switch
    {
        "missing-footer" => NativeLog.Replace(Footer, "", StringComparison.Ordinal),
        "wrong-footer" => NativeLog.Replace(Footer, "Transcript written on chapter.log.\n", StringComparison.Ordinal),
        "footer-no-LF" => NativeLog.TrimEnd('\n'),
        "missing-fatal" => NativeLog.Replace(Fatal, "", StringComparison.Ordinal),
        "bad-fatal" => NativeLog.Replace(Fatal, "! Fatal error occurred\n", StringComparison.Ordinal),
        "error-no-LF" => NativeLog.Replace(Error, Error.TrimEnd('\n'), StringComparison.Ordinal),
        "bad-primary" => NativeLog.Replace(Error, "! Undefined control sequence\n", StringComparison.Ordinal),
        "line-no-LF" => NativeLog.Replace(Echo, Echo.TrimEnd('\n'), StringComparison.Ordinal),
        "missing-line" => NativeLog.Replace(Echo, "", StringComparison.Ordinal),
        "line-no-echo" => NativeLog.Replace(Echo, "l.3 \n", StringComparison.Ordinal),
        "wrong-source-echo" => NativeLog.Replace(Echo, "l.3 \\someOtherCommand\n", StringComparison.Ordinal),
        "partial-command-echo" => NativeLog.Replace(Echo, "l.3 \\overclone\n", StringComparison.Ordinal),
        "wrong-line" => NativeLog.Replace("l.3 ", "l.2 ", StringComparison.Ordinal),
        "out-of-range" => NativeLog.Replace("l.3 ", "l.999 ", StringComparison.Ordinal),
        "invalid-line" => NativeLog.Replace("l.3 ", "l.0 ", StringComparison.Ordinal),
        "overflow-line" => NativeLog.Replace("l.3 ", "l.2147483648 ", StringComparison.Ordinal),
        "unknown-top" => NativeLog.Replace("(chapter.tex\n", "(unknown.tex\n", StringComparison.Ordinal),
        "external-top" => NativeLog.Replace("(chapter.tex\n", "(external.sty\n", StringComparison.Ordinal),
        "binary-top" => NativeLog.Replace("(chapter.tex\n", "(asset.bin\n", StringComparison.Ordinal),
        "wrong-main-context" => NativeLog.Replace("(main.tex\n", "(chapter.tex\n", StringComparison.Ordinal),
        "absolute-path" => NativeLog.Replace("(chapter.tex\n", "(/chapter.tex\n", StringComparison.Ordinal),
        "poison-before" => NativeLog.Replace(Error, "macro (poison)\n" + Error, StringComparison.Ordinal),
        "unmatched-close-before" => NativeLog.Replace("(main.tex\n", ")\n(main.tex\n", StringComparison.Ordinal),
        "info-parenthesis" => NativeLog.Replace(Error, "No file (poison).\n" + Error, StringComparison.Ordinal),
        "poison-after" => NativeLog.Replace(Fatal, "macro (poison)\n" + Fatal, StringComparison.Ordinal),
        "echo-parenthesis" => NativeLog.Replace(Echo, "l.3 \\overcloneUndefinedCommand (poison)\n", StringComparison.Ordinal),
        "pop-reopen" => NativeLog.Replace(Fatal, ")\n(chapter.tex\n" + Fatal, StringComparison.Ordinal),
        "fully-close" => NativeLog.Replace(Fatal, ")\n)\n" + Fatal, StringComparison.Ordinal),
        "switch-tail" => NativeLog.Replace(Fatal, ")\n(main.tex\n" + Fatal, StringComparison.Ordinal),
        "context-before-line" => NativeLog.Replace(Echo, ")\n(chapter.tex\n" + Echo, StringComparison.Ordinal),
        "after-footer" => NativeLog + "macro (poison)\n",
        "blank-after-footer" => NativeLog + "\n",
        "second-comment-echo" => NativeLog.Replace(Fatal, "  % another echo\n" + Fatal, StringComparison.Ordinal),
        "prefix-truncated" => NativeLog[NativeLog.IndexOf("(main.tex\n", StringComparison.Ordinal)..],
        "unsupported-banner" => NativeLog.Replace("This is pdfTeX,", "This is XeTeX,", StringComparison.Ordinal),
        _ => throw new ArgumentOutOfRangeException(nameof(variant))
    };

    [Theory]
    [InlineData("missing-footer")][InlineData("wrong-footer")][InlineData("footer-no-LF")]
    [InlineData("missing-fatal")][InlineData("bad-fatal")][InlineData("error-no-LF")][InlineData("bad-primary")]
    [InlineData("line-no-LF")][InlineData("missing-line")][InlineData("line-no-echo")]
    [InlineData("wrong-source-echo")][InlineData("partial-command-echo")][InlineData("wrong-line")]
    [InlineData("out-of-range")][InlineData("invalid-line")][InlineData("overflow-line")]
    [InlineData("unknown-top")][InlineData("external-top")][InlineData("binary-top")]
    [InlineData("wrong-main-context")][InlineData("absolute-path")][InlineData("poison-before")]
    [InlineData("unmatched-close-before")][InlineData("info-parenthesis")][InlineData("poison-after")]
    [InlineData("echo-parenthesis")][InlineData("pop-reopen")][InlineData("fully-close")]
    [InlineData("switch-tail")][InlineData("context-before-line")][InlineData("after-footer")]
    [InlineData("blank-after-footer")][InlineData("second-comment-echo")][InlineData("prefix-truncated")][InlineData("unsupported-banner")]
    public void Synthetic_variants_of_actual_log_never_reauthorize_a_poisoned_or_incomplete_context(string variant) => Run(async () =>
    {
        await using var f = await Open(); await f.Fail(Variant(variant));
        Assert.All(f.Report.Diagnostics, d =>
        {
            Assert.Null(d.Location);
            Assert.Null(CompilerSourceNavigationRequest.TryCreate(f.Session, f.Report, d));
        });
    });

    [Theory]
    [InlineData("log-bytes")][InlineData("line-bytes")][InlineData("log-lines")][InlineData("depth")]
    [InlineData("work")][InlineData("output")][InlineData("diagnostics")][InlineData("source-lines")][InlineData("message")]
    public void Limits_refuse_recovery_even_when_a_footer_is_present(string limit) => Run(async () =>
    {
        await using var f = await Open(); await f.Fail(NativeLog);
        var budget = limit switch
        {
            "log-bytes" => Budget with { MaxLogBytes = 593 },
            "line-bytes" => Budget with { MaxLineBytes = 32 },
            "log-lines" => Budget with { MaxLogLines = 12 },
            "depth" => Budget with { MaxContextDepth = 1 },
            "work" => Budget with { MaxWorkUnits = 64 },
            "output" => Budget with { MaxOutputBytes = 256 },
            "diagnostics" => Budget with { MaxDiagnostics = 1 },
            "source-lines" => Budget with { MaxSourceLines = 2 },
            "message" => Budget with { MaxMessageBytes = 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(limit))
        };
        var report = Assert.IsType<CompilerLogParseResult.Parsed>(CompilerLogParser.Parse(f.Report.Snapshot,
            Encoding.UTF8.GetBytes(NativeLog), budget, logIsComplete: false)).Report;
        Assert.All(report.Diagnostics, d => Assert.Null(d.Location));
        Assert.NotEqual(CompilerLogFlags.None, report.Flags & (CompilerLogFlags.LogByteLimit | CompilerLogFlags.LineByteLimit
            | CompilerLogFlags.LineCountLimit | CompilerLogFlags.ResourceLimit | CompilerLogFlags.IncompleteDiagnostic));
    });

    [Fact] public void Only_primary_error_is_recovered_and_explicit_file_line_behavior_remains_separate() => Run(async () =>
    {
        await using var f = await Open();
        await f.Fail(NativeLog.Replace(Error, "LaTeX Warning: Reference `x' undefined on input line 1.\n" + Error, StringComparison.Ordinal));
        Assert.Null(Assert.Single(f.Report.Diagnostics.Where(d => d.Code == CompilerDiagnosticCode.Reference)).Location);
        Assert.Equal(CompilerLocationBasis.NarrowFatalContext,
            Assert.Single(f.Report.Diagnostics.Where(d => d.Code == CompilerDiagnosticCode.Error)).Location!.Basis);
        // Synthetic explicit-file:line suffix is NOT part of the real log. It
        // invalidates fatal-tail recovery, but the original explicit basis keeps
        // its independent exact-file semantics; no explicit evidence is invented.
        await f.Fail(NativeLog + "main.tex:1: LaTeX Error: explicit variant\n");
        var errors = f.Report.Diagnostics.Where(d => d.Code == CompilerDiagnosticCode.Error).ToArray();
        Assert.Equal(2, errors.Length); Assert.Null(errors[0].Location);
        Assert.Equal(CompilerLocationBasis.ExplicitFileLine, errors[1].Location!.Basis);
    });
}
