#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using Paradise.Authoring;
using Paradise.Export.Serialization;

namespace Paradise.Export.Data
{
    /// <summary>
    /// Puts an authored payload where the runtime expects to find it.
    ///
    /// TWO destinations, and the second is "the list":
    ///
    /// - <see cref="ParadiseComponentIds.Identity"/> lands on <see cref="LevelEntityData"/> itself.
    ///   Identity is what an entity IS, not something it has, so it has no entry of its own.
    /// - EVERYTHING else — the engine's components and a game's alike — is appended to
    ///   <see cref="LevelEntityData.Components"/> untouched.
    ///
    /// There used to be a third: nine typed slots the engine's own components were unpacked into.
    /// That tier is gone. It bought typed access at the cost of a GUID-to-slot mapping that had to
    /// exist in this file, in the Godot editor, and again in the Blender addon's Python mirror —
    /// so an engine component could not be added without editing all three. Reading a component
    /// back is now <see cref="LevelEntityExtensions.Get{T}"/> or <see cref="Materialize"/>.
    ///
    /// Reflection-free throughout: the dispatch selects a source-generated
    /// <see cref="JsonTypeInfo{T}"/>, because a reflection deserializer would pin Godot's
    /// collectible AssemblyLoadContext and break C# hot-reload (godotengine/godot#78513).
    /// </summary>
    public static class AuthoredComponentRouter
    {
        /// <summary>
        /// Apply one authored component to an entity. Returns false when the payload names an
        /// engine id but cannot be read as that component — the caller should report it rather than
        /// silently drop authored data.
        /// </summary>
        /// <remarks>
        /// An if-chain rather than the switch this used to be: a <see cref="Guid"/> cannot be
        /// <c>const</c>, so it cannot be a <c>case</c> label. The order is the contract's own.
        /// </remarks>
        public static bool Apply(LevelEntityData entity, AuthoredComponentData component)
        {
            Guid id = component.Id;

            if (id == ParadiseComponentIds.Identity)
            {
                if (Read<IdentityComponentData>(component) is not { } identity)
                {
                    return false;
                }
                // Spread across the entity's own fields. DisplayName and SpawnPhase are left
                // alone when unauthored so the exporter's own defaults (node name, LevelStart)
                // survive rather than being overwritten with null.
                entity.Kind = identity.Kind;
                entity.IsActive = identity.IsActive;
                entity.InitialAnimation = NullIfBlank(identity.InitialAnimation);
                entity.Prefab = NullIfBlank(identity.Prefab);
                if (!string.IsNullOrWhiteSpace(identity.DisplayName))
                {
                    entity.DisplayName = identity.DisplayName;
                }
                if (!string.IsNullOrWhiteSpace(identity.SpawnPhase))
                {
                    entity.SpawnPhase = identity.SpawnPhase;
                }
                return true;
            }
            // Everything else rides in the list exactly as the editor wrote it. Nothing is
            // deserialized here, which is why this can no longer fail for anything but identity:
            // an unreadable payload is now found by Materialize, which is the thing that reads it.
            entity.Components.Add(component);
            return true;
        }

        /// <summary>
        /// Apply many, returning the components that could not be read.
        ///
        /// The components themselves rather than their ids, so a caller's message can name the
        /// <see cref="AuthoredComponentData.Type"/> as well. "Could not read
        /// b7ab4dd8-c8da-4dc2-9e5e-192fd74deb11" is not a diagnostic anyone can act on.
        /// </summary>
        public static IReadOnlyList<AuthoredComponentData> ApplyAll(
            LevelEntityData entity, IEnumerable<AuthoredComponentData> components)
        {
            var failed = new List<AuthoredComponentData>();
            foreach (AuthoredComponentData component in components)
            {
                // Dropped only when there is NOTHING to identify it by. An id-less payload that
                // still names its type is the case the type name exists for — a document written
                // before its component had an id — so it rides through to Custom, where
                // Materialize repairs it. Dropping it here would make that fallback unreachable
                // for the one document that most needs it.
                if (component.Id == Guid.Empty && string.IsNullOrWhiteSpace(component.Type))
                {
                    continue;
                }
                if (!Apply(entity, component))
                {
                    failed.Add(component);
                }
            }
            return failed;
        }

        /// <summary>
        /// Every authored component on an entity, as INSTANCES.
        ///
        /// The engine's own come back through the closed switch below; a game's through its
        /// generated registry. The point is that the caller gets one list of records rather than
        /// raw JSON it has to remember to deserialize: a component nobody wrote an accessor for is
        /// otherwise authored, exported, and silently never read.
        ///
        /// Payloads whose id the registry does not know are retried against
        /// <see cref="AuthoredComponentData.Type"/>, then skipped and reported — never guessed at.
        /// </summary>
        public static IReadOnlyList<object> Materialize(
            LevelEntityData entity,
            IAuthoredComponentRegistry? registry = null,
            IList<AuthoredComponentData>? unresolved = null)
        {
            var instances = new List<object>();

            // Document order. It used to be "the order the contract declares the slots in", which
            // no longer exists — so the editor's order is the only order there is, and both
            // editors are explicit about emitting a stable one.
            foreach (AuthoredComponentData component in entity.Components)
            {
                if (Resolve(registry, component) is { } value)
                {
                    instances.Add(value);
                    continue;
                }
                unresolved?.Add(component);
            }

            return instances;
        }

