using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Analysis;
using Client.Analysis.RenderedReview;
using Client.Compilation.Native;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;
using static Workspace.Tests.RenderedRoleBindingUnicode.UnicodeIncludeBindingTests;

namespace Workspace.Tests.RenderedRoleBindingUnicode;

public sealed class NativeUnicodeRoleTheoryAttribute : TheoryAttribute
{
    public NativeUnicodeRoleTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RENDERED_ROLE_BINDING_UNICODE_FIXTURE")))
            Skip = "Exact native PDF/map and genuine 1.7 Rust response fixture not supplied.";
    }
}

public sealed class ProjectCaptionBindingTests
{
    [NativeUnicodeRoleFact]
    public void Genuine_project_captions_bind_both_native_ranges_without_editing_either_file() => Run(async () =>
    {
        await using var h = await Open(captionRoles: true);
        var before = h.Work.State();
        var thesis = h.Report.Result.Thesis!;
        Assert.Equal(AnalysisCatalog.CaptionRolesVersion, h.Report.Result.Profile.Version);
        Assert.Equal(2, thesis.ClosureFileIds.Length);
        Assert.True(thesis.CompleteLiteralScan);
        Assert.Empty(thesis.Limitations);
        Assert.Equal(new AnalysisThesisCaptionRoleCapabilityDto("p16-article-t2a-ukrainian-caption-v1", 16,
            NativeCompilerInputBudgetPins.ClosureHash, "73d1e45bfccbc0048189e355a3a7989578d120e22eb1a95433fec1813bafa6d3"),
            thesis.CaptionRoleCapability);
        Assert.Equal(2, thesis.CaptionRoles.Length);
        foreach (var (file, roleName, text, line, start, end) in new[] {
            (h.Main, "figureCaption", "Іван і Ірина", 7, 8, 20),
            (h.Chapter, "tableCaption", "Інші вимірювання", 3, 31, 47),
        })
        {
            var role = thesis.CaptionRoles.Single(r => r.FileId == file.ToString());
            var capture = h.Report.Snapshot.Files.Single(f => f.FileId == file);
            Assert.Equal(text, Encoding.UTF8.GetString(capture.Bytes.AsSpan()[(int)role.Span.StartByte..(int)role.Span.EndByte]));
            Assert.Contains("🙂", Encoding.UTF8.GetString(capture.Bytes.AsSpan()[..(int)role.Span.StartByte]));
            Assert.Contains("\r\n", Encoding.UTF8.GetString(capture.Bytes.AsSpan()[..(int)role.Span.StartByte]));
            var result = await h.Binding.BindAsync(h.Request(role.EventIndex));
            Assert.True(result.Value is not null, result.Code);
            var proof = result.Value!;
            Assert.Equal(roleName, proof.Annotation.Role);
            Assert.Equal(role.FileId, proof.Annotation.SourceFileId);
            Assert.Equal(role.Span, proof.Annotation.SourceSpan);
            Assert.Equal(start, proof.Annotation.CharacterStart);
            Assert.Equal(end, proof.Annotation.CharacterEnd);
            Assert.Equal(line, proof.SourceLine);
            Assert.Equal(text, string.Concat(proof.NativeCharacters.Select(g => new Rune(g.UnicodeValue).ToString())));
            Assert.Equal("captionText", proof.RangeKind);
            Assert.False(proof.GlyphInkMeasured);
            Assert.True(await h.Binding.VerifyCurrentAsync(proof, "bachelorQualification"));
        }
        Assert.Equal(before, h.Work.State());
        Assert.Equal(0, h.Work.Repo.Commits);
        Assert.Equal(2, h.Inspector.Calls);
        Assert.Equal(6, h.Queries.Calls);
    });

    [NativeUnicodeRoleTheory]
    [InlineData("different-profile")]
    [InlineData("different-engine")]
    [InlineData("different-bundle")]
    [InlineData("binary-shadow")]
    public void Compiler_context_mismatch_refuses_before_native_lookup(string refusal) => Run(async () =>
    {
        await using var h = await Open(captionRoles: true, refusal: refusal);
        var root = h.Report.Result.Thesis!.CaptionRoles[0];
        Assert.Equal(NativeCompilerInputBudgetPins.ClosureHash, h.Report.Result.Thesis.CaptionRoleCapability!.RuntimeClosureSha256);
        var result = await h.Binding.BindAsync(h.Request(root.EventIndex));
        Assert.Null(result.Value);
        Assert.Equal("unsupported-or-ambiguous-source-role", result.Code);
        Assert.Equal(0, h.Queries.Calls);
        Assert.Equal(0, h.Inspector.Calls);
    });

    [NativeUnicodeRoleTheory]
    [InlineData("same-hash-recompile")]
    [InlineData("worker-reset")]
    [InlineData("source-edit")]
    public void Issued_project_role_retires_with_its_original_authority(string change) => Run(async () =>
    {
        await using var h = await Open(captionRoles: true);
        var request = h.Request(h.Report.Result.Thesis!.CaptionRoles[1].EventIndex);
        Assert.Null((await h.Binding.BindAsync(request with { Report = h.Report with { } })).Value);
        Assert.Equal(0, h.Queries.Calls);
        var result = await h.Binding.BindAsync(request);
        Assert.True(result.Value is not null, result.Code);
        var proof = result.Value!;
        switch (change)
        {
            case "same-hash-recompile":
                await h.Compile();
                Assert.Equal(proof.Output.OutputHash, h.Session.DisplayedOutput!.OutputHash);
                Assert.NotEqual(proof.Output.RequestId, h.Session.DisplayedOutput.RequestId);
                break;
            case "worker-reset": h.Js.CurrentEpoch++; break;
            case "source-edit": Assert.True(h.Work.Workspace.EditText(h.Chapter, "Edited included file")); break;
        }
        Assert.False(await h.Binding.VerifyCurrentAsync(proof, "bachelorQualification"));
    });

