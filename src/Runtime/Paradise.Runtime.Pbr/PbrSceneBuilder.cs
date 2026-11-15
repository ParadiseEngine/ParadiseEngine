using System.Numerics;
using Paradise.Rendering.Pbr;

namespace Paradise.Runtime.Pbr;

/// <summary>Rebuilds a <see cref="PbrScene"/>'s instances each frame from what the game's world says is
/// visible, keeping one <see cref="PbrInstance"/> per key across frames.</summary>
/// <remarks>
/// <para>Extraction is the game's query over its snapshot (the engine cannot name a game's ECS
/// components); per renderable it calls <see cref="Add(long, AssetHandle{PbrMeshAsset}, in Matrix4x4, int)"/>
/// with a stable key, normally the entity id. Stable instances matter: motion vectors and the tracer's
/// caches recognise an object by its instance, so a pool reused in a different order each frame would
/// smear motion across unrelated objects.</para>
/// <para>Camera, lights and the rest of the scene stay the caller's to set on <see cref="Scene"/>.</para>
/// </remarks>
public sealed class PbrSceneBuilder
{
    private readonly Dictionary<long, Slot> _slots = [];
    private readonly List<long> _stale = [];
    private int _frame;

    public PbrSceneBuilder(PbrScene? scene = null) => Scene = scene ?? new PbrScene();

    /// <summary>The scene the builder fills; its <see cref="PbrScene.Instances"/> belong to the builder.</summary>
    public PbrScene Scene { get; }

    /// <summary>Renderables skipped this frame because their mesh was not ready.</summary>
    public int Skipped { get; private set; }

    /// <summary>Keys the builder holds instances for.</summary>
    public int Count => _slots.Count;

    /// <summary>Starts a frame: the instance list is rebuilt from the following adds.</summary>
    public void Begin()
    {
        _frame++;
        Skipped = 0;
        Scene.Instances.Clear();
    }

    /// <summary>Adds a renderable whose mesh is still loading or ready; one still loading is skipped.</summary>
    /// <returns>The instance, for further settings, or null when skipped.</returns>
    public PbrInstance? Add(long key, AssetHandle<PbrMeshAsset> mesh, in Matrix4x4 model, int jointOffset = -1)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (!mesh.TryGet(out var asset))
        {
            Skipped++;
            return null;
        }

        return Add(key, asset.Mesh, model, jointOffset);
    }

    /// <summary>Adds a renderable. A key keeps its instance while its mesh stays the same.</summary>
    /// <param name="model">Row-vector model matrix.</param>
    /// <param name="jointOffset">The palette offset <see cref="JointPaletteStager.Stage(ReadOnlySpan{Matrix4x4}, ReadOnlySpan{int}, ReadOnlySpan{Matrix4x4}, int)"/> returned, or -1.</param>
    /// <exception cref="InvalidOperationException">The key was already added this frame.</exception>
    public PbrInstance Add(long key, PbrMesh mesh, in Matrix4x4 model, int jointOffset = -1)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (!_slots.TryGetValue(key, out var slot) || !ReferenceEquals(slot.Instance.Mesh, mesh))
        {
            // PbrInstance.Mesh is init-only: a new mesh is a new instance, as it is to motion history.
            slot = new Slot(new PbrInstance { Mesh = mesh });
            _slots[key] = slot;
        }
        else if (slot.Frame == _frame)
        {
            throw new InvalidOperationException($"Renderable {key} was added twice in one frame.");
        }

        slot.Frame = _frame;
        slot.Instance.Model = model;
        slot.Instance.JointOffset = jointOffset;
        Scene.Instances.Add(slot.Instance);
        return slot.Instance;
    }

    /// <summary>Ends the frame, forgetting keys that were not added in it.</summary>
    public void End()
    {
        foreach (var (key, slot) in _slots)
        {
            if (slot.Frame != _frame) _stale.Add(key);
        }

        foreach (var key in _stale) _slots.Remove(key);
        _stale.Clear();
    }

    private sealed class Slot(PbrInstance instance)
    {
        public readonly PbrInstance Instance = instance;
        public int Frame;
    }
}
