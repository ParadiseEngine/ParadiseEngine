using System.Numerics;

namespace Paradise.Rendering.Pbr;

public enum PbrLightType : byte
{
    Directional = 0,
    Point = 1,
    Spot = 2,
}

/// <summary>One punctual light (up to <see cref="FrameUniformsGpu.MaxSceneLights"/> per frame).
/// Color is LINEAR. For directionals, <see cref="Direction"/> points FROM the surface TOWARD
/// the light (the shader's L convention).</summary>
public sealed record PbrLight
{
    public PbrLightType Type { get; init; }
    public Vector3 Position { get; init; }
    public Vector3 Direction { get; init; } = Vector3.UnitY;
    public Vector3 Color { get; init; } = Vector3.One;
    public float Intensity { get; init; } = 1f;
    public float Range { get; init; }
    public float SpotOuterDegrees { get; init; } = 45f;
    public float SpotInnerDegrees { get; init; }

    // Shadow intent. The atlas tile assignment + light-space matrices are computed by the renderer
    // each frame (it owns the atlas), so ToGpu leaves the shadow slots "off" (base tile -1) and the
    // renderer overrides SpotAngles.zw + ShadowAtlas for lights that actually get a tile.
    public bool CastsShadows { get; init; }
    public float ShadowStrength { get; init; } = 1f;
    public bool SoftShadows { get; init; }

    public SceneLightGpu ToGpu() => new()
    {
        PositionAndType = new Vector4(Position, (float)Type),
        DirectionAndRange = new Vector4(Direction, Range),
        ColorAndIntensity = new Vector4(Color, Intensity),
        SpotAngles = new Vector4(SpotOuterDegrees, SpotInnerDegrees, -1f, 0f), // z=-1 → no shadow
        ShadowAtlas = Vector4.Zero,
    };
}

/// <summary>Hemisphere ambient (linear colors) + exposure; <see cref="Flat"/> uses the sky
/// color everywhere (the exported "flat ambient" mode).</summary>
public sealed record PbrAmbient
{
    public Vector3 Sky { get; init; } = new(0.21f, 0.23f, 0.26f);
    public Vector3 Equator { get; init; } = new(0.11f, 0.12f, 0.13f);
    public Vector3 Ground { get; init; } = new(0.05f, 0.05f, 0.05f);
    public float Exposure { get; init; } = 1f;
    public bool Flat { get; init; }
}

/// <summary>Camera state: matrices via <see cref="PbrMath"/>, plus the world position the
/// shader needs for view vectors.</summary>
public struct PbrCamera
{
    public Matrix4x4 View;
    public Matrix4x4 Projection;
    public Vector3 Position;
}

/// <summary>One uploaded draw batch: geometry handles plus the material it binds.</summary>
public sealed record PbrPrimitive(
    BufferHandle VertexBuffer,
    BufferHandle IndexBuffer,
    uint IndexCount,
    ulong VertexByteLength,
    ulong IndexByteLength,
    int MaterialId,
    // Object-space AABB (for fitting the directional shadow frustum). Default (zero) is treated as
    // "unknown" and contributes only the instance origin to the world bounds.
    Vector3 LocalMin = default,
    Vector3 LocalMax = default);

/// <summary>An uploaded mesh (one or more primitives sharing an instance transform).</summary>
public sealed record PbrMesh(PbrPrimitive[] Primitives);

/// <summary>One rendered instance of an uploaded mesh.</summary>
public sealed class PbrInstance
{
    public required PbrMesh Mesh { get; init; }
    public Matrix4x4 Model = Matrix4x4.Identity;
    public float Highlight;
}

/// <summary>Everything <see cref="PbrRenderer.RenderFrame"/> consumes for one frame. Plain CPU
/// state — mutate freely between frames.</summary>
public sealed class PbrScene
{
    public PbrCamera Camera;
    public PbrAmbient Ambient = new();
    public ColorRgba ClearColor = ColorRgba.CornflowerBlue;
    public List<PbrLight> Lights { get; } = [];
    public List<PbrInstance> Instances { get; } = [];
}
