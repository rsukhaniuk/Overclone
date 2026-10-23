using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Client.Workspace.Transfer;

namespace Client.Compilation.Diagnostics;

/// <summary>Conservative pdfTeX/LaTeX log subset. Log assertions cannot prove
/// compile success, PDF validity, KNU compliance or current workspace freshness.
/// Only a complete scalar-boundary prefix is inspected under byte limits; an
/// overlong physical line is not parsed partially. Unknowns remain explicit.</summary>
public static class CompilerLogParser
{
    public static CompilerLogParseResult Parse(CompilationInputSnapshot? snapshot, ReadOnlySpan<byte> logUtf8,
        CompilerLogBudget? budget, bool logIsComplete, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return new CompilerLogParseResult.Cancelled();
        if (budget is null || !budget.IsValid) return new CompilerLogParseResult.Rejected(CompilerLogIssue.InvalidBudget);
        if (snapshot is null || snapshot.Identity is not { } identity || snapshot.Files.IsDefault || snapshot.Selection is null
            || identity.CaptureId == Guid.Empty || identity.ServiceId == Guid.Empty || identity.ProjectId.Value == Guid.Empty
            || identity.SessionId == Guid.Empty || identity.WorkingGeneration < 0 || identity.LocalWorkspaceEpoch < 0 || identity.SelectionVersion < 0
            || identity.BaseHeadRevisionId?.Value == Guid.Empty || !CompilationCanonical.ValidHash(snapshot.ContentDigest))
            return new CompilerLogParseResult.Rejected(CompilerLogIssue.InvalidSnapshot);
        if (snapshot.Files.Length > budget.MaxFiles) return new CompilerLogParseResult.Rejected(CompilerLogIssue.FileCountLimit);
        long bytes = 0, metadata = 0;
        // Complete count/length preflight, before path scans, source scans or copies.
        foreach (var file in snapshot.Files)
        {
            if (cancellationToken.IsCancellationRequested) return new CompilerLogParseResult.Cancelled();
            if (file is null || file.RelativePath is null || file.MediaKind is not ("text" or "binary")
                || file.FileId.Value == Guid.Empty || !CompilationCanonical.ValidHash(file.Hash))
                return new CompilerLogParseResult.Rejected(CompilerLogIssue.InvalidSnapshot);
            if (file.ByteLength > budget.MaxSourceBytes - bytes)
                return new CompilerLogParseResult.Rejected(CompilerLogIssue.SourceByteLimit);
            bytes += file.ByteLength;
            if (file.RelativePath.Length > budget.MaxMetadataBytes - metadata - 128)
                return new CompilerLogParseResult.Rejected(CompilerLogIssue.MetadataLimit);
            metadata += file.RelativePath.Length + 128L;
        }
        var parser = new Parser(snapshot, budget, cancellationToken);
        try { return parser.Run(logUtf8, logIsComplete); }
        catch (InvalidLog error) { return new CompilerLogParseResult.Rejected(error.Issue); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new CompilerLogParseResult.Cancelled(); }
    }

    private sealed class InvalidLog(CompilerLogIssue issue) : Exception { internal CompilerLogIssue Issue => issue; }
    private sealed class Limit : Exception;
    private sealed record Candidate(CompilerDiagnosticCode Code, CompilerDiagnosticSeverity Severity,
        int Start, int End, CompilationInputFile? File, int? Line, bool Direct, CompilerLocationIssue Issue);
    private readonly record struct SourceLine(int Start, int End, int Utf16Start, int Utf16End);
    private sealed record SourceMap(List<SourceLine>? Lines, CompilerLocationIssue Issue);

    private sealed class Parser(CompilationInputSnapshot snapshot, CompilerLogBudget budget, CancellationToken ct)
    {
        private readonly Dictionary<string, CompilationInputFile> _files = new(StringComparer.Ordinal);
        private readonly Dictionary<CompilationInputFile, SourceMap> _maps = [];
        private readonly List<CompilationInputFile?> _context = [];
        private readonly List<Candidate> _records = [];
        private Candidate? _pending;
        private CompilerLogFlags _flags;
        private long _work, _retained;
        private int _consumed, _lines, _sourceLines;
        private bool _uncertain;
        // A stopped-fatal stack is NOT balanced. This separate, sticky state
        // machine may retain ONE primary undefined-command line only; report
        // uncertainty/completeness flags and all ordinary context guards remain.
        private enum FatalTail { Prefix, Line, Tail, Footer, Complete, Rejected }
        private FatalTail _fatalTail;
        private long _contextVersion, _fatalVersion;
        private CompilationInputFile? _fatalFile;
        private Candidate? _fatalPending, _fatalCandidate;
        private string? _fatalEcho;
        private bool _fatalComment, _sawFatalStop;
        private const string FatalStop = "!  ==> Fatal error occurred, no output PDF file produced!";
        private const CompilerLogFlags FatalLimits = CompilerLogFlags.LogByteLimit | CompilerLogFlags.LineByteLimit
            | CompilerLogFlags.LineCountLimit | CompilerLogFlags.ResourceLimit | CompilerLogFlags.IncompleteDiagnostic
            | CompilerLogFlags.UnsupportedBanner;

