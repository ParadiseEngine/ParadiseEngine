using System;
using System.Collections.Generic;

namespace Paradise.Ui.ImGui;

/// <summary>Transfers frame snapshots and their ordered texture operations between
/// threads.</summary>
/// <remarks>Publish texture operations before the snapshot; acquire the snapshot before draining
/// operations so every referenced texture is available. Superseded snapshots may be recycled, but
/// operations must never be dropped. A single lock protects all slot transitions; an object lock
/// lets Coyote schedule Monitor.Enter/Exit.</remarks>
public sealed class ImGuiFrameExchange
{
    private readonly object _lock = new();
    private readonly Stack<ImGuiDrawSnapshot> _free = new();
    private ImGuiDrawSnapshot? _latest;
    private ImGuiDrawSnapshot? _rendering;

    /// <summary>The PRODUCER side of the texture queue: enqueue a frame's ops here before
    /// publishing that frame's snapshot. Do not drain it directly — draining outside
    /// <see cref="AcquireForRender"/> is exactly the ordering this type exists to prevent.</summary>
    public ImGuiTextureOps TextureOps { get; } = new();

    /// <summary>Sim thread: a snapshot to capture into — recycled from the pool when one is
    /// spare. Pair every rent with a <see cref="Publish"/>.</summary>
    public ImGuiDrawSnapshot Rent()
    {
        lock (_lock)
        {
            return _free.Count > 0 ? _free.Pop() : new ImGuiDrawSnapshot();
        }
    }

    /// <summary>Sim thread: make <paramref name="snapshot"/> the newest frame. A previous frame
    /// the render thread never took is recycled — dropping it is correct, since nobody misses a
    /// frame that was never shown.</summary>
    public void Publish(ImGuiDrawSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_lock)
        {
            if (_latest is not null) _free.Push(_latest);
            _latest = snapshot;
        }
    }

    /// <summary>Acquires the newest snapshot and appends its pending texture operations.</summary>
    /// <remarks>Render thread only. Reuse the same operation list and apply it before drawing, even
    /// for a repeated snapshot; only ApplyTextureOps clears it so skipped frames retain pending
    /// work.</remarks>
    /// <param name="textureOps">Receives every texture operation not yet applied, in order,
    /// appended after anything already in it.</param>
    /// <param name="isNew">False when this is the same snapshot as the previous call — hosts with
    /// retained scenes (Godot canvas items) skip the rebuild then. It says nothing about
    /// <paramref name="textureOps"/>, which must be applied either way.</param>
    /// <returns>The snapshot to draw, or null before the first published frame.</returns>
    public ImGuiDrawSnapshot? AcquireForRender(List<ImGuiTextureOp> textureOps, out bool isNew)
    {
        ArgumentNullException.ThrowIfNull(textureOps);
        ImGuiDrawSnapshot? snapshot;
        lock (_lock)
        {
            isNew = _latest is not null;
            if (_latest is not null)
            {
                if (_rendering is not null) _free.Push(_rendering);
                _rendering = _latest;
                _latest = null;
            }
            snapshot = _rendering;
        }
        // Strictly after the swap. See the type's remarks — this line is the invariant.
        TextureOps.DrainTo(textureOps);
        return snapshot;
    }
}
