using Tomlyn;

using Zio;

namespace Paradise.Assets.Project;

/// <summary>A validated <c>assets/project.toml</c>.</summary>
/// <remarks>
/// Unknown keys and values are refused rather than defaulted, because a <c>document_format</c>
/// typo that quietly built TOML into a release tree is found on ship day. Unknown profile names
/// are the game's to invent. <c>blob</c> and <c>pack</c> are refused until a writer exists: a
/// strict loader that accepts a value nothing implements only moves the failure to the build.
/// </remarks>
public sealed class ProjectManifest
{
    public const int SupportedSchemaVersion = 1;

    private readonly Dictionary<string, BuildProfile> _profiles;

    private ProjectManifest(string name, int schemaVersion, AssetIgnoreRules ignore, Dictionary<string, BuildProfile> profiles, ExtractSettings extract, HostSettings host)
    {
        Extract = extract;
        Host = host;
        Name = name;
        SchemaVersion = schemaVersion;
        Ignore = ignore;
        _profiles = profiles;
    }

    public string Name { get; }

    public int SchemaVersion { get; }

    /// <summary><c>[assets] ignore</c>: files under <c>assets/</c> no verb looks at.</summary>
    public AssetIgnoreRules Ignore { get; }

    /// <summary>Where <c>paradise assets extract</c> puts what it extracts, and which components it wires a mesh into; every member optional.</summary>
    public ExtractSettings Extract { get; }

    /// <summary>The game's launcher for <c>paradise host</c>; <see cref="HostSettings.None"/> when the project declares none.</summary>
    public HostSettings Host { get; }

    /// <summary>Case-sensitive, as TOML keys are.</summary>
    public IReadOnlyDictionary<string, BuildProfile> Profiles => _profiles;

    public bool TryGetProfile(string name, out BuildProfile? profile) => _profiles.TryGetValue(name, out profile);

    /// <exception cref="ProjectManifestException">The file is not valid TOML, or the document is not a valid manifest.</exception>
    public static ProjectManifest Load(IFileSystem fileSystem, UPath path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        path.AssertNotNull(nameof(path));

        string text;
        try
        {
            text = fileSystem.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ProjectManifestException(path.FullName, $"could not be read ({error.Message})", error);
        }

        return Parse(text, path.FullName);
    }

