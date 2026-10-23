using System.Text;
using Client.Compilation;
using Client.Compilation.Diagnostics;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Transfer;
using Xunit;

namespace Workspace.Tests.CompilationDiagnostics;

// ALL logs in this class are SYNTHETIC, including the fatal-tail examples.
// No native execution, browser proof, source spans or PDF measurement is claimed.
public sealed class CompilerBoxWarningTests
{
    private static readonly CompilerLogBudget Budget = new(32, 1024 * 1024, 65536,
        65536, 4096, 1000, 128, 1024, 32, 10000, 10_000_000, 2_000_000);
    private const string OverH = @"Overfull \hbox (1.23456pt too wide) in paragraph at lines 2--3";
    private const string UnderH = @"Underfull \hbox (badness 10000) in paragraph at lines 2--3";
    private const string OverV = @"Overfull \vbox (12.0pt too high) detected at line 9";
    private const string UnderV = @"Underfull \vbox (badness 10000) has occurred while \output is active";
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static bool IsBox(CompilerLogDiagnostic d) => d.Code is CompilerDiagnosticCode.OverfullHBox
        or CompilerDiagnosticCode.UnderfullHBox or CompilerDiagnosticCode.OverfullVBox or CompilerDiagnosticCode.UnderfullVBox;

    private static async Task<CompilationInputSnapshot> Snapshot(string? source = null)
    {
        await using var f = await CompilationTestWorkspace.Open();
        f.Workspace.Files.Clear();
        byte[] bytes = [239, 187, 191, .. Bytes(source ?? "% SYNTHETIC Україна 😀\r\n\\Bad\r\nthird\r\n")];
        Assert.True(TextContent.TryDecode(bytes, out var text, out var bom));
        f.Workspace.Files.Add(new(FileId.New(), "main.tex", "text", text, null, bom,
            false, false, false, true, false, null, 0, bytes));
        f.Selection.SetSelection(CompilationTestWorkspace.Select(f.Workspace.Files[0].Id));
        return await f.Snapshot();
    }
    private static CompilerLogReport Parse(CompilationInputSnapshot s, string log, CompilerLogBudget? b = null, bool complete = true)
        => Assert.IsType<CompilerLogParseResult.Parsed>(CompilerLogParser.Parse(s, Bytes(log), b ?? Budget, complete)).Report;

    [Theory]
    [InlineData(OverH, CompilerDiagnosticCode.OverfullHBox, "TEX105", "The compiler reported an overfull horizontal box.")]
    [InlineData(UnderH, CompilerDiagnosticCode.UnderfullHBox, "TEX106", "The compiler reported an underfull horizontal box.")]
    [InlineData(OverV, CompilerDiagnosticCode.OverfullVBox, "TEX107", "The compiler reported an overfull vertical box.")]
    [InlineData(UnderV, CompilerDiagnosticCode.UnderfullVBox, "TEX108", "The compiler reported an underfull vertical box.")]
    public async Task FourKindsHaveFixedSafeMessagesAndNoSourceLocation(string header, CompilerDiagnosticCode code, string stable, string safe)
    {
        var s = await Snapshot();
        var r = Parse(s, "(main.tex\n" + header + "\n)\n");
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal(code, d.Code); Assert.Equal(stable, d.StableCode); Assert.Equal(safe, d.SafeMessage);
        Assert.Equal(CompilerDiagnosticSeverity.Warning, d.Severity);
        Assert.Null(d.Location); Assert.Equal(CompilerLocationIssue.MissingLine, d.LocationIssue);
        Assert.Equal(CompilerLogFlags.None, r.Flags); Assert.Same(s, r.Snapshot);
        Assert.Equal(s.Identity, r.SnapshotIdentity); Assert.Equal(s.ContentDigest, r.InputContentDigest);
        Assert.Equal(10, d.LogStartByte); Assert.Equal(10 + Bytes(header).Length, d.LogEndByte);
        var envelope = Assert.Single(r.EnvelopeEntries); Assert.Equal(stable, envelope.Code); Assert.Equal(safe, envelope.SafeMessage);
        Assert.DoesNotContain("1.23456", safe); Assert.DoesNotContain("main.tex", safe);
        Assert.DoesNotContain("10000", safe); Assert.DoesNotContain("output", safe);
    }

