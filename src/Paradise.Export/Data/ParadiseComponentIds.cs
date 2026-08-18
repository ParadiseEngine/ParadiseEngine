#nullable enable
namespace Paradise.Export.Data
{
    /// <summary>
    /// The stable ids the engine's own authored components travel under.
    ///
    /// These are contract: an editor writes them into the exported document and
    /// <see cref="AuthoredComponentRouter"/> maps them back onto the typed record. Renaming one
    /// orphans every scene that authored it.
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
        public const string Identity = "paradise.identity";

        public const string Renderable = "paradise.renderable";
        public const string Collider = "paradise.collider";
        public const string Rigidbody = "paradise.rigidbody";
        public const string Agent = "paradise.agent";
        public const string Interactable = "paradise.interactable";
        public const string SpriteAnimation = "paradise.sprite-animation";
        public const string ParticleEmitter = "paradise.particle-emitter";
        public const string AudioEmitter = "paradise.audio-emitter";
    }
}
