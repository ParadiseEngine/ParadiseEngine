using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using Paradise.Authoring;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// The resolution rule, directly: the guid decides and the path is a hint.
///
/// Every consumer in the pipeline goes through this, so the four outcomes are pinned here rather
/// than once per verb — a change that made a stale path resolve to the wrong asset would otherwise
/// surface as a baked path in a build test with nothing naming the cause.
/// </summary>
public class AssetIndexTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    [Test]
    public async Task a_reference_whose_halves_agree_is_resolved()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var guid = Material(fileSystem, "/game/assets/materials/rust.toml");

        var resolution = Index(fileSystem).Resolve(new AssetReference(guid, "materials/rust.toml"));

        await Assert.That(resolution.Status).IsEqualTo(ReferenceStatus.Resolved);
        await Assert.That(resolution.Asset).IsEqualTo(new UPath("/game/assets/materials/rust.toml"));
    }

    [Test]
    public async Task a_stale_path_resolves_by_guid_and_reports_where_the_asset_went()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var guid = Material(fileSystem, "/game/assets/materials/patina.toml");

        var resolution = Index(fileSystem).Resolve(new AssetReference(guid, "materials/rust.toml"));

        await Assert.That(resolution.Status).IsEqualTo(ReferenceStatus.Stale);
        await Assert.That(resolution.Path).IsEqualTo("materials/patina.toml");
        await Assert.That(resolution.Current.Guid).IsEqualTo(guid);
        await Assert.That(resolution.Current.Path).IsEqualTo("materials/patina.toml");
    }

    [Test]
    public async Task a_path_naming_a_different_asset_does_not_win_over_the_guid()
    {
        // The case a swapped pair of filenames produces. Resolving by path here would repoint
        // every reference at the wrong asset with nothing to see in any diff.
        using var fileSystem = ProjectVerifierTests.CreateProject();
        Material(fileSystem, "/game/assets/materials/rust.toml");
        var patina = Material(fileSystem, "/game/assets/materials/patina.toml");

        var resolution = Index(fileSystem).Resolve(new AssetReference(patina, "materials/rust.toml"));

        await Assert.That(resolution.Status).IsEqualTo(ReferenceStatus.Stale);
        await Assert.That(resolution.Asset).IsEqualTo(new UPath("/game/assets/materials/patina.toml"));
        await Assert.That(resolution.HintIdentity).IsNotEqualTo(patina);
    }

    [Test]
    public async Task a_guid_nothing_carries_is_unresolved()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        Material(fileSystem, "/game/assets/materials/rust.toml");

        var resolution = Index(fileSystem).Resolve(new AssetReference(Guid.NewGuid(), "materials/rust.toml"));

        await Assert.That(resolution.Status).IsEqualTo(ReferenceStatus.Unresolved);
        await Assert.That(resolution.Found).IsFalse();
    }

    [Test]
    public async Task a_path_naming_an_asset_with_no_sidecar_is_undetermined_rather_than_unresolved()
    {
        // Nothing can be said about the reference until that asset has an identity, and the
        // missing sidecar is already its own finding.
        using var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.CreateDirectory("/game/assets/materials");
        fileSystem.WriteAllText("/game/assets/materials/rust.toml", "a = 1\n");

        var resolution = Index(fileSystem).Resolve(new AssetReference(Guid.NewGuid(), "materials/rust.toml"));

        await Assert.That(resolution.Status).IsEqualTo(ReferenceStatus.Undetermined);
    }

    [Test]
    public async Task a_path_that_climbs_out_of_the_tree_resolves_by_guid_like_any_other_hint()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var guid = Material(fileSystem, "/game/assets/materials/rust.toml");

        var resolution = Index(fileSystem).Resolve(new AssetReference(guid, "../../etc/passwd"));

        await Assert.That(resolution.Status).IsEqualTo(ReferenceStatus.Stale);
        await Assert.That(resolution.Path).IsEqualTo("materials/rust.toml");
    }

    /// <summary>Two sidecars claiming one identity: whichever asset the ordinal scan reached first, always — an arbitrary but stable answer, while verify reports the duplicate.</summary>
    [Test]
    public async Task a_duplicated_guid_resolves_to_the_first_asset_in_scan_order()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var meta = SidecarMeta.Mint();
        fileSystem.CreateDirectory("/game/assets/materials");
        fileSystem.WriteAllText("/game/assets/materials/a.toml", "a = 1\n");
        fileSystem.WriteAllText("/game/assets/materials/b.toml", "a = 2\n");
        meta.Save(fileSystem, "/game/assets/materials/a.toml.meta");
        meta.Save(fileSystem, "/game/assets/materials/b.toml.meta");

        var resolution = Index(fileSystem).Resolve(new AssetReference(meta.Guid, "materials/b.toml"));

        await Assert.That(resolution.Asset).IsEqualTo(new UPath("/game/assets/materials/a.toml"));
    }

    [Test]
    public async Task an_ignored_file_carries_no_identity_into_the_index()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject(ignore: ["*.blend"]);
        fileSystem.CreateDirectory("/game/assets/models");
        fileSystem.WriteAllText("/game/assets/models/crate.blend", "x");
        var meta = SidecarMeta.Mint();
        meta.Save(fileSystem, "/game/assets/models/crate.blend.meta");

        var sources = AssetPaths.Scan(fileSystem, s_layout.Assets);
        var index = AssetIndex.Build(fileSystem, sources, ProjectManifest.Load(fileSystem, s_layout.Manifest).Ignore);

        await Assert.That(index.Find(meta.Guid)).IsNull();
    }

    private static AssetIndex Index(MemoryFileSystem fileSystem)
        => AssetIndex.Build(fileSystem, AssetPaths.Scan(fileSystem, s_layout.Assets));

    private static Guid Material(MemoryFileSystem fileSystem, UPath path)
    {
        ProjectVerifierTests.WriteDocument(fileSystem, path, "a = 1\n");
        return SidecarMeta.Load(fileSystem, SidecarMeta.PathFor(path)).Guid;
    }
}
