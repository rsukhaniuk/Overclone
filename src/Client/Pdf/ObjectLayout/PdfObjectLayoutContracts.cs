using System.Collections.ObjectModel;

namespace Client.Pdf;

/// <summary>Explicit direct-image inspection. Optional text layout comes from the same native page handle.</summary>
public sealed record PdfObjectLayoutRequest(PdfDocument Document, int PageIndex, PdfInspectionBudget Budget,
    bool IncludeTextLayout = false)
{
    public bool IsValid => Document is not null && Budget is not null && Budget.IsValid
        && Document.Length <= Budget.InputBytes && PageIndex >= 0 && PageIndex < Budget.Pages;

    internal PdfInspectRequest? ToInspectRequest() => IsValid
        ? new PdfInspectRequest(Document, PageIndex, Budget, raster: false,
            rasterWidth: 1, rasterHeight: 1, inspectLayout: IncludeTextLayout) { InspectObjectLayout = true }
        : null;
}

public interface IPdfObjectLayoutInspector
{
    Task<PdfObjectLayoutOutcome> InspectObjectLayoutAsync(PdfObjectLayoutRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record PdfObjectLayoutOutcome(PdfInspectionStatus Status, string RequestId, string DocumentSha256,
    long WorkerEpoch, int PageIndex, PdfRuntimeIdentity? Runtime, PdfObjectLayoutFacts? Facts,
    PdfLayoutCapabilities? LayoutCapabilities, PdfLayoutFacts? LayoutFacts, string? LayoutFactsSha256, string Code)
{
    internal static PdfObjectLayoutOutcome FromInspection(PdfInspectionOutcome result, int pageIndex)
        => new(result.Status, result.RequestId, result.DocumentSha256, result.WorkerEpoch, pageIndex, result.Runtime,
            result.ObjectLayout?.Facts, result.Layout?.Capabilities, result.Layout?.Facts,
            result.ObjectLayout?.LayoutFactsSha256, result.Code);
}

internal sealed record PdfObjectLayoutInspection(PdfObjectLayoutFacts Facts, string? LayoutFactsSha256);

public readonly record struct PdfImageMatrix(double A, double B, double C, double D, double E, double F);
public readonly record struct PdfImageQuad(PdfPoint P00, PdfPoint P10, PdfPoint P11, PdfPoint P01);
public sealed record PdfImagePlacementUncertainty(double AbsoluteErrorRaw, string Method);
public sealed record PdfDirectObjectCounts(int Text, int Path, int Image, int Shading, int Form, int Unknown);
public sealed record PdfImagePlacement(int ObjectIndex, bool Active, string Status, string Reason,
    PdfImageMatrix? Matrix, PdfImageQuad? Quad, PdfBox? RawBox, PdfImagePlacementUncertainty? Uncertainty)
{
    public const string Method = "native-image-unit-square-placement";
    public const string Limitation = "source-ctm-accumulation-error-unmeasured";
}

/// <summary>Immutable geometry of parsed top-level PDFium image placements. Ordinals are local to this exact capture.</summary>
public sealed class PdfObjectLayoutFacts
{
    internal PdfObjectLayoutFacts(int pageIndex, int pageCount, PdfBox media, PdfBox? crop, PdfBox effective,
        int rotation, PdfUserUnit userUnit, int directObjectCount, PdfDirectObjectCounts objectTypeCounts,
        PdfImagePlacement[] images, string[] limitations, string json, string sha256)
    {
        PageIndex = pageIndex;
        PageCount = pageCount;
        Media = media;
        Crop = crop;
        Effective = effective;
        Rotation = rotation;
        UserUnit = userUnit;
        DirectObjectCount = directObjectCount;
        ObjectTypeCounts = objectTypeCounts;
        Images = Array.AsReadOnly((PdfImagePlacement[])images.Clone());
        Limitations = Array.AsReadOnly((string[])limitations.Clone());
        Json = json;
        Sha256 = sha256;
    }

    public int SchemaVersion => 1;
    public string CoordinateSpace => "pdf-raw-unrotated-bottom-left";
    public string Scope => "direct-page-image-placement";
    public int PageIndex { get; }
    public int PageCount { get; }
    public PdfBox Media { get; }
    public PdfBox? Crop { get; }
    public PdfBox Effective { get; }
    public int Rotation { get; }
    public PdfUserUnit UserUnit { get; }
    public int DirectObjectCount { get; }
    public PdfDirectObjectCounts ObjectTypeCounts { get; }
    public ReadOnlyCollection<PdfImagePlacement> Images { get; }
    public bool ScanComplete => true;
    public ReadOnlyCollection<string> Limitations { get; }
    public string Json { get; }
    public string Sha256 { get; }
}