        private void Work(long amount)
        {
            ct.ThrowIfCancellationRequested();
            if (amount > budget.MaxWorkUnits - _work) throw new Limit();
            _work += amount;
        }
        private void Retain(long amount)
        {
            if (amount > budget.MaxOutputBytes - _retained) throw new Limit();
            _retained += amount;
        }
        private int PathBytes(string path)
        {
            Work(path.Length * 16L + 1);
            var span = path.AsSpan(); int bytes = 0;
            while (!span.IsEmpty)
            {
                ct.ThrowIfCancellationRequested();
                if (Rune.DecodeFromUtf16(span, out var rune, out int used) != OperationStatus.Done)
                    throw new InvalidLog(CompilerLogIssue.InvalidSnapshot);
                if (rune.Utf8SequenceLength > budget.MaxMetadataBytes - bytes)
                    throw new InvalidLog(CompilerLogIssue.MetadataLimit);
                bytes += rune.Utf8SequenceLength; span = span[used..];
            }
            return bytes;
        }
        private void Prepare()
        {
            Retain(256); long metadata = 0;
            var ids = new HashSet<Client.Workspace.Domain.FileId>();
            bool main = false;
            foreach (var file in snapshot.Files)
            {
                int length = PathBytes(file.RelativePath);
                if (length + 128L > budget.MaxMetadataBytes - metadata)
                    throw new InvalidLog(CompilerLogIssue.MetadataLimit);
                metadata += length + 128L;
                if (file.RelativePath != ProjectPathValidator.Normalize(file.RelativePath)
                    || ProjectPathValidator.Validate(file.RelativePath) is not null
                    || !_files.TryAdd(file.RelativePath, file) || !ids.Add(file.FileId))
                    throw new InvalidLog(CompilerLogIssue.InvalidSnapshot);
                Retain(length + 128L);
                main |= file.FileId == snapshot.MainFileId && file.MediaKind == "text";
            }
            if (!main) throw new InvalidLog(CompilerLogIssue.InvalidSnapshot);
        }
        internal CompilerLogParseResult Run(ReadOnlySpan<byte> log, bool complete)
        {
            if (!complete) _flags |= CompilerLogFlags.CallerIncomplete;
            if (log.IsEmpty) _flags |= CompilerLogFlags.EmptyLog | CompilerLogFlags.UnknownContent;
            if (log.Length > budget.MaxLogBytes) _flags |= CompilerLogFlags.LogByteLimit;
            try
            {
                Prepare();
                int length = Math.Min(log.Length, budget.MaxLogBytes);
                // Own the bounded prefix after all count/length preflights and
                // before validation; no caller buffer is retained by the parser.
                Work(length); Retain(length);
                byte[] ownedLog = log[..length].ToArray();
                Scan(ownedLog, length < log.Length); FlushPending();
            }
            catch (Limit) { _flags |= CompilerLogFlags.ResourceLimit; }
            if (_pending is not null) _flags |= CompilerLogFlags.IncompleteDiagnostic;
            if (_context.Count != 0 || _uncertain) _flags |= CompilerLogFlags.UncertainContext;
            // A pop/reopen or other invalid fatal tail cannot fall back to a
            // superficially balanced final stack and re-authorize the primary.
            if (_sawFatalStop && _fatalCandidate is not null && !HasFatalEvidence(_fatalCandidate))
                _flags |= CompilerLogFlags.UncertainContext;
            var diagnostics = ImmutableArray.CreateBuilder<CompilerLogDiagnostic>(_records.Count);
            var entries = ImmutableArray.CreateBuilder<CompileDiagnosticEntry>();
            var codes = new HashSet<CompilerDiagnosticCode>();
            // Final record/projection storage was reserved when each record was added.
            foreach (var candidate in _records)
            {
                ct.ThrowIfCancellationRequested();
                var issue = candidate.Issue;
                CompilerLineLocation? location = null;
                if (candidate.Line is not null && issue == CompilerLocationIssue.None)
                {
                    bool narrowFatal = HasFatalEvidence(candidate);
                    if (!candidate.Direct && (_flags & CompilerLogFlags.UncertainContext) != 0 && !narrowFatal)
                        issue = CompilerLocationIssue.UncertainContext;
                    else
                    {
                        try
                        {
                            location = Locate(candidate, out issue, narrowFatal);
                            if (narrowFatal && location is not null && !FatalEchoMatches(candidate.File!, location))
                            { location = null; issue = CompilerLocationIssue.UncertainContext; }
                        }
                        catch (Limit) { location = null; _flags |= CompilerLogFlags.ResourceLimit; issue = CompilerLocationIssue.ResourceLimit; }
                    }
                }
                diagnostics.Add(new(candidate.Code, candidate.Severity, candidate.Start, candidate.End, location, issue));
                if (codes.Add(candidate.Code)) entries.Add(new(CompilerLogMessages.Code(candidate.Code), CompilerLogMessages.Message(candidate.Code)));
            }
            ct.ThrowIfCancellationRequested();
            return new CompilerLogParseResult.Parsed(new(snapshot, _flags, diagnostics.ToImmutable(), entries.ToImmutable(), _consumed, _lines, _work, _retained));
        }
        private void Scan(ReadOnlySpan<byte> log, bool prefixCut)
        {
            int limit = log.Length, start = 0, pos = 0;
            bool stopped = false;
            while (pos < limit)
            {
                Work(1);
                var status = Rune.DecodeFromUtf8(log.Slice(pos, limit - pos), out var rune, out int used);
                if (status != OperationStatus.Done)
                {
                    // A scalar cut only by the caller byte cap is not malformed input.
                    if (status == OperationStatus.NeedMoreData && prefixCut) { stopped = true; break; }
                    throw new InvalidLog(CompilerLogIssue.InvalidLogUtf8);
                }
                var category = Rune.GetUnicodeCategory(rune);
                if ((category is UnicodeCategory.Control or UnicodeCategory.Format && rune.Value is not (9 or 10 or 13))
                    && !(pos == 0 && rune.Value == 0xfeff))
                    throw new InvalidLog(CompilerLogIssue.InvalidLogControl);
                if (pos - start + used > budget.MaxLineBytes && rune.Value is not (10 or 13))
                { _flags |= CompilerLogFlags.LineByteLimit; stopped = true; break; }
                if (rune.Value is 10 or 13)
                {
                    if (_lines >= budget.MaxLogLines) { _flags |= CompilerLogFlags.LineCountLimit; stopped = true; break; }
                    Process(log[start..pos], start, pos, terminated: true); _lines++;
                    pos += used;
                    if (rune.Value == 13 && pos < limit && log[pos] == 10) { Work(1); pos++; }
                    start = pos; _consumed = pos;
                }
                else { pos += used; _consumed = pos; }
            }
            // Never parse a partial physical line at a byte cap, even if its prefix
            // is valid UTF-8. ConsumedLogBytes still records the inspected prefix.
            if (!stopped && !prefixCut && start < pos)
            {
                if (_lines >= budget.MaxLogLines) _flags |= CompilerLogFlags.LineCountLimit;
                else { Process(log[start..pos], start, pos, terminated: false); _lines++; }
            }
            else if (start < pos || stopped)
            {
                _flags |= CompilerLogFlags.IncompleteDiagnostic;
                var partial = log[start..pos];
                Work(partial.Length * 4L + 24); // BOM/prefix tests and possible unknown-record handling
                if (start == 0 && partial.StartsWith("\uFEFF"u8)) partial = partial[3..];
                if (partial.StartsWith("Overfull "u8) || partial.StartsWith("Underfull "u8))
                {
                    FlushPending(); _uncertain = true; Unknown(start, pos);
                }
            }
        }
        private void Add(Candidate candidate)
        {
            if (_records.Count >= budget.MaxDiagnostics) throw new Limit();
            int message = Encoding.UTF8.GetByteCount(CompilerLogMessages.Message(candidate.Code));
            if (message > budget.MaxMessageBytes) throw new Limit();
            // Reserve both full record and deduplicated B2a projection + file path.
            int path = candidate.File is null ? 0 : PathBytes(candidate.File.RelativePath);
            Work(256); Retain(512L + 2L * message + path);
            _records.Add(candidate);
        }
        private void FlushPending()
        {
            if (_pending is not { } pending) return;
            Add(pending with { Issue = pending.Issue == CompilerLocationIssue.None ? CompilerLocationIssue.MissingLine : pending.Issue });
            _pending = null; _flags |= CompilerLogFlags.IncompleteDiagnostic;
        }
        private CompilationInputFile? Match(ReadOnlySpan<char> alleged)
        {
            // Only TeX's single conventional './' prefix is recognized. No trim,
            // basename fallback, case/NFC repair, absolute or traversal repair.
            if (alleged.StartsWith("./", StringComparison.Ordinal)) alleged = alleged[2..];
            return _files.GetValueOrDefault(alleged.ToString());
        }
        private Candidate Record(CompilerDiagnosticCode code, CompilerDiagnosticSeverity severity, int start, int end,
            CompilationInputFile? file, bool direct = false)
            => new(code, severity, start, end, file, null, direct,
                file is null ? (_uncertain ? CompilerLocationIssue.UncertainContext : CompilerLocationIssue.UnknownFile) : CompilerLocationIssue.None);
        private CompilationInputFile? Current => !_uncertain && _context.Count > 0 ? _context[^1] : null;