    [NativeUnicodeRoleTheory]
    [InlineData("missing-roles")]
    [InlineData("missing-capability")]
    [InlineData("null-capability")]
    [InlineData("invalid-index")]
    [InlineData("duplicate-role")]
    [InlineData("foreign-role-field")]
    [InlineData("incorrect-file")]
    [InlineData("incorrect-span")]
    [InlineData("incorrect-root")]
    [InlineData("unknown-method")]
    [InlineData("wrong-environment")]
    [InlineData("wrong-capability-id")]
    [InlineData("wrong-profile")]
    [InlineData("wrong-closure")]
    [InlineData("wrong-source-pins")]
    public void Project_caption_wire_rejects_unbound_or_unknown_evidence(string mutation)
    {
        var fixture = Load(Folder, captionRoles: true);
        var snapshot = fixture.Snapshot();
        var valid = CaptionRoleReply(fixture, snapshot, "caption-wire");
        Assert.IsType<AnalysisResponse.Completed>(ThesisValidation.ParseResponse(valid, snapshot, "caption-wire"));
        var response = JsonNode.Parse(valid)!;
        var thesis = response["payload"]!["thesis"]!.AsObject();
        var roles = thesis["captionRoles"]!.AsArray();
        var role = roles[0]!.AsObject();
        var capability = thesis["captionRoleCapability"]!.AsObject();
        switch (mutation)
        {
            case "missing-roles": thesis.Remove("captionRoles"); break;
            case "missing-capability": thesis.Remove("captionRoleCapability"); break;
            case "null-capability": thesis["captionRoleCapability"] = null; break;
            case "invalid-index": role["eventIndex"] = 999; break;
            case "duplicate-role": roles.Insert(1, role.DeepClone()); break;
            case "foreign-role-field": role["glyphInkMeasured"] = true; break;
            case "incorrect-file": role["fileId"] = roles[1]!["fileId"]!.GetValue<string>(); break;
            case "incorrect-span": role["span"]!["startByte"] = 0; break;
            case "incorrect-root": role["contextRootFileId"] = "unrelated-root"; break;
            case "unknown-method": role["method"] = "guessed-caption-title"; break;
            case "wrong-environment": role["environment"] = "illustration"; break;
            case "wrong-capability-id": capability["id"] = "unrelated-capability"; break;
            case "wrong-profile": capability["runtimeProfile"] = 15; break;
            case "wrong-closure": capability["runtimeClosureSha256"] = new string('0', 64); break;
            case "wrong-source-pins": capability["sourcePinsSha256"] = new string('0', 64); break;
        }
        Assert.Throws<JsonException>(() => ThesisValidation.ParseResponse(response.ToJsonString(), snapshot, "caption-wire"));
    }

    [NativeUnicodeRoleFact]
    public void Genuine_package_free_project_preserves_no_caption_capability_without_losing_source_events()
    {
        var folder = Path.Combine(Folder, "controls/ascii-executed-include");
        var fixture = Load(folder, captionRoles: true);
        var snapshot = fixture.Snapshot();
        const string request = "empty-caption-capability";
        var valid = CaptionRoleReply(fixture, snapshot, request);
        var result = Assert.IsType<AnalysisResponse.Completed>(ThesisValidation.ParseResponse(valid, snapshot, request));
        var thesis = result.Result.Thesis!;
        Assert.True(thesis.CompleteLiteralScan);
        Assert.Equal(2, thesis.Events.Count(e => e.Kind == "captionTitle"));
        Assert.False(thesis.CaptionRoles.IsDefault);
        Assert.Empty(thesis.CaptionRoles);
        Assert.Null(thesis.CaptionRoleCapability);

        var response = JsonNode.Parse(valid)!;
        var genuineCapability = Load(Folder, captionRoles: true).Response["payload"]!["thesis"]!["captionRoleCapability"]!;
        response["payload"]!["thesis"]!["captionRoleCapability"] = genuineCapability.DeepClone();
        Assert.Throws<JsonException>(() => ThesisValidation.ParseResponse(response.ToJsonString(), snapshot, request));
    }

    [NativeUnicodeRoleFact]
    public void Legacy_wire_refuses_the_new_fields_even_for_an_empty_capability()
    {
        var fixture = Load(Folder);
        var snapshot = fixture.Snapshot();
        var valid = fixture.Reply(snapshot, "legacy-wire");
        var completed = Assert.IsType<AnalysisResponse.Completed>(ThesisValidation.ParseResponse(valid, snapshot, "legacy-wire"));
        Assert.True(completed.Result.Thesis!.CaptionRoles.IsDefault);
        Assert.Null(completed.Result.Thesis.CaptionRoleCapability);
        var response = JsonNode.Parse(valid)!;
        response["payload"]!["thesis"]!["captionRoles"] = new JsonArray();
        response["payload"]!["thesis"]!["captionRoleCapability"] = null;
        Assert.Throws<JsonException>(() => ThesisValidation.ParseResponse(response.ToJsonString(), snapshot, "legacy-wire"));
    }
}
