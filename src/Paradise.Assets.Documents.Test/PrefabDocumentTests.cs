using Paradise.Authoring;

namespace Paradise.Assets.Documents.Test;

/// <summary>
/// The all-components scene document: identity, name, parent and placement are components like
/// any other, which is what lets a prefab instance override them through one mechanism.
/// </summary>
public class PrefabDocumentTests
{
    private const string CrateGuid = "3f2a1b4c-5d6e-4f70-8192-a3b4c5d6e7f8";
    private const string LidGuid = "9a8b7c6d-5e4f-4031-8213-4c5d6e7f8091";
    private const string RenderableId = "bdc4fc87-d7b4-41f1-bc90-fc827005adfc";

    private static readonly string Meta = DocumentGuid.Format(WellKnownComponents.MetaId);
    private static readonly string Transform = DocumentGuid.Format(WellKnownComponents.TransformId);

    private static readonly string Canonical =
        "schema_version = 1\n" +
        "\n[[objects]]\n" +
        "\n[[objects.components]]\n" +
        $"id = \"{Meta}\"\n" +
        "type = \"meta\"\n" +
        $"Guid = \"{CrateGuid}\"\n" +
        "Name = \"crate_01\"\n" +
        "\n[[objects.components]]\n" +
        $"id = \"{Transform}\"\n" +
        "type = \"transform\"\n" +
        "Position = [0.0, 1.5, 0.0]\n" +
        "Rotation = [0.0, 0.0, 0.0, 1.0]\n" +
        "Scale = [1.0, 1.0, 1.0]\n" +
        "\n[[objects.components]]\n" +
        $"id = \"{RenderableId}\"\n" +
        "type = \"Paradise.Export.Data.RenderableComponentData\"\n" +
        "Mesh = { guid = \"11111111-2222-4333-8444-555555555555\", path = \"Models/crate.glb\" }\n" +
        "\n[[objects]]\n" +
        "\n[[objects.components]]\n" +
        $"id = \"{Meta}\"\n" +
        "type = \"meta\"\n" +
        $"Guid = \"{LidGuid}\"\n" +
        "Name = \"lid\"\n" +
        $"Parent = \"{CrateGuid}\"\n";

    private static PrefabDocumentException Rejects(string text)
    {
        try
        {
            PrefabDocumentSerializer.Parse(text, "x.scene");
        }
        catch (PrefabDocumentException error)
        {
            return error;
        }

        throw new Exception("expected the document to be rejected");
    }

    /// <summary>A minimal object: just a meta component carrying an identity.</summary>
    private static string Object(string guid, string name = "x", string? extra = null) =>
        "\n[[objects]]\n\n[[objects.components]]\n" +
        $"id = \"{Meta}\"\ntype = \"meta\"\nGuid = \"{guid}\"\nName = \"{name}\"\n" + (extra ?? "");

    [Test]
    public async Task a_canonical_document_round_trips_byte_for_byte()
    {
        // THE property of the format: read → write is the identity on canonical input, or every
        // tool touching a scene would litter diffs with reformatting.
        var document = PrefabDocumentSerializer.Parse(Canonical, "district.scene");

        await Assert.That(PrefabDocumentSerializer.Write(document)).IsEqualTo(Canonical);
    }

    [Test]
    public async Task identity_name_and_parent_are_read_from_the_meta_component()
    {
        var document = PrefabDocumentSerializer.Parse(Canonical, "district.scene");

        await Assert.That(document.Objects.Count).IsEqualTo(2);
        var crate = document.Objects[0];
        await Assert.That(crate.Guid).IsEqualTo(Guid.Parse(CrateGuid));
        await Assert.That(crate.Name).IsEqualTo("crate_01");
        await Assert.That(crate.Parent).IsNull();
        await Assert.That(document.Objects[1].Parent).IsEqualTo(Guid.Parse(CrateGuid));
    }

