using System.Text.Json;

using TUnit.Assertions.Enums;

using Zio;
using Zio.FileSystems;

namespace Paradise.Cli.Test;

public class NavMeshCommandTests
{
    private const string Geometry = """
        {"vertices":[[-10,0,-10],[-10,0,10],[10,0,10],[10,0,-10]],"indices":[0,1,2,0,2,3],"settings":{}}
        """;

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task bake_and_preview_work_without_an_asset_project_and_replace_complete_outputs(bool existing)
    {
        using var fileSystem = GeometryFiles();
        if (existing)
        {
            fileSystem.CreateDirectory("/output");
            fileSystem.WriteAllText("/output/level.navmesh", "old binary");
            fileSystem.WriteAllText("/output/preview.json", "old preview");
        }

        await Assert.That(NavMeshCommands.Bake(fileSystem, "/geometry.json", "/output/level.navmesh", "/output/preview.json"))
            .IsEqualTo(0);
        var bytes = fileSystem.ReadAllBytes("/output/level.navmesh");
        await Assert.That(bytes.Length).IsGreaterThan(100);
        await Assert.That(NavMeshCommands.Preview(fileSystem, "/output/level.navmesh", "/output/reloaded.json"))
            .IsEqualTo(0);
        await Assert.That(fileSystem.ReadAllBytes("/output/level.navmesh")).IsEquivalentTo(bytes, CollectionOrdering.Matching);
        await Assert.That(fileSystem.ReadAllText("/geometry.json")).IsEqualTo(Geometry);
        await Assert.That(fileSystem.ReadAllText("/output/reloaded.json")).IsEqualTo(fileSystem.ReadAllText("/output/preview.json"));
        using var preview = JsonDocument.Parse(fileSystem.ReadAllText("/output/preview.json"));
        await Assert.That(preview.RootElement.GetProperty("vertices").GetArrayLength()).IsGreaterThan(2);
        await Assert.That(preview.RootElement.GetProperty("indices").GetArrayLength()).IsGreaterThan(2);
        await Assert.That(fileSystem.EnumerateFiles("/output", "*", SearchOption.AllDirectories).Count()).IsEqualTo(3);
    }

    [Test]
    public async Task bake_without_preview_only_writes_the_binary()
    {
        using var fileSystem = GeometryFiles();

        await Assert.That(NavMeshCommands.Bake(fileSystem, "/geometry.json", "/level.navmesh")).IsEqualTo(0);
        await Assert.That(fileSystem.EnumerateFiles("/", "*", SearchOption.AllDirectories).Count()).IsEqualTo(2);
    }

    [Test]
    [Arguments("{malformed")]
    [Arguments("{\"vertices\":[],\"indices\":[]}")]
    [Arguments("{\"vertices\":[[0,0,0],[0,0,20],[20,0,20]],\"indices\":[0,1,50]}")]
    [Arguments("{\"vertices\":[[-10,0,-10],[-10,0,10],[10,0,10],[10,0,-10]],\"indices\":[0,2,1,0,3,2]}")]
    public async Task invalid_input_or_failed_bake_preserves_existing_outputs(string json)
    {
        using var fileSystem = GeometryFiles();
        fileSystem.WriteAllText("/geometry.json", json);
        fileSystem.WriteAllText("/level.navmesh", "old binary");
        fileSystem.WriteAllText("/preview.json", "old preview");
        var errors = new List<string>();

        var exit = NavMeshCommands.Bake(fileSystem, "/geometry.json", "/level.navmesh", "/preview.json", errors.Add);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(errors.Single()).Contains("bake-navmesh '/geometry.json':");
        await Assert.That(fileSystem.ReadAllText("/level.navmesh")).IsEqualTo("old binary");
        await Assert.That(fileSystem.ReadAllText("/preview.json")).IsEqualTo("old preview");
        await Assert.That(fileSystem.EnumerateFiles("/", "*", SearchOption.AllDirectories).Count()).IsEqualTo(3);
    }

    [Test]
    public async Task missing_input_does_not_create_output_directories()
    {
        using var fileSystem = new MemoryFileSystem();
        var errors = new List<string>();

        var exit = NavMeshCommands.Bake(fileSystem, "/missing.json", "/output/level.navmesh", error: errors.Add);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(errors.Single()).Contains("/missing.json");
        await Assert.That(fileSystem.DirectoryExists("/output")).IsFalse();
    }

    [Test]
    [Arguments("/input.navmesh", "/input.navmesh", "/preview.json")]
    [Arguments("/input.navmesh", "/level.navmesh", "/input.navmesh")]
    [Arguments("/input.navmesh", "/level.navmesh", "/level.navmesh")]
    [Arguments("/input.navmesh", "/LEVEL.navmesh", "/level.navmesh")]
    [Arguments("/input.navmesh", "/folder/../input.navmesh", "/preview.json")]
    public async Task colliding_bake_paths_are_rejected_before_reading_or_writing(string input, string output, string preview)
    {
        using var fileSystem = new MemoryFileSystem();
        var errors = new List<string>();

        var exit = NavMeshCommands.Bake(fileSystem, input, output, new UPath(preview), errors.Add);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(errors.Single()).Contains("paths must be distinct");
        await Assert.That(fileSystem.EnumerateFiles("/", "*", SearchOption.AllDirectories)).IsEmpty();
    }

