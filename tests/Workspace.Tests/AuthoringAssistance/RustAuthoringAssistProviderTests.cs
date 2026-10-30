using Client.Editing.Assistance;
using Microsoft.JSInterop;
using Xunit;

namespace Workspace.Tests.AuthoringAssistance;

// Actual C# provider lifetime with held foreign transport tasks. No browser proof.
public sealed class RustAuthoringAssistProviderTests
{
    private static readonly TimeSpan Bound=TimeSpan.FromSeconds(5);
    private sealed class Runtime(AuthoringAssistRequest request) : IJSRuntime
    {
        internal readonly Module Module=new(request);internal TaskCompletionSource<IJSObjectReference>? Import;
        internal int Imports;
        public ValueTask<T> InvokeAsync<T>(string identifier,object?[]? args)=>InvokeAsync<T>(identifier,default,args);
        public async ValueTask<T> InvokeAsync<T>(string identifier,CancellationToken ct,object?[]? args)
        {
            Assert.Equal("import",identifier);Assert.Equal("./js/authoring-assist-bridge.js",args![0]);Assert.False(ct.CanBeCanceled);Imports++;
            return (T)(object)(Import is null?Module:await Import.Task.ConfigureAwait(false));
        }
    }
    private sealed class Module(AuthoringAssistRequest request) : IJSObjectReference
    {
        internal readonly Port Port=new(request);internal TaskCompletionSource<IJSObjectReference>? Factory;
        internal int Disposals;
        public ValueTask<T> InvokeAsync<T>(string identifier,object?[]? args)=>InvokeAsync<T>(identifier,default,args);
        public async ValueTask<T> InvokeAsync<T>(string identifier,CancellationToken ct,object?[]? args)
        {Assert.Equal("createAuthoringAssistPort",identifier);Assert.False(ct.CanBeCanceled);return (T)(object)(Factory is null?Port:await Factory.Task.ConfigureAwait(false));}
        public ValueTask DisposeAsync(){Disposals++;return ValueTask.CompletedTask;}
    }
    private sealed class Port(AuthoringAssistRequest request) : IJSObjectReference
    {
        internal int Calls,Cancels,Closes,Disposals;internal long Epoch;
        internal TaskCompletionSource? Hold,CancelHold;
        internal readonly TaskCompletionSource Reached=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Func<AuthoringAssistBridgeReply,AuthoringAssistBridgeReply>? Transform;
        internal SynchronizationContext? CancelContext;
        public ValueTask<T> InvokeAsync<T>(string identifier,object?[]? args)=>InvokeAsync<T>(identifier,default,args);
        public async ValueTask<T> InvokeAsync<T>(string identifier,CancellationToken ct,object?[]? args)
        {
            Assert.False(ct.CanBeCanceled);
            if(identifier=="cancel"){Cancels++;CancelContext=SynchronizationContext.Current;if(CancelHold is not null)await CancelHold.Task.ConfigureAwait(false);return default!;}
            if(identifier=="dispose"){Closes++;return default!;}
            Assert.Equal("evaluate",identifier);Assert.Equal(2,args!.Length);Assert.IsType<string>(args[0]);Assert.IsType<byte[]>(args[1]);Calls++;
            Reached.TrySetResult();if(Hold is not null)await Hold.Task.ConfigureAwait(false);
            var reply=new AuthoringAssistBridgeReply{Version=1,RequestId=request.RequestId,WorkerEpoch=++Epoch,Status="completed",ResponseJson=AuthoringAssistSessionTests.Body(request)};
            return (T)(object)(Transform?.Invoke(reply)??reply);
        }
        public ValueTask DisposeAsync(){Disposals++;return ValueTask.CompletedTask;}
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Cancelled_actual_import_or_factory_holds_slot_until_settlement_and_never_dispatches_late(bool factory)
    {
        var request=AuthoringAssistJsonTests.Request();var js=new Runtime(request);
        var hold=new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(factory)js.Module.Factory=hold;else js.Import=hold;
        await using var provider=new RustAuthoringAssistProvider(js);using var stop=new CancellationTokenSource();
        var first=provider.EvaluateAsync(request,stop.Token);stop.Cancel();Assert.False(first.IsCompleted);
        Assert.Equal(AuthoringAssistStatus.Busy,(await provider.EvaluateAsync(request)).Status);
        hold.TrySetResult(factory?js.Module.Port:js.Module);
        Assert.Equal(AuthoringAssistStatus.Cancelled,(await first.WaitAsync(Bound)).Status);Assert.Equal(0,js.Module.Port.Calls);
        Assert.Equal(AuthoringAssistStatus.Completed,(await provider.EvaluateAsync(request)).Status);Assert.Equal(1,js.Imports);
    }
    [Fact] public async Task Evaluation_and_late_cancel_settlement_both_hold_admission()
    {
        var request=AuthoringAssistJsonTests.Request();var js=new Runtime(request);
        js.Module.Port.Hold=new(TaskCreationOptions.RunContinuationsAsynchronously);js.Module.Port.CancelHold=new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider=new RustAuthoringAssistProvider(js);using var stop=new CancellationTokenSource();
        var first=provider.EvaluateAsync(request,stop.Token);await js.Module.Port.Reached.Task.WaitAsync(Bound);stop.Cancel();
        Assert.Equal(1,js.Module.Port.Cancels);Assert.False(first.IsCompleted);Assert.Null(js.Module.Port.CancelContext);
        js.Module.Port.Hold.TrySetResult();Assert.Equal(AuthoringAssistStatus.Busy,(await provider.EvaluateAsync(request)).Status);
        js.Module.Port.CancelHold.TrySetResult();Assert.Equal(AuthoringAssistStatus.Cancelled,(await first.WaitAsync(Bound)).Status);
        Assert.Equal(1,js.Module.Port.Calls);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Dispose_is_bounded_and_late_import_or_factory_reference_is_cleaned_once(bool factory)
    {
        var request=AuthoringAssistJsonTests.Request();var js=new Runtime(request);var hold=new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(factory)js.Module.Factory=hold;else js.Import=hold;
        var provider=new RustAuthoringAssistProvider(js);var task=provider.EvaluateAsync(request);await provider.DisposeAsync();Assert.False(task.IsCompleted);
        hold.TrySetResult(factory?js.Module.Port:js.Module);
        Assert.Equal(AuthoringAssistStatus.Disposed,(await task.WaitAsync(Bound)).Status);
        Assert.Equal(1,js.Module.Disposals);if(factory){Assert.Equal(1,js.Module.Port.Closes);Assert.Equal(1,js.Module.Port.Disposals);}
        await provider.DisposeAsync();Assert.Equal(1,js.Module.Disposals);
    }
    [Theory][InlineData("version")][InlineData("id")][InlineData("epoch")][InlineData("status")][InlineData("payload-on-error")][InlineData("oversize")]
    public async Task Malformed_wrapper_and_payload_bounds_fail_closed(string mutation)
    {
        var request=AuthoringAssistJsonTests.Request();var js=new Runtime(request);
        js.Module.Port.Transform=r=>
        {
            switch(mutation){case "version":r.Version=2;break;case "id":r.RequestId="wrong";break;case "epoch":r.WorkerEpoch=0;break;
                case "status":r.Status="unknown";break;case "payload-on-error":r.Status="unavailable";break;case "oversize":r.ResponseJson=new string('a',262145);break;}
            return r;
        };
        await using var provider=new RustAuthoringAssistProvider(js);Assert.Equal(AuthoringAssistStatus.InvalidResponse,(await provider.EvaluateAsync(request)).Status);
    }
    [Fact] public async Task Completed_reply_has_actual_monotonic_epoch_and_no_import_on_invalid_input()
    {
        var request=AuthoringAssistJsonTests.Request();var js=new Runtime(request);await using var provider=new RustAuthoringAssistProvider(js);
        Assert.Equal(AuthoringAssistStatus.InvalidInput,(await provider.EvaluateAsync(request with{Context=(AuthoringAssistContext)999})).Status);
        Assert.Equal(0,js.Imports);var first=await provider.EvaluateAsync(request);Assert.Equal(AuthoringAssistStatus.Completed,first.Status);Assert.Equal(1,first.WorkerEpoch);
        js.Module.Port.Transform=r=>{r.WorkerEpoch=1;return r;};Assert.Equal(AuthoringAssistStatus.InvalidResponse,(await provider.EvaluateAsync(request)).Status);
    }
}
