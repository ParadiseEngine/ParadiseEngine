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

    private static LevelData Bake(PrefabDocument document, List<string>? errors = null)
        => PrefabBake.Bake(document, _ => null, ".json", errors ?? new List<string>());

    private static JsonElement Payload(IReadOnlyList<AuthoredComponentData> entity, Guid id)
        => entity.Single(component => component.Id == id).Data;

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

    /// <summary>Hierarchy ships now, so a parented object without a transform is a build error
    /// naming the object — not a silent identity placement decided by the loader.</summary>
    [Test]
    public async Task a_parented_object_without_a_transform_is_an_error()
    {
        var document = new PrefabDocument();
        var root = PrefabObject.WithMeta(RootGuid, "district");
        root.Components.Add(LocalTransformCodec.Write(LocalTransform.Identity));
        document.Objects.Add(root);
        document.Objects.Add(PrefabObject.WithMeta(ChildGuid, "floating", RootGuid));

        var errors = new List<string>();
        Bake(document, errors);

        await Assert.That(errors.Single()).Contains("floating");
        await Assert.That(errors.Single()).Contains("transform");
    }

    /// <summary>An unparented root may omit its transform — cameras and directional lights keep
    /// their unplaced semantics, and nothing is synthesized for them any more.</summary>
    [Test]
    public async Task an_unparented_object_may_omit_its_transform()
    {
        var document = new PrefabDocument();
        document.Objects.Add(PrefabObject.WithMeta(RootGuid, "viewpoint"));

        var errors = new List<string>();
        var level = Bake(document, errors);

        await Assert.That(errors).IsEmpty();
        await Assert.That(level.Entities.Single()
                .Any(component => component.Id == WellKnownEntityComponents.TransformId))
            .IsFalse();
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