        private void Process(ReadOnlySpan<byte> bytes, int start, int end, bool terminated)
        {
            Work(bytes.Length * 32L + 1); // bounded constant-number string scans below
            string text = Encoding.UTF8.GetString(bytes); // validation already completed
            var line = text.AsSpan();
            if (start == 0 && line.StartsWith("\ufeff", StringComparison.Ordinal)) line = line[1..];
            ObserveFatalTail(line, terminated);
            if (line.IsWhiteSpace()) return;
            Work(line.Length * 2L + 1); // Additional anchored box-prefix comparisons
            if (BoxRecordStart(line))
            {
                // Complete physical headers only. A later box record cannot be
                // an earlier bang error's echo or supply its missing source line.
                Work(line.Length * 8L + 1); FlushPending();
                if (terminated && TryBoxWarning(line, out var boxCode))
                {
                    var box = Record(boxCode, CompilerDiagnosticSeverity.Warning, start, end, Current);
                    Add(box with { Issue = box.Issue == CompilerLocationIssue.None ? CompilerLocationIssue.MissingLine : box.Issue });
                }
                else
                {
                    if (!terminated) _flags |= CompilerLogFlags.IncompleteDiagnostic;
                    if (line.Contains('(') || line.Contains(')')) _uncertain = true;
                    Unknown(start, end);
                }
                return; // Metric parentheses never enter the filename grammar.
            }
            if (TryFileLine(line, start, end, out var direct)) { FlushPending(); Add(direct!); return; }
            if (line.StartsWith("!", StringComparison.Ordinal))
            {
                FlushPending(); var code = Classify(line);
                var record = Record(code, CompilerDiagnosticSeverity.Error, start, end, Current);
                if (code == CompilerDiagnosticCode.Fatal) Add(record with { Issue = CompilerLocationIssue.MissingLine });
                else _pending = record;
                if (_fatalPending is null && line.SequenceEqual("! Undefined control sequence.")) _fatalPending = record;
                return;
            }
            if (line.StartsWith("l.", StringComparison.Ordinal))
            {
                if (_pending is { } pending)
                {
                    int number = LineNumber(line[2..]);
                    var located = pending with { Line = number > 0 ? number : null, Issue = number > 0 ? pending.Issue : CompilerLocationIssue.InvalidLineNumber };
                    Add(located);
                    if (ReferenceEquals(pending, _fatalPending)) _fatalCandidate = located;
                    _pending = null;
                }
                else Unknown(start, end);
                return;
            }
            if (_pending is not null) return; // no source/error continuation echo
            if (IsWarning(line))
            {
                var code = Classify(line); var record = Record(code, CompilerDiagnosticSeverity.Warning, start, end, Current);
                int marker = line.IndexOf("on input line ", StringComparison.Ordinal);
                int number = marker < 0 ? 0 : LineNumber(line[(marker + 14)..], allowFinalDot: true);
                Add(record with { Line = number > 0 ? number : null, Issue = marker < 0 ? CompilerLocationIssue.MissingLine : number > 0 ? record.Issue : CompilerLocationIssue.InvalidLineNumber });
                return;
            }
            if (line[0] is '(' or ')')
            {
                bool before = _uncertain; Context(line);
                if (_uncertain && !before) Unknown(start, end);
                return;
            }
            if (line.StartsWith("This is ", StringComparison.Ordinal) && !line.StartsWith("This is pdfTeX,", StringComparison.Ordinal))
                _flags |= CompilerLogFlags.UnsupportedBanner;
            if (IsInfo(line)) return;
            // Arbitrary text (including wrapped/macro-produced context) is never
            // considered proof of a clean log or a reliable parenthesis stack.
            if (line.Contains('(') || line.Contains(')')) _uncertain = true;
            Unknown(start, end);
        }
        private string ExpectedJobLogFooter
        {
            get
            {
                var path = snapshot.Files.FirstOrDefault(file => file.FileId == snapshot.MainFileId)?.RelativePath;
                if (path is null || !path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)) return "";
                var leaf = path[(path.LastIndexOf('/') + 1)..];
                return "Transcript written on " + leaf[..^4] + ".log.";
            }
        }

