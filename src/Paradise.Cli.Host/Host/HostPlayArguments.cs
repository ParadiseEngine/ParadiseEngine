using Paradise.Assets.Project;

using Zio;

namespace Paradise.Cli;

/// <summary>The launcher's argument list for one Play: where the built scene and config are, in the order the manifest and the caller add to them.</summary>
internal static class HostPlayArguments
{
    /// <summary>
    /// A scene named under <c>assets/</c> plays from its play-tree twin, so the caller says which
    /// DOCUMENT it is looking at and never has to know the build's layout. Anything else — a path
    /// already inside a build tree, or elsewhere — passes through as given.
    /// </summary>
    public static UPath ResolveScene(AssetProjectLayout layout, UPath scene)
    {
        ArgumentNullException.ThrowIfNull(layout);
        scene.AssertAbsolute(nameof(scene));

        return scene.IsInDirectory(layout.Assets, recursive: true)
            ? layout.EditorPlay / scene.FullName[(layout.Assets.FullName.Length + 1)..]
            : scene;
    }

    /// <summary>The caller's document, else the manifest's <c>[host] scene</c>, else nothing (the launcher's own default).</summary>
    public static UPath? ChooseScene(AssetProjectLayout layout, UPath? requested, HostSettings host)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(host);

        if (requested is { } scene) return scene;
        return host.Scene is { } declared ? layout.Assets / declared : (UPath?)null;
    }

    /// <summary>
    /// <c>&lt;play&gt;/&lt;project name&gt;/config.toml|json</c> when it exists; the same convention
    /// <c>paradise new</c> lays down, so a project that follows it passes nothing.
    /// </summary>
    public static UPath? FindConfig(IFileSystem fileSystem, AssetProjectLayout layout, string projectName)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);

        var directory = layout.EditorPlay / projectName;
        foreach (var extension in new[] { ".toml", ".json" })
        {
            var candidate = directory / ("config" + extension);
            if (fileSystem.FileExists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>Scene and config first (the launcher's own flags), then the manifest's defaults, then the caller's — so a caller can override a default by repeating the flag.</summary>
    public static IReadOnlyList<string> Compose(
        Func<UPath, string> render,
        UPath? scene,
        UPath? config,
        IReadOnlyList<string> manifestArguments,
        IReadOnlyList<string> callerArguments)
    {
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(manifestArguments);
        ArgumentNullException.ThrowIfNull(callerArguments);

        var arguments = new List<string>();
        if (scene is { } s)
        {
            arguments.Add("--scene");
            arguments.Add(render(s));
        }

        if (config is { } c)
        {
            arguments.Add("--config");
            arguments.Add(render(c));
        }

        arguments.AddRange(manifestArguments);
        arguments.AddRange(callerArguments);
        return arguments;
    }
}
