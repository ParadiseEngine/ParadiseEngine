using System.Numerics;
using Paradise.Assets.Gltf;

namespace Paradise.Rendering.Pbr;

/// <summary>
/// Row-major flipbook spritesheet layout (left-to-right, then top-to-bottom; glTF texcoords —
/// v grows downward, so row 0 is the TOP row of the sheet). Normalizes at construction:
/// frame count 0 means the full grid, and everything clamps to sane bounds.
/// </summary>
public readonly struct FlipbookLayout
{
    public FlipbookLayout(int columns, int rows, int frameCount = 0)
    {
        Columns = Math.Max(1, columns);
        Rows = Math.Max(1, rows);
        FrameCount = Math.Clamp(frameCount <= 0 ? Columns * Rows : frameCount, 1, Columns * Rows);
    }

    public int Columns { get; }
    public int Rows { get; }
    public int FrameCount { get; }

    /// <summary>A single frame (1×1 grid) — the layout of an un-animated sprite.</summary>
    public static FlipbookLayout SingleFrame => new(1, 1);

    /// <summary>UV rectangle of a frame; out-of-range frames wrap (looping flipbooks).</summary>
    public (Vector2 Min, Vector2 Max) UvRect(int frame)
    {
        var wrapped = ((frame % FrameCount) + FrameCount) % FrameCount;
        var column = wrapped % Columns;
        var row = wrapped / Columns;
        return (
            new Vector2(column / (float)Columns, row / (float)Rows),
            new Vector2((column + 1) / (float)Columns, (row + 1) / (float)Rows));
    }
}

/// <summary>One sprite in a <see cref="PbrSpriteBatch"/>: a camera-facing square quad.</summary>
public readonly record struct SpriteInstance(Vector3 Center, float HalfSize, int Frame);

/// <summary>One voxel in a <see cref="PbrVoxelBatch"/>: an axis-aligned cube.</summary>
public readonly record struct VoxelInstance(Vector3 Center, float HalfExtent);

/// <summary>
/// World-space sprite/voxel geometry in the renderer's interleaved vertex layout (12 floats:
/// pos3/normal3/uv2/tangent4 — the <see cref="Procedural"/> layout), written into caller-owned
/// arrays so dynamic batches can rewrite their vertex streams every frame. A zeroed region is a
/// degenerate (invisible) primitive — batches blank dead slots that way.
/// </summary>
public static class SpriteGeometry
{
    public const int FloatsPerVertex = 12;
    public const int QuadFloats = 4 * FloatsPerVertex;
    public const int CubeFloats = 24 * FloatsPerVertex;

    /// <summary>Quad corners in TL, BL, BR, TR order (CCW toward <c>right × up</c> — the
    /// normal faces the viewer when handed camera billboard axes).</summary>
    public static void WriteQuad(
        float[] vertices, int offset, in Vector3 center, in Vector3 halfRight, in Vector3 halfUp,
        in FlipbookLayout layout, int frame)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        var normal = Vector3.Cross(halfRight, halfUp);
        var normalLength = normal.Length();
        normal = normalLength > 1e-12f ? normal / normalLength : Vector3.UnitZ;
        var tangent = halfRight.LengthSquared() > 1e-12f ? Vector3.Normalize(halfRight) : Vector3.UnitX;

