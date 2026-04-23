using System;
using System.Reflection;
using Paradise.Rendering.WebGPU.Internal;
using WgWebGPU = WebGpuSharp.WebGPU;
using WgTextureView = WebGpuSharp.TextureView;
using WgTexture = WebGpuSharp.Texture;
using WgTextureDescriptor = WebGpuSharp.TextureDescriptor;
using WgTextureFormat = WebGpuSharp.TextureFormat;
using WgTextureUsage = WebGpuSharp.TextureUsage;
using WgTextureDimension = WebGpuSharp.TextureDimension;
using WgRenderPassDescriptor = WebGpuSharp.RenderPassDescriptor;
using WgRenderPassColorAttachment = WebGpuSharp.RenderPassColorAttachment;
using WgLoadOp = WebGpuSharp.LoadOp;
using WgStoreOp = WebGpuSharp.StoreOp;
using WgColor = WebGpuSharp.Color;
using WgExtent3D = WebGpuSharp.Extent3D;
using WgSurfaceGetCurrentTextureStatus = WebGpuSharp.SurfaceGetCurrentTextureStatus;
using WgRenderPassEncoder = WebGpuSharp.RenderPassEncoder;
using WgCommandEncoder = WebGpuSharp.CommandEncoder;

namespace Paradise.Rendering.WebGPU;

/// <summary>WebGPU (Dawn) backend entry point. Constructed with a <see cref="SurfaceDescriptor"/>
/// for windowed rendering, or via <see cref="CreateHeadless"/> for the offscreen adapter path
/// used by CI smoke tests. Exposes resource Create/Destroy plus the
/// <see cref="Submit(in RenderCommandStream)"/> path that drives a real frame.</summary>
public sealed class WebGpuRenderer : IDisposable
{
    private const int DefaultFramesInFlight = 2;

    private readonly WebGpuDevice _device;
    private readonly SurfaceState? _surface;
    private readonly bool _isHeadless;
    private readonly DeferredDestructionQueue _destructionQueue = new(DefaultFramesInFlight);
    private readonly PipelineCache _pipelineCache = new();
    private WgTexture? _offscreenTarget;
    private uint _offscreenWidth;
    private uint _offscreenHeight;
    private bool _disposed;

    /// <summary>Create a surface-backed renderer. The descriptor's platform tag selects the native
    /// surface variant; <see cref="SurfacePlatform.Headless"/> is rejected — use
    /// <see cref="CreateHeadless"/> instead.</summary>
    public WebGpuRenderer(in SurfaceDescriptor surface)
    {
        if (surface.Platform == SurfacePlatform.Headless)
            throw new ArgumentException(
                "Headless platform must use WebGpuRenderer.CreateHeadless(); the surface ctor requires a real native window.",
                nameof(surface));

        var instance = WgWebGPU.CreateInstance()
            ?? throw new InvalidOperationException("WebGPU.CreateInstance returned null — Dawn natives may be missing.");
        var nativeSurface = SurfaceFactory.Create(instance, surface);
        _device = WebGpuDevice.Create(instance, nativeSurface);
        _surface = new SurfaceState(_device, nativeSurface, surface.Width, surface.Height);
        _isHeadless = false;
    }

    private WebGpuRenderer(uint width, uint height)
    {
        var instance = WgWebGPU.CreateInstance()
            ?? throw new InvalidOperationException("WebGPU.CreateInstance returned null — Dawn natives may be missing.");
        _device = WebGpuDevice.Create(instance, compatibleSurface: null);
        _isHeadless = true;
        _offscreenWidth = width == 0 ? 1 : width;
        _offscreenHeight = height == 0 ? 1 : height;
        _offscreenTarget = CreateOffscreenTarget(_offscreenWidth, _offscreenHeight);
    }

    /// <summary>Construct the renderer using the headless adapter path. No native surface is
    /// created; clear frames render into an offscreen <c>BGRA8Unorm</c> texture sized
    /// <paramref name="width"/> x <paramref name="height"/>. The CI smoke test driver consumes
    /// this path with <c>SDL_VIDEODRIVER=dummy</c>.</summary>
    public static WebGpuRenderer CreateHeadless(uint width = 1, uint height = 1) => new(width, height);

    /// <summary>The native swapchain format for windowed renderers, or <see cref="TextureFormat.Bgra8Unorm"/>
    /// for the headless offscreen target. Pipeline color targets must match this format or the
    /// backend will reject the pipeline at draw time.</summary>
    public TextureFormat ColorFormat =>
        _isHeadless
            ? TextureFormat.Bgra8Unorm
            : FormatConversions.FromWgpu(_surface!.Format);

