using System;
using System.Collections.Generic;
using WebGpuSharp;

namespace Paradise.Ui.ImGui;

/// <summary>WebGPU renderer for <see cref="ImGuiDrawSnapshot"/>s — a managed port of the
/// official <c>imgui_impl_wgpu</c> backend, reduced to the classic static-font-atlas model
/// (no 1.92 dynamic-texture protocol: its create/update statuses are the documented
/// cross-thread hazard, and the font atlas is uploaded once at construction instead).
///
/// One pipeline: pos2f/uv2f/col-unorm8x4 vertices, straight-alpha SrcOver blending, ortho
/// projection from the snapshot's display rect, per-command scissor. Draws with
/// <c>LoadOp.Load</c> so the UI composites over whatever the frame already contains. Runs
/// entirely on the render thread; snapshots arrive from the ImGui thread.</summary>
public sealed class ImGuiWebGpuRenderer
{
    /// <summary>Texture id the font atlas registers under (set it via
    /// <c>io.Fonts.SetTexID(ImGuiWebGpuRenderer.FontTextureId)</c>).</summary>
    public static readonly nint FontTextureId = 1;

    private readonly Device _device;
    private readonly Queue _queue;
    private readonly RenderPipeline _pipeline;
    private readonly WebGpuSharp.Buffer _uniformBuffer;
    private readonly Sampler _sampler;
    private readonly BindGroupLayout _bindGroupLayout;
    private readonly Dictionary<nint, BindGroup> _bindGroups = new();
    private readonly Dictionary<nint, TextureView> _textures = new();
    private WebGpuSharp.Buffer? _vertexBuffer;
    private WebGpuSharp.Buffer? _indexBuffer;
    private ulong _vertexCapacity;
    private ulong _indexCapacity;