        var (uvMin, uvMax) = layout.UvRect(frame);
        WriteVertex(vertices, offset, center - halfRight + halfUp, normal, uvMin.X, uvMin.Y, tangent);
        WriteVertex(vertices, offset + FloatsPerVertex, center - halfRight - halfUp, normal, uvMin.X, uvMax.Y, tangent);
        WriteVertex(vertices, offset + 2 * FloatsPerVertex, center + halfRight - halfUp, normal, uvMax.X, uvMax.Y, tangent);
        WriteVertex(vertices, offset + 3 * FloatsPerVertex, center + halfRight + halfUp, normal, uvMax.X, uvMin.Y, tangent);
    }

    /// <summary>Axis-aligned cube, 4 verts × 6 faces with per-face flat normals (the voxel look).</summary>
    public static void WriteCube(float[] vertices, int offset, in Vector3 center, float halfExtent)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        Span<Vector3> axes =
        [
            Vector3.UnitX, -Vector3.UnitX,
            Vector3.UnitY, -Vector3.UnitY,
            Vector3.UnitZ, -Vector3.UnitZ,
        ];
        for (var face = 0; face < 6; face++)
        {
            var normal = axes[face];
            // Any consistent in-plane frame works (flat per-face normals, untextured faces).
            var up = MathF.Abs(normal.Y) > 0.5f ? Vector3.UnitZ : Vector3.UnitY;
            var right = Vector3.Cross(up, normal);
            var faceCenter = center + normal * halfExtent;
            var halfRight = right * halfExtent;
            var halfUp = Vector3.Cross(normal, right) * halfExtent;
            var faceOffset = offset + face * 4 * FloatsPerVertex;
            WriteVertex(vertices, faceOffset, faceCenter - halfRight + halfUp, normal, 0f, 0f, right);
            WriteVertex(vertices, faceOffset + FloatsPerVertex, faceCenter - halfRight - halfUp, normal, 0f, 1f, right);
            WriteVertex(vertices, faceOffset + 2 * FloatsPerVertex, faceCenter + halfRight - halfUp, normal, 1f, 1f, right);
            WriteVertex(vertices, faceOffset + 3 * FloatsPerVertex, faceCenter + halfRight + halfUp, normal, 1f, 0f, right);
        }
    }

    public static uint[] QuadIndices(int quadCount)
    {
        var indices = new uint[Math.Max(0, quadCount) * 6];
        for (var quad = 0; quad < quadCount; quad++)
        {
            var v = (uint)(quad * 4);
            var i = quad * 6;
            indices[i] = v; indices[i + 1] = v + 1; indices[i + 2] = v + 2;
            indices[i + 3] = v; indices[i + 4] = v + 2; indices[i + 5] = v + 3;
        }
        return indices;
    }

    public static uint[] CubeIndices(int cubeCount) => QuadIndices(cubeCount * 6);

    private static void WriteVertex(
        float[] vertices, int offset, in Vector3 position, in Vector3 normal, float u, float v, in Vector3 tangent)
    {
        vertices[offset] = position.X; vertices[offset + 1] = position.Y; vertices[offset + 2] = position.Z;
        vertices[offset + 3] = normal.X; vertices[offset + 4] = normal.Y; vertices[offset + 5] = normal.Z;
        vertices[offset + 6] = u; vertices[offset + 7] = v;
        vertices[offset + 8] = tangent.X; vertices[offset + 9] = tangent.Y; vertices[offset + 10] = tangent.Z;
        vertices[offset + 11] = 1f;
    }
}

/// <summary>Materials for sprite rendering.</summary>
public static class PbrSpriteMaterials
{
    /// <summary>
    /// Spritesheet material: a STANDALONE KTX2 texture (not GLB-embedded) × tint, alpha
    /// BLENDED — the PBR shader has no cutout/mask path, and blend keeps sprites out of the
    /// shadow-caster set. No cull mode is set anywhere, so sprite quads render double-sided.
    /// <paramref name="sheetKtx2"/> null renders the tint alone (untextured).
    /// </summary>
    public static int AddSheetMaterial(MaterialResourceCache materials, byte[]? sheetKtx2, Vector4 tint)
    {
        ArgumentNullException.ThrowIfNull(materials);
        var material = new GltfMaterialData(
            Name: "sprite-sheet",
            BaseColorFactor: tint,
            MetallicFactor: 0f,
            RoughnessFactor: 1f,
            EmissiveFactor: Vector3.Zero,
            NormalScale: 1f,
            OcclusionStrength: 1f,
            TransmissionFactor: 0f,
            AlphaMode: GltfAlphaMode.Blend,
            AlphaCutoff: 0.5f,
            DoubleSided: true,
            BaseColorImage: sheetKtx2 is null ? -1 : 0,
            MetallicRoughnessImage: -1,
            NormalImage: -1,
            OcclusionImage: -1,
            EmissiveImage: -1,
            BaseColorUvTransform: GltfUvTransform.Identity);
        return materials.AddMaterial(in material, sheetKtx2 is null ? [] : [new GltfImageData(sheetKtx2)]);
    }
}

