using System;
using System.Collections.Generic;
using System.Numerics;
using Paradise.Rendering;

namespace Paradise.Ui.ImGui;

/// <summary>Renders ImGui snapshots through any <see cref="IRenderer"/>: WebGPU on the desktop, the
/// browser backend in WebAssembly, or a recording fake in a test.</summary>
/// <remarks>
/// The renderer-neutral twin of <see cref="ImGuiWebGpuRenderer"/>, with the same texture protocol
/// (ordered <see cref="ImGuiTextureOp"/>s, a fixed retirement delay, host textures from
/// <see cref="FirstHostTextureId"/>) and the same output: straight-alpha blending, an orthographic
/// projection and a scissor per command. It records draws into a render pass the caller opens over
/// its color target with <see cref="LoadOp.Load"/>, so the overlay rides in the frame's own stream
/// rather than a native callback.
/// <para>Per frame, on the render thread: <see cref="ApplyTextureOps"/>, then <see cref="Record"/>
/// inside the pass, then submit the stream. <see cref="Record"/> uploads the snapshot's buffers
/// immediately, so record one snapshot per submitted stream.</para>
/// </remarks>
public sealed class ImGuiRenderer : IDisposable
{
    /// <summary>Where host-owned texture ids start; the same id space as
    /// <see cref="ImGuiWebGpuRenderer.FirstHostTextureId"/>.</summary>
    public const ulong FirstHostTextureId = ImGuiWebGpuRenderer.FirstHostTextureId;

    /// <summary>Ages retired textures over three successful ApplyTextureOps calls, including retirement.</summary>
    private const int DestroyDelayFrames = 3;

    private const ulong UniformBytes = 64;

    private static readonly BindGroupLayoutDesc s_group = new(0,
    [
        new BindGroupLayoutEntryDesc(0, ShaderStage.Vertex, BindingResourceType.UniformBuffer, MinBufferSize: UniformBytes),
        new BindGroupLayoutEntryDesc(1, ShaderStage.Fragment, BindingResourceType.SampledTexture),
        new BindGroupLayoutEntryDesc(2, ShaderStage.Fragment, BindingResourceType.Sampler),
    ]);

    private readonly IRenderer _renderer;
    private readonly PipelineHandle _pipeline;
    private readonly BufferHandle _uniforms;
    private readonly SamplerHandle _sampler;
    private readonly Dictionary<ulong, BindGroupHandle> _bindGroups = new();
    private readonly Dictionary<ulong, TextureSource> _textures = new();
    /// <summary>Textures this renderer allocated (ImGui's), which it frees; host views are the host's.</summary>
    private readonly Dictionary<ulong, TextureHandle> _ownedTextures = new();
    private readonly List<RetiredTexture> _retiring = new();
    private BufferHandle _vertexBuffer;
    private BufferHandle _indexBuffer;
    private ulong _vertexCapacity;
    private ulong _indexCapacity;
    private bool _disposed;

