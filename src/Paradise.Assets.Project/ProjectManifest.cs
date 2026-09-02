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

    private ProjectManifest(string name, int schemaVersion, Dictionary<string, BuildProfile> profiles)
    {
        Name = name;
        SchemaVersion = schemaVersion;
        _profiles = profiles;
    }

    public string Name { get; }

    public int SchemaVersion { get; }

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
        RejectUnknown(sourceName, document.Build?.Unknown, "in [build]");

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

                RejectUnknown(sourceName, profileDocument?.Unknown, $"on build profile '{profileName}'");
                profiles.Add(profileName, ReadProfile(sourceName, profileName, profileDocument));
            }
        }

        return new ProjectManifest(document.Name, schemaVersion, profiles);
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
