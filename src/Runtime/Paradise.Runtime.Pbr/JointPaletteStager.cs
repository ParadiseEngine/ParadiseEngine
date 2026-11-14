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
    /// <param name="meshJoint">-1 stages the palette into the model's space, for an instance placed at the
    /// model's origin. A joint index stages it into that joint's space (<c>× inverse(model[meshJoint])</c>), which
    /// is right only when the instance's <see cref="PbrInstance.Model"/> includes <c>model[meshJoint]</c>; both
    /// put the vertex in the same place.</param>
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

    /// <summary>Stages a skinned mesh posed by <paramref name="player"/>, for one instance whose
    /// <see cref="PbrInstance.Model"/> is the model's placement.</summary>
    /// <remarks>Staged in model space (no mesh-joint term): a cooked blob's rigid draws are baked into the
    /// model's space, so this is the one placement under which an instance of the whole
    /// <see cref="PbrMeshAsset.Mesh"/> draws rigid and skinned primitives alike in the right place, and one
    /// palette serves every skinned draw whatever node each sat on. It equals staging with each draw's
    /// <see cref="PbrMeshAsset.MeshJoints"/> under a placement that includes that joint's model matrix.</remarks>
    public int Stage(AnimationPlayer player, PbrMeshAsset mesh)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.Skin is not { } skin)
            throw new ArgumentException($"Mesh '{mesh.Path}' has no skin to pose.", nameof(mesh));
        return Stage(player.ModelMatrices, skin.Joints, skin.InverseBindMatrices, meshJoint: -1);
    }
}