        private bool KnownFatalContext
        {
            get
            {
                Work(_context.Count + 1L);
                return !_uncertain && _context.Count > 0
                    && _context[0] is { } main && main.FileId == snapshot.MainFileId
                    && _context.All(file => file is { MediaKind: "text" });
            }
        }

        private void ObserveFatalTail(ReadOnlySpan<char> line, bool terminated)
        {
            if (line.SequenceEqual(FatalStop)) _sawFatalStop = true;
            if (_fatalTail == FatalTail.Rejected) return;
            // Deliberately limited full physical-record shape, even when the
            // caller honestly marks the overall log incomplete. No suffix-only
            // recovery, footer without LF, restarted banner, or trailing record.
            if (!terminated) { _fatalTail = FatalTail.Rejected; return; }
            if (_lines == 0)
            {
                if (!line.StartsWith("This is pdfTeX,", StringComparison.Ordinal)) _fatalTail = FatalTail.Rejected;
                return;
            }
            if (_lines == 1)
            {
                if (!line.SequenceEqual("entering extended mode")) _fatalTail = FatalTail.Rejected;
                return;
            }
            if (_fatalTail == FatalTail.Prefix)
            {
                if (line.StartsWith("This is ", StringComparison.Ordinal) || line.StartsWith("Transcript written on ", StringComparison.Ordinal))
                { _fatalTail = FatalTail.Rejected; return; }
                if (line.StartsWith("!", StringComparison.Ordinal))
                {
                    if (!line.SequenceEqual("! Undefined control sequence.") || !KnownFatalContext || _pending is not null)
                    { _fatalTail = FatalTail.Rejected; return; }
                    _fatalFile = Current; _fatalVersion = _contextVersion; _fatalTail = FatalTail.Line;
                }
                // Context lines still pass through the ORIGINAL sticky grammar.
                // Parentheses hidden in info/source-like text are not context.
                else if ((line.Contains('(') || line.Contains(')')) && (line.IsEmpty || line[0] is not ('(' or ')')))
                    _fatalTail = FatalTail.Rejected;
                return;
            }
            if (!KnownFatalContext || _contextVersion != _fatalVersion || !ReferenceEquals(Current, _fatalFile))
            { _fatalTail = FatalTail.Rejected; return; }
            if (_fatalTail == FatalTail.Line)
            {
                if (!line.StartsWith("l.", StringComparison.Ordinal) || line.Contains('(') || line.Contains(')')
                    || _pending is null || !ReferenceEquals(_pending, _fatalPending))
                { _fatalTail = FatalTail.Rejected; return; }
                int number = Number(line[2..], out int digits);
                int echo = 2 + digits;
                if (number <= 0 || echo + 1 >= line.Length || line[echo] != ' ')
                { _fatalTail = FatalTail.Rejected; return; }
                // Keep the literal echo, never normalize or use it to infer a
                // file/column. A later exact captured-source comparison can only
                // REFUSE this parser-provided file+line, never retarget it.
                Retain(2L * (line.Length - echo - 1));
                _fatalEcho = line[(echo + 1)..].ToString(); _fatalTail = FatalTail.Tail;
                return;
            }
            if (_fatalTail == FatalTail.Tail)
            {
                if (line.SequenceEqual(FatalStop)) { _fatalTail = FatalTail.Footer; return; }
                // One optional indented comment echo (e.g. TeX ^^ escaped UTF-8)
                // is not scanned as context, nor emitted as a source message.
                if (!_fatalComment && !line.IsEmpty && (line[0] is ' ' or '\t')
                    && line.TrimStart().StartsWith("% ", StringComparison.Ordinal) && !line.Contains('(') && !line.Contains(')'))
                { _fatalComment = true; return; }
            }
            else if (_fatalTail == FatalTail.Footer && line.SequenceEqual(ExpectedJobLogFooter))
            { _fatalTail = FatalTail.Complete; return; }
            _fatalTail = FatalTail.Rejected; // Sticky, including any record after the footer.
        }

