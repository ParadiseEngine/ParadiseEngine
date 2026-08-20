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
    /// This is what lets the engine's components be DECLARED (with <c>[Authored]</c>) without the
    /// exported document changing shape. An editor knows only ids and JSON — it has no idea that
    /// <c>paradise.rigidbody</c> belongs in <see cref="EntityComponentsData.Rigidbody"/> — so the
    /// mapping lives here, on the contract, where both halves can see it.
    ///
    /// Three destinations:
    ///
    /// - <see cref="ParadiseComponentIds.Identity"/> lands on <see cref="LevelEntityData"/> itself.
    ///   Identity is what an entity IS, not something it has.
    /// - Every other engine id lands in its typed <see cref="EntityComponentsData"/> slot.
    /// - Anything else is a GAME's own component and lands in
    ///   <see cref="EntityComponentsData.Custom"/>, untouched.
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
            EntityComponentsData components = entity.Components;
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
            if (id == ParadiseComponentIds.Renderable)
            {
                return Assign<RenderableComponentData>(component,
                    value => components.Renderable = value);
            }
            if (id == ParadiseComponentIds.Collider)
            {
                return Assign<ColliderComponentData>(component,
                    value => components.Collider = value);
            }
            if (id == ParadiseComponentIds.Rigidbody)
            {
                return Assign<RigidbodyComponentData>(component,
                    value => components.Rigidbody = value);
            }
            if (id == ParadiseComponentIds.Agent)
            {
                return Assign<AgentComponentData>(component, value => components.Agent = value);
            }
            if (id == ParadiseComponentIds.Interactable)
            {
                return Assign<EntityInteractableComponentData>(component,
                    value => components.Interactable = value);
            }
            if (id == ParadiseComponentIds.SpriteAnimation)
            {
                return Assign<SpriteAnimationComponentData>(component,
                    value => components.SpriteAnimation = value);
            }
            if (id == ParadiseComponentIds.Light)
            {
                return Assign<SceneLightData>(component, value => components.Light = value);
            }
            if (id == ParadiseComponentIds.AudioEmitter)
            {
                return Assign<AudioEmitterComponentData>(component,
                    value => components.AudioEmitter = value);
            }
            if (id == ParadiseComponentIds.ParticleEmitter)
            {
                return Assign<ParticleEmitterComponentData>(component,
                    value => components.ParticleEmitter = value);
            }

            // A game's own component. The engine cannot name the type and does not try — the
            // payload rides along and the game reads it with its own context.
            (components.Custom ??= new List<AuthoredComponentData>()).Add(component);
            return true;
        }

        /// <summary>
        /// Apply many, returning the components that could not be read.
        ///
        /// The components themselves rather than their ids, so a caller's message can name the
        /// <see cref="AuthoredComponentData.Type"/> as well. "Could not read
        /// a1d3f6b0-0000-4000-8000-000000000003" is not a diagnostic anyone can act on.
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
        /// The engine's own arrive already typed — that is what the typed slots are — and a game's
        /// come back through its generated registry. The point is that the caller gets one list of
        /// records rather than a mixture of typed properties and raw JSON it has to remember to
        /// deserialize: a component nobody wrote an accessor for is otherwise authored, exported,
        /// and silently never read.
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
            EntityComponentsData c = entity.Components;

            // Ordered like the contract declares them, so a caller walking the list sees a stable
            // shape rather than one that depends on how the document happened to be written.
            if (c.Renderable is { } renderable) instances.Add(renderable);
            if (c.Collider is { } collider) instances.Add(collider);
            if (c.Rigidbody is { } rigidbody) instances.Add(rigidbody);
            if (c.Interactable is { } interactable) instances.Add(interactable);
            if (c.Agent is { } agent) instances.Add(agent);
            if (c.SpriteAnimation is { } sprite) instances.Add(sprite);
            if (c.ParticleEmitter is { } particles) instances.Add(particles);
            if (c.AudioEmitter is { } audio) instances.Add(audio);
            if (c.Light is { } light) instances.Add(light);

            foreach (AuthoredComponentData custom in c.Custom ?? [])
            {
                if (Resolve(registry, custom) is { } value)
                {
                    instances.Add(value);
                    continue;
                }
                unresolved?.Add(custom);
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

        /// <summary>True when an id belongs to the engine, i.e. it routes to a typed slot rather
        /// than into <see cref="EntityComponentsData.Custom"/>.</summary>
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

        private static bool Assign<T>(AuthoredComponentData component, System.Action<T> assign)
            where T : class
        {
            if (Read<T>(component) is not { } value)
            {
                return false;
            }
            assign(value);
            return true;
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
