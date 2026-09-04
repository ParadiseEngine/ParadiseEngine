using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using Paradise.Authoring;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// <c>rm</c> refuses what something still points at, and forced, it says exactly what it broke.
/// It never edits a document to make a dangling reference disappear.
/// </summary>
public class AssetRemoverTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    [Test]
    public async Task an_unreferenced_asset_goes_with_its_sidecar()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        Asset(fileSystem, "/game/assets/models/crate.glb");

        var result = AssetRemover.Remove(fileSystem, s_layout, "/game/assets/models/crate.glb");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Removed).IsEquivalentTo(new[] { "models/crate.glb" });
        await Assert.That(fileSystem.FileExists("/game/assets/models/crate.glb")).IsFalse();
        await Assert.That(fileSystem.FileExists("/game/assets/models/crate.glb.meta")).IsFalse();
    }

    [Test]
    public async Task a_referenced_asset_is_refused_naming_every_reference()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var crate = Asset(fileSystem, "/game/assets/models/crate.glb");
        Level(fileSystem, "/game/assets/levels/district.prefab", new AssetReference(crate, "models/crate.glb"));

        var result = AssetRemover.Remove(fileSystem, s_layout, "/game/assets/models/crate.glb");

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("still referenced by 1 file(s)");
        await Assert.That(result.Dangling.Count).IsEqualTo(1);
        await Assert.That(result.Dangling[0].ReferrerPath).IsEqualTo(new UPath("/game/assets/levels/district.prefab"));
        await Assert.That(fileSystem.FileExists("/game/assets/models/crate.glb")).IsTrue();
    }

    [Test]
    public async Task forced_it_deletes_and_leaves_the_references_for_verify()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var crate = Asset(fileSystem, "/game/assets/models/crate.glb");
        Level(fileSystem, "/game/assets/levels/district.prefab", new AssetReference(crate, "models/crate.glb"));
        var before = fileSystem.ReadAllText("/game/assets/levels/district.prefab");

        var result = AssetRemover.Remove(fileSystem, s_layout, "/game/assets/models/crate.glb", force: true);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Dangling.Count).IsEqualTo(1);
        await Assert.That(fileSystem.FileExists("/game/assets/models/crate.glb")).IsFalse();
        // The evidence stays: verify names the reference, the author decides what it becomes.
        await Assert.That(fileSystem.ReadAllText("/game/assets/levels/district.prefab")).IsEqualTo(before);
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout).Where(f => f.Severity == VerifySeverity.Error).Count()).IsEqualTo(1);
    }

    [Test]
    public async Task a_directory_whose_files_only_reference_each_other_is_removable()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var crate = Asset(fileSystem, "/game/assets/props/models/crate.glb");
        Level(fileSystem, "/game/assets/props/box.prefab", new AssetReference(crate, "props/models/crate.glb"));

        var result = AssetRemover.Remove(fileSystem, s_layout, "/game/assets/props");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Dangling).IsEmpty();
        await Assert.That(fileSystem.DirectoryExists("/game/assets/props")).IsFalse();
    }

    [Test]
    public async Task a_directory_referenced_from_outside_is_refused()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var crate = Asset(fileSystem, "/game/assets/props/crate.glb");
        Level(fileSystem, "/game/assets/levels/district.prefab", new AssetReference(crate, "props/crate.glb"));

        var result = AssetRemover.Remove(fileSystem, s_layout, "/game/assets/props");

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(fileSystem.DirectoryExists("/game/assets/props")).IsTrue();
    }

    [Test]
    public async Task a_dry_run_removes_nothing_and_still_reports()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var crate = Asset(fileSystem, "/game/assets/models/crate.glb");
        Level(fileSystem, "/game/assets/levels/district.prefab", new AssetReference(crate, "models/crate.glb"));

        var result = AssetRemover.Remove(fileSystem, s_layout, "/game/assets/models/crate.glb", force: true, dryRun: true);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Removed).IsEquivalentTo(new[] { "models/crate.glb" });
        await Assert.That(result.Dangling.Count).IsEqualTo(1);
        await Assert.That(fileSystem.FileExists("/game/assets/models/crate.glb")).IsTrue();
    }

    [Test]
    [Arguments("/game/assets", "cannot be removed")]
    [Arguments("/game/assets/project.toml", "stays where it is")]
    [Arguments("/game/assets/models/crate.glb.meta", "is a sidecar")]
    [Arguments("/game/elsewhere.glb", "not under")]
    [Arguments("/game/assets/models/missing.glb", "does not exist")]
    public async Task what_rm_refuses_it_refuses_before_touching_anything(string target, string reason)
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        Asset(fileSystem, "/game/assets/models/crate.glb");

        var result = AssetRemover.Remove(fileSystem, s_layout, target, force: true);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains(reason);
        await Assert.That(fileSystem.FileExists("/game/assets/models/crate.glb")).IsTrue();
    }

    private static Guid Asset(MemoryFileSystem fileSystem, UPath path)
    {
        fileSystem.CreateDirectory(path.GetDirectory());
        fileSystem.WriteAllBytes(path, [1]);
        var meta = SidecarMeta.Mint();
        meta.Save(fileSystem, SidecarMeta.PathFor(path));
        return meta.Guid;
    }

    private static void Level(MemoryFileSystem fileSystem, UPath path, AssetReference reference)
    {
        var root = PrefabObject.WithMeta(Guid.NewGuid(), "object");
        root.Components.Add(new PrefabComponent(Guid.NewGuid(), "game.Mesh", new CanonicalTomlTable { { "Mesh", AssetReferenceCodec.Write(reference) } }));
        var document = new PrefabDocument();
        document.Objects.Add(root);
        fileSystem.CreateDirectory(path.GetDirectory());
        PrefabDocumentSerializer.Save(fileSystem, path, document);
        SidecarMeta.Mint().Save(fileSystem, SidecarMeta.PathFor(path));
    }
}