    [Theory]
    [InlineData(@"Overfull \hbox (0.00002pt too wide)")]
    [InlineData(@"Overfull \hbox (32767.99999pt too wide) in alignment at lines 1--2147483647")]
    [InlineData(@"Underfull \hbox (badness 0) detected at line 1")]
    [InlineData(@"Underfull \hbox (badness 42) has occurred while \output is active")]
    [InlineData(@"Overfull \vbox (1.0pt too high) in alignment at lines 1--2")]
    [InlineData(@"Overfull \vbox (2.00001pt too high) has occurred while \output is active")]
    [InlineData(@"Underfull \vbox (badness 7) in alignment at lines 3--4")]
    [InlineData(@"Underfull \vbox (badness 99) detected at line 2147483647")]
    [InlineData(@"Overfull \vbox (1.0pt too high) has occurred while \output is active []")]
    [InlineData(@"Underfull \vbox (badness 1) has occurred while \output is active []")]
    public async Task FiniteStandardContextFormsRemainWarningsWithoutUsingTheirNumbersAsLines(string header)
    {
        var r = Parse(await Snapshot(), header + "\n");
        var d = Assert.Single(r.Diagnostics); Assert.True(IsBox(d));
        Assert.Equal(CompilerDiagnosticSeverity.Warning, d.Severity); Assert.Null(d.Location);
        Assert.Equal(CompilerLocationIssue.UnknownFile, d.LocationIssue);
        Assert.Equal(CompilerLogFlags.None, r.Flags);
    }

    [Theory]
    [InlineData(@"Overfull \hbox (1.0pt too wide) in paragraph at lines 2--3", "\n")]
    [InlineData(@"Underfull \hbox (badness 10) in paragraph at lines 2--3", "\r\n")]
    [InlineData(@"Overfull \vbox (1.0pt too high) detected at line 2", "\r")]
    public async Task BomCrlfAndUnicodeSurroundingSourceDoNotBecomeWarningTextOrOffsets(string header, string newline)
    {
        var s = await Snapshot("% Київ 🙂\r\n\\Bad\r\n");
        string prefix = "\uFEFF(main.tex" + newline;
        var r = Parse(s, prefix + header + newline + ")" + newline);
        var d = Assert.Single(r.Diagnostics);
        Assert.True(IsBox(d)); Assert.Null(d.Location);
        Assert.Equal(Bytes(prefix).Length, d.LogStartByte);
        Assert.Equal(Bytes(prefix + header).Length, d.LogEndByte);
        Assert.DoesNotContain("Київ", d.SafeMessage); Assert.DoesNotContain("🙂", d.SafeMessage);
        Assert.Equal(CompilerLogFlags.None, r.Flags);
    }

    [Fact]
    public async Task DuplicateOccurrencesStayInFullReportWhileEnvelopeDeduplicatesOnlyCodes()
    {
        var r = Parse(await Snapshot(), "(main.tex\n" + OverH + "\n" + OverH + "\n" + UnderV + "\n)\n");
        Assert.Equal(3, r.Diagnostics.Length); Assert.Equal(2, r.EnvelopeEntries.Length);
        Assert.Equal(new[] { "TEX105", "TEX105", "TEX108" }, r.Diagnostics.Select(d => d.StableCode));
        Assert.Equal(new[] { "TEX105", "TEX108" }, r.EnvelopeEntries.Select(d => d.Code));
        Assert.True(r.Diagnostics[0].LogEndByte < r.Diagnostics[1].LogStartByte);
        Assert.All(r.Diagnostics, d => Assert.Null(d.Location));
    }

