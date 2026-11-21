using System.Collections.Immutable;
using System.Text;
using Client.Analysis;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Patching;
using Client.Workspace.Transfer;

namespace Client.Review;

internal static class ReviewPreparation
{
    internal static TextChangeSet? CanonicalF1(AnalysisReport report,string id,out AnalysisFixDescriptorDto? fix,out ReviewCitation? citation)
    {
        fix=null;citation=null;
        // Retain the historical entry name; admission now resolves any finite
        // canonical rule-fix ID, never a caller-provided descriptor or parser.
        if(!AnalysisCatalog.IsCanonicalFixId(id)||report.Snapshot.Selection.Profile is not
            { Id: AnalysisCatalog.KnuId, Version: AnalysisCatalog.Version or AnalysisCatalog.LatestVersion or AnalysisCatalog.MarginFixVersion or AnalysisCatalog.GeometryPackageVersion or AnalysisCatalog.ProjectMarginFixVersion }
            ||report.Snapshot.Configuration is null||report.Result.Knu is not{} knu)return null;
        if(report.Result.SnapshotId!=report.Snapshot.Id.ToString()
            ||report.Result.Profile.Id!=report.Snapshot.Profile.Id
            ||report.Result.Profile.Version!=report.Snapshot.Profile.Version)return null;
        // Reuse adapter consistency/capture validation, not a second LaTeX or
        // length predicate. Never resolve caller-supplied descriptor objects.
        KnuWireValidation.Validate(report.Result);
        KnuWireValidation.Snapshot(report.Result,report.Snapshot);
        fix=knu.FixDescriptors.SingleOrDefault(f=>f.Id==id);
        if(fix is null)return null;
        var descriptor=fix;
        var source=report.Snapshot.Files.Single(f=>f.FileId.ToString()==descriptor.FileId);
        var rule=knu.Metadata.Rules.Single(r=>r.RuleId==descriptor.RuleId);
        citation=new(rule.RuleId,rule.Version,rule.Citation.Section,
            rule.Citation.PrintedPages.ToImmutableArray(),rule.Citation.PdfPages.ToImmutableArray());
        return TextChangeSet.Own(TextPatchOrigin.Rule,fix.Explanation,
            [new(source.FileId,source.RelativePath,source.Sha256,source.ByteLength,
                [new(fix.Span.StartByte,fix.Span.EndByte,Encoding.UTF8.GetBytes(fix.ExpectedOriginal),Encoding.UTF8.GetBytes(fix.Replacement))])],true).ChangeSet;
    }

    internal static PatchSourceInput[] Sources(WorkspaceCoordinator workspace,AnalysisSnapshot snapshot)
    {
        var text=snapshot.Files.ToDictionary(f=>f.FileId);
        return workspace.Files.Where(w=>!w.Deleted).Select(w=>text.TryGetValue(w.Id,out var f)
            ?new PatchSourceInput(w.Id,w.RelativePath,w.MediaKind,f.ByteLength,w.HasBom,f.Bytes.AsMemory())
            :new PatchSourceInput(w.Id,w.RelativePath,w.MediaKind,w.BinaryContent?.LongLength??w.DurableLength,w.HasBom)).ToArray();
    }

    internal static ImmutableArray<ReviewFilePreview> Diff(PreparedTextBatch batch)
    {
        long remaining=ArchiveLimits.DiffByteBudget;
        var result=ImmutableArray.CreateBuilder<ReviewFilePreview>(batch.Files.Length);
        foreach(var file in batch.Files)
        {
            long size=(long)file.Before.Length+file.After.Length;
            // Bound line-list AND Myers trace memory before splitting strings.
            int a=CountLines(file.Before.Content),b=CountLines(file.After.Content);
            long n=(long)a+b;
            long work=(n+1)*(2*n+1)*sizeof(int)+size;
            List<DiffLine>? diff=null;
            if(a<=ArchiveLimits.DiffLineBudget&&b<=ArchiveLimits.DiffLineBudget&&work<=remaining)
            {
                remaining-=work;
                diff=LineDiff.Compute(TextContent.SplitLines(file.Before.Content),TextContent.SplitLines(file.After.Content));
            }
            result.Add(new(file,diff?.ToImmutableArray()??ImmutableArray<DiffLine>.Empty,diff is null));
        }
        return result.MoveToImmutable();
    }
    private static int CountLines(string text)
    {
        int n=1;
        for(int i=0;i<text.Length;i++)
        {
            if(text[i]=='\r'){n++;if(i+1<text.Length&&text[i+1]=='\n')i++;}
            else if(text[i]=='\n')n++;
            if(n>ArchiveLimits.DiffLineBudget)return n;
        }
        return n;
    }

    /// <summary>Cluster adjacent original edits so deletions that collapse to
    /// the same postimage start become one unambiguous inverse. All inverse
    /// expected bytes are sliced from the owned postimage, not a caller string.</summary>
    internal static FilePatchInput[] Inverse(PreparedTextBatch batch)
    {
        return batch.Files.Select(file=>
        {
            var edits=new List<TextEditInput>();long delta=0;int i=0;
            while(i<file.Edits.Length)
            {
                var first=file.Edits[i++];long start=first.StartByte,end=first.EndByte;
                long difference=first.Replacement.Length-first.Expected.Length;
                while(i<file.Edits.Length&&file.Edits[i].StartByte<=end)
                {
                    var next=file.Edits[i++];end=next.EndByte;
                    difference+=(long)next.Replacement.Length-next.Expected.Length;
                }
                long postStart=start+delta,postEnd=end+delta+difference;
                var expected=file.After.Span.Slice((int)postStart,(int)(postEnd-postStart));
                var replacement=file.Before.Span.Slice((int)start,(int)(end-start));
                // Individually effective forward edits can cancel within a
                // cluster. Do not feed that inert inverse to 4a's no-op guard.
                if(!expected.SequenceEqual(replacement))
                    edits.Add(new(postStart,postEnd,expected.ToArray(),replacement.ToArray()));
                // Track every forward cluster, including omitted net-zero ones.
                delta+=difference;
            }
            return new FilePatchInput(file.FileId,file.RelativePath,file.After.Hash,file.After.Length,edits);
        }).ToArray();
    }
}
