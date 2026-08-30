using Paradise.Authoring;

namespace Paradise.Assets.Documents.Test;

/// <summary>
/// The all-components scene document: identity, name, parent and placement are components like
/// any other, which is what lets a prefab instance override them through one mechanism.
/// </summary>
public class SceneDocumentTests
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

    private static SceneDocumentException Rejects(string text)
    {
        try
        {
            SceneDocumentSerializer.Parse(text, "x.scene");
        }
        catch (SceneDocumentException error)
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
        var document = SceneDocumentSerializer.Parse(Canonical, "district.scene");

        await Assert.That(SceneDocumentSerializer.Write(document)).IsEqualTo(Canonical);
    }

    [Test]
    public async Task identity_name_and_parent_are_read_from_the_meta_component()
    {
        var document = SceneDocumentSerializer.Parse(Canonical, "district.scene");

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
        var document = SceneDocumentSerializer.Parse(Canonical, "district.scene");

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
        var document = SceneDocumentSerializer.Parse(Canonical, "district.scene");
        var mesh = document.Objects[0].Component(Guid.Parse(RenderableId))!.Data.Value("Mesh");

        var reference = AssetReferenceCodec.Read(mesh, "on the crate", m => new SceneDocumentException("x", m));

        await Assert.That(reference!.Path).IsEqualTo("Models/crate.glb");
    }

    [Test]
    public async Task an_empty_scene_is_just_its_version()
    {
        await Assert.That(SceneDocumentSerializer.Write(new SceneDocument())).IsEqualTo("schema_version = 1\n");
    }

    [Test]
    public async Task component_order_survives_the_round_trip()
    {
        // Order is data: the runtime applies components in document order.
        var document = SceneDocumentSerializer.Parse(Canonical, "x.scene");
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

        var document = SceneDocumentSerializer.Parse(text, "x.scene");

        await Assert.That(document.Objects[0].Component(Guid.Parse(RenderableId))!.Removed).IsTrue();
        await Assert.That(SceneDocumentSerializer.Write(document)).IsEqualTo(text);
    }

    [Test]
    public async Task a_prefab_reference_round_trips()
    {
        var text = "schema_version = 1\n\n[[objects]]\n" +
                   $"prefab = {{ guid = \"{LidGuid}\", path = \"prefabs/rail.prefab\" }}\n" +
                   $"\n[[objects.components]]\nid = \"{Meta}\"\ntype = \"meta\"\nGuid = \"{CrateGuid}\"\n";

        var document = SceneDocumentSerializer.Parse(text, "x.scene");

        await Assert.That(document.Objects[0].Prefab!.Path).IsEqualTo("prefabs/rail.prefab");
        await Assert.That(SceneDocumentSerializer.Write(document)).IsEqualTo(text);
    }

    [Test]
    public async Task a_target_carrier_needs_no_identity_of_its_own()
    {
        // A carrier addresses a prefab-local object; the resolved child's guid is always minted,
        // so requiring one here would mean inventing an identity nothing uses.
        var text = "schema_version = 1\n\n[[objects]]\n\n[[objects.components]]\n" +
                   $"id = \"{Meta}\"\ntype = \"meta\"\nParent = \"{CrateGuid}\"\nTarget = \"{LidGuid}\"\n" +
                   Object(CrateGuid);

        var document = SceneDocumentSerializer.Parse(text, "x.scene");

        await Assert.That(document.Objects[0].Target).IsEqualTo(Guid.Parse(LidGuid));
        await Assert.That(document.Objects[0].Guid).IsNull();
    }

    [Test]
    public async Task the_single_root_is_inferred_from_the_absence_of_a_parent()
    {
        var document = SceneDocumentSerializer.Parse(Canonical, "x.scene");

        await Assert.That(document.SingleRoot()!.Guid).IsEqualTo(Guid.Parse(CrateGuid));
    }

    [Test]
    public async Task a_document_with_two_roots_has_no_single_root()
    {
        // Not an error here — the serializer reads scenes too, and a scene has many roots. It is
        // PrefabDocument that requires exactly one.
        var document = SceneDocumentSerializer.Parse($"schema_version = 1\n{Object(CrateGuid)}{Object(LidGuid)}", "x");

        await Assert.That(document.SingleRoot()).IsNull();
    }

    [Test]
    public async Task an_unrecognised_payload_survives_a_round_trip()
    {
        // What makes it safe to open a document full of components this build has never heard of.
        var text = $"schema_version = 1\n{Object(CrateGuid)}" +
                   $"\n[[objects.components]]\nid = \"{RenderableId}\"\ntype = \"Nobody.Knows\"\n" +
                   "Count = 3\nRatio = 0.5\nFlag = true\nList = [1, 2]\n" +
                   "\n[objects.components.Nested]\nInner = \"deep\"\n";

        await Assert.That(SceneDocumentSerializer.Write(SceneDocumentSerializer.Parse(text, "x.scene")))
            .IsEqualTo(text);
    }
}
