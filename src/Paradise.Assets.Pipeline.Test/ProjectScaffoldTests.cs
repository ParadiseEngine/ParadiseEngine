using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// What <c>paradise new</c> produces.
///
/// The first two tests ARE the specification: a scaffold that does not verify, or does not build,
/// is a directory of plausible-looking files rather than a project. Everything else here pins a
/// detail that would make the sample worse without breaking either.
/// </summary>
public class ProjectScaffoldTests
{
    private static readonly UPath s_root = "/game";

    private static MemoryFileSystem Scaffold()
    {
        var fileSystem = new MemoryFileSystem();
        ProjectScaffold.Create(fileSystem, s_root, "demo");
        return fileSystem;
    }

    private static AssetProjectLayout Layout(MemoryFileSystem fileSystem)
        => AssetProjectLayout.Locate(fileSystem, s_root);

    [Test]
    public async Task the_scaffolded_project_verifies_clean()
    {
        using var fileSystem = Scaffold();

        var findings = ProjectVerifier.Verify(fileSystem, Layout(fileSystem));

        // Warnings are allowed to exist in principle; errors are not, and neither is a warning
        // here, because every file the scaffold writes is one it also understands.
        await Assert.That(findings.Select(f => f.ToString()).ToArray()).IsEmpty();
    }

    [Test]
    public async Task the_scaffolded_project_builds()
    {
        using var fileSystem = Scaffold();

        // encoder: null on purpose -- the sample ships no textures, and a build that needed one
        // would fail here rather than quietly passing behind a fake.
        var result = new BuildRunner(fileSystem, Layout(fileSystem), encoder: null).Run("dev");

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(result.Succeeded).IsTrue();

        // The level reaches the runtime's format, which is the point of scaffolding a project
        // rather than an assets folder. `dev` is named because naming no profile is a DIFFERENT
        // request — see the test below — and it is what `paradise new` prints as the first step.
        await Assert.That(fileSystem.FileExists("/game/build/levels/main.json")).IsTrue();
    }

    [Test]
    public async Task naming_no_profile_builds_the_defaults_rather_than_dev()
    {
        // `dev` is not privileged, so an absent --profile is the built-in defaults (TOML), not the
        // scaffolded JSON profile. The scaffold must still build cleanly that way — the whole
        // sample goes through the TOML bake — and the extension is how the two requests differ.
        using var fileSystem = Scaffold();

        var result = new BuildRunner(fileSystem, Layout(fileSystem), encoder: null).Run();

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/build/levels/main.toml")).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/build/levels/main.json")).IsFalse();
    }

    [Test]
    public async Task every_asset_has_a_sidecar_without_a_hash()
    {
        using var fileSystem = Scaffold();
        var layout = Layout(fileSystem);

        foreach (var path in fileSystem.EnumerateFiles(layout.Assets, "*", SearchOption.AllDirectories))
        {
            if (SidecarMeta.IsSidecarPath(path)) continue;

            var sidecar = SidecarMeta.PathFor(path);
            await Assert.That(fileSystem.FileExists(sidecar)).IsTrue();

            var meta = SidecarMeta.Load(fileSystem, sidecar);
            await Assert.That(meta.Hash).IsNull();
        }
    }

    [Test]
    public async Task the_sample_level_instantiates_the_cube_prefab()
    {
        using var fileSystem = Scaffold();

        var level = PrefabDocumentSerializer.Load(fileSystem, "/game/assets/levels/main.prefab");
        var instances = level.Objects.Where(o => o.Prefab is not null).ToList();

        // Three of them, sharing one prefab and differing only in transform -- the argument for
        // prefabs, stated in the sample rather than in a comment.
        await Assert.That(instances.Count).IsEqualTo(3);
        await Assert.That(instances.Select(o => o.Prefab!.Path).Distinct().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task the_generated_cube_is_a_readable_unit_cube()
    {
        using var fileSystem = Scaffold();

        var glb = fileSystem.ReadAllBytes("/game/assets/Models/cube.glb");
        await Assert.That(GlbBinary.TryRead(glb, out var gltf, out var bin)).IsTrue();
        await Assert.That(bin.Length).IsGreaterThan(0);

        // POSITION's min/max are what every bounds consumer reads, and a cube that is not 1x1x1
        // would make the sample's scales mean something other than metres.
        var accessor = gltf["accessors"]!.AsArray()[0]!;
        await Assert.That(accessor["min"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray())
            .IsEquivalentTo(new[] { -0.5, -0.5, -0.5 });
        await Assert.That(accessor["max"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray())
            .IsEquivalentTo(new[] { 0.5, 0.5, 0.5 });
    }

    [Test]
    public async Task scaffolding_over_a_non_empty_directory_is_refused()
    {
        using var fileSystem = new MemoryFileSystem();
        fileSystem.CreateDirectory(s_root);
        fileSystem.WriteAllText(s_root / "README.md", "mine");

        var error = Assert.Throws<IOException>(() => ProjectScaffold.Create(fileSystem, s_root, "demo"));

        await Assert.That(error!.Message).Contains("not empty");
    }

    /// <summary>The engine ships no ignore list; the scaffold seeds one the project then owns.</summary>
    [Test]
    public async Task the_manifest_seeds_an_ignore_list_the_project_owns()
    {
        using var fileSystem = Scaffold();

        var manifest = ProjectManifest.Load(fileSystem, s_root / "assets" / "project.toml");

        await Assert.That(manifest.Ignore.Patterns).Contains(".DS_Store");
        await Assert.That(manifest.Ignore.Patterns).Contains("*.blend1");
        await Assert.That(manifest.Ignore.Matches(s_root / "assets", s_root / "assets" / "props" / "crate.blend1")).IsTrue();
    }
}
