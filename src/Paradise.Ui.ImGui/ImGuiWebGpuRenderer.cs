using System;
using System.Collections.Generic;
using WebGpuSharp;

namespace Paradise.Ui.ImGui;

/// <summary>Renders ImGui snapshots as a WebGPU overlay on the render thread.</summary>
/// <remarks>Apply ordered texture operations before Render; host textures use RegisterTexture and a
/// separate ID range. The pipeline uses straight-alpha blending, orthographic projection,
/// per-command scissors and LoadOp.Load.</remarks>
public sealed class ImGuiWebGpuRenderer : IDisposable
{
    /// <summary>Where host-owned texture ids start. ImGui numbers its own textures from a
    /// counter that starts at 1 and increments once per texture ever created, so a host that
    /// registers above this bound cannot collide with it in any plausible session — while
    /// still leaving both spaces plain integers a draw command can carry.</summary>
    public const ulong FirstHostTextureId = 1UL << 32;

    /// <summary>Keeps retired textures alive across three ApplyTextureOps calls.</summary>
    /// <remarks>The delay covers rendering, published and captured snapshots that may still
    /// reference the texture.</remarks>
    private const int DestroyDelayFrames = 3;

    private readonly Device _device;
    private readonly Queue _queue;
    private readonly RenderPipeline _pipeline;
    private readonly WebGpuSharp.Buffer _uniformBuffer;
    private readonly Sampler _sampler;
    private readonly BindGroupLayout _bindGroupLayout;
    private readonly Dictionary<ulong, BindGroup> _bindGroups = new();
    private readonly Dictionary<ulong, TextureView> _textures = new();
    /// <summary>Textures this renderer allocated itself (ImGui's), which it therefore has to
    /// free. Host textures in <see cref="_textures"/> are the host's to dispose.</summary>
    private readonly Dictionary<ulong, Texture> _ownedTextures = new();
    private readonly List<RetiredTexture> _retiring = new();
    private WebGpuSharp.Buffer? _vertexBuffer;
    private WebGpuSharp.Buffer? _indexBuffer;
    private ulong _vertexCapacity;
    private ulong _indexCapacity;
    private bool _disposed;

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

