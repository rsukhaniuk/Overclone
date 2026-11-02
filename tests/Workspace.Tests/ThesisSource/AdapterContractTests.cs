using Client.Analysis;
using Microsoft.JSInterop;
using Xunit;

namespace Workspace.Tests.ThesisSource;

public sealed class AdapterContractTests
{
    // Requires the exact master-owned RustSourceAnalyzer proposal. These tests
    // inspect the actual adapter's interop token, not a worker-cancellation model.
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Started_thesis_interop_retains_actual_promise_while_legacy_contract_stays_unchanged(bool thesis)
    {
        AnalysisSnapshot snapshot;Func<string,string> reply;
        if(thesis){var f=ThesisFixture.Load("manual-positive");snapshot=f.Snapshot();reply=id=>f.Reply(snapshot,id);}
        else {var f=KnuFixture.Load("positive");snapshot=f.Snapshot();reply=id=>f.Reply(snapshot,id);}
        var inner=new KnuProtocolTests.FixtureJs((_,id)=>reply(id));var js=new TokenJs(inner);
        using var cancel=new CancellationTokenSource();await using var analyzer=new RustSourceAnalyzer(js);
        Assert.IsType<AnalysisJobResult.Completed>(await analyzer.AnalyzeAsync(snapshot,cancel.Token));
        Assert.Equal(!thesis,js.SourceToken.CanBeCanceled);
        Assert.Single(inner.Calls);
    }
    private sealed class TokenJs(KnuProtocolTests.FixtureJs inner):IJSRuntime,IJSObjectReference
    {
        internal CancellationToken SourceToken;
        public ValueTask<T> InvokeAsync<T>(string id,object?[]? args)=>InvokeAsync<T>(id,CancellationToken.None,args);
        public async ValueTask<T> InvokeAsync<T>(string id,CancellationToken ct,object?[]? args)
        {
            if(id=="import"){await inner.InvokeAsync<IJSObjectReference>(id,ct,args);return (T)(object)this;}
            if(id=="analyzeSource")SourceToken=ct;
            return await inner.InvokeAsync<T>(id,ct,args);
        }
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
