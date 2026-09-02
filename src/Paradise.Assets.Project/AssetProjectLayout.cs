using Zio;

namespace Paradise.Assets.Project;

/// <summary>The directory layout of one asset project, rooted at the directory that holds <c>assets/</c>.</summary>
/// <remarks>
/// <c>assets/</c> is the committed truth; <c>.editor/</c> and <c>build/</c> are derived, and
/// deleting either must lose nothing. Anything that would break that — a setting only in
/// <c>.editor/</c>, an artifact nothing can regenerate — belongs in <c>assets/</c> instead.
/// </remarks>
public sealed class AssetProjectLayout
{
    public const string AssetsDirectoryName = "assets";

    public const string EditorDirectoryName = ".editor";

    public const string BuildDirectoryName = "build";

    /// <summary>Inside <c>assets/</c> because it is authored, not derived.</summary>
    public const string ManifestFileName = "project.toml";

    /// <summary>Takes the root on trust; callers that need the check have <see cref="TryLocate"/>.</summary>
    public AssetProjectLayout(UPath root)
    {
        root.AssertNotNull(nameof(root));
        Root = root.ToAbsolute();
    }

    public UPath Root { get; }

    public UPath Assets => Root / AssetsDirectoryName;

    public UPath Manifest => Assets / ManifestFileName;

    public UPath Editor => Root / EditorDirectoryName;

    /// <summary>Materialized working <c>.blend</c> files; disposable.</summary>
    public UPath EditorBlend => Editor / "blend";

    /// <summary>The content-addressed artifact cache, shared with the Blender addon: same directory, same digest scheme, same entry layout.</summary>
    public UPath EditorCache => Editor / "cache";

    /// <summary>Layout-identical to <see cref="Build"/> so a playmode bug is a build bug.</summary>
    public UPath EditorPlay => Editor / "play";

    public UPath EditorState => Editor / "state.toml";

    public UPath Build => Root / BuildDirectoryName;

    public UPath OutputFor(ProjectOutputTarget target) => target switch
    {
        ProjectOutputTarget.Build => Build,
        ProjectOutputTarget.Play => EditorPlay,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown output target."),
    };

    /// <summary>Walks up to the first directory holding <c>assets/project.toml</c>; the FILE is the marker because a game repo can easily hold some other <c>assets</c> folder.</summary>
    public static bool TryLocate(IFileSystem fileSystem, UPath startDirectory, out AssetProjectLayout? layout)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        startDirectory.AssertNotNull(nameof(startDirectory));

        var current = startDirectory.ToAbsolute();
        while (!current.IsNull)
        {
            if (fileSystem.FileExists(current / AssetsDirectoryName / ManifestFileName))
            {
                layout = new AssetProjectLayout(current);
                return true;
            }

            var parent = current.GetDirectory();
            if (parent == current) break;
            current = parent;
        }

        layout = null;
        return false;
    }

    /// <exception cref="DirectoryNotFoundException">No <c>assets/project.toml</c> was found.</exception>
    public static AssetProjectLayout Locate(IFileSystem fileSystem, UPath startDirectory)
    {
        if (TryLocate(fileSystem, startDirectory, out var layout)) return layout!;
        throw new DirectoryNotFoundException(
            $"No Paradise asset project at or above '{startDirectory}': expected an " +
            $"'{AssetsDirectoryName}/{ManifestFileName}' in some parent directory.");
    }
}

/// <summary>Which build-shaped output tree a caller wants; same layout, differing only in who writes it and whether it ships.</summary>
public enum ProjectOutputTarget
{
    Build,

    Play,
}
