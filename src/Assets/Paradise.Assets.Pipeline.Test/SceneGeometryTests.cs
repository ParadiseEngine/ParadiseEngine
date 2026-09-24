using TUnit.Assertions.Enums;

using System.Numerics;

using Paradise.Assets.Documents;
using Paradise.Assets.Gltf.Test;
using Paradise.Authoring;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// <see cref="SceneGeometry"/>: the document-to-world-triangles walk the navmesh and collider
/// bakes share — prefab expansion, row-vector world composition matching the game loader's
/// <c>Place()</c>, the schema-driven field walk, and the mesh-document-to-triangles decode.
/// </summary>
public class SceneGeometryTests
{
    private const string RootGuid = "b0000000-0000-4000-8000-000000000001";
    private const string ChildGuid = "b0000000-0000-4000-8000-000000000002";
    private const string HolderGuid = "b0000000-0000-4000-8000-000000000003";
    private const string PrefabRootGuid = "b0000000-0000-4000-8000-000000000004";
    private const string PrefabChildGuid = "b0000000-0000-4000-8000-000000000005";
    private const string InstanceGuid = "b0000000-0000-4000-8000-000000000006";

    private static readonly Guid s_meshComponent = Guid.Parse("edee8bd8-9321-47db-819d-9bdadf010be4");

    private static PrefabComponent Transform(float x, float y, float z, float scale = 1f) =>
        new(WellKnownComponents.TransformId, WellKnownComponents.TransformType,
            new CanonicalTomlTable
            {
                { WellKnownComponents.Position, new object[] { (double)x, (double)y, (double)z } },
                { WellKnownComponents.Scale, new object[] { (double)scale, (double)scale, (double)scale } },
            });

    private static Vector3 At(SceneGeometry.Entry entry) => entry.World!.Value.Translation;

    [Test]
    public async Task a_child_composes_its_parents_placement()
    {
        var document = new PrefabDocument();
        var root = PrefabObject.WithMeta(Guid.Parse(RootGuid), "Root");
        root.Components.Add(Transform(10f, 0f, 0f, scale: 2f));
        var child = PrefabObject.WithMeta(Guid.Parse(ChildGuid), "Child", Guid.Parse(RootGuid));
        child.Components.Add(Transform(1f, 0f, 0f));
        document.Objects.Add(root);
        document.Objects.Add(child);

        var scene = SceneGeometry.Resolve(document, _ => null, []);

        // Row-vector local * parentWorld: the child's local +X lands scaled, then translated.
        await Assert.That(At(scene.Objects[1])).IsEqualTo(new Vector3(12f, 0f, 0f));
    }

    [Test]
    public async Task a_transformless_object_sits_where_its_parent_is()
    {
        // The loader's own rule: no local transform and no placed parent is UNPLACED; no local
        // transform under a placed parent is identity-local — an empty holder is somewhere.
        var document = new PrefabDocument();
        document.Objects.Add(PrefabObject.WithMeta(Guid.Parse(RootGuid), "Unplaced"));
        var child = PrefabObject.WithMeta(Guid.Parse(ChildGuid), "Child");
        child.Components.Add(Transform(1f, 0f, 0f));
        var holder = PrefabObject.WithMeta(Guid.Parse(HolderGuid), "Holder", Guid.Parse(ChildGuid));
        document.Objects.Add(child);
        document.Objects.Add(holder);

        var scene = SceneGeometry.Resolve(document, _ => null, []);

        await Assert.That(scene.Objects[0].World).IsNull();
        await Assert.That(At(scene.Objects[1])).IsEqualTo(new Vector3(1f, 0f, 0f));
        await Assert.That(At(scene.Objects[2])).IsEqualTo(new Vector3(1f, 0f, 0f));
    }

