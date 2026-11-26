using TUnit.Assertions.Enums;
using Paradise.Assets.Documents;
using Paradise.Authoring;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// The prefab resolver, and the normative rules the Python mirror has to match exactly.
///
/// These are the fixtures both implementations run against: anything that differs here differs
/// in the documents each writes, and the divergence surfaces long afterwards as a prefab-check
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

    /// <summary>The document, having checked it obeys the single-root rule.</summary>
    /// <remarks>
    /// Fixtures are built object by object rather than parsed, so they skip the serializer where
    /// that rule is normally enforced. Running it here keeps a fixture from being something no
    /// reader would ever hand the resolver.
    /// </remarks>
    private static PrefabDocument Validated(PrefabDocument document, string sourceName)
    {
        document.Validate(sourceName);
        return document;
    }

    /// <summary>A one-object prefab: meta, transform, a mesh and a tag.</summary>
    private static PrefabDocument SingleObjectPrefab()
    {
        var document = new PrefabDocument();
        var root = PrefabObject.WithMeta(Guid.Parse(RootLocal), "Post");
        root.Components.Add(new PrefabComponent(
            WellKnownComponents.TransformId, WellKnownComponents.TransformType,
            new CanonicalTomlTable
            {
                { WellKnownComponents.Position, new object[] { 0.0, 0.0, 0.0 } },
                { WellKnownComponents.Scale, new object[] { 1.0, 1.0, 1.0 } },
            }));
        root.Components.Add(new PrefabComponent(Guid.Parse(MeshId), "ObstacleMesh",
            new CanonicalTomlTable { { "Mesh", "Models/unit_box.glb" } }));
        root.Components.Add(new PrefabComponent(Guid.Parse(TagId), "ObstacleTag"));
        document.Objects.Add(root);
        return Validated(document, "lamp.prefab");
    }

    /// <summary>A two-object prefab: Post, with Bulb parented beneath it.</summary>
    private static PrefabDocument TwoObjectPrefab()
    {
        var document = SingleObjectPrefab();
        var child = PrefabObject.WithMeta(Guid.Parse(ChildLocal), "Bulb", Guid.Parse(RootLocal));
        child.Components.Add(new PrefabComponent(Guid.Parse(MaterialsId), "Materials",
            new CanonicalTomlTable { { "Slots", new object[] { "materials/warm.toml" } } }));
        document.Objects.Add(child);
        return Validated(document, "lamp.prefab");
    }

    private static PrefabObject Instance(params PrefabComponent[] extra)
    {
        var instance = PrefabObject.WithMeta(Guid.Parse(InstanceGuid), "Lamp_03");
        instance.Prefab = PrefabRef;
        foreach (var component in extra) instance.Components.Add(component);
        return instance;
    }

    private static PrefabResolver.ResolveResult Resolve(PrefabDocument prefab, params PrefabObject[] objects)
    {
        var scene = new PrefabDocument();
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
        var instance = Instance(new PrefabComponent(
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
        var instance = Instance(new PrefabComponent(Guid.Parse(MaterialsId), "Materials",
            new CanonicalTomlTable { { "Slots", new object[] { "materials/red.toml" } } }));

        var resolved = Resolve(SingleObjectPrefab(), instance).Document.Objects[0];

        await Assert.That(resolved.Component(Guid.Parse(MaterialsId))).IsNotNull();
    }

    [Test]
    public async Task a_removed_component_is_dropped()
    {
        var instance = Instance(new PrefabComponent(Guid.Parse(TagId), removed: true));

        var resolved = Resolve(SingleObjectPrefab(), instance).Document.Objects[0];

        await Assert.That(resolved.Component(Guid.Parse(TagId))).IsNull();
        await Assert.That(resolved.Component(Guid.Parse(MeshId))).IsNotNull();
    }

    [Test]
    public async Task removing_a_component_the_prefab_does_not_have_is_an_error()
    {
        var instance = Instance(new PrefabComponent(Guid.Parse(MaterialsId), removed: true));

        var result = Resolve(SingleObjectPrefab(), instance);

        await Assert.That(result.Errors.Count).IsEqualTo(1);
        await Assert.That(result.Errors[0].Message).Contains("does not have");
    }

    [Test]
    public async Task component_order_is_prefab_order_then_instance_additions()
    {
        // Order is data -- the runtime applies components in it -- so it is pinned rather than
        // left to whatever the merge loop happens to produce.
        var instance = Instance(new PrefabComponent(Guid.Parse(MaterialsId), "Materials"));

        var ids = Resolve(SingleObjectPrefab(), instance).Document.Objects[0]
            .Components.Select(c => c.Id).ToList();

        await Assert.That(ids).IsEquivalentTo(new[]
        {
            WellKnownComponents.MetaId, WellKnownComponents.TransformId,
            Guid.Parse(MeshId), Guid.Parse(TagId), Guid.Parse(MaterialsId),
        }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task an_instance_can_be_parented_even_though_the_prefab_root_has_no_parent()
    {
        // The rule that a naive "unknown field" check would forbid, and the commonest edit there
        // is: a prefab root deliberately has no Parent, because that absence is what makes it the
        // root.
        var parent = PrefabObject.WithMeta(Guid.Parse(ChildLocal), "Holder");
        var instance = Instance();
        instance.Components[0] = new PrefabComponent(
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

    // multi-object prefabs

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
        var second = PrefabObject.WithMeta(Guid.Parse("7c2e9a41-1111-4222-8333-444444444444"), "Lamp_04");
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
        var carrier = new PrefabObject();
        carrier.Components.Add(new PrefabComponent(
            WellKnownComponents.MetaId, WellKnownComponents.MetaType,
            new CanonicalTomlTable
            {
                { WellKnownComponents.Parent, InstanceGuid },
                { WellKnownComponents.Target, ChildLocal },
            }));
        carrier.Components.Add(new PrefabComponent(Guid.Parse(MaterialsId), "Materials",
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
        var carrier = new PrefabObject();
        carrier.Components.Add(new PrefabComponent(
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
        var carrier = new PrefabObject();
        carrier.Components.Add(new PrefabComponent(
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

    // prefab validation

    [Test]
    public async Task a_prefab_with_two_roots_is_refused()
    {
        var document = SingleObjectPrefab();
        document.Objects.Add(PrefabObject.WithMeta(Guid.Parse(ChildLocal), "Loose"));

        var error = Assert.Throws<PrefabDocumentException>(() => Validated(document, "x.prefab"));

        await Assert.That(error!.Message).Contains("2 root objects");
    }

    [Test]
    public async Task a_prefab_may_instantiate_another_prefab()
    {
        // fitting.prefab's own child is an instance of lamp.prefab, so resolving ONE instance of
        // fitting has to expand two levels. This is the case the format always allowed and the
        // resolver refused; a level is a document holding instances, so it is also every level.
        var result = ResolveWith(TwoLevelLookup, OuterInstance());

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(result.Document.Objects.Select(o => o.Name ?? "").ToArray())
            .IsEquivalentTo(new[] { "Fitting_01", "Post" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task a_nested_instance_mints_identities_that_survive_two_levels()
    {
        var result = ResolveWith(TwoLevelLookup, OuterInstance());

        // The inner prefab's root becomes the inner instance, whose own guid was minted from the
        // outer instance's. Nothing may collide, and no prefab-local id may survive.
        var guids = result.Document.Objects.Select(o => o.Guid).ToList();
        await Assert.That(guids.Distinct().Count()).IsEqualTo(guids.Count);
        await Assert.That(guids).DoesNotContain(Guid.Parse(RootLocal));
        await Assert.That(guids).DoesNotContain(Guid.Parse(OuterInnerLocal));
    }

    [Test]
    public async Task a_prefab_cycle_is_reported_rather_than_recursed()
    {
        // A prefab whose child instantiates the prefab it is in. Without the stack this recurses
        // until the process dies, which tells an author nothing; the error names the chain.
        var result = ResolveWith(_ => SelfReferencingPrefab(), Instance());

        await Assert.That(result.Errors).Count().IsEqualTo(1);
        await Assert.That(result.Errors[0].Message).Contains("form a cycle");
        await Assert.That(result.Errors[0].Message).Contains("lamp.prefab");
    }

    /// <summary>A prefab whose child instantiates <c>prefabs/lamp.prefab</c>.</summary>
    private static PrefabDocument OuterPrefab()
    {
        var document = new PrefabDocument();
        document.Objects.Add(PrefabObject.WithMeta(Guid.Parse(OuterRootLocal), "Fitting"));

        var inner = PrefabObject.WithMeta(Guid.Parse(OuterInnerLocal), "Post", Guid.Parse(OuterRootLocal));
        inner.Prefab = PrefabRef;
        document.Objects.Add(inner);

        return Validated(document, "fitting.prefab");
    }

    /// <summary>A prefab that reaches itself through its own child.</summary>
    private static PrefabDocument SelfReferencingPrefab()
    {
        var document = SingleObjectPrefab();
        var child = PrefabObject.WithMeta(Guid.Parse(ChildLocal), "Inner", Guid.Parse(RootLocal));
        child.Prefab = PrefabRef;
        document.Objects.Add(child);
        return Validated(document, "prefabs/lamp.prefab");
    }

    private static PrefabObject OuterInstance()
    {
        var instance = PrefabObject.WithMeta(Guid.Parse(InstanceGuid), "Fitting_01");
        instance.Prefab = OuterRef;
        return instance;
    }

    /// <summary>Hands back whichever of the two fixtures a reference names.</summary>
    private static PrefabDocument? TwoLevelLookup(AssetReference reference)
        => reference.Path == PrefabRef.Path ? SingleObjectPrefab() : OuterPrefab();

    private static PrefabResolver.ResolveResult ResolveWith(
        Func<AssetReference, PrefabDocument?> prefabs, params PrefabObject[] objects)
    {
        var scene = new PrefabDocument();
        foreach (var candidate in objects) scene.Objects.Add(candidate);
        return PrefabResolver.Resolve(scene, prefabs);
    }

    private static readonly AssetReference OuterRef =
        new(Guid.Parse("5f2a6666-7777-4888-8999-aaaaaaaaaaaa"), "prefabs/fitting.prefab");

    private const string OuterRootLocal = "c0ffee00-0000-4000-8000-000000000001";
    private const string OuterInnerLocal = "c0ffee00-0000-4000-8000-000000000002";

    [Test]
    public async Task an_empty_prefab_is_refused()
    {
        var error = Assert.Throws<PrefabDocumentException>(
            () => Validated(new PrefabDocument(), "x.prefab"));

        await Assert.That(error!.Message).Contains("no objects");
    }

    // references to the prefab's own members (ParadiseBlenderEditor#70)

    private const string TargetsId = "3c1d5e7a-0000-4000-8000-0000000000aa";
    private const string Elsewhere = "99999999-8888-4777-8666-555555555555";

    /// <summary>Post and Bulb, where Post names both by their prefab-LOCAL guids, every way a payload can.</summary>
    private static PrefabDocument SelfReferencingMembers()
    {
        var document = TwoObjectPrefab();
        document.Objects[0].Components.Add(new PrefabComponent(Guid.Parse(TargetsId), "Targets",
            new CanonicalTomlTable
            {
                { "Light", ChildLocal },
                { "Self", RootLocal.ToUpperInvariant() },
                { "Undashed", Guid.Parse(ChildLocal).ToString("N") },
                { "Outside", Elsewhere.ToUpperInvariant() },
                { "Unset", "00000000-0000-0000-0000-000000000000" },
                { "List", new object[] { ChildLocal, "not a guid", 3L } },
                { "Nested", new CanonicalTomlTable { { "Target", ChildLocal } } },
                { "Rows", new List<CanonicalTomlTable> { new() { { "Target", ChildLocal } } } },
                { "Inline", new object[] { Inline(("Target", ChildLocal), ("Weight", 2.0)) } },
                { "Asset", Inline(("guid", ChildLocal), ("path", "prefabs/lamp.prefab")) },
            }));
        return Validated(document, "lamp.prefab");
    }

    private static CanonicalInlineTable Inline(params (string Key, object Value)[] pairs)
    {
        var table = new CanonicalInlineTable();
        foreach (var (key, value) in pairs) table.Add(key, value);
        return table;
    }

    private static CanonicalTomlTable Targets(PrefabObject resolved) => resolved.Component(Guid.Parse(TargetsId))!.Data;

    private static string Minted(string instance, string local)
        => DocumentGuid.Format(PrefabResolver.MintChildGuid(Guid.Parse(instance), Guid.Parse(local)));

    [Test]
    public async Task a_prefab_naming_its_own_members_names_the_instances_members()
    {
        var result = Resolve(SelfReferencingMembers(), Instance());

        await Assert.That(result.Errors).IsEmpty();
        var targets = Targets(result.Document.Objects[0]);
        var bulb = Minted(InstanceGuid, ChildLocal);
        await Assert.That(result.Document.Objects[1].Guid).IsEqualTo(Guid.Parse(bulb));
        await Assert.That(targets.Value("Light")).IsEqualTo(bulb);
        await Assert.That(targets.Value("Self")).IsEqualTo(InstanceGuid);     // the root is the instance
        await Assert.That(targets.Value("Undashed")).IsEqualTo(bulb);         // canonical once remapped
        await Assert.That(((IReadOnlyList<object>)targets.Value("List")!)[0]).IsEqualTo(bulb);
        await Assert.That(((CanonicalTomlTable)targets.Value("Nested")!).Value("Target")).IsEqualTo(bulb);
        await Assert.That(((IReadOnlyList<CanonicalTomlTable>)targets.Value("Rows")!)[0].Value("Target")).IsEqualTo(bulb);
        var inline = (CanonicalInlineTable)((IReadOnlyList<object>)targets.Value("Inline")!)[0];
        await Assert.That(inline.Value("Target")).IsEqualTo(bulb);
        await Assert.That(inline.Value("Weight")).IsEqualTo(2.0);
    }

    [Test]
    public async Task values_naming_no_member_keep_their_spelling()
    {
        var targets = Targets(Resolve(SelfReferencingMembers(), Instance()).Document.Objects[0]);

        await Assert.That(targets.Value("Outside")).IsEqualTo(Elsewhere.ToUpperInvariant());
        await Assert.That(targets.Value("Unset")).IsEqualTo("00000000-0000-0000-0000-000000000000");
        var list = (IReadOnlyList<object>)targets.Value("List")!;
        await Assert.That(list[1]).IsEqualTo("not a guid");
        await Assert.That(list[2]).IsEqualTo(3L);
        // An asset reference names an asset, never an object, whatever its guid.
        await Assert.That(((CanonicalInlineTable)targets.Value("Asset")!).Value("guid")).IsEqualTo(ChildLocal);
    }

    [Test]
    public async Task each_instance_names_its_own_members()
    {
        const string secondGuid = "7c2e9a41-1111-4222-8333-444444444444";
        var second = PrefabObject.WithMeta(Guid.Parse(secondGuid), "Lamp_04");
        second.Prefab = PrefabRef;

        var result = Resolve(SelfReferencingMembers(), Instance(), second);

        await Assert.That(Targets(result.Document.Objects[0]).Value("Light")).IsEqualTo(Minted(InstanceGuid, ChildLocal));
        await Assert.That(Targets(result.Document.Objects[2]).Value("Light")).IsEqualTo(Minted(secondGuid, ChildLocal));
        await Assert.That(Targets(result.Document.Objects[2]).Value("Self")).IsEqualTo(secondGuid);
    }

    [Test]
    public async Task an_instances_override_is_carried_verbatim()
    {
        // An override is spelled in the document holding the instance, in RESOLVED guids, so it
        // is never remapped -- not even when it happens to spell a local of the prefab.
        var instance = Instance(new PrefabComponent(Guid.Parse(TargetsId), "Targets",
            new CanonicalTomlTable { { "Light", ChildLocal }, { "Extra", RootLocal } }));

        var targets = Targets(Resolve(SelfReferencingMembers(), instance).Document.Objects[0]);

        await Assert.That(targets.Value("Light")).IsEqualTo(ChildLocal);
        await Assert.That(targets.Value("Extra")).IsEqualTo(RootLocal);
        await Assert.That(targets.Value("Self")).IsEqualTo(InstanceGuid);   // not overridden: remapped
    }

    [Test]
    public async Task a_component_naming_no_member_is_passed_through_as_it_is()
    {
        var prefab = SingleObjectPrefab();
        var mesh = prefab.Objects[0].Component(Guid.Parse(MeshId));

        var resolved = Resolve(prefab, Instance()).Document.Objects[0];

        await Assert.That(resolved.Component(Guid.Parse(MeshId))).IsSameReferenceAs(mesh);
    }

    [Test]
    public async Task the_prefab_itself_is_not_rewritten()
    {
        var prefab = SelfReferencingMembers();

        Resolve(prefab, Instance());

        await Assert.That(Targets(prefab.Objects[0]).Value("Light")).IsEqualTo(ChildLocal);
    }

    [Test]
    public async Task references_inside_a_nested_prefab_name_the_nested_members()
    {
        // fitting.prefab places lamp.prefab, and lamp names its own Bulb. Flattening fitting
        // rewrites that to the Bulb fitting holds, and placing fitting rewrites it again -- to the
        // very guid the resolved Bulb carries.
        var result = ResolveWith(
            reference => reference.Path == PrefabRef.Path ? SelfReferencingMembers() : OuterPrefab(),
            OuterInstance());

        await Assert.That(result.Errors).IsEmpty();
        var post = result.Document.Objects.Single(o => o.Name == "Post");
        var bulb = result.Document.Objects.Single(o => o.Name == "Bulb");
        await Assert.That(Targets(post).Value("Light")).IsEqualTo(DocumentGuid.Format(bulb.Guid!.Value));
        await Assert.That(Targets(post).Value("Self")).IsEqualTo(DocumentGuid.Format(post.Guid!.Value));
    }

    [Test]
    public async Task an_outer_prefab_naming_nested_content_names_the_placed_member()
    {
        // fitting names lamp's Bulb by its FLATTENED local, uuid5(inner instance, Bulb), the
        // value an author picks inside fitting; placed, it must name that Bulb of THIS fitting.
        var outer = OuterPrefab();
        outer.Objects[0].Components.Add(new PrefabComponent(Guid.Parse(TargetsId), "Targets",
            new CanonicalTomlTable { { "Light", Minted(OuterInnerLocal, ChildLocal) } }));

        var result = ResolveWith(
            reference => reference.Path == PrefabRef.Path ? TwoObjectPrefab() : outer,
            OuterInstance());

        await Assert.That(result.Errors).IsEmpty();
        var bulb = result.Document.Objects.Single(o => o.Name == "Bulb");
        await Assert.That(Targets(result.Document.Objects[0]).Value("Light"))
            .IsEqualTo(DocumentGuid.Format(bulb.Guid!.Value));
    }

    [Test]
    public async Task a_plain_object_passes_through_untouched()
    {
        var plain = PrefabObject.WithMeta(Guid.Parse(ChildLocal), "Hand placed");

        var result = Resolve(SingleObjectPrefab(), plain);

        await Assert.That(result.Expanded).IsEqualTo(0);
        await Assert.That(result.Document.Objects[0]).IsSameReferenceAs(plain);
    }
}
