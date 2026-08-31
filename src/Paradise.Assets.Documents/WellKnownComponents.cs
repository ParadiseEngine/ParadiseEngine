namespace Paradise.Assets.Documents;

/// <summary>
/// The components the authoring format itself defines, as opposed to the ones a game declares.
/// </summary>
/// <remarks>
/// An object has no privileged members — identity, name, parent and placement are components
/// like a game's own, which is what lets a prefab instance override any of them through the one
/// override mechanism. These ids are FIXED FOREVER: they are written into every document, and
/// changing one orphans every object in every scene.
/// </remarks>
public static class WellKnownComponents
{
    /// <summary>
    /// <c>meta</c> — identity, display name, the parent link, and the prefab-override addressing.
    /// </summary>
    /// <remarks>
    /// The parent link lives here, not on <see cref="TransformId"/>: a reparent changes what an
    /// object IS, a transform is only numbers — so moving and re-hanging differ in a diff.
    /// </remarks>
    public static readonly System.Guid MetaId = System.Guid.Parse("0f1d4b3a-8c27-4a55-9b6e-2f7c1d40a913");

    /// <summary>The readable name of <see cref="MetaId"/>.</summary>
    public const string MetaType = "meta";

    /// <summary>
    /// <c>transform</c> — the object's LOCAL position, rotation and scale.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT <c>Paradise.Export.Data.TransformComponentData</c>'s id: that carries the
    /// baked <c>World</c> matrix, this is the authoring TRS the bake flattens — two field sets
    /// under one id is a collision that costs nothing to avoid.
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
    /// Spelled <c>Dropped</c>, not <c>Removed</c>, so it never differs from the component-level
    /// <c>removed</c> marker by case alone.
    /// </remarks>
    public const string Dropped = "Dropped";

    /// <summary>
    /// Whether <paramref name="key"/> is a <c>meta</c> field the format itself defines, as
    /// opposed to a game-extended payload field riding along in the same table.
    /// </summary>
    /// <remarks>
    /// The set the resolver must NOT copy through — it rebuilds every format-owned field itself.
    /// Adding a meta field means adding it here; the copy-through loop needs no edit.
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
    /// These two are the components whose schema the FORMAT owns (game payloads stay opaque);
    /// without this check <c>Position = [0.0, 1.5]</c> baked silently as the origin. <c>meta</c>
    /// is OPEN — unknown fields ride along — while <c>transform</c> is CLOSED, because nothing
    /// reads an unknown field there: it is a typo, not an extension.
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
