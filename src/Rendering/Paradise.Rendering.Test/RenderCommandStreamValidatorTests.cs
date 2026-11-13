using System;
using System.Buffers;
using System.Collections.Generic;

namespace Paradise.Rendering.Test;

public class RenderCommandStreamValidatorTests
{
    private static readonly TextureViewHandle s_offscreenView = new(5, 1);

    private sealed class NoOpHostPass : HostRenderPass;

    [Test]
    public async Task accepts_render_compute_and_host_work_in_sequence()
    {
        var stream = Stream(
            [OffscreenPass()],
            [new HostPassInvocation(new NoOpHostPass(), s_offscreenView)],
            encoder =>
            {
                encoder.BeginPass(0);
                encoder.SetPipeline(new PipelineHandle(1, 1));
                encoder.SetBindGroup(0, new BindGroupHandle(1, 1));
                encoder.Draw(new DrawCommand(3, 1, 0, 0));
                encoder.EndPass();
                encoder.BeginComputePass();
                encoder.SetComputePipeline(new ComputePipelineHandle(1, 1));
                encoder.SetBindGroup(0, new BindGroupHandle(2, 1));
                encoder.Dispatch(new DispatchCommand(1, 1, 1));
                encoder.EndComputePass();
                encoder.HostPass(0);
            });

        await Assert.That(() => RenderCommandStreamValidator.Validate(in stream, allowBackbuffer: false)).ThrowsNothing();
    }

    [Test]
    [Arguments("nested-render", "Nested BeginPass")]
    [Arguments("render-in-compute", "BeginPass inside an open compute pass")]
    [Arguments("compute-in-render", "BeginComputePass inside an open render pass")]
    [Arguments("end-render-in-compute", "EndPass inside a compute pass")]
    [Arguments("draw-outside", "outside of an active BeginPass/EndPass scope")]
    [Arguments("draw-in-compute", "Render command issued inside a compute pass")]
    [Arguments("dispatch-in-render", "Compute command issued inside a render pass")]
    [Arguments("bind-outside", "outside of an active BeginPass/EndPass scope")]
    [Arguments("pass-index", "BeginPass references pass index 3")]
    [Arguments("unclosed", "ended with an open render pass")]
    [Arguments("host-in-pass", "Host callback issued inside an open pass")]
    [Arguments("host-index", "Host callback index is outside")]
    public async Task rejects_malformed_structure(string scenario, string message)
    {
        var stream = Stream([BackbufferPass()], [], encoder =>
        {
            switch (scenario)
            {
                case "nested-render": encoder.BeginPass(0); encoder.BeginPass(0); break;
                case "render-in-compute": encoder.BeginComputePass(); encoder.BeginPass(0); break;
                case "compute-in-render": encoder.BeginPass(0); encoder.BeginComputePass(); break;
                case "end-render-in-compute": encoder.BeginComputePass(); encoder.EndPass(); break;
                case "draw-outside": encoder.Draw(new DrawCommand(3, 1, 0, 0)); break;
                case "draw-in-compute": encoder.BeginComputePass(); encoder.Draw(new DrawCommand(3, 1, 0, 0)); break;
                case "dispatch-in-render": encoder.BeginPass(0); encoder.Dispatch(new DispatchCommand(1, 1, 1)); break;
                case "bind-outside": encoder.SetBindGroup(0, new BindGroupHandle(1, 1)); break;
                case "pass-index": encoder.BeginPass(3); break;
                case "unclosed": encoder.BeginPass(0); break;
                case "host-in-pass": encoder.BeginPass(0); encoder.HostPass(0); break;
                case "host-index": encoder.HostPass(0); break;
            }
        });

        await Assert.That(() => RenderCommandStreamValidator.Validate(in stream, allowBackbuffer: true))
            .ThrowsExactly<InvalidOperationException>()
            .WithMessageContaining(message, StringComparison.Ordinal);
    }

