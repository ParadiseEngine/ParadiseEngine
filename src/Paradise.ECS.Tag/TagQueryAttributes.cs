namespace Paradise.ECS;

/// <summary>
/// Declares that a <c>[Queryable]</c> matches only entities carrying this tag.
/// </summary>
/// <typeparam name="T">The tag type, declared with <see cref="TagAttribute"/>.</typeparam>
/// <remarks>
/// <para>
/// The tag counterpart of <c>WithAttribute&lt;T&gt;</c>, and a separate attribute rather than an
/// overload of it because tags are a separate thing: <c>With</c> is constrained to
/// <c>IComponent</c> and adds a bit to the ARCHETYPE mask, while a tag is a bit in a mask stored in
/// the <c>EntityTags</c> component and can be added or removed without moving the entity between
/// archetypes. That difference is the whole point of tags, and it is why a tag cannot be expressed
/// as a component constraint. <see cref="WithoutTagAttribute{T}"/> is the invert.
/// </para>
/// <para>
/// Declaring one has two effects on the generated queryable. <c>EntityTags</c> joins the required
/// COMPONENT mask, so archetypes whose entities cannot carry tags are rejected without inspecting a
/// single entity. And each matched row is then tested against the required tag mask, so iteration
/// yields only tagged entities — including for a <c>[Queryable(Singleton = true)]</c>, whose
/// "exactly one" is counted after the filter.
/// </para>
/// <para>
/// <b>Where it applies:</b> query iteration (<c>QueryResult</c>), a
/// <c>[Queryable(Singleton = true)]</c>'s Resolve — whose "exactly one" then means exactly one
/// TAGGED entity — ENTITY-mode system claims, and lookups (<c>TQueryable.ReadLookup</c> /
/// <c>WriteLookup</c>). A system field of any of those kinds can opt out with
/// <see cref="IgnoreTagsAttribute"/>, in which case that claim matches on archetype alone.
/// It does NOT apply to chunk-mode or world-mode claims (<c>TQueryable.Chunk</c>,
/// <c>TQueryable.Segments</c>) or to <c>ChunkQueryResult</c>: those hand out whole chunks and
/// flat spans, where entities carrying the tag and entities not carrying it sit side by side
/// and the consumer indexes rows positionally. Skipping rows there would break that indexing
/// rather than filter it. A system that claims a tag-filtered queryable as <c>Chunk</c> or
/// <c>Segments</c> is therefore a compile error (PECS3012), unless that field is marked
/// <see cref="IgnoreTagsAttribute"/> — which there acknowledges the filter cannot run.
/// </para>
/// <para>
/// <b>Cost:</b> the row test is the only filter — matching archetypes are scanned in full, and a
/// rare tag over a populous archetype pays for every entity it skips. Chunk-level skipping (each
/// chunk carries the union of its entities' tags; a clear bit is proof the whole chunk can be
/// passed over) is not wired into the query path; see ParadiseEngine#166 for why, and for when it
/// starts to matter.
/// </para>
/// <example>
/// <code>
/// [Queryable(Singleton = true)]
/// [WithTag&lt;CameraTargetTag&gt;]
/// [With&lt;Position&gt;(IsReadOnly = true)]
/// public readonly ref partial struct CameraTarget;
/// </code>
/// </example>
/// </remarks>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
public sealed class WithTagAttribute<T> : Attribute
    where T : ITag
{
}

/// <summary>
/// Declares that a <c>[Queryable]</c> matches only entities that do NOT carry this tag.
/// </summary>
/// <typeparam name="T">The tag type, declared with <see cref="TagAttribute"/>.</typeparam>
/// <remarks>
/// <para>
/// The tag counterpart of <c>WithoutAttribute&lt;T&gt;</c>, and the invert of
/// <see cref="WithTagAttribute{T}"/>. Same mechanism, opposite bit: the generated row test is
/// <c>!Mask.Get(T.TagId)</c>. Declaring both <c>[WithTag&lt;T&gt;]</c> and
/// <c>[WithoutTag&lt;T&gt;]</c> for the same T is a compile error (PECS017) — that is an empty
/// set, not a filter.
/// </para>
/// <para>
/// <b>Where it applies</b> is identical to <see cref="WithTagAttribute{T}"/>: query iteration,
/// singleton resolve, entity-mode claims, and lookups — each of which a system field can opt
/// out of with <see cref="IgnoreTagsAttribute"/>. Chunk and Segments claims of a tag-filtered
/// queryable are PECS3012 unless that field is marked <see cref="IgnoreTagsAttribute"/>.
/// </para>
/// <para>
/// <b>Chunk skip cannot help.</b> The per-chunk mask is a sticky union. A clear bit proves
/// nobody has T (every row matches — the enumerator still walks them). A set bit proves nothing
/// (a mix, or a leftover after a remove). There is no intersection mask, so
/// <c>IQueryData.ChunkMatches</c> for a without-only filter is always true.
/// </para>
/// <example>
/// <code>
/// [Queryable]
/// [WithoutTag&lt;DeadTag&gt;]
/// [With&lt;Health&gt;]
/// public readonly ref partial struct Alive;
/// </code>
/// </example>
/// </remarks>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
public sealed class WithoutTagAttribute<T> : Attribute
    where T : ITag
{
}

/// <summary>
/// Skips the queryable's <see cref="WithTagAttribute{T}"/> / <see cref="WithoutTagAttribute{T}"/>
/// filter on this system field.
/// </summary>
/// <remarks>
/// <para>
/// Those attributes are row filters. On a lookup, entity, or singleton claim they run by
/// default; this attribute turns them off for that field, so the claim matches on archetype
/// alone. On <c>Chunk</c> and <c>Segments</c> they cannot run without misaligning spans, so a
/// filtered queryable claimed that way is PECS3012 unless this attribute is present — which
/// there acknowledges the filter will not run. Test the bits yourself (claim
/// <c>EntityTags</c>, or read them off the span).
/// </para>
/// <para>
/// Valid on <c>Chunk</c>, <c>Segments</c>, <c>Entity</c>, <c>Singleton</c>,
/// <c>ReadLookup</c>, and <c>WriteLookup</c> fields (PECS3013 otherwise).
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class IgnoreTagsAttribute : Attribute
{
}
