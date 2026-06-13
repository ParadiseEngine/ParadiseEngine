using System.Numerics;
using Paradise.ECS;

namespace Paradise.Rendering.ECS;

/// <summary>World-space transform matrix for a renderable entity.</summary>
[Component]
public partial struct LocalToWorld
{
    public Matrix4x4 Value;
}

/// <summary>Application-defined mesh identifier. Mapped to a GPU vertex buffer at submission time.</summary>
[Component]
public partial struct MeshRef
{
    public uint MeshId;
}

/// <summary>Application-defined material identifier. Mapped to a pipeline/bind-group at submission time.</summary>
[Component]
public partial struct MaterialRef
{
    public uint MaterialId;
}

/// <summary>
/// Camera parameters read by the extraction system to produce a <see cref="ExtractedCamera"/>.
/// <para>
/// <see cref="View"/> and <see cref="Projection"/> are combined into a single ViewProjection matrix
/// during extraction so the renderer never touches live ECS state during submission.
/// </para>
/// </summary>
[Component]
public partial struct CameraComponent
{
    public Matrix4x4 View;
    public Matrix4x4 Projection;

    /// <summary>
    /// Index into the application's render-view table, or <c>uint.MaxValue</c> (i.e., -1 cast to uint)
    /// to target the swapchain surface.
    /// </summary>
    public uint TargetView;
}

/// <summary>Query descriptor: entities that can be rendered (have transform + mesh + material).</summary>
[Queryable]
[With<LocalToWorld>(IsReadOnly = true)]
[With<MeshRef>(IsReadOnly = true)]
[With<MaterialRef>(IsReadOnly = true)]
public readonly ref partial struct RenderableQuery;

/// <summary>Query descriptor: entities that act as cameras.</summary>
[Queryable]
[With<CameraComponent>(IsReadOnly = true)]
public readonly ref partial struct CameraQuery;
