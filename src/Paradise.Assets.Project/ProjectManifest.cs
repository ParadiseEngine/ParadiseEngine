using Tomlyn;

using Zio;

namespace Paradise.Assets.Project;

/// <summary>A validated <c>assets/project.toml</c>.</summary>
/// <remarks>Rejects unknown settings and unsupported <c>blob</c>/<c>pack</c> modes; projects may choose their own profile names.</remarks>
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

    /// <summary>
    /// <c>[extensions] assemblies</c>: assemblies the tooling loads to find this project's own
    /// importers, relative to the PROJECT ROOT like <c>[host] project</c> — an extension assembly
    /// is a sibling of the asset tree, never inside it. Empty for a project that extends nothing.
    /// </summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];

    /// <summary>Source projects published before extension loading, relative to the project root.</summary>
    public IReadOnlyList<string> ExtensionProjects { get; init; } = [];

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
        RejectUnknown(sourceName, document.Host?.Unknown, "in [host]");
        RejectUnknown(sourceName, document.Extensions?.Unknown, "in [extensions]");

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

        // Assets-relative and nothing else. `/etc` or `../out` would reach
        // `(layout.Assets / relative)` and put extraction output outside the tree, where nothing
        // indexes it — diagnosed here, at the key, rather than as an unresolved entry per file.
        string? Folder(string? value, string key)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var folder = value.Trim().Replace('\\', '/').TrimEnd('/');
            if (folder.Length == 0) return null;

            if (folder.StartsWith('/') || (folder.Length > 1 && folder[1] == ':'))
            {
                throw new ProjectManifestException(sourceName, $"sets '{key}' in [extract] to '{value}', which is absolute; extraction directories are relative to assets/");
            }

            if (folder.Split('/').Any(segment => segment == ".."))
            {
                throw new ProjectManifestException(sourceName, $"sets '{key}' in [extract] to '{value}', which climbs above assets/; extraction writes inside the asset tree");
            }

            return folder;
        }

        var directories = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in document.Extract?.Unknown ?? [])
        {
            // A kind's value is a directory. Anything else is a typo worth naming here, since no
            // reading of it makes sense whatever the extractor chain turns out to declare.
            if (value is not string text)
            {
                throw new ProjectManifestException(sourceName, $"sets '{key}' in [extract] to {(value is null ? "nothing" : value.GetType().Name.ToLowerInvariant())}; a kind's value is a directory");
            }

            if (Folder(text, key) is { } folder) directories[key] = folder;
        }

        var extract = new ExtractSettings(
            Folder(document.Extract?.Directory, "directory"),
            string.IsNullOrWhiteSpace(document.Extract?.StaticMeshComponent) ? null : document.Extract.StaticMeshComponent,
            string.IsNullOrWhiteSpace(document.Extract?.SkinnedMeshComponent) ? null : document.Extract.SkinnedMeshComponent)
        {
            Directories = directories,
        };
        var assemblies = document.Extensions?.Assemblies ?? [];
        if (assemblies.Any(string.IsNullOrWhiteSpace))
        {
            throw new ProjectManifestException(sourceName, "lists an empty string in [extensions] assemblies");
        }

        var projects = (document.Extensions?.Projects ?? []).Select(path => ExtensionProjectPath(path, sourceName)).ToArray();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            if (!names.Add(Path.GetFileNameWithoutExtension(project)))
                throw new ProjectManifestException(sourceName, $"[extensions] projects repeats the output name '{Path.GetFileNameWithoutExtension(project)}'");
        }

        return new ProjectManifest(document.Name, schemaVersion, ignore, profiles, extract, ReadHost(sourceName, document.Host))
        {
            Extensions = assemblies.ConvertAll(path => path.Trim()),
            ExtensionProjects = projects,
        };
    }

    private static string ExtensionProjectPath(string? value, string sourceName)
    {
        var path = value?.Trim();
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Contains(':')
            || path.StartsWith('/') || path.Split('/').Any(part => part is "" or "." or "..")
            || string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(path))
            || (Path.GetExtension(path).ToLowerInvariant() is not (".csproj" or ".proj")))
            throw new ProjectManifestException(sourceName, $"[extensions] projects requires relative .csproj or .proj paths without traversal: '{value}'");
        return path;
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