    /// <summary>Applies texture operations in order and clears the list once they have been
    /// applied.</summary>
    /// <remarks>Call once per render frame before Render. AcquireForRender appends so skipped
    /// frames retain work; do not coalesce the create/update/destroy sequence.</remarks>
    public void ApplyTextureOps(List<ImGuiTextureOp> ops)
    {
        ArgumentNullException.ThrowIfNull(ops);
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case ImGuiTextureOpKind.Create:
                    CreateTexture(op);
                    break;
                case ImGuiTextureOpKind.Update:
                    UpdateTexture(op);
                    break;
                case ImGuiTextureOpKind.Destroy:
                    RetireTexture(op.TextureId);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ops), op.Kind, "Unknown ImGui texture op.");
            }
        }
        // Only once every op landed. A throw part-way leaves the list intact, so the next call
        // replays it: a Create for a live id retires and recreates, an Update finds its texture,
        // a Destroy for an already-retired id is a no-op. Wasteful, and correct.
        ops.Clear();
        AgeRetiredTextures();
    }

    /// <summary>Expose an arbitrary HOST-owned texture view to ImGui draws under
    /// <paramref name="id"/> (use the id as <c>ImTextureID</c>). The view stays the caller's to
    /// keep alive and dispose; this renderer only maps it.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> is below
    /// <see cref="FirstHostTextureId"/>, where it could collide with an ImGui-owned texture.</exception>
    public void RegisterTexture(ulong id, TextureView view)
    {
        if (id < FirstHostTextureId)
        {
            throw new ArgumentOutOfRangeException(
                nameof(id), id, $"Host texture ids start at {FirstHostTextureId} — below that is ImGui's own id space.");
        }
        _textures[id] = view;
        _bindGroups.Remove(id);
    }

    /// <summary>Removes a host texture registration and its cached bind group.</summary>
    /// <remarks>Call before destroying the host view. ImGui-owned textures must retire through
    /// Destroy operations to preserve their delayed resource and lookup lifetime.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> is below
    /// <see cref="FirstHostTextureId"/>, so it belongs to ImGui rather than the host.</exception>
    public void UnregisterTexture(ulong id)
    {
        if (id < FirstHostTextureId)
        {
            throw new ArgumentOutOfRangeException(
                nameof(id), id, $"Host texture ids start at {FirstHostTextureId}; an ImGui-owned texture is retired by its Destroy op.");
        }
        _textures.Remove(id);
        _bindGroups.Remove(id);
    }

    /// <summary>Releases owned textures and buffers, including delayed retirements.</summary>
    /// <remarks>Idempotent; registered host textures remain host-owned. WebGPUSharp exposes
    /// deterministic destruction only for textures and buffers; other handles use
    /// finalizers.</remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var texture in _ownedTextures.Values) texture.Destroy();
        _ownedTextures.Clear();
        foreach (var retired in _retiring) retired.Texture.Destroy();
        _retiring.Clear();
        _textures.Clear();
        _bindGroups.Clear();
        _vertexBuffer?.Destroy();
        _indexBuffer?.Destroy();
        _uniformBuffer.Destroy();
    }

    private void CreateTexture(in ImGuiTextureOp op)
    {
        // ImGui reuses a UniqueID only after the old texture is destroyed, but a create for an
        // id we still hold would otherwise leak the old one silently.
        RetireTexture(op.TextureId);
        var texture = _device.CreateTexture(new TextureDescriptor
        {
            Label = $"ImGui.Texture{op.TextureId}",
            Size = new Extent3D(op.Width, op.Height, 1),
            Format = TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.TextureBinding | TextureUsage.CopyDst,
            MipLevelCount = 1,
            SampleCount = 1,
            Dimension = TextureDimension.D2,
        }) ?? throw new InvalidOperationException($"ImGui texture {op.TextureId} creation failed.");
        _ownedTextures[op.TextureId] = texture;
        _textures[op.TextureId] = texture.CreateView()!;
        _bindGroups.Remove(op.TextureId);
        Write(texture, op);
    }

    private void UpdateTexture(in ImGuiTextureOp op)
    {
        if (!_ownedTextures.TryGetValue(op.TextureId, out var texture))
        {
            // A missing Create indicates the caller discarded a drained operation list; identify
            // that ownership error here.
            throw new InvalidOperationException(
                $"ImGui texture {op.TextureId} was updated before it was created. Its Create op " +
                $"never reached {nameof(ApplyTextureOps)}: pass the same list to every " +
                "AcquireForRender and let ApplyTextureOps be the only thing that clears it.");
        }
        Write(texture, op);
    }

    private void Write(Texture texture, in ImGuiTextureOp op)
    {
        var destination = new TexelCopyTextureInfo
        {
            Texture = texture,
            MipLevel = 0,
            Origin = new Origin3D(op.X, op.Y, 0),
        };
        var layout = new TexelCopyBufferLayout
        {
            Offset = 0,
            BytesPerRow = op.Width * ImGuiTextureOp.BytesPerPixel,
            RowsPerImage = op.Height,
        };
        _queue.WriteTexture(destination, op.Pixels, layout, new Extent3D(op.Width, op.Height, 1));
    }

    /// <summary>Retires a texture and its lookup entry after DestroyDelayFrames.</summary>
    /// <remarks>Repeated snapshots may still name the ID while a Destroy operation arrives;
    /// removing its lookup early would hide their glyphs.</remarks>
    private void RetireTexture(ulong id)
    {
        if (!_ownedTextures.Remove(id, out var texture)) return;
        _textures.TryGetValue(id, out var view);
        _retiring.Add(new RetiredTexture(id, texture, view, DestroyDelayFrames));
    }

    private void AgeRetiredTextures()
    {
        for (var i = _retiring.Count - 1; i >= 0; i--)
        {
            var retired = _retiring[i];
            if (retired.FramesLeft > 1)
            {
                _retiring[i] = retired with { FramesLeft = retired.FramesLeft - 1 };
                continue;
            }
            // Only drop the lookup if it still names the texture being freed: CreateTexture
            // retires an id it is about to re-register, and that newer entry has to outlive its
            // predecessor's wait.
            if (_textures.TryGetValue(retired.Id, out var current) && ReferenceEquals(current, retired.View))
            {
                _textures.Remove(retired.Id);
                _bindGroups.Remove(retired.Id);
            }
            retired.Texture.Destroy();
            _retiring.RemoveAt(i);
        }
    }

    /// <param name="View">The view registered when the texture was retired, so aging can tell a
    /// stale entry from one a later Create put back under the same id.</param>
    private readonly record struct RetiredTexture(ulong Id, Texture Texture, TextureView? View, int FramesLeft);

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
            if (!TryGetBindGroup(command.TextureId, out var bindGroup)) continue;

            pass.SetScissorRect(sx, sy, sw, sh);
            pass.SetBindGroup(0, bindGroup);
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

    /// <summary>Gets a registered texture's bind group, or returns false to skip the
    /// draw.</summary>
    /// <remarks>A missing ID indicates a lost operation or premature unregistration; a fallback
    /// texture would conceal that error.</remarks>
    private bool TryGetBindGroup(ulong textureId, out BindGroup bindGroup)
    {
        if (_bindGroups.TryGetValue(textureId, out var cached))
        {
            bindGroup = cached;
            return true;
        }
        if (!_textures.TryGetValue(textureId, out var view))
        {
            bindGroup = null!;
            return false;
        }
        bindGroup = _device.CreateBindGroup(new BindGroupDescriptor
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
        return true;
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
