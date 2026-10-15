using System.Buffers;
using System.Text;
using Client.Compilation.Diagnostics;

namespace Client.Compilation.Presentation;

/// <summary>Explicit caller conversion limits, separate from D1's parser policy.
/// Units bound two UTF-16 passes (measure then encode). Conversion is all-or-nothing:
/// no truncated line, surrogate, BOM or CRLF is repaired. These are not device caps.</summary>
public sealed record CompileAttemptPresentationBudget(int MaxConversionBytes, long MaxConversionUnits, CompilerLogBudget ParserBudget)
{
    public bool IsValid => MaxConversionBytes > 0 && MaxConversionBytes <= Array.MaxLength
        && MaxConversionUnits > 0 && ParserBudget is { IsValid: true };
}

public enum CompileAttemptPresentationIssue { InvalidBudget, MissingAttempt, ConversionByteLimit, ConversionWorkLimit, InvalidText }

/// <summary>Only the presenter constructs this binding. D1's full result retains
/// severity, occurrences, uncertainty, flags, exact LINE spans and the original
/// snapshot. No lossy EnvelopeEntries projection, fixes or PDF claims. A Parsed
/// result is not a clean log or successful engine. Freshness is read separately
/// from lifecycle State.IsAttemptCurrent; historical presentations never retarget.</summary>
public sealed class CompileAttemptPresentation
{
    internal CompileAttemptPresentation(ValidatedCompileAttempt? attempt, CompilerLogParseResult? parserResult,
        CompileAttemptPresentationIssue? issue, bool cancelled, int convertedBytes)
    { Attempt = attempt; ParserResult = parserResult; Issue = issue; IsCancelled = cancelled; ConvertedBytes = convertedBytes; }
    public ValidatedCompileAttempt? Attempt { get; }
    public CompilerLogParseResult? ParserResult { get; }
    public CompileAttemptPresentationIssue? Issue { get; }
    public bool IsCancelled { get; }
    public int ConvertedBytes { get; }
}

public static class CompileAttemptPresenter
{
    /// <summary>Uses ONLY real D1. No full-log split/regex, source copies, implicit
    /// completeness or replacement decoding. Checks cancellation before allocation,
    /// during both conversion passes and before returning parser output. An oversized
    /// log returns an explicit unavailable result with zero conversion allocation.
    /// Unscanned data is never declared valid. D1 may further limit interpretation.</summary>
    public static CompileAttemptPresentation Present(ValidatedCompileAttempt? attempt,
        CompileAttemptPresentationBudget? budget, CancellationToken cancellationToken = default)
    {
        CompileAttemptPresentation Unavailable(CompileAttemptPresentationIssue issue) => new(attempt, null, issue, false, 0);
        CompileAttemptPresentation Cancelled(int bytes = 0) => new(attempt, null, null, true, bytes);
        if (cancellationToken.IsCancellationRequested) return Cancelled();
        if (budget is null || !budget.IsValid) return Unavailable(CompileAttemptPresentationIssue.InvalidBudget);
        if (attempt is null) return Unavailable(CompileAttemptPresentationIssue.MissingAttempt);
        var log = attempt.LogText.AsSpan();
        // UTF-16 char count is a lower bound on strict UTF-8 length. A tiny parser
        // byte/work/retained budget therefore cannot cause a whole-large-log copy.
        long cap = Math.Min(budget.MaxConversionBytes, Math.Min(budget.ParserBudget.MaxLogBytes,
            Math.Min(budget.ParserBudget.MaxWorkUnits, budget.ParserBudget.MaxOutputBytes)));
        if (log.Length > cap) return Unavailable(CompileAttemptPresentationIssue.ConversionByteLimit);
        if (2L * log.Length > budget.MaxConversionUnits) return Unavailable(CompileAttemptPresentationIssue.ConversionWorkLimit);
        int length = 0;
        for (int pos = 0; pos < log.Length;)
        {
            if (cancellationToken.IsCancellationRequested) return Cancelled();
            if (Rune.DecodeFromUtf16(log[pos..], out var rune, out int used) != OperationStatus.Done)
                return Unavailable(CompileAttemptPresentationIssue.InvalidText);
            if (rune.Utf8SequenceLength > cap - length) return Unavailable(CompileAttemptPresentationIssue.ConversionByteLimit);
            length += rune.Utf8SequenceLength; pos += used;
        }
        if (cancellationToken.IsCancellationRequested) return Cancelled();
        var utf8 = new byte[length];
        for (int pos = 0, dest = 0; pos < log.Length;)
        {
            if (cancellationToken.IsCancellationRequested) return Cancelled(length);
            // Already measured strict immutable text; no replacement fallback.
            Rune.DecodeFromUtf16(log[pos..], out var rune, out int used);
            dest += rune.EncodeToUtf8(utf8.AsSpan(dest)); pos += used;
        }
        if (cancellationToken.IsCancellationRequested) return Cancelled(length);
        var parsed = CompilerLogParser.Parse(attempt.Snapshot, utf8, budget.ParserBudget,
            logIsComplete: false, cancellationToken: cancellationToken);
        if (cancellationToken.IsCancellationRequested || parsed is CompilerLogParseResult.Cancelled) return Cancelled(length);
        return new(attempt, parsed, null, false, length);
    }
}
