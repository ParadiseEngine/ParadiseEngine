using Tomlyn;

using Zio;

namespace Paradise.Assets.Project;

/// <summary>
/// A validated <c>assets/project.toml</c>: what the project is called, which schema it speaks,
/// and the build profiles it declares.
/// </summary>
/// <remarks>
/// <para>
/// The manifest lives inside <c>assets/</c> because it is authored, not derived — the same reason
/// project settings move out of the <c>.blend</c>. Anything a rebuild must reproduce is a
/// manifest key; anything a rebuild produces is not.
/// </para>
/// <para>
/// Loading is <b>strict</b>. A value this build does not understand is an error naming the key
/// and the profile, never a silent fall back to a default: a <c>document_format</c> typo that
/// quietly built TOML into a release tree is the kind of failure nobody finds until ship day.
/// Unknown profile <i>names</i> are the deliberate exception — those are the game's to invent.
/// </para>
/// </remarks>
public sealed class ProjectManifest
{
    /// <summary>The only <c>schema_version</c> this build reads.</summary>
    public const int SupportedSchemaVersion = 1;

    private readonly Dictionary<string, BuildProfile> _profiles;

    private ProjectManifest(string name, int schemaVersion, Dictionary<string, BuildProfile> profiles)
    {
        Name = name;
        SchemaVersion = schemaVersion;
        _profiles = profiles;
    }

    /// <summary>The project name. Required; used for output naming and diagnostics.</summary>
    public string Name { get; }

    /// <summary>The manifest schema version. Required, and always <see cref="SupportedSchemaVersion"/>.</summary>
    public int SchemaVersion { get; }

    /// <summary>The declared build profiles, keyed by their manifest name (case-sensitive, as TOML keys are).</summary>
    public IReadOnlyDictionary<string, BuildProfile> Profiles => _profiles;

    /// <summary>Looks up a declared profile by name.</summary>
    /// <param name="name">The profile name, e.g. <c>dev</c>.</param>
    /// <param name="profile">The profile, or <see langword="null"/> when undeclared.</param>
    /// <returns><see langword="true"/> if the manifest declares <paramref name="name"/>.</returns>
    public bool TryGetProfile(string name, out BuildProfile? profile) => _profiles.TryGetValue(name, out profile);

    /// <summary>
    /// Reads and validates the manifest at <paramref name="path"/>.
    /// </summary>
    /// <param name="fileSystem">The filesystem holding the project.</param>
    /// <param name="path">Absolute path of the manifest, typically <see cref="AssetProjectLayout.Manifest"/>.</param>
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

    /// <summary>
    /// Validates an already-read manifest. The filesystem-free half of <see cref="Load"/>.
    /// </summary>
    /// <param name="toml">The manifest text.</param>
    /// <param name="sourceName">What to call the source in error messages.</param>
    /// <exception cref="ProjectManifestException">The text is not valid TOML, or the document is not a valid manifest.</exception>
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

        var profiles = new Dictionary<string, BuildProfile>(StringComparer.Ordinal);
        if (document.Build?.Profiles is { } declared)
        {
            foreach (var (profileName, profileDocument) in declared)
            {
                if (string.IsNullOrWhiteSpace(profileName))
                {
                    throw new ProjectManifestException(sourceName, "declares a build profile with an empty name");
                }

                profiles.Add(profileName, ReadProfile(sourceName, profileName, profileDocument));
            }
        }

        return new ProjectManifest(document.Name, schemaVersion, profiles);
    }

    private static BuildProfile ReadProfile(string sourceName, string profileName, BuildProfileDocument? document)
    {
        // An entirely absent table body is legal TOML ("[build.profiles.dev]" with no keys) and
        // means "all defaults", which is exactly BuildProfile.Default.
        if (document is null) return BuildProfile.Default;

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
        "blob" => DocumentFormat.Blob,
        _ => throw new ProjectManifestException(
            sourceName,
            $"sets document_format = \"{value}\" on build profile '{profileName}'; expected \"toml\", \"json\" or \"blob\""),
    };

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

/// <summary>
/// A project manifest could not be read, parsed, or validated.
/// </summary>
/// <remarks>
/// One exception type for all three because the caller's response is the same in every case:
/// report it to the author and stop. The message carries the source and the offending key.
/// </remarks>
public sealed class ProjectManifestException : Exception
{
    /// <summary>Creates an exception describing a problem with <paramref name="sourceName"/>.</summary>
    /// <param name="sourceName">The manifest path, or another name for the source text.</param>
    /// <param name="problem">The problem, phrased to follow the source name — e.g. "is empty".</param>
    /// <param name="innerException">The underlying failure, when there was one.</param>
    public ProjectManifestException(string sourceName, string problem, Exception? innerException = null)
        : base($"Project manifest '{sourceName}' {problem}.", innerException)
    {
        SourceName = sourceName;
    }

    /// <summary>The manifest this failure is about.</summary>
    public string SourceName { get; }
}