    /// <summary>Resize the surface (or offscreen target) to <paramref name="width"/> x
    /// <paramref name="height"/>. Zero-sized requests are clamped to 1.</summary>
    public void Resize(uint width, uint height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isHeadless)
        {
            if (width == 0) width = 1;
            if (height == 0) height = 1;
            if (width == _offscreenWidth && height == _offscreenHeight) return;
            _offscreenTarget?.Destroy();
            _offscreenWidth = width;
            _offscreenHeight = height;
            _offscreenTarget = CreateOffscreenTarget(width, height);
        }
        else
        {
            _surface!.Resize(width, height);
        }
    }

    /// <summary>Acquire the next color attachment, run a single render pass that clears it to
    /// <paramref name="clearColor"/>, submit, and present (windowed) or simply discard (headless).</summary>
    public void RenderClearFrame(in ColorRgba clearColor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!TryAcquireBackbufferView(out var view)) return;

        var encoder = _device.Device.CreateCommandEncoder();
        var colors = new WgRenderPassColorAttachment[1];
        colors[0] = new WgRenderPassColorAttachment
        {
            View = view,
            LoadOp = WgLoadOp.Clear,
            StoreOp = WgStoreOp.Store,
            ClearValue = new WgColor(clearColor.R, clearColor.G, clearColor.B, clearColor.A),
            DepthSlice = null,
        };
        var passDesc = new WgRenderPassDescriptor
        {
            ColorAttachments = colors,
            Label = "ParadiseClearPass",
        };
        var pass = encoder.BeginRenderPass(in passDesc);
        pass.End();
        var commandBuffer = encoder.Finish();
        _device.Queue.Submit(commandBuffer);
        if (!_isHeadless) _surface!.Native.Present();
        _destructionQueue.AdvanceFrame();
    }

    // -------- Resource creation / destruction --------

    /// <summary>Raw-WGSL shader creation path. <see cref="ShaderDesc.Source"/> must be the WGSL
    /// source bytes (UTF-8). Used by tests and consumers not going through Slang.</summary>
    public ShaderHandle CreateShader(in ShaderDesc desc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var wgsl = System.Text.Encoding.UTF8.GetString(desc.Source);
        return _device.CreateShader(wgsl, desc.Name ?? string.Empty);
    }

    /// <summary>Slang-output shader creation path. Carries the entry-point name forward so the
    /// pipeline build can reference the right exported function on the WebGPU shader module.</summary>
    public ShaderHandle CreateShader(in ShaderModuleDesc moduleDesc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _device.CreateShaderModule(moduleDesc);
    }

    public void DestroyShader(ShaderHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _destructionQueue.Schedule(() => _device.DestroyShader(handle));
    }

    public BufferHandle CreateBuffer(in BufferDesc desc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _device.CreateBuffer(in desc);
    }

    /// <summary>Create a buffer and immediately upload <paramref name="data"/> to it. The buffer
    /// is created with <see cref="BufferUsage.CopyDst"/> implicitly added so the upload can
    /// succeed.</summary>
    public BufferHandle CreateBufferWithData<T>(in BufferDesc desc, ReadOnlySpan<T> data) where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var byteSize = (ulong)(data.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<T>());
        var sized = new BufferDesc(desc.Name, byteSize > desc.Size ? byteSize : desc.Size, desc.Usage | BufferUsage.CopyDst, desc.MappedAtCreation);
        var handle = _device.CreateBuffer(in sized);
        var native = _device.ResolveBuffer(handle);
        _device.Queue.WriteBuffer(native, 0, data);
        return handle;
    }

    public void DestroyBuffer(BufferHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _destructionQueue.Schedule(() => _device.DestroyBuffer(handle));
    }

    public PipelineHandle CreatePipeline(in PipelineDesc desc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _pipelineCache.GetOrCreate(in desc, d => _device.CreatePipeline(in d));
    }

    /// <summary>Build a <see cref="PipelineDesc"/> from a Slang-reflected program plus a target
    /// color format, then route through <see cref="CreatePipeline(in PipelineDesc)"/> (and its
    /// pipeline cache). Vertex layout is taken verbatim from the program's reflection record —
    /// the M1 design contract's "no hand-coded layout" rule lives in this method's body.</summary>
    public PipelineHandle CreatePipeline(in ShaderProgramDesc program, TextureFormat colorFormat)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ShaderModuleDesc? vsModule = null;
        ShaderModuleDesc? fsModule = null;
        foreach (var m in program.Modules)
        {
            if ((m.Stage & ShaderStage.Vertex) != 0) vsModule = m;
            if ((m.Stage & ShaderStage.Fragment) != 0) fsModule = m;
        }
        if (vsModule is null) throw new InvalidOperationException("ShaderProgramDesc has no vertex module.");
        if (fsModule is null) throw new InvalidOperationException("ShaderProgramDesc has no fragment module.");

        var vsHandle = _device.CreateShaderModule(vsModule);
        var fsHandle = ReferenceEquals(vsModule, fsModule)
            ? vsHandle
            : _device.CreateShaderModule(fsModule);

        var pipelineDesc = new PipelineDesc
        {
            Name = "ShaderProgramPipeline",
            VertexShader = vsHandle,
            VertexEntryPoint = vsModule.EntryPoint,
            FragmentShader = fsHandle,
            FragmentEntryPoint = fsModule.EntryPoint,
            VertexLayouts = program.VertexBuffers,
            Topology = PrimitiveTopology.TriangleList,
            StripIndexFormat = IndexFormat.Uint16,
            ColorFormat = colorFormat,
            DepthStencilFormat = null,
            Layout = program.Layout,
        };
        return CreatePipeline(in pipelineDesc);
    }

    public void DestroyPipeline(PipelineHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pipelineCache.Forget(handle);
        _destructionQueue.Schedule(() => _device.DestroyPipeline(handle));
    }

    // -------- Command stream submission --------

    /// <summary>Submit a recorded <see cref="RenderCommandStream"/>. Acquires the backbuffer view,
    /// walks every <see cref="RenderCommand"/>, dispatches to WebGPU, presents (when windowed),
    /// and advances the frame counter so deferred destructions can drain.</summary>
    public void Submit(in RenderCommandStream stream)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!TryAcquireBackbufferView(out var view)) return;

        var encoder = _device.Device.CreateCommandEncoder();
        ExecuteStream(in stream, encoder, view);
        var commandBuffer = encoder.Finish();
        _device.Queue.Submit(commandBuffer);
        if (!_isHeadless) _surface!.Native.Present();
        _destructionQueue.AdvanceFrame();
    }

    private void ExecuteStream(in RenderCommandStream stream, WgCommandEncoder encoder, WgTextureView backbuffer)
    {
        var passes = stream.Passes.Span;
        var commands = stream.Commands.Span;

        WgRenderPassEncoder? activePass = null;
        try
        {
        for (var i = 0; i < commands.Length; i++)
        {
            ref readonly var cmd = ref commands[i];
            switch (cmd.Kind)
            {
                case RenderCommandKind.BeginPass:
                {
                    if (activePass is not null)
                        throw new InvalidOperationException(
                            "Nested BeginPass — previous pass was not ended (missing EndPass).");
                    var passIndex = cmd.BeginPass.PassIndex;
                    if ((uint)passIndex >= (uint)passes.Length)
                        throw new InvalidOperationException(
                            $"BeginPass references pass index {passIndex} but only {passes.Length} pass(es) declared.");
                    activePass = BeginPass(encoder, passes[passIndex], backbuffer);
                    break;
                }
                case RenderCommandKind.EndPass:
                    activePass?.End();
                    activePass = null;
                    break;
                case RenderCommandKind.SetPipeline:
                {
                    var pass = RequireActivePass(activePass);
                    pass.SetPipeline(_device.ResolvePipeline(cmd.SetPipeline.Pipeline));
                    break;
                }
                case RenderCommandKind.SetVertexBuffer:
                {
                    var pass = RequireActivePass(activePass);
                    var p = cmd.SetVertexBuffer;
                    pass.SetVertexBuffer(p.Slot, _device.ResolveBuffer(p.Buffer), p.Offset, p.Size);
                    break;
                }
                case RenderCommandKind.SetIndexBuffer:
                {
                    var pass = RequireActivePass(activePass);
                    var p = cmd.SetIndexBuffer;
                    pass.SetIndexBuffer(_device.ResolveBuffer(p.Buffer), FormatConversions.ToWgpu(p.Format), p.Offset, p.Size);
                    break;
                }
                case RenderCommandKind.SetBindGroup:
                    // M1 reserves the command but the backend has nothing to bind. M2 will fill in
                    // the resource payload + dispatch wgpuRenderPassEncoderSetBindGroup.
                    break;
                case RenderCommandKind.Draw:
                {
                    var pass = RequireActivePass(activePass);
                    var d = cmd.Draw;
                    pass.Draw(d.VertexCount, d.InstanceCount, d.FirstVertex, d.FirstInstance);
                    break;
                }
                case RenderCommandKind.DrawIndexed:
                {
                    var pass = RequireActivePass(activePass);
                    var d = cmd.DrawIndexed;
                    pass.DrawIndexed(d.IndexCount, d.InstanceCount, d.FirstIndex, d.BaseVertex, d.FirstInstance);
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unknown RenderCommandKind '{cmd.Kind}'.");
            }
        }

        if (activePass is not null)
            throw new InvalidOperationException("RenderCommandStream ended with an open render pass — missing EndPass.");
        activePass = null;
        }
        finally
        {
            // Defensive close on exception so a stale-handle / NotSupportedException mid-pass
            // doesn't leave the native render-pass encoder un-Ended (Dawn requires every
            // BeginRenderPass to be matched with End before the encoder is finished). On the
            // happy path activePass was nulled before the try-block exited.
            activePass?.End();
        }
    }

    private static WgRenderPassEncoder RequireActivePass(WgRenderPassEncoder? pass) =>
        pass ?? throw new InvalidOperationException("Render command issued outside of an active BeginPass/EndPass scope.");

    private static WgRenderPassEncoder BeginPass(WgCommandEncoder encoder, RenderPassDesc pass, WgTextureView backbuffer)
    {
        // M1 supports a single color attachment that always targets the backbuffer view (the
        // RenderPassDesc.ColorAttachments[i].View slot is reserved for M2 when offscreen targets
        // and multi-attachment passes land — for now the View handle is ignored and the backbuffer
        // is bound, with an explicit unsupported throw for >1 attachments).
        var colorCount = pass.ColorAttachmentCount;
        if (colorCount != 1)
            throw new NotSupportedException(
                $"M1 supports exactly one color attachment per pass (got {colorCount}). " +
                "Multi-attachment + non-backbuffer rendering lands in M2.");

        var src = pass.Colors.Slot0;
        var colors = new WgRenderPassColorAttachment[1];
        // Explicit switch over LoadOp/StoreOp instead of binary comparison so a future enum
        // addition (e.g. LoadOp.DontCare for an attachment whose contents the GPU may discard)
        // surfaces as a build break here rather than silently routing through Clear/Discard.
        colors[0] = new WgRenderPassColorAttachment
        {
            View = backbuffer,
            LoadOp = src.Load switch
            {
                LoadOp.Load => WgLoadOp.Load,
                LoadOp.Clear => WgLoadOp.Clear,
                _ => throw new NotSupportedException($"LoadOp '{src.Load}' has no WebGPU mapping."),
            },
            StoreOp = src.Store switch
            {
                StoreOp.Store => WgStoreOp.Store,
                StoreOp.Discard => WgStoreOp.Discard,
                _ => throw new NotSupportedException($"StoreOp '{src.Store}' has no WebGPU mapping."),
            },
            ClearValue = new WgColor(src.ClearValue.R, src.ClearValue.G, src.ClearValue.B, src.ClearValue.A),
            DepthSlice = null,
        };
        var desc = new WgRenderPassDescriptor
        {
            ColorAttachments = colors,
            Label = "ParadiseRenderPass",
        };
        return encoder.BeginRenderPass(in desc);
    }

    private bool TryAcquireBackbufferView(out WgTextureView view)
    {
        if (_isHeadless)
        {
            view = _offscreenTarget!.CreateView();
            return true;
        }

        var current = _surface!.Native.GetCurrentTexture();
        switch (current.Status)
        {
            case WgSurfaceGetCurrentTextureStatus.SuccessOptimal:
            case WgSurfaceGetCurrentTextureStatus.SuccessSuboptimal:
                break;
            case WgSurfaceGetCurrentTextureStatus.Outdated:
            case WgSurfaceGetCurrentTextureStatus.Lost:
                _surface.Reconfigure();
                view = null!;
                return false;
            default:
                throw new InvalidOperationException($"Surface texture acquisition failed: {current.Status}");
        }
        var surfaceTexture = current.Texture
            ?? throw new InvalidOperationException(
                $"Surface texture was null despite status {current.Status} — WebGPUSharp invariant violation.");
        view = surfaceTexture.CreateView();
        return true;
    }

    private WgTexture CreateOffscreenTarget(uint width, uint height)
    {
        var desc = new WgTextureDescriptor
        {
            Label = "ParadiseHeadlessTarget",
            Size = new WgExtent3D(width, height, 1),
            MipLevelCount = 1,
            SampleCount = 1,
            Dimension = WgTextureDimension.D2,
            Format = WgTextureFormat.BGRA8Unorm,
            Usage = WgTextureUsage.RenderAttachment | WgTextureUsage.CopySrc,
        };
        return _device.Device.CreateTexture(in desc);
    }

    /// <summary>Internal helper for <see cref="Paradise.Rendering.Sample"/>: load a Slang-compiled
    /// shader pair from an embedded resource pair (<paramref name="logicalNamePrefix"/>.wgsl +
    /// .reflection.json) and return a <see cref="ShaderProgramDesc"/>. Wraps
    /// <see cref="ShaderProgramLoader.Load"/> so the loader stays internal while the sample
    /// (and tests) can drive it without InternalsVisibleTo to every consumer.</summary>
    public static ShaderProgramDesc LoadShaderProgram(Assembly assembly, string logicalNamePrefix) =>
        ShaderProgramLoader.Load(assembly, logicalNamePrefix);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _destructionQueue.DrainAll();
        _pipelineCache.Clear();
        _offscreenTarget?.Destroy();
        _surface?.Dispose();
        _device.Dispose();
    }
}