    [Theory]
    [InlineData(@"overfull \hbox (1.0pt too wide)")]
    [InlineData(@"Overfull  \hbox (1.0pt too wide)")]
    [InlineData(@"Overfull \\hbox (1.0pt too wide)")]
    [InlineData(@"Overfull \Hbox (1.0pt too wide)")]
    [InlineData(@"Underfull \vBox (badness 12)")]
    [InlineData(@"Overfull \hbox (1pt too wide)")]
    [InlineData(@"Overfull \hbox (1.pt too wide)")]
    [InlineData(@"Overfull \hbox (.1pt too wide)")]
    [InlineData(@"Overfull \hbox (-1.0pt too wide)")]
    [InlineData(@"Overfull \hbox (+1.0pt too wide)")]
    [InlineData(@"Overfull \hbox (1e2pt too wide)")]
    [InlineData(@"Overfull \hbox (01.0pt too wide)")]
    [InlineData(@"Overfull \hbox (1.000001pt too wide)")]
    [InlineData(@"Overfull \hbox (32768.0pt too wide)")]
    [InlineData(@"Overfull \hbox (999999999999999999999999.0pt too wide)")]
    [InlineData(@"Overfull \hbox (NaNpt too wide)")]
    [InlineData(@"Overfull \hbox (Infinitypt too wide)")]
    [InlineData(@"Overfull \hbox (1.0mm too wide)")]
    [InlineData(@"Overfull \hbox (1.0pt too high)")]
    [InlineData(@"Overfull \vbox (1.0pt too wide)")]
    [InlineData(@"Underfull \hbox (badness -1)")]
    [InlineData(@"Underfull \hbox (badness 01)")]
    [InlineData(@"Underfull \hbox (badness 10001)")]
    [InlineData(@"Underfull \hbox (badness 99999999999999999999)")]
    [InlineData(@"Underfull \hbox (badness 1.0)")]
    [InlineData(@"Underfull \hbox (badness  1)")]
    [InlineData(@"Underfull \hbox (1.0pt too wide)")]
    [InlineData(@"Overfull \hbox (1.0pt too wide")]
    [InlineData(@"Overfull \hbox (1.0pt too wide) ")]
    [InlineData(@"Overfull \hbox (1.0pt too wide).")]
    [InlineData(@"Overfull \hbox (1.0pt too wide) []")]
    [InlineData(@"Overfull \hbox (1.0pt too wide) <script>secret</script>")]
    [InlineData(@"Overfull \hbox (1.0pt too wide) on input line 2")]
    [InlineData(@"Overfull \vbox (1.0pt too high) in paragraph at lines 1--2")]
    [InlineData(@"Underfull \hbox (badness 1) in paragraph at lines 0--2")]
    [InlineData(@"Underfull \hbox (badness 1) in paragraph at lines 1-2")]
    [InlineData(@"Underfull \hbox (badness 1) in paragraph at lines 1--2147483648")]
    [InlineData(@"Underfull \vbox (badness 1) detected at line 0")]
    [InlineData(@"Underfull \vbox (badness 1) detected at line 2evil")]
    [InlineData(@"Underfull \vbox (badness 1) has occurred while output is active")]
    [InlineData(@"Underfull \vbox (badness 1) has occurred while \\output is active")]
    [InlineData(@"Underfull \hbox (badness 1) has occurred while \output is active []")]
    [InlineData(@"Overfull \vbox (1.0pt too high) detected at line 2 []")]
    [InlineData(@"Underfull \vbox (badness 1) in alignment at lines 1--2 []")]
    [InlineData(@"Underfull \vbox (badness 1) has occurred while \output is active[]")]
    [InlineData(@"Underfull \vbox (badness 1) has occurred while \output is active  []")]
    [InlineData(@"Underfull \vbox (badness 1) has occurred while \output is active [ ]")]
    [InlineData(@"Underfull \vbox (badness 1) has occurred while \output is active [][]")]
    [InlineData(@"Underfull \vbox (badness 1) has occurred while \output is active [] extra")]
    [InlineData(@"Overfull \vbox (1.0pt too high) has occurred while \output is active [] ")]
    [InlineData(@"Overfull \vbox (1.0pt too high) has occurred while \output is active []()")]
    [InlineData(@"prose: Overfull \hbox (1.0pt too wide)")]
    [InlineData(@" Overfull \hbox (1.0pt too wide)")]
    [InlineData(@"<span>Overfull \hbox (1.0pt too wide)</span>")]
    [InlineData(@"l.2 Overfull \hbox (1.0pt too wide)")]
    [InlineData(@"main.tex:2: Overfull \hbox (1.0pt too wide)")]
    public async Task MalformedWrappedEchoAndNearMissRecordsAreUnknownNotErrorsOrWarnings(string record)
    {
        var r = Parse(await Snapshot(), record + "\n");
        Assert.NotEmpty(r.Diagnostics); Assert.All(r.Diagnostics, d => Assert.Equal(CompilerDiagnosticCode.Unknown, d.Code));
        Assert.All(r.Diagnostics, d => Assert.Equal(CompilerDiagnosticSeverity.Unknown, d.Severity));
        Assert.True(r.Flags.HasFlag(CompilerLogFlags.UnknownContent));
        Assert.All(r.Diagnostics, d => { Assert.Null(d.Location); Assert.DoesNotContain("secret", d.SafeMessage); });
    }

