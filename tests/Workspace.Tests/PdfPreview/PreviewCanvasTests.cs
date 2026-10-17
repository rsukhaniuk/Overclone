using Client.Pdf.Preview;
using Microsoft.JSInterop;
using Xunit;

namespace Workspace.Tests.PdfPreview;

// CONTRACT-only delayed interop. Real visible canvas pixels require browser evidence.
public sealed class PreviewCanvasTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(4);
    [Fact] public async Task No_frame_does_not_import_or_draw()
    {
        var js = new Runtime(); await using var c = new PdfPreviewCanvas(js, default); c.Select(1, null);
        Assert.Equal(0, js.Imports); Assert.False(c.ActualWorkPending); Assert.Equal("Cleared for latest selection", c.Status);
    }
    [Fact] public async Task Late_import_draws_latest_frame_only()
    {
        var first = await PreviewInspector.Frame(); var latest = await PreviewInspector.Frame(2);
        var js = new Runtime { Gate = PreviewInspector.Signal<IJSInProcessObjectReference>() }; await using var canvas = new PdfPreviewCanvas(js, default);
        try
        {
            canvas.Select(first.Generation, first); await js.Reached.Task.WaitAsync(Bound); canvas.Select(latest.Generation, latest);
            Assert.Equal(1, js.Imports); js.Gate.SetResult(js.Module); await canvas.WhenSettled.WaitAsync(Bound);
            Assert.Equal(1, js.Module.Creates); Assert.Equal(2, js.Module.Channel.PaintedGeneration); Assert.Equal(new long[] { 2 }, js.Module.Channel.Draws);
        }
        finally { js.Gate.TrySetResult(js.Module); await canvas.WhenSettled.WaitAsync(Bound); }
    }
    [Fact] public async Task Synchronous_invalidation_prevents_late_old_draw_from_painting_newer_selection()
    {
        var first = await PreviewInspector.Frame(); var latest = await PreviewInspector.Frame(2);
        var js = new Runtime(); js.Module.Channel.Gate = PreviewInspector.Signal<bool>(); await using var canvas = new PdfPreviewCanvas(js, default);
        try
        {
            canvas.Select(first.Generation, first); await js.Module.Channel.Reached.Task.WaitAsync(Bound);
            canvas.Select(latest.Generation, latest); Assert.Equal(2, js.Module.Channel.Generation); Assert.Equal(0, js.Module.Channel.PaintedGeneration);
            Assert.True(canvas.ActualWorkPending); js.Module.Channel.Gate.SetResult(true); await canvas.WhenSettled.WaitAsync(Bound);
            Assert.Equal(2, js.Module.Channel.PaintedGeneration); Assert.Equal(new long[] { 1, 2 }, js.Module.Channel.Draws); Assert.Equal(1, js.Module.Channel.PeakActive);
            canvas.Select(1, first); Assert.Equal(2, js.Module.Channel.Generation);
        }
        finally { js.Module.Channel.Gate.TrySetResult(true); await canvas.WhenSettled.WaitAsync(Bound); }
    }
    [Fact] public async Task Dispose_during_import_returns_promptly_and_late_module_never_creates_canvas()
    {
        var frame = await PreviewInspector.Frame(); var js = new Runtime { Gate = PreviewInspector.Signal<IJSInProcessObjectReference>() };
        var canvas = new PdfPreviewCanvas(js, default);
        try
        {
            canvas.Select(frame.Generation, frame); await js.Reached.Task.WaitAsync(Bound); await canvas.DisposeAsync(); Assert.True(canvas.ActualWorkPending);
            js.Gate.SetResult(js.Module); await canvas.WhenSettled.WaitAsync(Bound);
            Assert.Equal(1, js.Module.Releases); Assert.Equal(0, js.Module.Creates);
        }
        finally { js.Gate.TrySetResult(js.Module); await canvas.WhenSettled.WaitAsync(Bound); await canvas.DisposeAsync(); }
    }
    [Fact] public async Task Dispose_during_draw_clears_channel_and_never_posts_callback_after_settlement()
    {
        var frame = await PreviewInspector.Frame(); var js = new Runtime(); js.Module.Channel.Gate = PreviewInspector.Signal<bool>();
        var canvas = new PdfPreviewCanvas(js, default); int events = 0; canvas.Changed += () => events++;
        try
        {
            canvas.Select(frame.Generation, frame); await js.Module.Channel.Reached.Task.WaitAsync(Bound); await canvas.DisposeAsync(); int before = events;
            Assert.True(canvas.ActualWorkPending); Assert.True(js.Module.Channel.Closed); js.Module.Channel.Gate.SetResult(true); await canvas.WhenSettled.WaitAsync(Bound);
            Assert.Equal(0, js.Module.Channel.PaintedGeneration); Assert.Equal(before, events); Assert.Equal(1, js.Module.Releases); Assert.Equal(1, js.Module.Channel.Releases);
        }
        finally { js.Module.Channel.Gate.TrySetResult(true); await canvas.WhenSettled.WaitAsync(Bound); await canvas.DisposeAsync(); }
    }
    [Fact] public async Task Wrong_generation_frame_is_rejected_before_import_or_clone()
    {
        var frame = await PreviewInspector.Frame(); var js = new Runtime(); await using var canvas = new PdfPreviewCanvas(js, default);
        Assert.Throws<ArgumentException>(() => canvas.Select(2, frame)); Assert.Equal(0, js.Imports);
    }
    private sealed class Runtime : IJSRuntime
    {
        internal readonly Module Module = new(); internal int Imports; internal TaskCompletionSource<IJSInProcessObjectReference>? Gate;
        internal readonly TaskCompletionSource<bool> Reached = PreviewInspector.Signal<bool>();
        public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) => InvokeAsync<T>(name, CancellationToken.None, args);
        public ValueTask<T> InvokeAsync<T>(string name, CancellationToken ct, object?[]? args)
        { Assert.Equal("import", name); Assert.Equal("./js/pdf-preview-canvas.js", args![0]); Imports++; Reached.TrySetResult(true); return new(Get<T>()); }
        private async Task<T> Get<T>() => (T)(object)(Gate is null ? Module : await Gate.Task);
    }
    private sealed class Module : IJSInProcessObjectReference
    {
        internal readonly Channel Channel = new(); internal int Creates, Releases;
        public T Invoke<T>(string name, params object?[]? args) { Assert.Equal("create", name); Creates++; return (T)(object)Channel; }
        public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) => ValueTask.FromResult(Invoke<T>(name, args));
        public ValueTask<T> InvokeAsync<T>(string name, CancellationToken ct, object?[]? args) => InvokeAsync<T>(name, args);
        public void Dispose() => Releases++;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class Channel : IJSInProcessObjectReference
    {
        internal long Generation, PaintedGeneration; internal bool Closed; internal int Releases, Active, PeakActive;
        internal readonly List<long> Draws = []; internal TaskCompletionSource<bool>? Gate;
        internal readonly TaskCompletionSource<bool> Reached = PreviewInspector.Signal<bool>();
        public T Invoke<T>(string name, params object?[]? args)
        {
            if (name == "invalidate") { long g = Convert.ToInt64(args![0]); if (g > Generation) { Generation = g; PaintedGeneration = 0; } }
            else if (name == "dispose") { Closed = true; PaintedGeneration = 0; }
            else if (name == "measure") return (T)(object)360;
            else throw new JSException("Unexpected synchronous draw operation.");
            return default!;
        }
        public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) => InvokeAsync<T>(name, CancellationToken.None, args);
        public ValueTask<T> InvokeAsync<T>(string name, CancellationToken ct, object?[]? args) => new(Draw<T>(name, args));
        private async Task<T> Draw<T>(string name, object?[]? args)
        {
            Assert.Equal("draw", name); long g = Convert.ToInt64(args![0]); Draws.Add(g); Active++; PeakActive = Math.Max(PeakActive, Active); Reached.TrySetResult(true);
            try
            {
                if (Draws.Count == 1 && Gate is not null) await Gate.Task;
                bool paint = !Closed && g == Generation; if (paint) PaintedGeneration = g; return (T)(object)paint;
            }
            finally { Active--; }
        }
        public void Dispose() => Releases++;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
