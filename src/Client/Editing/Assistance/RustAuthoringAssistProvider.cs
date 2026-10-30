using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;

namespace Client.Editing.Assistance;

/// <summary>One actual preparation/import/worker operation at a time. Cancellation
/// signals JS but does not abandon actual tasks or release admission prematurely.</summary>
public sealed class RustAuthoringAssistProvider(IJSRuntime js) : IAuthoringAssistProvider
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task<Port>? _portTask;
    private bool _disposed;
    private int _busy;
    private long _lastEpoch;
    public async Task<AuthoringAssistReply> EvaluateAsync(AuthoringAssistRequest request, CancellationToken cancellationToken = default)
    {
        string id=request?.RequestId??"";
        AuthoringAssistReply End(AuthoringAssistStatus status)=>new(status,id);
        CancellationTokenSource linked;
        lock(_gate)
        {
            if(_disposed)return End(AuthoringAssistStatus.Disposed);
            if(_busy!=0)return End(AuthoringAssistStatus.Busy);
            _busy=1;linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,_shutdown.Token);
        }
        CancellationTokenRegistration registration=default;Task? cancellation=null;
        try
        {
            if(linked.IsCancellationRequested)return End(AuthoringAssistStatus.Cancelled);
            string metadata;byte[] packed;
            try{(metadata,packed)=AuthoringAssistJson.Pack(request!);}
            catch(Exception e) when(e is ArgumentException or JsonException or InvalidOperationException or OverflowException)
            {return End(AuthoringAssistStatus.InvalidInput);}
            var port=await GetPortAsync().ConfigureAwait(false); // Actual uncancellable task.
            if(_disposed)return End(AuthoringAssistStatus.Disposed);
            if(linked.IsCancellationRequested)return End(AuthoringAssistStatus.Cancelled);
            registration=linked.Token.Register(()=>cancellation=CancelAsync(port,id));
            if(linked.IsCancellationRequested)return End(AuthoringAssistStatus.Cancelled);
            var reply=await Transport(()=>port.Channel.InvokeAsync<AuthoringAssistBridgeReply>("evaluate",[metadata,packed])).ConfigureAwait(false);
            if(_disposed)return End(AuthoringAssistStatus.Disposed);
            if(linked.IsCancellationRequested)return End(AuthoringAssistStatus.Cancelled);
            if(reply is null || reply.Version!=1 || reply.RequestId!=id || reply.WorkerEpoch<0 || reply.WorkerEpoch>9007199254740991)
                return End(AuthoringAssistStatus.InvalidResponse);
            if(reply.WorkerEpoch>0)
            {
                if(reply.WorkerEpoch<=_lastEpoch)return End(AuthoringAssistStatus.InvalidResponse);
                _lastEpoch=reply.WorkerEpoch;
            }
            var status=reply.Status switch
            {
                "completed"=>AuthoringAssistStatus.Completed,"invalidInput"=>AuthoringAssistStatus.InvalidInput,
                "cancelled"=>AuthoringAssistStatus.Cancelled,"timedOut"=>AuthoringAssistStatus.TimedOut,
                "busy"=>AuthoringAssistStatus.Busy,"disposed"=>AuthoringAssistStatus.Disposed,
                "unavailable"=>AuthoringAssistStatus.Unavailable,_=>AuthoringAssistStatus.InvalidResponse
            };
            if(status==AuthoringAssistStatus.Completed)
            {
                if(!AuthoringAssistJson.Epoch(reply.WorkerEpoch) || !AuthoringAssistJson.Text(reply.ResponseJson,AuthoringAssistJson.ResponseMaxBytes,false))
                    return End(AuthoringAssistStatus.InvalidResponse);
            }
            else if(reply.ResponseJson is not null)return End(AuthoringAssistStatus.InvalidResponse);
            return new(status,id,reply.WorkerEpoch,reply.ResponseJson);
        }
        catch(ObjectDisposedException){return End(_disposed?AuthoringAssistStatus.Disposed:AuthoringAssistStatus.Unavailable);}
        catch(OperationCanceledException){return End(linked.IsCancellationRequested?AuthoringAssistStatus.Cancelled:AuthoringAssistStatus.Unavailable);}
        catch(JsonException){return End(AuthoringAssistStatus.InvalidResponse);}
        catch(JSException){return End(AuthoringAssistStatus.Unavailable);}
        finally
        {
            registration.Dispose();
            if(cancellation is not null)await cancellation.ConfigureAwait(false);
            linked.Dispose();lock(_gate)_busy=0;
        }
    }
    private async Task<Port> GetPortAsync()
    {
        Task<Port> task;
        lock(_gate){ObjectDisposedException.ThrowIf(_disposed,this);task=_portTask??=InitializeAsync();}
        try{return await task.ConfigureAwait(false);}
        catch{lock(_gate)if(ReferenceEquals(task,_portTask))_portTask=null;throw;}
    }
    private async Task<Port> InitializeAsync()
    {
        var module=await Transport(()=>js.InvokeAsync<IJSObjectReference>("import",["./js/authoring-assist-bridge.js"])).ConfigureAwait(false);
        if(module is null)throw new JSException("Assistance unavailable.");
        IJSObjectReference? channel=null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed,this);
            channel=await Transport(()=>module.InvokeAsync<IJSObjectReference>("createAuthoringAssistPort",[])).ConfigureAwait(false);
            if(channel is null)throw new JSException("Assistance unavailable.");
            ObjectDisposedException.ThrowIf(_disposed,this);return new(module,channel);
        }
        catch
        {
            if(channel is not null)await new Port(module,channel).CloseAsync().ConfigureAwait(false);
            else await ReleaseAsync(module).ConfigureAwait(false);
            throw;
        }
    }
    public ValueTask DisposeAsync()
    {
        Task<Port>? task;
        lock(_gate){if(_disposed)return ValueTask.CompletedTask;_disposed=true;task=_portTask;}
        _shutdown.Cancel();_shutdown.Dispose();
        if(task is not null)Observe(CloseWhenReadyAsync(task));
        return ValueTask.CompletedTask;
    }
    private static async Task CloseWhenReadyAsync(Task<Port> task)
    {try{await (await task.ConfigureAwait(false)).CloseAsync().ConfigureAwait(false);}catch(Exception e)when(e is not OutOfMemoryException){}}
    // Avoid SDK internal await capture of a borrowed UI/compile owner, exactly as
    // the existing RenderedExtent evaluator does. No UI/model work enters here.
    private static T Transport<T>(Func<T> action)
    {var context=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(null);try{return action();}finally{SynchronizationContext.SetSynchronizationContext(context);}}
    private static async Task CancelAsync(Port port,string id)
    {try{await Transport(()=>port.Channel.InvokeAsync<IJSVoidResult>("cancel",[id])).ConfigureAwait(false);}catch(Exception e)when(e is not OutOfMemoryException){}}
    private static async Task ReleaseAsync(IJSObjectReference reference)
    {try{await Transport(reference.DisposeAsync).ConfigureAwait(false);}catch(Exception e)when(e is not OutOfMemoryException){}}
    private static void Observe(Task task)=>_=task.ContinueWith(t=>{_=t.Exception;},CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
    private sealed class Port(IJSObjectReference module,IJSObjectReference channel)
    {
        private int _closed;
        internal IJSObjectReference Channel=>channel;
        internal async Task CloseAsync()
        {
            if(Interlocked.Exchange(ref _closed,1)!=0)return;
            try{await Transport(()=>channel.InvokeAsync<IJSVoidResult>("dispose",[])).ConfigureAwait(false);}
            catch(Exception e)when(e is not OutOfMemoryException){}
            finally{await ReleaseAsync(channel).ConfigureAwait(false);await ReleaseAsync(module).ConfigureAwait(false);}
        }
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AuthoringAssistBridgeReply
{
    [JsonRequired] public int Version {get;set;}
    [JsonRequired] public string RequestId {get;set;}="";
    [JsonRequired] public long WorkerEpoch {get;set;}
    [JsonRequired] public string Status {get;set;}="";
    [JsonRequired] public string? ResponseJson {get;set;}
}
