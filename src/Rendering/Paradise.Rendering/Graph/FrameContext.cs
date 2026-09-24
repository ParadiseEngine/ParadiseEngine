namespace Paradise.Rendering.Graph;

/// <summary>Everything a feature is given for one frame's <see cref="IRenderFeature.Setup"/>.
/// A struct handed by reference: it carries no state of its own beyond the frame's size and
/// requirements, and the objects it points at outlive the frame.</summary>
public readonly struct FrameContext
{
    public FrameContext(FrameGraph graph, uint width, uint height, FrameRequirements requirements, FrameBlackboard blackboard)
    {
        Graph = graph;
        Width = width;
        Height = height;
        Requirements = requirements;
        Blackboard = blackboard;
    }

    public FrameGraph Graph { get; }
    public uint Width { get; }
    public uint Height { get; }

    /// <summary>The union of every enabled feature's <see cref="IRenderFeature.Requires"/>.</summary>
    public FrameRequirements Requirements { get; }

    public FrameBlackboard Blackboard { get; }

    /// <summary>The registry behind the graph. Every target a feature declares lives here.</summary>
    public GraphTextureRegistry Textures =>
        Graph.Textures ?? throw new System.InvalidOperationException("The graph owns no textures.");

    /// <summary>The 1×1 black fallback, for a binding this frame does not use.</summary>
    public GraphTexture Black => Graph.Texture(Textures.Black);
}
