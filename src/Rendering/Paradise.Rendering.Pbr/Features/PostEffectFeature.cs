using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

[StructLayout(LayoutKind.Sequential)]
internal struct PostUniformsGpu
{
    public Vector4 Screen;
    public Vector4 A;
    public Vector4 B;
    public Vector4 C;
    public Vector4 D;
    public Vector4 E;
    public Matrix4x4 InverseProjection;
}

/// <summary>Shared resource ownership and explicit color-chain advancement for one post effect.</summary>
public abstract class PostEffectFeature : IRenderFeature
{
    private readonly PipelineHandle _pipeline;
    private readonly BindGroupLayoutDesc _layout;
    private readonly BufferHandle _uniforms;
    private readonly string _name;
    private readonly string _target;
    private readonly bool _display;
    private readonly int _event;
    private PbrColorLut? _lut;
    private TextureHandle _lutTexture;
    private TextureViewHandle _lutView;

    private protected PostEffectFeature(PbrContext ctx, string name, string fragment, bool display, int passEvent)
    {
        Context = ctx;
        _name = name;
        _target = "PbrPost" + name;
        _display = display;
        _event = passEvent;
        var program = ShaderPrograms.Load("Shaders.postEffects");
        _layout = ShaderPrograms.FindGroup(program, 0);
        _pipeline = ctx.Renderer.CreatePipeline(program, PbrTargets.HdrFormat, fragmentEntryPoint: fragment);
        _uniforms = ctx.Renderer.CreateBuffer(new BufferDesc(_target + "Uniforms",
            (ulong)Unsafe.SizeOf<PostUniformsGpu>(), BufferUsage.Uniform | BufferUsage.CopyDst));
    }

    private protected PbrContext Context { get; }
    private protected abstract bool SceneEnabled { get; }
    private protected virtual FrameRequirements Inputs => FrameRequirements.None;
    private protected virtual PbrColorLut? ColorLut => null;
    private protected abstract PostUniformsGpu Parameters();
    public abstract FeatureDefinition Definition { get; }
    public FrameRequirements Requires => SceneEnabled
        ? Inputs | (_display ? FrameRequirements.DisplayColor : FrameRequirements.None)
        : FrameRequirements.None;

    public void Resize(uint width, uint height)
    {
        if (Context.Targets.Contains(_target)) Context.Targets.Ensure(_target, Context.FrameTarget(PbrTargets.HdrFormat));
    }

    public void OnEnabledChanged(bool enabled)
    {
        if (!enabled) Release();
    }

    public void Setup(in FrameContext frame)
    {
        if (!SceneEnabled)
        {
            Release();
            return;
        }
        var depth = frame.Blackboard.GetOrDefault(PbrResults.PrepassDepth, frame.Black);
        var motion = frame.Blackboard.GetOrDefault(PbrResults.MotionVectors, frame.Black);
        if (((Inputs & FrameRequirements.DepthNormalPrepass) != 0 && depth == frame.Black)
            || ((Inputs & FrameRequirements.MotionVectors) != 0 && motion == frame.Black)) return;
        var result = _display ? PbrResults.DisplayColor : PbrResults.SceneColor;
        if (!frame.Blackboard.TryGet(result, out var source)) return;
        EnsureLut(ColorLut);
        Context.Targets.Ensure(_target, Context.FrameTarget(PbrTargets.HdrFormat));
        var target = frame.Graph.Texture(_target);
        var uniforms = Parameters();
        uniforms.Screen = new Vector4(Context.Width, Context.Height, 1f / Context.Width, 1f / Context.Height);
        uniforms.InverseProjection = Matrix4x4.Invert(Context.Projection, out var inverse)
            ? inverse : Matrix4x4.Identity;
        Context.Renderer.UpdateBuffer<PostUniformsGpu>(_uniforms, 0, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));
        frame.Graph.AddRasterPass("Post." + _name, (RenderPassEvent)_event)
            .Color(0, target, LoadOp.Clear)
            .BindGroup(0, _target, _layout,
            [
                GraphBinding.Texture(0, source),
                GraphBinding.Sampler(1, Context.LinearClampSampler),
                GraphBinding.Buffer(2, _uniforms, 0, (ulong)Unsafe.SizeOf<PostUniformsGpu>()),
                GraphBinding.Texture(3, depth),
                GraphBinding.Texture(4, motion),
                _lutView.IsValid ? GraphBinding.View(5, _lutView) : GraphBinding.Texture(5, frame.Black),
            ])
            .Record(this, Record);
        frame.Blackboard.Advance(result, source, target);
    }

    private void EnsureLut(PbrColorLut? lut)
    {
        if (ReferenceEquals(lut, _lut)) return;
        ReleaseLut();
        if (lut is null) return;
        var width = (uint)(lut.Size * lut.Size);
        var height = (uint)lut.Size;
        var pixels = new Half[lut.Colors.Length * 4];
        for (var i = 0; i < lut.Colors.Length; i++)
        {
            pixels[i * 4] = (Half)lut.Colors[i].X;
            pixels[i * 4 + 1] = (Half)lut.Colors[i].Y;
            pixels[i * 4 + 2] = (Half)lut.Colors[i].Z;
            pixels[i * 4 + 3] = (Half)1f;
        }
        _lutTexture = Context.Renderer.CreateTexture(new TextureDesc("PbrColorLut", width, height, 1, 1, 1,
            TextureDimension.D2, TextureFormat.Rgba16Float, TextureUsage.TextureBinding | TextureUsage.CopyDst));
        Context.Renderer.WriteTexture(_lutTexture, 0, MemoryMarshal.AsBytes(pixels.AsSpan()), width * 8, height, width, height);
        _lutView = Context.Renderer.CreateTextureView(new TextureViewDesc("PbrColorLut", _lutTexture, TextureViewDimension.D2, 0, 1));
        _lut = lut;
    }

    private void ReleaseLut()
    {
        if (_lutView.IsValid) Context.Renderer.DestroyTextureView(_lutView);
        if (_lutTexture.IsValid) Context.Renderer.DestroyTexture(_lutTexture);
        _lutView = default;
        _lutTexture = default;
        _lut = null;
    }

    private void Release()
    {
        Context.Targets.Release(_target);
        ReleaseLut();
    }

    private static void Record(PostEffectFeature self, ref PassRecording pass, int _) =>
        Fullscreen.Record(ref pass, self._pipeline);

    public void Dispose()
    {
        Release();
        Context.Renderer.DestroyPipeline(_pipeline);
        Context.Renderer.DestroyBuffer(_uniforms);
    }
}
