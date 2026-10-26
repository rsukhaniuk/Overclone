using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Client.Analysis;
using Client.Workspace.Domain;
using Xunit;

namespace Workspace.Tests.ThesisFormatting;

// Root generates these via the actual Rust cfg(test) public-entry exporter.
// No fallback/synthetic successful response and no test project resource change.
internal sealed class ThesisFormattingFixture
{
    private readonly KnuFixture inner;
    internal JsonObject Response => inner.Response;
    internal byte[] Bytes => inner.Bytes;
    internal string Json => inner.Json;
    private ThesisFormattingFixture(KnuFixture fixture) => inner = fixture;
    internal static ThesisFormattingFixture Load(string name)
    {
        var directory = Environment.GetEnvironmentVariable("THESIS_FORMATTING_FIXTURE_DIR")
            ?? throw new InvalidOperationException("Root must generate actual Rust formatting fixtures first.");
        var path = Path.Combine(directory, name + ".json");
        var data = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var fixture = KnuFixture.Load("negative"); // reuse identity-only rebind code, not this fixture's output
        fixture.Data.Clear();
        foreach (var pair in data) fixture.Data.Add(pair.Key, pair.Value?.DeepClone());
        return new(fixture);
    }
    internal AnalysisSnapshot Snapshot()
    {
        var bytes = Bytes;
        var file = new CapturedFile(FileId.New(), "main.tex", bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }), bytes.Length,
            SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes)), ImmutableArray.CreateRange(bytes));
        return new(AnalysisSnapshotId.New(), ProjectId.New(), Guid.NewGuid(), 7, 1, RevisionId.New(),
            new(AnalysisCatalog.KnuId, AnalysisCatalog.FormattingVersion), AnalysisLimits.Defaults(), [file], [],
            new(AnalysisWorkType.BachelorQualification));
    }
    internal string Reply(AnalysisSnapshot snapshot, string requestId) => inner.Reply(snapshot, requestId);
}

public sealed class FormattingFactAttribute : FactAttribute
{
    public FormattingFactAttribute()
    { if (Environment.GetEnvironmentVariable("THESIS_FORMATTING_FIXTURE_DIR") is null) Skip = "Requires root-generated actual Rust formatting fixtures (THESIS_FORMATTING_FIXTURE_DIR)."; }
}
public sealed class FormattingTheoryAttribute : TheoryAttribute
{
    public FormattingTheoryAttribute()
    { if (Environment.GetEnvironmentVariable("THESIS_FORMATTING_FIXTURE_DIR") is null) Skip = "Requires root-generated actual Rust formatting fixtures (THESIS_FORMATTING_FIXTURE_DIR)."; }
}
