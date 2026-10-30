using System.Text.Json;
using Client.Analysis;
using Client.Editing;
using Client.Editing.Assistance;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.AuthoringAssistance;

public sealed partial class AuthoringAssistSessionTests
{
    [Fact] public void Includegraphics_offers_binary_path_metadata_without_read_hash_or_copy_of_asset_contents()=>Run(async()=>
    {
        await using var f=await CompilationTestWorkspace.Open();const string source="\\includegraphics{fig}";Prepare(f,source);
        var asset=f.Workspace.Files.Single(x=>x.MediaKind=="binary");Assert.True(f.Workspace.RenameFile(asset.Id,"figures/графік😀.png"));
        var reference=asset.BinaryContent;var assetHash=asset.DurableHash;f.Repo.ReadHashes.Clear();
        await using var factory=new AnalysisSnapshotFactory(f.Workspace);var provider=new Provider{PathCandidate=true};
        int caret=source.LastIndexOf("fig",StringComparison.Ordinal)+3;var editor=new Editor(f.Workspace){Selection=new(caret,caret)};
        using var session=Session(f,factory,provider,editor);session.SetContext(AuthoringAssistContext.InputPath);
        Assert.True(await session.SuggestAsync());var report=Assert.IsType<AuthoringAssistReport>(session.Report);
        var candidate=Assert.Single(report.Result.Candidates);Assert.Equal("path",candidate.Kind);Assert.Equal(asset.RelativePath,candidate.Label);Assert.Null(candidate.Definition);
        Assert.Same(reference,asset.BinaryContent);Assert.DoesNotContain(assetHash!.Value,f.Repo.ReadHashes);
        var packed=AuthoringAssistJson.Pack(provider.Last!);Assert.Equal(provider.Last!.Snapshot.Files.Sum(x=>x.ByteLength),packed.PackedBytes.LongLength);
        using var metadata=JsonDocument.Parse(packed.MetadataJson);
        var descriptor=metadata.RootElement.GetProperty("payload").GetProperty("files").EnumerateArray().Single(d=>d.GetProperty("fileId").GetString()==asset.Id.ToString());
        Assert.Equal("binary",descriptor.GetProperty("mediaKind").GetString());Assert.Equal(0,descriptor.GetProperty("byteLength").GetInt64());
        Assert.Equal(packed.PackedBytes.LongLength,descriptor.GetProperty("byteOffset").GetInt64());Assert.False(descriptor.GetProperty("hasBom").GetBoolean());
        string emptyHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant();
        Assert.Equal(emptyHash,descriptor.GetProperty("sha256").GetString());Assert.NotEqual(assetHash.Value.ToString(),emptyHash);
        Assert.False(await session.NavigateAsync(report,candidate.Id)); // Binary has no text source span.
        Assert.True(await session.InsertAsync(report,candidate.Id));Assert.Equal("\\includegraphics{figures/графік😀.png}",f.Workspace.ActiveFile!.Content);
        Assert.Same(reference,asset.BinaryContent);Assert.DoesNotContain(assetHash.Value,f.Repo.ReadHashes);
    });
    [Theory][InlineData("tree")][InlineData("replacement")][InlineData("media")]
    public void Binary_path_reply_is_stale_after_tree_metadata_or_asset_replacement(string change)=>Run(async()=>
    {
        await using var f=await CompilationTestWorkspace.Open();const string source="\\includegraphics{fig}";Prepare(f,source);
        var asset=f.Workspace.Files.Single(x=>x.MediaKind=="binary");Assert.True(f.Workspace.RenameFile(asset.Id,"fig.png"));
        await using var factory=new AnalysisSnapshotFactory(f.Workspace);var provider=new Provider{PathCandidate=true,Hold=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        int caret=source.LastIndexOf("fig",StringComparison.Ordinal)+3;var editor=new Editor(f.Workspace){Selection=new(caret,caret)};
        using var session=Session(f,factory,provider,editor);session.SetContext(AuthoringAssistContext.InputPath);
        var job=session.SuggestAsync();await provider.Reached.Task.WaitAsync(Bound);
        if(change=="tree")Assert.True(f.Workspace.RenameFile(asset.Id,"renamed.png"));
        if(change=="replacement")Assert.True(f.Workspace.ReplaceFileBytes(asset.Id,[1,2,3]));
        if(change=="media")asset.MediaKind="text"; // Defense against changed metadata even without a coordinator notification.
        string before=f.State();provider.Hold.TrySetResult();Assert.False(await job.WaitAsync(Bound));Assert.Null(session.Report);Assert.Equal(before,f.State());Assert.Equal(0,editor.Applies);
    });
    [Fact] public void Outline_navigation_is_a_readonly_single_consumption_action()=>Run(async()=>
    {
        await using var f=await CompilationTestWorkspace.Open(readOnly:true);Prepare(f);
        await using var factory=new AnalysisSnapshotFactory(f.Workspace);var provider=new Provider{Outline=true};var editor=new Editor(f.Workspace);
        using var session=Session(f,factory,provider,editor);session.SetContext(AuthoringAssistContext.Outline);
        Assert.True(await session.SuggestAsync());var report=session.Report!;Assert.Single(report.Result.Outline);Assert.Empty(report.Result.Candidates);
        Assert.False(session.CanInsert(report));Assert.True(await session.NavigateAsync(report,outlineIndex:0));Assert.Null(session.Report);Assert.Equal(0,editor.Applies);
    });
    [Theory][InlineData(AuthoringAssistStatus.TimedOut)][InlineData(AuthoringAssistStatus.Unavailable)][InlineData(AuthoringAssistStatus.InvalidResponse)]
    public void Provider_failure_never_mutates_or_publishes_a_proposal(AuthoringAssistStatus status)=>Run(async()=>
    {
        await using var f=await CompilationTestWorkspace.Open();Prepare(f);await using var factory=new AnalysisSnapshotFactory(f.Workspace);
        var editor=new Editor(f.Workspace);using var session=Session(f,factory,new Provider{Status=status},editor);string before=f.State();
        Assert.False(await session.SuggestAsync());Assert.Null(session.Report);Assert.Equal(before,f.State());Assert.Equal(0,editor.Applies);
    });
    [Fact] public void Malformed_source_reply_is_nonmutating()=>Run(async()=>
    {
        await using var f=await CompilationTestWorkspace.Open();Prepare(f);await using var factory=new AnalysisSnapshotFactory(f.Workspace);
        var editor=new Editor(f.Workspace);using var session=Session(f,factory,new Provider{InvalidBody=true},editor);string before=f.State();
        Assert.False(await session.SuggestAsync());Assert.Null(session.Report);Assert.Equal(before,f.State());Assert.Equal(0,editor.Applies);
    });
}
