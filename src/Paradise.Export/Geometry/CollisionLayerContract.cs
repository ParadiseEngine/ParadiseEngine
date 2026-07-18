#nullable enable
using System.Numerics;

namespace Paradise.Export.Geometry
{
    /// <summary>
    /// The engine-neutral collision-layer contract. Godot stores collision layers as a bitmask on
    /// the owning body, but <c>ColliderShapeData.Layer</c> is a Unity-style single layer INDEX:
    /// consumers reconstruct the membership mask as <c>1u &lt;&lt; Layer</c> (see
    /// <c>Paradise.Sample.Runtime.SceneAssembler.AppendCollider</c>). A single int therefore cannot
    /// represent multi-layer membership — this helper collapses a mask to the index of its lowest
    /// set bit and exposes <see cref="IsMultiLayer"/> so the exporter can warn on the lossy case.
    /// </summary>
    public static class CollisionLayerContract
    {
        /// <summary>Index of the lowest set bit of a Godot collision mask (mask 1 → 0, mask 2 → 1);
        /// an unlayered body (mask 0) maps to index 0.</summary>
        public static int MaskToLayerIndex(uint mask) =>
            mask == 0u ? 0 : BitOperations.TrailingZeroCount(mask);

        /// <summary>True when the mask has more than one layer bit set — <see cref="MaskToLayerIndex"/>
        /// is lossy here (all but the lowest bit are discarded).</summary>
        public static bool IsMultiLayer(uint mask) => (mask & (mask - 1u)) != 0u;
    }
}
