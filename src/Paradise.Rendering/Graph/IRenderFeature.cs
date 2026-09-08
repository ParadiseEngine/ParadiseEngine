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

    /// <summary>Tonemapping must publish display-linear color for effects before presentation.</summary>
    DisplayColor = 1 << 3,
}

/// <summary>Owns one render feature's resources and declares its passes.</summary>
/// <remarks>Pipeline order controls setup dependencies; outputs travel through the frame
/// blackboard. Definition connects each built-in or game feature to the shared configuration
/// switches.</remarks>
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

    /// <summary>Prepare frame-local camera or scene state before partitioning and GPU uploads.</summary>
    /// <remarks>Runs after the pipeline snapshots switches and before requirements and setup;
    /// changes must stay in frame-local state rather than modifying authored scene data.</remarks>
    void PrepareFrame()
    {
    }

    /// <summary>Declare this frame's passes and publish what other features may consume.</summary>
    void Setup(in FrameContext frame);

    /// <summary>Uploads data staged during recording, after compilation and before
    /// submission.</summary>
    /// <remarks>Called once for every enabled feature. Uniform rings filled by pass recorders are
    /// not ready during Setup and must be uploaded here.</remarks>
    void BeforeSubmit()
    {
    }

    /// <summary>Updates persistent feature state when its enabled switch changes.</summary>
    /// <remarks>Override to retract plans, uniforms or textures that other features still read
    /// while this feature is disabled.</remarks>
    void OnEnabledChanged(bool enabled)
    {
    }
}