    [Test]
    public async Task an_instances_transform_places_the_prefabs_children()
    {
        var prefab = new PrefabDocument();
        var post = PrefabObject.WithMeta(Guid.Parse(PrefabRootGuid), "Post");
        post.Components.Add(Transform(0f, 0f, 0f));
        var bulb = PrefabObject.WithMeta(Guid.Parse(PrefabChildGuid), "Bulb", Guid.Parse(PrefabRootGuid));
        bulb.Components.Add(Transform(0f, 1f, 0f));
        prefab.Objects.Add(post);
        prefab.Objects.Add(bulb);

        var document = new PrefabDocument();
        var instance = PrefabObject.WithMeta(Guid.Parse(InstanceGuid), "Lamp");
        instance.Prefab = new AssetReference(Guid.NewGuid(), "prefabs/lamp.prefab");
        instance.Components.Add(Transform(5f, 0f, 0f));
        document.Objects.Add(instance);

        var errors = new List<string>();
        var scene = SceneGeometry.Resolve(document, _ => prefab, errors);

        await Assert.That(errors).IsEmpty();
        await Assert.That(scene.Objects.Count).IsEqualTo(2);
        // The instance IS the prefab's root: it keeps its own guid, the child mints a stable one.
        await Assert.That(scene.Objects[0].Object.Guid).IsEqualTo(Guid.Parse(InstanceGuid));
        await Assert.That(scene.Objects[1].Object.Guid)
            .IsEqualTo(PrefabResolver.MintChildGuid(Guid.Parse(InstanceGuid), Guid.Parse(PrefabChildGuid)));
        await Assert.That(At(scene.Objects[0])).IsEqualTo(new Vector3(5f, 0f, 0f));
        await Assert.That(At(scene.Objects[1])).IsEqualTo(new Vector3(5f, 1f, 0f));
    }

