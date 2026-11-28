using System.Numerics;
using System.Runtime.InteropServices;
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
/// <para>An instance belongs to its key while the key is added every frame. Once a key is missing from a
/// frame, or changes mesh, its instance is retired and may later be reused for another key with the same
/// mesh, so a steady stream of spawns allocates nothing. A retired instance always sits out at least one
/// frame first: the renderer keeps motion history only for instances it drew in the previous frame, so the
/// new key never inherits the old one's motion. Do not keep an instance after its key is gone.</para>
/// <para>Camera, lights and the rest of the scene stay the caller's to set on <see cref="Scene"/>.</para>
/// </remarks>
public sealed class PbrSceneBuilder
{
    /// <summary>Frames a mesh's retired instances wait for reuse before they are dropped, so pools do not
    /// keep released meshes alive.</summary>
    private const int IdleFramesBeforeTrim = 120;

    private readonly Dictionary<long, Slot> _slots = [];
    private readonly Dictionary<PbrMesh, Pool> _pools = new(ReferenceEqualityComparer.Instance);
    private readonly List<long> _stale = [];
    private readonly List<PbrInstance> _retiring = [];
    private readonly List<PbrMesh> _idle = [];
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
    /// <returns>The key's instance. A new key's instance has every setting at its default.</returns>
    /// <exception cref="InvalidOperationException">The key was already added this frame.</exception>
    public PbrInstance Add(long key, PbrMesh mesh, in Matrix4x4 model, int jointOffset = -1)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_slots, key, out var exists);
        if (!exists)
        {
            slot.Instance = Rent(mesh);
        }
        else if (slot.Frame == _frame)
        {
            // Before the mesh check: an instance this frame already drew must never be retired, or the
            // next frame could rent it to another key along with this one's motion history.
            throw new InvalidOperationException($"Renderable {key} was added twice in one frame.");
        }
        else if (!ReferenceEquals(slot.Instance.Mesh, mesh))
        {
            // PbrInstance.Mesh is init-only: a new mesh is a new instance, as it is to motion history.
            _retiring.Add(slot.Instance);
            slot.Instance = Rent(mesh);
        }

        slot.Frame = _frame;
        var instance = slot.Instance;
        instance.Model = model;
        instance.JointOffset = jointOffset;
        Scene.Instances.Add(instance);
        return instance;
    }

    /// <summary>Ends the frame, forgetting keys that were not added in it and retiring their instances.</summary>
    public void End()
    {
        foreach (var (key, slot) in _slots)
        {
            if (slot.Frame == _frame) continue;
            _stale.Add(key);
            _retiring.Add(slot.Instance);
        }

        foreach (var key in _stale) _slots.Remove(key);
        _stale.Clear();

        // Retired now, so the earliest reuse is next frame: none of these was drawn in this one.
        foreach (var instance in _retiring) PoolOf(instance.Mesh).Free.Push(instance);
        _retiring.Clear();
        TrimIdlePools();
    }

    private PbrInstance Rent(PbrMesh mesh)
    {
        if (!_pools.TryGetValue(mesh, out var pool) || !pool.Free.TryPop(out var instance))
            return new PbrInstance { Mesh = mesh };

        pool.LastUsed = _frame;
        Reset(instance);
        return instance;
    }

    private Pool PoolOf(PbrMesh mesh)
    {
        ref var pool = ref CollectionsMarshal.GetValueRefOrAddDefault(_pools, mesh, out _);
        pool ??= new Pool();
        pool.LastUsed = _frame;
        return pool;
    }

    private void TrimIdlePools()
    {
        foreach (var (mesh, pool) in _pools)
            if (_frame - pool.LastUsed > IdleFramesBeforeTrim) _idle.Add(mesh);
        foreach (var mesh in _idle) _pools.Remove(mesh);
        _idle.Clear();
    }

    /// <summary>Returns everything but the mesh to a new instance's defaults; <see cref="Add(long, PbrMesh, in Matrix4x4, int)"/>
    /// sets the transform and palette offset.</summary>
    private static void Reset(PbrInstance instance)
    {
        instance.GiMesh = null;
        instance.Highlight = 0f;
        instance.ReceivesDecals = true;
        instance.GiMode = PbrGiMode.Static;
        instance.ShadowsOnly = false;
    }

    private struct Slot
    {
        public PbrInstance Instance;
        public int Frame;
    }

    private sealed class Pool
    {
        public readonly Stack<PbrInstance> Free = new();
        public int LastUsed;
    }
}
