using System.Text;
using System.Text.Json;
using Client.AI;
using Client.Analysis;
using Client.Pages;
using Client.Review;
using Client.Workspace.Patching;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Workspace.Tests.AI;

public class AiSessionTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static string Proposal(string text, string explanation = "Proposed Ukrainian correction")
        => JsonSerializer.Serialize(new { replacementText = text, explanation });

    [Fact]
    public async Task Explicit_consent_prepares_only_authoritative_active_file_without_save_or_automatic_request()
    {
        await using var h = await Host.Open();
        Assert.Null(h.Session.Connection); Assert.Empty(h.Ollama.Requests);
        Assert.True(h.Session.Connect("test:local"));
        Assert.Empty(h.Ollama.Requests);
        await h.Session.PrepareAsync();
        var context = Assert.IsType<AiFileContext>(h.Session.Context);
        var bytes = await h.F.Workspace.GetFileBytesAsync(h.F.Workspace.ActiveFile!, default);
        Assert.Equal("entry", context.Filename); Assert.True(context.HasBom);
        Assert.Equal(bytes.Length, context.ByteLength);
        Assert.Equal(CompilationTestWorkspace.Hash(bytes), context.Hash);
        Assert.Equal(h.F.Workspace.CurrentTextPatchIdentity, context.Identity);
        Assert.Equal("Україна🙂\r\nmain\rlast\n", context.Text);
        Assert.Empty(h.Ollama.Requests); Assert.Equal(0, h.F.Repo.Commits);
        await h.Session.SendAsync("Explain", false, consent: false);
        Assert.Empty(h.Ollama.Requests);
        h.Ollama.Reply = AiReply.Answer("plain answer");
        await h.Session.SendAsync("Explain", false, consent: true);
        Assert.Single(h.Ollama.Requests); Assert.Equal("plain answer", h.Session.Answer);
        var prompt = h.Ollama.Requests[0];
        Assert.Equal(h.Session.PayloadPreview("Explain", false), prompt);
        Assert.DoesNotContain("chapter.tex", prompt); Assert.DoesNotContain("asset.bin", prompt);
        Assert.All(h.F.Repo.ReadHashes, hash => Assert.Equal(context.Hash, hash));
        Assert.False(h.F.Workspace.IsDirty); Assert.Equal(0, h.F.Repo.Commits);
        Assert.Null(h.Review.State.Preview);
    }

    [Fact]
    public async Task Unsaved_text_uses_coordinator_bytes_not_textarea_line_endings_or_durable_hash()
    {
        await using var h = await Host.Open(); var w = h.F.Workspace;
        w.EditText(h.F.Main, "Новий🙂\r\nрядок\rкінець");
        h.Session.Connect("test"); await h.Session.PrepareAsync();
        var context = h.Session.Context!;
        Assert.Equal("Новий🙂\nрядок\nкінець", context.Text); Assert.True(context.HasBom);
        Assert.Equal(CompilationTestWorkspace.Hash(await w.GetFileBytesAsync(w.ActiveFile!, default)), context.Hash);
        Assert.NotEqual(w.ActiveFile!.DurableHash, context.Hash);
        Assert.True(w.IsDirty); Assert.Equal(0, h.F.Repo.Commits);
    }

    [Fact]
    public async Task Byte_preimage_is_owned_and_rechecked_even_if_repository_changes_without_a_generation_event()
    {
        await using var h = await Host.Open(); h.Session.Connect("test");
        await h.Session.PrepareAsync(); var context = h.Session.Context!;
        var bytes = h.F.Repo.Content[context.Hash]; bytes[^2] = (byte)'X';
        Assert.Equal("Україна🙂\r\nmain\rlast\n", context.Text);
        await h.Session.SendAsync("Explain", false, true);
        Assert.Empty(h.Ollama.Requests); Assert.Null(h.Session.Context); Assert.Null(h.Session.Answer);
    }

    [Fact]
    public async Task ABA_edits_invalidate_context_and_prepared_proposal_even_if_text_and_hash_return()
    {
        await using var h = await Host.Open(); var w = h.F.Workspace;
        w.EditText(h.F.Main, "A🙂");
        h.Session.Connect("test"); await h.Session.PrepareAsync();
        var context = h.Session.Context!;
        h.Ollama.Reply = AiReply.Answer(Proposal("Edited🙂"));
        await h.Session.SendAsync("Fix", true, true); Assert.True(h.Session.HasProposal);
        w.EditText(h.F.Main, "B"); w.EditText(h.F.Main, "A🙂");
        Assert.False(context.IsCurrent(w)); Assert.False(await context.VerifyAsync(w, default));
        Assert.Null(h.Session.Context); Assert.Null(h.Session.Answer); Assert.False(h.Session.HasProposal);
        await h.Session.PreviewProposalAsync(); Assert.Null(h.Review.State.Preview); Assert.Equal(0, h.F.Repo.Commits);
    }

    [Fact]
    public async Task Active_selection_change_is_checked_before_send_even_without_workspace_event()
    {
        await using var h = await Host.Open(); h.Session.Connect("test");
        await h.Session.PrepareAsync();
        h.F.Workspace.SelectFile(h.F.Workspace.Files.Single(f => f.RelativePath == "chapter.tex").Id);
        await h.Session.SendAsync("Explain", false, true);
        Assert.Empty(h.Ollama.Requests); Assert.Null(h.Session.Context);
    }

    [Fact]
    public async Task Generation_change_during_capture_wait_discards_context_without_abandoning_read_owner()
    {
        await using var h = await Host.Open(); h.Session.Connect("test");
        var gate = h.F.Hold(); var capture = h.Session.PrepareAsync();
        await gate.WaitReached(); h.F.Workspace.EditText(h.F.Main, "changed🙂");
        Assert.True(h.Session.Busy); Assert.Null(h.Session.Context);
        await h.Session.PrepareAsync(); Assert.True(h.Session.Busy);
        gate.Release.TrySetResult(h.F.Repo.Content[gate.Hash]); await capture.WaitAsync(Bound);
        Assert.False(h.Session.Busy); Assert.Null(h.Session.Context); Assert.Empty(h.Ollama.Requests);
    }

    [Fact]
    public async Task Late_noncooperative_reply_after_cancel_never_repaints_or_allocates_another_send()
    {
        await using var h = await Host.Open(); h.Session.Connect("test"); await h.Session.PrepareAsync();
        h.Ollama.Hold = true;
        var sending = h.Session.SendAsync("Fix", true, true); await h.Ollama.Reached.Task.WaitAsync(Bound);
        h.Session.Cancel(); Assert.True(h.Session.Busy); Assert.Null(h.Session.Context);
        for (var i = 0; i < 10; i++)
        {
            await h.Session.PrepareAsync(); await h.Session.SendAsync("Again", true, true); h.Session.Cancel();
        }
        Assert.Single(h.Ollama.Requests); Assert.True(h.Ollama.Token.IsCancellationRequested);
        h.Ollama.Release.TrySetResult(AiReply.Answer(Proposal("late"))); await sending.WaitAsync(Bound);
        Assert.False(h.Session.Busy); Assert.Null(h.Session.Answer); Assert.False(h.Session.HasProposal); Assert.Null(h.Review.State.Preview);
    }

    [Fact]
    public async Task Changing_the_model_cancels_an_in_flight_reply_and_keeps_the_slot_until_settlement()
    {
        await using var h = await Host.Open();
        h.Session.Connect("old-model");
        var old = h.Session.Connection!;
        await h.Session.PrepareAsync();
        h.Ollama.Hold = true;
        var sending = h.Session.SendAsync("Explain", false, true);
        await h.Ollama.Reached.Task.WaitAsync(Bound);

        Assert.True(h.Session.Connect("new-model"));
        Assert.False(old.Connected);
        Assert.Equal("new-model", h.Session.Connection!.Model);
        Assert.True(h.Session.Busy);
        Assert.True(h.Ollama.Token.IsCancellationRequested);
        await h.Session.PrepareAsync();
        Assert.Single(h.Ollama.Requests);
        h.Ollama.Release.TrySetResult(AiReply.Answer("late answer"));
        await sending.WaitAsync(Bound);
        Assert.Null(h.Session.Answer);
        Assert.Null(h.Session.Context);

        await h.Session.PrepareAsync();
        h.Ollama.Hold = false;
        h.Ollama.Reply = AiReply.Failure(AiOutcome.Connectivity);
        await h.Session.SendAsync("Explain", false, true);
        Assert.Equal(2, h.Ollama.Requests.Count);
        Assert.Null(h.Session.Answer);
        Assert.Contains("Cannot reach Ollama.", h.Session.Status);
    }

    [Fact]
    public async Task Disconnect_clears_context_answer_and_proposal_and_never_persists()
    {
        await using var h = await Host.Open(); h.Session.Connect("test");
        var old = h.Session.Connection!; await h.Session.PrepareAsync();
        h.Ollama.Reply = AiReply.Answer(Proposal("Пропозиція🙂")); await h.Session.SendAsync("Fix", true, true);
        Assert.True(h.Session.HasProposal); Assert.NotNull(h.Session.Answer);
        h.Session.Disconnect();
        Assert.False(old.Connected); Assert.Null(h.Session.Connection); Assert.Null(h.Session.Context);
        Assert.Null(h.Session.Answer); Assert.False(h.Session.HasProposal);
        await h.Session.PreviewProposalAsync(); Assert.Null(h.Review.State.Preview);
        Assert.Equal(0, h.F.Repo.Commits); Assert.Equal(0, h.F.Repo.Publishes); Assert.Equal(0, h.F.Repo.Restores);
    }

    [Fact]
    public async Task Project_close_invalidates_a_pending_reply_and_never_creates_a_review()
    {
        await using var h = await Host.Open(); h.Session.Connect("test"); await h.Session.PrepareAsync();
        h.Ollama.Hold = true; var request = h.Session.SendAsync("Fix", true, true); await h.Ollama.Reached.Task.WaitAsync(Bound);
        await h.F.Workspace.CloseAsync(default);
        h.Ollama.Release.TrySetResult(AiReply.Answer(Proposal("late"))); await request.WaitAsync(Bound);
        Assert.Null(h.Session.Answer); Assert.Null(h.Session.Context); Assert.Null(h.Review.State.Preview); Assert.Equal(0, h.F.Repo.Commits);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Reply_after_ABA_edit_or_active_file_switch_is_stale(bool switchFile)
    {
        await using var h = await Host.Open(); var w = h.F.Workspace;
        w.EditText(h.F.Main, "A🙂");
        h.Session.Connect("test"); await h.Session.PrepareAsync();
        h.Ollama.Hold = true; var request = h.Session.SendAsync("Fix", true, true); await h.Ollama.Reached.Task.WaitAsync(Bound);
        if (switchFile) w.SelectFile(w.Files.Single(f => f.RelativePath == "chapter.tex").Id);
        else { w.EditText(h.F.Main, "B"); w.EditText(h.F.Main, "A🙂"); }
        h.Ollama.Release.TrySetResult(AiReply.Answer(Proposal("late"))); await request.WaitAsync(Bound);
        Assert.Null(h.Session.Answer); Assert.False(h.Session.HasProposal); Assert.Null(h.Review.State.Preview);
        Assert.Equal(0, h.F.Repo.Commits);
    }

    [Fact]
    public async Task Exact_hash_is_rechecked_before_proposal_preview_not_just_before_send()
    {
        await using var h = await Host.Open(); h.Session.Connect("test"); await h.Session.PrepareAsync();
        h.Ollama.Reply = AiReply.Answer(Proposal("replacement")); await h.Session.SendAsync("Fix", true, true);
        var hash = h.Session.Context!.Hash; h.F.Repo.Content[hash][^2] = (byte)'X';
        await h.Session.PreviewProposalAsync();
        Assert.Null(h.Review.State.Preview); Assert.False(h.Session.HasProposal); Assert.Equal(0, h.F.Repo.Commits);
    }

    [Fact]
    public async Task Disconnect_during_review_read_does_not_cancel_a_later_unrelated_preview()
    {
        await using var h = await Host.Open(); h.Session.Connect("test"); await h.Session.PrepareAsync();
        h.Ollama.Reply = AiReply.Answer(Proposal("replacement")); await h.Session.SendAsync("Fix", true, true);
        var chapter = h.F.Workspace.Files.Single(f => f.RelativePath == "chapter.tex");
        var gate = h.F.Hold(chapter.Id);
        var previewing = h.Session.PreviewProposalAsync(); await gate.WaitReached();
        h.Session.Disconnect();
        // The repository deliberately ignores cancellation. The shared review
        // keeps admission until that actual read settles, including on AI disconnect.
        Assert.True(h.Review.State.Busy); Assert.False(previewing.IsCompleted);
        var w = h.F.Workspace; var file = w.ActiveFile!;
        var bytes = await w.GetFileBytesAsync(file, default);
        var set = TextChangeSet.Create(TextPatchOrigin.User, "Independent review", [new(file.Id, file.RelativePath,
            CompilationTestWorkspace.Hash(bytes), bytes.Length, [new(3, bytes.Length, bytes.AsMemory(3), Encoding.UTF8.GetBytes("user"))])]).ChangeSet!;
        var blocked = await h.Review.PreviewAsync(set, default);
        Assert.Equal(ReviewCode.Busy, blocked.Failure?.Code); Assert.Null(blocked.Preview);
        gate.Release.TrySetResult(h.F.Repo.Content[gate.Hash]); await Task.WhenAll(h.F.Repo.PendingReadSettlements).WaitAsync(Bound);
        await previewing.WaitAsync(Bound); Assert.False(h.Review.State.Busy);
        var other = (await h.Review.PreviewAsync(set, default)).Preview!;
        Assert.Equal(other.Id, h.Review.State.Preview?.Id); Assert.Null(h.Session.Context); Assert.Null(h.Session.Answer);
        Assert.Equal(0, h.F.Repo.Commits);
    }

    [Theory]
    [InlineData("{\"replacementText\":\"a\",\"replacementText\":\"b\",\"explanation\":\"x\"}")]
    [InlineData("{\"replacementText\":\"a\",\"explanation\":\"x\",\"command\":\"run\"}")]
    [InlineData("{\"replacementText\":1,\"explanation\":\"x\"}")]
    [InlineData("{\"replacementText\":\"a\",\"explanation\":\"\"}")]
    [InlineData("{\"replacementText\":\"\\uFEFFa\",\"explanation\":\"x\"}")]
    [InlineData("{\"replacementText\":\"\\uD800\",\"explanation\":\"x\"}")]
    [InlineData("```json\n{\"replacementText\":\"a\",\"explanation\":\"x\"}\n```")]
    [InlineData("Helpful answer, not an edit proposal")]
    public async Task Invalid_schema_duplicate_surrogate_BOM_or_prose_is_display_only(string response)
    {
        await using var h = await Host.Open(); h.Session.Connect("test"); await h.Session.PrepareAsync();
        h.Ollama.Reply = AiReply.Answer(response); await h.Session.SendAsync("Fix", true, true);
        Assert.Equal(response, h.Session.Answer); Assert.False(h.Session.HasProposal);
        await h.Session.PreviewProposalAsync(); Assert.Null(h.Review.State.Preview);
        Assert.False(h.F.Workspace.IsDirty); Assert.Equal(0, h.F.Repo.Commits);
    }

    [Fact]
    public async Task Chat_response_cannot_be_promoted_to_proposal_and_no_op_proposal_cannot_edit()
    {
        await using var h = await Host.Open(); h.Session.Connect("test"); await h.Session.PrepareAsync();
        h.Ollama.Reply = AiReply.Answer(Proposal("chat JSON")); await h.Session.SendAsync("Explain", false, true);
        Assert.False(h.Session.HasProposal); await h.Session.PreviewProposalAsync(); Assert.Null(h.Review.State.Preview);
        h.Ollama.Reply = AiReply.Answer(Proposal(h.Session.Context!.Text)); await h.Session.SendAsync("Fix", true, true);
        await h.Session.PreviewProposalAsync(); Assert.Null(h.Review.State.Preview); Assert.Equal(0, h.F.Repo.Commits);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Proposal_requires_explicit_preview_accept_and_exact_Unicode_BOM_undo(bool bom)
    {
        await using var h = await Host.Open(); var w = h.F.Workspace;
        if (!bom) { w.SelectFile(w.Files.Single(f => f.RelativePath == "chapter.tex").Id); }
        var file = w.ActiveFile!; var before = (await w.GetFileBytesAsync(file, default)).ToArray();
        h.Session.Connect("test"); await h.Session.PrepareAsync();
        const string afterText = "Виправлено🙂\r\nДругий рядок\rКінець\n";
        h.Ollama.Reply = AiReply.Answer(Proposal(afterText)); await h.Session.SendAsync("Fix", true, true);
        Assert.Null(h.Review.State.Preview); Assert.Equal(before, await w.GetFileBytesAsync(file, default));
        await h.Session.PreviewProposalAsync(); var preview = Assert.IsType<ReviewPreview>(h.Review.State.Preview);
        Assert.Equal(TextPatchOrigin.Ai, preview.Proposal.Origin); Assert.Single(preview.Prepared.Files);
        Assert.Equal(file.Id, preview.Prepared.Files[0].FileId);
        Assert.Equal(before, preview.Prepared.Files[0].Before.ToArray());
        var expected = bom ? new byte[] { 239, 187, 191 }.Concat(Encoding.UTF8.GetBytes(afterText)).ToArray() : Encoding.UTF8.GetBytes(afterText);
        Assert.Equal(expected, preview.Prepared.Files[0].After.ToArray());
        Assert.Equal(before, await w.GetFileBytesAsync(file, default)); Assert.Equal(0, h.F.Repo.Commits);
        var accepted = await h.Review.AcceptAsync(preview.Id, default);
        Assert.True(accepted.Changed); Assert.Equal(expected, await w.GetFileBytesAsync(w.Files.Single(f => f.Id == file.Id), default));
        Assert.Equal(1, h.F.Repo.Commits);
        var undo = await h.Review.UndoAsync(accepted.ActionId!.Value, default);
        Assert.True(undo.Changed); Assert.Equal(before, await w.GetFileBytesAsync(w.Files.Single(f => f.Id == file.Id), default));
        Assert.Equal(2, h.F.Repo.Commits);
    }

    [Fact]
    public async Task Disconnect_declines_only_its_own_preview_and_does_not_cancel_an_unrelated_review()
    {
        await using var h = await Host.Open(); h.Session.Connect("test"); await h.Session.PrepareAsync();
        h.Ollama.Reply = AiReply.Answer(Proposal("AI replacement")); await h.Session.SendAsync("Fix", true, true);
        await h.Session.PreviewProposalAsync(); var own = h.Review.State.Preview!.Id;
        h.Session.Disconnect(); Assert.Null(h.Review.State.Preview);
        Assert.False((await h.Review.AcceptAsync(own, default)).Changed);
        var w = h.F.Workspace; var bytes = await w.GetFileBytesAsync(w.ActiveFile!, default);
        var unrelated = TextChangeSet.Create(TextPatchOrigin.User, "Unrelated explicit review", [new(w.ActiveFile!.Id,
            w.ActiveFile.RelativePath, CompilationTestWorkspace.Hash(bytes), bytes.Length, [new(3, bytes.Length, bytes.AsMemory(3), Encoding.UTF8.GetBytes("User replacement"))])]).ChangeSet!;
        var other = (await h.Review.PreviewAsync(unrelated, default)).Preview!;
        h.Session.Connect("test"); await h.Session.PrepareAsync();
        h.Ollama.Reply = AiReply.Answer(Proposal("Another AI edit")); await h.Session.SendAsync("Fix", true, true);
        await h.Session.PreviewProposalAsync(); Assert.Equal(other.Id, h.Review.State.Preview?.Id);
        h.Session.Disconnect(); Assert.Equal(other.Id, h.Review.State.Preview?.Id);
        Assert.Equal(0, h.F.Repo.Commits);
    }

    [Fact]
    public async Task Oversized_context_instruction_explanation_and_output_fail_without_truncation_or_send()
    {
        await using var h = await Host.Open(); h.Session.Connect("test");
        h.F.Workspace.EditText(h.F.Main, new string('a', AiLimits.ContextBytes + 1));
        await h.Session.PrepareAsync(); Assert.Null(h.Session.Context); Assert.Contains("size limit", h.Session.Status);
        h.F.Workspace.EditText(h.F.Main, "small🙂"); await h.Session.PrepareAsync();
        await h.Session.SendAsync(new string('a', AiLimits.InstructionBytes + 1), false, true); Assert.Empty(h.Ollama.Requests);
        h.Ollama.Reply = AiReply.Answer(Proposal("valid", new string('a', 4097)));
        await h.Session.SendAsync("Fix", true, true); Assert.False(h.Session.HasProposal);
        h.Ollama.Reply = AiReply.Answer(new string('a', AiLimits.OutputBytes + 1));
        await h.Session.SendAsync("Explain", false, true); Assert.Null(h.Session.Answer); Assert.False(h.Session.HasProposal);
        Assert.Equal(0, h.F.Repo.Commits);
    }

    [Fact]
    public async Task Standalone_Razor_escapes_source_answer_and_model_output_instead_of_executable_HTML()
    {
        await using var h = await Host.Open(); h.Session.Connect("test");
        h.F.Workspace.EditText(h.F.Main, "<img src=x onerror=alert(1)>🙂"); await h.Session.PrepareAsync();
        h.Ollama.Reply = AiReply.Answer("<script>alert('untrusted')</script>"); await h.Session.SendAsync("Explain", false, true);
        using var services = new ServiceCollection().AddLogging().AddSingleton(h.F.Workspace)
            .AddSingleton<IChangeReviewService>(h.Review).AddAiAssistance().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<AiPanel>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Session"] = h.Session }));
            return root.ToHtmlString();
        });
        Assert.Contains("&lt;script&gt;", html); Assert.Contains("&lt;img src=x", html);
        Assert.DoesNotContain("<script>", html); Assert.DoesNotContain("<img ", html);
        Assert.DoesNotContain("ai-key", html);
        Assert.DoesNotContain("ai-provider", html);
        Assert.Equal(0, h.F.Repo.Commits);
    }

    [Fact]
    public async Task Panel_refuses_an_unsupported_address_and_local_connection_sends_nothing()
    {
        await using var h = await Host.Open();
        using var services = new ServiceCollection().AddLogging().AddSingleton(h.F.Workspace)
            .AddSingleton<IChangeReviewService>(h.Review).AddAiAssistance().BuildServiceProvider();
        await using var renderer = new PanelEventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var id = await renderer.MountAsync(h.Session);
            await renderer.ChangeAsync(id, "ai-model", "test-model", "oninput");
            await renderer.ChangeAsync(id, "ai-local-url", "https://example.invalid", "oninput");
            await renderer.ClickAsync(id, "ai-connect");
            Assert.Null(h.Session.Connection);
            Assert.Contains("supported Ollama address", h.Session.Status);

            await renderer.ChangeAsync(id, "ai-local-url", "http://127.0.0.1:11434", "oninput");
            await renderer.ClickAsync(id, "ai-connect");
            var connection = Assert.IsType<AiConnection>(h.Session.Connection);
            Assert.Equal("test-model", connection.Model);
            Assert.Equal("http://127.0.0.1:11434/api/chat", connection.Destination.AbsoluteUri);
        });
        Assert.Empty(h.Ollama.Requests);
        Assert.Equal(0, h.F.Repo.Commits);
    }

