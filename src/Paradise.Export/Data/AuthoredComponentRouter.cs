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
    /// - <see cref="IdentityComponentData"/> lands on <see cref="LevelEntityData"/> itself.
    ///   Identity is what an entity IS, not something it has, so it has no entry of its own.
    /// - EVERYTHING else — the engine's components and a game's alike — is appended to
    ///   <see cref="LevelEntityData.Components"/> untouched.
    ///
    /// There used to be a third: nine typed slots the engine's own components were unpacked into.
    /// That tier is gone. It bought typed access at the cost of a GUID-to-slot mapping that had to
    /// exist in this file, in the Godot editor, and again in the Blender addon's Python mirror —
    /// so an engine component could not be added without editing all three. Reading a component
    /// back is now <see cref="LevelEntityExtensions.Get{T}"/> or <c>Materialize</c>.
    ///
    /// Reflection-free throughout: the dispatch selects a source-generated
    /// <c>JsonTypeInfo&lt;T&gt;</c>, because a reflection deserializer would pin Godot's
    /// collectible AssemblyLoadContext and break C# hot-reload (godotengine/godot#78513).
    /// </summary>
    public static class AuthoredComponentRouter
    {
        /// <summary>
        /// The one id this class compares against, read off the record's own <c>[Guid]</c>.
        ///
        /// Cached rather than written <c>typeof(IdentityComponentData).GUID</c> at the comparison:
        /// <see cref="Apply"/> runs once per component per entity, and that expression is a
        /// metadata lookup, not a field read.
        /// </summary>
        private static readonly Guid IdentityId = typeof(IdentityComponentData).GUID;

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

            if (id == IdentityId)
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
            IList<AuthoredComponentData>? unresolved = null) =>
            Materialize(entity.Components, registry, unresolved);

        /// <summary>
        /// Every authored component in a LIST, as instances — the same reading, for payloads that
        /// did not come off an entity.
        ///
        /// An authored component is not an entity's private business: the same
        /// <c>{"Id", "Data"}</c> shape is how a game's CONFIG DOCUMENT stores its tuning groups,
        /// a file with no entities in it at all. Reading one used to mean hand-rolling this loop
        /// against <see cref="IAuthoredComponentRegistry"/> — which is how it was done, and it
        /// arrived without the <see cref="AuthoredComponentData.Type"/> fallback, so a document
        /// whose ids were regenerated was unreadable where the identical payloads on an entity
        /// still loaded. One reader, one set of rules, both callers.
        ///
        /// This does not ENFORCE anything: it materializes what it can and reports the rest
        /// through <paramref name="unresolved"/>. A caller that needs a payload to be present, to
        /// be unique, or to be of a kind that document may carry, checks that itself — the router
        /// cannot know which of those a given document requires.
        /// </summary>
        /// <param name="components">The payloads, read in order.</param>
        /// <param name="registry">The game's generated registry. The engine's own is always
        /// consulted first, so a caller that passes none still gets the engine's components.</param>
        /// <param name="unresolved">Collects payloads no registry could read. Null discards
        /// them, which is what a caller that has already validated its document wants.</param>
        public static IReadOnlyList<object> Materialize(
            IEnumerable<AuthoredComponentData> components,
            IAuthoredComponentRegistry? registry = null,
            IList<AuthoredComponentData>? unresolved = null)
        {
            var instances = new List<object>();

            // Document order. It used to be "the order the contract declares the slots in", which
            // no longer exists — so the editor's order is the only order there is, and both
            // editors are explicit about emitting a stable one.
            foreach (AuthoredComponentData component in components)
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
            // This assembly's own registry first, so a caller that passed none still gets the
            // engine's components back — a host reading a scene it does not itself add components
            // to. It is the SAME generated registry a game gets, consulted the same way: there is
            // no engine tier left, only registries, and the one that knows the id wins.
            return ReadFrom(AuthoredComponents.Default, component)
                ?? (registry is null ? null : ReadFrom(registry, component));
        }

        /// <summary>One payload out of one registry: by id, else by type name, else null.
        ///
        /// The second attempt is what makes an opaque id survivable. A component whose id was
        /// regenerated, or whose document was written by a host with a stale schema, still loads —
        /// and the alternative is a payload nobody can even identify, because the only thing it
        /// says about itself is a number that matches nothing.</summary>
        private static object? ReadFrom(
            IAuthoredComponentRegistry registry, AuthoredComponentData component)
        {
            try
            {
                return ReadOrThrow(registry, component);
            }
            catch (JsonException)
            {
                // A payload that is not valid JSON for this record. Reported by the caller, which
                // knows which entity it was; throwing here would cost the whole scene.
                return null;
            }
            catch (InvalidOperationException)
            {
                // Same outcome, different messenger: the GENERATED readers parse the JsonElement
                // directly rather than through a serializer, so a field of the wrong KIND (a
                // string where a float belongs) surfaces as this rather than as a JsonException.
                // Both mean "this payload is not that component".
                return null;
            }
        }

        private static object? ReadOrThrow(
            IAuthoredComponentRegistry registry, AuthoredComponentData component)
        {
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
