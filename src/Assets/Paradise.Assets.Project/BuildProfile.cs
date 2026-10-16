namespace Paradise.Assets.Project;

/// <summary>One <c>[build.profiles.&lt;name&gt;]</c>; names are free-form so a game with a <c>demo</c> profile need not patch this package.</summary>
public sealed class BuildProfile
{
    public BuildProfile(DocumentFormat documentFormat, TextureQuality textureQuality, bool pack, IReadOnlyList<string>? packRoots = null)
    {
        DocumentFormat = documentFormat;
        TextureQuality = textureQuality;
        Pack = pack;
        HasPackRoots = packRoots is not null;
        PackRoots = packRoots is null ? [] : Array.AsReadOnly(packRoots.ToArray());
    }

    /// <summary>Defaults favour legibility over speed, because a profile that omits every key is one someone is still setting up.</summary>
    public static BuildProfile Default { get; } = new(DocumentFormat.Toml, TextureQuality.Full, pack: false);

    public DocumentFormat DocumentFormat { get; }

    public TextureQuality TextureQuality { get; }

    /// <summary>Exports an independently compressed <c>assets.pak</c> after cooking the loose build tree.</summary>
    public bool Pack { get; }

    /// <summary>Whether packing uses explicit roots; an omitted list preserves the full-build selection.</summary>
    public bool HasPackRoots { get; }

    /// <summary>Assets-relative source paths or GUIDs whose cooked outputs and runtime dependencies ship.</summary>
    /// <remarks>An explicitly empty list ships only the pack manifest; additional roots cover assets loaded dynamically by game code.</remarks>
    public IReadOnlyList<string> PackRoots { get; }
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