/// <summary>
/// A kind of thing an extractor writes, as far as WHERE it goes is concerned, and what its
/// directory falls back to when the project names none for it.
/// </summary>
/// <remarks>
/// Declared by the extractor that produces the kind, not by the engine: a game's own container may
/// yield tilesets or LODs, and having to add an enum member for one would be the same "edit the
/// engine every time" the extractor chain exists to remove. <paramref name="Id"/> is the key an
/// author writes in <c>[extract]</c>, so it reads as the plural noun it is: <c>materials</c>.
/// </remarks>
/// <param name="FallsBackTo">The kind whose directory this one takes when the project names none — a <c>.skeleton</c> follows the geometry that names it. Null goes straight to <c>directory</c>.</param>
public sealed record ExtractKindDeclaration(string Id, string? FallsBackTo = null);

/// <summary>
/// The kind ids the built-in extractor declares, as constants. A game's extractor reuses one when
/// its output belongs with everything else of that kind — a tileset's materials are materials — and
/// declares its own id when it does not.
/// </summary>
public static class ExtractKind
{
    public const string Meshes = "meshes";
    public const string Skeletons = "skeletons";
    public const string Animations = "animations";
    public const string Materials = "materials";
    public const string Textures = "textures";
    public const string Prefabs = "prefabs";
}

/// <summary>
/// The <c>[extract]</c> section: where each kind of extracted asset lands, and the component type
/// names a generated prefab authors a mesh into (null = the schema decides by name).
/// </summary>
/// <remarks>
/// <para>
/// Every directory is assets-relative, and the keys are open: any key that is not one of this
/// section's own settings is a KIND, resolved against whatever the build's extractor chain
/// declares. The manifest cannot judge that on its own — whether a kind exists depends on the
/// chain the tool was built with, which is a runtime fact — so parsing accepts any string-valued
/// key and <c>verify</c> reports one nothing declares. A typo is still caught; it is caught by the
/// component that knows the answer.
/// </para>
/// <para>
/// <see cref="Directory"/> is the last fallback, and a null fallback means beside the source
/// container — so a project that sets nothing keeps the original behaviour, and one that sets only
/// <c>directory</c> keeps the single-folder one. A container's own <c>[glb] extract</c> outranks
/// all of it: a per-file directive is more specific than a project default.
/// </para>
/// </remarks>
public sealed record ExtractSettings(string? Directory, string? StaticMeshComponent, string? SkinnedMeshComponent)
{
    public static ExtractSettings None { get; } = new(null, null, null);

    /// <summary>Kind id to assets-relative directory, exactly as the manifest spelled it; unvalidated by design (see the remarks).</summary>
    public IReadOnlyDictionary<string, string> Directories { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The keys naming a kind, so <c>verify</c> can name one nothing declares.</summary>
    public IEnumerable<string> Kinds => Directories.Keys;

    /// <summary>
    /// Where <paramref name="kind"/> goes, or null for beside the source container: the kind's own
    /// directory, else the one it declares it falls back to, else <see cref="Directory"/>.
    /// </summary>
    /// <param name="declarations">The build's declared kinds, which is what makes a fallback resolvable.</param>
    public string? DirectoryFor(string kind, IReadOnlyList<ExtractKindDeclaration>? declarations = null)
    {
        ArgumentNullException.ThrowIfNull(kind);

        // A cycle in the declared fallbacks would spin here; the chain is short and the visited set
        // costs nothing, so a badly-declared extractor degrades to `directory` instead of hanging.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = kind;
        while (current is not null && seen.Add(current))
        {
            if (Directories.TryGetValue(current, out var directory)) return directory;
            current = declarations?.FirstOrDefault(d => string.Equals(d.Id, current, StringComparison.Ordinal))?.FallsBackTo;
        }

        return Directory;
    }
}
