using System;
using Paradise.Features;

namespace Paradise.Rendering.Graph;

/// <summary>What a feature needs the frame shaped like before any pass is declared. Queried
/// from every enabled feature and combined, then handed to all of them through
/// <see cref="FrameContext.Requirements"/>, so a feature can reshape a pass another feature
/// owns without either knowing the other's type.</summary>
[Flags]
public enum FrameRequirements
{
    None = 0,

    /// <summary>The opaque scene must be resolved into a sampleable texture before transparent
    /// geometry draws: the scene pass splits at the opaque/blend boundary.</summary>
    SceneColorCapture = 1 << 0,

    /// <summary>The opaque scene's depth and normals must exist before the main pass: the
    /// depth + normal pre-pass runs and publishes its targets, whatever the scene's own
    /// screen-space settings say.</summary>
    DepthNormalPrepass = 1 << 1,

    /// <summary>Current-to-previous screen-space motion must be available for temporal effects.</summary>
    MotionVectors = 1 << 2,

    /// <summary>Tonemapping must produce linear display color for effects before presentation.</summary>
    DisplayColor = 1 << 3,
}

/// <summary>One unit of the frame: a thing that owns its own GPU resources and declares its own
/// passes into the graph each frame.
///
/// <para>A feature is the extension seam. It sees the graph, the registry behind it, the frame's
/// requirements and the <see cref="FrameBlackboard"/>, and nothing of any other feature — a
/// result another feature needs is published by name. Features run in the order their
/// <see cref="RenderPipeline"/> sorted them into, which is what the composer of that pipeline is
/// responsible for.</para>
///
/// <para>A feature does not decide whether it runs. Its <see cref="Definition"/> names the switch
/// that does, and the pipeline reads that switch every frame from the engine's
/// <see cref="IFeatureSwitches"/> — so a game feature is turned off from a config file exactly
/// the way a built-in one is, and neither had to write any code for it.</para></summary>
public interface IRenderFeature : IDisposable
{
    /// <summary>This feature's identity: the switch that turns it on and off, whether it is on
    /// when nothing says otherwise, and a line for a listing.</summary>
    FeatureDefinition Definition { get; }

    /// <summary>How this feature needs the frame shaped. Read from every ENABLED feature before
    /// any feature's <see cref="Setup"/>.</summary>
    FrameRequirements Requires { get; }

    /// <summary>Declare the targets this feature owns at this size. Called once when the feature
    /// is added to a <see cref="RenderPipeline"/> and again whenever the frame changes size, so
    /// a feature need not create its targets in its constructor.</summary>
    void Resize(uint width, uint height);

    /// <summary>Prepare frame-local state before requirements and pass setup are evaluated.</summary>
    void PrepareFrame()
    {
    }

    /// <summary>Declare this frame's passes and publish what other features may consume.</summary>
    void Setup(in FrameContext frame);

    /// <summary>Between compile and submit, once per frame, for every enabled feature.
    ///
    /// <para><b>For what RECORDING staged.</b> A pass's recorder runs inside the compile, so a
    /// feature that fills a uniform ring while recording — the shadow pass writes one caster's
    /// matrix per draw — has nothing uploaded when <see cref="Setup"/> returns and no other
    /// moment to do it: after this the command stream is submitted and the GPU reads the buffer.
    /// Without the hook the only thing that CAN do the upload is whatever owns the frame loop,
    /// which then has to know that this particular feature stages draws — and a game's feature,
    /// which that loop has never heard of, cannot be uploaded at all.</para>
    ///
    /// <para>Most features have nothing to do here. Setting up a pass and recording it are the
    /// whole job unless a resource the stream reads is filled during recording.</para></summary>
    void BeforeSubmit()
    {
    }

    /// <summary>Called when this feature's switch flips, and only then — never once per frame.
    ///
    /// <para><b>Override it when being off is not the same as declaring no passes.</b> A feature
    /// whose whole contribution is the passes it declares needs nothing here: stop declaring
    /// them, its results stop appearing on the blackboard, and every consumer falls back. But a
    /// feature that leaves state BEHIND — a uniform buffer another feature binds, a plan another
    /// feature reads, a texture a material samples — is still being read after its last frame,
    /// and that state must say "off" rather than repeat whatever the last enabled frame put
    /// there. Skipping this is how a disabled feature keeps darkening the picture with the
    /// occlusion it computed a hundred frames ago.</para></summary>
    void OnEnabledChanged(bool enabled)
    {
    }
}
