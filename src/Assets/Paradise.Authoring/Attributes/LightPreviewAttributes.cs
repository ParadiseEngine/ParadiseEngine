namespace Paradise.Authoring;

/// <summary>Projects an authored light archetype into an editor's viewport.</summary>
/// <remarks>The preview is derived from components; native editor light settings never replace
/// the authored values. Fields on the same object declare their meaning with
/// <see cref="AuthorLightFieldAttribute"/>.</remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class AuthorLightPreviewAttribute(HostLightType type) : Attribute
{
    public HostLightType Type { get; } = type;
}

/// <summary>Identifies the light-preview value supplied by an authored property.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class AuthorLightFieldAttribute(LightPreviewField field) : Attribute
{
    public LightPreviewField Field { get; } = field;
}

/// <summary>Editor-neutral meanings of fields contributing to a light preview.</summary>
public enum LightPreviewField
{
    Color,
    Intensity,
    Range,
    Size,
    OuterDegrees,
    InnerDegrees,
    Shadows,
    Direction,
    ShadowStrength,
    AttenuationExponent,
    Specular,
}