    /// <param name="renderer">The backend the overlay draws through.</param>
    /// <param name="colorFormat">The format of the target the pass renders into; the renderer's own
    /// <see cref="IRenderer.ColorFormat"/> when null.</param>
    public ImGuiRenderer(IRenderer renderer, TextureFormat? colorFormat = null)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        _renderer = renderer;
        _uniforms = renderer.CreateBuffer(new BufferDesc("ImGui.Uniforms", UniformBytes, BufferUsage.Uniform | BufferUsage.CopyDst));
        _sampler = renderer.CreateSampler(new SamplerDesc(
            "ImGui.Sampler",
            SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge,
            SamplerFilterMode.Linear, SamplerFilterMode.Linear, SamplerFilterMode.Nearest));
        _pipeline = renderer.CreatePipeline(
            Program,
            colorFormat ?? renderer.ColorFormat,
            PrimitiveTopology.TriangleList,
            blend: BlendMode.AlphaBlend,
            depthWriteEnabled: false);
    }

    /// <summary>The overlay's shader program: WGSL with a hand-written layout, so it needs no shader
    /// toolchain. One group: the projection, the command's texture and a linear sampler.</summary>
    public static ShaderProgramDesc Program { get; } = new(
        [
            new ShaderModuleDesc(Wgsl, "vs_main", ShaderStage.Vertex),
            new ShaderModuleDesc(Wgsl, "fs_main", ShaderStage.Fragment),
        ],
        new PipelineLayoutDesc([s_group], []),
        [
            new VertexBufferLayoutDesc(ImGuiDrawSnapshot.VertexStride, VertexStepMode.Vertex,
            [
                new VertexAttributeDesc(0, VertexFormat.Float32x2, 0),
                new VertexAttributeDesc(1, VertexFormat.Float32x2, 8),
                new VertexAttributeDesc(2, VertexFormat.Unorm8x4, 16),
            ]),
        ])
    {
        UniformBlocks = [new UniformBlockDesc("Uniforms", 0, 0, (uint)UniformBytes, [new UniformFieldDesc("mvp", 0, (uint)UniformBytes)])],
    };

    /// <summary>Applies texture operations in order and clears the list once they have been applied.</summary>
    /// <remarks>Call once per render frame before <see cref="Record"/>, with the same list every
    /// <c>AcquireForRender</c> appends to, so skipped frames keep their work.</remarks>
    public void ApplyTextureOps(List<ImGuiTextureOp> ops)
    {
        ArgumentNullException.ThrowIfNull(ops);
        ObjectDisposedException.ThrowIf(_disposed, this);
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

        // A failure leaves the list intact; replaying is safe (Create replaces, Destroy repeats as a no-op).
        ops.Clear();
        AgeRetiredTextures();
    }

    /// <summary>Exposes a host-owned texture view to ImGui draws under <paramref name="id"/>. The view
    /// stays the caller's to keep alive and destroy.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> is below <see cref="FirstHostTextureId"/>.</exception>
    public void RegisterTexture(ulong id, TextureViewHandle view)
    {
        if (id < FirstHostTextureId)
        {
            throw new ArgumentOutOfRangeException(
                nameof(id), id, $"Host texture ids start at {FirstHostTextureId} — below that is ImGui's own id space.");
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        DropBindGroup(id);
        _textures[id] = TextureSource.Host(view);
    }

    /// <summary>Removes a host texture registration; call before destroying the view.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> is below <see cref="FirstHostTextureId"/>.</exception>
    public void UnregisterTexture(ulong id)
    {
        if (id < FirstHostTextureId)
        {
            throw new ArgumentOutOfRangeException(
                nameof(id), id, $"Host texture ids start at {FirstHostTextureId}; an ImGui-owned texture is retired by its Destroy op.");
        }

        DropBindGroup(id);
        _textures.Remove(id);
    }

    /// <summary>Uploads <paramref name="snapshot"/>'s geometry and records its draws into the render pass
    /// open on <paramref name="encoder"/>, which targets a <paramref name="targetWidth"/> x
    /// <paramref name="targetHeight"/> attachment.</summary>
    /// <remarks>Commands whose clip rectangle falls outside the target, or whose texture is unknown or
    /// retired, are skipped. The scissor is left at the last command's rectangle.</remarks>
    public void Record(ref RenderCommandEncoder encoder, ImGuiDrawSnapshot snapshot, uint targetWidth, uint targetHeight)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (snapshot.CommandCount == 0 || snapshot.VertexBytes == 0 ||
            snapshot.DisplaySize.X <= 0 || snapshot.DisplaySize.Y <= 0 || targetWidth == 0 || targetHeight == 0)
        {
            return;
        }

        EnsureBuffer(ref _vertexBuffer, ref _vertexCapacity, (ulong)snapshot.VertexBytes, BufferUsage.Vertex | BufferUsage.CopyDst, "ImGui.VB");
        EnsureBuffer(ref _indexBuffer, ref _indexCapacity, (ulong)snapshot.IndexBytes, BufferUsage.Index | BufferUsage.CopyDst, "ImGui.IB");
        _renderer.UpdateBuffer<byte>(_vertexBuffer, 0, snapshot.Vertices.AsSpan(0, AlignTo4(snapshot.VertexBytes)));
        // Uploads must be whole words; the snapshot's buffers grow in KiB, so the padded tail is there.
        _renderer.UpdateBuffer<byte>(_indexBuffer, 0, snapshot.Indices.AsSpan(0, AlignTo4(snapshot.IndexBytes)));
        var projection = Orthographic(snapshot);
        _renderer.UpdateBuffer(_uniforms, 0, new ReadOnlySpan<Matrix4x4>(in projection));

        encoder.SetPipeline(_pipeline);
        encoder.SetVertexBuffer(0, _vertexBuffer, 0, _vertexCapacity);
        encoder.SetIndexBuffer(_indexBuffer, IndexFormat.Uint16, 0, _indexCapacity);

        var clipScale = snapshot.FramebufferScale;
        var clipOffset = snapshot.DisplayPosition;
        var bound = default(BindGroupHandle);
        for (var i = 0; i < snapshot.CommandCount; i++)
        {
            var command = snapshot.Commands[i];
            if (Scissor(command.ClipRect, clipOffset, clipScale, targetWidth, targetHeight) is not { } rect) continue;
            if (!TryGetBindGroup(command.TextureId, out var group)) continue;

            encoder.SetScissorRect(rect.X, rect.Y, rect.Width, rect.Height);
            if (group != bound)
            {
                encoder.SetBindGroup(0, group);
                bound = group;
            }

            encoder.DrawIndexed(new DrawIndexedCommand(command.ElementCount, 1, command.IndexOffset, (int)command.VertexOffset, 0));
        }
    }

    /// <summary>Releases owned textures, bind groups and buffers, including delayed retirements.</summary>
    /// <remarks>Idempotent; registered host views remain host-owned.</remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var group in _bindGroups.Values) _renderer.DestroyBindGroup(group);
        _bindGroups.Clear();
        foreach (var texture in _ownedTextures.Values) _renderer.DestroyTexture(texture);
        _ownedTextures.Clear();
        foreach (var retired in _retiring) _renderer.DestroyTexture(retired.Texture);
        _retiring.Clear();
        _textures.Clear();
        if (_vertexBuffer.IsValid) _renderer.DestroyBuffer(_vertexBuffer);
        if (_indexBuffer.IsValid) _renderer.DestroyBuffer(_indexBuffer);
        _renderer.DestroyBuffer(_uniforms);
        _renderer.DestroySampler(_sampler);
        _renderer.DestroyPipeline(_pipeline);
    }

    /// <summary>A command's clip rectangle in target pixels, clamped to the target; null when empty.</summary>
    internal static SetScissorRectPayload? Scissor(Vector4 clip, Vector2 offset, Vector2 scale, uint targetWidth, uint targetHeight)
    {
        var x0 = (uint)Math.Clamp((clip.X - offset.X) * scale.X, 0f, targetWidth);
        var y0 = (uint)Math.Clamp((clip.Y - offset.Y) * scale.Y, 0f, targetHeight);
        var x1 = (uint)Math.Clamp((clip.Z - offset.X) * scale.X, 0f, targetWidth);
        var y1 = (uint)Math.Clamp((clip.W - offset.Y) * scale.Y, 0f, targetHeight);
        return x1 > x0 && y1 > y0 ? new SetScissorRectPayload(x0, y0, x1 - x0, y1 - y0) : null;
    }

    private void CreateTexture(in ImGuiTextureOp op)
    {
        // A replayed Create can name a live ID; retire its old allocation before replacing it.
        RetireTexture(op.TextureId);
        var texture = _renderer.CreateTexture(new TextureDesc(
            $"ImGui.Texture{op.TextureId}", op.Width, op.Height, 1, 1, 1,
            TextureDimension.D2, TextureFormat.Rgba8Unorm, TextureUsage.TextureBinding | TextureUsage.CopyDst));
        _ownedTextures[op.TextureId] = texture;
        DropBindGroup(op.TextureId);
        _textures[op.TextureId] = TextureSource.Owned(texture);
        Write(texture, op);
    }

    private void UpdateTexture(in ImGuiTextureOp op)
    {
        if (!_ownedTextures.TryGetValue(op.TextureId, out var texture))
        {
            throw new InvalidOperationException(
                $"ImGui texture {op.TextureId} was updated before it was created. Its Create op " +
                $"never reached {nameof(ApplyTextureOps)}: pass the same list to every " +
                "AcquireForRender and let ApplyTextureOps be the only thing that clears it.");
        }

        Write(texture, op);
    }

    private void Write(TextureHandle texture, in ImGuiTextureOp op) =>
        _renderer.WriteTextureRegion(texture, 0, op.X, op.Y, op.Pixels, op.Width * ImGuiTextureOp.BytesPerPixel, op.Width, op.Height);

    /// <summary>Retires a texture and its lookup entry after DestroyDelayFrames, so snapshots still
    /// naming it keep drawing until they are gone.</summary>
    private void RetireTexture(ulong id)
    {
        if (!_ownedTextures.Remove(id, out var texture)) return;
        _retiring.Add(new RetiredTexture(id, texture, DestroyDelayFrames));
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

            // Only drop the lookup if it still names this texture: a Create under the same id has
            // registered a newer one that must outlive its predecessor's wait.
            if (_textures.TryGetValue(retired.Id, out var current) && current.Texture == retired.Texture)
            {
                DropBindGroup(retired.Id);
                _textures.Remove(retired.Id);
            }

            _renderer.DestroyTexture(retired.Texture);
            _retiring.RemoveAt(i);
        }
    }

    private bool TryGetBindGroup(ulong textureId, out BindGroupHandle group)
    {
        if (_bindGroups.TryGetValue(textureId, out group)) return true;
        if (!_textures.TryGetValue(textureId, out var source)) return false;

        BindGroupEntryDesc[] entries =
        [
            BindGroupEntryDesc.ForBuffer(0, _uniforms, 0, UniformBytes),
            source.Texture.IsValid ? BindGroupEntryDesc.ForTexture(1, source.Texture) : BindGroupEntryDesc.ForTextureView(1, source.View),
            BindGroupEntryDesc.ForSampler(2, _sampler),
        ];
        group = _renderer.CreateBindGroup(new BindGroupDesc("ImGui.BindGroup", s_group, entries));
        _bindGroups[textureId] = group;
        return true;
    }

    private void DropBindGroup(ulong id)
    {
        if (_bindGroups.Remove(id, out var group)) _renderer.DestroyBindGroup(group);
    }

    private void EnsureBuffer(ref BufferHandle buffer, ref ulong capacity, ulong needed, BufferUsage usage, string label)
    {
        needed = (needed + 3ul) & ~3ul;
        if (buffer.IsValid && capacity >= needed) return;
        if (buffer.IsValid) _renderer.DestroyBuffer(buffer);
        capacity = Math.Max(64 * 1024, BitOperations.RoundUpToPowerOf2(needed));
        buffer = _renderer.CreateBuffer(new BufferDesc(label, capacity, usage));
    }

    private static int AlignTo4(int bytes) => (bytes + 3) & ~3;

    private static Matrix4x4 Orthographic(ImGuiDrawSnapshot snapshot)
    {
        var left = snapshot.DisplayPosition.X;
        var right = left + snapshot.DisplaySize.X;
        var top = snapshot.DisplayPosition.Y;
        var bottom = top + snapshot.DisplaySize.Y;
        // Column-major (WGSL mat4x4 memory order), z pinned to 0.5.
        return new Matrix4x4(
            2f / (right - left), 0f, 0f, 0f,
            0f, 2f / (top - bottom), 0f, 0f,
            0f, 0f, 1f, 0f,
            (right + left) / (left - right), (top + bottom) / (bottom - top), 0.5f, 1f);
    }

    /// <summary>What a texture id samples: a texture this renderer owns, or a view the host registered.</summary>
    private readonly record struct TextureSource(TextureHandle Texture, TextureViewHandle View)
    {
        public static TextureSource Owned(TextureHandle texture) => new(texture, default);
        public static TextureSource Host(TextureViewHandle view) => new(default, view);
    }

    private readonly record struct RetiredTexture(ulong Id, TextureHandle Texture, int FramesLeft);

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
