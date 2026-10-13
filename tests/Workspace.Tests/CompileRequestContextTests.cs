using Client.Compilation;
using Client.Workspace.Domain;
using Xunit;
using static Workspace.Tests.CompilationTestWorkspace;

namespace Workspace.Tests;

public sealed class CompileRequestContextTests
{
    private const int DescriptorBudget = 4096;
    private static CompileOutputDescriptor Out(string id = "pdf", string version = "1.0", string? hash = null)
        => new(id, version, new SHA256(hash ?? new string('a', 64)));

    [Theory]
    [InlineData(0, 1, 1, 1, 1, 1)]
    [InlineData(-1, 1, 1, 1, 1, 1)]
    [InlineData(1, 0, 1, 1, 1, 1)]
    [InlineData(1, -1, 1, 1, 1, 1)]
    [InlineData(1, 1, 0, 1, 1, 1)]
    [InlineData(1, 1, -1, 1, 1, 1)]
    [InlineData(1, 1, 1, 0, 1, 1)]
    [InlineData(1, 1, 1, 1, 0, 1)]
    [InlineData(1, 1, 1, 1, 1, 0)]
    public void Output_budgets_are_explicit_positive_representable_values(int p, int m, int d, int dc, int dm, int log)
        => Assert.False(new CompileOutputBudget(p, m, d, dc, dm, log).IsValid);

    [Fact]
    public void Budgets_use_arraymaxlength_bounds_not_intmaxvalue()
    {
        // Array.MaxLength (0x7FFFFFC7 = 2147483591) is strictly less than int.MaxValue
        // (2147483647): every capped field rejects int.MaxValue and accepts Array.MaxLength.
        Assert.True(new CompileOutputBudget(Array.MaxLength, Array.MaxLength, Array.MaxLength, Array.MaxLength, Array.MaxLength, Array.MaxLength).IsValid);
        Assert.False(new CompileOutputBudget(int.MaxValue, 1, 1, 1, 1, 1).IsValid);
        Assert.False(new CompileOutputBudget(1, int.MaxValue, 1, 1, 1, 1).IsValid);
        Assert.False(new CompileOutputBudget(1, 1, int.MaxValue, 1, 1, 1).IsValid);
        Assert.False(new CompileOutputBudget(1, 1, 1, int.MaxValue, 1, 1).IsValid);
        Assert.False(new CompileOutputBudget(1, 1, 1, 1, int.MaxValue, 1).IsValid);
        Assert.False(new CompileOutputBudget(1, 1, 1, 1, 1, int.MaxValue).IsValid);
    }

    [Fact]
    public async Task Descriptor_metadata_budget_is_explicit_at_the_caller_no_hidden_default()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        Assert.Equal(CompileOutputIssue.InvalidBudget, CompileRequestContext.Create(snapshot, 1, 0).Failure);
        Assert.Null(CompileRequestContext.Create(snapshot, 1, int.MaxValue).Context);
        var ctx = CompileRequestContext.Create(snapshot, workerEpoch: 7, maxDescriptorMetadataBytes: DescriptorBudget).Context!;
        Assert.NotEqual(Guid.Empty, ctx.RequestId.Value);
        Assert.Same(snapshot, ctx.Snapshot);
        Assert.Equal(snapshot.Identity, ctx.SnapshotIdentity);
        Assert.Equal(snapshot.ContentDigest, ctx.InputContentDigest);
        Assert.Equal(7, ctx.WorkerEpoch);
        Assert.Null(ctx.OutputFormat);
        Assert.Null(ctx.OutputAdapter);
        Assert.True(ctx.DescriptorMetadataByteLength > 0);
    }

    [Fact]
    public async Task Repeated_contexts_over_the_same_snapshot_have_distinct_fresh_request_ids()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var a = CompileRequestContext.Create(snapshot, 1, DescriptorBudget).Context!;
        var b = CompileRequestContext.Create(snapshot, 1, DescriptorBudget).Context!;
        Assert.NotEqual(a.RequestId, b.RequestId);
        Assert.Equal(a.InputContentDigest, b.InputContentDigest); // same content, distinct request ids
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Nonpositive_worker_epoch_is_an_explicit_budget_failure(long epoch)
        => Assert.Equal(CompileOutputIssue.InvalidBudget, CompileRequestContext.Create(await SnapshotFixture(), epoch, DescriptorBudget).Failure);

    [Fact]
    public async Task Null_snapshot_is_invalid_metadata()
        => Assert.Null(CompileRequestContext.Create(null!, 1, DescriptorBudget).Context);

    [Theory]
    [InlineData("id")]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("uppercase-hash")]
    [InlineData("surrogate")]
    [InlineData("control")]
    public async Task Invalid_request_descriptors_are_typed_failures(string change)
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        CompileOutputDescriptor? desc = change switch
        {
            "id" => Out(id: ""),
            "version" => Out(version: " "),
            "hash" => Out(hash: "bad"),
            "uppercase-hash" => Out(hash: new string('A', 64)),
            "surrogate" => Out(id: "\ud800"),
            "control" => Out(id: "bad\nid"),
            _ => throw new InvalidOperationException()
        };
        var failure = CompileRequestContext.Create(snapshot, 1, DescriptorBudget, outputFormat: desc).Failure;
        Assert.True(failure is CompileOutputIssue.InvalidMetadata or CompileOutputIssue.InvalidHash or CompileOutputIssue.MalformedUnicode);
    }

    [Fact]
    public async Task Request_context_owns_descriptors_independent_of_caller_records()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var format = Out("pdf", "1.0");
        var ctx = CompileRequestContext.Create(snapshot, 1, DescriptorBudget, outputFormat: format, outputAdapter: Out("native", "0.3")).Context!;
        // Records are immutable; re-binding the caller variable cannot retarget.
        format = format with { Id = "changed" };
        Assert.Equal("pdf", ctx.OutputFormat!.Id);
        Assert.Equal("native", ctx.OutputAdapter!.Id);
    }

    [Fact]
    public async Task Request_context_does_not_copy_snapshot_files_or_invent_selection()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var ctx = CompileRequestContext.Create(snapshot, 1, DescriptorBudget).Context!;
        Assert.Same(snapshot.Selection, ctx.Snapshot.Selection); // authoritative, not a competing copy
        Assert.Equal(snapshot.Files.Length, ctx.Snapshot.Files.Length);
        Assert.Equal(0, f.Repo.Commits); // no save/capture side effect from this boundary
        Assert.Equal(0, f.Lock.Queries);
    }

    private static async Task<CompilationInputSnapshot> SnapshotFixture()
    { await using var f = await CompilationTestWorkspace.Open(); return await f.Snapshot(); }
}
