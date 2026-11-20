using System.Globalization;
using System.Text;
using Client.Analysis.RenderedExtent;
using Client.Pdf;

namespace Client.Analysis.RenderedReview;

public enum RenderedReferencePairRole { Citation, Entry }
public enum RenderedReferenceComparison { NumbersMatch, NumbersDiffer, Indeterminate }

public sealed record RenderedReferenceSelection(string SelectionRequestId, long SelectionEpoch, int PageIndex,
    int CharacterStart, int CharacterEnd, string Literal)
{
    public const int MaxCharacters = 32;
    internal bool IsValid => RenderedExtentContract.RequestId(SelectionRequestId) && RenderedExtentContract.Epoch(SelectionEpoch)
        && PageIndex is >= 0 and < 512 && CharacterStart >= 0 && CharacterEnd > CharacterStart && CharacterEnd <= 100000
        && CharacterEnd - CharacterStart is > 0 and <= MaxCharacters && Literal is { Length: > 0 and <= MaxCharacters * 2 }
        && PdfHashes.ValidUtf16(Literal) && Literal.EnumerateRunes().Count() == CharacterEnd - CharacterStart
        && !Literal.EnumerateRunes().Any(Rune.IsControl);

    internal bool Matches<T>(IReadOnlyList<T> glyphs, Func<T, int> index, Func<T, int> scalar)
    {
        if (CharacterEnd > glyphs.Count) return false;
        int i = CharacterStart;
        foreach (var rune in Literal.EnumerateRunes())
        {
            var glyph = glyphs[i];
            if (index(glyph) != i || scalar(glyph) != rune.Value) return false;
            i++;
        }
        return i == CharacterEnd;
    }

    internal int? PrintedNumber()
    {
        if (Literal.Length is < 3 or > 11 || Literal[0] != '[' || Literal[^1] != ']' || Literal[1] == '0')
            return null;
        var digits = Literal.AsSpan(1, Literal.Length - 2);
        foreach (char digit in digits)
            if (!char.IsAsciiDigit(digit)) return null;
        return int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
    }
}

public sealed record RenderedReferencePair(PdfRuntimeIdentity Runtime, RenderedReferenceSelection Citation,
    RenderedReferenceSelection Entry)
{
    internal bool IsValid => Runtime is not null && PdfHashes.IsSha(Runtime.LoaderSha256) && PdfHashes.IsSha(Runtime.WasmSha256)
        && Citation is { IsValid: true } && Entry is { IsValid: true };
    internal int[] Pages => new[] { Citation.PageIndex, Entry.PageIndex }.Distinct().Order().ToArray();
    internal bool Overlapping => Citation.PageIndex == Entry.PageIndex
        && Citation.CharacterStart < Entry.CharacterEnd && Entry.CharacterStart < Citation.CharacterEnd;
}

public sealed record RenderedReferencePairRequest(RenderedReviewRequest Review, RenderedReferencePair Pair,
    ReadOnlyMemory<byte> FactsUtf8)
{
    internal bool IsValid => Review is { IsValid: true, AnnotationVersion: 0, Annotations.Count: 0 }
        && Pair is { IsValid: true } && Review.RequestedPages.SequenceEqual(Pair.Pages)
        && FactsUtf8.Length is > 0 and <= RenderedReviewContract.FactsMaxBytes;
}
public sealed record RenderedReferencePairObservation(RenderedReferencePairRequest Request,
    RenderedReferenceComparison Comparison, string Reason, int? CitationNumber, int? EntryNumber);
public sealed record RenderedReferencePairReply(RenderedExtentEvaluationStatus Status, string RequestId, long WorkerEpoch,
    RenderedReferencePairObservation? Observation = null);

public interface IRenderedReferencePairEvaluator
{
    Task<RenderedReferencePairReply> EvaluateReferencePairAsync(RenderedReferencePairRequest request,
        CancellationToken cancellationToken = default);
}
