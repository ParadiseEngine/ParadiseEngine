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
        await Assert.That(graph.DependentsOf(crate)).IsEquivalentTo(new[] { edge });
        await Assert.That(graph.DependenciesOf(level)).IsEquivalentTo(new[] { edge });
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

        await Assert.That(graph.DependentsOf(rust).Select(e => e.Referrer)).IsEquivalentTo(new[] { crate });
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
        });
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
    public async Task dependent_files_are_listed_once_however_many_references_they_hold()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var crate = Asset(fileSystem, "/game/assets/models/crate.glb");
        Level(fileSystem, "/game/assets/levels/district.prefab",
            new AssetReference(crate, "models/crate.glb"), new AssetReference(crate, "models/crate.glb"));

        var graph = Graph(fileSystem);

        await Assert.That(graph.DependentsOf(crate).Count).IsEqualTo(2);
        await Assert.That(graph.DependentFilesOf(crate)).IsEquivalentTo(new UPath[] { "/game/assets/levels/district.prefab" });
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
