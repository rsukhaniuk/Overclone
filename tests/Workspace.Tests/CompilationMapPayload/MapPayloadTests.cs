using Client.Compilation;
using Client.Workspace.Domain;
using Xunit;
using static Workspace.Tests.CompilationTestWorkspace;

namespace Workspace.Tests.CompilationMapPayload;

// Synthetic boundary-only bytes, deliberately NOT a SyncTeX/PDF validity fixture.
// Real coordinator, immutable capture and trusted B2a context; no native/Rust proof.
public sealed class MapPayloadTests
{
    private static readonly CompileOutputBudget Limits = new(32, 4096, 4, 64, 256, 512);
    private static readonly byte[] Pdf = [37, 80, 68, 70, 0, 255];
    private static readonly byte[] Map = [0, 255, 128, 13, 10, 1]; // arbitrary opaque bytes
    private static CompileRequestContext Context(CompilationInputSnapshot snapshot, long epoch = 3)
        => CompileRequestContext.Create(snapshot, epoch, 4096,
            new("PDF Україна🙂", "1", null), new("boundary-only", "1", null)).Context!;

    internal static CompileOutputEnvelopeInput Input(CompileRequestContext context, byte[]? map) => new()
    {
        RequestId = context.RequestId, SnapshotIdentity = context.SnapshotIdentity,
        InputContentDigest = context.InputContentDigest, WorkerEpoch = context.WorkerEpoch,
        Status = CompileOutputStatus.Succeeded, Payload = Pdf.ToArray(),
        ClaimedLength = Pdf.Length, ClaimedHash = Hash(Pdf),
        OutputFormat = context.OutputFormat, OutputAdapter = context.OutputAdapter,
        SourceMapPayload = map, SourceMapping = map is null ? null : new(Hash(map), map.LongLength)
    };

    private static CompileOutputEnvelope Envelope(CompileOutputEnvelopeInput input, CompileOutputBudget? limits = null)
    {
        var result = CompileOutputEnvelope.Create(input, limits ?? Limits);
        Assert.Null(result.Failure);
        return Assert.IsType<CompileOutputEnvelope>(result.Envelope);
    }

