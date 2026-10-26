using Client.Editing;
using Xunit;

namespace Workspace.Tests.Editing;

public sealed class SourceToolSnapshotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FindCanReadButEditsRequireWriter(bool readOnly)
    {
        await using var fixture = await CompilationTestWorkspace.Open(readOnly);
        var workspace = fixture.Workspace;
        workspace.SelectFile(fixture.Main);
        var snapshot = Assert.IsType<SourceToolSnapshot>(SourceToolSnapshot.Capture(workspace, 1));
        string before = fixture.State();
        Assert.True(snapshot.IsCurrent(workspace, 1, false));
        Assert.Equal(!readOnly, snapshot.IsCurrent(workspace, 1, true));
        Assert.Equal(before, fixture.State());
    }

    [Theory]
    [InlineData("content")]
    [InlineData("aba")]
    [InlineData("file")]
    [InlineData("file-aba")]
    [InlineData("intent")]
    [InlineData("session")]
    [InlineData("binary")]
    public async Task CapturedInteropCannotReviveAfterChanges(string change)
    {
        await using var fixture = await CompilationTestWorkspace.Open();
        var workspace = fixture.Workspace;
        workspace.SelectFile(fixture.Main);
        var snapshot = SourceToolSnapshot.Capture(workspace, 4)!;
        long intent = 4;
        switch (change)
        {
            case "content": workspace.EditText(fixture.Main, snapshot.Text + "🙂"); break;
            case "aba":
                workspace.EditText(fixture.Main, snapshot.Text + "🙂");
                workspace.EditText(fixture.Main, snapshot.Text);
                Assert.Equal(snapshot.Text, workspace.ActiveFile!.Content);
                break;
            case "file":
            case "file-aba":
                Assert.True(workspace.CreateFile("other.tex"));
                var other = workspace.Files.Single(f => f.RelativePath == "other.tex");
                workspace.EditText(other.Id, snapshot.Text);
                workspace.SelectFile(other.Id);
                intent++; // Same page contract as a real file-switch click.
                if (change == "file-aba") workspace.SelectFile(snapshot.File);
                break;
            case "intent": intent++; break;
            case "session":
                var project = workspace.Project!.Id;
                await workspace.CloseAsync(CancellationToken.None);
                await workspace.OpenAsync(project, CancellationToken.None);
                workspace.SelectFile(snapshot.File);
                break;
            case "binary": workspace.ActiveFile!.MediaKind = "binary"; break;
        }
        Assert.False(snapshot.IsCurrent(workspace, intent, false));
        Assert.False(snapshot.IsCurrent(workspace, intent, true));
    }

    [Fact]
    public async Task FileSwitchAbaWithoutGenerationChangeStillNeedsNewIntent()
    {
        await using var fixture = await CompilationTestWorkspace.Open();
        var workspace = fixture.Workspace;
        workspace.SelectFile(fixture.Main);
        Assert.True(workspace.CreateFile("other.tex"));
        var other = workspace.Files.Single(f => f.RelativePath == "other.tex");
        workspace.EditText(other.Id, workspace.ActiveFile!.Content);
        workspace.SelectFile(fixture.Main);
        var snapshot = SourceToolSnapshot.Capture(workspace, 7)!;
        workspace.SelectFile(other.Id);
        workspace.SelectFile(fixture.Main);
        Assert.Equal(snapshot.Generation, workspace.WorkingGeneration);
        Assert.False(snapshot.IsCurrent(workspace, 9, true));
    }
}
