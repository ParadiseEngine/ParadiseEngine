using System.Collections.Immutable;
using Paradise.Assets.Documents;
using Zio;

namespace Paradise.Editor.Core.Persistence;

/// <summary>What the editor remembers about this user, across projects.</summary>
/// <remarks>Disposable by design: deleting the file loses a theme choice and a recent list, and
/// nothing else. That is why a version below <see cref="Version"/> is discarded rather than
/// migrated — see <see cref="Read"/>.</remarks>
public sealed record EditorUserSettings(string Theme, ImmutableList<string> RecentProjects)
{
    public const int Version = 1;
    public const string DefaultTheme = "dark";

    /// <summary>How many recent projects to keep. A list nobody scrolls is a list nobody reads.</summary>
    public const int MaxRecentProjects = 10;

    public static EditorUserSettings Default { get; } = new(DefaultTheme, ImmutableList<string>.Empty);

    /// <summary>This list with <paramref name="project"/> at the front, deduplicated and trimmed.</summary>
    public EditorUserSettings WithRecentProject(string project) => this with
    {
        RecentProjects = RecentProjects
            .Remove(project)
            .Insert(0, project)
            .Take(MaxRecentProjects)
            .ToImmutableList(),
    };

    /// <summary>Read the settings, or <see cref="Default"/> when the file is absent, unreadable or
    /// from a future version.</summary>
    /// <remarks>Never throws on content. A settings file the user broke by hand must cost them
    /// their settings, not their editor.</remarks>
    public static EditorUserSettings Read(IFileSystem fileSystem, UPath path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (!fileSystem.FileExists(path)) return Default;

        try
        {
            var document = CanonicalTomlReader.Parse(fileSystem.ReadAllText(path), path.FullName);
            if (document.Value("Version") is not long version || version > Version) return Default;

            var recent = document.Value("RecentProjects") is IReadOnlyList<object> projects
                ? projects.OfType<string>().ToImmutableList()
                : ImmutableList<string>.Empty;
            return new EditorUserSettings(document.Value("Theme") as string ?? DefaultTheme, recent);
        }
        catch (InvalidDataException)
        {
            return Default;
        }
    }

    public void Write(IFileSystem fileSystem, UPath path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var document = new CanonicalTomlTable { { "Version", Version }, { "Theme", Theme } };
        if (!RecentProjects.IsEmpty) document.Add("RecentProjects", RecentProjects.Cast<object>().ToArray());

        var directory = path.GetDirectory();
        if (!directory.IsEmpty && !fileSystem.DirectoryExists(directory)) fileSystem.CreateDirectory(directory);
        fileSystem.WriteAllText(path, CanonicalTomlWriter.WriteString(document));
    }
}