    public ImGuiWebGpuRenderer(Device device, TextureFormat colorFormat)
    {
        _device = device;
        _queue = device.GetQueue() ?? throw new InvalidOperationException("Device has no queue.");

        _uniformBuffer = device.CreateBuffer(new BufferDescriptor
        {
            Label = "ImGui.Uniforms",
            Size = 64,
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
            MappedAtCreation = false,
        }) ?? throw new InvalidOperationException("ImGui uniform buffer creation failed.");

        _sampler = device.CreateSampler(new SamplerDescriptor
        {
            Label = "ImGui.Sampler",
            MinFilter = FilterMode.Linear,
            MagFilter = FilterMode.Linear,
            MipmapFilter = MipmapFilterMode.Nearest,
            AddressModeU = AddressMode.ClampToEdge,
            AddressModeV = AddressMode.ClampToEdge,
            AddressModeW = AddressMode.ClampToEdge,
        }) ?? throw new InvalidOperationException("ImGui sampler creation failed.");

        _bindGroupLayout = device.CreateBindGroupLayout(new BindGroupLayoutDescriptor
        {
            Label = "ImGui.BindGroupLayout",
            Entries =
            [
                new BindGroupLayoutEntry
                {
                    Binding = 0,
                    Visibility = ShaderStage.Vertex,
                    Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform, MinBindingSize = 64 },
                },
                new BindGroupLayoutEntry
                {
                    Binding = 1,
                    Visibility = ShaderStage.Fragment,
                    Texture = new TextureBindingLayout { SampleType = TextureSampleType.Float, ViewDimension = TextureViewDimension.D2, Multisampled = false },
                },
                new BindGroupLayoutEntry
                {
                    Binding = 2,
                    Visibility = ShaderStage.Fragment,
                    Sampler = new SamplerBindingLayout { Type = SamplerBindingType.Filtering },
                },
            ],
        }) ?? throw new InvalidOperationException("ImGui bind group layout creation failed.");
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDescriptor
        {
            BindGroupLayouts = [_bindGroupLayout],
        }) ?? throw new InvalidOperationException("ImGui pipeline layout creation failed.");

        var moduleDesc = new ShaderModuleWGSLDescriptor { Code = Wgsl };
        var module = _device.CreateShaderModuleWGSL("ImGui", in moduleDesc)
            ?? throw new InvalidOperationException("ImGui WGSL compile failed.");
        var vertexLayout = new VertexBufferLayout
        {
            ArrayStride = ImGuiDrawSnapshot.VertexStride,
            StepMode = VertexStepMode.Vertex,
            Attributes = new VertexAttribute[]
            {
                new() { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 },
                new() { Format = VertexFormat.Float32x2, Offset = 8, ShaderLocation = 1 },
                new() { Format = VertexFormat.Unorm8x4, Offset = 16, ShaderLocation = 2 },
            },
        };
        var colorTargets = new ColorTargetState[]
        {
            new()
            {
                Format = colorFormat,
                // ImGui emits straight (non-premultiplied) alpha.
                Blend = new BlendState
                {
                    Color = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.SrcAlpha, DstFactor = BlendFactor.OneMinusSrcAlpha },
                    Alpha = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha },
                },
                WriteMask = ColorWriteMask.All,
            },
        };
        var pipelineDesc = new RenderPipelineDescriptor
        {
            Label = "ImGui",
            Layout = pipelineLayout,
            Vertex = new VertexState
            {
                Module = module,
                EntryPoint = "vs_main",
                Buffers = new WebGpuManagedSpan<VertexBufferLayout>(new[] { vertexLayout }),
            },
            Fragment = new FragmentState
            {
                Module = module,
                EntryPoint = "fs_main",
                Targets = new WebGpuManagedSpan<ColorTargetState>(colorTargets),
            },
            Primitive = new PrimitiveState { Topology = PrimitiveTopology.TriangleList },
            Multisample = new MultisampleState { Count = 1, Mask = uint.MaxValue },
        };
        _pipeline = _device.CreateRenderPipelineSync(in pipelineDesc)
            ?? throw new InvalidOperationException("ImGui pipeline creation failed.");
    }

    /// <summary>Upload the (static) font atlas and register it under
    /// <see cref="FontTextureId"/>. Call once, before the first <see cref="Render"/>.</summary>
    public void SetFontAtlas(ReadOnlySpan<byte> rgba, uint width, uint height)
    {
        var texture = _device.CreateTexture(new TextureDescriptor
        {
            Label = "ImGui.FontAtlas",
            Size = new Extent3D(width, height, 1),
            Format = TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.TextureBinding | TextureUsage.CopyDst,
            MipLevelCount = 1,
            SampleCount = 1,
            Dimension = TextureDimension.D2,
        }) ?? throw new InvalidOperationException("ImGui font atlas creation failed.");
        var destination = new TexelCopyTextureInfo { Texture = texture, MipLevel = 0 };
        var layout = new TexelCopyBufferLayout { Offset = 0, BytesPerRow = width * 4, RowsPerImage = height };
        var extent = new Extent3D(width, height, 1);
        _queue.WriteTexture(destination, rgba, layout, extent);
        RegisterTexture(FontTextureId, texture.CreateView()!);
    }

    /// <summary>Expose an arbitrary texture view to ImGui draws under <paramref name="id"/>
    /// (use the id as <c>ImTextureID</c>).</summary>
    public void RegisterTexture(nint id, TextureView view)
    {
        _textures[id] = view;
        _bindGroups.Remove(id);
    }

    /// <summary>Record one snapshot into <paramref name="encoder"/>, compositing over
    /// <paramref name="target"/>. Render thread only.</summary>
    public void Render(CommandEncoder encoder, TextureView target, uint targetWidth, uint targetHeight, ImGuiDrawSnapshot snapshot)
    {
        if (snapshot.CommandCount == 0 || snapshot.VertexBytes == 0 ||
            snapshot.DisplaySize.X <= 0 || snapshot.DisplaySize.Y <= 0)
        {
            return;
        }

        EnsureBuffer(ref _vertexBuffer, ref _vertexCapacity, (ulong)snapshot.VertexBytes, BufferUsage.Vertex | BufferUsage.CopyDst, "ImGui.VB");
        EnsureBuffer(ref _indexBuffer, ref _indexCapacity, (ulong)snapshot.IndexBytes, BufferUsage.Index | BufferUsage.CopyDst, "ImGui.IB");
        _queue.WriteBuffer(_vertexBuffer!, 0, snapshot.Vertices.AsSpan(0, snapshot.VertexBytes));
        _queue.WriteBuffer(_indexBuffer!, 0, snapshot.Indices.AsSpan(0, AlignIndexBytes(snapshot.IndexBytes)));
        _queue.WriteBuffer(_uniformBuffer, 0, Orthographic(snapshot));

        var colors = new RenderPassColorAttachment[]
        {
            new() { View = target, LoadOp = LoadOp.Load, StoreOp = StoreOp.Store, DepthSlice = null },
        };
        var passDesc = new RenderPassDescriptor { Label = "ImGui", ColorAttachments = colors };
        var pass = encoder.BeginRenderPass(in passDesc);
        pass.SetPipeline(_pipeline);
        pass.SetVertexBuffer(0, _vertexBuffer!, 0, _vertexCapacity);
        pass.SetIndexBuffer(_indexBuffer!, IndexFormat.Uint16, 0, _indexCapacity);

        var clipScale = snapshot.FramebufferScale;
        var clipOffset = snapshot.DisplayPosition;
        for (var i = 0; i < snapshot.CommandCount; i++)
        {
            var command = snapshot.Commands[i];
            var x0 = (command.ClipRect.X - clipOffset.X) * clipScale.X;
            var y0 = (command.ClipRect.Y - clipOffset.Y) * clipScale.Y;
            var x1 = (command.ClipRect.Z - clipOffset.X) * clipScale.X;
            var y1 = (command.ClipRect.W - clipOffset.Y) * clipScale.Y;
            var sx = (uint)Math.Clamp(x0, 0, targetWidth);
            var sy = (uint)Math.Clamp(y0, 0, targetHeight);
            var sw = (uint)Math.Clamp(x1, 0, targetWidth) - sx;
            var sh = (uint)Math.Clamp(y1, 0, targetHeight) - sy;
            if (sw == 0 || sh == 0) continue;

            pass.SetScissorRect(sx, sy, sw, sh);
            pass.SetBindGroup(0, GetBindGroup(command.TextureId));
            pass.DrawIndexed(command.ElementCount, 1, command.IndexOffset, (int)command.VertexOffset, 0);
        }
        pass.End();
    }

    private static int AlignIndexBytes(int bytes) => (bytes + 3) & ~3; // WriteBuffer needs 4B multiples

    private static float[] Orthographic(ImGuiDrawSnapshot snapshot)
    {
        var left = snapshot.DisplayPosition.X;
        var right = left + snapshot.DisplaySize.X;
        var top = snapshot.DisplayPosition.Y;
        var bottom = top + snapshot.DisplaySize.Y;
        // Column-major (WGSL mat4x4 memory order), z pinned to 0.5.
        return
        [
            2f / (right - left), 0f, 0f, 0f,
            0f, 2f / (top - bottom), 0f, 0f,
            0f, 0f, 1f, 0f,
            (right + left) / (left - right), (top + bottom) / (bottom - top), 0.5f, 1f,
        ];
    }

    private void EnsureBuffer(ref WebGpuSharp.Buffer? buffer, ref ulong capacity, ulong needed, BufferUsage usage, string label)
    {
        needed = (needed + 3ul) & ~3ul;
        if (buffer is not null && capacity >= needed) return;
        buffer?.Destroy();
        capacity = Math.Max(64 * 1024, System.Numerics.BitOperations.RoundUpToPowerOf2(needed));
        buffer = _device.CreateBuffer(new BufferDescriptor
        {
            Label = label,
            Size = capacity,
            Usage = usage,
            MappedAtCreation = false,
        }) ?? throw new InvalidOperationException($"{label}: buffer creation failed.");
    }

    private BindGroup GetBindGroup(nint textureId)
    {
        if (_bindGroups.TryGetValue(textureId, out var cached)) return cached;
        if (!_textures.TryGetValue(textureId, out var view))
        {
            // Unknown id — fall back to the font atlas so the draw stays visible.
            view = _textures[FontTextureId];
        }
        var bindGroup = _device.CreateBindGroup(new BindGroupDescriptor
        {
            Label = "ImGui.BindGroup",
            Layout = _bindGroupLayout,
            Entries =
            [
                new BindGroupEntry { Binding = 0, Buffer = _uniformBuffer, Offset = 0, Size = 64 },
                new BindGroupEntry { Binding = 1, TextureView = view },
                new BindGroupEntry { Binding = 2, Sampler = _sampler },
            ],
        }) ?? throw new InvalidOperationException("ImGui bind group creation failed.");
        _bindGroups[textureId] = bindGroup;
        return bindGroup;
    }

    private const string Wgsl = """
        struct Uniforms { mvp: mat4x4<f32> }
        @group(0) @binding(0) var<uniform> u: Uniforms;
        @group(0) @binding(1) var tex: texture_2d<f32>;
        @group(0) @binding(2) var samp: sampler;

        struct VsOut {
            @builtin(position) pos: vec4<f32>,
            @location(0) uv: vec2<f32>,
            @location(1) color: vec4<f32>,
        }

        @vertex fn vs_main(
            @location(0) pos: vec2<f32>,
            @location(1) uv: vec2<f32>,
            @location(2) color: vec4<f32>) -> VsOut {
            var o: VsOut;
            o.pos = u.mvp * vec4<f32>(pos, 0.0, 1.0);
            o.uv = uv;
            o.color = color;
            return o;
        }

        @fragment fn fs_main(i: VsOut) -> @location(0) vec4<f32> {
            return i.color * textureSample(tex, samp, i.uv);
        }
        """;
}
