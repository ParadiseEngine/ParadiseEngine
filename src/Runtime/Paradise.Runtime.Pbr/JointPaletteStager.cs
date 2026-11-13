using System.Numerics;
using Paradise.Animation;
using Paradise.Rendering.Pbr;

namespace Paradise.Runtime.Pbr;

/// <summary>Packs each frame's skinned instances into the renderer's shared joint palette.</summary>
/// <remarks>Per frame: <see cref="Begin"/>, then one <c>Stage</c> per skinned instance, whose returned offset
/// goes to <see cref="PbrInstance.JointOffset"/>, all before <see cref="PbrRenderer.RenderFrame"/>.
/// Offsets are handed out end to end; the palette grows as they need.</remarks>
public sealed class JointPaletteStager
{
    private readonly PbrRenderer _renderer;
    private Matrix4x4[] _palette = new Matrix4x4[64];

    public JointPaletteStager(PbrRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        _renderer = renderer;
    }

    /// <summary>Palette slots handed out this frame.</summary>
    public int Staged { get; private set; }

    /// <summary>Starts a frame at palette offset zero.</summary>
    public void Begin() => Staged = 0;

    /// <summary>Stages one skinned instance from model-space joint matrices and its mesh's skin.</summary>
    /// <param name="models">Model-space joint matrices, e.g. <see cref="AnimationPlayer.ModelMatrices"/>.</param>
    /// <param name="joints">Palette slot → skeleton joint (the mesh skin's joints).</param>
    /// <param name="inverseBinds">The skin's inverse-bind matrices.</param>
    /// <param name="meshJoint">The joint the mesh sits on, or -1 for the model's origin.</param>
    /// <returns>The instance's <see cref="PbrInstance.JointOffset"/>.</returns>
    public int Stage(ReadOnlySpan<Matrix4x4> models, ReadOnlySpan<int> joints, ReadOnlySpan<Matrix4x4> inverseBinds, int meshJoint = -1)
    {
        if (_palette.Length < joints.Length) Array.Resize(ref _palette, (int)BitOperations.RoundUpToPowerOf2((uint)joints.Length));
        var palette = _palette.AsSpan(0, joints.Length);
        SkinningPalette.Compute(models, joints, inverseBinds, meshJoint, palette);
        var offset = Staged;
        _renderer.SetJointPalette(offset, palette);
        Staged += joints.Length;
        return offset;
    }

    /// <summary>Stages a skinned instance posed by <paramref name="player"/>.</summary>
    public int Stage(AnimationPlayer player, PbrMeshAsset mesh, int meshJoint = -1)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.Skin is not { } skin)
            throw new ArgumentException($"Mesh '{mesh.Path}' has no skin to pose.", nameof(mesh));
        return Stage(player.ModelMatrices, skin.Joints, skin.InverseBindMatrices, meshJoint);
    }
}
