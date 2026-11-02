using System.Text;
using Client.Compilation;
using Client.Compilation.SyncTeX;
using Xunit;
using Hash=Client.Workspace.Domain.SHA256;

namespace Workspace.Tests.SourceMapGeometry;

internal static class GeometryFixture
{
    internal static readonly CompileOutputBudget Budget=new(8*1024*1024,65536,16,64,1024,4096);
    internal static readonly CompileOutputDescriptor Format=new("pdf","geometry-contract-v1",null),Adapter=new("geometry-contract-fixture","v1",null);
    internal static byte[] Bytes(string s)=>Encoding.UTF8.GetBytes(s);
    internal static Hash Digest(byte[] b)=>Hash.FromBytes(System.Security.Cryptography.SHA256.HashData(b));
    internal static byte[] Native(string engine,string file)
    {
        using var stream=typeof(GeometryFixture).Assembly.GetManifestResourceStream($"Workspace.Tests.Fixtures.SourceMapGeometry.native.{engine.Replace('-', '_')}.{file}")
            ??throw new InvalidOperationException("Required copied native geometry fixture missing.");
        using var output=new MemoryStream();stream.CopyTo(output);return output.ToArray();
    }
    internal static SourceMapGeometryArtifact Assemble(string engine="lualatex",uint pass=3)
    {
        var result=SourceMapGeometryCodec.AssembleData(engine,pass,Native(engine,"geometry.json"),Native(engine,"source.synctex"),Native(engine,"output.pdf"),engine=="latex-dvi"?Native(engine,"intermediate.dvi"):null,Budget);
        Assert.Null(result.Error);return Assert.IsType<SourceMapGeometryArtifact>(result.Artifact);
    }
    internal static CompileOutputEnvelopeInput Input(CompileRequestContext context,bool geometry=true,string engine="lualatex")
    {
        var pdf=Native(engine,"output.pdf");var map=Native(engine,"source.synctex");var g=geometry?Assemble(engine):null;
        return new(){RequestId=context.RequestId,SnapshotIdentity=context.SnapshotIdentity,InputContentDigest=context.InputContentDigest,WorkerEpoch=context.WorkerEpoch,
            Status=CompileOutputStatus.Succeeded,OutputFormat=context.OutputFormat,OutputAdapter=context.OutputAdapter,
            Payload=pdf,ClaimedLength=pdf.Length,ClaimedHash=Digest(pdf),SourceMapping=new(Digest(map),map.Length),SourceMapPayload=map,
            SourceMapGeometryPayload=g?.ToArray(),SourceMapGeometry=g?.Identity};
    }
}