    [Test]
    public async Task a_parent_cycle_is_reported_and_its_members_unplaced()
    {
        var document = new PrefabDocument();
        var a = PrefabObject.WithMeta(Guid.Parse(RootGuid), "A", Guid.Parse(ChildGuid));
        a.Components.Add(Transform(1f, 0f, 0f));
        var b = PrefabObject.WithMeta(Guid.Parse(ChildGuid), "B", Guid.Parse(RootGuid));
        b.Components.Add(Transform(0f, 1f, 0f));
        document.Objects.Add(a);
        document.Objects.Add(b);

        var errors = new List<string>();
        var scene = SceneGeometry.Resolve(document, _ => null, errors);

        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0]).Contains("cycle");
        await Assert.That(scene.Objects[0].World).IsNull();
        await Assert.That(scene.Objects[1].World).IsNull();
    }

    [Test]
    public async Task host_kind_fields_surface_through_composition_and_arrays()
    {
        var schema = new AuthoredComponentSchema
        {
            Fields =
            [
                new AuthoredFieldSchema { Name = "Mesh", Type = AuthoredFieldTypes.String, AuthoredBy = AuthoredBySources.Mesh },
                new AuthoredFieldSchema
                {
                    Name = "Parts",
                    Type = AuthoredFieldTypes.Array,
                    Items = new AuthoredFieldSchema
                    {
                        Type = AuthoredFieldTypes.Object,
                        Fields =
                        [
                            new AuthoredFieldSchema { Name = "Mesh", Type = AuthoredFieldTypes.String, AuthoredBy = AuthoredBySources.Mesh },
                        ],
                    },
                },
                new AuthoredFieldSchema { Name = "Label", Type = AuthoredFieldTypes.String },
            ],
        };

        var mesh = new CanonicalInlineTable { { "guid", DocumentGuid.Format(s_meshComponent) }, { "path", "models/a.mesh" } };
        var data = new CanonicalTomlTable
        {
            { "Mesh", mesh },
            // An array of objects is a [[table]] on the wire — CanonicalTomlTable elements.
            { "Parts", new List<CanonicalTomlTable> { new() { { "Mesh", mesh } } } },
            { "Label", "x" },
        };

        var found = SceneGeometry.HostValues(data, schema, AuthoredBySources.Mesh).ToList();

        await Assert.That(found.Count).IsEqualTo(2);
        await Assert.That(found[0].Field.Name).IsEqualTo("Mesh");
        await Assert.That(ReferenceEquals(found[0].Value, mesh)).IsTrue();
    }

    private const string Glb = "/game/assets/models/tri.glb";
    private const string MeshDoc = "/game/assets/models/tri.mesh";

    private static byte[] TriangleGlb(float tx = 0f, float ty = 0f, float tz = 0f)
    {
        var builder = new GlbTestBuilder();
        var position = builder.AddFloatAccessor([0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f], "VEC3");
        var indices = builder.AddIndexAccessor([0, 1, 2]);
        var node = builder.AddNode(
            mesh: builder.AddMesh(GlbTestBuilder.Primitive(position, indices: indices)),
            translation: [tx, ty, tz], name: "Tri");
        builder.SetSceneRoots(node);
        return builder.Build();
    }

    private static (MemoryFileSystem FileSystem, AssetIndex Index, AssetReference Mesh) Project(byte[] glb)
    {
        var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.WriteAllBytes(Glb, glb);
        var glbGuid = ProjectVerifierTests.Mint(fileSystem, Glb);
        fileSystem.WriteAllBytes(MeshDoc, new MeshReferenceDocument(new AssetReference(glbGuid, "models/tri.glb"), MeshSlot.Mesh).WriteBytes());
        var meshGuid = ProjectVerifierTests.Mint(fileSystem, MeshDoc);
        return (fileSystem, AssetIndex.Scan(fileSystem, "/game/assets"), new AssetReference(meshGuid, "models/tri.mesh"));
    }

    [Test]
    public async Task a_mesh_document_appends_its_triangles_in_world_space()
    {
        var (fileSystem, index, mesh) = Project(TriangleGlb(tz: 3f));
        using var _ = fileSystem;
        var vertices = new List<float>();
        var indices = new List<int>();
        var errors = new List<string>();

        var appended = SceneGeometry.AppendMeshTriangles(
            fileSystem, index, mesh, Matrix4x4.CreateTranslation(10f, 0f, 0f), vertices, indices, errors);

        await Assert.That(appended).IsTrue();
        await Assert.That(errors).IsEmpty();
        // GLB node translation composes under the object's world.
        await Assert.That(vertices.ToArray())
            .IsEquivalentTo(new[] { 10f, 0f, 3f, 11f, 0f, 3f, 10f, 1f, 3f }, CollectionOrdering.Matching);
        await Assert.That(indices.ToArray()).IsEquivalentTo(new[] { 0, 1, 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task a_mirrored_world_flips_winding()
    {
        var (fileSystem, index, mesh) = Project(TriangleGlb());
        using var _ = fileSystem;
        var vertices = new List<float>();
        var indices = new List<int>();
        var errors = new List<string>();

        SceneGeometry.AppendMeshTriangles(
            fileSystem, index, mesh, Matrix4x4.CreateScale(-1f, 1f, 1f), vertices, indices, errors);

        await Assert.That(indices.ToArray()).IsEquivalentTo(new[] { 0, 2, 1 }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task incomplete_primitives_cannot_form_triangles_across_primitive_boundaries(bool mirrored)
    {
        var builder = new GlbTestBuilder();
        var positions = builder.AddFloatAccessor([0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f], "VEC3");
        var first = builder.AddIndexAccessor([0, 1, 2, 0]);
        var second = builder.AddIndexAccessor([1, 2]);
        var node = builder.AddNode(mesh: builder.AddMesh(
            GlbTestBuilder.Primitive(positions, indices: first),
            GlbTestBuilder.Primitive(positions, indices: second)));
        builder.SetSceneRoots(node);
        var (fileSystem, index, mesh) = Project(builder.Build());
        using var _ = fileSystem;
        var errors = new List<string>();
        var vertices = new List<float>();
        var indices = new List<int>();

        var appended = SceneGeometry.AppendMeshTriangles(fileSystem, index, mesh,
            mirrored ? Matrix4x4.CreateScale(-1f, 1f, 1f) : Matrix4x4.Identity,
            vertices, indices, errors);

        await Assert.That(appended).IsFalse();
        await Assert.That(errors.Single()).Contains("tri.mesh");
        await Assert.That(errors.Single()).Contains("incomplete triangles");
        await Assert.That(vertices).IsEmpty();
        await Assert.That(indices).IsEmpty();
    }

    [Test]
    public async Task an_unresolvable_mesh_is_an_error_naming_the_reference()
    {
        var (fileSystem, index, _) = Project(TriangleGlb());
        using var _ = fileSystem;
        var missing = new AssetReference(Guid.NewGuid(), "models/gone.mesh");
        var errors = new List<string>();

        var appended = SceneGeometry.AppendMeshTriangles(
            fileSystem, index, missing, Matrix4x4.Identity, [], [], errors);

        await Assert.That(appended).IsFalse();
        await Assert.That(errors.Single()).Contains("models/gone.mesh");
    }

    [Test]
    public async Task a_skeleton_document_carries_no_triangles()
    {
        var fileSystem = ProjectVerifierTests.CreateProject();
        using var _ = fileSystem;
        fileSystem.WriteAllBytes(Glb, TriangleGlb());
        var glbGuid = ProjectVerifierTests.Mint(fileSystem, Glb);
        fileSystem.WriteAllBytes("/game/assets/models/tri.skeleton",
            new MeshReferenceDocument(new AssetReference(glbGuid, "models/tri.glb"), MeshSlot.Skeleton).WriteBytes());
        var skeletonGuid = ProjectVerifierTests.Mint(fileSystem, "/game/assets/models/tri.skeleton");
        var index = AssetIndex.Scan(fileSystem, "/game/assets");
        var errors = new List<string>();

        var appended = SceneGeometry.AppendMeshTriangles(
            fileSystem, index, new AssetReference(skeletonGuid, "models/tri.skeleton"),
            Matrix4x4.Identity, [], [], errors);

        await Assert.That(appended).IsFalse();
        await Assert.That(errors.Single()).Contains("no triangles");
    }

    [Test]
    public async Task load_expands_prefab_instances_through_the_index()
    {
        var fileSystem = ProjectVerifierTests.CreateProject();
        using var _ = fileSystem;

        fileSystem.CreateDirectory("/game/assets/prefabs");
        var prefab = new PrefabDocument();
        var post = PrefabObject.WithMeta(Guid.Parse(PrefabRootGuid), "Post");
        post.Components.Add(Transform(0f, 0f, 0f));
        var bulb = PrefabObject.WithMeta(Guid.Parse(PrefabChildGuid), "Bulb", Guid.Parse(PrefabRootGuid));
        bulb.Components.Add(Transform(0f, 1f, 0f));
        prefab.Objects.Add(post);
        prefab.Objects.Add(bulb);
        PrefabDocumentSerializer.Save(fileSystem, "/game/assets/prefabs/lamp.prefab", prefab);
        var prefabGuid = ProjectVerifierTests.Mint(fileSystem, "/game/assets/prefabs/lamp.prefab");

        var level = new PrefabDocument();
        var instance = PrefabObject.WithMeta(Guid.Parse(InstanceGuid), "Lamp");
        instance.Prefab = new AssetReference(prefabGuid, "prefabs/lamp.prefab");
        instance.Components.Add(Transform(5f, 0f, 0f));
        level.Objects.Add(instance);
        PrefabDocumentSerializer.Save(fileSystem, "/game/assets/levels/level.prefab", level);
        ProjectVerifierTests.Mint(fileSystem, "/game/assets/levels/level.prefab");

        var errors = new List<string>();
        var scene = SceneGeometry.Load(
            fileSystem, AssetIndex.Scan(fileSystem, "/game/assets"), "/game/assets/levels/level.prefab", errors);

        await Assert.That(errors).IsEmpty();
        await Assert.That(scene.Objects.Count).IsEqualTo(2);
        await Assert.That(At(scene.Objects[1])).IsEqualTo(new Vector3(5f, 1f, 0f));
    }

    [Test]
    public async Task nested_prefabs_compose_child_transform_overrides_before_world_placement()
    {
        var innerId = Guid.NewGuid();
        var outerId = Guid.NewGuid();
        var inner = new PrefabDocument();
        var post = PrefabObject.WithMeta(Guid.Parse(PrefabRootGuid), "Post");
        post.Components.Add(Transform(0, 0, 0));
        var child = PrefabObject.WithMeta(Guid.Parse(PrefabChildGuid), "Child", post.Guid);
        child.Components.Add(Transform(0, 1, 0));
        inner.Objects.Add(post);
        inner.Objects.Add(child);
        var outer = new PrefabDocument();
        var holder = PrefabObject.WithMeta(Guid.Parse(HolderGuid), "Holder");
        holder.Components.Add(Transform(2, 0, 0));
        outer.Objects.Add(holder);
        var nested = PrefabObject.WithMeta(Guid.Parse(ChildGuid), "Nested", holder.Guid);
        nested.Prefab = new AssetReference(innerId, "inner.prefab");
        nested.Components.Add(Transform(3, 0, 0));
        outer.Objects.Add(nested);
        var carrier = new PrefabObject();
        carrier.Components.Add(new PrefabComponent(WellKnownComponents.MetaId, WellKnownComponents.MetaType,
            new CanonicalTomlTable { { WellKnownComponents.Parent, ChildGuid }, { WellKnownComponents.Target, PrefabChildGuid } }));
        carrier.Components.Add(Transform(0, 4, 0));
        outer.Objects.Add(carrier);
        var level = new PrefabDocument();
        var instance = PrefabObject.WithMeta(Guid.Parse(InstanceGuid), "Scene instance");
        instance.Prefab = new AssetReference(outerId, "outer.prefab");
        instance.Components.Add(Transform(10, 0, 0, scale: 2));
        level.Objects.Add(instance);
        var errors = new List<string>();

        var scene = SceneGeometry.Resolve(level, reference => reference.Guid == innerId ? inner : outer, errors);

        await Assert.That(errors).IsEmpty();
        await Assert.That(At(scene.Objects.Single(entry => entry.Object.Name == "Child")))
            .IsEqualTo(new Vector3(16, 8, 0));
    }

    [Test]
    public async Task a_mesh_document_matches_the_cookers_whole_glb_instance_semantics()
    {
        var builder = new GlbTestBuilder();
        var position = builder.AddFloatAccessor([0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f], "VEC3");
        var indices = builder.AddIndexAccessor([0, 1, 2]);
        var meshIndex = builder.AddMesh(GlbTestBuilder.Primitive(position, indices: indices));
        var first = builder.AddNode(mesh: meshIndex, translation: [2, 0, 0]);
        var second = builder.AddNode(mesh: meshIndex, translation: [5, 0, 0]);
        builder.SetSceneRoots(first, second);
        var (fs, index, mesh) = Project(builder.Build());
        using var _ = fs;
        var vertices = new List<float>();
        var triangles = new List<int>();
        var errors = new List<string>();

        SceneGeometry.AppendMeshTriangles(fs, index, mesh, Matrix4x4.Identity, vertices, triangles, errors);

        await Assert.That(errors).IsEmpty();
        await Assert.That(vertices.Count).IsEqualTo(18);
        await Assert.That(triangles.Count).IsEqualTo(6);
        await Assert.That(vertices[0]).IsEqualTo(2f);
        await Assert.That(vertices[9]).IsEqualTo(5f);
    }
}
