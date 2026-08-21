#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.IO;
using System.Text.Json;
using Paradise.Authoring;
using Paradise.Export.Serialization;

namespace Paradise.Export.Data
{
    /// <summary>
    /// A file of authored components, keyed by the record type each payload turned out to be.
    ///
    /// The same <c>Components</c> array of <c>{"Id", "Data"}</c> pairs an entity carries, in a
    /// document of its own: a game's tuning, a level's settings, a difficulty table. Nothing here
    /// knows what any particular document is FOR — it reads the ids a file declares, materializes
    /// what the registries know, and hands back records the caller asks for by type.
    ///
    /// This exists because every game was writing it. Reading such a file meant hand-rolling a
    /// loop over <see cref="IAuthoredComponentRegistry"/>, and a hand-rolled loop is where the
    /// <see cref="AuthoredComponentData.Type"/> fallback goes missing — so a document whose ids
    /// were regenerated became unreadable while the identical payloads on an entity still loaded.
    /// One reader, one set of rules.
    ///
    /// WHAT IT REFUSES, AND WHY ONLY THIS MUCH. Two things, both because the document cannot
    /// represent them rather than because they are bad style: an id that is not a GUID, and two
    /// payloads for the same component. This is a map keyed by type — a second payload has nowhere
    /// to go, and silently keeping the last one is exactly the edit that looks applied and is not.
    ///
    /// Everything else is the caller's to decide, and is reported rather than thrown:
    /// <see cref="Unresolved"/> holds the payloads no registry could read. Whether that is fatal
    /// depends on the document — a game's tuning file refuses to start, a level's settings may not
    /// care — and this type cannot know which. The same reason
    /// <see cref="AuthoredComponentRouter.Materialize(IEnumerable{AuthoredComponentData},
    /// IAuthoredComponentRegistry, IList{AuthoredComponentData})"/> reports instead of enforcing.
    ///
    /// Hand-edited by design, so the parser tolerates comments and a trailing comma. These files
    /// are read by people and written by people at least as often as by an editor.
    /// </summary>
    public sealed class AuthoredDocument
    {
        /// <summary>The array every authored document carries its payloads in.</summary>
        public const string ComponentsKey = "Components";

        /// <summary>What a hand-edited file is allowed to contain beyond strict JSON.</summary>
        private static readonly JsonDocumentOptions DocumentOptions = new()
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private readonly ImmutableDictionary<Type, object> _components;

        private AuthoredDocument(
            ImmutableDictionary<Type, object> components,
            IReadOnlyList<AuthoredComponentData> unresolved)
        {
            _components = components;
            Unresolved = unresolved;
        }

        /// <summary>An empty document — every <see cref="Get{T}"/> returns record defaults.</summary>
        public static AuthoredDocument Empty { get; } =
            new(ImmutableDictionary<Type, object>.Empty, Array.Empty<AuthoredComponentData>());

        /// <summary>
        /// The payloads no registry could read, as they appeared in the file.
        ///
        /// Reported as the whole component so a caller's message can name the type as well as the
        /// id — "could not read &lt;guid&gt;" is not something anyone can act on. Empty for a
        /// document this build fully understands.
        /// </summary>
        public IReadOnlyList<AuthoredComponentData> Unresolved { get; }

        /// <summary>The component records this document declared, in document order.</summary>
        public IReadOnlyCollection<object> Components => _components.Values.ToArray();

        /// <summary>Read a document from disk. <paramref name="path"/> names it in any error.</summary>
        public static AuthoredDocument Load(
            string path, IAuthoredComponentRegistry? registry = null) =>
            Parse(File.ReadAllText(path), registry, path);

        /// <summary>
        /// Read a document from text.
        /// </summary>
        /// <param name="json">The document.</param>
        /// <param name="registry">The game's generated registry. The engine's own is always
        /// consulted first, so a caller that passes none still gets the engine's components.</param>
        /// <param name="source">What to call the document in an error message.</param>
        public static AuthoredDocument Parse(
            string json, IAuthoredComponentRegistry? registry = null, string source = "document")
        {
            using JsonDocument document = JsonDocument.Parse(json, DocumentOptions);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"'{source}': the document must be a JSON object.");
            }

            if (!root.TryGetProperty(ComponentsKey, out JsonElement components))
            {
                // A document may legitimately declare nothing and lean on record defaults.
                return Empty;
            }

