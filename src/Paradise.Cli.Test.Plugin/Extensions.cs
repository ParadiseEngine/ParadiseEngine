using Paradise.Assets.Pipeline;
using Paradise.Cli.Test.PluginDependency;

namespace Paradise.Cli.Test.Plugin;

public sealed class GoodExtension : ITrayExtension, IDisposable
{
    private ITrayExtensionContext? _context;

    public IReadOnlyList<TrayTaskGroup> CreateTaskGroups(ITrayExtensionContext context)
    {
        _context = context;
        return [new()
        {
            Id = "fixture", Label = Probe.Label, AutoTask = "compile",
            Inputs = [new("authoring/scripts", ["*.story"])],
            Outputs = ["story/output.json"],
            Tasks = [new("compile", "Compile from DLL", token =>
                context.RunProcessAsync("fixture-compiler", ["source with spaces", "--check"], token))],
        }];
    }

    public void Dispose() => _context?.Log("fixture disposed");
}

public sealed class DualRoleExtension : ITrayExtension, IAssetImporter, IDisposable
{
    private ITrayExtensionContext? _context;
    public string Name => "fixture-importer";
    public bool RecordsIdentity => false;
    public bool Claims(ImportCandidate candidate) => false;
    public bool Import(ImportContext context, List<string> errors) => false;
    public IReadOnlyList<TrayTaskGroup> CreateTaskGroups(ITrayExtensionContext context)
    {
        _context = context;
        return [];
    }
    public void Dispose() => _context?.Log("dual role disposed");
}

public sealed class ThrowingConstructor : ITrayExtension
{
    public ThrowingConstructor() => throw new InvalidOperationException("fixture constructor failure");
    public IReadOnlyList<TrayTaskGroup> CreateTaskGroups(ITrayExtensionContext context) => [];
}

public sealed class NoDefaultConstructor(string name) : ITrayExtension
{
    public IReadOnlyList<TrayTaskGroup> CreateTaskGroups(ITrayExtensionContext context)
        => throw new InvalidOperationException(name);
}

public sealed class OpenGeneric<T> : ITrayExtension
{
    public IReadOnlyList<TrayTaskGroup> CreateTaskGroups(ITrayExtensionContext context) => [];
}

public sealed class InvalidRegistration : ITrayExtension
{
    public IReadOnlyList<TrayTaskGroup> CreateTaskGroups(ITrayExtensionContext context)
        => throw new InvalidDataException("fixture registration failure");
}