/// <summary>
/// One flipbook-animated world-space quad: a dynamic primitive whose UVs address the selected
/// sheet frame and whose axes are re-written each frame (hand it camera axes to billboard, or
/// an orientation's basis vectors for a fixed quad). The caller owns the clock — this class
/// only renders a frame index.
/// </summary>
public sealed class PbrSpriteQuad
{
    private readonly PbrPrimitive _primitive;
    private readonly float[] _vertices = new float[SpriteGeometry.QuadFloats];
    private readonly FlipbookLayout _layout;

    public PbrSpriteQuad(PbrRenderer pbr, in FlipbookLayout layout, byte[]? sheetKtx2, Vector4 tint)
    {
        ArgumentNullException.ThrowIfNull(pbr);
        _layout = layout;
        var material = PbrSpriteMaterials.AddSheetMaterial(pbr.Materials, sheetKtx2, tint);
        _primitive = pbr.UploadPrimitive(_vertices, SpriteGeometry.QuadIndices(1), material, dynamic: true);
        Instance = new PbrInstance { Mesh = new PbrMesh([_primitive]), Model = Matrix4x4.Identity };
    }

    /// <summary>Add this to <see cref="PbrScene.Instances"/> once; Update re-writes it in place.</summary>
    public PbrInstance Instance { get; }

    public void Update(
        PbrRenderer pbr, in Vector3 center, in Vector3 right, in Vector3 up, in Vector2 size, int frame)
    {
        ArgumentNullException.ThrowIfNull(pbr);
        SpriteGeometry.WriteQuad(
            _vertices, 0, center, right * (size.X * 0.5f), up * (size.Y * 0.5f), _layout, frame);
        pbr.UpdatePrimitiveVertices(_primitive, _vertices);
    }
}

/// <summary>
/// A dynamic batch of camera-facing flipbook quads (sprite particles): one primitive, one
/// draw, re-written from caller data every frame. Slots beyond the instances handed to
/// <see cref="Update"/> are zeroed (degenerate — invisible), so a shrinking batch never
/// leaves stale quads behind.
/// </summary>
public sealed class PbrSpriteBatch
{
    private readonly PbrPrimitive _primitive;
    private readonly float[] _vertices;
    private readonly FlipbookLayout _layout;

    public PbrSpriteBatch(PbrRenderer pbr, int capacity, in FlipbookLayout layout, byte[]? sheetKtx2, Vector4 tint)
    {
        ArgumentNullException.ThrowIfNull(pbr);
        Capacity = Math.Max(1, capacity);
        _layout = layout;
        _vertices = new float[Capacity * SpriteGeometry.QuadFloats];
        var material = PbrSpriteMaterials.AddSheetMaterial(pbr.Materials, sheetKtx2, tint);
        _primitive = pbr.UploadPrimitive(_vertices, SpriteGeometry.QuadIndices(Capacity), material, dynamic: true);
        Instance = new PbrInstance { Mesh = new PbrMesh([_primitive]), Model = Matrix4x4.Identity };
    }

    public int Capacity { get; }

    /// <summary>Add this to <see cref="PbrScene.Instances"/> once; Update re-writes it in place.</summary>
    public PbrInstance Instance { get; }

