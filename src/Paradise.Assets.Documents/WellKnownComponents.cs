namespace Paradise.Assets.Documents;

/// <summary>The components the authoring format itself defines; their ids are written into every document and are FIXED FOREVER.</summary>
public static class WellKnownComponents
{
    /// <summary>The parent link lives here, not on transform: a reparent changes what an object IS, so moving and re-hanging differ in a diff.</summary>
    public static readonly System.Guid MetaId = System.Guid.Parse("0f1d4b3a-8c27-4a55-9b6e-2f7c1d40a913");

    public const string MetaType = "meta";

    /// <summary>Deliberately not <c>Paradise.Export.Data.TransformComponentData</c>'s id: that is the baked world matrix, this is the authoring TRS.</summary>
    public static readonly System.Guid TransformId = System.Guid.Parse("7e55c210-3d41-4b8a-8f26-9c0a5e71b4d2");

    public const string TransformType = "transform";

    /// <summary>Unique per document; prefab-local inside a prefab.</summary>
    public const string Guid = "Guid";

    /// <summary>Not unique, not identity.</summary>
    public const string Name = "Name";

    public const string Parent = "Parent";

    /// <summary>On an override carrier only: the prefab-local guid it addresses.</summary>
    public const string Target = "Target";

    /// <summary>Spelled <c>Dropped</c>, not <c>Removed</c>, so it never differs from the component-level <c>removed</c> by case alone.</summary>
    public const string Dropped = "Dropped";

    /// <summary>The fields the resolver must NOT copy through; adding a meta field means adding it here.</summary>
    public static bool IsMetaField(string key) =>
        key is Guid or Name or Parent or Target or Dropped;

    /// <summary>Engine convention: Y-up, metres.</summary>
    public const string Position = "Position";

    /// <summary><c>[x, y, z, w]</c>.</summary>
    public const string Rotation = "Rotation";

    public const string Scale = "Scale";

    /// <summary>
    /// The first shape problem in a well-known payload, or <see langword="null"/>. Without this,
    /// <c>Position = [0.0, 1.5]</c> baked silently as the origin. <c>meta</c> is open (unknown
    /// fields ride along); <c>transform</c> is closed, because an unknown field there is a typo.
    /// </summary>
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
