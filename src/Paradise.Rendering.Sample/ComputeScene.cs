using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using Paradise.Rendering;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Sample;

/// <summary>The compute demo: an animated plasma written by a COMPUTE pass each frame — inside a
/// <see cref="IRenderer.SubmitOffscreen"/> stream, the game-owned channel — then sampled to the
/// backbuffer by a fullscreen render pass in the presenting <see cref="IRenderer.Submit"/>. The
/// two-submit shape is the point: it is exactly how a game runs GPU simulation (water heightfield,
/// caustics) ahead of the engine's frame.</summary>
internal sealed class ComputeScene : IDisposable
{
    private const uint PlasmaSize = 256;

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct ComputeParamsGpu
    {
        [FieldOffset(0)] public Vector4 TimeAndSize;
    }

    private readonly WebGpuRenderer _renderer;
    private readonly ComputePipelineHandle _computePipeline;
    private readonly PipelineHandle _displayPipeline;
    private readonly BufferHandle _paramsBuffer;
    private readonly TextureHandle _plasmaTexture;
    private readonly TextureViewHandle _plasmaView;
    private readonly SamplerHandle _sampler;
    private readonly BindGroupHandle _computeGroup;
    private readonly BindGroupHandle _displayGroup;
    private readonly ArrayBufferWriter<RenderCommand> _computeWriter = new(8);
    private readonly ArrayBufferWriter<RenderCommand> _displayWriter = new(8);
    private readonly RenderPassDesc[] _passes = new RenderPassDesc[1];
    private int _frame;

    public ComputeScene(WebGpuRenderer renderer)
    {
        _renderer = renderer;

        var computeProgram = WebGpuRenderer.LoadShaderProgram(typeof(ComputeScene).Assembly, "Shaders.proceduralCompute");
        var displayProgram = WebGpuRenderer.LoadShaderProgram(typeof(ComputeScene).Assembly, "Shaders.computeDisplay");

        _computePipeline = renderer.CreateComputePipeline(computeProgram);
        _displayPipeline = renderer.CreatePipeline(displayProgram, renderer.ColorFormat);

        _plasmaTexture = renderer.CreateTexture(new TextureDesc(
            "Plasma", PlasmaSize, PlasmaSize, 1, 1, 1, TextureDimension.D2,
            TextureFormat.Rgba16Float, TextureUsage.StorageBinding | TextureUsage.TextureBinding));
        _plasmaView = renderer.CreateTextureView(new TextureViewDesc(
            "PlasmaView", _plasmaTexture, TextureViewDimension.D2, 0, 1));

        _paramsBuffer = renderer.CreateBuffer(new BufferDesc(
            "PlasmaParams", 16, BufferUsage.Uniform | BufferUsage.CopyDst));

        _sampler = renderer.CreateSampler(new SamplerDesc(
            "PlasmaSampler",
            SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge,
            SamplerFilterMode.Linear, SamplerFilterMode.Linear, SamplerFilterMode.Nearest));

        _computeGroup = renderer.CreateBindGroup(new BindGroupDesc(
            "PlasmaComputeGroup", computeProgram.Layout.Groups[0], new[]
            {
                BindGroupEntryDesc.ForBuffer(0, _paramsBuffer, 0, 16),
                BindGroupEntryDesc.ForTextureView(1, _plasmaView),
            }));
        _displayGroup = renderer.CreateBindGroup(new BindGroupDesc(
            "PlasmaDisplayGroup", displayProgram.Layout.Groups[0], new[]
            {
                BindGroupEntryDesc.ForTextureView(0, _plasmaView),
                BindGroupEntryDesc.ForSampler(1, _sampler),
            }));

        _passes[0] = new RenderPassDesc(colorAttachmentCount: 1);
        _passes[0].Colors.Slot0 = new ColorAttachmentDesc(
            View: RenderViewHandle.Invalid,
            Load: LoadOp.Clear,
            Store: StoreOp.Store,
            ClearValue: ColorRgba.Black);
    }

    public void RenderFrame()
    {
        _frame++;
        var uniforms = new ComputeParamsGpu
        {
            TimeAndSize = new Vector4(_frame / 60f, PlasmaSize, 0f, 0f),
        };
        _renderer.UpdateBuffer<ComputeParamsGpu>(_paramsBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));

        // Simulation first, offscreen — any number of these may precede the presenting submit.
        _computeWriter.ResetWrittenCount();
        var compute = new RenderCommandEncoder(_computeWriter);
        compute.BeginComputePass();
        compute.SetComputePipeline(_computePipeline);
        compute.SetBindGroup(0, _computeGroup);
        compute.Dispatch(new DispatchCommand(PlasmaSize / 8, PlasmaSize / 8, 1));
        compute.EndComputePass();
        _renderer.SubmitOffscreen(new RenderCommandStream(_computeWriter.WrittenMemory, ReadOnlyMemory<RenderPassDesc>.Empty));

        // Then the presenting frame, which sees the compute results by queue order.
        _displayWriter.ResetWrittenCount();
        var display = new RenderCommandEncoder(_displayWriter);
        display.BeginPass(0);
        display.SetPipeline(_displayPipeline);
        display.SetBindGroup(0, _displayGroup);
        display.Draw(new DrawCommand(3, 1, 0, 0));
        display.EndPass();
        _renderer.Submit(new RenderCommandStream(_displayWriter.WrittenMemory, _passes));
    }

    public void Dispose()
    {
        _renderer.DestroyBindGroup(_displayGroup);
        _renderer.DestroyBindGroup(_computeGroup);
        _renderer.DestroySampler(_sampler);
        _renderer.DestroyBuffer(_paramsBuffer);
        _renderer.DestroyTextureView(_plasmaView);
        _renderer.DestroyTexture(_plasmaTexture);
        _renderer.DestroyPipeline(_displayPipeline);
        _renderer.DestroyComputePipeline(_computePipeline);
    }
}
