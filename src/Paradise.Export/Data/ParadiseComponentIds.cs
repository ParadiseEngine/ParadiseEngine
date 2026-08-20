#nullable enable
using System;

namespace Paradise.Export.Data
{
    /// <summary>
    /// The stable ids the engine's own authored components travel under.
    ///
    /// These are contract: an editor writes them into the exported document and
    /// <see cref="AuthoredComponentRouter"/> maps them back onto the typed record.
    ///
    /// They are GUIDs, and they are the SAME GUIDs the records carry in their
    /// <see cref="System.Runtime.InteropServices.GuidAttribute"/> — one component, one identity,
    /// rather than an authoring id and an ECS id that can drift apart. <see cref="Light"/> and
    /// <see cref="Identity"/> are the two that had no ECS counterpart to borrow from and continue
    /// the same series.
    ///
    /// Note what is NOT here: an "is this enabled" flag for anything. Presence of the component is
    /// the flag, which is why the old EntityExport booleans (IsAgent, IsDynamicBody, a particle
    /// kind whose first member meant "none") have no successor — you add the component or you
    /// don't.
    /// </summary>
    public static class ParadiseComponentIds
    {
        /// <summary>Entity-level identity. Routed onto <see cref="LevelEntityData"/> itself rather
        /// than into <see cref="EntityComponentsData"/> — it is what the entity IS, not something
        /// it has.</summary>
        public static readonly Guid Identity = new(Raw.Identity);

        public static readonly Guid Renderable = new(Raw.Renderable);
        public static readonly Guid Collider = new(Raw.Collider);
        public static readonly Guid Rigidbody = new(Raw.Rigidbody);
        public static readonly Guid Agent = new(Raw.Agent);
        public static readonly Guid Interactable = new(Raw.Interactable);
        public static readonly Guid SpriteAnimation = new(Raw.SpriteAnimation);
        public static readonly Guid ParticleEmitter = new(Raw.ParticleEmitter);
        public static readonly Guid AudioEmitter = new(Raw.AudioEmitter);
        public static readonly Guid Light = new(Raw.Light);

        /// <summary>
        /// The same ids as string literals, for the <c>[Guid(...)]</c> on each record.
        ///
        /// They exist only because an attribute argument must be a compile-time constant and a
        /// <see cref="Guid"/> cannot be one. Everything that is not an attribute should use the
        /// <see cref="Guid"/> above, so that a typo is a compile error rather than a lookup that
        /// quietly finds nothing.
        /// </summary>
        public static class Raw
        {
            public const string Identity = "a1d3f6b0-0000-4000-8000-00000000000a";
            public const string Renderable = "a1d3f6b0-0000-4000-8000-000000000001";
            public const string Collider = "a1d3f6b0-0000-4000-8000-000000000002";
            public const string Rigidbody = "a1d3f6b0-0000-4000-8000-000000000003";
            public const string Agent = "a1d3f6b0-0000-4000-8000-000000000004";
            public const string Interactable = "a1d3f6b0-0000-4000-8000-000000000005";
            public const string SpriteAnimation = "a1d3f6b0-0000-4000-8000-000000000006";
            public const string ParticleEmitter = "a1d3f6b0-0000-4000-8000-000000000007";
            public const string AudioEmitter = "a1d3f6b0-0000-4000-8000-000000000008";
            public const string Light = "a1d3f6b0-0000-4000-8000-000000000009";
        }
    }
}
