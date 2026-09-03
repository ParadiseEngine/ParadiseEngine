#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.IO;
using System.Text.Json;
using Paradise.Authoring;
using Paradise.Export.Serialization;
using Zio;

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
            IReadOnlyList<object> ordered,
            IReadOnlyList<AuthoredComponentData> unresolved)
        {
            _components = components;
            Components = ordered;
            Unresolved = unresolved;
        }

        /// <summary>An empty document — every <see cref="Get{T}"/> returns record defaults.</summary>
        public static AuthoredDocument Empty { get; } = new(
            ImmutableDictionary<Type, object>.Empty,
            Array.Empty<object>(),
            Array.Empty<AuthoredComponentData>());

        /// <summary>
        /// The payloads no registry could read, as they appeared in the file.
        ///
        /// Reported as the whole component so a caller's message can name the type as well as the
        /// id — "could not read &lt;guid&gt;" is not something anyone can act on. Empty for a
        /// document this build fully understands.
        /// </summary>
        public IReadOnlyList<AuthoredComponentData> Unresolved { get; }

        /// <summary>
        /// The component records this document declared, in DOCUMENT order.
        ///
        /// Held as its own list rather than projected from the map: a map keyed by type
        /// enumerates in hash order, so reading it back would report an order the file never had
        /// — and would allocate a fresh array on every access besides.
        /// </summary>
        public IReadOnlyList<object> Components { get; }

        /// <summary>Read a document out of <paramref name="fileSystem"/>. <paramref name="path"/>
        /// names it in any error.</summary>
        /// <remarks>
        /// <para>
        /// Dispatches on the EXTENSION, because a build writes its documents in whichever form its
        /// profile names and the file is the thing that knows which. A TOML document is bridged to
        /// the contract's JSON text and read by the same parser below — one reader for both forms,
        /// rather than a second traversal growing beside the first and drifting from it.
        /// </para>
        /// <para>
        /// A filesystem rather than a host path, because what a runtime reads a document out of is
        /// not always a directory: a shipped build mounts its content root, a test mounts memory,
        /// and neither should have to materialize a temp directory to be read. It also removes the
        /// separator dance — a <see cref="UPath"/> is '/'-separated on every platform, which is
        /// exactly how the contract spells a field.
        /// </para>
        /// </remarks>
        public static AuthoredDocument Load(
            IFileSystem fileSystem, UPath path, IAuthoredComponentRegistry? registry = null)
        {
            ArgumentNullException.ThrowIfNull(fileSystem);

            string text = fileSystem.ReadAllText(path);
            if (".toml".Equals(path.GetExtensionWithDot(), StringComparison.OrdinalIgnoreCase))
            {
                text = Serialization.ExportTomlReader.ToJsonText(text);
            }

            return Parse(text, registry, path.FullName);
        }

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

            // Resolved instances line up with the payloads that produced them, minus the
            // unresolved ones — Materialize preserves order and appends misses to `unresolved`
            // in that same order, so walking both together names the id behind each instance.
            var idOf = new Dictionary<Type, Guid>();
            ImmutableDictionary<Type, object>.Builder builder =
                ImmutableDictionary.CreateBuilder<Type, object>();

            // Walked with two cursors, matched by REFERENCE. Materialize keeps document order and
            // appends each miss to `unresolved` in that same order, so stepping the two together
            // names the id behind every instance. Reference equality rather than the record's
            // own: AuthoredComponentData carries a JsonElement, and comparing those structurally
            // is both unreliable and needlessly expensive when the objects are literally the ones
            // we handed in.
            int miss = 0;
            int resolved = 0;
            foreach (AuthoredComponentData payload in payloads)
            {
                if (miss < unresolved.Count && ReferenceEquals(unresolved[miss], payload))
                {
                    miss++;
                    continue;
                }

                object instance = instances[resolved++];
                Guid id = payload.Id;
                Type type = instance.GetType();

                // The guard above catches a repeated ID; this catches a repeated RECORD, which is
                // not the same thing. Two distinct ids reach one type whenever the second resolves
                // through the Type-name fallback — the stale-guid case this reader exists to
                // support — and the map is keyed by type, so the second would quietly replace the
                // first. That is the failure refusing duplicates is FOR, so it is refused here too.
                if (idOf.TryGetValue(type, out Guid first))
                {
                    throw new InvalidDataException(
                        $"'{source}': components '{first}' and '{id}' both resolve to "
                        + $"{type.Name}. A document holds one payload per component, so the "
                        + "second has nowhere to go — delete one.");
                }

                idOf[type] = id;
                builder[type] = instance;
            }

            return new AuthoredDocument(builder.ToImmutable(), instances, unresolved);
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
                //
                // An ABSENT Data reads as an empty object, not as a malformed payload: it is the
                // same statement as an absent member keeping its initializer, one level up, so a
                // component with no fields worth writing is just its id. Left as
                // default(JsonElement) it would instead fail to deserialize and land in
                // Unresolved — neither read nor reported as wrong, which is the worst of both.
                Data = ReadData(entry, id, source),
            };
        }

        private static JsonElement ReadData(JsonElement entry, string id, string source)
        {
            if (!entry.TryGetProperty("Data", out JsonElement data))
            {
                return EmptyObject;
            }
            if (data.ValueKind != JsonValueKind.Object)
            {
                // PRESENT and wrong is a different thing from absent, and worth saying so: an
                // author who wrote a Data meant something by it.
                throw new InvalidDataException(
                    $"'{source}': component '{id}' has a Data that is not an object.");
            }
            return data.Clone();
        }

        /// <summary>Stands in for an omitted Data. Parsed once; JsonElement is a struct over a
        /// document, so it needs one to point at.</summary>
        private static readonly JsonElement EmptyObject =
            JsonDocument.Parse("{}").RootElement.Clone();

        /// <summary>
        /// This document's <typeparamref name="T"/>, or its record defaults when none was declared.
        ///
        /// Defaulting rather than throwing matches what the reader does with an absent MEMBER: a
        /// record keeps its own initializer. A document that omits a whole component is the same
        /// statement one notch up, and a caller that needs it present says so itself — see
        /// <see cref="Has{T}"/>.
        /// </summary>
        public T Get<T>() where T : new() =>
            _components.TryGetValue(typeof(T), out object? component) ? (T)component : new T();

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
            Type type = component.GetType();

            // Order is maintained rather than recomputed: a replacement keeps the position the
            // file gave it, and something new goes on the end. Rebuilding from the map would
            // shuffle every other component as a side effect of touching one.
            var ordered = new List<object>(Components.Count + 1);
            bool replaced = false;
            foreach (object existing in Components)
            {
                if (existing.GetType() == type)
                {
                    ordered.Add(component);
                    replaced = true;
                }
                else
                {
                    ordered.Add(existing);
                }
            }
            if (!replaced)
            {
                ordered.Add(component);
            }

            return new AuthoredDocument(
                _components.SetItem(type, component), ordered, Unresolved);
        }

    }
}