    /// <summary>The filesystem-free half of <see cref="Load"/>.</summary>
    public static ProjectManifest Parse(string toml, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(toml);
        ArgumentNullException.ThrowIfNull(sourceName);

        ProjectManifestDocument? document;
        try
        {
            document = TomlSerializer.Deserialize<ProjectManifestDocument>(
                toml,
                ProjectManifestSerializerContext.Default);
        }
        catch (TomlException error)
        {
            throw new ProjectManifestException(sourceName, $"is not valid TOML ({error.Message})", error);
        }

        if (document is null) throw new ProjectManifestException(sourceName, "is empty");
        RejectUnknown(sourceName, document.Unknown, "at the document root");
        RejectUnknown(sourceName, document.Assets?.Unknown, "in [assets]");
        RejectUnknown(sourceName, document.Build?.Unknown, "in [build]");
        RejectUnknown(sourceName, document.Extract?.Unknown, "in [extract]");
        RejectUnknown(sourceName, document.Host?.Unknown, "in [host]");

        if (string.IsNullOrWhiteSpace(document.Name))
        {
            throw new ProjectManifestException(sourceName, "must declare a non-empty 'name'");
        }

        if (document.SchemaVersion is not { } schemaVersion)
        {
            throw new ProjectManifestException(sourceName, $"must declare 'schema_version = {SupportedSchemaVersion}'");
        }

        if (schemaVersion != SupportedSchemaVersion)
        {
            throw new ProjectManifestException(
                sourceName,
                $"declares schema_version = {schemaVersion}, which this build cannot read " +
                $"(supported: {SupportedSchemaVersion})");
        }

        AssetIgnoreRules ignore;
        try
        {
            ignore = AssetIgnoreRules.Parse(document.Assets?.Ignore ?? []);
        }
        catch (ArgumentException error)
        {
            throw new ProjectManifestException(sourceName, $"has an invalid [assets] ignore list: {error.Message}", error);
        }

        var profiles = new Dictionary<string, BuildProfile>(StringComparer.Ordinal);
        if (document.Build?.Profiles is { } declared)
        {
            foreach (var (profileName, profileDocument) in declared)
            {
                if (string.IsNullOrWhiteSpace(profileName))
                {
                    throw new ProjectManifestException(sourceName, "declares a build profile with an empty name");
                }

                RejectUnknown(sourceName, profileDocument?.Unknown, $"on build profile '{profileName}'");
                profiles.Add(profileName, ReadProfile(sourceName, profileName, profileDocument));
            }
        }

        static string? Folder(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('/');

        var extract = new ExtractSettings(
            Folder(document.Extract?.Directory),
            string.IsNullOrWhiteSpace(document.Extract?.StaticMeshComponent) ? null : document.Extract.StaticMeshComponent,
            string.IsNullOrWhiteSpace(document.Extract?.SkinnedMeshComponent) ? null : document.Extract.SkinnedMeshComponent)
        {
            Meshes = Folder(document.Extract?.Meshes),
            Skeletons = Folder(document.Extract?.Skeletons),
            Animations = Folder(document.Extract?.Animations),
            Materials = Folder(document.Extract?.Materials),
            Textures = Folder(document.Extract?.Textures),
            Prefabs = Folder(document.Extract?.Prefabs),
        };
        return new ProjectManifest(document.Name, schemaVersion, ignore, profiles, extract, ReadHost(sourceName, document.Host));
    }

    private static HostSettings ReadHost(string sourceName, HostSectionDocument? document)
    {
        if (document is null) return HostSettings.None;

        var project = document.Project?.Trim();
        if (project is not null && project.Length == 0)
        {
            throw new ProjectManifestException(sourceName, "sets an empty [host] project; omit the key when there is no launcher");
        }

        if (project is not null && !project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProjectManifestException(sourceName, $"sets [host] project = \"{project}\", which is not a .csproj; the host is built and run through MSBuild");
        }

        if (document.Arguments is { } arguments && arguments.Any(string.IsNullOrEmpty))
        {
            throw new ProjectManifestException(sourceName, "lists an empty string in [host] arguments");
        }

        var scene = document.Scene?.Trim();
        if (scene is not null && scene.Length == 0)
        {
            throw new ProjectManifestException(sourceName, "sets an empty [host] scene; omit the key when there is no default document");
        }

        return new HostSettings(project, document.Arguments ?? [], scene?.TrimStart('/'));
    }

    private static BuildProfile ReadProfile(string sourceName, string profileName, BuildProfileDocument? document)
    {
        // "[build.profiles.dev]" with no keys deserializes as null and means all defaults.
        if (document is null) return BuildProfile.Default;

        if (document.Pack == true)
        {
            throw new ProjectManifestException(
                sourceName,
                $"sets pack = true on build profile '{profileName}', which is reserved: no packer exists yet");
        }

        return new BuildProfile(
            ReadDocumentFormat(sourceName, profileName, document.DocumentFormat),
            ReadTextureQuality(sourceName, profileName, document.TextureQuality),
            document.Pack ?? BuildProfile.Default.Pack);
    }

    private static DocumentFormat ReadDocumentFormat(string sourceName, string profileName, string? value) => value switch
    {
        null => BuildProfile.Default.DocumentFormat,
        "toml" => DocumentFormat.Toml,
        "json" => DocumentFormat.Json,
        "blob" => throw new ProjectManifestException(
            sourceName,
            $"sets document_format = \"blob\" on build profile '{profileName}', which is reserved: no writer exists yet"),
        _ => throw new ProjectManifestException(
            sourceName,
            $"sets document_format = \"{value}\" on build profile '{profileName}'; expected \"toml\" or \"json\""),
    };

    private static void RejectUnknown(string sourceName, Dictionary<string, object?>? unknown, string context)
    {
        if (unknown is not { Count: > 0 }) return;
        var keys = string.Join("', '", unknown.Keys);
        throw new ProjectManifestException(
            sourceName,
            $"has unknown key(s) '{keys}' {context}; a key this build does not read is a setting that never applies");
    }

    private static TextureQuality ReadTextureQuality(string sourceName, string profileName, string? value) => value switch
    {
        null => BuildProfile.Default.TextureQuality,
        "fast" => TextureQuality.Fast,
        "full" => TextureQuality.Full,
        _ => throw new ProjectManifestException(
            sourceName,
            $"sets texture_quality = \"{value}\" on build profile '{profileName}'; expected \"fast\" or \"full\""),
    };
}

/// <summary>One exception type for read, parse and validation failures, because the caller's response is the same: report and stop.</summary>
public sealed class ProjectManifestException : Exception
{
    public ProjectManifestException(string sourceName, string problem, Exception? innerException = null)
        : base($"Project manifest '{sourceName}' {problem}.", innerException)
    {
        SourceName = sourceName;
    }

