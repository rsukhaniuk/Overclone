using System.Collections.Immutable;
using Client.Workspace.Domain;

namespace Client.Compilation.SyncTeX;

/// <summary>Opaque copied geometry bytes, not native execution or live authority.</summary>
public sealed record SourceMapGeometryIdentity(SHA256 Hash, long ByteLength);
public readonly record struct SourceMapWriterBox(double Left, double Bottom, double Right, double Top);
/// <summary>x=A*h+C*v+E; y=B*h+D*v+F. Values only, no array/ROM alias.</summary>
public readonly record struct SourceMapMatrix(double A,double B,double C,double D,double E,double F)
{
    public bool TryInverse(out SourceMapMatrix inverse)
    {
        double determinant=A*D-B*C;
        inverse=default;
        if(!double.IsFinite(determinant)||determinant==0)return false;
        inverse=new(D/determinant,-B/determinant,-C/determinant,A/determinant,
            (C*F-D*E)/determinant,(B*E-A*F)/determinant);
        return double.IsFinite(inverse.A)&&double.IsFinite(inverse.B)&&double.IsFinite(inverse.C)
            &&double.IsFinite(inverse.D)&&double.IsFinite(inverse.E)&&double.IsFinite(inverse.F);
    }
}
public sealed class SourceMapPageTransform
{
    internal SourceMapPageTransform(int page,string status,string reason,SourceMapMatrix? matrix,SourceMapWriterBox? box)
    {Page=page;Status=status;Reason=reason;WriterToPdfRaw=matrix;WriterPageBoxBp=box;}
    public int Page {get;}
    public string Status {get;}
    public string Reason {get;}
    public SourceMapMatrix? WriterToPdfRaw {get;}
    public SourceMapWriterBox? WriterPageBoxBp {get;}
}
public sealed class SourceMapGeometrySidecar
{
    internal SourceMapGeometrySidecar(ImmutableList<SourceMapPageTransform> pages)=>Pages=pages;
    public int SchemaVersion=>1;
    public ImmutableList<SourceMapPageTransform> Pages {get;}
}
public sealed class SourceMapGeometry
{
    internal SourceMapGeometry(string engine,uint pass,string kind,string? intermediate,string pdf,string map,ImmutableList<SourceMapPageTransform> pages)
    {EngineId=engine;FinalPass=pass;IntermediateKind=kind;IntermediateSha256=intermediate;PdfSha256=pdf;MapSha256=map;Pages=pages;}
    public int SchemaVersion=>1;
    public string EngineId {get;}
    public uint FinalPass {get;}
    public string IntermediateKind {get;}
    public string? IntermediateSha256 {get;}
    public string PdfSha256 {get;}
    public string MapSha256 {get;}
    public ImmutableList<SourceMapPageTransform> Pages {get;}
}
/// <summary>Privately owned parsed wrapper, never a query/current-output ticket.</summary>
public sealed class SourceMapGeometryArtifact
{
    private readonly byte[] _bytes;
    internal SourceMapGeometryArtifact(byte[] owned,SourceMapGeometry geometry)
    {_bytes=owned;Geometry=geometry;Identity=new(SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(owned)),owned.LongLength);}
    public SourceMapGeometry Geometry {get;}
    public SourceMapGeometryIdentity Identity {get;}
    public byte[] ToArray()=>(byte[])_bytes.Clone();
    internal ReadOnlySpan<byte> BytesSpan=>_bytes;
}
public enum SourceMapGeometryError { InvalidInput, OverBudget, InvalidUtf8, InvalidShape, InvalidTransform, CorrelationMismatch }
public sealed record SourceMapGeometryReadResult(SourceMapGeometryArtifact? Artifact,SourceMapGeometryError? Error);
public sealed record SourceMapGeometrySidecarResult(SourceMapGeometrySidecar? Sidecar,SourceMapGeometryError? Error);
