using System.Collections.Immutable;
using Client.Compilation;
using Client.Workspace.Domain;
using Xunit;
using static Workspace.Tests.CompilationTestWorkspace;

namespace Workspace.Tests;

public sealed class CompilationDigestTests
{
    [Fact]
    public async Task Repeated_reordered_captures_have_new_ids_and_same_content_digest_not_budget_or_version_noise()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var a = Descriptor("a"); var z = Descriptor("z");
        f.Selection.SetSelection(Select(f.Main, bundles: [z, a], tools: [z, a], options: [new("z", "2"), new("a", "1")]));
        var first = await f.Snapshot();
        f.Workspace.Files.Reverse();
        f.Selection.SetSelection(Select(first.MainFileId, bundles: [a, z], tools: [a, z], options: [new("a", "1"), new("z", "2")]));
        var second = await f.Snapshot(Budget with { MaxFiles = 100, MaxTotalBytes = 100000000 });
        Assert.NotEqual(first.Identity.CaptureId, second.Identity.CaptureId);
        Assert.NotEqual(first.Identity.SelectionVersion, second.Identity.SelectionVersion);
        Assert.Equal(first.ContentDigest, second.ContentDigest); Assert.Equal(first.MetadataByteLength, second.MetadataByteLength);
        Assert.Equal(first.Files.Select(x => x.RelativePath), second.Files.Select(x => x.RelativePath));
        Assert.False(f.Capture.IsCurrent(first)); Assert.True(f.Capture.IsCurrent(second));
    }

    [Fact]
    public async Task V1_encoding_has_an_independently_calculated_fixed_golden()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var id = FileId.Parse(Guid.Parse("00000000-0000-0000-0000-000000000001")); var bytes = Array.Empty<byte>();
        f.Repo.Revision = f.Repo.Revision! with { Id = RevisionId.New(), Files = ImmutableArray.Create(new ProjectFile(id, "a.tex", "text", Hash(bytes), 0, false)) };
        f.Repo.Project = f.Repo.Project with { HeadRevisionId = f.Repo.Revision.Id }; f.Repo.Remember();
        await f.Workspace.OpenAsync(f.Repo.Project.Id, None); f.Selection.SetSelection(Select(id));
        var snapshot = await f.Snapshot();
        // Independently encoded via Python struct.pack('<i'/'<q'), length-prefixed
        // strict UTF-8 fields and boolean byte according to documented v1 schema.
        Assert.Equal(315, snapshot.MetadataByteLength);
        Assert.Equal("fe4312018a94a0039646ef21de68ed4e1a20e804eec1621d416c5595b7536a1e", snapshot.ContentDigest.Hex);
    }

    [Theory]
    [InlineData("main-bytes")]
    [InlineData("other-text")]
    [InlineData("binary-bytes")]
    [InlineData("path")]
    [InlineData("file-id")]
    [InlineData("inventory")]
    [InlineData("media-kind")]
    [InlineData("bom-and-bytes")]
    public async Task Complete_inventory_identity_changes_even_for_non_main_inputs(string change)
    {
        await using var f = await CompilationTestWorkspace.Open(); var before = await f.Snapshot();
        var main = f.Workspace.Files.Single(w => w.Id == f.Main);
        var other = f.Workspace.Files.Single(w => w.RelativePath == "chapter.tex");
        var bin = f.Workspace.Files.Single(w => w.MediaKind == "binary");
        switch (change)
        {
            case "main-bytes": Assert.True(f.Workspace.EditText(main.Id, "changed main")); break;
            case "other-text": Assert.True(f.Workspace.EditText(other.Id, "changed include")); break;
            case "binary-bytes": Assert.True(f.Workspace.ReplaceFileBytes(bin.Id, [255, 1, 2])); break;
            case "path": Assert.True(f.Workspace.RenameFile(bin.Id, "renamed.bin")); break;
            case "inventory": Assert.True(f.Workspace.CreateFileFromBytes("extra.bin", [])); break;
            case "file-id":
                var updated = f.Repo.Revision! with { Id = RevisionId.New(), Files = f.Repo.Revision.Files.Select(file => file.Id == bin.Id ? file with { Id = FileId.New() } : file).ToImmutableArray() };
                f.Repo.Revision = updated; f.Repo.Project = f.Repo.Project with { HeadRevisionId = updated.Id }; f.Repo.Remember();
                await f.Workspace.OpenAsync(f.Repo.Project.Id, None); break;
            case "media-kind":
                // Synthetic valid model reclassification, not an analyzer rule
                // or UI permission bypass. Same path/bytes, different media.
                other.MediaKind = "binary"; other.Content = ""; other.BinaryContent = f.Repo.Content[other.DurableHash!.Value]; break;
            case "bom-and-bytes": main.HasBom = false; Assert.True(f.Workspace.EditText(main.Id, main.Content)); break;
        }
        Assert.NotEqual(before.ContentDigest, (await f.Snapshot()).ContentDigest);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("engine-id")]
    [InlineData("engine-version")]
    [InlineData("engine-hash")]
    [InlineData("bundle-id")]
    [InlineData("bundle-version")]
    [InlineData("bundle-hash")]
    [InlineData("tool-id")]
    [InlineData("tool-version")]
    [InlineData("tool-hash")]
    [InlineData("setting-key")]
    [InlineData("setting-value")]
    public async Task Explicit_selection_descriptor_fields_independently_affect_digest(string change)
    {
        await using var f = await CompilationTestWorkspace.Open();
        var input = new CompilationSelectionInput(f.Main, Descriptor(), [Descriptor("bundle")], [Descriptor("tool")], [new("profile", "A")]);
        f.Selection.SetSelection(CompilationSelection.Create(input, Budget.MaxMetadataBytes).Selection); var before = await f.Snapshot();
        input = change switch
        {
            "main" => input with { MainFileId = f.Workspace.Files.Single(w => w.RelativePath == "chapter.tex").Id },
            "engine-id" => input with { Engine = input.Engine with { Id = "other" } },
            "engine-version" => input with { Engine = input.Engine with { Version = "other" } },
            "engine-hash" => input with { Engine = input.Engine with { Hash = new(new string('b', 64)) } },
            "bundle-id" => input with { Bundles = [input.Bundles[0] with { Id = "other" }] },
            "bundle-version" => input with { Bundles = [input.Bundles[0] with { Version = "other" }] },
            "bundle-hash" => input with { Bundles = [input.Bundles[0] with { Hash = new(new string('b', 64)) }] },
            "tool-id" => input with { Tools = [input.Tools[0] with { Id = "other" }] },
            "tool-version" => input with { Tools = [input.Tools[0] with { Version = "other" }] },
            "tool-hash" => input with { Tools = [input.Tools[0] with { Hash = new(new string('b', 64)) }] },
            "setting-key" => input with { Options = [new("different", "A")] },
            "setting-value" => input with { Options = [new("profile", "B")] },
            _ => throw new InvalidOperationException()
        };
        f.Selection.SetSelection(Assert.IsType<CompilationSelection>(CompilationSelection.Create(input, Budget.MaxMetadataBytes).Selection));
        Assert.NotEqual(before.ContentDigest, (await f.Snapshot()).ContentDigest);
    }

    [Fact]
    public async Task Length_prefixes_prevent_concatenated_option_field_ambiguity()
    {
        await using var f = await CompilationTestWorkspace.Open();
        f.Selection.SetSelection(Select(f.Main, options: [new("a", "bc")])); var first = await f.Snapshot();
        f.Selection.SetSelection(Select(f.Main, options: [new("ab", "c")])); var second = await f.Snapshot();
        Assert.NotEqual(first.ContentDigest, second.ContentDigest);
    }

    [Fact]
    public async Task Active_editor_navigation_is_not_main_selection_or_content_identity()
    {
        await using var f = await CompilationTestWorkspace.Open(); var first = await f.Snapshot();
        Assert.True(f.Workspace.SelectFile(f.Workspace.Files.Single(w => w.MediaKind == "binary").Id));
        var second = await f.Snapshot(); Assert.Equal(first.MainFileId, second.MainFileId);
        Assert.Equal(first.ContentDigest, second.ContentDigest); Assert.True(f.Capture.IsCurrent(first));
    }

    [Fact]
    public async Task Independent_owned_captures_do_not_share_new_binary_backing_buffers()
    {
        await using var f = await CompilationTestWorkspace.Open(); var bytes = new byte[] { 255, 1, 2 };
        Assert.True(f.Workspace.CreateFileFromBytes("new.bin", bytes)); var first = await f.Snapshot();
        bytes[1] = 9; var second = await f.Snapshot(); // external caller array mutation, not a workspace command
        Assert.Equal(new byte[] { 255, 1, 2 }, first.Files.Single(x => x.RelativePath == "new.bin").ToArray());
        Assert.Equal(new byte[] { 255, 9, 2 }, second.Files.Single(x => x.RelativePath == "new.bin").ToArray());
        Assert.NotEqual(first.ContentDigest, second.ContentDigest);
        var copied = second.Files.Single(x => x.RelativePath == "new.bin").ToArray(); copied[1] = 7;
        Assert.Equal((byte)9, second.Files.Single(x => x.RelativePath == "new.bin").ByteSpan[1]);
    }
}
