using Paradise.Assets.Pipeline;
using Paradise.Assets.Project;

using Zio;
using Zio.FileSystems;

namespace Paradise.Cli;

/// <summary>Runs the <c>paradise</c> CLI with the supplied importer chain.</summary>
/// <remarks>Games can call <c>BuildHost.Run(args, [.. AssetImporters.All, new MyImporter()])</c> to extend every verb.</remarks>
public static class BuildHost
{
    /// <summary>Exit codes: 0 clean, 1 findings or failure, 2 usage error — the same trio as contract-check.</summary>
    public static int Run(string[] args, IReadOnlyList<IAssetImporter>? importers = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        var chain = importers ?? AssetImporters.All;
        if (chain.Count == 0) return Unknown("the importer chain is empty; pass AssetImporters.All plus your own");
        if (args.Length == 0) return Usage();

        // The group is required: `build` would otherwise have to mean "build assets" forever.
        var group = args[0];
        var verb = args.Length > 1 ? args[1] : null;
        var rest = args.Skip(2).ToArray();

        using var physical = new PhysicalFileSystem();

        return group switch
        {
            "new" => New(physical, args.Skip(1).ToArray()),
            "assets" => Assets(physical, chain, verb, rest),
            "tools" => Tools(physical, verb, rest),
            "host" => Host(physical, chain, verb, rest),
            "--help" or "-h" or "help" => Usage(),
            _ => Unknown($"unknown command '{group}'"),
        };
    }

    private static int New(PhysicalFileSystem physical, string[] arguments)
    {
        string? name = null;
        string? output = null;

        for (var i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case "--output" when i + 1 < arguments.Length:
                    output = arguments[++i];
                    break;

                default:
                    if (arguments[i].StartsWith('-') || name is not null) return Unknown($"unexpected argument '{arguments[i]}'");
                    name = arguments[i];
                    break;
            }
        }

        if (name is null) return Unknown("'new' needs a project name: paradise new <name> [--output <dir>]");

