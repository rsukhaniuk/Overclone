using System.Text.Json;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Storage;
using Microsoft.JSInterop;
using Xunit;

namespace Workspace.Tests;

public class PatchBoundaryPortTests
{
    [Fact]
    public async Task Lock_adapter_checks_its_ID_table_then_browser_and_release_invalidates_it()
    {
        var js=new Js();await using var locks=new JsWorkspaceLocks(js);var name="oop-project:test";
        Assert.True(await locks.IsHeldAsync(name,CancellationToken.None)); // any client
        Assert.False(await locks.OwnsAsync("other-client",name,CancellationToken.None));Assert.DoesNotContain("ownsLock",js.Module.Calls);
        var id=await locks.TryAcquireAsync(name,CancellationToken.None);Assert.Equal("ours",id);
        Assert.True(locks.IsOwnedLocally(id!,name));Assert.False(locks.IsOwnedLocally(id!,"wrong-name"));
        Assert.True(await locks.OwnsAsync(id!,name,CancellationToken.None));Assert.Equal(new object?[]{id,name},js.Module.OwnerArgs);
        js.Module.Owned=false;Assert.False(await locks.OwnsAsync(id!,name,CancellationToken.None));
        await locks.ReleaseAsync(id!,CancellationToken.None);Assert.False(locks.IsOwnedLocally(id!,name));
        Assert.False(await locks.OwnsAsync(id!,name,CancellationToken.None));
    }

    [Theory]
    [InlineData("healthy",true)][InlineData("wrong-head",false)][InlineData("missing-manifest",false)]
    [InlineData("damaged",false)][InlineData("foreign-project",false)]
    public async Task Repository_head_verifies_objects_without_loading_an_editor_snapshot(string mode,bool expected)
    {
        var js=new Js();js.Module.Mode=mode;var repo=new WorkspaceStorage(js);
        Assert.Equal(expected,await repo.VerifyHeadAsync(js.Module.Project,js.Module.Head,CancellationToken.None));
        Assert.DoesNotContain("readContent",js.Module.Calls);Assert.DoesNotContain("commitBatch",js.Module.Calls);
        if(expected){Assert.Equal(2,js.Module.Calls.Count(x=>x=="getProject"));Assert.Contains("verifyContent",js.Module.Calls);}
    }

    [Fact]
    public async Task Head_advance_during_object_verification_is_detected_by_the_final_metadata_read()
    {
        var js=new Js();var repo=new WorkspaceStorage(js);var barrier=js.Module.VerifyBarrier=new WorkspaceTextPatchTests.Barrier();
        var task=repo.VerifyHeadAsync(js.Module.Project,js.Module.Head,CancellationToken.None);await barrier.Reached.Task;
        js.Module.Changed=true;barrier.Release.SetResult();Assert.False(await task);
    }

    private sealed class Js:IJSRuntime
    {
        internal Module Module=new();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier,object?[]? args)=>InvokeAsync<TValue>(identifier,CancellationToken.None,args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier,CancellationToken cancellationToken,object?[]? args)
            => ValueTask.FromResult((TValue)(object)Module);
    }
    private sealed class Module:IJSObjectReference
    {
        internal List<string> Calls=new();internal object?[]? OwnerArgs;
        internal bool Owned=true,Changed;internal string Mode="healthy";
        internal ProjectId Project=ProjectId.New();internal RevisionId Head=RevisionId.New();
        internal WorkspaceTextPatchTests.Barrier? VerifyBarrier;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier,object?[]? args)=>InvokeAsync<TValue>(identifier,CancellationToken.None,args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier,CancellationToken ct,object?[]? args)
        {
            Calls.Add(identifier);
            if(identifier=="verifyContent"&&VerifyBarrier is{} b)await b.Wait(ct);
            object? value=identifier switch{
                "tryAcquireLock"=>"ours","releaseLock"=>true,"lockHeld"=>true,"ownsLock"=>Owned,
                "getProject"=>new{Id=Project.ToString(),Name="p",HeadRevisionId=(Changed||Mode=="wrong-head"?RevisionId.New():Head).ToString()},
                "getRevision" when Mode=="missing-manifest"=>null,
                "getRevision"=>new{Id=Head.ToString(),ProjectId=(Mode=="foreign-project"?ProjectId.New():Project).ToString(),CreatedAt=DateTimeOffset.UtcNow.ToString("O"),Files=new[]{new{Id=FileId.New().ToString(),RelativePath="a.tex",MediaKind="text",ContentHash=new string('a',64),ByteLength=3}}},
                "verifyContent"=>new{Exists=true,Length=3,HashMatch=Mode!="damaged"},
                _=>throw new InvalidOperationException("Unexpected browser operation: "+identifier),
            };
            if(identifier=="ownsLock")OwnerArgs=args;
            return JsonSerializer.Deserialize<TValue>(JsonSerializer.Serialize(value))!;
        }
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
