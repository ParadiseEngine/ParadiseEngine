#nullable enable
using System;
using System.Numerics;

namespace Paradise.Export.Geometry
{
    /// <summary>Folds collider-relative scale into shape dimensions using the Unity export rules.</summary>
    /// <remarks>
    /// Entity scale remains in the entity transform and is folded by the consumer.
    /// Y-aligned capsules use max(|x|, |z|) for radius and |y| for height;
    /// other orientations are represented by the collider's local rotation.
    /// </remarks>
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
