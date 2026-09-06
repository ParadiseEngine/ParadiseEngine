using System.Text.Json;
using System.Xml.Linq;

using Zio;

namespace Paradise.Cli;

/// <summary>
/// Whether a launcher's last build is still current, answered from the filesystem in milliseconds
/// so the common Play — nothing changed — skips MSBuild's several-second no-op.
/// </summary>
/// <remarks>
/// The reference closure comes from <c>obj/project.assets.json</c>, which restore already
/// flattened — including ProjectReferences a <c>Directory.Build.targets</c> injected that the
/// csproj never mentions, which is exactly how a workspace builds a game against engine source.
/// Deliberately no MSBuild evaluation and no attempt to be exact: a newer source anywhere in the
/// closure means "build", and MSBuild then decides what that build actually is.
///
/// "Newer" is against <see cref="StampFileName"/>, written by <see cref="HostSession"/> after a
/// build it ran succeeded — NOT against the launcher's own dll. An incremental build that touched
/// one library leaves the launcher dll where it was, so a dll-relative check reports the tree
/// stale after every edit forever. A build made elsewhere (an IDE, a shell) leaves the stamp
/// behind and costs one no-op MSBuild pass, after which the stamp is current again.
/// </remarks>
internal sealed record HostFreshness(
    UPath Csproj,
    UPath? Output,
    bool OutputExists,
    bool HasStamp,
    bool HasAssets,
    bool SourcesChanged,
    bool ProjectFilesChanged,
    IReadOnlyList<UPath> ProjectDirectories)
{
    public const string AssetsFileName = "project.assets.json";

    /// <summary>Under <c>obj/</c>, which every repo already ignores.</summary>
    public const string StampFileName = "paradise-host.stamp";

    private static readonly HashSet<string> s_sourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".slang", ".resx", ".xaml", ".razor", ".tt",
    };

    private static readonly HashSet<string> s_projectExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj", ".props", ".targets", ".slnx", ".sln",
    };

    private static readonly HashSet<string> s_projectFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "global.json", "packages.lock.json", "nuget.config",
    };

    public bool IsFresh => OutputExists && HasStamp && HasAssets && !SourcesChanged && !ProjectFilesChanged;

    /// <summary>A restore is only owed when something restore reads has changed; skipping it is most of what makes a rebuild after one edit bearable.</summary>
    public bool NeedsRestore => !HasAssets || ProjectFilesChanged;

    public static HostFreshness Inspect(IFileSystem fileSystem, UPath csproj, string configuration)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        csproj.AssertAbsolute(nameof(csproj));
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);

        var directory = csproj.GetDirectory();
        var assetsPath = directory / "obj" / AssetsFileName;
        var hasAssets = fileSystem.FileExists(assetsPath);
        var stampPath = StampPath(csproj);
        var hasStamp = fileSystem.FileExists(stampPath);

        var directories = new List<UPath> { directory };
        string? framework = null;
        string? runtime = null;
        if (hasAssets)
        {
            ReadAssets(fileSystem, assetsPath, directory, directories, out framework, out runtime);
        }

        var output = framework is null ? (UPath?)null : FindOutput(fileSystem, directory / "bin" / configuration / framework, runtime, AssemblyName(fileSystem, csproj) + ".dll");
        var outputExists = output is { } path && fileSystem.FileExists(path);

        var outputStamp = hasStamp ? fileSystem.GetLastWriteTime(stampPath) : DateTime.MinValue;
        var assetsStamp = hasAssets ? fileSystem.GetLastWriteTime(assetsPath) : DateTime.MinValue;

        var sourcesChanged = false;
        var projectFilesChanged = false;
        foreach (var root in directories)
        {
            Scan(fileSystem, root, outputStamp, assetsStamp, ref sourcesChanged, ref projectFilesChanged);
        }

        // Directory.Build.props/targets, Directory.Packages.props and global.json apply from any
        // ancestor; the injected ProjectReferences live in one of them.
        for (var ancestor = directory.GetDirectory(); !ancestor.IsNull && !ancestor.IsEmpty; ancestor = ancestor.GetDirectory())
        {
            foreach (var file in fileSystem.EnumerateFiles(ancestor))
            {
                Classify(fileSystem, file, outputStamp, assetsStamp, ref sourcesChanged, ref projectFilesChanged, projectOnly: true);
            }

            if (ancestor == UPath.Root) break;
        }

        return new HostFreshness(csproj, output, outputExists, hasStamp, hasAssets, sourcesChanged, projectFilesChanged, directories);
    }

    public static UPath StampPath(UPath csproj) => csproj.GetDirectory() / "obj" / StampFileName;

    /// <summary>Record that a build succeeded now; the next <see cref="Inspect"/> measures against this moment.</summary>
    public static void Stamp(IFileSystem fileSystem, UPath csproj)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        var path = StampPath(csproj);
        fileSystem.CreateDirectory(path.GetDirectory());
        fileSystem.WriteAllText(path, DateTime.UtcNow.ToString("O") + "\n");
    }

    /// <summary>
    /// <c>bin/&lt;cfg&gt;/&lt;tfm&gt;/&lt;name&gt;.dll</c>, or one directory deeper when the project restored
    /// for a runtime identifier (the SDK appends <c>&lt;rid&gt;/</c> by default). When neither is
    /// there, the rid path is what the message names and what a build is expected to produce.
    /// </summary>
    private static UPath FindOutput(IFileSystem fileSystem, UPath frameworkDirectory, string? runtime, string fileName)
    {
        var flat = frameworkDirectory / fileName;
        if (runtime is null) return flat;
        var withRuntime = frameworkDirectory / runtime / fileName;
        return fileSystem.FileExists(flat) && !fileSystem.FileExists(withRuntime) ? flat : withRuntime;
    }

    private static void ReadAssets(IFileSystem fileSystem, UPath assetsPath, UPath directory, List<UPath> directories, out string? framework, out string? runtime)
    {
        framework = null;
        runtime = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(fileSystem.ReadAllText(assetsPath));
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var rootElement = document.RootElement;
            if (rootElement.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Object)
            {
                foreach (var library in libraries.EnumerateObject())
                {
                    if (!library.Value.TryGetProperty("type", out var type) || type.GetString() != "project") continue;
                    if (!library.Value.TryGetProperty("msbuildProject", out var projectPath) || projectPath.GetString() is not { Length: > 0 } relative) continue;

                    // Restore spells the path with the host's separators; a UPath wants '/'.
                    var referenced = directory / relative.Replace('\\', '/');
                    var referencedDirectory = referenced.GetDirectory();
                    if (!directories.Contains(referencedDirectory)) directories.Add(referencedDirectory);
                }
            }

            if (rootElement.TryGetProperty("project", out var project)
                && project.TryGetProperty("frameworks", out var frameworks)
                && frameworks.ValueKind == JsonValueKind.Object)
            {
                framework = frameworks.EnumerateObject().Select(entry => entry.Name).FirstOrDefault();
            }

            // Restore records the rid(s) the project was restored for; one means the output moved
            // under it. Several (a RuntimeIdentifiers list) leave the flat layout alone.
            if (rootElement.TryGetProperty("project", out var project2)
                && project2.TryGetProperty("runtimes", out var runtimes)
                && runtimes.ValueKind == JsonValueKind.Object)
            {
                var names = runtimes.EnumerateObject().Select(entry => entry.Name).ToList();
                if (names.Count == 1) runtime = names[0];
            }
        }
    }

    private static string AssemblyName(IFileSystem fileSystem, UPath csproj)
    {
        try
        {
            var declared = XDocument.Parse(fileSystem.ReadAllText(csproj))
                .Descendants("AssemblyName")
                .Select(element => element.Value.Trim())
                .FirstOrDefault(value => value.Length > 0 && !value.Contains("$("));
            if (declared is not null) return declared;
        }
        catch (System.Xml.XmlException)
        {
            // An unparsable csproj is MSBuild's to report; the default name still finds the output.
        }

        return csproj.GetNameWithoutExtension() ?? csproj.GetName();
    }

    private static void Scan(IFileSystem fileSystem, UPath directory, DateTime outputStamp, DateTime assetsStamp, ref bool sourcesChanged, ref bool projectFilesChanged)
    {
        if (!fileSystem.DirectoryExists(directory)) return;

        foreach (var file in fileSystem.EnumerateFiles(directory))
        {
            Classify(fileSystem, file, outputStamp, assetsStamp, ref sourcesChanged, ref projectFilesChanged, projectOnly: false);
        }

        foreach (var child in fileSystem.EnumerateDirectories(directory))
        {
            var name = child.GetName();
            // bin/ and obj/ are outputs of the thing being checked; a dot-directory is an IDE's.
            if (name is "bin" or "obj" || name.StartsWith('.')) continue;
            Scan(fileSystem, child, outputStamp, assetsStamp, ref sourcesChanged, ref projectFilesChanged);
        }
    }

    private static void Classify(IFileSystem fileSystem, UPath file, DateTime outputStamp, DateTime assetsStamp, ref bool sourcesChanged, ref bool projectFilesChanged, bool projectOnly)
    {
        var extension = file.GetExtensionWithDot() ?? string.Empty;
        var name = file.GetName();
        var isProjectFile = s_projectExtensions.Contains(extension) || s_projectFileNames.Contains(name);
        if (isProjectFile)
        {
            var stamp = fileSystem.GetLastWriteTime(file);
            if (stamp > assetsStamp) projectFilesChanged = true;
            if (stamp > outputStamp) sourcesChanged = true;
            return;
        }

        if (projectOnly || !s_sourceExtensions.Contains(extension)) return;
        if (fileSystem.GetLastWriteTime(file) > outputStamp) sourcesChanged = true;
    }
}
