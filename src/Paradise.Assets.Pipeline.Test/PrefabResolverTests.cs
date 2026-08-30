using Paradise.Assets.Documents;
using Paradise.Authoring;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// The prefab resolver, and the normative rules the Python mirror has to match exactly.
///
/// These are the fixtures both implementations run against: anything that differs here differs
/// in the documents each writes, and the divergence surfaces long afterwards as a scene-check
/// failure or a scene whose entity handles moved.
/// </summary>
public class PrefabResolverTests
{
    private const string RootLocal = "aaaaaaaa-0000-4000-8000-000000000001";
    private const string ChildLocal = "aaaaaaaa-0000-4000-8000-000000000002";
    private const string InstanceGuid = "410f381b-fc6e-5a66-a70a-698972a199b5";
    private const string MeshId = "edee8bd8-9321-47db-819d-9bdadf010be4";
    private const string MaterialsId = "bdc4fc87-d7b4-41f1-bc90-fc827005adfc";
    private const string TagId = "01b792a0-f12e-4fe9-9867-907ae988b301";

    private static readonly AssetReference PrefabRef =
        new(Guid.Parse("5f2a1111-2222-4333-8444-555555555555"), "prefabs/lamp.prefab");

    /// <summary>A one-object prefab: meta, transform, a mesh and a tag.</summary>
    private static PrefabDocument SingleObjectPrefab()
    {
        var document = new SceneDocument();
        var root = SceneObject.WithMeta(Guid.Parse(RootLocal), "Post");
        root.Components.Add(new SceneComponent(
            WellKnownComponents.TransformId, WellKnownComponents.TransformType,
            new CanonicalTomlTable
            {
                { WellKnownComponents.Position, new object[] { 0.0, 0.0, 0.0 } },
                { WellKnownComponents.Scale, new object[] { 1.0, 1.0, 1.0 } },
            }));
        root.Components.Add(new SceneComponent(Guid.Parse(MeshId), "ObstacleMesh",
            new CanonicalTomlTable { { "Mesh", "Models/unit_box.glb" } }));
        root.Components.Add(new SceneComponent(Guid.Parse(TagId), "ObstacleTag"));
        document.Objects.Add(root);
        return PrefabDocument.Validate(document, "lamp.prefab");
    }

    /// <summary>A two-object prefab: Post, with Bulb parented beneath it.</summary>
    private static PrefabDocument TwoObjectPrefab()
    {
        var document = SingleObjectPrefab().Document;
        var child = SceneObject.WithMeta(Guid.Parse(ChildLocal), "Bulb", Guid.Parse(RootLocal));
        child.Components.Add(new SceneComponent(Guid.Parse(MaterialsId), "Materials",
            new CanonicalTomlTable { { "Slots", new object[] { "materials/warm.toml" } } }));
        document.Objects.Add(child);
        return PrefabDocument.Validate(document, "lamp.prefab");
    }

    private static SceneObject Instance(params SceneComponent[] extra)
    {
        var instance = SceneObject.WithMeta(Guid.Parse(InstanceGuid), "Lamp_03");
        instance.Prefab = PrefabRef;
        foreach (var component in extra) instance.Components.Add(component);
        return instance;
    }

    private static PrefabResolver.ResolveResult Resolve(PrefabDocument prefab, params SceneObject[] objects)
    {
        var scene = new SceneDocument();
        foreach (var candidate in objects) scene.Objects.Add(candidate);
        return PrefabResolver.Resolve(scene, _ => prefab);
    }

    [Test]
    public async Task an_instance_becomes_the_prefabs_root_carrying_its_own_identity()
    {
        var result = Resolve(SingleObjectPrefab(), Instance());

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(result.Document.Objects.Count).IsEqualTo(1);
        var resolved = result.Document.Objects[0];
        await Assert.That(resolved.Guid).IsEqualTo(Guid.Parse(InstanceGuid));
        await Assert.That(resolved.Name).IsEqualTo("Lamp_03");
        await Assert.That(resolved.Prefab).IsNull();   // flattened: nothing downstream sees prefabs
    }

    [Test]
    public async Task unmentioned_components_are_inherited()
    {
        var resolved = Resolve(SingleObjectPrefab(), Instance()).Document.Objects[0];

        await Assert.That(resolved.Component(Guid.Parse(MeshId))!.Data.Value("Mesh"))
            .IsEqualTo("Models/unit_box.glb");
        await Assert.That(resolved.Component(Guid.Parse(TagId))).IsNotNull();
    }

    [Test]
    public async Task a_repeated_component_is_overridden_field_by_field()
    {
        // Scale is given, Position is not -- so Position must survive from the prefab. Anything
        // else would make every instance restate every field it did not want to change.
        var instance = Instance(new SceneComponent(
            WellKnownComponents.TransformId, WellKnownComponents.TransformType,
            new CanonicalTomlTable { { WellKnownComponents.Scale, new object[] { 1.0, 0.08, 4.0 } } }));

        var transform = Resolve(SingleObjectPrefab(), instance).Document.Objects[0]
            .Component(WellKnownComponents.TransformId)!;

        await Assert.That(((IReadOnlyList<object>)transform.Data.Value(WellKnownComponents.Scale)!)[1]).IsEqualTo(0.08);
        await Assert.That(transform.Data.ContainsKey(WellKnownComponents.Position)).IsTrue();
    }