    /// <summary>Instances beyond <see cref="Capacity"/> are ignored.</summary>
    public void Update(PbrRenderer pbr, ReadOnlySpan<SpriteInstance> sprites, in Vector3 right, in Vector3 up)
    {
        ArgumentNullException.ThrowIfNull(pbr);
        var count = Math.Min(sprites.Length, Capacity);
        // Blank only the tail that was live LAST frame — the [0, count) region is fully
        // rewritten below, so a full-buffer memset would be redundant work every frame.
        if (_lastCount > count)
        {
            Array.Clear(_vertices, count * SpriteGeometry.QuadFloats,
                (_lastCount - count) * SpriteGeometry.QuadFloats);
        }
        _lastCount = count;
        for (var i = 0; i < count; i++)
        {
            ref readonly var sprite = ref sprites[i];
            SpriteGeometry.WriteQuad(
                _vertices, i * SpriteGeometry.QuadFloats, sprite.Center,
                right * sprite.HalfSize, up * sprite.HalfSize, _layout, sprite.Frame);
        }
        pbr.UpdatePrimitiveVertices(_primitive, _vertices);
    }

    private int _lastCount;
}

/// <summary>
/// A dynamic batch of solid axis-aligned cubes (voxel particles): one primitive, one draw,
/// re-written from caller data every frame. Same zero-blanking contract as
/// <see cref="PbrSpriteBatch"/>.
///
/// Voxels are OPAQUE, so they join the shadow-caster set — but a dynamic primitive's
/// object-space AABB is fixed at upload time, and this batch uploads zeroed vertices (an
/// "unknown" AABB that contributes only the instance origin to the shadow-frustum fit).
/// Pass <c>boundsRadius</c> — the max distance voxels roam from the batch origin — when voxel
/// shadows matter and no other opaque geometry spans the scene; 0 keeps the unknown AABB.
/// </summary>
public sealed class PbrVoxelBatch
{
    private readonly PbrPrimitive _primitive;
    private readonly float[] _vertices;

    public PbrVoxelBatch(PbrRenderer pbr, int capacity, Vector4 color, float boundsRadius = 0f)
    {
        ArgumentNullException.ThrowIfNull(pbr);
        Capacity = Math.Max(1, capacity);
        _vertices = new float[Capacity * SpriteGeometry.CubeFloats];
        var material = pbr.Materials.AddDefaultMaterial(color, metallic: 0f, roughness: 1f);
        var uploaded = pbr.UploadPrimitive(_vertices, SpriteGeometry.CubeIndices(Capacity), material, dynamic: true);
        _primitive = boundsRadius > 0f
            ? uploaded with { LocalMin = new Vector3(-boundsRadius), LocalMax = new Vector3(boundsRadius) }
            : uploaded;
        Instance = new PbrInstance { Mesh = new PbrMesh([_primitive]), Model = Matrix4x4.Identity };
    }

    public int Capacity { get; }

    /// <summary>Add this to <see cref="PbrScene.Instances"/> once; Update re-writes it in place.</summary>
    public PbrInstance Instance { get; }

    /// <summary>Instances beyond <see cref="Capacity"/> are ignored.</summary>
    public void Update(PbrRenderer pbr, ReadOnlySpan<VoxelInstance> voxels)
    {
        ArgumentNullException.ThrowIfNull(pbr);
        var count = Math.Min(voxels.Length, Capacity);
        // Blank only the tail that was live LAST frame (see PbrSpriteBatch.Update).
        if (_lastCount > count)
        {
            Array.Clear(_vertices, count * SpriteGeometry.CubeFloats,
                (_lastCount - count) * SpriteGeometry.CubeFloats);
        }
        _lastCount = count;
        for (var i = 0; i < count; i++)
        {
            ref readonly var voxel = ref voxels[i];
            SpriteGeometry.WriteCube(_vertices, i * SpriteGeometry.CubeFloats, voxel.Center, voxel.HalfExtent);
        }
        pbr.UpdatePrimitiveVertices(_primitive, _vertices);
    }

    private int _lastCount;
}
