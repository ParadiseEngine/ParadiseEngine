namespace Paradise.Assets.Project;

/// <summary>
/// One named set of build output choices, declared as <c>[build.profiles.&lt;name&gt;]</c> in the
/// project manifest.
/// </summary>
/// <remarks>
/// Profile names are free-form. The pipeline gives <c>dev</c>, <c>debug</c> and <c>release</c>
/// conventional meanings, but the manifest does not enumerate them: a game with a
/// <c>demo</c> profile should not have to patch this package.
/// </remarks>
public sealed class BuildProfile
{
    /// <summary>
    /// Creates a profile.
    /// </summary>
    /// <param name="documentFormat">How authored documents are serialized into the output tree.</param>
    /// <param name="textureQuality">How much time the texture step may spend per image.</param>
    /// <param name="pack">Whether the output tree is packed into an archive.</param>
    public BuildProfile(DocumentFormat documentFormat, TextureQuality textureQuality, bool pack)
    {
        DocumentFormat = documentFormat;
        TextureQuality = textureQuality;
        Pack = pack;
    }

    /// <summary>
    /// The profile an empty <c>[build.profiles.x]</c> table means: hand-inspectable TOML, full
    /// texture quality, unpacked. Defaults favour correctness and legibility over speed, because
    /// a profile that omits every key is most likely one someone is still setting up.
    /// </summary>
    public static BuildProfile Default { get; } = new(DocumentFormat.Toml, TextureQuality.Full, pack: false);

    /// <summary>How authored config and scene documents are written into the output tree.</summary>
    public DocumentFormat DocumentFormat { get; }

    /// <summary>How much time the texture step may spend per image.</summary>
    public TextureQuality TextureQuality { get; }

    /// <summary>Whether the output tree is packed into a single archive.</summary>
    public bool Pack { get; }
}

/// <summary>
/// The serialized form of authored documents in a build output tree.
/// </summary>
/// <remarks>
/// This is a per-profile choice rather than a global one because the trade-off genuinely differs
/// per build: a release profile choosing <see cref="Json"/> or <see cref="Blob"/> links no TOML
/// reader at all, while a debug profile keeping <see cref="Toml"/> ships configs a field
/// engineer can edit.
/// </remarks>
public enum DocumentFormat
{
    /// <summary>Canonical TOML — diffable against <c>assets/</c>, and editable in the field.</summary>
    Toml,

    /// <summary>JSON, matching the existing export contract.</summary>
    Json,

    /// <summary>Binary blob. Reserved: no writer exists yet.</summary>
    Blob,
}

/// <summary>
/// How much time the texture step may spend encoding one image.
/// </summary>
public enum TextureQuality
{
    /// <summary>Fastest encode that produces a usable image. For iteration, not for shipping.</summary>
    Fast,

    /// <summary>Full-quality encode.</summary>
    Full,
}
