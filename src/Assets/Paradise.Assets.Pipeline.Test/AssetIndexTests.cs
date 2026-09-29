using TUnit.Assertions.Enums;

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

    [Test]
    public async Task a_path_climbing_above_the_root_with_a_guid_nobody_carries_resolves_to_no_place_at_all()
    {
        // The path half cannot even be combined onto the root (an absolute path merely resolves
        // to itself, which Problem reports as outside assets/), so there is nothing to name;
        // consumers check Found before opening Asset rather than throwing on a null path.
        using var fileSystem = ProjectVerifierTests.CreateProject();
        Material(fileSystem, "/game/assets/materials/rust.toml");

        var resolution = Index(fileSystem).Resolve(new AssetReference(Guid.NewGuid(), "../../../etc/passwd"));

        await Assert.That(resolution.Status).IsEqualTo(ReferenceStatus.Unresolved);
        await Assert.That(resolution.Found).IsFalse();
        await Assert.That(resolution.Asset.IsNull).IsTrue();
    }

    /// <summary>Two sidecars claiming one identity: whichever asset the ordinal scan reached first, always — an arbitrary but stable answer, while verify reports the duplicate.</summary>
    [Test]
    public async Task a_duplicated_guid_resolves_to_the_first_asset_in_scan_order()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var meta = SidecarMeta.Mint();
        meta.Importer = "config";
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

        var index = AssetIndex.Scan(
            fileSystem, s_layout.Assets, ProjectManifest.Load(fileSystem, s_layout.Manifest).Ignore);

        await Assert.That(index.Find(meta.Guid)).IsNull();
    }

    [Test]
    public async Task refreshing_a_sidecar_replaces_its_owners_identity()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        UPath path = "/game/assets/materials/rust.toml";
        var previous = Material(fileSystem, path);
        var index = Index(fileSystem);
        var replacement = ProjectVerifierTests.Mint(fileSystem, path);

        index.Refresh(fileSystem, SidecarMeta.PathFor(path));

        await Assert.That(index.Find(previous)).IsNull();
        await Assert.That(index.PathsOf(previous)).IsEmpty();
        await Assert.That(index.Find(replacement)).IsEqualTo((UPath?)path);
        await Assert.That(index.IdentityOf(path)).IsEqualTo((Guid?)replacement);
    }

    [Test]
    public async Task refreshing_additions_and_deletions_keeps_ordinal_inventory_and_orphan_sidecars()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var index = Index(fileSystem);
        UPath path = "/game/assets/materials/rust.toml";
        var guid = Material(fileSystem, path);
        var sidecar = SidecarMeta.PathFor(path);

        index.Refresh(fileSystem, path);

        await Assert.That(index.Find(guid)).IsEqualTo((UPath?)path);
        await Assert.That(index.Contains(sidecar)).IsTrue();
        await Assert.That(index.Files).IsEquivalentTo(
            fileSystem.EnumerateFiles(s_layout.Assets, "*", SearchOption.AllDirectories).OrderBy(file => file.FullName, StringComparer.Ordinal),
            CollectionOrdering.Matching);

        fileSystem.DeleteFile(path);
        index.Refresh(fileSystem, sidecar);

        await Assert.That(index.Contains(path)).IsFalse();
        await Assert.That(index.Contains(sidecar)).IsTrue();
        await Assert.That(index.Find(guid)).IsNull();
        await Assert.That(index.Resolve(new AssetReference(guid, "materials/rust.toml")).Status).IsEqualTo(ReferenceStatus.Unresolved);
    }

    [Test]
    public async Task removing_a_sidecar_is_authoritative_and_makes_the_owner_undetermined()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        UPath path = "/game/assets/materials/rust.toml";
        var guid = Material(fileSystem, path);
        var index = Index(fileSystem);
        var sidecar = SidecarMeta.PathFor(path);

        // The old spelling may still pass FileExists on a case-insensitive mount after a move.
        index.Remove(sidecar);

        await Assert.That(index.Contains(sidecar)).IsFalse();
        await Assert.That(index.Contains(path)).IsTrue();
        await Assert.That(index.Find(guid)).IsNull();
        await Assert.That(index.Resolve(new AssetReference(guid, "materials/rust.toml")).Status).IsEqualTo(ReferenceStatus.Undetermined);

        index.Refresh(fileSystem, sidecar);
        await Assert.That(index.Find(guid)).IsEqualTo((UPath?)path);

        index.Remove(path);
        await Assert.That(index.Contains(path)).IsFalse();
        await Assert.That(index.Contains(sidecar)).IsTrue();
        await Assert.That(index.Resolve(new AssetReference(guid, "materials/rust.toml")).Status).IsEqualTo(ReferenceStatus.Unresolved);
    }

    [Test]
    public async Task duplicate_guid_winners_follow_ordinal_order_and_fall_back_after_identity_changes_or_removal()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        UPath first = "/game/assets/materials/a.toml";
        UPath second = "/game/assets/materials/b.toml";
        UPath third = "/game/assets/materials/c.toml";
        var guid = Material(fileSystem, second);
        Material(fileSystem, third);
        ProjectVerifierTests.Mint(fileSystem, third, guid);
        var index = Index(fileSystem);
        Material(fileSystem, first);
        ProjectVerifierTests.Mint(fileSystem, first, guid);

        index.Refresh(fileSystem, first);
        await Assert.That(index.PathsOf(guid)).IsEquivalentTo(new[] { first, second, third }, CollectionOrdering.Matching);
        await Assert.That(index.Find(guid)).IsEqualTo((UPath?)first);

        ProjectVerifierTests.Mint(fileSystem, first);
        index.Refresh(fileSystem, first);
        await Assert.That(index.Find(guid)).IsEqualTo((UPath?)second);

        index.Remove(second);
        await Assert.That(index.Find(guid)).IsEqualTo((UPath?)third);
        await Assert.That(index.PathsOf(guid)).IsEquivalentTo(new[] { third }, CollectionOrdering.Matching);

        index.Remove(third);
        await Assert.That(index.Find(guid)).IsNull();
        await Assert.That(index.PathsOf(guid)).IsEmpty();
    }

    [Test]
    [Arguments("/game/assets/materials/patina.toml")]
    [Arguments("/game/assets/materials/Rust.toml")]
    public async Task a_rename_replaces_old_exact_paths_and_keeps_identity(string destination)
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        UPath original = "/game/assets/materials/rust.toml";
        UPath moved = destination;
        var guid = Material(fileSystem, original);
        var index = Index(fileSystem);
        fileSystem.MoveFile(original, moved);
        fileSystem.MoveFile(SidecarMeta.PathFor(original), SidecarMeta.PathFor(moved));

        index.Remove(original);
        index.Remove(SidecarMeta.PathFor(original));
        index.Refresh(fileSystem, moved);

        await Assert.That(index.Contains(original)).IsFalse();
        await Assert.That(index.Contains(SidecarMeta.PathFor(original))).IsFalse();
        await Assert.That(index.Contains(moved)).IsTrue();
        await Assert.That(index.Contains(SidecarMeta.PathFor(moved))).IsTrue();
        await Assert.That(index.Find(guid)).IsEqualTo((UPath?)moved);
        await Assert.That(index.Resolve(new AssetReference(guid, "materials/rust.toml")).Path).IsEqualTo(index.Relative(moved));
    }

    [Test]
    public async Task case_folded_diagnostics_fall_back_when_the_first_spelling_is_removed()
    {
        using var fileSystem = new ZipArchiveFileSystem(new MemoryStream(), System.IO.Compression.ZipArchiveMode.Update, isCaseSensitive: true);
        UPath upper = "/game/assets/materials/Rust.toml";
        UPath lower = "/game/assets/materials/rust.toml";
        fileSystem.CreateDirectory(upper.GetDirectory());
        fileSystem.WriteAllText(upper, "a = 1");
        fileSystem.WriteAllText(lower, "a = 2");
        var index = AssetIndex.Scan(fileSystem, s_layout.Assets);

        index.Remove(upper);

        await Assert.That(index.TryFindIgnoringCase("/game/assets/materials/RUST.toml", out var actual)).IsTrue();
        await Assert.That(actual).IsEqualTo(lower);
        await Assert.That(index.Contains(upper)).IsFalse();
        await Assert.That(index.Contains(lower)).IsTrue();
    }

    [Test]
    public async Task malformed_and_missing_sidecars_clear_identity_until_a_valid_refresh()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        UPath path = "/game/assets/materials/rust.toml";
        var guid = Material(fileSystem, path);
        var index = Index(fileSystem);
        var sidecar = SidecarMeta.PathFor(path);
        fileSystem.WriteAllText(sidecar, "not valid toml = =");

        index.Refresh(fileSystem, path);
        await Assert.That(index.Find(guid)).IsNull();
        await Assert.That(index.Resolve(new AssetReference(guid, "materials/rust.toml")).Status).IsEqualTo(ReferenceStatus.Undetermined);

        ProjectVerifierTests.Mint(fileSystem, path, guid);
        index.Refresh(fileSystem, path);
        await Assert.That(index.Resolve(new AssetReference(guid, "materials/rust.toml")).Status).IsEqualTo(ReferenceStatus.Resolved);

        fileSystem.DeleteFile(sidecar);
        index.Refresh(fileSystem, path);
        await Assert.That(index.Contains(sidecar)).IsFalse();
        await Assert.That(index.Resolve(new AssetReference(guid, "materials/rust.toml")).Status).IsEqualTo(ReferenceStatus.Undetermined);
    }

    [Test]
    public async Task refresh_retains_ignore_rules_and_cannot_admit_files_outside_the_index()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject(ignore: ["*.toml"]);
        UPath path = "/game/assets/materials/rust.toml";
        var guid = Material(fileSystem, path);
        var ignore = ProjectManifest.Load(fileSystem, s_layout.Manifest).Ignore;
        var index = AssetIndex.Scan(fileSystem, s_layout.Assets, ignore);
        UPath outside = "/game/outside.toml";
        fileSystem.WriteAllText(outside, "a = 1");
        ProjectVerifierTests.Mint(fileSystem, outside, guid);

        index.Refresh(fileSystem, path);
        index.Refresh(fileSystem, outside);
        index.Remove(SidecarMeta.PathFor(path));

        await Assert.That(index.IsIgnored(path)).IsTrue();
        await Assert.That(index.Contains(outside)).IsFalse();
        await Assert.That(index.Find(guid)).IsNull();
        await Assert.That(index.Resolve(new AssetReference(guid, "materials/rust.toml")).Status).IsEqualTo(ReferenceStatus.Unresolved);

        index.Refresh(fileSystem, path, AssetIgnoreRules.None);
        await Assert.That(index.IsIgnored(path)).IsFalse();
        await Assert.That(index.Find(guid)).IsEqualTo((UPath?)path);
    }

    [Test]
    public async Task refreshing_one_asset_does_not_enumerate_or_read_unrelated_assets()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        UPath changed = "/game/assets/materials/rust.toml";
        UPath untouched = "/game/assets/materials/grass.toml";
        Material(fileSystem, changed);
        var untouchedGuid = Material(fileSystem, untouched);
        var index = Index(fileSystem);
        var changedGuid = ProjectVerifierTests.Mint(fileSystem, changed);
        using var targeted = new TargetedFileSystem(fileSystem, SidecarMeta.PathFor(changed));

        index.Refresh(targeted, changed);

        await Assert.That(index.Find(changedGuid)).IsEqualTo((UPath?)changed);
        await Assert.That(index.Find(untouchedGuid)).IsEqualTo((UPath?)untouched);
    }

    private sealed class TargetedFileSystem(IFileSystem fallback, UPath allowed) : ComposeFileSystem(fallback, owned: false)
    {
        protected override UPath ConvertPathToDelegate(UPath path) => path;
        protected override UPath ConvertPathFromDelegate(UPath path) => path;

        protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
        {
            if (path != allowed) throw new InvalidOperationException($"Unrelated read: {path}");
            return base.OpenFileImpl(path, mode, access, share);
        }

        protected override IEnumerable<UPath> EnumeratePathsImpl(UPath path, string searchPattern, SearchOption searchOption, SearchTarget searchTarget)
            => throw new InvalidOperationException("An incremental refresh must not enumerate the tree.");
    }

    private static AssetIndex Index(MemoryFileSystem fileSystem)
        => AssetIndex.Scan(fileSystem, s_layout.Assets);

    private static Guid Material(MemoryFileSystem fileSystem, UPath path)
    {
        ProjectVerifierTests.WriteDocument(fileSystem, path, "a = 1\n");
        return SidecarMeta.Load(fileSystem, SidecarMeta.PathFor(path)).Guid;
    }
}