    [Test]
    public async Task a_component_only_the_instance_has_is_added()
    {
        var instance = Instance(new SceneComponent(Guid.Parse(MaterialsId), "Materials",
            new CanonicalTomlTable { { "Slots", new object[] { "materials/red.toml" } } }));

        var resolved = Resolve(SingleObjectPrefab(), instance).Document.Objects[0];

        await Assert.That(resolved.Component(Guid.Parse(MaterialsId))).IsNotNull();
    }

    [Test]
    public async Task a_removed_component_is_dropped()
    {
        var instance = Instance(new SceneComponent(Guid.Parse(TagId), removed: true));

        var resolved = Resolve(SingleObjectPrefab(), instance).Document.Objects[0];

        await Assert.That(resolved.Component(Guid.Parse(TagId))).IsNull();
        await Assert.That(resolved.Component(Guid.Parse(MeshId))).IsNotNull();
    }

    [Test]
    public async Task removing_a_component_the_prefab_does_not_have_is_an_error()
    {
        var instance = Instance(new SceneComponent(Guid.Parse(MaterialsId), removed: true));

        var result = Resolve(SingleObjectPrefab(), instance);

        await Assert.That(result.Errors.Count).IsEqualTo(1);
        await Assert.That(result.Errors[0].Message).Contains("does not have");
    }

    [Test]
    public async Task component_order_is_prefab_order_then_instance_additions()
    {
        // Order is data -- the runtime applies components in it -- so it is pinned rather than
        // left to whatever the merge loop happens to produce.
        var instance = Instance(new SceneComponent(Guid.Parse(MaterialsId), "Materials"));

        var ids = Resolve(SingleObjectPrefab(), instance).Document.Objects[0]
            .Components.Select(c => c.Id).ToList();

        await Assert.That(ids).IsEquivalentTo(new[]
        {
            WellKnownComponents.MetaId, WellKnownComponents.TransformId,
            Guid.Parse(MeshId), Guid.Parse(TagId), Guid.Parse(MaterialsId),
        });
    }

    [Test]
    public async Task an_instance_can_be_parented_even_though_the_prefab_root_has_no_parent()
    {
        // The rule that a naive "unknown field" check would forbid, and the commonest edit there
        // is: a prefab root deliberately has no Parent, because that absence is what makes it the
        // root.
        var parent = SceneObject.WithMeta(Guid.Parse(ChildLocal), "Holder");
        var instance = Instance();
        instance.Components[0] = new SceneComponent(
            WellKnownComponents.MetaId, WellKnownComponents.MetaType,
            new CanonicalTomlTable
            {
                { WellKnownComponents.Guid, InstanceGuid },
                { WellKnownComponents.Name, "Lamp_03" },
                { WellKnownComponents.Parent, ChildLocal },
            });

        var result = Resolve(SingleObjectPrefab(), parent, instance);

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(result.Document.Objects[1].Parent).IsEqualTo(Guid.Parse(ChildLocal));
    }

    // ---- multi-object prefabs ------------------------------------------------------------

    [Test]
    public async Task children_follow_their_instance_in_prefab_document_order()
    {
        var result = Resolve(TwoObjectPrefab(), Instance());

        await Assert.That(result.Document.Objects.Count).IsEqualTo(2);
        await Assert.That(result.Document.Objects[0].Name).IsEqualTo("Lamp_03");
        await Assert.That(result.Document.Objects[1].Name).IsEqualTo("Bulb");
    }

    [Test]
    public async Task a_child_gets_a_minted_identity_parented_to_the_instance()
    {
        var child = Resolve(TwoObjectPrefab(), Instance()).Document.Objects[1];

        await Assert.That(child.Guid).IsEqualTo(
            PrefabResolver.MintChildGuid(Guid.Parse(InstanceGuid), Guid.Parse(ChildLocal)));
        await Assert.That(child.Parent).IsEqualTo(Guid.Parse(InstanceGuid));
    }

    [Test]
    public async Task two_instances_of_one_prefab_give_their_children_different_identities()
    {
        // What makes the minting namespace the INSTANCE rather than the prefab: twenty instances
        // need twenty distinct sets of children, with no guid bookkeeping in the document.
        var second = SceneObject.WithMeta(Guid.Parse("7c2e9a41-1111-4222-8333-444444444444"), "Lamp_04");
        second.Prefab = PrefabRef;

        var result = Resolve(TwoObjectPrefab(), Instance(), second);

        await Assert.That(result.Document.Objects[1].Guid).IsNotEqualTo(result.Document.Objects[3].Guid);
    }

    [Test]
    public async Task minting_is_stable_across_runs()
    {
        var once = PrefabResolver.MintChildGuid(Guid.Parse(InstanceGuid), Guid.Parse(ChildLocal));
        var twice = PrefabResolver.MintChildGuid(Guid.Parse(InstanceGuid), Guid.Parse(ChildLocal));

        await Assert.That(once).IsEqualTo(twice);
    }

