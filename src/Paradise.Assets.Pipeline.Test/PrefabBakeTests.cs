using System.Numerics;
using System.Text.Json;

using Paradise.Assets.Documents;
using Paradise.Export.Data;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// The v6 bake: a passthrough, not a flatten. Identity, hierarchy and local TRS survive into the
/// contract; what the bake still consumes is prefab structure (instances, override carriers).
/// </summary>
public class PrefabBakeTests
{
    private static readonly Guid RootGuid = new("3f2a1b4c-5d6e-4f70-8192-a3b4c5d6e7f8");
    private static readonly Guid ChildGuid = new("9a8b7c6d-5e4f-4031-8213-4c5d6e7f8091");

    private static PrefabDocument Scene()
    {
        var document = new PrefabDocument();
        var root = PrefabObject.WithMeta(RootGuid, "district");
        root.Components.Add(LocalTransformCodec.Write(LocalTransform.Identity));
        document.Objects.Add(root);

        var child = PrefabObject.WithMeta(ChildGuid, "crate", RootGuid);
        child.Components.Add(LocalTransformCodec.Write(
            new LocalTransform(new Vector3(1.5f, 0f, -2f), Quaternion.Identity, Vector3.One)));
        document.Objects.Add(child);
        return document;
    }

    private static PrefabData Bake(PrefabDocument document, List<string>? errors = null)
        => PrefabBake.Bake(document, _ => null, ".json", errors ?? new List<string>());

    private static JsonElement Payload(IReadOnlyList<AuthoredComponentData> entity, Guid id)
        => entity.Single(component => component.Id == id).Data;

