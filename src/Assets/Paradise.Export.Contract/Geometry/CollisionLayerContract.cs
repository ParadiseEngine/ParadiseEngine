#nullable enable
using System.Numerics;

namespace Paradise.Export.Geometry
{
    /// <summary>Converts a collision-layer mask to its lowest set-bit index.</summary>
    /// <remarks>Consumers reconstruct <c>1u &lt;&lt; Layer</c>; <see cref="IsMultiLayer"/> identifies masks that lose membership.</remarks>
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