    [Test]
    public async Task minting_matches_the_specified_uuid5_over_the_guids_text()
    {
        // Pinned as a VALUE, and verified against CPython:
        //
        //   uuid.uuid5(uuid.UUID('410f381b-fc6e-5a66-a70a-698972a199b5'),
        //              'aaaaaaaa-0000-4000-8000-000000000002')
        //   -> 6a8f7f6a-5cf4-59f3-ae75-6717b3ae43e3
        //
        // Hashing the raw bytes instead would give a DIFFERENT guid in each language, because
        // .NET's Guid.ToByteArray is mixed-endian and Python's UUID.bytes is big-endian. Hashing
        // the canonical text sidesteps that, and this value is what proves it.
        var minted = PrefabResolver.MintChildGuid(Guid.Parse(InstanceGuid), Guid.Parse(ChildLocal));

        await Assert.That(DocumentGuid.Format(minted)).IsEqualTo("6a8f7f6a-5cf4-59f3-ae75-6717b3ae43e3");
    }

    [Test]
    public async Task a_carrier_overrides_a_child_and_occupies_no_slot_of_its_own()
    {
        var carrier = new SceneObject();
        carrier.Components.Add(new SceneComponent(
            WellKnownComponents.MetaId, WellKnownComponents.MetaType,
            new CanonicalTomlTable
            {
                { WellKnownComponents.Parent, InstanceGuid },
                { WellKnownComponents.Target, ChildLocal },
            }));
        carrier.Components.Add(new SceneComponent(Guid.Parse(MaterialsId), "Materials",
            new CanonicalTomlTable { { "Slots", new object[] { "materials/dead.toml" } } }));

        var result = Resolve(TwoObjectPrefab(), Instance(), carrier);

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(result.Document.Objects.Count).IsEqualTo(2);   // carrier consumed
        var slots = (IReadOnlyList<object>)result.Document.Objects[1]
            .Component(Guid.Parse(MaterialsId))!.Data.Value("Slots")!;
        await Assert.That(slots[0]).IsEqualTo("materials/dead.toml");
    }

    [Test]
    public async Task a_dropped_child_is_removed()
    {
        var carrier = new SceneObject();
        carrier.Components.Add(new SceneComponent(
            WellKnownComponents.MetaId, WellKnownComponents.MetaType,
            new CanonicalTomlTable
            {
                { WellKnownComponents.Parent, InstanceGuid },
                { WellKnownComponents.Target, ChildLocal },
                { WellKnownComponents.Dropped, true },
            }));

        var result = Resolve(TwoObjectPrefab(), Instance(), carrier);

        await Assert.That(result.Document.Objects.Count).IsEqualTo(1);
        await Assert.That(result.Document.Objects[0].Name).IsEqualTo("Lamp_03");
    }

    [Test]
    public async Task a_carrier_targeting_nothing_in_the_prefab_is_an_error()
    {
        var carrier = new SceneObject();
        carrier.Components.Add(new SceneComponent(
            WellKnownComponents.MetaId, WellKnownComponents.MetaType,
            new CanonicalTomlTable
            {
                { WellKnownComponents.Parent, InstanceGuid },
                { WellKnownComponents.Target, "99999999-8888-4777-8666-555555555555" },
            }));

        var result = Resolve(TwoObjectPrefab(), Instance(), carrier);

        await Assert.That(result.Errors.Count).IsEqualTo(1);
        await Assert.That(result.Errors[0].Message).Contains("does not contain");
    }

    // ---- prefab validation ---------------------------------------------------------------

    [Test]
    public async Task a_prefab_with_two_roots_is_refused()
    {
        var document = SingleObjectPrefab().Document;
        document.Objects.Add(SceneObject.WithMeta(Guid.Parse(ChildLocal), "Loose"));

        var error = Assert.Throws<SceneDocumentException>(() => PrefabDocument.Validate(document, "x.prefab"));

        await Assert.That(error!.Message).Contains("2 root objects");
    }

    [Test]
    public async Task a_prefab_instantiating_another_prefab_is_refused_for_now()
    {
        var document = SingleObjectPrefab().Document;
        document.Objects[0].Prefab = PrefabRef;

        var error = Assert.Throws<SceneDocumentException>(() => PrefabDocument.Validate(document, "x.prefab"));

        await Assert.That(error!.Message).Contains("not supported yet");
    }

    [Test]
    public async Task an_empty_prefab_is_refused()
    {
        var error = Assert.Throws<SceneDocumentException>(
            () => PrefabDocument.Validate(new SceneDocument(), "x.prefab"));

        await Assert.That(error!.Message).Contains("no objects");
    }

    [Test]
    public async Task a_plain_object_passes_through_untouched()
    {
        var plain = SceneObject.WithMeta(Guid.Parse(ChildLocal), "Hand placed");

        var result = Resolve(SingleObjectPrefab(), plain);

        await Assert.That(result.Expanded).IsEqualTo(0);
        await Assert.That(result.Document.Objects[0]).IsSameReferenceAs(plain);
    }
}