    /// <summary>inf and nan are legal TOML with no JSON spelling; the bake reports them on the error list, naming the object and component, rather than throwing out of Import.</summary>
    [Test]
    public async Task a_non_finite_float_is_an_error_naming_the_component_not_an_exception()
    {
        var document = Scene();
        document.Objects[1].Components.Add(new PrefabComponent(
            new Guid("7c1d2e3f-4a5b-4c6d-8e7f-90a1b2c3d4e5"), "Game.Health",
            new CanonicalTomlTable { { "Regen", double.PositiveInfinity } }));
        var errors = new List<string>();

        var level = Bake(document, errors);

        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0]).Contains("crate");
        await Assert.That(errors[0]).Contains("Game.Health");
        await Assert.That(errors[0]).Contains("inf");
        await Assert.That(errors[0]).Contains("Regen");
        await Assert.That(level.Entities.Count).IsEqualTo(2);
    }

    [Test]
    public async Task meta_and_transform_pass_through_with_identity_and_hierarchy()
    {
        var level = Bake(Scene());

        await Assert.That(level.Entities.Count).IsEqualTo(2);

        var childMeta = Payload(level.Entities[1], WellKnownEntityComponents.MetaId);
        await Assert.That(childMeta.GetProperty(WellKnownEntityComponents.Guid).GetString())
            .IsEqualTo(DocumentGuid.Format(ChildGuid));
        await Assert.That(childMeta.GetProperty(WellKnownEntityComponents.Name).GetString())
            .IsEqualTo("crate");
        await Assert.That(childMeta.GetProperty(WellKnownEntityComponents.Parent).GetString())
            .IsEqualTo(DocumentGuid.Format(RootGuid));

        var childTransform = Payload(level.Entities[1], WellKnownEntityComponents.TransformId);
        var position = childTransform.GetProperty(WellKnownEntityComponents.Position);
        await Assert.That(position.GetArrayLength()).IsEqualTo(3);
        await Assert.That(position[0].GetSingle()).IsEqualTo(1.5f);
        await Assert.That(childTransform.GetProperty(WellKnownEntityComponents.Rotation).GetArrayLength())
            .IsEqualTo(4);
        await Assert.That(childTransform.GetProperty(WellKnownEntityComponents.Scale).GetArrayLength())
            .IsEqualTo(3);
    }

    /// <summary>The bake makes no presence judgement: ANY object may omit its transform —
    /// parented or not — and nothing is synthesized. What an absent transform means (identity
    /// local, unplaced camera, an error) is the loader's rule, because since v6 the engine is
    /// never the party that gives a payload meaning.</summary>
    [Test]
    public async Task any_object_may_omit_its_transform_and_nothing_is_synthesized()
    {
        var document = new PrefabDocument();
        var root = PrefabObject.WithMeta(RootGuid, "viewpoint");
        document.Objects.Add(root);
        document.Objects.Add(PrefabObject.WithMeta(ChildGuid, "attachment", RootGuid));

        var errors = new List<string>();
        var level = Bake(document, errors);

        await Assert.That(errors).IsEmpty();
        foreach (var entity in level.Entities)
        {
            await Assert.That(entity.Any(component => component.Id == WellKnownEntityComponents.TransformId))
                .IsFalse();
        }
    }

    /// <summary>The bake consumes prefab structure: resolved output never carries the
    /// carrier-only meta fields, which describe overrides rather than objects.</summary>
    [Test]
    public async Task resolved_output_never_carries_target_or_dropped()
    {
        var prefab = Scene();

        var scene = new PrefabDocument();
        var instance = PrefabObject.WithMeta(new Guid("11111111-2222-4333-8444-555555555555"), "district_01");
        instance.Prefab = new Paradise.Authoring.AssetReference(Guid.NewGuid(), "prefabs/district.prefab");
        instance.Components.Add(LocalTransformCodec.Write(LocalTransform.Identity));
        scene.Objects.Add(instance);

        var errors = new List<string>();
        var level = PrefabBake.Bake(scene, _ => prefab, ".json", errors);

        await Assert.That(errors).IsEmpty();
        foreach (var entity in level.Entities)
        {
            var meta = Payload(entity, WellKnownEntityComponents.MetaId);
            await Assert.That(meta.TryGetProperty("Target", out _)).IsFalse();
            await Assert.That(meta.TryGetProperty("Dropped", out _)).IsFalse();
        }
    }

    /// <summary>Instances still resolve at build: the expanded child arrives as a plain entity
    /// whose payloads went through the same passthrough as everything else.</summary>
    [Test]
    public async Task a_prefab_instance_resolves_into_passthrough_entities()
    {
        var prefab = Scene();
        var instanceGuid = new Guid("11111111-2222-4333-8444-555555555555");

        var scene = new PrefabDocument();
        var instance = PrefabObject.WithMeta(instanceGuid, "district_01");
        instance.Prefab = new Paradise.Authoring.AssetReference(Guid.NewGuid(), "prefabs/district.prefab");
        instance.Components.Add(LocalTransformCodec.Write(LocalTransform.Identity));
        scene.Objects.Add(instance);

        var level = PrefabBake.Bake(scene, _ => prefab, ".json", new List<string>());

        await Assert.That(level.Entities.Count).IsEqualTo(2);
        var root = Payload(level.Entities[0], WellKnownEntityComponents.MetaId);
        await Assert.That(root.GetProperty(WellKnownEntityComponents.Guid).GetString())
            .IsEqualTo(DocumentGuid.Format(instanceGuid));
        var child = Payload(level.Entities[1], WellKnownEntityComponents.MetaId);
        await Assert.That(child.GetProperty(WellKnownEntityComponents.Parent).GetString())
            .IsEqualTo(DocumentGuid.Format(instanceGuid));
    }

    /// <summary>
    /// A reference the bake flattens must name where the build actually PUT the asset — for both
    /// authored extensions, not just <c>.toml</c>.
    /// </summary>
    /// <remarks>
    /// A <c>.prefab</c> in payload position is not an instance: it is a component holding a
    /// document to load later (a spawner naming what it spawns), so the bake never expands it and
    /// the path is all the runtime gets. The document importer compiles that file to the profile's
    /// format, so a path left saying <c>.prefab</c> names a file no build wrote.
    /// </remarks>
    [Test]
    public async Task a_referenced_document_is_repointed_at_its_built_form()
    {
        var document = new PrefabDocument();
        var root = PrefabObject.WithMeta(RootGuid, "spawner");
        root.Components.Add(new PrefabComponent(
            new Guid("7c1d2e3f-4a5b-4c6d-8e7f-90a1b2c3d4e5"), "game.Spawner", new CanonicalTomlTable
            {
                { "Spawns", AssetReferenceCodec.Write(new Paradise.Authoring.AssetReference(ChildGuid, "prefabs/crate.prefab")) },
                { "Material", AssetReferenceCodec.Write(new Paradise.Authoring.AssetReference(ChildGuid, "materials/rust.toml")) },
                { "Mesh", AssetReferenceCodec.Write(new Paradise.Authoring.AssetReference(ChildGuid, "models/crate.glb")) },
            }));
        document.Objects.Add(root);

        var payload = Payload(Bake(document).Entities[0], new Guid("7c1d2e3f-4a5b-4c6d-8e7f-90a1b2c3d4e5"));

        // Both authored extensions become the profile's; a carried-through mesh keeps its own.
        await Assert.That(payload.GetProperty("Spawns").GetString()).IsEqualTo("prefabs/crate.json");
        await Assert.That(payload.GetProperty("Material").GetString()).IsEqualTo("materials/rust.json");
        await Assert.That(payload.GetProperty("Mesh").GetString()).IsEqualTo("models/crate.glb");
    }

    /// <summary>
    /// A build-time delegate that has no built path for a reference FAILS the bake at that site.
    /// The authored path never stands in for a built one: it is a hint a rename leaves stale, and a
    /// document that shipped it would name a file the build did not write, with no error anywhere.
    /// </summary>
    [Test]
    public async Task a_reference_nothing_is_built_for_fails_the_bake_naming_the_site()
    {
        var document = new PrefabDocument();
        var root = PrefabObject.WithMeta(RootGuid, "spawner");
        root.Components.Add(new PrefabComponent(
            new Guid("7c1d2e3f-4a5b-4c6d-8e7f-90a1b2c3d4e5"), "game.Spawner", new CanonicalTomlTable
            {
                { "Mesh", AssetReferenceCodec.Write(new Paradise.Authoring.AssetReference(ChildGuid, "models/crate.glb")) },
            }));
        document.Objects.Add(root);
        var errors = new List<string>();

        PrefabBake.Bake(document, _ => null, ".json", errors, builtPath: _ => null);

        await Assert.That(errors).Count().IsEqualTo(1);
        await Assert.That(errors[0]).Contains("spawner");
        await Assert.That(errors[0]).Contains("game.Spawner");
        await Assert.That(errors[0]).Contains("models/crate.glb");
    }

    /// <summary>
    /// Play keeps the <c>.prefab</c> name, so a spawner must keep pointing at one. Configs still
    /// follow the profile format — otherwise they would name a file the config importer did not
    /// write.
    /// </summary>
    [Test]
    public async Task play_keeps_prefab_references_and_rewrites_configs()
    {
        var document = new PrefabDocument();
        var root = PrefabObject.WithMeta(RootGuid, "spawner");
        root.Components.Add(new PrefabComponent(
            new Guid("7c1d2e3f-4a5b-4c6d-8e7f-90a1b2c3d4e5"), "game.Spawner", new CanonicalTomlTable
            {
                { "Spawns", AssetReferenceCodec.Write(new Paradise.Authoring.AssetReference(ChildGuid, "prefabs/crate.prefab")) },
                { "Material", AssetReferenceCodec.Write(new Paradise.Authoring.AssetReference(ChildGuid, "materials/rust.toml")) },
                { "Mesh", AssetReferenceCodec.Write(new Paradise.Authoring.AssetReference(ChildGuid, "models/crate.glb")) },
            }));
        document.Objects.Add(root);

        var payload = Payload(
            PrefabBake.Bake(document, _ => null, ".prefab", ".toml", new List<string>()).Entities[0],
            new Guid("7c1d2e3f-4a5b-4c6d-8e7f-90a1b2c3d4e5"));

        await Assert.That(payload.GetProperty("Spawns").GetString()).IsEqualTo("prefabs/crate.prefab");
        await Assert.That(payload.GetProperty("Material").GetString()).IsEqualTo("materials/rust.toml");
        await Assert.That(payload.GetProperty("Mesh").GetString()).IsEqualTo("models/crate.glb");
    }

    /// <summary>
    /// A payload table inside an array is data, not a reference, and reaches the contract whole.
    /// </summary>
    /// <remarks>
    /// The other half of the rule <c>ProjectVerifier.Walk</c> applies, and the two must be pinned
    /// together: the reader wraps every table inside an array as inline, so a bake matching on the
    /// model type reads a <c>path</c> that is not there and nulls the element. Verify calls this
    /// document clean — <c>a_payload_table_inside_an_array_is_not_read_as_a_reference</c> — so a
    /// bake that dropped it would lose a collider with nothing anywhere reporting it.
    /// </remarks>
    [Test]
    public async Task a_payload_table_inside_an_array_survives_the_bake()
    {
        var componentId = new Guid("2b3c4d5e-6f70-4812-9a3b-4c5d6e7f8091");
        var document = new PrefabDocument();
        var root = PrefabObject.WithMeta(RootGuid, "crate");
        root.Components.Add(new PrefabComponent(componentId, "game.Body", new CanonicalTomlTable
        {
            {
                "Colliders", new object[]
                {
                    new CanonicalInlineTable { { "ShapeType", "Box" }, { "Radius", 2.0 } },
                }
            },
        }));
        document.Objects.Add(root);

        var colliders = Payload(Bake(document).Entities[0], componentId).GetProperty("Colliders");

        await Assert.That(colliders.GetArrayLength()).IsEqualTo(1);
        await Assert.That(colliders[0].GetProperty("ShapeType").GetString()).IsEqualTo("Box");
        await Assert.That(colliders[0].GetProperty("Radius").GetDouble()).IsEqualTo(2.0);
    }

    /// <summary>
    /// The contract's copy of the well-known ids cannot drift from the authoring format's —
    /// Paradise.Export cannot reference Paradise.Assets.Documents, so the equality lives here,
    /// in the one test project that sees both.
    /// </summary>
    [Test]
    public async Task the_contract_and_authoring_well_known_ids_agree()
    {
        await Assert.That(WellKnownEntityComponents.MetaId).IsEqualTo(WellKnownComponents.MetaId);
        await Assert.That(WellKnownEntityComponents.TransformId).IsEqualTo(WellKnownComponents.TransformId);
        await Assert.That(WellKnownEntityComponents.MetaType).IsEqualTo(WellKnownComponents.MetaType);
        await Assert.That(WellKnownEntityComponents.TransformType).IsEqualTo(WellKnownComponents.TransformType);
        await Assert.That(WellKnownEntityComponents.Guid).IsEqualTo(WellKnownComponents.Guid);
        await Assert.That(WellKnownEntityComponents.Name).IsEqualTo(WellKnownComponents.Name);
        await Assert.That(WellKnownEntityComponents.Parent).IsEqualTo(WellKnownComponents.Parent);
        await Assert.That(WellKnownEntityComponents.Position).IsEqualTo(WellKnownComponents.Position);
        await Assert.That(WellKnownEntityComponents.Rotation).IsEqualTo(WellKnownComponents.Rotation);
        await Assert.That(WellKnownEntityComponents.Scale).IsEqualTo(WellKnownComponents.Scale);
    }
}
