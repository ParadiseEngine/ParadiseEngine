namespace Paradise.Assets.Documents;

/// <summary>
/// The components the authoring format itself defines, as opposed to the ones a game declares.
/// </summary>
/// <remarks>
/// <para>
/// An object has no privileged members: its identity, its name, its place in the tree and its
/// placement are all components, addressed the same way a game's components are. That is what
/// lets a prefab instance override any of them through one mechanism — a component the instance
/// repeats is overridden — instead of needing a second, special syntax for the four fields that
/// used to be spelled at the object level.
/// </para>
/// <para>
/// These ids are FIXED FOREVER. They are written into every document, and changing one orphans
/// every object in every scene.
/// </para>
/// </remarks>
public static class WellKnownComponents
{
    /// <summary>
    /// <c>meta</c> — identity, display name, the parent link, and the prefab-override addressing.
    /// </summary>
    /// <remarks>
    /// Structure lives here rather than on <see cref="TransformId"/> because a reparent changes
    /// what an object IS in the tree, while a transform is only numbers. Keeping them apart means
    /// moving an object and re-hanging it are different edits in a diff.
    /// </remarks>
    public static readonly System.Guid MetaId = System.Guid.Parse("0f1d4b3a-8c27-4a55-9b6e-2f7c1d40a913");

    /// <summary>The readable name of <see cref="MetaId"/>.</summary>
    public const string MetaType = "meta";

    /// <summary>
    /// <c>transform</c> — the object's LOCAL position, rotation and scale.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT <c>Paradise.Export.Data.TransformComponentData</c>'s id. That component
    /// carries a single <c>World</c> matrix — the baked form — while this is the authoring form
    /// the bake flattens against the parent chain. Two different field sets under one id is the
    /// collision that makes ShiningPie's two <c>GameConfig</c> declarations unmergeable, and it
    /// costs nothing to avoid here.
    /// </remarks>
    public static readonly System.Guid TransformId = System.Guid.Parse("7e55c210-3d41-4b8a-8f26-9c0a5e71b4d2");

    /// <summary>The readable name of <see cref="TransformId"/>.</summary>
    public const string TransformType = "transform";

    // ---- meta fields ---------------------------------------------------------------------

    /// <summary>The object's identity. Unique per document; prefab-local inside a prefab.</summary>
    public const string Guid = "Guid";

    /// <summary>Display name. Diagnostics and readability; not unique, not identity.</summary>
    public const string Name = "Name";

    /// <summary>The parent object's <see cref="Guid"/>, or absent for a root.</summary>
    public const string Parent = "Parent";

    /// <summary>
    /// On an object that overrides a prefab CHILD: the prefab-local guid it addresses. Absent
    /// on every ordinary object.
    /// </summary>
    public const string Target = "Target";

    /// <summary>
    /// On a <see cref="Target"/> carrier: whether that prefab child is dropped, along with its
    /// descendants.
    /// </summary>
    /// <remarks>
    /// Spelled <c>Dropped</c>, not <c>Removed</c>, so it does not differ from the component-level
    /// <c>removed</c> marker by case alone. Two spellings of one word meaning different things at
    /// different levels is a diff nobody can read correctly at a glance.
    /// </remarks>
    public const string Dropped = "Dropped";

    /// <summary>
    /// Whether <paramref name="key"/> is a <c>meta</c> field the format itself defines, as
    /// opposed to a game-extended payload field riding along in the same table.
    /// </summary>
    /// <remarks>
    /// The resolver rebuilds every format-owned field itself — identity is minted, the parent is
    /// remapped, and the carrier-only fields describe the override rather than the object — so
    /// this is the set it must NOT copy through. Adding a meta field means adding it here, and
    /// the copy-through loop needs no edit.
    /// </remarks>
    public static bool IsMetaField(string key) =>
        key is Guid or Name or Parent or Target or Dropped;

    // ---- transform fields ----------------------------------------------------------------

    /// <summary>Local translation, engine convention (Y-up, metres).</summary>
    public const string Position = "Position";

    /// <summary>Local rotation as <c>[x, y, z, w]</c>.</summary>
    public const string Rotation = "Rotation";

    /// <summary>Local scale.</summary>
    public const string Scale = "Scale";

    // ---- shape ---------------------------------------------------------------------------

    /// <summary>
    /// The first shape problem in a well-known component's payload, phrased to follow a source
    /// name — or <see langword="null"/> when there is none, including for a component that is
    /// not well-known at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A game component's payload is deliberately opaque here — its shape is a schema question
    /// the game answers. These two are the components whose schema the FORMAT owns, so the
    /// format checks them; without this, <c>Position = [0.0, 1.5]</c> baked silently as the
    /// origin, which is data loss dressed as a default.
    /// </para>
    /// <para>
    /// <c>meta</c> stays OPEN — the resolver carries unknown meta fields through — so only the
    /// fields it defines are checked. <c>transform</c> is CLOSED: nothing reads an unknown field
    /// on it and the bake replaces it wholesale, so an unknown name there is a typo, not an
    /// extension.
    /// </para>
    /// </remarks>
    public static string? PayloadProblem(PrefabComponent component)
    {
        if (component.Id == MetaId) return MetaProblem(component.Data);
        if (component.Id == TransformId) return TransformProblem(component.Data);
        return null;
    }

    private static string? MetaProblem(CanonicalTomlTable data)
    {
        foreach (var (key, value) in data)
        {
            switch (key)
            {
                case Guid or Parent or Target when !IsGuidText(value):
                    return $"needs '{MetaType}.{key}' to be a UUID string";
                case Name when value is not string:
                    return $"needs '{MetaType}.{Name}' to be a string";
                case Dropped when value is not bool:
                    return $"needs '{MetaType}.{Dropped}' to be a boolean";
            }
        }

        if (data.ContainsKey(Dropped) && !data.ContainsKey(Target))
        {
            return $"marks '{MetaType}.{Dropped}' without a '{Target}' — only an override carrier can drop a prefab child";
        }

        return null;
    }

    private static string? TransformProblem(CanonicalTomlTable data)
    {
        foreach (var (key, value) in data)
        {
            switch (key)
            {
                case Position or Scale when !IsNumberArray(value, 3):
                    return $"needs '{TransformType}.{key}' to be an array of 3 numbers";
                case Rotation when !IsNumberArray(value, 4):
                    return $"needs '{TransformType}.{Rotation}' to be an array of 4 numbers";
                case not (Position or Rotation or Scale):
                    return $"holds '{key}', which '{TransformType}' does not define — a misspelled field would otherwise bake as the identity, silently";
            }
        }

        return null;
    }

    private static bool IsGuidText(object value)
        => value is string text && DocumentGuid.TryParse(text, out var guid) && guid != System.Guid.Empty;

    private static bool IsNumberArray(object value, int length)
    {
        if (value is not IReadOnlyList<object> items || items.Count != length) return false;
        foreach (var item in items)
        {
            if (item is not (long or double)) return false;
        }

        return true;
    }
}