    [Fact]
    public async Task Opaque_map_is_owned_with_its_pdf_and_only_exposed_after_trusted_acceptance()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot(); var context = Context(snapshot); var before = f.State();
        var caller = Map.ToArray(); var input = Input(context, caller); var envelope = Envelope(input);
        caller[0] ^= 1; // after creation, BEFORE acceptance
        var first = envelope.TryAccept(context).Output!;
        Assert.True(first.HasSourceMapPayload);
        Assert.Same(context, first.RequestContext); Assert.Same(snapshot, first.Snapshot);
        Assert.Equal(Map, first.SourceMapPayloadBytes.ToArray()); Assert.Equal(Pdf, first.PayloadBytes.ToArray());
        Assert.Equal(new CompileSourceMappingIdentity(Hash(Map), Map.Length), first.SourceMapping);
        var copy = first.ToSourceMapPayloadArray()!; Assert.NotSame(caller, copy); copy[1] ^= 1;
        var second = envelope.TryAccept(context).Output!;
        var secondCopy = second.ToSourceMapPayloadArray()!; Assert.NotSame(copy, secondCopy); secondCopy[2] ^= 1;
        Assert.Equal(Map, first.SourceMapPayloadBytes.ToArray()); Assert.Equal(Map, second.SourceMapPayloadBytes.ToArray());
        Assert.Equal(first.ContentIdentity, second.ContentIdentity);
        Assert.DoesNotContain(typeof(CompileOutputEnvelope).GetProperties(), p =>
            p.PropertyType == typeof(byte[]) || p.PropertyType == typeof(ReadOnlySpan<byte>));
        Assert.Null(typeof(CompileOutputEnvelope).GetMethod("ToSourceMapPayloadArray"));
        Assert.Equal(before, f.State()); Assert.Equal(0, f.Repo.Commits);
    }

    [Fact]
    public async Task Mutation_before_creation_is_checked_against_the_claim_not_silently_rehashed_as_authority()
    {
        await using var f = await CompilationTestWorkspace.Open(); var context = Context(await f.Snapshot());
        var caller = Map.ToArray(); var input = Input(context, caller); caller[0] ^= 1;
        var result = CompileOutputEnvelope.Create(input, Limits);
        Assert.Null(result.Envelope); Assert.Equal(CompileOutputIssue.SourceMapHashMismatch, result.Failure);
        // A producer may explicitly supply a new identity for changed bytes.
        var corrected = Envelope(input with { SourceMapping = new(Hash(caller), caller.Length) }).TryAccept(context).Output!;
        Assert.Equal(caller, corrected.SourceMapPayloadBytes.ToArray());
    }

    [Fact]
    public async Task Null_and_empty_success_mean_absence_without_changing_identity_or_metadata_measure()
    {
        await using var f = await CompilationTestWorkspace.Open(); var context = Context(await f.Snapshot());
        var legacy = Envelope(Input(context, null));
        var empty = Envelope(Input(context, null) with { SourceMapPayload = [] });
        foreach (var envelope in new[] { legacy, empty })
        {
            var accepted = envelope.TryAccept(context).Output!;
            Assert.False(accepted.HasSourceMapPayload); Assert.Empty(accepted.SourceMapPayloadBytes.ToArray());
            Assert.Null(accepted.ToSourceMapPayloadArray());
        }
        Assert.Equal(legacy.ContentIdentity, empty.ContentIdentity);
        Assert.Equal(legacy.MetadataByteLength, empty.MetadataByteLength);
    }

    [Fact]
    public async Task Identity_only_legacy_mapping_is_valid_even_when_its_length_exceeds_the_byte_cap()
    {
        await using var f = await CompilationTestWorkspace.Open(); var context = Context(await f.Snapshot());
        foreach (long length in new[] { 0L, 1L, long.MaxValue })
        {
            var mapping = new CompileSourceMappingIdentity(Hash(Map), length);
            var input = Input(context, null) with { SourceMapping = mapping };
            var legacy = Envelope(input, Limits with { MaxSourceMapPayloadBytes = 1 });
            var empty = Envelope(input with { SourceMapPayload = [] }, Limits with { MaxSourceMapPayloadBytes = 1 });
            Assert.Equal(legacy.ContentIdentity, empty.ContentIdentity);
            var accepted = legacy.TryAccept(context).Output!;
            Assert.Equal(mapping, accepted.SourceMapping); Assert.False(accepted.HasSourceMapPayload);
        }
    }

    [Fact]
    public async Task Byte_presence_adds_no_second_content_identity_component_or_metadata_charge()
    {
        await using var f = await CompilationTestWorkspace.Open(); var context = Context(await f.Snapshot());
        var input = Input(context, Map);
        var owned = Envelope(input); var metadataOnly = Envelope(input with { SourceMapPayload = null });
        Assert.Equal(metadataOnly.ContentIdentity, owned.ContentIdentity);
        Assert.Equal(metadataOnly.MetadataByteLength, owned.MetadataByteLength);
        var changedBytes = Map.ToArray(); changedBytes[0] ^= 1;
        var changed = Envelope(Input(context, changedBytes));
        Assert.NotEqual(owned.ContentIdentity, changed.ContentIdentity);
    }

    [Fact]
    public void Six_argument_budget_construction_and_deconstruction_keep_their_original_shape()
    {
        var budget = new CompileOutputBudget(1, 2, 3, 4, 5, 6);
        var (pdf, metadata, diagnostics, code, message, log) = budget;
        Assert.Equal((1, 2, 3, 4, 5, 6), (pdf, metadata, diagnostics, code, message, log));
        Assert.Equal(1_048_575, budget.MaxSourceMapPayloadBytes); Assert.True(budget.IsValid);
        Assert.Equal(19, (int)CompileOutputIssue.MappingMismatch); // existing numeric values preserved
        Assert.Equal(20, (int)CompileOutputIssue.OverSourceMapPayloadBudget);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(1_048_576)] [InlineData(int.MaxValue)]
    public async Task Invalid_map_caps_are_typed_even_without_a_map(int cap)
    {
        await using var f = await CompilationTestWorkspace.Open(); var context = Context(await f.Snapshot());
        var budget = Limits with { MaxSourceMapPayloadBytes = cap }; Assert.False(budget.IsValid);
        var result = CompileOutputEnvelope.Create(Input(context, null), budget);
        Assert.Null(result.Envelope); Assert.Equal(CompileOutputIssue.InvalidBudget, result.Failure);
    }

    [Theory]
    [InlineData(1)] [InlineData(6)] [InlineData(1_048_575)]
    public async Task Exact_map_cap_is_accepted_independently_of_small_pdf_and_metadata_caps(int cap)
    {
        await using var f = await CompilationTestWorkspace.Open(); var context = Context(await f.Snapshot());
        var map = new byte[cap]; map[^1] = 255;
        var input = Input(context, map); var initial = Envelope(input, Limits with { MaxSourceMapPayloadBytes = cap });
        var budget = Limits with { MaxSourceMapPayloadBytes = cap, MaxPayloadBytes = Pdf.Length, MaxMetadataBytes = checked((int)initial.MetadataByteLength) };
        var accepted = Envelope(input, budget).TryAccept(context).Output!;
        Assert.Equal(cap, accepted.SourceMapPayloadBytes.Length); Assert.Equal(map, accepted.ToSourceMapPayloadArray());
        Assert.Equal(CompileOutputIssue.OverPayloadBudget,
            CompileOutputEnvelope.Create(input, budget with { MaxPayloadBytes = Pdf.Length - 1 }).Failure);
        Assert.Equal(CompileOutputIssue.OverMetadataBudget,
            CompileOutputEnvelope.Create(input, budget with { MaxMetadataBytes = budget.MaxMetadataBytes - 1 }).Failure);
    }

    [Theory]
    [InlineData(1)] [InlineData(1_048_575)]
    public async Task Excess_map_is_rejected_before_copy_hash_or_metadata_walk(int cap)
    {
        await using var f = await CompilationTestWorkspace.Open(); var context = Context(await f.Snapshot());
        var map = new byte[cap + 1]; var metadata = new NoWalkDiagnostics();
        // Valid PDF shape but deliberately wrong PDF/map hashes and malformed log.
        // A map-size preflight must win before any expensive PDF/map work or text scan.
        var input = Input(context, null) with
        {
            SourceMapPayload = map, SourceMapping = new(new(new string('0', 64)), long.MaxValue),
            ClaimedHash = new(new string('0', 64)), Diagnostics = metadata, LogText = "\ud800"
        };
        var budget = Limits with { MaxSourceMapPayloadBytes = cap };
        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = CompileOutputEnvelope.Create(input, budget);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Null(result.Envelope); Assert.Equal(CompileOutputIssue.OverSourceMapPayloadBudget, result.Failure);
        Assert.Equal(0, metadata.Walks);
        if (cap == 1_048_575) Assert.True(allocated < map.Length, "Over-limit rejection must not allocate a map-sized copy.");
    }

    [Theory]
    [InlineData("absent", CompileOutputIssue.MappingMismatch)]
    [InlineData("negative", CompileOutputIssue.SourceMapLengthMismatch)]
    [InlineData("zero", CompileOutputIssue.SourceMapLengthMismatch)]
    [InlineData("short", CompileOutputIssue.SourceMapLengthMismatch)]
    [InlineData("long", CompileOutputIssue.SourceMapLengthMismatch)]
    [InlineData("overflow", CompileOutputIssue.SourceMapLengthMismatch)]
    [InlineData("forged", CompileOutputIssue.SourceMapHashMismatch)]
    [InlineData("null-hash", CompileOutputIssue.InvalidHash)]
    [InlineData("unicode-hash", CompileOutputIssue.InvalidHash)]
    [InlineData("uppercase-hash", CompileOutputIssue.InvalidHash)]
    public async Task Nonempty_bytes_require_exact_mapping_length_and_lowercase_sha256(string mode, CompileOutputIssue issue)
    {
        await using var f = await CompilationTestWorkspace.Open(); var context = Context(await f.Snapshot());
        var input = Input(context, Map); var mapping = input.SourceMapping!;
        input = input with { SourceMapping = mode switch
        {
            "absent" => null, "negative" => mapping with { ByteLength = -1 }, "zero" => mapping with { ByteLength = 0 },
            "short" => mapping with { ByteLength = Map.Length - 1 }, "long" => mapping with { ByteLength = Map.Length + 1 },
            "overflow" => mapping with { ByteLength = long.MaxValue }, "forged" => mapping with { Hash = new(new string('0', 64)) },
            "null-hash" => mapping with { Hash = new(null!) }, "unicode-hash" => mapping with { Hash = new("Україна🙂\ud800") },
            "uppercase-hash" => mapping with { Hash = new(Hash(Map).Hex.ToUpperInvariant()) }, _ => throw new InvalidOperationException()
        } };
        var result = CompileOutputEnvelope.Create(input, Limits);
        Assert.Null(result.Envelope); Assert.Equal(issue, result.Failure);
    }

    [Theory]
    [InlineData(CompileOutputStatus.Failed)] [InlineData(CompileOutputStatus.Cancelled)]
    [InlineData(CompileOutputStatus.TimedOut)] [InlineData(CompileOutputStatus.Unsupported)]
    public async Task Non_success_never_owns_map_bytes_but_preserves_legacy_metadata(CompileOutputStatus status)
    {
        await using var f = await CompilationTestWorkspace.Open(); var context = Context(await f.Snapshot());
        var input = Input(context, Map) with { Status = status, Payload = null, ClaimedHash = null, ClaimedLength = 0 };
        foreach (var bytes in new[] { Map, Array.Empty<byte>() })
        {
            var result = CompileOutputEnvelope.Create(input with { SourceMapPayload = bytes }, Limits);
            Assert.Null(result.Envelope); Assert.Equal(CompileOutputIssue.PayloadOnNonSuccess, result.Failure);
        }
        var accepted = Envelope(input with { SourceMapPayload = null }).TryAccept(context).Output!;
        Assert.Equal(status, accepted.Status); Assert.NotNull(accepted.SourceMapping); Assert.Null(accepted.ContentIdentity);
        Assert.False(accepted.HasSourceMapPayload); Assert.Null(accepted.ToSourceMapPayloadArray());
        Assert.Null(accepted.ToPayloadArray());
    }

    [Theory]
    [InlineData("request", CompileOutputIssue.RequestMismatch)]
    [InlineData("capture", CompileOutputIssue.SnapshotMismatch)]
    [InlineData("source", CompileOutputIssue.DigestMismatch)]
    [InlineData("epoch", CompileOutputIssue.EpochMismatch)]
    [InlineData("format", CompileOutputIssue.FormatMismatch)]
    [InlineData("adapter", CompileOutputIssue.AdapterMismatch)]
    public async Task Map_bytes_cannot_bypass_expected_context_correlation(string mode, CompileOutputIssue issue)
    {
        await using var f = await CompilationTestWorkspace.Open(); var context = Context(await f.Snapshot());
        var input = Input(context, Map);
        input = mode switch
        {
            "request" => input with { RequestId = CompileRequestId.New() },
            "capture" => input with { SnapshotIdentity = context.SnapshotIdentity with { CaptureId = Guid.NewGuid() } },
            "source" => input with { InputContentDigest = new(new string('0', 64)) },
            "epoch" => input with { WorkerEpoch = context.WorkerEpoch + 1 },
            "format" => input with { OutputFormat = context.OutputFormat! with { Id = "different" } },
            "adapter" => input with { OutputAdapter = context.OutputAdapter! with { Id = "different" } }, _ => throw new InvalidOperationException()
        };
        var envelope = Envelope(input); var acceptance = envelope.TryAccept(context);
        Assert.Null(acceptance.Output); Assert.Equal(issue, acceptance.Failure);
        Assert.Equal(CompileOutputIssue.InvalidMetadata, envelope.TryAccept(null!).Failure);
    }

    [Fact]
    public async Task New_capture_and_actual_source_edit_do_not_reauthorize_an_old_map()
    {
        await using var f = await CompilationTestWorkspace.Open(); var snapshot = await f.Snapshot();
        var old = Context(snapshot); var envelope = Envelope(Input(old, Map));
        var recaptured = Context(await f.Snapshot());
        Assert.Equal(old.InputContentDigest, recaptured.InputContentDigest);
        Assert.NotEqual(old.SnapshotIdentity.CaptureId, recaptured.SnapshotIdentity.CaptureId);
        Assert.Equal(CompileOutputIssue.RequestMismatch, envelope.TryAccept(recaptured).Failure);
        Assert.True(f.Workspace.EditText(f.Main, "нові джерела🙂")); var changed = Context(await f.Snapshot(), 4);
        Assert.NotEqual(old.InputContentDigest, changed.InputContentDigest);
        Assert.Null(envelope.TryAccept(changed).Output);
        // Acceptance is correlation, not currentness. An old exact context remains
        // acceptable here; lifecycle/workspace freshness is a separate existing gate.
        Assert.Equal(Map, envelope.TryAccept(old).Output!.SourceMapPayloadBytes.ToArray());
        Assert.Equal(0, f.Repo.Commits);
    }

    private sealed class NoWalkDiagnostics : IReadOnlyList<CompileDiagnosticEntry>
    {
        internal int Walks;
        public int Count => 1;
        public CompileDiagnosticEntry this[int index] { get { Walks++; throw new InvalidOperationException("must not walk"); } }
        public IEnumerator<CompileDiagnosticEntry> GetEnumerator() { Walks++; throw new InvalidOperationException("must not walk"); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
