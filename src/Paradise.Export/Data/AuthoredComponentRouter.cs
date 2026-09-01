#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using Paradise.Authoring;
using Paradise.Export.Serialization;

namespace Paradise.Export.Data
{
    /// <summary>
    /// Reads authored payloads back into the records they were written from.
    ///
    /// <b>There is one destination now, and it is "the list".</b> This class used to have two: a
    /// payload was either an <c>Identity</c> component — spread onto the entity's own
    /// fields, because identity was what an entity WAS rather than something it had — or it was
    /// appended to the entity's component list. Schema v5 removed the entity record entirely, so
    /// there is nothing left to spread onto and nothing left to route: an object IS its
    /// components. What remains is the READING, which is the part callers actually wanted.
    ///
    /// Before that there was a third destination: nine typed slots the engine's own components
    /// were unpacked into. That tier bought typed access at the cost of a GUID-to-slot mapping
    /// duplicated in this file, in the Godot editor, and again in the Blender addon's Python
    /// mirror — so an engine component could not be added without editing all three.
    ///
    /// Reflection-free throughout: the dispatch selects a source-generated
    /// <c>JsonTypeInfo&lt;T&gt;</c>, because a reflection deserializer would pin Godot's
    /// collectible AssemblyLoadContext and break C# hot-reload (godotengine/godot#78513).
    /// </summary>
    public static class AuthoredComponentRouter
    {
        /// <summary>
        /// Every authored component in a list, as INSTANCES.
        ///
        /// The point is that the caller gets one list of records rather than raw JSON it has to
        /// remember to deserialize: a component nobody wrote an accessor for is otherwise
        /// authored, exported, and silently never read.
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
        /// <param name="registry">The game's generated registry — the only lookup there is,
        /// since v6 the engine declares no authored components of its own. Null routes every
        /// payload to <paramref name="unresolved"/>.</param>
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
            // The CALLER'S registry is the only lookup. The engine declares no authored
            // components (v6), so there is no engine tier to consult first — a caller that
            // passes none gets every payload back through `unresolved`, which is the honest
            // answer for a reader with no declarations of its own.
            return registry is null ? null : ReadFrom(registry, component);
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
    }
}
