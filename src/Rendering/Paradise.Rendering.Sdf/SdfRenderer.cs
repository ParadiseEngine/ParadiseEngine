using System.Buffers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Sdf;

/// <summary>The signed-distance-field scene renderer: a <see cref="RenderPipeline"/> of features
/// driven once per frame through a <see cref="FrameGraph"/>.</summary>
/// <remarks>A path of its own beside the PBR renderer, sharing only the backend contract. It
/// draws no meshes: the scene is a list of analytic shapes traced per pixel. Hosts extend the
/// frame through <see cref="Pipeline"/> and read <see cref="SdfTargets"/> by name.</remarks>
public sealed class SdfRenderer : IDisposable
{
    /// <summary>Capacity of the shape storage buffer.</summary>
    /// <remarks>Every field evaluation walks the shapes in order, so cost grows with this count
    /// times the pixels traced; unions far from a sample are skipped by their bounds.</remarks>
    public const int MaxShapes = 256;

    /// <summary>Capacity of the light array in the frame uniforms.</summary>
    public const int MaxLights = 8;

    private readonly IRenderer _renderer;
    private readonly SdfContext _ctx;
    private readonly FrameGraph _graph;
    private readonly ArrayBufferWriter<RenderCommand> _commandWriter = new(64);
    private bool _disposed;
    private bool _rendering;

    /// <param name="renderer">The backend the frame is submitted to.</param>
    /// <param name="switches">The process-wide feature configuration, read each frame;
    /// unconfigured hosts explicitly pass <c>new FeatureSwitches()</c>.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="logger">Where frame-graph diagnostics go.</param>
    public SdfRenderer(IRenderer renderer, FeatureSwitches switches, uint width, uint height, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(switches);
        _renderer = renderer;
        _ctx = new SdfContext(renderer, width, height);
        _graph = new FrameGraph(_ctx.Targets, _ctx.BindGroups, logger ?? NullLogger.Instance);
        Pipeline = new RenderPipeline(_ctx.Width, _ctx.Height, switches);
        Pipeline
            .Add(new SdfSceneFeature(_ctx), SdfFeatureOrder.Scene)
            .Add(new SdfCompositeFeature(_ctx), SdfFeatureOrder.Composite);
    }

    /// <summary>The features that make up a frame, in the order they set up.</summary>
    /// <remarks>A host adds its own through <see cref="RenderPipeline.Add"/> at an
    /// <see cref="SdfFeatureOrder"/> slot; they see the targets named in <see cref="SdfTargets"/>
    /// and the results named in <see cref="SdfResults"/>.</remarks>
    public RenderPipeline Pipeline { get; }

    /// <summary>The engine's feature configuration, as this renderer reads it every frame.</summary>
    public FeatureSwitches Switches => Pipeline.Switches;

    /// <summary>The passes the last frame submitted, in the order a backend times them.</summary>
    public IReadOnlyList<string> LastPassNames => _graph.LivePassNames;

    public float AspectRatio => _ctx.Width / (float)_ctx.Height;

    public void Resize(uint width, uint height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == _ctx.Width && height == _ctx.Height) return;
        _ctx.Resize(width, height);
        Pipeline.Resize(width, height);
    }

    /// <summary>Pack the scene, declare and compile its passes, then upload and submit.</summary>
    /// <exception cref="ArgumentException">The camera cannot be inverted, a shape is not a
    /// similarity transform of its primitive, or the scene exceeds <see cref="MaxShapes"/> or
    /// <see cref="MaxLights"/>.</exception>
    public void RenderFrame(SdfScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_rendering) throw new InvalidOperationException("A render frame is already active.");
        _rendering = true;
        try
        {
            _ctx.BeginFrame(scene);
            Pipeline.BeginFrame();
            Pipeline.PrepareFrame();
            _graph.Reset();
            Pipeline.Setup(_graph);

            _commandWriter.ResetWrittenCount();
            var stream = _graph.Compile(_commandWriter);
            _ctx.BindGroups.EndFrame();

            Pipeline.BeforeSubmit();
            _renderer.Submit(in stream);
        }
        finally
        {
            _rendering = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_rendering) throw new InvalidOperationException("Dispose the renderer between frames.");
        _disposed = true;
        // Features release their pipelines and buffers before the context retires what they bound.
        try
        {
            Pipeline.Dispose();
        }
        finally
        {
            _ctx.Dispose();
        }
    }
}