        private bool HasFatalEvidence(Candidate candidate) => ReferenceEquals(candidate, _fatalCandidate)
            && _fatalTail == FatalTail.Complete && !_uncertain && _contextVersion == _fatalVersion
            && _context.Count > 0 && ReferenceEquals(Current, _fatalFile) && (_flags & FatalLimits) == 0;

        private bool FatalEchoMatches(CompilationInputFile file, CompilerLineLocation location)
        {
            if (string.IsNullOrEmpty(_fatalEcho)) return false;
            Work(_fatalEcho.Length * 4L + location.EndByte - location.StartByte);
            Retain(Encoding.UTF8.GetByteCount(_fatalEcho));
            var echo = Encoding.UTF8.GetBytes(_fatalEcho);
            var source = file.ByteSpan[(int)location.StartByte..(int)location.EndByte];
            if (!source.StartsWith(echo)) return false;
            var remaining = source[echo.Length..];
            // Accept a full exact line echo, or its literal command prefix
            // followed only by whitespace/comment. No partial command matching.
            while (!remaining.IsEmpty && remaining[0] is (byte)' ' or (byte)'\t') remaining = remaining[1..];
            return remaining.IsEmpty || remaining[0] == (byte)'%';
        }

        private void Unknown(int start, int end)
        { _flags |= CompilerLogFlags.UnknownContent; Add(Record(CompilerDiagnosticCode.Unknown, CompilerDiagnosticSeverity.Unknown, start, end, null)); }
        private static bool BoxRecordStart(ReadOnlySpan<char> line)
            => line.StartsWith("Overfull ", StringComparison.Ordinal) || line.StartsWith("Underfull ", StringComparison.Ordinal);

