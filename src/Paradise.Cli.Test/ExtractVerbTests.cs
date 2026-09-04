using Paradise.Assets.Pipeline;
using Paradise.Assets.Project;

using Zio;
using Zio.FileSystems;

namespace Paradise.Cli.Test;

/// <summary>The <c>extract</c> verb's walk: <c>--all</c> sees the tree the way every other verb does.</summary>
public class ExtractVerbTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    [Test]
    public async Task extract_all_skips_a_glb_the_manifest_ignores()
    {
        // An ignored GLB has no sidecar, so walking into it would fail the whole run with
        // "has no sidecar yet" over a file the project said is not its concern.
        using var fileSystem = new MemoryFileSystem();
        fileSystem.CreateDirectory("/game/assets/models/scratch");
        fileSystem.WriteAllText("/game/assets/project.toml", "name = \"game\"\nschema_version = 1\n[assets]\nignore = [\"models/scratch/**\"]\n");
        fileSystem.WriteAllBytes("/game/assets/models/scratch/wip.glb", [1, 2, 3]);

        var exit = Verbs.Extract(fileSystem, s_layout, "/game/assets/models", all: true, ConflictResolution.Refuse);

        await Assert.That(exit).IsEqualTo(0);
    }
}
