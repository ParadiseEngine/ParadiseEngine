namespace Paradise.Assets.Project;

/// <summary>One <c>[build.profiles.&lt;name&gt;]</c>; names are free-form so a game with a <c>demo</c> profile need not patch this package.</summary>
public sealed class BuildProfile
{
    public BuildProfile(DocumentFormat documentFormat, TextureQuality textureQuality, bool pack)
    {
        DocumentFormat = documentFormat;
        TextureQuality = textureQuality;
        Pack = pack;
    }

    /// <summary>Defaults favour legibility over speed, because a profile that omits every key is one someone is still setting up.</summary>
    public static BuildProfile Default { get; } = new(DocumentFormat.Toml, TextureQuality.Full, pack: false);

    public DocumentFormat DocumentFormat { get; }

    public TextureQuality TextureQuality { get; }

    /// <summary>Reserved: no packer exists yet (issue #198).</summary>
    public bool Pack { get; }
}

/// <summary>Per profile, not global: a release profile can link no TOML reader while a debug one ships editable configs.</summary>
public enum DocumentFormat
{
    Toml,

    Json,

    /// <summary>Reserved: no writer exists yet (issue #198).</summary>
    Blob,
}

public enum TextureQuality
{
    /// <summary>For iteration, never for shipping.</summary>
    Fast,

    Full,
}
