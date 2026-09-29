using TUnit.Assertions.Enums;
using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using Paradise.Authoring;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// Who references what, by identity. Every verb that follows a move or refuses a delete asks
/// this, so the shape of an edge and what survives a dangling target are pinned here.
/// </summary>
public class ReferenceGraphTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    [Test]
    public async Task a_document_reference_is_an_edge_from_the_document_to_the_asset()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var crate = Asset(fileSystem, "/game/assets/models/crate.glb");
        var level = Level(fileSystem, "/game/assets/levels/district.prefab", new AssetReference(crate, "models/crate.glb"));

        var graph = Graph(fileSystem);

        await Assert.That(graph.Edges.Count).IsEqualTo(1);
        var edge = graph.Edges[0];
        await Assert.That(edge.Referrer).IsEqualTo(level);
        await Assert.That(edge.ReferrerPath).IsEqualTo(new UPath("/game/assets/levels/district.prefab"));
        await Assert.That(edge.Target).IsEqualTo(crate);
        await Assert.That(edge.Where).IsEqualTo("game.Mesh.Mesh");
        await Assert.That(edge.Path).IsEqualTo("models/crate.glb");
        await Assert.That(graph.DependentsOf(crate)).IsEquivalentTo(new[] { edge }, CollectionOrdering.Matching);
        await Assert.That(graph.DependenciesOf(level)).IsEquivalentTo(new[] { edge }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task a_reference_to_an_identity_nobody_carries_is_kept_so_it_can_be_named()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var gone = Guid.NewGuid();
        Level(fileSystem, "/game/assets/levels/district.prefab", new AssetReference(gone, "models/crate.glb"));

        var graph = Graph(fileSystem);

        await Assert.That(graph.DependentsOf(gone).Count).IsEqualTo(1);
        await Assert.That(graph.DependentsOf(gone)[0].Path).IsEqualTo("models/crate.glb");
    }

    [Test]
    public async Task a_recorded_glb_image_is_an_edge_from_the_mesh_to_the_texture()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var rust = Asset(fileSystem, "/game/assets/textures/rust.png");
        var crate = Asset(fileSystem, "/game/assets/models/crate.glb", MeshContainerTests.Glb("""{"images":[{"uri":"../textures/rust.png"},{"uri":"../textures/other.png"}]}"""));
        MeshReferencesTests.Record(fileSystem, "/game/assets/models/crate.glb", "images[0]", "../textures/rust.png", new AssetReference(rust, "textures/rust.png"));

        var graph = Graph(fileSystem);

        // Only the recorded image: a uri with no entry names no identity to draw an edge to.
        await Assert.That(graph.Edges.Count).IsEqualTo(1);
        await Assert.That(graph.Edges[0].Referrer).IsEqualTo(crate);
        await Assert.That(graph.Edges[0].Target).IsEqualTo(rust);
        await Assert.That(graph.Edges[0].Where).IsEqualTo("images[0]");
    }

    [Test]
    public async Task transitive_dependents_climb_through_a_prefab_to_the_level_that_instances_it()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var rust = Asset(fileSystem, "/game/assets/textures/rust.png");
        var crate = Asset(fileSystem, "/game/assets/models/crate.glb", MeshContainerTests.Glb("""{"images":[{"uri":"../textures/rust.png"}]}"""));
        MeshReferencesTests.Record(fileSystem, "/game/assets/models/crate.glb", "images[0]", "../textures/rust.png", new AssetReference(rust, "textures/rust.png"));
        var box = Level(fileSystem, "/game/assets/prefabs/box.prefab", new AssetReference(crate, "models/crate.glb"));
        var level = Level(fileSystem, "/game/assets/levels/district.prefab", new AssetReference(box, "prefabs/box.prefab"));

        var graph = Graph(fileSystem);

        await Assert.That(graph.DependentsOf(rust).Select(e => e.Referrer)).IsEquivalentTo(new[] { crate }, CollectionOrdering.Matching);
        // Unordered deliberately: TransitiveDependentsOf returns an IReadOnlySet, built by a
        // stack walk over a HashSet. It is a set by type and by construction; there is no order.
        await Assert.That(graph.TransitiveDependentsOf(rust)).IsEquivalentTo(new[] { crate, box, level });
        await Assert.That(graph.TransitiveDependentsOf(level)).IsEmpty();
    }

    [Test]
    public async Task a_document_that_will_not_parse_or_has_no_identity_is_unreadable_not_silent()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.CreateDirectory("/game/assets/levels");
        fileSystem.WriteAllText("/game/assets/levels/broken.prefab", "this is not toml = = =");
        ProjectVerifierTests.MintDocumentSidecar(fileSystem, "/game/assets/levels/broken.prefab");
        fileSystem.WriteAllText("/game/assets/levels/orphan.prefab", "schema_version = 1\n");

        var graph = Graph(fileSystem);

        await Assert.That(graph.Edges).IsEmpty();
        await Assert.That(graph.Unreadable).IsEquivalentTo(new UPath[]
        {
            "/game/assets/levels/broken.prefab", "/game/assets/levels/orphan.prefab",
        }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ignored_files_are_neither_edges_nor_unreadable_nor_rewritten()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject(ignore: ["scratch/**"]);
        var crate = Asset(fileSystem, "/game/assets/models/crate.glb");
        fileSystem.CreateDirectory("/game/assets/scratch");
        fileSystem.WriteAllBytes("/game/assets/scratch/old.png", [1]);
        var draft = new PrefabDocument();
        var root = PrefabObject.WithMeta(Guid.NewGuid(), "draft");
        root.Components.Add(new PrefabComponent(Guid.NewGuid(), "game.Mesh", new CanonicalTomlTable { { "Mesh", AssetReferenceCodec.Write(new AssetReference(crate, "models/OLD.glb")) } }));
        draft.Objects.Add(root);
        PrefabDocumentSerializer.Save(fileSystem, "/game/assets/scratch/draft.prefab", draft);
        var before = fileSystem.ReadAllText("/game/assets/scratch/draft.prefab");
        var ignore = ProjectManifest.Load(fileSystem, s_layout.Manifest).Ignore;
        var index = AssetIndex.Scan(fileSystem, s_layout.Assets, ignore);

        var graph = ReferenceGraph.Build(fileSystem, s_layout, index, ignore);
        ReferenceRepair.Fix(fileSystem, s_layout, index);

        await Assert.That(graph.Unreadable).IsEmpty();
        await Assert.That(graph.PathOnly).IsEmpty();
        await Assert.That(graph.DependentsOf(crate)).IsEmpty();
        await Assert.That(fileSystem.ReadAllText("/game/assets/scratch/draft.prefab")).IsEqualTo(before);
    }

    [Test]
    public async Task a_material_is_an_edge_from_the_document_to_the_texture_it_samples()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var grass = Asset(fileSystem, "/game/assets/textures/grass.png");
        ProjectVerifierTests.WriteDocument(fileSystem, "/game/assets/materials/grass.material",
            $"BaseColorTexture = {{ guid = \"{grass}\", path = \"textures/grass.png\" }}\nNormalTexture = {{}}\n");
        var material = SidecarMeta.Load(fileSystem, "/game/assets/materials/grass.material.meta").Guid;

        var graph = Graph(fileSystem);

        await Assert.That(graph.DependentsOf(grass).Count).IsEqualTo(1);
        await Assert.That(graph.DependentsOf(grass)[0].Referrer).IsEqualTo(material);
        await Assert.That(graph.DependentsOf(grass)[0].Where).IsEqualTo("BaseColorTexture");
        await Assert.That(graph.Unreadable).IsEmpty();
    }

    [Test]
    public async Task dependent_files_are_listed_once_however_many_references_they_hold()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var crate = Asset(fileSystem, "/game/assets/models/crate.glb");
        Level(fileSystem, "/game/assets/levels/district.prefab",
            new AssetReference(crate, "models/crate.glb"), new AssetReference(crate, "models/crate.glb"));

        var graph = Graph(fileSystem);

        await Assert.That(graph.DependentsOf(crate).Count).IsEqualTo(2);
        await Assert.That(graph.DependentFilesOf(crate)).IsEquivalentTo(new UPath[] { "/game/assets/levels/district.prefab" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task refreshing_a_document_replaces_reverse_edges_without_reading_other_documents()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var first = Asset(fileSystem, "/game/assets/textures/first.png");
        var second = Asset(fileSystem, "/game/assets/textures/second.png");
        UPath changed = "/game/assets/levels/changed.prefab";
        UPath untouched = "/game/assets/levels/untouched.prefab";
        var referrer = Level(fileSystem, changed, new AssetReference(first, "textures/first.png"));
        var other = Level(fileSystem, untouched, new AssetReference(first, "textures/first.png"));
        var index = AssetIndex.Scan(fileSystem, s_layout.Assets);
        var graph = ReferenceGraph.Build(fileSystem, s_layout, index);
        Level(fileSystem, changed, new AssetReference(second, "textures/second.png"));
        ProjectVerifierTests.Mint(fileSystem, changed, referrer);
        using var targeted = new TargetedFileSystem(fileSystem, changed);
        index.Refresh(targeted, changed);

        graph.Refresh(targeted, s_layout, index, changed);

        await Assert.That(graph.DependentsOf(first).Select(edge => edge.Referrer)).IsEquivalentTo(new[] { other });
        await Assert.That(graph.DependentsOf(second).Select(edge => edge.Referrer)).IsEquivalentTo(new[] { referrer });
        await Assert.That(graph.DependenciesOf(referrer).Select(edge => edge.Target)).IsEquivalentTo(new[] { second });
        await Assert.That(graph.Edges.Select(edge => edge.ReferrerPath)).IsEquivalentTo(new[] { changed, untouched }, CollectionOrdering.Matching);

        // A repeated notification replaces, rather than duplicates, the same outgoing edge.
        graph.Refresh(targeted, s_layout, index, SidecarMeta.PathFor(changed));
        await Assert.That(graph.DependentsOf(second).Select(edge => edge.Referrer)).IsEquivalentTo(new[] { referrer });
    }

    [Test]
    public async Task removing_a_target_keeps_dangling_incoming_edges_until_the_referrer_is_removed()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        UPath target = "/game/assets/textures/rust.png";
        UPath document = "/game/assets/levels/district.prefab";
        var targetGuid = Asset(fileSystem, target);
        var referrer = Level(fileSystem, document, new AssetReference(targetGuid, "textures/rust.png"));
        var index = AssetIndex.Scan(fileSystem, s_layout.Assets);
        var graph = ReferenceGraph.Build(fileSystem, s_layout, index);
        fileSystem.DeleteFile(target);
        index.Remove(target);

        graph.Remove(target);

        await Assert.That(graph.DependentsOf(targetGuid).Select(edge => edge.Referrer)).IsEquivalentTo(new[] { referrer });
        await Assert.That(graph.DependentsOf(targetGuid)[0].Path).IsEqualTo("textures/rust.png");

        fileSystem.DeleteFile(document);
        index.Remove(document);
        graph.Refresh(fileSystem, s_layout, index, document);

        await Assert.That(graph.DependentsOf(targetGuid)).IsEmpty();
        await Assert.That(graph.DependenciesOf(referrer)).IsEmpty();
        await Assert.That(graph.Edges).IsEmpty();
        await Assert.That(graph.Unreadable).IsEmpty();
    }

    [Test]
    [Arguments("/game/assets/levels/moved.prefab")]
    [Arguments("/game/assets/levels/District.prefab")]
    public async Task renamed_referrers_and_changed_guids_leave_no_edges_at_the_old_path_or_identity(string destination)
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var target = Asset(fileSystem, "/game/assets/textures/rust.png");
        UPath original = "/game/assets/levels/district.prefab";
        UPath moved = destination;
        var referrer = Level(fileSystem, original, new AssetReference(target, "textures/rust.png"));
        var index = AssetIndex.Scan(fileSystem, s_layout.Assets);
        var graph = ReferenceGraph.Build(fileSystem, s_layout, index);
        fileSystem.MoveFile(original, moved);
        fileSystem.MoveFile(SidecarMeta.PathFor(original), SidecarMeta.PathFor(moved));
        index.Remove(original);
        index.Remove(SidecarMeta.PathFor(original));
        index.Refresh(fileSystem, moved);

        graph.Remove(original);
        graph.Refresh(fileSystem, s_layout, index, moved);

        await Assert.That(graph.DependentFilesOf(target)).IsEquivalentTo(new[] { moved }, CollectionOrdering.Matching);
        await Assert.That(graph.DependenciesOf(referrer).Select(edge => edge.ReferrerPath)).IsEquivalentTo(new[] { moved });

        var replacement = ProjectVerifierTests.Mint(fileSystem, moved);
        index.Refresh(fileSystem, moved);
        graph.Refresh(fileSystem, s_layout, index, SidecarMeta.PathFor(moved));

        await Assert.That(graph.DependenciesOf(referrer)).IsEmpty();
        await Assert.That(graph.DependenciesOf(replacement).Select(edge => edge.Target)).IsEquivalentTo(new[] { target });
        await Assert.That(graph.DependentsOf(target).Select(edge => edge.Referrer)).IsEquivalentTo(new[] { replacement });
    }

    [Test]
    public async Task removing_one_duplicate_guid_referrer_does_not_remove_the_others_edges()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var target = Asset(fileSystem, "/game/assets/textures/rust.png");
        UPath first = "/game/assets/levels/first.prefab";
        UPath second = "/game/assets/levels/second.prefab";
        var referrer = Level(fileSystem, first, new AssetReference(target, "textures/rust.png"));
        Level(fileSystem, second, new AssetReference(target, "textures/rust.png"));
        ProjectVerifierTests.Mint(fileSystem, second, referrer);
        var graph = Graph(fileSystem);

        graph.Remove(first);

        await Assert.That(graph.DependentFilesOf(target)).IsEquivalentTo(new[] { second });
        await Assert.That(graph.DependenciesOf(referrer).Select(edge => edge.ReferrerPath)).IsEquivalentTo(new[] { second });
        await Assert.That(graph.Edges.Select(edge => edge.ReferrerPath)).IsEquivalentTo(new[] { second });
    }

    [Test]
    public async Task malformed_documents_replace_old_edges_with_unreadable_state_and_recover_when_valid()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var target = Asset(fileSystem, "/game/assets/textures/rust.png");
        UPath document = "/game/assets/levels/district.prefab";
        var referrer = Level(fileSystem, document, new AssetReference(target, "textures/rust.png"));
        var content = fileSystem.ReadAllText(document);
        var index = AssetIndex.Scan(fileSystem, s_layout.Assets);
        var graph = ReferenceGraph.Build(fileSystem, s_layout, index);
        fileSystem.WriteAllText(document, "not toml = =");

        graph.Refresh(fileSystem, s_layout, index, document);

        await Assert.That(graph.Unreadable).IsEquivalentTo(new[] { document });
        await Assert.That(graph.DependentsOf(target)).IsEmpty();
        await Assert.That(graph.DependenciesOf(referrer)).IsEmpty();
        await Assert.That(graph.PathOnly).IsEmpty();

        fileSystem.WriteAllText(document, content);
        graph.Refresh(fileSystem, s_layout, index, document);

        await Assert.That(graph.Unreadable).IsEmpty();
        await Assert.That(graph.DependentsOf(target).Select(edge => edge.Referrer)).IsEquivalentTo(new[] { referrer });

        index.Remove(SidecarMeta.PathFor(document));
        graph.Refresh(fileSystem, s_layout, index, document);
        await Assert.That(graph.Unreadable).IsEquivalentTo(new[] { document });
        await Assert.That(graph.DependentsOf(target)).IsEmpty();

        index.Refresh(fileSystem, document);
        graph.Refresh(fileSystem, s_layout, index, document);
        await Assert.That(graph.Unreadable).IsEmpty();
        await Assert.That(graph.DependentsOf(target).Select(edge => edge.Referrer)).IsEquivalentTo(new[] { referrer });
    }

    [Test]
    public async Task path_only_sites_are_replaced_when_recorded_changed_or_removed()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var target = Asset(fileSystem, "/game/assets/textures/rust.png");
        UPath model = "/game/assets/models/crate.glb";
        var referrer = Asset(fileSystem, model, MeshContainerTests.Glb("""{"images":[{"uri":"../textures/rust.png"}]}"""));
        var index = AssetIndex.Scan(fileSystem, s_layout.Assets);
        var graph = ReferenceGraph.Build(fileSystem, s_layout, index);
        await Assert.That(graph.PathOnly.Select(entry => entry.Site.Hint)).IsEquivalentTo(new string?[] { "textures/rust.png" });
        MeshReferencesTests.Record(fileSystem, model, "images[0]", "../textures/rust.png", new AssetReference(target, "textures/rust.png"));

        graph.Refresh(fileSystem, s_layout, index, SidecarMeta.PathFor(model));

        await Assert.That(graph.PathOnly).IsEmpty();
        await Assert.That(graph.DependentsOf(target).Select(edge => edge.Referrer)).IsEquivalentTo(new[] { referrer });

        fileSystem.WriteAllBytes(model, MeshContainerTests.Glb("""{"images":[{"uri":"../textures/new.png"}]}"""));
        graph.Refresh(fileSystem, s_layout, index, model);
        await Assert.That(graph.DependentsOf(target)).IsEmpty();
        await Assert.That(graph.PathOnly.Select(entry => entry.Site.Hint)).IsEquivalentTo(new string?[] { "textures/new.png" });

        graph.Remove(model);
        await Assert.That(graph.PathOnly).IsEmpty();
    }

    [Test]
    public async Task a_document_read_race_is_unreadable_until_the_next_successful_refresh()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var target = Asset(fileSystem, "/game/assets/models/crate.glb");
        UPath document = "/game/assets/models/crate.mesh";
        ProjectVerifierTests.WriteDocument(fileSystem, document,
            new MeshReferenceDocument(new AssetReference(target, "models/crate.glb"), MeshSlot.Mesh).Write());
        var referrer = SidecarMeta.Load(fileSystem, SidecarMeta.PathFor(document)).Guid;
        var index = AssetIndex.Scan(fileSystem, s_layout.Assets);
        var graph = ReferenceGraph.Build(fileSystem, s_layout, index);
        using var racing = new TargetedFileSystem(fileSystem, document) { Unreadable = true };

        graph.Refresh(racing, s_layout, index, document);

        await Assert.That(graph.Unreadable).Contains(document);
        await Assert.That(graph.DependentsOf(target)).IsEmpty();

        racing.Unreadable = false;
        graph.Refresh(racing, s_layout, index, document);

        await Assert.That(graph.Unreadable).DoesNotContain(document);
        await Assert.That(graph.DependentsOf(target).Select(edge => edge.Referrer)).IsEquivalentTo(new[] { referrer });
    }

    private sealed class TargetedFileSystem(IFileSystem fallback, UPath asset) : ComposeFileSystem(fallback, owned: false)
    {
        public bool Unreadable { get; set; }

        protected override UPath ConvertPathToDelegate(UPath path) => path;
        protected override UPath ConvertPathFromDelegate(UPath path) => path;

        protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
        {
            if (path != asset && path != SidecarMeta.PathFor(asset)) throw new InvalidOperationException($"Unrelated read: {path}");
            if (Unreadable && path == asset) throw new IOException("The document is being edited.");
            return base.OpenFileImpl(path, mode, access, share);
        }

        protected override IEnumerable<UPath> EnumeratePathsImpl(UPath path, string searchPattern, SearchOption searchOption, SearchTarget searchTarget)
            => throw new InvalidOperationException("An incremental refresh must not enumerate the tree.");
    }

    private static ReferenceGraph Graph(MemoryFileSystem fileSystem)
        => ReferenceGraph.Build(fileSystem, s_layout, AssetIndex.Scan(fileSystem, s_layout.Assets));

    private static Guid Asset(MemoryFileSystem fileSystem, UPath path, byte[]? bytes = null)
    {
        fileSystem.CreateDirectory(path.GetDirectory());
        fileSystem.WriteAllBytes(path, bytes ?? [1]);
        return ProjectVerifierTests.Mint(fileSystem, path);
    }

    /// <summary>A document with one object whose <c>game.Mesh</c> component holds the references, one field per reference.</summary>
    private static Guid Level(MemoryFileSystem fileSystem, UPath path, params AssetReference[] references)
    {
        var root = PrefabObject.WithMeta(Guid.NewGuid(), "object");
        var data = new CanonicalTomlTable();
        for (var i = 0; i < references.Length; i++)
        {
            data.Add(i == 0 ? "Mesh" : $"Mesh{i}", AssetReferenceCodec.Write(references[i]));
        }

        root.Components.Add(new PrefabComponent(Guid.NewGuid(), "game.Mesh", data));
        var document = new PrefabDocument();
        document.Objects.Add(root);
        fileSystem.CreateDirectory(path.GetDirectory());
        PrefabDocumentSerializer.Save(fileSystem, path, document);
        return ProjectVerifierTests.Mint(fileSystem, path);
    }
}
