using System.Collections.ObjectModel;

namespace Client.Pdf;

// Additive to E1 ABI1. Character indexes are PDFium indexes, never UTF-16 offsets.
public sealed record PdfLayoutRequest(PdfDocument Document, int PageIndex, PdfInspectionBudget Budget)
{
    public bool IsValid => Document is not null && Budget is not null && Budget.IsValid
        && Document.Length <= Budget.InputBytes && PageIndex >= 0 && PageIndex < Budget.Pages;
}

public interface IPdfLayoutInspector
{
    Task<PdfLayoutOutcome> InspectLayoutAsync(PdfLayoutRequest request, CancellationToken cancellationToken = default);
}

public sealed record PdfLayoutCapabilities(int SchemaVersion, string CoordinateSpace, string PhysicalScale,
    string GlyphInk, string TextStyle, string LineGrouping, int MaxCharacters,
    string NominalSizeUnit, string NominalSizeMethod);
public sealed record PdfUserUnit(string Status, double? Value, string Method);
public readonly record struct PdfCharacterMatrix(double A, double B, double C, double D, double E, double F);
public sealed record PdfLayoutCharacter(int Index, int Unicode, PdfPoint? Origin, PdfPoint? Advance,
    PdfCharacterMatrix? Matrix, double? NominalSize, string? FontName, uint? FontFlags, int? RenderMode,
    PdfBox? InkBox, string InkStatus, string StyleStatus);

public sealed class PdfLayoutFacts
{
    internal PdfLayoutFacts(int pageIndex, int pageCount, PdfBox media, PdfBox? crop, PdfBox effective,
        int rotation, PdfUserUnit userUnit, PdfLayoutCharacter[] characters, string[] limitations, string json)
    {
        PageIndex = pageIndex; PageCount = pageCount; Media = media; Crop = crop; Effective = effective;
        Rotation = rotation; UserUnit = userUnit; Characters = Array.AsReadOnly((PdfLayoutCharacter[])characters.Clone());
        Limitations = Array.AsReadOnly((string[])limitations.Clone()); Json = json;
    }
    public int SchemaVersion => 1;
    public int PageIndex { get; }
    public int PageCount { get; }
    public PdfBox Media { get; }
    public PdfBox? Crop { get; }
    public PdfBox Effective { get; }
    public int Rotation { get; }
    public PdfUserUnit UserUnit { get; }
    public ReadOnlyCollection<PdfLayoutCharacter> Characters { get; }
    public bool Complete => true;
    public ReadOnlyCollection<string> Limitations { get; }
    // The exact validated UTF-8 JSON is retained for hashing and Rust evaluation.
    public string Json { get; }
}

public sealed record PdfLayoutOutcome(PdfInspectionStatus Status, string RequestId, string DocumentSha256,
    long WorkerEpoch, PdfRuntimeIdentity? Runtime = null, PdfLayoutCapabilities? Capabilities = null,
    PdfLayoutFacts? Facts = null, string Code = "");
public sealed record PdfLayoutInspection(PdfLayoutCapabilities? Capabilities, PdfLayoutFacts? Facts, string Code);
