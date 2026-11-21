using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Analysis;
using Client.Workspace.Domain;
using Xunit;

namespace Workspace.Tests;

public sealed class KnuProjectMarginContractTests
{
    [Theory]
    [InlineData("before", true, true)]
    [InlineData("after", true, false)]
    [InlineData("missing-include", false, false)]
    public async Task Real_producer_context_and_fix_are_bound_to_the_capture(string phase, bool hasContext, bool hasFix)
    {
        var (fixture, snapshot) = Load(phase);
        var result = Parse(fixture.Response);
        var (request, _) = AnalysisRequestPacker.Pack(snapshot);
        using var requestDoc = JsonDocument.Parse(request);
        Assert.Equal(snapshot.ReferenceConfiguration!.RootFileId,
            requestDoc.RootElement.GetProperty("payload").GetProperty("referenceConfiguration").GetProperty("rootFileId").GetString());
        Assert.IsType<AnalysisJobResult.Completed>(await AnalyzeAsync(fixture, snapshot));
        Assert.Equal(hasContext, result.ProjectMarginContext is not null);
        Assert.Equal(hasFix, !result.Knu!.FixDescriptors.IsEmpty);
        if (hasFix)
        {
            var fix = Assert.Single(result.Knu.FixDescriptors);
            Assert.Equal(result.ProjectMarginContext!.RootFileId, fix.FileId);
            Assert.Equal("12mm", fix.ExpectedOriginal);
            Assert.Equal("25mm", fix.Replacement);
        }
    }

    [Fact]
    public async Task Forged_or_omitted_include_evidence_refuses()
    {
        var (fixture, snapshot) = Load("before");
        var payload = fixture.Response["payload"]!.AsObject();
        var context = payload["projectMarginContext"]!.AsObject();
        context["inputSpan"]!["startByte"] = 129;
        Parse(fixture.Response);
        var failure = Assert.IsType<AnalysisJobResult.Failed>(await AnalyzeAsync(fixture, snapshot));
        Assert.Equal("InvalidInput", failure.Error.Code);

        payload.Remove("projectMarginContext");
        Assert.Throws<JsonException>(() => Parse(fixture.Response));
    }

    private static async Task<AnalysisJobResult> AnalyzeAsync(KnuFixture fixture, AnalysisSnapshot snapshot)
    {
        var js = new KnuProtocolTests.FixtureJs((_, requestId) =>
        {
            var response = (JsonObject)fixture.Response.DeepClone();
            response["requestId"] = requestId;
            return response.ToJsonString();
        });
        await using var analyzer = new RustSourceAnalyzer(js);
        return await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
    }

    private static AnalysisResultDto Parse(JsonObject response) =>
        Assert.IsType<AnalysisResponse.Completed>(AnalysisJson.ParseResponse(response.ToJsonString())).Result;

    private static (KnuFixture Fixture, AnalysisSnapshot Snapshot) Load(string phase)
    {
        var fixture = KnuFixture.Load("project-margins-" + phase);
        var dto = fixture.Request.Deserialize<AnalysisRequestEnvelope>(AnalysisJson.Options)!.Payload;
        var packed = fixture.Bytes;
        var files = dto.Files.Select(descriptor =>
        {
            var bytes = packed.AsSpan((int)descriptor.ByteOffset, (int)descriptor.ByteLength).ToArray();
            return new CapturedFile(new(Guid.Parse(descriptor.FileId)), descriptor.RelativePath, descriptor.HasBom,
                bytes.Length, SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes)), ImmutableArray.Create(bytes));
        }).ToImmutableArray();
        var snapshot = new AnalysisSnapshot(new(Guid.Parse(dto.SnapshotId)), new(Guid.Parse(dto.ProjectId)),
            Guid.Parse(dto.SessionId), dto.WorkingGeneration, 0, new RevisionId(Guid.Parse(dto.BaseHeadRevisionId!)),
            new(dto.Profile.Id, dto.Profile.Version), AnalysisLimits.Defaults(), files, [], dto.Configuration, ReferenceConfiguration: dto.ReferenceConfiguration);
        return (fixture, snapshot);
    }
}
