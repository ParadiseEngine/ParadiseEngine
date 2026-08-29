using Paradise.Assets.Cli;
using Paradise.Assets.Pipeline;
using Paradise.Assets.Project;

using Zio.FileSystems;

// Exit codes: 0 clean, 1 findings or failure, 2 usage error. The pattern every CI step
// understands, and the same trio the bridge's contract-check settled on.
if (args.Length == 0) return Usage();

var verb = args[0];
string? projectDirectory = null;
var fix = false;
var keepEditor = false;
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--project" when i + 1 < args.Length:
            projectDirectory = args[++i];
            break;

        case "--fix":
            fix = true;
            break;

        case "--keep-editor":
            keepEditor = true;
            break;

        default:
            Console.Error.WriteLine($"paradise-assets: unknown argument '{args[i]}'");
            return Usage();
    }
}

using var physical = new PhysicalFileSystem();
var start = physical.ConvertPathFromInternal(Path.GetFullPath(projectDirectory ?? Directory.GetCurrentDirectory()));
AssetProjectLayout layout;
try
{
    layout = AssetProjectLayout.Locate(physical, start);
}
catch (DirectoryNotFoundException error)
{
    Console.Error.WriteLine($"paradise-assets: {error.Message}");
    return 1;
}

switch (verb)
{
    case "verify":
        return Verbs.Verify(physical, layout);

    case "scene-check":
        return Verbs.SceneCheck(physical, layout, fix);

    case "clean":
        return Verbs.Clean(physical, layout, keepEditor);

    case "build":
    case "watch":
    case "mv":
    case "pack":
        Console.Error.WriteLine($"paradise-assets: '{verb}' is not implemented yet (tracked by the asset-management plan).");
        return 1;

    default:
        Console.Error.WriteLine($"paradise-assets: unknown verb '{verb}'");
        return Usage();
}

static int Usage()
{
    Console.Error.WriteLine(
        """
        usage: paradise-assets <verb> [options]

        verbs:
          verify                 check the assets/ tree: sidecars, identities, document validity
          scene-check [--fix]    police (or restore) canonical form of *.scene.toml documents
          clean [--keep-editor]  delete derived output (build/, and .editor/ unless kept)

        options:
          --project <dir>        the project root (default: found from the working directory)
        """);
    return 2;
}