    [Theory]
    [InlineData(@"! Overfull \hbox (1.0pt too wide)")]
    [InlineData(@"! Underfull \vbox (badness 10000)")]
    [InlineData(@"! Overfull arbitrary errmessage SECRET")]
    [InlineData(@"! Underfull arbitrary errmessage SECRET")]
    public async Task GenuineBangErrorsBeginningWithBoxWordsKeepOriginalPendingLineBehavior(string error)
    {
        var r = Parse(await Snapshot(), "(main.tex\n" + error + "\nl.2 \\Bad\n)\n");
        var d = Assert.Single(r.Diagnostics); Assert.Equal(CompilerDiagnosticCode.Error, d.Code);
        Assert.Equal(CompilerDiagnosticSeverity.Error, d.Severity); Assert.Equal("TEX005", d.StableCode);
        Assert.Equal("The compiler reported an error; review the local log.", d.SafeMessage);
        Assert.NotNull(d.Location); Assert.Equal(2, d.Location.Line);
        Assert.Equal(CompilerLocationBasis.BalancedNarrowContext, d.Location.Basis);
        Assert.DoesNotContain(r.Diagnostics, IsBox); Assert.DoesNotContain("SECRET", d.SafeMessage);
    }

    [Theory]
    [InlineData(@"main.tex:2: ! Overfull \hbox (1.0pt too wide)")]
    [InlineData(@"main.tex:2: ! Underfull \vbox (badness 10000)")]
    public async Task ExplicitFileLineBangErrorKeepsOriginalErrorAndLocation(string record)
    {
        var r = Parse(await Snapshot(), record + "\n"); var d = Assert.Single(r.Diagnostics);
        Assert.Equal(CompilerDiagnosticCode.Error, d.Code); Assert.Equal(CompilerDiagnosticSeverity.Error, d.Severity);
        Assert.NotNull(d.Location); Assert.Equal(2, d.Location.Line);
        Assert.Equal(CompilerLocationBasis.ExplicitFileLine, d.Location.Basis);
        Assert.Equal("The compiler reported an error; review the local log.", d.SafeMessage);
        Assert.DoesNotContain(r.Diagnostics, IsBox);
    }

    [Fact]
    public async Task ASecondBangWithBoxWordsFlushesEarlierErrorButStillUsesItsOwnLine()
    {
        var r = Parse(await Snapshot(), "(main.tex\n! First pending error.\n! Overfull \\hbox (1.0pt too wide)\nl.2 \\Bad\n)\n");
        Assert.Equal(2, r.Diagnostics.Length); Assert.All(r.Diagnostics, d => Assert.Equal(CompilerDiagnosticCode.Error, d.Code));
        Assert.Null(r.Diagnostics[0].Location); Assert.Equal(CompilerLocationIssue.MissingLine, r.Diagnostics[0].LocationIssue);
        Assert.Equal(2, r.Diagnostics[1].Location!.Line); Assert.True(r.Flags.HasFlag(CompilerLogFlags.IncompleteDiagnostic));
        Assert.DoesNotContain(r.Diagnostics, IsBox);
    }

    [Fact]
    public async Task ExistingLatexWarningWrapperRemainsGenericWarningNotSubstringClassifiedAsBox()
    {
        var r = Parse(await Snapshot(), "LaTeX Warning: " + OverH + "\n"); var d = Assert.Single(r.Diagnostics);
        Assert.Equal(CompilerDiagnosticCode.Warning, d.Code); Assert.Equal("TEX104", d.StableCode);
        Assert.Equal(CompilerDiagnosticSeverity.Warning, d.Severity); Assert.DoesNotContain(r.Diagnostics, IsBox);
    }

