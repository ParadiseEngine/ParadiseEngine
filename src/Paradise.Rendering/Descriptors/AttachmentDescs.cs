namespace Paradise.Rendering;

/// <summary>Color attachment binding for a single render-pass color slot.</summary>
public readonly struct ColorAttachmentDesc
{
    public readonly RenderViewHandle View;
    public readonly LoadOp Load;
    public readonly StoreOp Store;
    public readonly ColorRgba ClearValue;

    public ColorAttachmentDesc(RenderViewHandle view, LoadOp load, StoreOp store, ColorRgba clearValue)
    {
        View = view;
        Load = load;
        Store = store;
        ClearValue = clearValue;
    }
}

/// <summary>Depth attachment binding for a render pass.</summary>
public readonly struct DepthAttachmentDesc
{
    public readonly TextureHandle DepthTexture;
    public readonly LoadOp DepthLoad;
    public readonly StoreOp DepthStore;
    public readonly float ClearDepth;

    public DepthAttachmentDesc(TextureHandle depthTexture, LoadOp depthLoad, StoreOp depthStore, float clearDepth)
    {
        DepthTexture = depthTexture;
        DepthLoad = depthLoad;
        DepthStore = depthStore;
        ClearDepth = clearDepth;
    }
}
