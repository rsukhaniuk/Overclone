using Client.Compilation;
using Client.Compilation.Lifecycle;
using Xunit;
using static Workspace.Tests.CompilationTestWorkspace;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.CompilationMapPayload;

// Modeled transport, real coordinator/capture/lifecycle/B2a. No native map proof.
public sealed class MapLifecycleTests
{
    private static readonly byte[] Map = [0, 255, 128, 1]; // synthetic boundary-only bytes
    private static CompileOutputEnvelopeInput WithMap(CompileOutputEnvelopeInput input) => input with
    { SourceMapPayload = Map.ToArray(), SourceMapping = new(Hash(Map), Map.Length) };

    [Fact]
    public void Actual_lifecycle_accepts_owned_bytes_and_keeps_them_historical_after_edit() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var ticket = service.Request(Options); var worker = await workers.Next(); var reply = WithMap(worker.Output());
        worker.Reply.SetResult(reply);
        var result = await ticket.Completion; await service.WhenIdle;
        Assert.Equal(CompileLifecycleReason.Succeeded, result.Reason); var accepted = result.Output!;
        Assert.Same(worker.Context, accepted.RequestContext); Assert.Same(ticket.Snapshot, accepted.Snapshot);
        Assert.Same(accepted, service.State.LastSuccessful); Assert.True(service.State.IsCurrent);
        reply.SourceMapPayload![0] ^= 1;
        Assert.Equal(Map, accepted.SourceMapPayloadBytes.ToArray());
        Assert.True(f.Workspace.EditText(f.Main, "changed Україна🙂"));
        Assert.False(service.State.IsCurrent); Assert.Same(accepted, service.State.LastSuccessful);
        Assert.Equal(Map, accepted.ToSourceMapPayloadArray()); Assert.Equal(0, f.Repo.Commits);
    });

    [Fact]
    public void Late_old_map_cannot_replace_new_current_output() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var first = service.Request(Options); var old = await workers.Next(); var oldReply = WithMap(old.Output());
        Assert.True(f.Workspace.EditText(f.Main, "new input🙂"));
        var next = service.Request(Options); var fresh = await workers.Next();
        Assert.NotEqual(old.Context.RequestId, fresh.Context.RequestId);
        fresh.Reply.SetResult(WithMap(fresh.Output())); var output = (await next.Completion).Output!;
        await service.WhenIdle; old.Reply.SetResult(oldReply);
        Assert.Null((await first.Completion).Output); Assert.Same(output, service.State.LastSuccessful);
        Assert.True(service.State.IsCurrent); Assert.Equal(Map, output.ToSourceMapPayloadArray());
    });

    [Fact]
    public void Invalid_map_is_fixed_failure_without_source_or_log_exception_echo() => Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); var workers = new Workers();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, workers, new(4, long.MaxValue), new Clock());
        var ticket = service.Request(Options); var worker = await workers.Next();
        worker.Reply.SetResult(WithMap(worker.Output()) with
        {
            SourceMapping = new(new(new string('0', 64)), Map.Length),
            LogText = "PRIVATE boundary-only source/log sentinel Україна🙂"
        });
        var result = await ticket.Completion; await service.WhenIdle;
        Assert.Equal(CompileLifecycleReason.InvalidOutput, result.Reason);
        Assert.Null(result.Output); Assert.Null(service.State.LastSuccessful);
        Assert.DoesNotContain("PRIVATE", result.SafeMessage); Assert.DoesNotContain("Україна", result.SafeMessage);
        Assert.Equal(0, f.Repo.Commits);
    });
}