        // SYNTACTIC TeX layout subset, never a physical-PDF measurement or a
        // source range. Process charges an additional8N work before these scans.
        private static bool TryBoxWarning(ReadOnlySpan<char> text, out CompilerDiagnosticCode code)
        {
            code = CompilerDiagnosticCode.Unknown;
            bool overfull = TakeBox(ref text, "Overfull ");
            if (!overfull && !TakeBox(ref text, "Underfull ")) return false;
            bool horizontal = TakeBox(ref text, @"\hbox (");
            if (!horizontal && !TakeBox(ref text, @"\vbox (")) return false;
            if (overfull)
            {
                // print_scaled emits unsigned point magnitude with a decimal
                // fraction (at most5 digits); no sign/exponent/other unit accepted.
                if (!BoxUnsigned(ref text, 32767, 5, positive: false) || !TakeBox(ref text, ".")) return false;
                int fraction = 0;
                while (fraction < text.Length && char.IsAsciiDigit(text[fraction]))
                { if (++fraction > 5) return false; }
                if (fraction == 0) return false;
                text = text[fraction..];
                if (!TakeBox(ref text, horizontal ? "pt too wide)" : "pt too high)")) return false;
            }
            else if (!TakeBox(ref text, "badness ") || !BoxUnsigned(ref text, 10000, 5, positive: false)
                || !TakeBox(ref text, ")")) return false;
            if (!text.IsEmpty)
            {
                if (TakeBox(ref text, " in paragraph at lines "))
                {
                    if (!horizontal || !BoxRange(ref text)) return false;
                }
                else if (TakeBox(ref text, " in alignment at lines "))
                { if (!BoxRange(ref text)) return false; }
                else if (TakeBox(ref text, " detected at line "))
                { if (!BoxUnsigned(ref text, int.MaxValue, 10, positive: true)) return false; }
                else if (TakeBox(ref text, @" has occurred while \output is active"))
                {
                    // vpack does not print_ln before suppressed show_box output;
                    // ONLY its output-active branch may share this exact tail.
                    if (!horizontal && text.SequenceEqual(" []")) text = text[3..];
                }
                else return false;
            }
            if (!text.IsEmpty) return false; // No detail/source echo/suffix recovery.
            code = (overfull, horizontal) switch
            {
                (true, true) => CompilerDiagnosticCode.OverfullHBox,
                (false, true) => CompilerDiagnosticCode.UnderfullHBox,
                (true, false) => CompilerDiagnosticCode.OverfullVBox,
                _ => CompilerDiagnosticCode.UnderfullVBox
            };
            return true;
        }
        private static bool TakeBox(ref ReadOnlySpan<char> text, ReadOnlySpan<char> literal)
        {
            if (!text.StartsWith(literal, StringComparison.Ordinal)) return false;
            text = text[literal.Length..]; return true;
        }
        private static bool BoxRange(ref ReadOnlySpan<char> text)
            => BoxUnsigned(ref text, int.MaxValue, 10, positive: true) && TakeBox(ref text, "--")
                && BoxUnsigned(ref text, int.MaxValue, 10, positive: true);
        private static bool BoxUnsigned(ref ReadOnlySpan<char> text, int maximum, int maxDigits, bool positive)
        {
            int digits = 0, value = 0;
            while (digits < text.Length && char.IsAsciiDigit(text[digits]))
            {
                int d = text[digits] - '0';
                if (digits >= maxDigits || value > (maximum - d) / 10) return false;
                value = value * 10 + d; digits++;
            }
            if (digits == 0 || value > maximum || (positive && value == 0) || (digits > 1 && text[0] == '0')) return false;
            text = text[digits..]; return true;
        }

