using System.Runtime.InteropServices;
using Paradise.Assets.Project;
using Paradise.Authoring;
using Zio;
using Zio.FileSystems;

namespace Paradise.Cli.Test.Plugin;

[Authored, Guid("2cba26a4-38f5-4e27-8cf9-8018f94e4eca")]
public sealed record ActionFixture
{
    [AuthoredButton]
    public static void SharedDependency(AuthorActionContext context)
    {
        using var files = new MemoryFileSystem();
        files.CreateDirectory("/project/assets");
        files.WriteAllText("/project/assets/project.toml", "name = \"action\"\nschema_version = 1\n");
        var layout = AssetProjectLayout.Locate(files, "/project");
        context.Result.Toggles["SharedDependency"] = layout.Root.FullName == "/project";
    }
}

[Authored, Guid("7e6a8f9e-077d-4325-980c-ec1a78f82312")]
public sealed record DuplicateActionFixtureOne
{
    [AuthoredButton] public static void Run() => throw new InvalidOperationException("must not run ambiguous action");
}

[Authored, Guid("7e6a8f9e-077d-4325-980c-ec1a78f82312")]
public sealed record DuplicateActionFixtureTwo
{
    [AuthoredButton] public static void Run() => throw new InvalidOperationException("must not run ambiguous action");
}