#pragma warning disable BL0006 // Test-only renderer dispatches real Razor events; no private component-field access.
    private sealed class PanelEventRenderer(IServiceProvider services, ILoggerFactory logging)
        : Microsoft.AspNetCore.Components.RenderTree.Renderer(services, logging)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        internal async Task<int> MountAsync(AiSession session)
        {
            var id = AssignRootComponentId(InstantiateComponent(typeof(AiPanel)));
            await RenderRootComponentAsync(id, ParameterView.FromDictionary(new Dictionary<string, object?> { ["Session"] = session }));
            return id;
        }
        internal Dictionary<string, ulong> Events(int id, string testId)
        {
            var frames = GetCurrentRenderTreeFrames(id);
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames.Array[i];
                if (frame.FrameType != Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Element) continue;
                var attributes = new Dictionary<string, object?>(); var events = new Dictionary<string, ulong>();
                for (var j = i + 1; j < frames.Count && frames.Array[j].FrameType == Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Attribute; j++)
                {
                    var attribute = frames.Array[j]; attributes[attribute.AttributeName] = attribute.AttributeValue;
                    if (attribute.AttributeEventHandlerId != 0) events[attribute.AttributeName] = attribute.AttributeEventHandlerId;
                }
                if (attributes.TryGetValue("data-testid", out var value) && Equals(value, testId)) return events;
            }
            throw new InvalidOperationException("Requested UI control was not rendered.");
        }
        internal Task ChangeAsync(int id, string testId, string value, string eventName = "onchange")
            => DispatchEventAsync(Events(id, testId)[eventName], null, new ChangeEventArgs { Value = value });
        internal Task ClickAsync(int id, string testId)
            => DispatchEventAsync(Events(id, testId)["onclick"], null, new MouseEventArgs());
        protected override Task UpdateDisplayAsync(in Microsoft.AspNetCore.Components.RenderTree.RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
    }
