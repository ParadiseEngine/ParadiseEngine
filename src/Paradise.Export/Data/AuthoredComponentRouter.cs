#nullable enable
using System.Collections.Generic;
using System.Text.Json;
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
    /// - <c>paradise.identity</c> lands on <see cref="LevelEntityData"/> itself. Identity is what an
    ///   entity IS, not something it has.
    /// - Every other engine id lands in its typed <see cref="EntityComponentsData"/> slot.
    /// - Anything else is a GAME's own component and lands in
    ///   <see cref="EntityComponentsData.Custom"/>, untouched.
    ///
    /// Reflection-free throughout: the switch selects a source-generated
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
        public static bool Apply(LevelEntityData entity, AuthoredComponentData component)
        {
            EntityComponentsData components = entity.Components;
            switch (component.Id)
            {
                case ParadiseComponentIds.Identity:
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

                case ParadiseComponentIds.Renderable:
                    return Assign<RenderableComponentData>(component,
                        value => components.Renderable = value);

                case ParadiseComponentIds.Collider:
                    return Assign<ColliderComponentData>(component,
                        value => components.Collider = value);

                case ParadiseComponentIds.Rigidbody:
                    return Assign<RigidbodyComponentData>(component,
                        value => components.Rigidbody = value);

                case ParadiseComponentIds.Agent:
                    return Assign<AgentComponentData>(component,
                        value => components.Agent = value);

                case ParadiseComponentIds.Interactable:
                    return Assign<EntityInteractableComponentData>(component,
                        value => components.Interactable = value);

                case ParadiseComponentIds.SpriteAnimation:
                    return Assign<SpriteAnimationComponentData>(component,
                        value => components.SpriteAnimation = value);

                case ParadiseComponentIds.AudioEmitter:
                    return Assign<AudioEmitterComponentData>(component,
                        value => components.AudioEmitter = value);

                case ParadiseComponentIds.ParticleEmitter:
                    return Assign<ParticleEmitterComponentData>(component,
                        value => components.ParticleEmitter = value);

                default:
                    // A game's own component. The engine cannot name the type and does not try —
                    // the payload rides along and the game reads it with its own context.
                    (components.Custom ??= new List<AuthoredComponentData>()).Add(component);
                    return true;
            }
        }

        /// <summary>Apply many, reporting the ids that could not be read.</summary>
        public static IReadOnlyList<string> ApplyAll(
            LevelEntityData entity, IEnumerable<AuthoredComponentData> components)
        {
            var failed = new List<string>();
            foreach (AuthoredComponentData component in components)
            {
                if (string.IsNullOrWhiteSpace(component.Id))
                {
                    continue;
                }
                if (!Apply(entity, component))
                {
                    failed.Add(component.Id);
                }
            }
            return failed;
        }

        /// <summary>True when an id belongs to the engine, i.e. it routes to a typed slot rather
        /// than into <see cref="EntityComponentsData.Custom"/>.</summary>
        public static bool IsEngineComponent(string id) => id switch
        {
            ParadiseComponentIds.Identity or
            ParadiseComponentIds.Renderable or
            ParadiseComponentIds.Collider or
            ParadiseComponentIds.Rigidbody or
            ParadiseComponentIds.Agent or
            ParadiseComponentIds.Interactable or
            ParadiseComponentIds.SpriteAnimation or
            ParadiseComponentIds.ParticleEmitter or
            ParadiseComponentIds.AudioEmitter => true,
            _ => false,
        };

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