    [Fact]
    public async Task HeaderParenthesesDoNotPoisonOtherwiseBalancedReliableErrorAndReferenceNavigation()
    {
        var r = Parse(await Snapshot(), "(main.tex\n" + OverH + "\n! Missing $ inserted.\nl.2 secret\nLaTeX Warning: Reference `x' undefined on input line 2.\n)\n");
        Assert.Equal(CompilerLogFlags.None, r.Flags);
        Assert.Null(Assert.Single(r.Diagnostics.Where(IsBox)).Location);
        foreach (var d in r.Diagnostics.Where(d => !IsBox(d)))
        { Assert.NotNull(d.Location); Assert.Equal(2, d.Location.Line); Assert.Equal(CompilerLocationBasis.BalancedNarrowContext, d.Location.Basis); }
    }

    [Fact]
    public async Task DetailEchoRemainsUnknownAndCanPoisonContextRatherThanBeingParsedAsAFile()
    {
        var r = Parse(await Snapshot(), "(main.tex\n" + UnderH + "\n\\OT1/cmr/m/n/10 (SOURCE_SECRET)\n! Missing $ inserted.\nl.2 secret\n)\n");
        Assert.True(r.Flags.HasFlag(CompilerLogFlags.UnknownContent)); Assert.True(r.Flags.HasFlag(CompilerLogFlags.UncertainContext));
        Assert.Contains(r.Diagnostics, d => d.Code == CompilerDiagnosticCode.Unknown);
        Assert.Null(Assert.Single(r.Diagnostics.Where(d => d.Code == CompilerDiagnosticCode.Error)).Location);
        Assert.DoesNotContain(r.EnvelopeEntries, e => e.SafeMessage.Contains("SOURCE_SECRET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StickyUncertaintyIsNotRehabilitatedByARecognizedBoxHeaderOrLaterBalancedClose()
    {
        var r = Parse(await Snapshot(), "(main.tex\nprose (unknown.tex)\n" + OverV + "\n)\n");
        var box = Assert.Single(r.Diagnostics.Where(IsBox)); Assert.Null(box.Location);
        Assert.Equal(CompilerLocationIssue.UncertainContext, box.LocationIssue);
        Assert.True(r.Flags.HasFlag(CompilerLogFlags.UncertainContext));
        r = Parse(await Snapshot(), "(main.tex\n" + UnderV + "\n");
        Assert.Null(Assert.Single(r.Diagnostics).Location); Assert.True(r.Flags.HasFlag(CompilerLogFlags.UncertainContext));
        Assert.Equal(CompilerLocationIssue.MissingLine, r.Diagnostics[0].LocationIssue);
    }

    [Fact]
    public async Task UnknownExternalTopFrameDoesNotFallBackToMain()
    {
        var r = Parse(await Snapshot(), "(main.tex\n(article.cls\n" + OverH + "\n)\n)\n");
        var d = Assert.Single(r.Diagnostics); Assert.Null(d.Location);
        Assert.Equal(CompilerLocationIssue.UnknownFile, d.LocationIssue);
    }

    [Fact]
    public async Task BoxAfterPendingBangRetainsEarlierIncompleteErrorAndDoesNotStealLaterLineEcho()
    {
        var r = Parse(await Snapshot(), "(main.tex\n! Undefined control sequence.\n" + OverH + "\nl.2 SECRET_ECHO\n)\n");
        var error = Assert.Single(r.Diagnostics.Where(d => d.Code == CompilerDiagnosticCode.Error));
        Assert.Null(error.Location); Assert.Equal(CompilerLocationIssue.MissingLine, error.LocationIssue);
        Assert.True(r.Flags.HasFlag(CompilerLogFlags.IncompleteDiagnostic));
        Assert.Single(r.Diagnostics.Where(IsBox)); Assert.Contains(r.Diagnostics, d => d.Code == CompilerDiagnosticCode.Unknown);
        Assert.All(r.Diagnostics, d => Assert.Null(d.Location));
    }

    [Fact]
    public async Task BoxLikeMalformedRecordDoesNotDiscardAPendingBangEither()
    {
        var r = Parse(await Snapshot(), "(main.tex\n! Missing $ inserted.\nOverfull \\hbox (bad metric)\nl.2 echo\n)\n");
        Assert.Single(r.Diagnostics.Where(d => d.Code == CompilerDiagnosticCode.Error));
        Assert.DoesNotContain(r.Diagnostics, IsBox); Assert.All(r.Diagnostics, d => Assert.Null(d.Location));
        Assert.True(r.Flags.HasFlag(CompilerLogFlags.IncompleteDiagnostic));
    }

    [Fact]
    public async Task ExistingPendingBangSourceEchoIsStillAnErrorEchoNotASecondBoxDiagnostic()
    {
        var r = Parse(await Snapshot(), "(main.tex\n! Missing $ inserted.\nl.2 Overfull \\hbox (1.0pt too wide)\n)\n");
        Assert.DoesNotContain(r.Diagnostics, IsBox); Assert.Single(r.Diagnostics.Where(d => d.Code == CompilerDiagnosticCode.Error));
    }

    [Fact]
    public async Task OriginalFatalTailProofIsNotRehabilitatedOrChangedByBoxClassification()
    {
        const string prefix = "This is pdfTeX, SYNTHETIC\nentering extended mode\n(main.tex\n";
        const string tail = "! Undefined control sequence.\nl.2 \\Bad\n!  ==> Fatal error occurred, no output PDF file produced!\nTranscript written on main.log.\n";
        var s = await Snapshot();
        var clean = Parse(s, prefix + tail);
        var primary = Assert.Single(clean.Diagnostics.Where(d => d.Code == CompilerDiagnosticCode.Error));
        Assert.Equal(CompilerLocationBasis.NarrowFatalContext, primary.Location!.Basis);
        var before = Parse(s, prefix + OverH + "\n" + tail);
        Assert.Single(before.Diagnostics.Where(IsBox));
        Assert.Null(Assert.Single(before.Diagnostics.Where(d => d.Code == CompilerDiagnosticCode.Error)).Location);
        var during = Parse(s, prefix + tail.Replace("l.2 \\Bad\n", "l.2 \\Bad\n" + UnderV + "\n", StringComparison.Ordinal));
        Assert.Null(Assert.Single(during.Diagnostics.Where(d => d.Code == CompilerDiagnosticCode.Error)).Location);
        Assert.True(during.Flags.HasFlag(CompilerLogFlags.UncertainContext));
    }

    [Fact]
    public async Task UnterminatedAndWrappedPhysicalHeaderNeverBecomesABoxWarning()
    {
        var s = await Snapshot();
        var r = Parse(s, OverH);
        Assert.DoesNotContain(r.Diagnostics, IsBox); Assert.Equal(CompilerDiagnosticCode.Unknown, Assert.Single(r.Diagnostics).Code);
        Assert.True(r.Flags.HasFlag(CompilerLogFlags.IncompleteDiagnostic));
        r = Parse(s, "Overfull \\hbox (1.23456pt too wide) in paragraph at\nlines 2--3\n");
        Assert.DoesNotContain(r.Diagnostics, IsBox); Assert.Equal(2, r.Diagnostics.Length);
        Assert.All(r.Diagnostics, d => Assert.Equal(CompilerDiagnosticCode.Unknown, d.Code));
    }

    [Fact]
    public async Task CompletePhysicalRecordInAnHonestlyIncompleteLogCanBeClassifiedButNotLocated()
    {
        var r = Parse(await Snapshot(), OverH + "\n", complete: false);
        Assert.True(IsBox(Assert.Single(r.Diagnostics))); Assert.Null(r.Diagnostics[0].Location);
        Assert.True(r.Flags.HasFlag(CompilerLogFlags.CallerIncomplete));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PhysicalByteLineAndLogLimitsNeverPromoteClippedHeader(int kind)
    {
        var b = kind switch
        {
            0 => Budget with { MaxLogBytes = Bytes(OverH).Length - 1 },
            1 => Budget with { MaxLineBytes = 18 },
            _ => Budget with { MaxLogLines = 1 }
        };
        var text = kind == 2 ? "This is pdfTeX, SYNTHETIC\n" + OverH + "\n" : OverH + "\n";
        var r = Parse(await Snapshot(), text, b);
        Assert.DoesNotContain(r.Diagnostics, IsBox); Assert.True(r.Flags.HasFlag(CompilerLogFlags.IncompleteDiagnostic));
        Assert.True(r.Flags.HasFlag(CompilerLogFlags.UnknownContent));
        Assert.Contains(r.Diagnostics, d => d.Code == CompilerDiagnosticCode.Unknown);
        Assert.True(r.WorkUnits <= b.MaxWorkUnits); Assert.True(r.RetainedBytes <= b.MaxOutputBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task DiagnosticMessageWorkAndOutputLimitsRemainExplicit(int kind)
    {
        var b = kind switch
        {
            0 => Budget with { MaxDiagnostics = 1 },
            1 => Budget with { MaxMessageBytes = 1 },
            2 => Budget with { MaxWorkUnits = 1 },
            _ => Budget with { MaxOutputBytes = 1 }
        };
        var r = Parse(await Snapshot(), OverH + "\n" + UnderV + "\n", b);
        Assert.True(r.Flags.HasFlag(CompilerLogFlags.ResourceLimit));
        Assert.True(r.Diagnostics.Length <= b.MaxDiagnostics); Assert.True(r.WorkUnits <= b.MaxWorkUnits);
        Assert.True(r.RetainedBytes <= b.MaxOutputBytes); Assert.All(r.Diagnostics, d => Assert.Null(d.Location));
    }

    [Fact]
    public async Task AdditionalBoxScansAreChargedAndCannotReadSourceLinesForNavigation()
    {
        var s = await Snapshot(new string('x', 10000) + "\n\\Bad\n");
        var r = Parse(s, OverH + "\n", Budget with { MaxSourceLines = 1 });
        Assert.True(IsBox(Assert.Single(r.Diagnostics))); Assert.Null(r.Diagnostics[0].Location);
        Assert.False(r.Flags.HasFlag(CompilerLogFlags.ResourceLimit));
        long accountedFloor = Bytes(OverH + "\n").Length + Bytes(OverH).Length * 32L + OverH.Length * 8L;
        Assert.True(r.WorkUnits >= accountedFloor, "base scan and additional8N grammar work both charged");
    }

    [Fact]
    public async Task OneLessThanActualWorkReservationRefusesWithoutExceedingItsBudget()
    {
        var s = await Snapshot(); var full = Parse(s, OverH + "\n" + UnderV + "\n");
        var b = Budget with { MaxWorkUnits = full.WorkUnits - 1 };
        var partial = Parse(s, OverH + "\n" + UnderV + "\n", b);
        Assert.True(partial.Flags.HasFlag(CompilerLogFlags.ResourceLimit)); Assert.True(partial.WorkUnits <= b.MaxWorkUnits);
        Assert.All(partial.Diagnostics, d => Assert.Null(d.Location));
    }

    [Fact]
    public async Task CancellationIsTypedBeforeAnySyntheticBoxParsing()
    {
        var s = await Snapshot(); using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.IsType<CompilerLogParseResult.Cancelled>(CompilerLogParser.Parse(s, Bytes(OverH + "\n"), Budget, true, cts.Token));
    }

    [Fact]
    public void EnumNumericValuesAreAppendOnly()
    {
        Assert.Equal(0, (int)CompilerDiagnosticCode.MissingFile); Assert.Equal(1, (int)CompilerDiagnosticCode.Encoding);
        Assert.Equal(2, (int)CompilerDiagnosticCode.Metric); Assert.Equal(3, (int)CompilerDiagnosticCode.Unicode);
        Assert.Equal(4, (int)CompilerDiagnosticCode.Error); Assert.Equal(5, (int)CompilerDiagnosticCode.Fatal);
        Assert.Equal(6, (int)CompilerDiagnosticCode.Reference); Assert.Equal(7, (int)CompilerDiagnosticCode.Citation);
        Assert.Equal(8, (int)CompilerDiagnosticCode.Rerun); Assert.Equal(9, (int)CompilerDiagnosticCode.Warning);
        Assert.Equal(10, (int)CompilerDiagnosticCode.Unknown); Assert.Equal(11, (int)CompilerDiagnosticCode.OverfullHBox);
        Assert.Equal(12, (int)CompilerDiagnosticCode.UnderfullHBox); Assert.Equal(13, (int)CompilerDiagnosticCode.OverfullVBox);
        Assert.Equal(14, (int)CompilerDiagnosticCode.UnderfullVBox);
    }
}
