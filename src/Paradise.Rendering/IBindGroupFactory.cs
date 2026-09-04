namespace Paradise.Rendering;

/// <summary>The bind-group half of <see cref="IRenderer"/>, split out for the same reason as
/// <see cref="ITextureFactory"/>: the frame graph's bind group cache should be provable without
/// a GPU.</summary>
public interface IBindGroupFactory
{
    /// <summary>Create a bind group binding concrete resources to one of a program's bind-group
    /// layouts.</summary>
    BindGroupHandle CreateBindGroup(in BindGroupDesc desc);

    /// <summary>Destroy a bind group.</summary>
    void DestroyBindGroup(BindGroupHandle handle);
}
