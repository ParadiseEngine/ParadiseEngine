#nullable enable
using System;
using System.Numerics;

namespace Paradise.Export.Geometry
{
    /// <summary>
    /// Engine-neutral collider scale-folding, ported from ParadiseUnityEditor's
    /// ColliderExportUtility. The export contract bakes the collider's lossy scale (relative to
    /// the entity root) into the shape's dimensions; the root's OWN scale stays in the entity
    /// WorldMatrix, so a data consumer folds it in with these same rules (see
    /// Paradise.Sample.Runtime.SceneAssembler.AppendCollider).
    ///
    /// Godot's <c>CapsuleShape3D</c> is always Y-axis aligned (unlike Unity's <c>direction</c>
    /// enum), so only the Y-aligned capsule case is modeled here — it matches Unity's
    /// <c>direction = 1</c> path exactly (radius from max(|x|,|z|), height from |y|). Non-Y
    /// capsule orientation in Godot is achieved by rotating the node, captured separately in the
    /// collider's local rotation.
    /// </summary>
    public static class ColliderScaleFold
    {
        /// <summary>Per-component lossy scale of a collider relative to the export root, with a
        /// divide-by-zero guard (mirrors Unity's <c>GetRelativeScale</c>).</summary>
        public static Vector3 RelativeScale(Vector3 sourceLossyScale, Vector3 rootLossyScale) =>
            new(
                Divide(sourceLossyScale.X, rootLossyScale.X),
                Divide(sourceLossyScale.Y, rootLossyScale.Y),
                Divide(sourceLossyScale.Z, rootLossyScale.Z));

        /// <summary>Box full-size folded with the absolute relative scale (component-wise).</summary>
        public static Vector3 BoxSize(Vector3 size, Vector3 relativeScale) =>
            size * Abs(relativeScale);

        /// <summary>Sphere radius folded with the largest absolute scale axis.</summary>
        public static float SphereRadius(float radius, Vector3 relativeScale)
        {
            Vector3 s = Abs(relativeScale);
            return radius * MathF.Max(s.X, MathF.Max(s.Y, s.Z));
        }

        /// <summary>Y-aligned capsule radius folded with max(|x|,|z|).</summary>
        public static float CapsuleRadius(float radius, Vector3 relativeScale)
        {
            Vector3 s = Abs(relativeScale);
            return radius * MathF.Max(s.X, s.Z);
        }

        /// <summary>Y-aligned capsule height folded with |y|.</summary>
        public static float CapsuleHeight(float height, Vector3 relativeScale) =>
            height * MathF.Abs(relativeScale.Y);

        private static float Divide(float value, float divisor) =>
            MathF.Abs(divisor) <= 1e-6f ? 0f : value / divisor;

        private static Vector3 Abs(Vector3 v) =>
            new(MathF.Abs(v.X), MathF.Abs(v.Y), MathF.Abs(v.Z));
    }
}