    [Test]
    public async Task offscreen_submission_rejects_backbuffer_targets()
    {
        var pass = Stream([BackbufferPass()], [], encoder => { encoder.BeginPass(0); encoder.EndPass(); });
        var host = Stream([], [new HostPassInvocation(new NoOpHostPass(), default)], encoder => encoder.HostPass(0));

        await Assert.That(() => RenderCommandStreamValidator.Validate(in pass, allowBackbuffer: true)).ThrowsNothing();
        await Assert.That(() => RenderCommandStreamValidator.Validate(in pass, allowBackbuffer: false))
            .ThrowsExactly<InvalidOperationException>().WithMessageContaining("explicit ColorView targets", StringComparison.Ordinal);
        await Assert.That(() => RenderCommandStreamValidator.Validate(in host, allowBackbuffer: false))
            .ThrowsExactly<InvalidOperationException>().WithMessageContaining("require an explicit target", StringComparison.Ordinal);
    }

    [Test]
    public async Task pipeline_and_pass_must_agree_on_depth()
    {
        var withDepth = new PipelineHandle(1, 1);
        var withoutDepth = new PipelineHandle(2, 1);
        var depths = new Dictionary<PipelineHandle, bool> { [withDepth] = true, [withoutDepth] = false };
        var depthless = Stream([BackbufferPass()], [], encoder => { encoder.BeginPass(0); encoder.SetPipeline(withDepth); encoder.EndPass(); });
        var matching = Stream([BackbufferPass()], [], encoder => { encoder.BeginPass(0); encoder.SetPipeline(withoutDepth); encoder.EndPass(); });

        await Assert.That(() => RenderCommandStreamValidator.Validate(in depthless, allowBackbuffer: true, depths))
            .ThrowsExactly<InvalidOperationException>().WithMessageContaining("active pass has no Depth attachment", StringComparison.Ordinal);
        await Assert.That(() => RenderCommandStreamValidator.Validate(in matching, allowBackbuffer: true, depths)).ThrowsNothing();
        // Without a lookup the check is skipped rather than guessed.
        await Assert.That(() => RenderCommandStreamValidator.Validate(in depthless, allowBackbuffer: true)).ThrowsNothing();
    }

    [Test]
    public async Task more_than_one_color_attachment_is_not_supported()
    {
        var stream = Stream([new RenderPassDesc(colorAttachmentCount: 2)], [], _ => { });
        await Assert.That(() => RenderCommandStreamValidator.Validate(in stream, allowBackbuffer: true)).ThrowsExactly<NotSupportedException>();
    }

    [Test]
    public async Task validation_allocates_nothing()
    {
        var depths = new Dictionary<PipelineHandle, bool> { [new PipelineHandle(1, 1)] = false };
        var stream = Stream([BackbufferPass()], [], encoder =>
        {
            encoder.BeginPass(0);
            encoder.SetPipeline(new PipelineHandle(1, 1));
            for (var i = 0; i < 64; i++) encoder.Draw(new DrawCommand(3, 1, 0, 0));
            encoder.EndPass();
        });
        RenderCommandStreamValidator.Validate(in stream, allowBackbuffer: true, depths);

        var before = GC.GetAllocatedBytesForCurrentThread();
        RenderCommandStreamValidator.Validate(in stream, allowBackbuffer: true, depths);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated).IsEqualTo(0L);
    }

    private static RenderPassDesc BackbufferPass()
    {
        var pass = new RenderPassDesc(colorAttachmentCount: 1);
        pass[0] = new ColorAttachmentDesc(default, LoadOp.Clear, StoreOp.Store, ColorRgba.Black);
        return pass;
    }

    private static RenderPassDesc OffscreenPass()
    {
        var pass = new RenderPassDesc(colorAttachmentCount: 1);
        pass[0] = new ColorAttachmentDesc(default, LoadOp.Clear, StoreOp.Store, ColorRgba.Black, s_offscreenView);
        return pass;
    }

    private static RenderCommandStream Stream(RenderPassDesc[] passes, HostPassInvocation[] hostPasses, Action<RenderCommandEncoder> record)
    {
        var writer = new ArrayBufferWriter<RenderCommand>(16);
        record(new RenderCommandEncoder(writer));
        return new RenderCommandStream { Passes = passes, HostPasses = hostPasses, Commands = writer.WrittenMemory };
    }
}
