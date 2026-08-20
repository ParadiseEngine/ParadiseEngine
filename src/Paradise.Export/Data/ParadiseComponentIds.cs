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
    /// rather than an authoring id and an ECS id that can drift apart.
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
        ///
        /// GENERATED, every one of them, by an actual UUID generator. Adding a component means
        /// running <c>uuidgen</c> (or <c>Guid.NewGuid()</c>) and pasting the result — never
        /// hand-typing a value, and never continuing a visible pattern.
        ///
        /// These were a counted sequence once, all sharing a prefix and differing in the last
        /// digit, and the sequence is what made them dangerous. It reads as an invitation: the
        /// obvious way to add the eleventh component is to type the next number, which is both
        /// how the tenth got its id and how a game repo would mint one that collides with the
        /// engine. A generated id has no next, so the only way to get another is to generate it.
        /// The numbering also lied — the version nibble claimed random while the value carried
        /// almost no entropy at all.
        /// </summary>
        public static class Raw
        {
            public const string Identity = "0c068bf4-495f-495b-be8d-9b02042a41c2";
            public const string Renderable = "f2c0357e-94dd-4a5a-9803-518066cb54b2";
            public const string Collider = "e1cd1bc8-86f2-4225-adc9-4a324c70ebf9";
            public const string Rigidbody = "b7ab4dd8-c8da-4dc2-9e5e-192fd74deb11";
            public const string Agent = "5801915b-3d0c-4940-8970-7d1487b991cf";
            public const string Interactable = "0283ee5f-775b-412b-a91c-03ecd9b61165";
            public const string SpriteAnimation = "d3e53cd4-89c6-4ca8-851e-7596da889c68";
            public const string ParticleEmitter = "1b4d1bdd-dea1-4b86-9b6a-879c46346b9e";
            public const string AudioEmitter = "e6ec7f42-df09-4ec9-af06-128ddf3eda8e";
            public const string Light = "fc886b84-c48c-4415-afd9-b03d6faf5ab7";
        }
    }
}
