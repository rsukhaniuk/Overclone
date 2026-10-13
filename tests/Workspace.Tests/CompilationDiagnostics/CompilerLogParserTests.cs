using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Client.Compilation;
using Client.Compilation.Diagnostics;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Transfer;
using Xunit;

namespace Workspace.Tests.CompilationDiagnostics;

// Unless read from Fixtures/*.log, every log below is SYNTHETIC. No native
// execution, file:line runtime availability, bibliography support or PDF proof.
public sealed class CompilerLogParserTests
{
    private static readonly CompilerLogBudget Budget = new(32, 1024 * 1024, 65536,
        65536, 4096, 1000, 128, 1024, 32, 10000, 10_000_000, 2_000_000);
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static byte[] Fixture(string name, [CallerFilePath] string file = "")
        => File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(file)!, "Fixtures", name));
    private static async Task<CompilationInputSnapshot> Snapshot(params (string Path, byte[] Bytes)[] files)
    {
        await using var f = await CompilationTestWorkspace.Open();
        f.Workspace.Files.Clear();
        if (files.Length == 0) files = [("main.tex", Bytes("one\ntwo\nthree\n"))];
        foreach (var (path, bytes) in files)
        {
            Assert.True(TextContent.TryDecode(bytes, out var text, out var bom));
            f.Workspace.Files.Add(new(FileId.New(), path, "text", text, null, bom,
                false, false, false, true, false, null, 0, bytes.ToArray()));
        }
        f.Selection.SetSelection(CompilationTestWorkspace.Select(f.Workspace.Files[0].Id));
        return await f.Snapshot();
    }
    private static CompilerLogReport Parse(CompilationInputSnapshot snapshot, string text, CompilerLogBudget? budget = null, bool complete = true)
        => Assert.IsType<CompilerLogParseResult.Parsed>(CompilerLogParser.Parse(snapshot, Bytes(text), budget ?? Budget, complete)).Report;
    private static CompilerLogDiagnostic One(CompilerLogReport report, CompilerDiagnosticCode code)
        => Assert.Single(report.Diagnostics.Where(d => d.Code == code));

    [Theory]
    [InlineData("missing-class.log", CompilerDiagnosticCode.MissingFile)]
    [InlineData("encoding.log", CompilerDiagnosticCode.Encoding)]
    [InlineData("metric.log", CompilerDiagnosticCode.Metric)]
    [InlineData("unicode.log", CompilerDiagnosticCode.Unicode)]
    public async Task GenuineCapturedErrorExcerptsAreRecognizedWithoutInventingFileContext(string name, CompilerDiagnosticCode code)
    {
        var snapshot = await Snapshot(("main.tex", Fixture("emoji-only.tex")));
        var report = Assert.IsType<CompilerLogParseResult.Parsed>(CompilerLogParser.Parse(snapshot, Fixture(name), Budget, false)).Report;
        var d = One(report, code); Assert.Equal(CompilerDiagnosticSeverity.Error, d.Severity);
        Assert.Null(d.Location); Assert.NotEqual(CompilerLocationIssue.None, d.LocationIssue);
        Assert.True(report.Flags.HasFlag(CompilerLogFlags.CallerIncomplete));
        Assert.Same(snapshot, report.Snapshot); Assert.Equal(snapshot.Identity, report.SnapshotIdentity);
        Assert.Equal(snapshot.ContentDigest, report.InputContentDigest); Assert.Equal("compiler", d.Origin);
        Assert.DoesNotContain("^^f0", d.SafeMessage); Assert.DoesNotContain("article.cls", d.SafeMessage);
    }
    [Fact]
    public async Task GenuineWarningsStayWarningsAndBibliographyTailIsExplicitlyUnknownNotClean()
    {
        var snapshot = await Snapshot();
        var warnings = Assert.IsType<CompilerLogParseResult.Parsed>(CompilerLogParser.Parse(snapshot, Fixture("warnings.log"), Budget, false)).Report;
        Assert.Equal(3, warnings.Diagnostics.Count(d => d.Code == CompilerDiagnosticCode.Reference));
        Assert.DoesNotContain(warnings.Diagnostics, d => d.Severity == CompilerDiagnosticSeverity.Error);
        var bib = Assert.IsType<CompilerLogParseResult.Parsed>(CompilerLogParser.Parse(snapshot, Fixture("bibtex-tail.log"), Budget, true)).Report;
        Assert.True(bib.Flags.HasFlag(CompilerLogFlags.UnknownContent)); Assert.NotEmpty(bib.Diagnostics);
        Assert.All(bib.Diagnostics, d => Assert.Null(d.Location));
    }
    [Fact]
    public async Task GenuinePositiveInfoIsNotAnErrorOrACompileSuccessClaim()
    {
        var snapshot = await Snapshot();
        var report = Assert.IsType<CompilerLogParseResult.Parsed>(CompilerLogParser.Parse(snapshot, Fixture("info.log"), Budget, true)).Report;
        Assert.Empty(report.Diagnostics); Assert.Equal(CompilerLogFlags.None, report.Flags);
        // No native exit/status or PDF data is accepted/derived by this API.
        Assert.Same(snapshot, report.Snapshot);
    }
    [Theory]
    [InlineData("! Undefined control sequence.", CompilerDiagnosticCode.Error)]
    [InlineData("! Missing $ inserted.", CompilerDiagnosticCode.Error)]
    [InlineData("!  ==> Fatal error occurred, no output PDF file produced!", CompilerDiagnosticCode.Fatal)]
    public async Task StandardBangErrorsAndFatalStopsKeepSafeFixedMessages(string error, CompilerDiagnosticCode code)
    {
        var report = Parse(await Snapshot(), "(main.tex\n" + error + "\nl.2 SOURCE_SECRET\n)\n");
        var d = One(report, code); Assert.Equal(CompilerDiagnosticSeverity.Error, d.Severity);
        Assert.DoesNotContain("SOURCE_SECRET", d.SafeMessage);
        Assert.DoesNotContain(report.EnvelopeEntries, x => x.SafeMessage.Contains("SOURCE_SECRET", StringComparison.Ordinal));
    }
    [Fact]
    public async Task BalancedNarrowContextRestoresMainAfterIncludedChapter()
    {
        var snapshot = await Snapshot(("main.tex", Bytes("main1\nmain2")), ("chapters/one.tex", Bytes("chapter1\nchapter2")));
        var r = Parse(snapshot, "(main.tex\n(chapters/one.tex\n! Undefined control sequence.\nl.2 secret\n)\n! Missing $ inserted.\nl.1 secret\n)\n");
        Assert.Equal(2, r.Diagnostics.Length);
        var a = Assert.IsType<CompilerLineLocation>(r.Diagnostics[0].Location);
        var b = Assert.IsType<CompilerLineLocation>(r.Diagnostics[1].Location);
        Assert.Equal("chapters/one.tex", a.RelativePath); Assert.Equal(9, a.StartByte); Assert.Equal(17, a.EndByte);
        Assert.Equal(CompilerLocationBasis.BalancedNarrowContext, a.Basis);
        Assert.Equal("main.tex", b.RelativePath); Assert.Equal(0, b.StartByte); Assert.Equal(5, b.EndByte);
        Assert.Equal(snapshot.Files.Single(f => f.RelativePath == a.RelativePath).Hash, a.FileHash);
    }
    [Theory]
    [InlineData("(main.tex\n", "")]
    [InlineData("(main.tex\n(unknown.tex\n", ")\n)\n")]
    [InlineData("(main.tex\n(macro produced filename)\n", ")\n")]
    [InlineData("(main.tex\n(chapters/wrapped\nname.tex\n", ")\n)\n")]
    [InlineData("(main.tex\n", ")extra\n")]
    public async Task AmbiguousWrappedMacroOrUnbalancedContextNeverProducesAuthoritativeSpan(string prefix, string suffix)
    {
        var r = Parse(await Snapshot(), prefix + "! Undefined control sequence.\nl.1 secret\n" + suffix);
        Assert.Null(One(r, CompilerDiagnosticCode.Error).Location);
        Assert.True(r.Flags.HasFlag(CompilerLogFlags.UncertainContext));
    }
    [Fact]
    public async Task ExternalPackageContextDoesNotFallBackToMainButRestorationCanBeReliable()
    {
        var r = Parse(await Snapshot(), "(main.tex\n(fontenc.sty\n! Unknown encoding.\nl.1 secret\n)\n! Missing $ inserted.\nl.2 secret\n)\n");
        Assert.Null(r.Diagnostics[0].Location); Assert.Equal(CompilerLocationIssue.UnknownFile, r.Diagnostics[0].LocationIssue);
        Assert.NotNull(r.Diagnostics[1].Location); Assert.Equal(2, r.Diagnostics[1].Location!.Line);
    }
    [Theory]
    [InlineData("./main.tex", true)]
    [InlineData("main.tex", true)]
    [InlineData("MAIN.tex", false)]
    [InlineData("/project/main.tex", false)]
    [InlineData("../main.tex", false)]
    [InlineData("a/../main.tex", false)]
    [InlineData("C:/main.tex", false)]
    [InlineData("C:\\main.tex", false)]
    [InlineData("././main.tex", false)]
    [InlineData(" main.tex", false)]
    public async Task FileLineMatchesOnlyExactCapturedPathOrOneExplicitDotSlash(string alleged, bool expected)
    {
        var r = Parse(await Snapshot(), alleged + ":2: LaTeX Error: problem\n");
        Assert.Equal(expected, One(r, CompilerDiagnosticCode.Error).Location is not null);
    }
    [Fact]
    public async Task BasenameIsNeverGuessedAndSpacesUnicodeCaseAndColonAreNotRepaired()
    {
        var snapshot = await Snapshot(("a/main.tex", Bytes("one")), ("b/main.tex", Bytes("two")), ("розділ/has space é.tex", Bytes("А🙂")));
        var r = Parse(snapshot, "main.tex:1: LaTeX Error: secret\nрозділ/has space é.tex:1: LaTeX Error: secret\nрозділ/has space e\u0301.tex:1: LaTeX Error: secret\nрозділ/has space é.tex:bad: LaTeX Error: secret\nC:/a/main.tex:1: LaTeX Error: secret\n");
        Assert.Null(r.Diagnostics[0].Location);
        var d = r.Diagnostics.Single(x => x.Location is not null);
        Assert.Equal("розділ/has space é.tex", d.Location!.RelativePath); Assert.Equal(6, d.Location.EndByte);
        Assert.Equal(CompilerLocationPrecision.Line, d.Location.Precision);
        Assert.Equal(CompilerLocationBasis.ExplicitFileLine, d.Location.Basis);
    }
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    [InlineData("999999999999999999999")]
    [InlineData("3evil")]
    [InlineData("1.2evil")]
    [InlineData("1.")]
    public async Task MalformedOrOverflowLineNumbersDoNotBecomeOffsets(string number)
    {
        var snapshot = await Snapshot();
        var r = Parse(snapshot, "(main.tex\n! Undefined control sequence.\nl." + number + " secret\n)\n");
        var d = One(r, CompilerDiagnosticCode.Error); Assert.Null(d.Location); Assert.Equal(CompilerLocationIssue.InvalidLineNumber, d.LocationIssue);
    }
    [Theory]
    [InlineData("main.tex:999: LaTeX Error: x", CompilerLocationIssue.OutOfRange)]
    [InlineData("main.tex:2147483648: LaTeX Error: x", CompilerLocationIssue.InvalidLineNumber)]
    public async Task FileLineOutOfRangeIsExplicit(string log, CompilerLocationIssue expected)
    {
        var d = One(Parse(await Snapshot(), log), CompilerDiagnosticCode.Error);
        Assert.Null(d.Location); Assert.Equal(expected, d.LocationIssue);
    }
    [Theory]
    [InlineData(1, 3, 9, 0, 3)]
    [InlineData(2, 11, 12, 5, 6)]
    [InlineData(3, 14, 14, 8, 8)]
    [InlineData(4, 15, 15, 9, 9)]
    public async Task ExactBomCrlfLfUkrainianEmojiLineOffsetsHaveIndependentLiteralOracle(int line, long start, long end, int uStart, int uEnd)
    {
        byte[] source = [239, 187, 191, .. Bytes("А🙂\r\nB\r\n\n")];
        var snapshot = await Snapshot(("main.tex", source));
        var r = Parse(snapshot, $"main.tex:{line}: LaTeX Error: x\n");
        var location = Assert.IsType<CompilerLineLocation>(One(r, CompilerDiagnosticCode.Error).Location);
        Assert.Equal(start, location.StartByte); Assert.Equal(end, location.EndByte);
        Assert.Equal(uStart, location.SourceUtf16Start); Assert.Equal(uEnd, location.SourceUtf16End);
        Assert.Equal(CompilationTestWorkspace.Hash(source), location.FileHash);
    }
    [Theory]
    [InlineData("A\rB", 2, 2, 3)]
    [InlineData("A\nB", 2, 2, 3)]
    [InlineData("A\r\nB", 2, 3, 4)]
    [InlineData("", 1, 0, 0)]
    [InlineData("\n", 2, 1, 1)]
    public async Task LoneCrLfEmptySourceAndEmptyLastLineAreExact(string source, int line, long start, long end)
    {
        var d = One(Parse(await Snapshot(("main.tex", Bytes(source))), $"main.tex:{line}: Undefined control sequence.\n"), CompilerDiagnosticCode.Error);
        Assert.Equal(start, d.Location!.StartByte); Assert.Equal(end, d.Location.EndByte);
    }
    [Theory]
    [InlineData("LaTeX Warning: Citation `x' undefined on input line 2.", CompilerDiagnosticCode.Citation)]
    [InlineData("LaTeX Warning: Reference `x' undefined on input line 2.", CompilerDiagnosticCode.Reference)]
    [InlineData("LaTeX Warning: Label(s) changed. Rerun to get cross-references right.", CompilerDiagnosticCode.Rerun)]
    [InlineData("Package hyperref Warning: unusual input on input line 2.", CompilerDiagnosticCode.Warning)]
    public async Task WarningsHaveStableSeverityAndOptionalLinePrecision(string warning, CompilerDiagnosticCode code)
    {
        var r = Parse(await Snapshot(), "(main.tex\n" + warning + "\n)\n");
        var d = One(r, code); Assert.Equal(CompilerDiagnosticSeverity.Warning, d.Severity);
        if (code == CompilerDiagnosticCode.Rerun) Assert.Null(d.Location); else Assert.Equal(2, d.Location!.Line);
    }
    [Fact]
    public async Task B2aProjectionDeduplicatesCodesWithoutChangingOpaqueEnvelopeOrFullOccurrences()
    {
        var r = Parse(await Snapshot(), "main.tex:1: LaTeX Error: secret\nmain.tex:2: LaTeX Error: other\n");
        Assert.Equal(2, r.Diagnostics.Length); Assert.Single(r.EnvelopeEntries); Assert.Equal("TEX005", r.EnvelopeEntries[0].Code);
        Assert.Equal(r.Diagnostics[0].SafeMessage, r.EnvelopeEntries[0].SafeMessage);
        Assert.DoesNotContain("secret", r.EnvelopeEntries[0].SafeMessage);
        var copy = r.Diagnostics.ToArray(); copy[0] = copy[1]; Assert.Equal(1, r.Diagnostics[0].Location!.Line);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public async Task AllBudgetsMustBeExplicitAndPositive(int field)
    {
        var b = field switch
        {
            0 => Budget with { MaxFiles = 0 },
            1 => Budget with { MaxSourceBytes = 0 },
            2 => Budget with { MaxMetadataBytes = 0 },
            3 => Budget with { MaxLogBytes = 0 },
            4 => Budget with { MaxLineBytes = 0 },
            5 => Budget with { MaxLogLines = 0 },
            6 => Budget with { MaxDiagnostics = 0 },
            7 => Budget with { MaxMessageBytes = 0 },
            8 => Budget with { MaxContextDepth = 0 },
            9 => Budget with { MaxSourceLines = 0 },
            10 => Budget with { MaxWorkUnits = 0 },
            _ => Budget with { MaxOutputBytes = 0 }
        };
        Assert.Equal(CompilerLogIssue.InvalidBudget, Assert.IsType<CompilerLogParseResult.Rejected>(CompilerLogParser.Parse(await Snapshot(), [], b, true)).Issue);
    }
    [Fact]
    public async Task NullBudgetSnapshotAndLengthPreflightsAreTyped()
    {
        var s = await Snapshot();
        Assert.Equal(CompilerLogIssue.InvalidSnapshot, Assert.IsType<CompilerLogParseResult.Rejected>(CompilerLogParser.Parse(null, [], Budget, true)).Issue);
        Assert.Equal(CompilerLogIssue.InvalidBudget, Assert.IsType<CompilerLogParseResult.Rejected>(CompilerLogParser.Parse(s, [], null, true)).Issue);
        Assert.Equal(CompilerLogIssue.SourceByteLimit, Assert.IsType<CompilerLogParseResult.Rejected>(CompilerLogParser.Parse(s, [255], Budget with { MaxSourceBytes = 1 }, true)).Issue);
        Assert.Equal(CompilerLogIssue.MetadataLimit, Assert.IsType<CompilerLogParseResult.Rejected>(CompilerLogParser.Parse(s, [255], Budget with { MaxMetadataBytes = 1 }, true)).Issue);
        var many = await Snapshot(("main.tex", Bytes("one")), ("other.tex", Bytes("two")));
        Assert.Equal(CompilerLogIssue.FileCountLimit, Assert.IsType<CompilerLogParseResult.Rejected>(CompilerLogParser.Parse(many, [255], Budget with { MaxFiles = 1 }, true)).Issue);
    }
    [Fact]
    public async Task ByteCapStopsAtScalarBoundaryAndNeverParsesAClippedRecord()
    {
        var s = await Snapshot();
        var r = Parse(s, "🙂\nmain.tex:1: LaTeX Error: secret", Budget with { MaxLogBytes = 2 });
        Assert.True(r.Flags.HasFlag(CompilerLogFlags.LogByteLimit)); Assert.Empty(r.Diagnostics); Assert.Equal(0, r.ConsumedLogBytes);
        var full = "main.tex:1: LaTeX Error: secret\n";
        r = Parse(s, full, Budget with { MaxLogBytes = full.Length - 1 });
        Assert.Empty(r.Diagnostics); Assert.True(r.Flags.HasFlag(CompilerLogFlags.IncompleteDiagnostic));
        var bad = CompilerLogParser.Parse(s, [0xf0, 0x9f], Budget, true);
        Assert.Equal(CompilerLogIssue.InvalidLogUtf8, Assert.IsType<CompilerLogParseResult.Rejected>(bad).Issue);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task EveryIncrementalLimitProducesExplicitPartialStatusNeverClean(int limit)
    {
        var b = limit switch
        {
            0 => Budget with { MaxLineBytes = 3 },
            1 => Budget with { MaxLogLines = 1 },
            2 => Budget with { MaxDiagnostics = 1 },
            3 => Budget with { MaxMessageBytes = 1 },
            4 => Budget with { MaxWorkUnits = 1 },
            5 => Budget with { MaxOutputBytes = 1 },
            _ => Budget with { MaxSourceLines = 1 }
        };
        var r = Parse(await Snapshot(), "main.tex:2: LaTeX Error: x\nmain.tex:3: LaTeX Error: y\n", b);
        Assert.NotEqual(CompilerLogFlags.None, r.Flags); Assert.True(r.WorkUnits <= b.MaxWorkUnits); Assert.True(r.RetainedBytes <= b.MaxOutputBytes);
        Assert.True(r.Diagnostics.Length <= b.MaxDiagnostics);
    }
    [Fact]
    public async Task ContextDepthBoundAndUnknownEmptyUnsupportedLogsAreExplicit()
    {
        var s = await Snapshot(); var r = Parse(s, "(main.tex\n(main.tex\n", Budget with { MaxContextDepth = 1 });
        Assert.True(r.Flags.HasFlag(CompilerLogFlags.ResourceLimit));
        Assert.True(Parse(s, "").Flags.HasFlag(CompilerLogFlags.EmptyLog));
        Assert.True(Parse(s, "random uninterpreted secret").Flags.HasFlag(CompilerLogFlags.UnknownContent));
        Assert.True(Parse(s, "This is XeTeX, unknown engine").Flags.HasFlag(CompilerLogFlags.UnsupportedBanner));
        Assert.True(Parse(s, "This is pdfTeX, known banner", complete: false).Flags.HasFlag(CompilerLogFlags.CallerIncomplete));
        r = Parse(s, "! Unknown compiler error.\n"); Assert.True(r.Flags.HasFlag(CompilerLogFlags.IncompleteDiagnostic)); Assert.Null(r.Diagnostics[0].Location);
    }
    [Theory]
    [InlineData("\u0000")]
    [InlineData("\u001b[31m")]
    [InlineData("\u0085")]
    [InlineData("\u202e")]
    [InlineData("\u200b")]
    public async Task LogControlAndBidiInjectionIsRejectedWithoutEcho(string control)
    {
        var r = CompilerLogParser.Parse(await Snapshot(), Bytes("secret" + control), Budget, true);
        Assert.Equal(CompilerLogIssue.InvalidLogControl, Assert.IsType<CompilerLogParseResult.Rejected>(r).Issue);
    }
    [Fact]
    public async Task InvalidUtf8AndCancellationAreTypedAndDoNotExposeExceptionsOrPartialAuthority()
    {
        var s = await Snapshot();
        Assert.Equal(CompilerLogIssue.InvalidLogUtf8, Assert.IsType<CompilerLogParseResult.Rejected>(CompilerLogParser.Parse(s, [0xff], Budget, true)).Issue);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.IsType<CompilerLogParseResult.Cancelled>(CompilerLogParser.Parse(s, [0xff], Budget, true, cts.Token));
    }
    [Fact]
    public async Task ANewSnapshotCannotRetargetReturnedLocationsOrTheirFreshnessIdentity()
    {
        var a = await Snapshot(("main.tex", Bytes("old\n"))); var b = await Snapshot(("main.tex", Bytes("newer\n")));
        var r = Parse(a, "main.tex:1: LaTeX Error: x\n");
        Assert.NotEqual(b.Identity, r.SnapshotIdentity); Assert.Equal(a.Files[0].Hash, r.Diagnostics[0].Location!.FileHash);
        Assert.Equal(3, r.Diagnostics[0].Location!.EndByte); Assert.Same(a, r.Snapshot);
    }
    [Fact]
    public async Task ForgedMalformedSourceCannotAcquireAuthoritativeSpan()
    {
        // Synthetic corruption defense only: public B1 capture rejects invalid UTF-8
        // text. Reflection deliberately bypasses that authority to test the guard.
        var s = await Snapshot(); var old = s.Files[0];
        var fileCtor = typeof(CompilationInputFile).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        var corrupt = (CompilationInputFile)fileCtor.Invoke([old.FileId, old.RelativePath, "text", false, old.Hash, new byte[] { 65, 255 }]);
        var snapshotCtor = typeof(CompilationInputSnapshot).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        var forged = (CompilationInputSnapshot)snapshotCtor.Invoke([s.Identity, s.Selection, s.Budget, ImmutableArray.Create(corrupt), s.ContentDigest, s.MetadataByteLength]);
        var d = One(Parse(forged, "main.tex:1: LaTeX Error: x\n"), CompilerDiagnosticCode.Error);
        Assert.Null(d.Location); Assert.Equal(CompilerLocationIssue.InvalidSourceUtf8, d.LocationIssue);
    }
    [Theory]
    [InlineData("null-path")]
    [InlineData("invalid-path-unicode")]
    [InlineData("null-media")]
    [InlineData("bad-hash")]
    [InlineData("bad-id")]
    [InlineData("null-identity")]
    [InlineData("empty-capture")]
    [InlineData("default-files")]
    [InlineData("duplicate-file")]
    public async Task SyntheticCorruptMetadataIsTypedNotAnException(string defect)
    {
        var s = await Snapshot(); var old = s.Files[0];
        var fileCtor = typeof(CompilationInputFile).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        var file = (CompilationInputFile)fileCtor.Invoke([
            defect == "bad-id" ? default(FileId) : old.FileId,
            defect == "null-path" ? null : defect == "invalid-path-unicode" ? "\ud800" : old.RelativePath,
            defect == "null-media" ? null : "text", false,
            defect == "bad-hash" ? new SHA256("invalid") : old.Hash, Bytes("x")]);
        var files = defect == "default-files" ? default : defect == "duplicate-file" ? ImmutableArray.Create(file, file) : ImmutableArray.Create(file);
        var identity = defect == "null-identity" ? null : defect == "empty-capture" ? s.Identity with { CaptureId = Guid.Empty } : s.Identity;
        var snapshotCtor = typeof(CompilationInputSnapshot).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        var forged = (CompilationInputSnapshot)snapshotCtor.Invoke([identity, s.Selection, s.Budget, files, s.ContentDigest, s.MetadataByteLength]);
        Assert.Equal(CompilerLogIssue.InvalidSnapshot, Assert.IsType<CompilerLogParseResult.Rejected>(CompilerLogParser.Parse(forged, [], Budget, true)).Issue);
    }
    [Fact]
    public async Task SyntheticBinarySourceIsNeverDecodedIntoALineSpan()
    {
        var s = await Snapshot();
        var fileCtor = typeof(CompilationInputFile).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        var binary = (CompilationInputFile)fileCtor.Invoke([FileId.New(), "asset.bin", "binary", false, s.Files[0].Hash, new byte[] { 255, 254 }]);
        var snapshotCtor = typeof(CompilationInputSnapshot).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        var forged = (CompilationInputSnapshot)snapshotCtor.Invoke([s.Identity, s.Selection, s.Budget, s.Files.Add(binary), s.ContentDigest, s.MetadataByteLength]);
        var d = One(Parse(forged, "asset.bin:1: LaTeX Error: x\n"), CompilerDiagnosticCode.Error);
        Assert.Null(d.Location); Assert.Equal(CompilerLocationIssue.BinarySource, d.LocationIssue);
    }
    [Fact]
    public async Task ConcurrentCancellationReturnsTypedCancelledWithoutPublishingPartialLocations()
    {
        var s = await Snapshot(); using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = Bytes(new string('a', 2_000_000));
        var b = Budget with { MaxLogBytes = log.Length, MaxLineBytes = log.Length, MaxWorkUnits = 100_000_000, MaxOutputBytes = 4_000_000 };
        var task = Task.Run(() => { started.SetResult(); return CompilerLogParser.Parse(s, log, b, true, cts.Token); });
        await started.Task; cts.Cancel();
        Assert.IsType<CompilerLogParseResult.Cancelled>(await task);
    }
}