    public string SourceName { get; }
}

/// <summary>
/// The <c>[host]</c> section: the launcher's csproj, RELATIVE TO THE PROJECT ROOT (the directory
/// holding <c>assets/</c>) rather than to <c>assets/</c> like <c>[extract]</c>, because a launcher
/// is a sibling of the asset tree, never inside it; and the arguments every launch gets before the
/// caller's own. Null project means the game is not launched by the CLI. <paramref name="Scene"/>
/// is the document a Play with no <c>--scene</c> runs (the tray's Play), assets-relative.
/// </summary>
public sealed record HostSettings(string? Project, IReadOnlyList<string> Arguments, string? Scene = null)
{
    public static HostSettings None { get; } = new(null, []);
}

/// <summary>A file an extraction is about to write, as far as WHERE it goes is concerned.</summary>
public enum ExtractKind
{
    /// <summary>The geometry documents: a <c>.mesh</c> or a <c>.skinnedmesh</c>.</summary>
    Mesh,
    /// <summary>The <c>.skeleton</c> a skinned mesh names. Its own kind because a project can reasonably file it with the rig's clips rather than with the geometry; it defaults to the geometry's directory, which is where it has always gone.</summary>
    Skeleton,
    Animation,
    Material,
    Texture,
    Prefab,
}

/// <summary>
/// The <c>[extract]</c> section: where each kind of extracted asset lands, and the component type
/// names a generated prefab authors a mesh into (null = the schema decides by name).
/// </summary>
/// <remarks>
/// Every directory is assets-relative. <see cref="Directory"/> is what a kind that names none
/// falls back to, and a null fallback means beside the GLB — so a project that sets nothing keeps
/// the original behaviour, and one that sets only <c>directory</c> keeps the single-folder one.
/// A GLB's own <c>[glb] extract</c> outranks all of it: a per-GLB directive is more specific than
/// a project default, so it names one folder for everything that GLB extracts to.
/// </remarks>
public sealed record ExtractSettings(string? Directory, string? StaticMeshComponent, string? SkinnedMeshComponent)
{
    public static ExtractSettings None { get; } = new(null, null, null);

    public string? Meshes { get; init; }

    /// <summary>Null falls back to <see cref="Meshes"/>: a skeleton has always landed with the geometry, and a project that says nothing keeps that.</summary>
    public string? Skeletons { get; init; }

    public string? Animations { get; init; }

    public string? Materials { get; init; }

    public string? Textures { get; init; }

    public string? Prefabs { get; init; }

    /// <summary>Where <paramref name="kind"/> goes, or null for beside the GLB.</summary>
    public string? DirectoryFor(ExtractKind kind) => kind switch
    {
        ExtractKind.Mesh => Meshes,
        ExtractKind.Skeleton => Skeletons ?? Meshes,
        ExtractKind.Animation => Animations,
        ExtractKind.Material => Materials,
        ExtractKind.Texture => Textures,
        ExtractKind.Prefab => Prefabs,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    } ?? Directory;
}
