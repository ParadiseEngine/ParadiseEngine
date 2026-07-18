#nullable enable
namespace Paradise.Export.Authoring
{
    /// <summary>
    /// Engine-neutral default values for authoring components. Ported verbatim from
    /// ParadiseUnityEditor so the Godot authoring layer (EntityExport) and the exporter agree
    /// on the same defaults, keeping exported agent data identical across both toolchains.
    /// </summary>
    public static class ParadiseAuthoringDefaults
    {
        /// <summary>Default agent movement speed, meters/second.</summary>
        public const float MoveSpeed = 1.4f;

        /// <summary>Default agent acceleration, meters/second^2.</summary>
        public const float Acceleration = 40f;

        /// <summary>Animation clip name used when no idle clip is authored.</summary>
        public const string IdleAnimationFallback = "Idle";

        /// <summary>Animation clip name used when no walk clip is authored.</summary>
        public const string WalkAnimationFallback = "Walk";
    }
}