        private static bool IsWarning(ReadOnlySpan<char> line)
            => line.StartsWith("LaTeX Warning:", StringComparison.Ordinal)
                || (line.StartsWith("Package ", StringComparison.Ordinal) && line.Contains(" Warning:", StringComparison.Ordinal));
        private static bool IsInfo(ReadOnlySpan<char> line)
            => line.StartsWith("This is pdfTeX,", StringComparison.Ordinal) || line.SequenceEqual("entering extended mode")
                || line.StartsWith("LaTeX2e <", StringComparison.Ordinal) || line.StartsWith("Document Class:", StringComparison.Ordinal)
                || line.StartsWith("Output written on ", StringComparison.Ordinal) || line.StartsWith("Transcript written on ", StringComparison.Ordinal)
                || line.StartsWith("No file ", StringComparison.Ordinal) || line.StartsWith("LaTeX Font Info:", StringComparison.Ordinal)
                || line.StartsWith("Package: ", StringComparison.Ordinal) || line.StartsWith("File: ", StringComparison.Ordinal);
        private static CompilerDiagnosticCode Classify(ReadOnlySpan<char> line)
        {
            if (line.Contains("Fatal error", StringComparison.Ordinal) || line.Contains("Emergency stop", StringComparison.Ordinal)) return CompilerDiagnosticCode.Fatal;
            if (line.Contains("Encoding file", StringComparison.Ordinal)) return CompilerDiagnosticCode.Encoding;
            if (line.Contains("Metric (TFM)", StringComparison.Ordinal)) return CompilerDiagnosticCode.Metric;
            if (line.Contains("Package inputenc Error:", StringComparison.Ordinal) && line.Contains("Unicode char", StringComparison.Ordinal)) return CompilerDiagnosticCode.Unicode;
            if (line.Contains("File `", StringComparison.Ordinal) && line.Contains("not found", StringComparison.Ordinal)) return CompilerDiagnosticCode.MissingFile;
            if (line.Contains("Reference `", StringComparison.Ordinal) || line.Contains("undefined references", StringComparison.Ordinal)) return CompilerDiagnosticCode.Reference;
            if (line.Contains("Citation `", StringComparison.Ordinal) || line.Contains("undefined citations", StringComparison.Ordinal)) return CompilerDiagnosticCode.Citation;
            if (line.Contains("Rerun", StringComparison.Ordinal)) return CompilerDiagnosticCode.Rerun;
            return IsWarning(line) ? CompilerDiagnosticCode.Warning : CompilerDiagnosticCode.Error;
        }
        private bool TryFileLine(ReadOnlySpan<char> line, int start, int end, out Candidate? record)
        {
            record = null;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] != ':' || i + 1 >= line.Length || !char.IsAsciiDigit(line[i + 1])) continue;
                int number = Number(line[(i + 1)..], out int digits);
                int after = i + 1 + digits;
                if (after >= line.Length || line[after] != ':') continue;
                var message = line[(after + 1)..].TrimStart();
                // This layout subset is physical-header-only, not file:line
                // errors or paragraph-derived source-navigation authority.
                Work(message.Length * 2L + 1);
                if (BoxRecordStart(message)) return false;
                bool warning = IsWarning(message);
                bool error = message.StartsWith("LaTeX Error:", StringComparison.Ordinal) || message.StartsWith("!", StringComparison.Ordinal)
                    || message.StartsWith("Undefined control sequence", StringComparison.Ordinal)
                    || message.StartsWith("Missing $ inserted", StringComparison.Ordinal)
                    || (message.StartsWith("Package ", StringComparison.Ordinal) && message.Contains(" Error:", StringComparison.Ordinal));
                if (!warning && !error) return false;
                var file = Match(line[..i]);
                record = Record(Classify(message), warning ? CompilerDiagnosticSeverity.Warning : CompilerDiagnosticSeverity.Error, start, end, file, true)
                    with
                { Line = number > 0 ? number : null, Issue = number > 0 ? file is null ? CompilerLocationIssue.UnknownFile : CompilerLocationIssue.None : CompilerLocationIssue.InvalidLineNumber };
                return true;
            }
            return false;
        }
        private static int LineNumber(ReadOnlySpan<char> text, bool allowFinalDot = false)
        {
            int number = Number(text, out int digits);
            if (digits == text.Length || (digits < text.Length && char.IsWhiteSpace(text[digits]))) return number;
            return allowFinalDot && text[digits] == '.' && text[(digits + 1)..].IsWhiteSpace() ? number : -1;
        }
        private static int Number(ReadOnlySpan<char> text, out int digits)
        {
            digits = 0; int number = 0; bool overflow = false;
            while (digits < text.Length && char.IsAsciiDigit(text[digits]))
            {
                int d = text[digits++] - '0';
                if (number > (int.MaxValue - d) / 10) overflow = true;
                if (!overflow) number = number * 10 + d;
            }
            return digits == 0 || overflow || number == 0 ? -1 : number;
        }
        private void Context(ReadOnlySpan<char> line)
        {
            _contextVersion++; // Includes pop/reopen A-B-A, not just final top file.
            // Deliberately narrow grammar: whole line consists only of '(' +
            // unquoted non-whitespace filename tokens, ')' and whitespace. Anything
            // else poisons ALL inferred context locations; explicit file:line survives.
            int pos = 0;
            while (pos < line.Length)
            {
                if (char.IsWhiteSpace(line[pos])) { pos++; continue; }
                if (line[pos] == ')')
                {
                    if (_context.Count == 0) { _uncertain = true; return; }
                    _context.RemoveAt(_context.Count - 1); pos++; continue;
                }
                if (line[pos++] != '(') { _uncertain = true; return; }
                int start = pos;
                while (pos < line.Length && !char.IsWhiteSpace(line[pos]) && line[pos] is not ('(' or ')')) pos++;
                if (start == pos) { _uncertain = true; return; }
                var token = line[start..pos]; var file = Match(token);
                bool external = token.EndsWith(".cls", StringComparison.Ordinal) || token.EndsWith(".clo", StringComparison.Ordinal)
                    || token.EndsWith(".sty", StringComparison.Ordinal) || token.EndsWith(".def", StringComparison.Ordinal)
                    || token.EndsWith(".dfu", StringComparison.Ordinal) || token.EndsWith(".fd", StringComparison.Ordinal)
                    || token.EndsWith(".cfg", StringComparison.Ordinal) || token.EndsWith(".aux", StringComparison.Ordinal);
                if (file is null && !external) { _uncertain = true; return; }
                if (_context.Count >= budget.MaxContextDepth) throw new Limit();
                Retain(16); _context.Add(file);
            }
        }
        private CompilerLineLocation? Locate(Candidate candidate, out CompilerLocationIssue issue, bool narrowFatal = false)
        {
            issue = CompilerLocationIssue.None;
            if (candidate.File is not { } file) { issue = CompilerLocationIssue.UnknownFile; return null; }
            if (!_maps.TryGetValue(file, out var map))
            {
                try { map = Index(file); }
                catch (Limit)
                {
                    _maps.Add(file, new(null, CompilerLocationIssue.ResourceLimit));
                    throw;
                }
                _maps.Add(file, map);
            }
            if (map.Issue != CompilerLocationIssue.None) { issue = map.Issue; return null; }
            int line = candidate.Line!.Value;
            if (line > map.Lines!.Count) { issue = CompilerLocationIssue.OutOfRange; return null; }
            var span = map.Lines[line - 1];
            return new(file, line, span.Start, span.End, span.Utf16Start, span.Utf16End,
                candidate.Direct ? CompilerLocationBasis.ExplicitFileLine : narrowFatal
                    ? CompilerLocationBasis.NarrowFatalContext : CompilerLocationBasis.BalancedNarrowContext);
        }
        private SourceMap Index(CompilationInputFile file)
        {
            if (file.MediaKind != "text") return new(null, CompilerLocationIssue.BinarySource);
            var bytes = file.ByteSpan;
            bool bom = bytes.StartsWith(new byte[] { 239, 187, 191 });
            if (file.HasBom != bom) return new(null, CompilerLocationIssue.InvalidSourceUtf8);
            int pos = bom ? 3 : 0, start = pos, utf16 = 0, utf16Start = 0;
            var lines = new List<SourceLine>();
            void AddLine(int end, int utf16End)
            {
                if (_sourceLines >= budget.MaxSourceLines) throw new Limit();
                _sourceLines++; Retain(24); lines.Add(new(start, end, utf16Start, utf16End));
            }
            while (pos < bytes.Length)
            {
                Work(1);
                if (Rune.DecodeFromUtf8(bytes[pos..], out var rune, out int used) != OperationStatus.Done)
                    return new(null, CompilerLocationIssue.InvalidSourceUtf8);
                if (rune.Value is 10 or 13)
                {
                    AddLine(pos, utf16); pos += used; utf16++;
                    if (rune.Value == 13 && pos < bytes.Length && bytes[pos] == 10) { Work(1); pos++; utf16++; }
                    start = pos; utf16Start = utf16;
                }
                else { pos += used; utf16 += rune.Utf16SequenceLength; }
            }
            AddLine(pos, utf16); // empty file and empty final line are real lines
            return new(lines, CompilerLocationIssue.None);
        }
    }
}
