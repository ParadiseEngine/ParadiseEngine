using Paradise.Assets.Pipeline;
using Paradise.Assets.Project;

using Zio;
using Zio.FileSystems;

namespace Paradise.Cli;

/// <summary>
/// The <c>paradise</c> command's entry point, callable from any console project. The dotnet
/// tool is <c>return BuildHost.Run(args);</c>; a game that extends the pipeline is
/// <c>return BuildHost.Run(args, [.. AssetImporters.All, new MyImporter()]);</c> in its own
/// <c>tools/assets</c> project, and build and watch run that chain. That
/// is the extension path (issue #208): a chain is code, so it is passed as code.
/// </summary>
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

        var root = Path.GetFullPath(Path.Combine(output ?? Directory.GetCurrentDirectory(), name));
        return Verbs.New(physical, physical.ConvertPathFromInternal(root), name);
    }

    private static int Assets(PhysicalFileSystem physical, IReadOnlyList<IAssetImporter> importers, string? assetVerb, string[] arguments)
    {
        if (assetVerb is null) return Unknown("'assets' needs a verb (verify, prefab-check, build, clean, watch, mv, rm, refs, extract, catalogue)");

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
                    if (arguments[i].StartsWith('-') || assetVerb is not ("mv" or "rm" or "refs" or "extract")) return Unknown($"unknown argument '{arguments[i]}'");
                    positional.Add(arguments[i]);
                    break;
            }
        }

        // Located here, not up front: `new` and `tools` have no project to find.
        var start = physical.ConvertPathFromInternal(Path.GetFullPath(projectDirectory ?? Directory.GetCurrentDirectory()));
        AssetProjectLayout layout;
        try
        {
            layout = AssetProjectLayout.Locate(physical, start);
        }
        catch (DirectoryNotFoundException error)
        {
            Console.Error.WriteLine($"paradise: {error.Message}");
            return 1;
        }

        return assetVerb switch
        {
            "verify" => Verbs.Verify(physical, layout, fix, importers),
            "prefab-check" => Verbs.PrefabCheck(physical, layout, fix),
            "clean" => Verbs.Clean(physical, layout, keepEditor),
            "build" => Verbs.Build(physical, layout, profile, editor, importers),
            "catalogue" => Verbs.Catalogue(physical, layout),
            "watch" => Verbs.Watch(physical, layout, profile, editorSpecified ? editor : true, dryRun, !noBuild, !noTray, importers),
            "mv" when positional.Count == 2 => Verbs.Move(physical, layout, Absolute(physical, positional[0]), Absolute(physical, positional[1]), importers),
            "mv" => Unknown("'mv' needs a source and a destination: paradise assets mv <from> <to>"),
            "rm" when positional.Count == 1 => Verbs.Remove(physical, layout, Absolute(physical, positional[0]), force, dryRun, importers),
            "rm" => Unknown("'rm' needs one path: paradise assets rm <path> [--force] [--dry-run]"),
            "refs" when positional.Count == 1 => Verbs.Refs(physical, layout, Absolute(physical, positional[0]), transitive, importers),
            "refs" => Unknown("'refs' needs one path: paradise assets refs <path> [--transitive]"),
            "extract" when positional.Count == 1 => Verbs.Extract(physical, layout, Absolute(physical, positional[0]), all, resolution, importers),
            "extract" => Unknown("'extract' needs one path: paradise assets extract <glb | dir --all> [--take-glb | --take-document]"),
            "pack" => NotImplemented(assetVerb),
            _ => Unknown($"unknown assets verb '{assetVerb}'"),
        };
    }

    private static UPath Absolute(PhysicalFileSystem physical, string path)
        => physical.ConvertPathFromInternal(Path.GetFullPath(path));

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
        var start = Path.GetFullPath(projectDirectory ?? Directory.GetCurrentDirectory());
        return AssetProjectLayout.TryLocate(physical, physical.ConvertPathFromInternal(start), out var layout)
            ? physical.ConvertPathToInternal(layout!.Root)
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
            assets catalogue              regenerate the Asset Browser catalogue of prefabs (needs Blender)

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