        var root = ProjectPaths.ResolveLinks(physical,
            physical.ConvertPathFromInternal(Path.GetFullPath(Path.Combine(output ?? Directory.GetCurrentDirectory(), name))));
        return Verbs.New(physical, root, name);
    }

    private static int Assets(PhysicalFileSystem physical, IReadOnlyList<IAssetImporter> importers, string? assetVerb, string[] arguments)
    {
        if (assetVerb is null) return Unknown("'assets' needs a verb (verify, prefab-check, build, clean, watch, mv, rm, refs, extract, convert, catalogue, invoke-action)");
        if (assetVerb is "invoke-action") return InvokeAction(physical, arguments);

        string? projectDirectory = null;
        string? profile = null;
        var editor = false;
        var editorSpecified = false;
        var fix = false;
        var keepEditor = false;
        var dryRun = false;
        var noBuild = false;
        var noTray = false;
        var force = false;
        var transitive = false;
        var all = false;
        var resolution = ConflictResolution.Refuse;
        var positional = new List<string>();

        for (var i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case "--project" when i + 1 < arguments.Length: projectDirectory = arguments[++i]; break;
                case "--profile" when i + 1 < arguments.Length: profile = arguments[++i]; break;
                case "--editor": editor = true; editorSpecified = true; break;
                case "--no-editor": editor = false; editorSpecified = true; break;
                case "--fix": fix = true; break;
                case "--keep-editor": keepEditor = true; break;
                case "--dry-run": dryRun = true; break;
                case "--no-build": noBuild = true; break;
                case "--no-tray": noTray = true; break;
                case "--force": force = true; break;
                case "--transitive": transitive = true; break;
                case "--all": all = true; break;
                case "--take-glb": resolution = ConflictResolution.TakeGlb; break;
                case "--take-document": resolution = ConflictResolution.TakeDocument; break;
                default:
                    if (arguments[i].StartsWith('-') || assetVerb is not ("mv" or "rm" or "refs" or "extract" or "convert")) return Unknown($"unknown argument '{arguments[i]}'");
                    positional.Add(arguments[i]);
                    break;
            }
        }

        // Located here, not up front: `new` and `tools` have no project to find.
        AssetProjectLayout layout;
        try
        {
            layout = LocateProject(physical, projectDirectory);
        }
        catch (DirectoryNotFoundException error)
        {
            Console.Error.WriteLine($"paradise: {error.Message}");
            return 1;
        }

        // Before the game's extensions load: a conversion needs no importers, and the Blender addon
        // waits on this verb while its author waits on the addon.
        if (assetVerb == "convert")
        {
            if (dryRun) return Unknown("'convert' has no --dry-run: it runs Blender and writes the converted GLB");
            return positional.Count == 1
                ? Verbs.Convert(physical, layout, Absolute(physical, layout, positional[0]))
                : Unknown($"'convert' needs one path: paradise assets convert <model> ({string.Join(", ", ModelSource.Extensions)})");
        }

        using var extensions = ExtensionLoader.Load(physical, layout, importers,
            includeTray: assetVerb == "watch" && !dryRun, buildProjects: !dryRun && assetVerb != "clean");
        if (extensions.BuildExitCode != 0) return extensions.BuildExitCode;
        importers = extensions.Importers;

        return assetVerb switch
        {
            "verify" => Verbs.Verify(physical, layout, fix, importers),
            "prefab-check" => Verbs.PrefabCheck(physical, layout, fix),
            "clean" => Verbs.Clean(physical, layout, keepEditor),
            "build" => Verbs.Build(physical, layout, profile, editor, importers),
            "catalogue" => Verbs.Catalogue(physical, layout),
            "watch" => Verbs.Watch(physical, layout, profile, editorSpecified ? editor : true, dryRun, !noBuild, !noTray, importers, extensions.TrayExtensions),
            "mv" when positional.Count == 2 => Verbs.Move(physical, layout, Absolute(physical, layout, positional[0]), Absolute(physical, layout, positional[1]), importers),
            "mv" => Unknown("'mv' needs a source and a destination: paradise assets mv <from> <to>"),
            "rm" when positional.Count == 1 => Verbs.Remove(physical, layout, Absolute(physical, layout, positional[0]), force, dryRun, importers),
            "rm" => Unknown("'rm' needs one path: paradise assets rm <path> [--force] [--dry-run]"),
            "refs" when positional.Count == 1 => Verbs.Refs(physical, layout, Absolute(physical, layout, positional[0]), transitive, importers),
            "refs" => Unknown("'refs' needs one path: paradise assets refs <path> [--transitive]"),
            "extract" when positional.Count == 1 => Verbs.Extract(physical, layout, Absolute(physical, layout, positional[0]), all, resolution, importers),
            "extract" => Unknown("'extract' needs one path: paradise assets extract <model | dir --all> [--take-glb | --take-document]"),
            "pack" => NotImplemented(assetVerb),
            _ => Unknown($"unknown assets verb '{assetVerb}'"),
        };
    }

    private static int InvokeAction(PhysicalFileSystem physical, string[] arguments)
    {
        string? projectDirectory = null;
        var configuration = "Debug";
        var noBuild = false;
        string? entity = null;
        bool? value = null;
        var onSave = false;
        string? state = null;
        string? response = null;
        string? assembly = null;
        var positional = new List<string>();

        for (var i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case "--project" when i + 1 < arguments.Length: projectDirectory = arguments[++i]; break;
                case "--configuration" or "-c" when i + 1 < arguments.Length: configuration = arguments[++i]; break;
                case "--entity" when i + 1 < arguments.Length: entity = arguments[++i]; break;
                case "--value" when i + 1 < arguments.Length:
                    if (!bool.TryParse(arguments[++i], out var parsedValue)) return Unknown("--value must be true or false");
                    value = parsedValue;
                    break;
                case "--on-save": onSave = true; break;
                case "--state" when i + 1 < arguments.Length: state = arguments[++i]; break;
                case "--response" when i + 1 < arguments.Length: response = arguments[++i]; break;
                case "--assembly" when i + 1 < arguments.Length: assembly = arguments[++i]; break;
                case "--no-build": noBuild = true; break;
                default:
                    if (arguments[i].StartsWith('-')) return Unknown($"unknown argument '{arguments[i]}'");
                    positional.Add(arguments[i]);
                    break;
            }
        }

        if (positional.Count != 3)
            return Unknown("'invoke-action' needs <document.prefab> <component-id> <action> [--entity <guid>] [--no-build] [-c <configuration>]");
        if (assembly is not null && !noBuild) return Unknown("--assembly requires --no-build");
        if (!Guid.TryParse(positional[1], out var componentId))
            return Unknown($"invoke-action: '{positional[1]}' is not a component id (a GUID)");
        Guid? entityId = null;
        if (entity is not null)
        {
            if (!Guid.TryParse(entity, out var parsed))
                return Unknown($"invoke-action: --entity '{entity}' is not a GUID");
            entityId = parsed;
        }

        AssetProjectLayout layout;
        try
        {
            layout = LocateProject(physical, projectDirectory);
        }
        catch (DirectoryNotFoundException error)
        {
            Console.Error.WriteLine($"paradise: {error.Message}");
            return 1;
        }

        var document = Absolute(physical, layout, positional[0]);
        if (!document.FullName.StartsWith(layout.Assets.FullName + "/", StringComparison.Ordinal)
            || !document.GetName().EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            return Unknown($"invoke-action: '{positional[0]}' must be a .prefab under the project's assets/");

        return Verbs.InvokeAction(physical, layout, document, componentId, positional[2], entityId, configuration, noBuild,
            CancellationToken.None, value, onSave, state is null ? (UPath?)null : Absolute(physical, layout, state),
            response is null ? (UPath?)null : Absolute(physical, layout, response), assembly is null ? (UPath?)null : Absolute(physical, layout, assembly));
    }

    private static int Host(PhysicalFileSystem physical, IReadOnlyList<IAssetImporter> importers, string? hostVerb, string[] arguments)
    {
        if (hostVerb is null) return Unknown("'host' needs a verb (build, play)");

        string? projectDirectory = null;
        string? profile = null;
        string? scene = null;
        string? config = null;
        var configuration = "Debug";
        var watch = false;
        var noBuild = false;
        var noAssets = false;
        var passthrough = new List<string>();

        for (var i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case "--project" when i + 1 < arguments.Length: projectDirectory = arguments[++i]; break;
                case "--profile" when i + 1 < arguments.Length: profile = arguments[++i]; break;
                case "--scene" when i + 1 < arguments.Length: scene = arguments[++i]; break;
                case "--config" when i + 1 < arguments.Length: config = arguments[++i]; break;
                case "--configuration" or "-c" when i + 1 < arguments.Length: configuration = arguments[++i]; break;
                case "--watch": watch = true; break;
                case "--no-build": noBuild = true; break;
                case "--no-assets": noAssets = true; break;
                case "--":
                    passthrough.AddRange(arguments.Skip(i + 1));
                    i = arguments.Length;
                    break;
                default:
                    return Unknown($"unknown argument '{arguments[i]}'");
            }
        }

        if (watch && noBuild) return Unknown("--no-build has no meaning with --watch: dotnet watch builds on its own");

        AssetProjectLayout layout;
        try
        {
            layout = LocateProject(physical, projectDirectory);
        }
        catch (DirectoryNotFoundException error)
        {
            Console.Error.WriteLine($"paradise: {error.Message}");
            return 1;
        }

        using var extensions = ExtensionLoader.Load(physical, layout, importers);
        if (extensions.BuildExitCode != 0) return extensions.BuildExitCode;
        importers = extensions.Importers;

        // No signal handling here on purpose: only a child process needs one (ConsoleProcessRunner
        // installs it for the child's lifetime), and a handler that outlived the child would
        // swallow the Ctrl+C that should end an asset cook outright.
        return hostVerb switch
        {
            "build" => Verbs.HostBuild(physical, layout, configuration, CancellationToken.None),
            "play" => Verbs.HostPlay(
                physical, layout, profile,
                scene is null ? (UPath?)null : Absolute(physical, layout, scene),
                config is null ? (UPath?)null : Absolute(physical, layout, config),
                watch, noBuild, noAssets, configuration, passthrough, importers, CancellationToken.None),
            _ => Unknown($"unknown host verb '{hostVerb}'"),
        };
    }

    // Locate on the caller's spelling — a working directory inside a linked directory under
    // assets/ still finds the project — then canonicalize the root so the textual containment
    // checks agree with arguments that reach it through a symlinked ancestor.
    private static AssetProjectLayout LocateProject(PhysicalFileSystem physical, string? projectDirectory)
        => new(ProjectPaths.ResolveLinks(physical, AssetProjectLayout.Locate(physical,
            physical.ConvertPathFromInternal(Path.GetFullPath(projectDirectory ?? Directory.GetCurrentDirectory()))).Root));

    private static UPath Absolute(PhysicalFileSystem physical, AssetProjectLayout layout, string path)
        // A caller may reach the project through a linked ancestor (a workspace view, an aliased
        // --project, an outside alias into assets/); resolve only up to where the path enters the
        // tree so a link inside assets/ still acts like itself — rm removes the link, not its target.
        => ProjectPaths.ResolveInto(physical,
            physical.ConvertPathFromInternal(Path.GetFullPath(path)), layout.Root);

    private static int Tools(PhysicalFileSystem physical, string? toolVerb, string[] arguments)
    {
        if (toolVerb is null) return Unknown("'tools' needs a verb (doctor, install)");

        string? projectDirectory = null;
        var rest = new List<string>();
        for (var i = 0; i < arguments.Length; i++)
        {
            if (arguments[i] == "--project" && i + 1 < arguments.Length) projectDirectory = arguments[++i];
            else rest.Add(arguments[i]);
        }

        return toolVerb switch
        {
            "doctor" => Verbs.ToolsDoctor(ProbeRoot(physical, projectDirectory)),
            "install" when rest.Count == 1 => Verbs.ToolsInstall(EngineRoot(), rest[0]),
            "install" => Unknown("'tools install' needs exactly one tool name (ktx, slang)"),
            _ => Unknown($"unknown tools verb '{toolVerb}'"),
        };
    }

    // `doctor` must answer for the same root `assets build` probes, or the two disagree about
    // whether ktx exists. That is the asset project's root when there is one; from an engine
    // checkout (no asset project) it is the checkout, so the vendored tree is seen.
    private static string ProbeRoot(PhysicalFileSystem physical, string? projectDirectory)
    {
        var start = physical.ConvertPathFromInternal(
            Path.GetFullPath(projectDirectory ?? Directory.GetCurrentDirectory()));
        return AssetProjectLayout.TryLocate(physical, start, out var layout)
            ? physical.ConvertPathToInternal(ProjectPaths.ResolveLinks(physical, layout!.Root))
            : EngineRoot();
    }

    // Only `install` needs an engine checkout (the bootstraps live in tools/); a game repo
    // consuming the packaged CLI has none, and then the working directory is as good as any.
    private static string EngineRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tools", "ktx"))) return directory.FullName;
        }

        return Directory.GetCurrentDirectory();
    }

    private static int NotImplemented(string verb)
    {
        Console.Error.WriteLine($"paradise: '{verb}' is not implemented yet (tracked by the asset-management plan).");
        return 1;
    }

    private static int Unknown(string message)
    {
        Console.Error.WriteLine($"paradise: {message}");
        return Usage();
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            """
            usage: paradise <command> <verb> [options]

            new <name> [--output <dir>]   create a project: assets tree, a sample level, .gitignore

            assets verify [--fix]         check the assets/ tree: sidecars, identities, validity
                                            --fix repoints reference paths a rename left stale
            assets prefab-check [--fix]   police (or restore) canonical form of *.prefab documents
            assets build [--profile p]    compile assets/ into build/ (or .editor/play with --editor)
            assets clean [--keep-editor]  delete derived output (build/, and .editor/ unless kept)
            assets watch                  keep *.meta in step, rebuilding .editor/play (play mode on)
                                            --no-editor rebuilds build/ instead; the tray toggles this
                                            --dry-run reports without writing; --no-build skips the rebuild
                                            a tray icon (idle/building/failed) on Windows and macOS
                                            --no-tray keeps the console-only behaviour
            assets mv <from> <to>         move a file or directory under assets/ with its sidecars,
                                            rewriting every prefab reference to the new path
            assets convert <model>        make a model source's converted GLB (.editor/converted/)
                                            current with headless Blender (.blend .fbx .obj .ply
                                            .stl .usd[a|c|z] .abc .bvh); prints its path as the
                                            last line (a .glb or .gltf prints its own path)
            assets catalogue              regenerate the Asset Browser catalogue of prefabs (needs Blender)
            assets invoke-action <document.prefab> <component-id> <action>
                                           run one [AuthoredButton], [AuthoredToggle], [AuthoredPreview] or
                                           [AuthoredOnSave] method: builds the [host] project when stale,
                                           then calls the method
                                           --entity <guid> hands the action the object's identity
                                           --no-build runs what is built; -c picks the configuration
                                           --value true|false supplies the toggle value
                                           --state <json> reads per-component toggle state
                                           --response <json> writes generic editor updates
                                           --on-save invokes only methods declared [AuthoredOnSave]
                                           previews return read-only overlay geometry; the editor owns visibility
                                           --assembly <dll> with --no-build selects an explicit host output

            host build                    build the launcher [host] names in assets/project.toml
            host play [--scene <doc>]     build assets into .editor/play, build the launcher if a source
                                            changed, run it on the document and wait for it to exit
                                            --watch runs it under `dotnet watch run` instead: an edit
                                            hot-patches or rebuilds and restarts the game
                                            --no-build runs what is built; --no-assets skips the asset build
                                            -c <configuration> (default Debug); arguments after -- go to the game

            tools doctor                  report every build tool: found, version, and how to fix
                                            probes the same root `assets build` does (--project applies)
            tools install <ktx|slang>     install one (may prompt for elevation on Windows)

            options:
              --project <dir>             the project root (default: found from the working directory)
              --profile <name>            a build profile declared in project.toml
                                            (omitted: the built-in defaults — toml, full quality)
            """);
        return 2;
    }
}