    [Test]
    public async Task a_payload_sits_flat_beside_id_and_type()
    {
        var document = PrefabDocumentSerializer.Parse(Canonical, "district.scene");

        var renderable = document.Objects[0].Component(Guid.Parse(RenderableId));
        await Assert.That(renderable!.Type).IsEqualTo("Paradise.Export.Data.RenderableComponentData");
        await Assert.That(renderable.Data.ContainsKey("Mesh")).IsTrue();
        // and the reserved keys are structure, not payload
        await Assert.That(renderable.Data.ContainsKey("id")).IsFalse();
        await Assert.That(renderable.Data.ContainsKey("type")).IsFalse();
    }

    [Test]
    public async Task an_asset_reference_in_a_payload_survives_the_round_trip()
    {
        var document = PrefabDocumentSerializer.Parse(Canonical, "district.scene");
        var mesh = document.Objects[0].Component(Guid.Parse(RenderableId))!.Data.Value("Mesh");

        var reference = AssetReferenceCodec.Read(mesh, "on the crate", m => new PrefabDocumentException("x", m));

        await Assert.That(reference!.Path).IsEqualTo("Models/crate.glb");
    }

    [Test]
    public async Task an_empty_scene_is_just_its_version()
    {
        await Assert.That(PrefabDocumentSerializer.Write(new PrefabDocument())).IsEqualTo("schema_version = 1\n");
    }

    [Test]
    public async Task component_order_survives_the_round_trip()
    {
        // Order is data: the runtime applies components in document order.
        var document = PrefabDocumentSerializer.Parse(Canonical, "x.scene");
        var ids = document.Objects[0].Components.Select(c => c.Id).ToList();

        await Assert.That(ids[0]).IsEqualTo(WellKnownComponents.MetaId);
        await Assert.That(ids[1]).IsEqualTo(WellKnownComponents.TransformId);
        await Assert.That(ids[2]).IsEqualTo(Guid.Parse(RenderableId));
    }

    [Test]
    public async Task an_object_with_no_identity_is_refused()
    {
        // Without meta.Guid an object cannot be addressed, parented, or merged into on save.
        var text = "schema_version = 1\n\n[[objects]]\n\n[[objects.components]]\n" +
                   $"id = \"{Transform}\"\ntype = \"transform\"\nPosition = [0.0, 0.0, 0.0]\n";

        await Assert.That(Rejects(text).Message).Contains("meta");
    }

    [Test]
    public async Task a_duplicate_identity_is_refused()
    {
        await Assert.That(Rejects($"schema_version = 1\n{Object(CrateGuid)}{Object(CrateGuid, "other")}").Message)
            .Contains("twice");
    }

    [Test]
    public async Task a_duplicate_component_id_on_one_object_is_refused()
    {
        var text = $"schema_version = 1\n{Object(CrateGuid)}" +
                   $"\n[[objects.components]]\nid = \"{Meta}\"\ntype = \"meta\"\nGuid = \"{LidGuid}\"\n";

        await Assert.That(Rejects(text).Message).Contains("twice");
    }

