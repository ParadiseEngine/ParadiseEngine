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
/// as a component constraint.
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
/// TAGGED entity — and ENTITY-mode system claims. It does NOT apply to chunk-mode or world-mode
/// claims (<c>TQueryable.Chunk</c>, <c>TQueryable.Segments</c>) or to <c>ChunkQueryResult</c>: those
/// hand out whole chunks and flat spans, where entities carrying the tag and entities not carrying
/// it sit side by side and the consumer indexes rows positionally. Skipping rows there would break
/// that indexing rather than filter it, so a batching consumer must test rows itself.
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
