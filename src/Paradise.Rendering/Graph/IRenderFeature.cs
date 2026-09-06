using System;

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
}

/// <summary>One unit of the frame: a thing that owns its own GPU resources and declares its own
/// passes into the graph each frame.
///
/// <para>A feature is the extension seam. It sees the graph, the registry behind it, the frame's
/// requirements and the <see cref="FrameBlackboard"/>, and nothing of any other feature — a
/// result another feature needs is published by name. Features run in list order, which is the
/// dependency order the composer of the list is responsible for.</para></summary>
public interface IRenderFeature : IDisposable
{
    string Name { get; }

    /// <summary>A disabled feature is skipped entirely: no requirements, no setup, no passes.</summary>
    bool Enabled { get; }

    /// <summary>How this feature needs the frame shaped. Read before any feature's
    /// <see cref="Setup"/>.</summary>
    FrameRequirements Requires { get; }

    /// <summary>The frame's targets changed size; re-declare the ones this feature owns.</summary>
    void Resize(uint width, uint height);

    /// <summary>Declare this frame's passes and publish what other features may consume.</summary>
    void Setup(in FrameContext frame);
}