        /// <summary>
        /// One payload as an instance: by id, else by type name, else null.
        ///
        /// The second attempt is what makes an opaque id survivable. A component whose id was
        /// regenerated, or whose document was written by a host with a stale schema, still loads —
        /// and the alternative is a payload nobody can even identify, because the only thing it
        /// says about itself is a number that matches nothing.
        /// </summary>
        private static object? Resolve(
            IAuthoredComponentRegistry? registry, AuthoredComponentData component)
        {
            // The engine's own first, and without a registry: Paradise.Export deliberately has no
            // generated one, and an engine component must materialize for a caller that passed no
            // registry at all (a host reading a scene it does not add components to).
            if (ReadEngineComponent(component) is { } engineComponent)
            {
                return engineComponent;
            }
            if (registry is null)
            {
                return null;
            }
            if (component.Id != Guid.Empty &&
                registry.TryRead(component.Id, component.Data, out object? byId) && byId is not null)
            {
                return byId;
            }
            if (!string.IsNullOrWhiteSpace(component.Type) &&
                registry.TryReadByType(component.Type!, component.Data, out object? byType))
            {
                return byType;
            }
            return null;
        }

        /// <summary>True when an id belongs to the engine rather than to a game.
        ///
        /// A question about OWNERSHIP now, not about destination — every component goes to the
        /// same place. Kept because hosts still ask it, and because it is the set
        /// <see cref="ReadEngineComponent"/> must stay in step with.</summary>
        public static bool IsEngineComponent(Guid id) => EngineIds.Contains(id);

        /// <summary>A set rather than the chain <see cref="Apply"/> uses: this one answers a
        /// membership question, and there is no per-id behaviour to hang off the branches.</summary>
        private static readonly HashSet<Guid> EngineIds =
        [
            ParadiseComponentIds.Identity,
            ParadiseComponentIds.Renderable,
            ParadiseComponentIds.Collider,
            ParadiseComponentIds.Rigidbody,
            ParadiseComponentIds.Agent,
            ParadiseComponentIds.Interactable,
            ParadiseComponentIds.SpriteAnimation,
            ParadiseComponentIds.ParticleEmitter,
            ParadiseComponentIds.AudioEmitter,
            ParadiseComponentIds.Light,
        ];

        /// <summary>One of the engine's own components as its record, or null when the id is not
        /// the engine's (or the payload will not read as it).
        ///
        /// A closed if-chain, deliberately. <c>ReadElement&lt;T&gt;</c> needs T at compile time, and
        /// the obvious alternative — <c>Type.GetType(component.Type)</c> — is an IL2057/IL3050
        /// error under this assembly's <c>IsAotCompatible</c> with warnings-as-errors, not merely
        /// a slower path. A Guid cannot be a <c>case</c> label, hence ifs rather than a switch.
        ///
        /// Must list exactly <see cref="EngineIds"/>. A component in one and not the other either
        /// never materializes or claims to be the engine's and is not.</summary>
        private static object? ReadEngineComponent(AuthoredComponentData component)
        {
            Guid id = component.Id;
            if (id == ParadiseComponentIds.Identity) return Read<IdentityComponentData>(component);
            if (id == ParadiseComponentIds.Renderable) return Read<RenderableComponentData>(component);
            if (id == ParadiseComponentIds.Collider) return Read<ColliderComponentData>(component);
            if (id == ParadiseComponentIds.Rigidbody) return Read<RigidbodyComponentData>(component);
            if (id == ParadiseComponentIds.Agent) return Read<AgentComponentData>(component);
            if (id == ParadiseComponentIds.Interactable) return Read<EntityInteractableComponentData>(component);
            if (id == ParadiseComponentIds.SpriteAnimation) return Read<SpriteAnimationComponentData>(component);
            if (id == ParadiseComponentIds.ParticleEmitter) return Read<ParticleEmitterComponentData>(component);
            if (id == ParadiseComponentIds.AudioEmitter) return Read<AudioEmitterComponentData>(component);
            if (id == ParadiseComponentIds.Light) return Read<SceneLightData>(component);
            return null;
        }

        private static T? Read<T>(AuthoredComponentData component) where T : class
        {
            try
            {
                return ExportJsonReader.ReadElement<T>(component.Data);
            }
            catch (JsonException)
            {
                // Reported by the caller, which knows which entity it was. Swallowing it here would
                // turn authored data quietly into a missing component.
                return null;
            }
        }

        private static string? NullIfBlank(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
