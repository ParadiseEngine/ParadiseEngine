using System;
using System.Collections.Generic;
using WebGpuSharp;

namespace Paradise.Ui.ImGui;

/// <summary>WebGPU renderer for <see cref="ImGuiDrawSnapshot"/>s — a managed port of the
/// official <c>imgui_impl_wgpu</c> backend, on Dear ImGui 1.92's dynamic-texture protocol.
///
/// <b>Textures arrive as work orders, not as an atlas.</b> There is no font-atlas upload here:
/// 1.92 owns its textures and asks the backend to create, patch and free them as fonts are
/// loaded and glyphs are rasterized on demand. Those requests reach this class as a queue of
/// <see cref="ImGuiTextureOp"/>s (see <see cref="ImGuiTextureCapture"/> for how they are read
/// off the ImGui thread), drained by <see cref="ApplyTextureOps"/> before each frame's
/// <see cref="Render"/>. Textures the HOST owns — a scene render target shown in a panel — are
/// still handed over directly by <see cref="RegisterTexture"/>, under ids at or above
/// <see cref="FirstHostTextureId"/> so the two id spaces cannot collide.
///
/// One pipeline: pos2f/uv2f/col-unorm8x4 vertices, straight-alpha SrcOver blending, ortho
/// projection from the snapshot's display rect, per-command scissor. Draws with
/// <c>LoadOp.Load</c> so the UI composites over whatever the frame already contains. Runs
/// entirely on the render thread; snapshots and texture ops arrive from the ImGui thread.</summary>
public sealed class ImGuiWebGpuRenderer
{
    /// <summary>Where host-owned texture ids start. ImGui numbers its own textures from a
    /// counter that starts at 1 and increments once per texture ever created, so a host that
    /// registers above this bound cannot collide with it in any plausible session — while
    /// still leaving both spaces plain integers a draw command can carry.</summary>
    public const ulong FirstHostTextureId = 1UL << 32;

    /// <summary>How many <see cref="ApplyTextureOps"/> calls a destroyed texture is kept alive
    /// for. A <see cref="ImGuiTextureOpKind.Destroy"/> means ImGui has stopped REFERENCING the
    /// texture, not that the GPU has stopped reading it: snapshots already submitted, and the
    /// one in flight, may still name its id. Three frames is comfortably past the deepest
    /// pipelining the handoff allows (one rendering + one latest + one being captured).</summary>
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

    /// <summary>Apply every operation in <paramref name="ops"/>, in order — the list
    /// <c>ImGuiFrameExchange.AcquireForRender</c> just filled. Render thread only, once per
    /// frame, BEFORE <see cref="Render"/>: the snapshot from that same acquire may name a texture
    /// these ops are what create.
    ///
    /// Applying every op rather than the newest per texture is deliberate: the queue is a state
    /// machine (create → update → destroy), and collapsing it would upload glyph patches into a
    /// texture that does not exist yet.</summary>
    public void ApplyTextureOps(IReadOnlyList<ImGuiTextureOp> ops)
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
            // Only reachable if a Create was lost, which the ops queue is built not to allow —
            // so this is a loud bug report, not a recoverable state.
            throw new InvalidOperationException(
                $"ImGui texture {op.TextureId} was updated before it was created — a texture op was dropped.");
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

    /// <summary>Retire <paramref name="id"/>: the GPU object AND its lookup entry wait out
    /// <see cref="DestroyDelayFrames"/> together.
    ///
    /// Dropping the lookup here while keeping the texture — which is what this did — made the
    /// delay pointless, and failed with the same signature the Create path is careful about. A
    /// snapshot the render thread still holds names this id, a command whose id resolves to no
    /// bind group is SKIPPED, so the UI vanishes for as long as that snapshot is redrawn with
    /// nothing in the geometry path to say why. The window is not theoretical:
    /// <see cref="ImGuiFrameExchange.AcquireForRender"/> returns the SAME snapshot when nothing
    /// new was published and drains the op queue anyway, so a sim tick that has enqueued its ops
    /// and not yet published its snapshot is exactly that state.</summary>
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

    /// <summary>The bind group for <paramref name="textureId"/>, or false when nothing is
    /// registered under it and the command must be skipped.
    ///
    /// There is no fallback texture on purpose. Under the static-atlas model an unknown id could
    /// borrow the font atlas and still look roughly right; with dynamic textures a missing id
    /// means an op was dropped or a host texture was unregistered while still referenced, and
    /// painting the font atlas over the geometry would disguise exactly the bug worth seeing.</summary>
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
