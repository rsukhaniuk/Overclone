using Client.Compilation;
using Client.Workspace.Domain;
using Xunit;
using static Workspace.Tests.CompilationTestWorkspace;

namespace Workspace.Tests;

public sealed class CompileOutputEnvelopeTests
{
    private const int DescriptorBudget = 4096;
    private static readonly CompileOutputBudget Budget = new(1024, 4096, 16, 64, 256, 512);
    private static readonly byte[] Pdf = Bytes("%PDF-1.4 synthetic, NOT a validity proof\n");
    private static SHA256 Hash(byte[] b) => SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(b));

    private static CompileRequestContext Context(CompilationInputSnapshot snapshot, long epoch = 3,
        CompileOutputDescriptor? format = null, CompileOutputDescriptor? adapter = null)
        => CompileRequestContext.Create(snapshot, epoch, DescriptorBudget, format, adapter).Context!;

    private static CompileOutputEnvelopeInput SuccessInput(CompileRequestContext ctx,
        CompileOutputDescriptor? format = null, CompileOutputDescriptor? adapter = null,
        CompileSourceMappingIdentity? mapping = null) => new()
        {
            RequestId = ctx.RequestId,
            SnapshotIdentity = ctx.SnapshotIdentity,
            InputContentDigest = ctx.InputContentDigest,
            WorkerEpoch = ctx.WorkerEpoch,
            Status = CompileOutputStatus.Succeeded,
            Payload = Pdf,
            ClaimedLength = Pdf.Length,
            ClaimedHash = Hash(Pdf),
            OutputFormat = format,
            OutputAdapter = adapter,
            SourceMapping = mapping
        };

    private static CompileOutputEnvelopeInput NonSuccessInput(CompileRequestContext ctx, CompileOutputStatus status,
        IReadOnlyList<CompileDiagnosticEntry>? diagnostics = null, string? log = null) => new()
        {
            RequestId = ctx.RequestId,
            SnapshotIdentity = ctx.SnapshotIdentity,
            InputContentDigest = ctx.InputContentDigest,
            WorkerEpoch = ctx.WorkerEpoch,
            Status = status,
            Diagnostics = diagnostics,
            LogText = log
        };

    [Fact]
    public async Task Invalid_budget_null_input_and_unknown_status_are_typed_failures()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var ctx = Context(snapshot);
        Assert.Null(CompileOutputEnvelope.Create(null!, Budget).Envelope);
        Assert.Null(CompileOutputEnvelope.Create(SuccessInput(ctx), null!).Envelope);
        Assert.Equal(CompileOutputIssue.InvalidBudget, CompileOutputEnvelope.Create(SuccessInput(ctx), new(0, 1, 1, 1, 1, 1)).Failure);
        var bad = SuccessInput(ctx) with { Status = (CompileOutputStatus)99 };
        Assert.Equal(CompileOutputIssue.InvalidStatus, CompileOutputEnvelope.Create(bad, Budget).Failure);
    }

    [Fact]
    public async Task Null_snapshot_identity_is_a_typed_mismatch_without_throwing_or_copying()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var ctx = Context(snapshot);
        var input = SuccessInput(ctx) with { SnapshotIdentity = null! };
        var result = CompileOutputEnvelope.Create(input, Budget);
        Assert.Equal(CompileOutputIssue.SnapshotMismatch, result.Failure);
        Assert.Null(result.Envelope);
        Assert.Equal(0, f.Repo.Commits); // no payload copy/hash or metadata scan reached storage
    }

    [Fact]
    public async Task Invalid_snapshot_identity_fields_are_typed_mismatch_before_expensive_work()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var ctx = Context(snapshot);
        // Each invariant is a distinct typed SnapshotMismatch; none reach payload copy/hash.
        Assert.Equal(CompileOutputIssue.SnapshotMismatch,
            CompileOutputEnvelope.Create(SuccessInput(ctx) with { SnapshotIdentity = ctx.SnapshotIdentity with { CaptureId = Guid.Empty } }, Budget).Failure);
        Assert.Equal(CompileOutputIssue.SnapshotMismatch,
            CompileOutputEnvelope.Create(SuccessInput(ctx) with { SnapshotIdentity = ctx.SnapshotIdentity with { WorkingGeneration = -1 } }, Budget).Failure);
        Assert.Equal(CompileOutputIssue.SnapshotMismatch,
            CompileOutputEnvelope.Create(SuccessInput(ctx) with { SnapshotIdentity = ctx.SnapshotIdentity with { SelectionVersion = -1 } }, Budget).Failure);
        // Empty non-null base-head id is invalid; a genuine head-null snapshot stays valid.
        Assert.Equal(CompileOutputIssue.SnapshotMismatch,
            CompileOutputEnvelope.Create(SuccessInput(ctx) with { SnapshotIdentity = ctx.SnapshotIdentity with { BaseHeadRevisionId = new(Guid.Empty) } }, Budget).Failure);
        var headNull = ctx.SnapshotIdentity with { BaseHeadRevisionId = null };
        Assert.NotNull(CompileOutputEnvelope.Create(SuccessInput(ctx) with { SnapshotIdentity = headNull }, Budget).Envelope);
    }

    [Fact]
    public async Task Success_envelope_is_intrinsically_valid_but_not_yet_accepted()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var ctx = Context(snapshot);
        var env = CompileOutputEnvelope.Create(SuccessInput(ctx), Budget).Envelope!;
        Assert.Equal(CompileOutputStatus.Succeeded, env.Status);
        Assert.Equal(Pdf.Length, env.OutputLength);
        Assert.Equal(Hash(Pdf), env.OutputHash);
        Assert.NotNull(env.ContentIdentity);
        // Not exposed until accepted: no public payload accessor on the untrusted envelope.
        Assert.Null(env.GetType().GetProperty("PayloadBytes")?.GetMethod);
        Assert.Null(env.GetType().GetProperty("OwnedPayload")?.GetMethod);
    }

    [Fact]
    public async Task Accepted_success_exposes_owned_payload_and_defensive_copies()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var ctx = Context(snapshot);
        var payload = Pdf.ToArray(); // local caller array, distinct from shared Pdf
        var input = SuccessInput(ctx) with { Payload = payload, ClaimedLength = payload.Length, ClaimedHash = Hash(payload) };
        var envelope = CompileOutputEnvelope.Create(input, Budget).Envelope!;
        var accepted = envelope.TryAccept(ctx).Output!;
        Assert.Same(ctx, accepted.RequestContext);
        Assert.Same(snapshot, accepted.Snapshot);
        Assert.Equal(ctx.RequestId, accepted.RequestId);
        Assert.Equal(Pdf, accepted.PayloadBytes.ToArray());
        var copy = accepted.ToPayloadArray()!;
        Assert.NotSame(payload, copy);
        copy[0] ^= 1; payload[0] ^= 1; // mutate returned copy and caller array AFTER create
        Assert.Equal(Pdf, accepted.PayloadBytes.ToArray()); // owned backing unchanged
        Assert.Equal(0, f.Repo.Commits); // no capture/save/analysis side effect
    }

    [Fact]
    public async Task Post_call_payload_mutation_cannot_retarget_owned_envelope()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var ctx = Context(snapshot);
        var payload = Pdf.ToArray();
        var input = SuccessInput(ctx) with { Payload = payload, ClaimedLength = payload.Length, ClaimedHash = Hash(payload) };
        var envelope = CompileOutputEnvelope.Create(input, Budget).Envelope!;
        payload[0] = 0; // mutate caller array AFTER create
        var accepted = envelope.TryAccept(ctx).Output!;
        Assert.Equal(Pdf, accepted.PayloadBytes.ToArray());
    }

    [Theory]
    [InlineData(CompileOutputStatus.Failed)]
    [InlineData(CompileOutputStatus.Cancelled)]
    [InlineData(CompileOutputStatus.TimedOut)]
    [InlineData(CompileOutputStatus.Unsupported)]
    public async Task Non_success_may_carry_diagnostics_but_never_a_payload(CompileOutputStatus status)
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var ctx = Context(snapshot);
        var input = NonSuccessInput(ctx, status,
            diagnostics: [new("CompileFailed", "synthetic bounded log line")], log: "synthetic log");
        var envelope = CompileOutputEnvelope.Create(input, Budget).Envelope!;
        var accepted = envelope.TryAccept(ctx).Output!;
        Assert.Same(ctx, accepted.RequestContext); // non-success also retains trusted provenance
        Assert.Equal(status, accepted.Status);
        Assert.Null(accepted.ContentIdentity);
        Assert.Null(accepted.OutputHash);
        Assert.Equal(0, accepted.OutputLength);
        Assert.Empty(accepted.PayloadBytes.ToArray()); // no accepted artifact
        Assert.Null(accepted.ToPayloadArray());
        Assert.Single(accepted.Diagnostics);
        Assert.Equal("synthetic log", accepted.LogText);
    }

    [Fact]
    public async Task Non_success_with_payload_is_rejected_before_acceptance()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var ctx = Context(snapshot);
        var input = NonSuccessInput(ctx, CompileOutputStatus.Failed) with { Payload = Pdf };
        Assert.Equal(CompileOutputIssue.PayloadOnNonSuccess, CompileOutputEnvelope.Create(input, Budget).Failure);
    }

    [Fact]
    public async Task Success_without_payload_or_hash_is_rejected()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var ctx = Context(snapshot);
        var input = SuccessInput(ctx) with { Payload = null };
        Assert.Equal(CompileOutputIssue.MissingPayload, CompileOutputEnvelope.Create(input, Budget).Failure);
        input = SuccessInput(ctx) with { ClaimedHash = null };
        Assert.Equal(CompileOutputIssue.InvalidHash, CompileOutputEnvelope.Create(input, Budget).Failure);
    }

    [Fact]
    public async Task Length_and_hash_corruption_are_separate_typed_failures()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var ctx = Context(snapshot);
        var input = SuccessInput(ctx) with { ClaimedLength = Pdf.Length + 1 };
        Assert.Equal(CompileOutputIssue.LengthMismatch, CompileOutputEnvelope.Create(input, Budget).Failure);
        input = SuccessInput(ctx) with { ClaimedHash = new(new string('0', 64)) };
        Assert.Equal(CompileOutputIssue.HashMismatch, CompileOutputEnvelope.Create(input, Budget).Failure);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("metadata")]
    [InlineData("diagnostics")]
    [InlineData("log")]
    public async Task Over_budget_payload_and_metadata_are_rejected_before_copy_or_scan(string which)
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var ctx = Context(snapshot);
        CompileOutputEnvelopeInput input = which switch
        {
            "payload" => SuccessInput(ctx) with { Payload = new byte[1025], ClaimedLength = 1025, ClaimedHash = Hash(new byte[1025]) },
            "metadata" => SuccessInput(ctx) with { Diagnostics = Enumerable.Range(0, 17).Select(i => new CompileDiagnosticEntry("c" + i, "m")).ToArray() },
            "diagnostics" => SuccessInput(ctx) with { Diagnostics = [new CompileDiagnosticEntry(new string('x', 65), "m")] },
            "log" => SuccessInput(ctx) with { LogText = new string('L', 513) },
            _ => throw new InvalidOperationException()
        };
        var failure = CompileOutputEnvelope.Create(input, Budget).Failure;
        Assert.Equal(which switch
        {
            "payload" => CompileOutputIssue.OverPayloadBudget,
            "metadata" => CompileOutputIssue.OverDiagnosticCount,
            "diagnostics" => CompileOutputIssue.OverMetadataBudget,
            "log" => CompileOutputIssue.OverMetadataBudget,
            _ => throw new InvalidOperationException()
        }, failure);
    }

    [Fact]
    public async Task Over_diagnostic_count_is_rejected_before_walking_entries_or_copying_payload()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var ctx = Context(snapshot);
        // Hostile list: Count=2 (safe) but indexer/enumerator throw if touched. MaxDiagnostics=1.
        // Create must return OverDiagnosticCount without reading any entry or copying payload.
        var hostile = new HostileDiagnosticList();
        var input = NonSuccessInput(ctx, CompileOutputStatus.Failed, diagnostics: hostile, log: "x");
        Assert.Equal(CompileOutputIssue.OverDiagnosticCount, CompileOutputEnvelope.Create(input, Budget with { MaxDiagnostics = 1 }).Failure);
        Assert.Equal(0, hostile.Accesses); // neither indexer nor enumerator was touched
        Assert.Equal(0, f.Repo.Commits);
    }

    [Fact]
    public async Task Per_field_byte_boundaries_use_exact_utf8_lengths()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var ctx = Context(snapshot);
        string k = "Київ", smiley = "🙂", ua = "Україна"; // Київ=8 bytes, 🙂=4 bytes, Україна=14 bytes
        string emoji64 = string.Concat(Enumerable.Repeat(smiley, 64));   // 256 bytes
        string emoji65 = string.Concat(Enumerable.Repeat(smiley, 65));   // 260 bytes
        (string Name, string Value, int Code, int Message, int Log, bool Ok)[] cases =
        [
            ("code-ascii-at", new string('x', 64), 64, 256, 512, true),
            ("code-ascii-over", new string('x', 65), 64, 256, 512, false),
            ("code-ukrainian-at", string.Concat(Enumerable.Repeat(k, 8)), 64, 256, 512, true),    // 64 bytes
            ("code-ukrainian-over", string.Concat(Enumerable.Repeat(k, 9)), 64, 256, 512, false),  // 72 bytes
            ("code-emoji-at", string.Concat(Enumerable.Repeat(smiley, 16)), 64, 256, 512, true),  // 64 bytes
            ("code-emoji-over", string.Concat(Enumerable.Repeat(smiley, 17)), 64, 256, 512, false), // 68 bytes
            ("message-emoji-at", emoji64, 64, 256, 512, true),
            ("message-emoji-over", emoji65, 64, 256, 512, false),
            ("log-ascii-at", new string('L', 512), 64, 256, 512, true),
            ("log-ukrainian-over", string.Concat(Enumerable.Repeat(ua, 37)), 64, 256, 512, false), // 518 bytes
        ];
        foreach (var (name, value, code, message, log, ok) in cases)
        {
            var b = new CompileOutputBudget(1024, 4096, 16, code, message, log);
            var input = NonSuccessInput(ctx, CompileOutputStatus.Failed, diagnostics: [new("code", "m")], log: "ok");
            if (name.StartsWith("code-")) input = input with { Diagnostics = [new(value, "m")] };
            else if (name.StartsWith("message-")) input = input with { Diagnostics = [new("c", value)] };
            else input = input with { LogText = value, Diagnostics = [new("c", "m")] };
            var result = CompileOutputEnvelope.Create(input, b);
            Assert.True(ok == (result.Envelope is not null), $"{name}: expected ok={ok}");
            if (ok)
                Assert.Equal(value, name.StartsWith("code-") ? result.Envelope!.Diagnostics[0].Code
                    : name.StartsWith("message-") ? result.Envelope!.Diagnostics[0].SafeMessage : result.Envelope!.LogText);
            else Assert.Equal(CompileOutputIssue.OverMetadataBudget, result.Failure);
        }
    }

    [Theory]
    [InlineData("surrogate")]
    [InlineData("control")]
    [InlineData("null-entry")]
    [InlineData("null-descriptor-id")]
    [InlineData("null-mapping-hash")]
    public async Task Malformed_or_null_nested_metadata_is_typed(string which)
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var ctx = Context(snapshot);
        CompileOutputEnvelopeInput input = which switch
        {
            "surrogate" => NonSuccessInput(ctx, CompileOutputStatus.Failed, diagnostics: [new("\ud800", "m")]),
            "control" => NonSuccessInput(ctx, CompileOutputStatus.Failed, diagnostics: [new("bad\n", "m")]),
            "null-entry" => NonSuccessInput(ctx, CompileOutputStatus.Failed, diagnostics: [null!]),
            "null-descriptor-id" => SuccessInput(ctx) with { OutputFormat = new(null!, "1.0", null) },
            "null-mapping-hash" => SuccessInput(ctx) with { SourceMapping = new(new(null!), 9) },
            _ => throw new InvalidOperationException()
        };
        var failure = CompileOutputEnvelope.Create(input, Budget).Failure;
        Assert.True(failure is CompileOutputIssue.MalformedUnicode or CompileOutputIssue.InvalidMetadata
            or CompileOutputIssue.InvalidHash);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("snapshot")]
    [InlineData("digest")]
    [InlineData("epoch")]
    [InlineData("format")]
    [InlineData("adapter")]
    public async Task Every_echoed_identity_mismatch_is_a_typed_rejection(string which)
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var ctx = Context(snapshot, format: new("pdf", "1.0", new(new string('a', 64))), adapter: new("native", "0.3", new(new string('a', 64))));
        var input = SuccessInput(ctx, format: ctx.OutputFormat, adapter: ctx.OutputAdapter);
        input = which switch
        {
            "request" => input with { RequestId = CompileRequestId.New() },
            "snapshot" => input with { SnapshotIdentity = snapshot.Identity with { CaptureId = Guid.NewGuid() } },
            "digest" => input with { InputContentDigest = new(new string('0', 64)) },
            "epoch" => input with { WorkerEpoch = ctx.WorkerEpoch + 1 },
            "format" => input with { OutputFormat = ctx.OutputFormat! with { Id = "different" } },
            "adapter" => input with { OutputAdapter = ctx.OutputAdapter! with { Id = "different" } },
            _ => throw new InvalidOperationException()
        };
        var envelope = CompileOutputEnvelope.Create(input, Budget).Envelope!;
        var issue = envelope.TryAccept(ctx).Failure;
        Assert.Equal(which switch
        {
            "request" => CompileOutputIssue.RequestMismatch,
            "snapshot" => CompileOutputIssue.SnapshotMismatch,
            "digest" => CompileOutputIssue.DigestMismatch,
            "epoch" => CompileOutputIssue.EpochMismatch,
            "format" => CompileOutputIssue.FormatMismatch,
            "adapter" => CompileOutputIssue.AdapterMismatch,
            _ => throw new InvalidOperationException()
        }, issue);
    }

    [Fact]
    public async Task Late_response_for_old_request_cannot_satisfy_newer_request()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var older = Context(snapshot, epoch: 1);
        var newer = Context(snapshot, epoch: 2);
        Assert.NotEqual(older.RequestId, newer.RequestId);
        var late = CompileOutputEnvelope.Create(SuccessInput(older), Budget).Envelope!;
        // A late reply for the older request id cannot satisfy the newer request;
        // request-id mismatch is reported before any later freshness check.
        Assert.Equal(CompileOutputIssue.RequestMismatch, late.TryAccept(newer).Failure);
        Assert.Null(late.TryAccept(newer).Output);
    }

    [Fact]
    public async Task Duplicate_diagnostic_codes_and_invalid_echoed_identity_are_rejected()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var ctx = Context(snapshot);
        var dup = NonSuccessInput(ctx, CompileOutputStatus.Failed, diagnostics: [new("dup", "a"), new("dup", "b")]);
        Assert.Equal(CompileOutputIssue.InvalidMetadata, CompileOutputEnvelope.Create(dup, Budget).Failure);
        var emptyReq = SuccessInput(ctx) with { RequestId = new(Guid.Empty) };
        Assert.Equal(CompileOutputIssue.RequestMismatch, CompileOutputEnvelope.Create(emptyReq, Budget).Failure);
    }

    [Fact]
    public async Task Accepted_output_preserves_trusted_request_and_snapshot_provenance()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var ctx = Context(snapshot);
        var accepted = CompileOutputEnvelope.Create(SuccessInput(ctx), Budget).Envelope!.TryAccept(ctx).Output!;
        // Provenance is the EXPECTED trusted context/snapshot, by reference, not reconstructed.
        Assert.Same(ctx, accepted.RequestContext);
        Assert.Same(snapshot, accepted.Snapshot);
        Assert.Same(ctx.Snapshot, accepted.Snapshot);
        Assert.Equal(ctx.SnapshotIdentity, accepted.SnapshotIdentity);
        Assert.Equal(ctx.InputContentDigest, accepted.InputContentDigest);
        Assert.Equal(ctx.WorkerEpoch, accepted.WorkerEpoch);
        Assert.Equal(snapshot.Files.Length, accepted.Snapshot.Files.Length);
        Assert.Same(snapshot.Files[0], accepted.Snapshot.Files[0]); // complete file inventory preserved
        Assert.Equal(0, f.Repo.Commits); // no save/capture/analysis side effect
    }

    [Fact]
    public async Task Accepted_provenance_survives_workspace_change_and_factory_disposal()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var ctx = Context(snapshot);
        var accepted = CompileOutputEnvelope.Create(SuccessInput(ctx), Budget).Envelope!.TryAccept(ctx).Output!;
        Assert.True(f.Workspace.EditText(f.Main, "later workspace change")); // input inventory must remain valid
        Assert.Equal(snapshot.Files.Length, accepted.Snapshot.Files.Length);
        Assert.Same(snapshot.Files[0], accepted.Snapshot.Files[0]);
        await f.Capture.DisposeAsync();
        Assert.Same(snapshot, accepted.Snapshot); // immutable owned snapshot survives factory disposal
        Assert.Equal(snapshot.ContentDigest, accepted.InputContentDigest); // correlation, not currentness
    }

    [Fact]
    public async Task Metadata_byte_length_matches_the_documented_v1_encoding()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var ctx = Context(snapshot);
        // overclone-compilation-output-metadata-v1: domain + two absent descriptors + absent mapping
        // + int32 count(1) + one ("c","m") entry + "ok" log. Independently measured in Python.
        var input = NonSuccessInput(ctx, CompileOutputStatus.Failed, diagnostics: [new("c", "m")], log: "ok");
        var envelope = CompileOutputEnvelope.Create(input, Budget).Envelope!;
        Assert.Equal(67, envelope.MetadataByteLength);
    }

    private sealed class HostileDiagnosticList : IReadOnlyList<CompileDiagnosticEntry>
    {
        internal int Accesses;
        public int Count => 2;
        public CompileDiagnosticEntry this[int index] { get { Accesses++; throw new InvalidOperationException("indexer must not be touched"); } }
        public IEnumerator<CompileDiagnosticEntry> GetEnumerator() { Accesses++; throw new InvalidOperationException("enumerator must not be touched"); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