    [Test]
    public async Task preview_cannot_overwrite_its_binary_input()
    {
        using var fileSystem = new MemoryFileSystem();
        fileSystem.WriteAllText("/level.navmesh", "existing binary");
        var errors = new List<string>();

        await Assert.That(NavMeshCommands.Preview(fileSystem, "/level.navmesh", "/level.navmesh", errors.Add)).IsEqualTo(1);
        await Assert.That(errors.Single()).Contains("paths must be distinct");
        await Assert.That(fileSystem.ReadAllText("/level.navmesh")).IsEqualTo("existing binary");
    }

    [Test]
    [Arguments("/level.navmesh.bin")]
    [Arguments("/level.bin")]
    [Arguments("/level.navmesh.tmp")]
    public async Task binary_paths_require_the_canonical_suffix(string binary)
    {
        using var fileSystem = GeometryFiles();
        var errors = new List<string>();

        await Assert.That(NavMeshCommands.Bake(fileSystem, "/geometry.json", binary, error: errors.Add)).IsEqualTo(1);
        await Assert.That(NavMeshCommands.Preview(fileSystem, binary, "/preview.json", errors.Add)).IsEqualTo(1);
        await Assert.That(errors.All(message => message.Contains("must end in '.navmesh'", StringComparison.Ordinal))).IsTrue();
        await Assert.That(fileSystem.EnumerateFiles("/", "*", SearchOption.AllDirectories).Count()).IsEqualTo(1);
    }

    [Test]
    public async Task corrupt_binary_preserves_the_previous_preview()
    {
        using var fileSystem = new MemoryFileSystem();
        fileSystem.WriteAllText("/level.navmesh", "broken MeshSet");
        fileSystem.WriteAllText("/preview.json", "old preview");
        var errors = new List<string>();

        await Assert.That(NavMeshCommands.Preview(fileSystem, "/level.navmesh", "/preview.json", errors.Add)).IsEqualTo(1);
        await Assert.That(errors.Single()).Contains("preview-navmesh '/level.navmesh':");
        await Assert.That(fileSystem.ReadAllText("/preview.json")).IsEqualTo("old preview");
        await Assert.That(fileSystem.EnumerateFiles("/", "*", SearchOption.AllDirectories).Count()).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task failed_preview_staging_or_commit_preserves_the_binary_and_removes_temporary_files(bool refuseCommit)
    {
        using var fileSystem = new RefusingPreviewFileSystem(refuseCommit);
        fileSystem.WriteAllText("/geometry.json", Geometry);
        fileSystem.WriteAllText("/level.navmesh", "old binary");
        fileSystem.CreateDirectory("/preview");
        fileSystem.WriteAllText("/preview/view.json", "old preview");
        var errors = new List<string>();

        var exit = NavMeshCommands.Bake(fileSystem, "/geometry.json", "/level.navmesh", "/preview/view.json", errors.Add);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(errors.Single()).Contains("preview refused");
        await Assert.That(fileSystem.ReadAllText("/level.navmesh")).IsEqualTo("old binary");
        await Assert.That(fileSystem.ReadAllText("/preview/view.json")).IsEqualTo("old preview");
        await Assert.That(fileSystem.EnumerateFiles("/", "*", SearchOption.AllDirectories).Count()).IsEqualTo(3);
    }

    [Test]
    public async Task cli_parses_both_commands_without_project_discovery()
    {
        var root = Path.Combine(Path.GetTempPath(), $"paradise-navmesh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "geometry.json");
            var output = Path.Combine(root, "level.navmesh");
            var preview = Path.Combine(root, "preview.json");
            var reloaded = Path.Combine(root, "reloaded.json");
            File.WriteAllText(input, Geometry);

            await Assert.That(BuildHost.Run(["assets", "bake-navmesh", "--input", input, "--output", output, "--preview", preview]))
                .IsEqualTo(0);
            await Assert.That(BuildHost.Run(["assets", "preview-navmesh", "--input", output, "--output", reloaded]))
                .IsEqualTo(0);
            await Assert.That(File.ReadAllText(reloaded)).IsEqualTo(File.ReadAllText(preview));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [Arguments("bake-navmesh", "--input")]
    [Arguments("bake-navmesh", "--output")]
    [Arguments("bake-navmesh", "--preview")]
    [Arguments("bake-navmesh", "--project")]
    [Arguments("preview-navmesh", "--preview")]
    public async Task incomplete_or_unsupported_options_are_usage_errors(string verb, string option)
    {
        await Assert.That(BuildHost.Run(["assets", verb, option])).IsEqualTo(2);
    }

    [Test]
    public async Task repeated_options_are_usage_errors()
    {
        await Assert.That(BuildHost.Run(["assets", "bake-navmesh", "--input", "one.json", "--input", "two.json", "--output", "level.navmesh"]))
            .IsEqualTo(2);
    }

    private static MemoryFileSystem GeometryFiles()
    {
        var fileSystem = new MemoryFileSystem();
        fileSystem.WriteAllText("/geometry.json", Geometry);
        return fileSystem;
    }

    private sealed class RefusingPreviewFileSystem(bool refuseCommit) : MemoryFileSystem
    {
        protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
        {
            if (!refuseCommit && path.GetDirectory() == new UPath("/preview") && mode == FileMode.CreateNew)
                throw new IOException("preview refused during staging");
            return base.OpenFileImpl(path, mode, access, share);
        }

        protected override void ReplaceFileImpl(UPath srcPath, UPath destPath, UPath destBackupPath, bool ignoreMetadataErrors)
        {
            if (refuseCommit && destPath == new UPath("/preview/view.json"))
                throw new IOException("preview refused during commit");
            base.ReplaceFileImpl(srcPath, destPath, destBackupPath, ignoreMetadataErrors);
        }
    }
}
