using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Hash=Client.Workspace.Domain.SHA256;

namespace Client.Compilation.SyncTeX;

/// <summary>Strict bounded DATA codec. Hashes are correlation, not authenticity,
/// PDF validity, physical units, converter composition or navigation authority.</summary>
public static class SourceMapGeometryCodec
{
    public const int MaxBytes=CompileOutputBudget.SourceMapGeometryByteLimit, MaxPages=512;
    private static readonly UTF8Encoding Utf8=new(false,true);
    private sealed class Invalid(SourceMapGeometryError error):Exception {internal SourceMapGeometryError Error {get;}=error;}
    private static void Need(bool ok,SourceMapGeometryError error=SourceMapGeometryError.InvalidShape){if(!ok)throw new Invalid(error);}
    public static bool IsValidIdentity(SourceMapGeometryIdentity? identity)=>identity is not null && identity.ByteLength is >0 and <=MaxBytes && ValidHash(identity.Hash.Hex);
    internal static bool ValidHash(string? hash)=>hash is {Length:64} && hash.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string? Kind(string? engine)=>engine switch {"pdflatex" or "lualatex"=>"none","latex-dvi"=>"dvi","xelatex"=>"xdv",_=>null};
    public static SourceMapGeometrySidecarResult ParseNativeSidecar(byte[]? bytes)
    {
        if(bytes is null||bytes.Length==0)return new(null,SourceMapGeometryError.InvalidInput);
        if(bytes.Length>MaxBytes)return new(null,SourceMapGeometryError.OverBudget);
        var owned=bytes.ToArray();
        try {using var doc=Document(owned);var root=doc.RootElement;Fields(root,"schemaVersion pages");Schema(root);return new(new(Pages(root.GetProperty("pages"))),null);}
        catch(Invalid e){return new(null,e.Error);}
        catch(DecoderFallbackException){return new(null,SourceMapGeometryError.InvalidUtf8);}
        catch(JsonException){return new(null,SourceMapGeometryError.InvalidShape);}
        catch(InvalidOperationException){return new(null,SourceMapGeometryError.InvalidUtf8);}
    }
    public static SourceMapGeometryReadResult ParseWrapper(byte[]? bytes)
    {
        if(bytes is null||bytes.Length==0)return new(null,SourceMapGeometryError.InvalidInput);
        if(bytes.Length>MaxBytes)return new(null,SourceMapGeometryError.OverBudget);
        // Never decode/hash the caller's buffer or retain read-only-memory aliases.
        var owned=bytes.ToArray();
        try
        {
            using var doc=Document(owned);var r=doc.RootElement;
            Fields(r,"schemaVersion engineId finalPass intermediateKind intermediateSha256 pdfSha256 mapSha256 pages");Schema(r);
            var engine=Text(r,"engineId");var kind=Text(r,"intermediateKind");Need(Kind(engine)==kind);
            Need(r.GetProperty("finalPass").ValueKind==JsonValueKind.Number&&r.GetProperty("finalPass").TryGetUInt32(out var pass)&&pass>0);
            // Read again after validation to keep definite-assignment explicit.
            var finalPass=r.GetProperty("finalPass").GetUInt32();
            string? intermediate=null;
            if(kind=="none")Need(r.GetProperty("intermediateSha256").ValueKind==JsonValueKind.Null);
            else {intermediate=Text(r,"intermediateSha256");Need(ValidHash(intermediate));}
            var pdf=Text(r,"pdfSha256");var map=Text(r,"mapSha256");Need(ValidHash(pdf)&&ValidHash(map));
            var geometry=new SourceMapGeometry(engine,finalPass,kind,intermediate,pdf,map,Pages(r.GetProperty("pages")));
            return new(new(owned,geometry),null);
        }
        catch(Invalid e){return new(null,e.Error);}
        catch(DecoderFallbackException){return new(null,SourceMapGeometryError.InvalidUtf8);}
        catch(JsonException){return new(null,SourceMapGeometryError.InvalidShape);}
        catch(InvalidOperationException){return new(null,SourceMapGeometryError.InvalidUtf8);}
    }
    /// <summary>Trusted future orchestration supplies the actual selected engine/
    /// final ordinal and already-composed native sidecar. No success flag or PDF
    /// geometry inference. Independently copied actual bytes define the hashes.</summary>
    public static SourceMapGeometryReadResult AssembleData(string engineId,uint finalPass,byte[]? nativeSidecar,
        byte[]? map,byte[]? pdf,byte[]? intermediate,CompileOutputBudget budget)
    {
        var kind=Kind(engineId);
        if(budget is null||!budget.IsValid||kind is null||finalPass==0||nativeSidecar is null||nativeSidecar.Length==0
            ||map is null||map.Length==0||pdf is null||pdf.Length==0||kind=="none"&&intermediate is not null
            ||kind!="none"&&(intermediate is null||intermediate.Length==0))return new(null,SourceMapGeometryError.InvalidInput);
        if(nativeSidecar.Length>MaxBytes||map.Length>budget.MaxSourceMapPayloadBytes||pdf.Length>budget.MaxPayloadBytes
            ||intermediate is not null&&intermediate.Length>budget.MaxPayloadBytes)return new(null,SourceMapGeometryError.OverBudget);
        var parsed=ParseNativeSidecar(nativeSidecar);if(parsed.Sidecar is not{} sidecar)return new(null,parsed.Error);
        var ownedMap=map.ToArray();var ownedPdf=pdf.ToArray();var ownedIntermediate=intermediate?.ToArray();
        string HashBytes(byte[] b)=>Hash.FromBytes(System.Security.Cryptography.SHA256.HashData(b)).Hex;
        var geometry=new SourceMapGeometry(engineId,finalPass,kind,ownedIntermediate is null?null:HashBytes(ownedIntermediate),HashBytes(ownedPdf),HashBytes(ownedMap),sidecar.Pages);
        try
        {
            var bytes=Encode(geometry,budget.MaxSourceMapGeometryBytes);
            return new(new(bytes,geometry),null);
        }
        catch(Invalid e){return new(null,e.Error);}
    }
    private static JsonDocument Document(byte[] owned)
    {
        Need(!owned.AsSpan().StartsWith(new byte[]{239,187,191}),SourceMapGeometryError.InvalidUtf8);
        return JsonDocument.Parse(Utf8.GetString(owned),new JsonDocumentOptions{MaxDepth=16});
    }
    private static void Fields(JsonElement value,string fields)
    {
        Need(value.ValueKind==JsonValueKind.Object);var required=fields.Split(' ');var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var p in value.EnumerateObject())Need(seen.Add(p.Name)&&required.Contains(p.Name,StringComparer.Ordinal));
        Need(seen.Count==required.Length);
    }
    private static void Schema(JsonElement root)=>Need(root.GetProperty("schemaVersion").ValueKind==JsonValueKind.Number&&root.GetProperty("schemaVersion").TryGetInt32(out var version)&&version==1);
    private static string Text(JsonElement root,string name)
    {
        var p=root.GetProperty(name);Need(p.ValueKind==JsonValueKind.String);var s=p.GetString();
        Need(s is not null && s.Length<=64 && s.All(c=>c is >= ' ' and <= '~'));
        return s!;
    }
    private static ImmutableList<SourceMapPageTransform> Pages(JsonElement pages)
    {
        Need(pages.ValueKind==JsonValueKind.Array&&pages.GetArrayLength() is >=1 and <=MaxPages);
        var result=ImmutableList.CreateBuilder<SourceMapPageTransform>();int expected=1;
        foreach(var p in pages.EnumerateArray())
        {
            Fields(p,"page status reason writerToPdfRaw writerPageBoxBp");
            Need(p.GetProperty("page").ValueKind==JsonValueKind.Number&&p.GetProperty("page").TryGetInt32(out var n)&&n==expected);
            var status=Text(p,"status");var reason=Text(p,"reason");
            Need(status is "available" or "unsupported" or "indeterminate");
            Need(reason is "none" or "writer-origin-unavailable" or "converter-transform-unavailable" or "noninvertible-transform" or "out-of-domain");
            SourceMapMatrix? matrix=null;SourceMapWriterBox? box=null;
            if(status=="available")
            {
                Need(reason=="none");var m=Numbers(p.GetProperty("writerToPdfRaw"),6);var b=Numbers(p.GetProperty("writerPageBoxBp"),4);
                var value=new SourceMapMatrix(m[0],m[1],m[2],m[3],m[4],m[5]);
                Need(value.TryInverse(out _),SourceMapGeometryError.InvalidTransform);
                Need(b[2]>b[0]&&b[3]>b[1],SourceMapGeometryError.InvalidTransform);
                matrix=value;box=new(b[0],b[1],b[2],b[3]);
            }
            else Need(reason!="none"&&p.GetProperty("writerToPdfRaw").ValueKind==JsonValueKind.Null&&p.GetProperty("writerPageBoxBp").ValueKind==JsonValueKind.Null);
            result.Add(new(expected++,status,reason,matrix,box));
        }
        return result.ToImmutable();
    }
    private static double[] Numbers(JsonElement value,int length)
    {
        Need(value.ValueKind==JsonValueKind.Array&&value.GetArrayLength()==length);var numbers=new double[length];int i=0;
        foreach(var v in value.EnumerateArray())
        {
            Need(v.ValueKind==JsonValueKind.Number&&v.TryGetDouble(out var number)&&double.IsFinite(number)&&Math.Abs(number)<=1000000,SourceMapGeometryError.InvalidTransform);
            numbers[i++]=v.GetDouble();
        }
        return numbers;
    }
    private static byte[] Encode(SourceMapGeometry geometry,int maximum)
    {
        using var stream=new LimitedStream(maximum);
        using(var w=new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();w.WriteNumber("schemaVersion",1);w.WriteString("engineId",geometry.EngineId);w.WriteNumber("finalPass",geometry.FinalPass);
            w.WriteString("intermediateKind",geometry.IntermediateKind);w.WriteString("intermediateSha256",geometry.IntermediateSha256);
            w.WriteString("pdfSha256",geometry.PdfSha256);w.WriteString("mapSha256",geometry.MapSha256);w.WriteStartArray("pages");
            foreach(var page in geometry.Pages)
            {
                w.WriteStartObject();w.WriteNumber("page",page.Page);w.WriteString("status",page.Status);w.WriteString("reason",page.Reason);
                if(page.WriterToPdfRaw is{} m)
                {w.WriteStartArray("writerToPdfRaw");foreach(var n in new[]{m.A,m.B,m.C,m.D,m.E,m.F})w.WriteNumberValue(n);w.WriteEndArray();}
                else w.WriteNull("writerToPdfRaw");
                if(page.WriterPageBoxBp is{} b)
                {w.WriteStartArray("writerPageBoxBp");foreach(var n in new[]{b.Left,b.Bottom,b.Right,b.Top})w.WriteNumberValue(n);w.WriteEndArray();}
                else w.WriteNull("writerPageBoxBp");w.WriteEndObject();
            }
            w.WriteEndArray();w.WriteEndObject();
        }
        return stream.ToArray();
    }
    private sealed class LimitedStream(int maximum):MemoryStream
    {
        public override void Write(byte[] buffer,int offset,int count){Check(count);base.Write(buffer,offset,count);}
        public override void Write(ReadOnlySpan<byte> buffer){Check(buffer.Length);base.Write(buffer);}
        private void Check(int count){if(count>maximum-Length)throw new Invalid(SourceMapGeometryError.OverBudget);}
    }
}