#pragma warning restore BL0006

    private sealed class Host : IAsyncDisposable
    {
        internal CompilationTestWorkspace F = null!;
        internal AnalysisCoordinator Analysis = null!;
        internal ChangeReviewService Review = null!;
        internal AiSession Session = null!;
        internal Provider Ollama = new();
        internal static async Task<Host> Open()
        {
            var h = new Host { F = await CompilationTestWorkspace.Open() };
            var analyzer = new Analyzer();
            h.Analysis = new(h.F.Workspace, analyzer, delayAsync: (_, ct) => Task.Delay(Timeout.Infinite, ct));
            h.Review = new(h.F.Workspace, h.Analysis, new SourceNavigator(h.Analysis, h.F.Workspace, analyzer));
            h.Session = new(h.F.Workspace, h.Review, h.Ollama);
            return h;
        }
        public async ValueTask DisposeAsync()
        {
            Ollama.Release.TrySetResult(AiReply.Failure(AiOutcome.Cancelled));
            Session.Dispose(); await Review.DisposeAsync(); await Analysis.DisposeAsync(); await F.DisposeAsync();
        }
    }
    private sealed class Analyzer : ISourceAnalyzer
    {
        public Task<AnalysisJobResult> AnalyzeAsync(AnalysisSnapshot snapshot, CancellationToken ct)
            => Task.FromResult<AnalysisJobResult>(new AnalysisJobResult.Cancelled("ai-test", 1));
        public Task<bool> IsCurrentWorkerEpochAsync(long epoch, CancellationToken ct) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Provider : IAiProvider
    {
        internal List<string> Requests = [];
        internal bool Hold;
        internal AiReply Reply = AiReply.Answer("answer");
        internal CancellationToken Token;
        internal TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<AiReply> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AiReply> SendAsync(AiConnection connection, AiTextRequest request, CancellationToken ct)
        {
            Requests.Add(request.Prompt); Token = ct; Reached.TrySetResult();
            return Hold ? await Release.Task : Reply; // Finite, deliberately noncooperative cancellation.
        }
    }
}