            if (components.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    $"'{source}': {ComponentsKey} must be an array.");
            }

            return Read(components, registry, source);
        }

        private static AuthoredDocument Read(
            JsonElement components, IAuthoredComponentRegistry? registry, string source)
        {
            var payloads = new List<AuthoredComponentData>();
            var seen = new HashSet<Guid>();

            foreach (JsonElement entry in components.EnumerateArray())
            {
                AuthoredComponentData payload = ReadPayload(entry, source);
                if (!seen.Add(payload.Id))
                {
                    throw new InvalidDataException(
                        $"'{source}': component '{payload.Id}' is declared twice. A document holds "
                        + "one payload per component, so the second has nowhere to go.");
                }
                payloads.Add(payload);
            }

            var unresolved = new List<AuthoredComponentData>();
            IReadOnlyList<object> instances =
                AuthoredComponentRouter.Materialize(payloads, registry, unresolved);

            ImmutableDictionary<Type, object>.Builder builder =
                ImmutableDictionary.CreateBuilder<Type, object>();
            foreach (object instance in instances)
            {
                builder[instance.GetType()] = instance;
            }

            return new AuthoredDocument(builder.ToImmutable(), unresolved);
        }

        private static AuthoredComponentData ReadPayload(JsonElement entry, string source)
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"'{source}': every entry in {ComponentsKey} must be an object.");
            }

            // The id is checked HERE rather than left to the deserializer, which would throw a
            // JsonException naming a token position. A document written before the ids became
            // GUIDs is the case that actually happens, and it deserves to be told what it is.
            if (!entry.TryGetProperty("Id", out JsonElement idElement)
                || idElement.ValueKind != JsonValueKind.String
                || idElement.GetString() is not { Length: > 0 } id)
            {
                throw new InvalidDataException(
                    $"'{source}': every component needs a non-empty string Id.");
            }

            if (!Guid.TryParse(id, out Guid componentId))
            {
                throw new InvalidDataException(
                    $"'{source}': component Id '{id}' is not a GUID. A component is identified by "
                    + "the [Guid] on its record.");
            }

            return new AuthoredComponentData
            {
                Id = componentId,
                Type = entry.TryGetProperty("Type", out JsonElement type)
                    && type.ValueKind == JsonValueKind.String
                        ? type.GetString()
                        : null,
                // CLONED, and it has to be: the JsonDocument these elements belong to is disposed
                // when parsing ends, and anything reaching Unresolved would otherwise carry a
                // Data that throws the moment a caller reads it.
                Data = entry.TryGetProperty("Data", out JsonElement data)
                    ? data.Clone()
                    : default,
            };
        }

        /// <summary>
        /// This document's <typeparamref name="T"/>, or its record defaults when none was declared.
        ///
        /// Defaulting rather than throwing matches what the reader does with an absent MEMBER: a
        /// record keeps its own initializer. A document that omits a whole component is the same
        /// statement one notch up, and a caller that needs it present says so itself — see
        /// <see cref="Has{T}"/>.
        /// </summary>
        public T Get<T>() where T : new() =>
            _components.TryGetValue(typeof(T), out object? component)
                ? (T)component
                : Default<T>.Instance;

        /// <summary>Whether the document actually declared a <typeparamref name="T"/>, as opposed
        /// to <see cref="Get{T}"/> being about to hand back defaults.</summary>
        public bool Has<T>() => _components.ContainsKey(typeof(T));

        /// <summary>
        /// This document with <paramref name="component"/> in place of whatever it held for that
        /// type.
        ///
        /// Keyed on the RUNTIME type, so a component the caller holds only as <c>object</c> lands
        /// under the same key <see cref="Get{T}"/> looks up.
        /// </summary>
        public AuthoredDocument With(object component)
        {
            ArgumentNullException.ThrowIfNull(component);
            return new AuthoredDocument(
                _components.SetItem(component.GetType(), component), Unresolved);
        }

        /// <summary>One shared defaults instance per component type.
        ///
        /// A fresh <c>new T()</c> per miss would allocate on every read of a component the
        /// document omits, and these are read from systems that run per frame. Safe to share
        /// because an authored component is replaced wholesale, never mutated in place.</summary>
        private static class Default<T> where T : new()
        {
            public static readonly T Instance = new();
        }
    }
}