    [Test]
    public async Task a_dangling_parent_is_refused()
    {
        var text = $"schema_version = 1\n{Object(CrateGuid, "a", $"Parent = \"{LidGuid}\"\n")}";

        await Assert.That(Rejects(text).Message).Contains("does not exist");
    }

    [Test]
    public async Task a_parent_cycle_is_refused()
    {
        var text = "schema_version = 1\n" +
                   Object(CrateGuid, "a", $"Parent = \"{LidGuid}\"\n") +
                   Object(LidGuid, "b", $"Parent = \"{CrateGuid}\"\n");

        await Assert.That(Rejects(text).Message).Contains("cycle");
    }

    [Test]
    public async Task an_unknown_document_key_is_refused()
    {
        // Before any header, so it really is a document-root key -- after one it would belong to
        // that table, and inside a component that makes it an ordinary payload field.
        await Assert.That(Rejects($"schema_version = 1\nnope = 1\n{Object(CrateGuid)}").Message)
            .Contains("unknown key");
    }

    [Test]
    public async Task an_unknown_key_on_an_object_is_refused()
    {
        await Assert.That(Rejects("schema_version = 1\n\n[[objects]]\nnope = 1\n").Message)
            .Contains("unknown key");
    }

    [Test]
    public async Task a_component_without_an_id_is_refused()
    {
        var text = "schema_version = 1\n\n[[objects]]\n\n[[objects.components]]\ntype = \"meta\"\n";

        await Assert.That(Rejects(text).Message).Contains("id");
    }

    [Test]
    public async Task a_removed_component_carrying_fields_is_refused()
    {
        // "Remove this, and also here is what it should contain" has no meaning, and is almost
        // certainly an edit that deleted only half of what it meant to.
        var text = $"schema_version = 1\n{Object(CrateGuid)}" +
                   $"\n[[objects.components]]\nid = \"{RenderableId}\"\nremoved = true\nMesh = \"x\"\n";

        await Assert.That(Rejects(text).Message).Contains("removed");
    }

    [Test]
    public async Task a_removed_marker_round_trips()
    {
        var text = $"schema_version = 1\n{Object(CrateGuid)}" +
                   $"\n[[objects.components]]\nid = \"{RenderableId}\"\nremoved = true\n";

        var document = PrefabDocumentSerializer.Parse(text, "x.scene");

        await Assert.That(document.Objects[0].Component(Guid.Parse(RenderableId))!.Removed).IsTrue();
        await Assert.That(PrefabDocumentSerializer.Write(document)).IsEqualTo(text);
    }

    [Test]
    public async Task a_prefab_reference_round_trips()
    {
        var text = "schema_version = 1\n\n[[objects]]\n" +
                   $"prefab = {{ guid = \"{LidGuid}\", path = \"prefabs/rail.prefab\" }}\n" +
                   $"\n[[objects.components]]\nid = \"{Meta}\"\ntype = \"meta\"\nGuid = \"{CrateGuid}\"\n";

        var document = PrefabDocumentSerializer.Parse(text, "x.scene");

        await Assert.That(document.Objects[0].Prefab!.Path).IsEqualTo("prefabs/rail.prefab");
        await Assert.That(PrefabDocumentSerializer.Write(document)).IsEqualTo(text);
    }

    [Test]
    public async Task a_target_carrier_needs_no_identity_of_its_own()
    {
        // A carrier addresses a prefab-local object; the resolved child's guid is always minted,
        // so requiring one here would mean inventing an identity nothing uses.
        var text = "schema_version = 1\n\n[[objects]]\n\n[[objects.components]]\n" +
                   $"id = \"{Meta}\"\ntype = \"meta\"\nParent = \"{CrateGuid}\"\nTarget = \"{LidGuid}\"\n" +
                   Object(CrateGuid);

        var document = PrefabDocumentSerializer.Parse(text, "x.scene");

        await Assert.That(document.Objects[0].Target).IsEqualTo(Guid.Parse(LidGuid));
        await Assert.That(document.Objects[0].Guid).IsNull();
    }

    [Test]
    public async Task the_single_root_is_inferred_from_the_absence_of_a_parent()
    {
        var document = PrefabDocumentSerializer.Parse(Canonical, "x.scene");

        await Assert.That(document.SingleRoot()!.Guid).IsEqualTo(Guid.Parse(CrateGuid));
    }

    [Test]
    public async Task a_document_with_two_roots_is_refused()
    {
        // There is one kind of document now, and every one of them is instantiable, so "exactly
        // one root" is checked on EVERY read rather than only when something is used as a prefab.
        // The message names the roots, because "which of these did you mean to be the root" is
        // the question the author has to answer.
        var error = Assert.Throws<PrefabDocumentException>(
            () => PrefabDocumentSerializer.Parse($"schema_version = 1\n{Object(CrateGuid)}{Object(LidGuid)}", "x.prefab"));

        await Assert.That(error!.Message).Contains("has 2 root objects");
        await Assert.That(error.Message).Contains("parent the others beneath it");
    }

    [Test]
    public async Task a_document_with_no_objects_is_refused()
    {
        var error = Assert.Throws<PrefabDocumentException>(
            () => PrefabDocumentSerializer.Parse("schema_version = 1\n", "empty.prefab"));

        await Assert.That(error!.Message).Contains("has no objects");
    }

    [Test]
    public async Task an_unrecognised_payload_survives_a_round_trip()
    {
        // What makes it safe to open a document full of components this build has never heard of.
        var text = $"schema_version = 1\n{Object(CrateGuid)}" +
                   $"\n[[objects.components]]\nid = \"{RenderableId}\"\ntype = \"Nobody.Knows\"\n" +
                   "Count = 3\nRatio = 0.5\nFlag = true\nList = [1, 2]\n" +
                   "\n[objects.components.Nested]\nInner = \"deep\"\n";

        await Assert.That(PrefabDocumentSerializer.Write(PrefabDocumentSerializer.Parse(text, "x.scene")))
            .IsEqualTo(text);
    }

    [Test]
    public async Task a_malformed_meta_parent_is_refused()
    {
        // Before the shape check a non-UUID Parent read as "no parent" — an object silently
        // promoted to a root is exactly the misread the strict reader exists to prevent.
        var text = $"schema_version = 1\n{Object(CrateGuid)}{Object(LidGuid, "lid", "Parent = \"not-a-guid\"\n")}";

        await Assert.That(Rejects(text).Message).Contains("meta.Parent");
    }

    [Test]
    public async Task a_dropped_marker_without_a_target_is_refused()
    {
        // Dropping addresses a prefab child; on a plain object it is ignored, and on an instance
        // it deletes the whole subtree — neither is ever what the author meant.
        var text = $"schema_version = 1\n{Object(CrateGuid, "x", "Dropped = true\n")}";

        await Assert.That(Rejects(text).Message).Contains("Dropped");
    }

    [Test]
    public async Task a_short_transform_position_is_refused()
    {
        var text = $"schema_version = 1\n{Object(CrateGuid)}" +
                   $"\n[[objects.components]]\nid = \"{Transform}\"\ntype = \"transform\"\nPosition = [0.0, 1.5]\n";

        await Assert.That(Rejects(text).Message).Contains("array of 3 numbers");
    }

    [Test]
    public async Task a_three_element_rotation_is_refused()
    {
        var text = $"schema_version = 1\n{Object(CrateGuid)}" +
                   $"\n[[objects.components]]\nid = \"{Transform}\"\ntype = \"transform\"\nRotation = [0.0, 0.0, 0.0]\n";

        await Assert.That(Rejects(text).Message).Contains("array of 4 numbers");
    }

    [Test]
    public async Task a_misspelled_transform_field_is_refused()
    {
        // 'Postion' used to bake silently as the origin. transform is a closed set precisely so
        // a typo is an error and not a teleport.
        var text = $"schema_version = 1\n{Object(CrateGuid)}" +
                   $"\n[[objects.components]]\nid = \"{Transform}\"\ntype = \"transform\"\nPostion = [0.0, 1.5, 0.0]\n";

        await Assert.That(Rejects(text).Message).Contains("Postion");
    }

    [Test]
    public async Task a_game_extended_meta_field_rides_along()
    {
        // meta's payload stays open — only the fields the format defines are shape-checked.
        var text = $"schema_version = 1\n{Object(CrateGuid, "x", "Zone = \"hub\"\n")}";

        await Assert.That(PrefabDocumentSerializer.Write(PrefabDocumentSerializer.Parse(text, "x.scene")))
            .IsEqualTo(text);
    }

    [Test]
    public async Task a_payload_using_a_reserved_key_is_refused_at_construction()
    {
        // The named error ReservedKeys promises: built with a payload 'id', the component would
        // collide with its own structure on write, as a duplicate key nobody could diagnose.
        var data = new CanonicalTomlTable { { PrefabComponent.IdKey, "collides" } };
        try
        {
            _ = new PrefabComponent(Guid.Parse(RenderableId), data: data);
            throw new Exception("expected the component to be refused");
        }
        catch (ArgumentException error)
        {
            await Assert.That(error.Message).Contains("reserved");
        }
    }
}
