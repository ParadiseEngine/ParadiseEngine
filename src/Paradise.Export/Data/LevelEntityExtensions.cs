#nullable enable
using System;
using System.Collections.Generic;
using Paradise.Export.Serialization;

namespace Paradise.Export.Data
{
    /// <summary>
    /// Reading one authored component off an entity.
    ///
    /// This is what replaced the typed slots. <c>entity.Components.Renderable?.Mesh</c> is now
    /// <c>entity.Get&lt;RenderableComponentData&gt;()?.Mesh</c>, and the interesting part is that
    /// the call site names no id: the key is <c>typeof(T).GUID</c>, the same
    /// <see cref="GuidAttribute"/> the record already carries for
    /// <c>[Authored]</c>. One component, one identity, and no constant to keep in step with it.
    ///
    /// One sharp edge, because it fails silently rather than loudly: a type with NO
    /// <see cref="GuidAttribute"/> still has a <c>GUID</c> — the runtime derives a stable one from
    /// the type name and assembly, not <see cref="Guid.Empty"/> — so
    /// <c>Get&lt;SomethingUntagged&gt;()</c> compiles, looks up an id nothing ever wrote, and
    /// returns null forever. Every authored record carries the attribute (a missing one is
    /// compile error PAUT005), so this only bites a caller reaching for a type that was never an
    /// authored component.
    /// </summary>
    public static class LevelEntityExtensions
    {
        /// <summary>
        /// The entity's <typeparamref name="T"/>, or null when it authors none.
        ///
        /// For the ENGINE's components. A game's records are not in this assembly's serializer
        /// context, so reading one here would throw rather than return null — use
        /// <see cref="AuthoredComponentRouter.Materialize"/> with the game's generated registry,
        /// which is what a game host wants anyway (it gets every component in one pass instead of
        /// one lookup per type).
        ///
        /// Deserializes on each call. That is deliberate rather than cached: every consumer of
        /// this in the workspace reads at LOAD time — building a scene, binding a level — and a
        /// cache keyed on an entity that callers are free to mutate is a correctness problem in
        /// exchange for nothing. A caller in a hot path should hold the result, or use
        /// <c>Materialize</c> once.
        /// </summary>
        public static T? Get<T>(this LevelEntityData entity) where T : class
        {
            ArgumentNullException.ThrowIfNull(entity);

            Guid id = typeof(T).GUID;
            foreach (AuthoredComponentData component in entity.Components)
            {
                if (component.Id == id)
                {
                    return ExportJsonReader.ReadElement<T>(component.Data);
                }
            }
            return null;
        }

        /// <summary>
        /// <typeparamref name="T"/> as an entry, ready to put in
        /// <see cref="LevelEntityData.Components"/>.
        ///
        /// The write half of <see cref="Get{T}"/>, and the only place the id and the type name are
        /// derived — an editor or a test that builds the triple by hand is one rename away from a
        /// payload nothing can resolve.
        /// </summary>
        public static AuthoredComponentData Entry<T>(T value) where T : class
        {
            ArgumentNullException.ThrowIfNull(value);

            return new AuthoredComponentData
            {
                Id = typeof(T).GUID,
                Type = typeof(T).FullName,
                Data = ExportJsonWriter.SerializeToElement(value),
            };
        }

        /// <summary>Author <typeparamref name="T"/> on the entity, replacing an existing entry
        /// rather than adding a second one.</summary>
        public static void Set<T>(this LevelEntityData entity, T value) where T : class
        {
            ArgumentNullException.ThrowIfNull(entity);

            AuthoredComponentData entry = Entry(value);
            for (int index = 0; index < entity.Components.Count; index++)
            {
                if (entity.Components[index].Id == entry.Id)
                {
                    entity.Components[index] = entry;
                    return;
                }
            }
            entity.Components.Add(entry);
        }

        /// <summary>Whether the entity authors <typeparamref name="T"/> at all, without paying to
        /// deserialize it — for a caller that only needs to know the component is there.</summary>
        public static bool Has<T>(this LevelEntityData entity) where T : class
        {
            ArgumentNullException.ThrowIfNull(entity);

            Guid id = typeof(T).GUID;
            foreach (AuthoredComponentData component in entity.Components)
            {
                if (component.Id == id)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Every entry for <typeparamref name="T"/>.
        ///
        /// The list does not enforce at-most-one the way a named slot did by construction, so a
        /// document CAN carry two of the same component. Callers that care should say so; this is
        /// how they find out rather than silently seeing the first.</summary>
        public static IReadOnlyList<AuthoredComponentData> Entries<T>(this LevelEntityData entity)
            where T : class
        {
            ArgumentNullException.ThrowIfNull(entity);

            Guid id = typeof(T).GUID;
            var found = new List<AuthoredComponentData>();
            foreach (AuthoredComponentData component in entity.Components)
            {
                if (component.Id == id)
                {
                    found.Add(component);
                }
            }
            return found;
        }
    }
}
