using System.Numerics;

using Zio.FileSystems;

namespace Paradise.Assets.Documents.Test;

public class SceneDocumentTests
{
    private const string CrateGuid = "1c9a2f4e-0d3b-4c5a-8e6f-7a8b9c0d1e2f";
    private const string LidGuid = "2d0b3f5f-1e4c-5d6b-9f70-8b9c0d1e2f30";
    private const string RenderableId = "f2c0357e-94dd-4a5a-9803-518066cb54b2";

    private const string Canonical =
        "schema_version = 1\n" +
        "\n[[objects]]\n" +
        $"guid = \"{CrateGuid}\"\n" +
        "name = \"crate_01\"\n" +
        "\n[objects.transform]\n" +
        "position = [0.0, 1.5, 0.0]\n" +
        "rotation = [0.0, 0.0, 0.0, 1.0]\n" +
        "scale = [1.0, 1.0, 1.0]\n" +
        "\n[[objects.components]]\n" +
        $"id = \"{RenderableId}\"\n" +
        "type = \"Paradise.Export.Data.RenderableComponentData\"\n" +
        "\n[objects.components.data]\n" +
        "Mesh = \"models/crate.glb\"\n" +
        "\n[[objects]]\n" +
        $"guid = \"{LidGuid}\"\n" +
        "name = \"lid\"\n" +
        $"parent = \"{CrateGuid}\"\n";

    [Test]
    public async Task a_canonical_document_round_trips_byte_for_byte()
    {
        // THE property of the format: read → write must be the identity on canonical input,
        // or every tool touching a scene would litter diffs with reformatting.
        var document = SceneDocumentSerializer.Parse(Canonical, "district.scene.toml");

        await Assert.That(SceneDocumentSerializer.Write(document)).IsEqualTo(Canonical);
    }

    [Test]
    public async Task the_model_reflects_the_document()
    {
        var document = SceneDocumentSerializer.Parse(Canonical, "district.scene.toml");

        await Assert.That(document.Objects.Count).IsEqualTo(2);
        var crate = document.Objects[0];
        await Assert.That(crate.Name).IsEqualTo("crate_01");
        await Assert.That(crate.Parent).IsNull();
        await Assert.That(crate.Transform.Position).IsEqualTo(new Vector3(0f, 1.5f, 0f));
        await Assert.That(crate.Components.Count).IsEqualTo(1);
        await Assert.That(crate.Components[0].Id).IsEqualTo(Guid.Parse(RenderableId));

        var lid = document.Objects[1];
        await Assert.That(lid.Parent).IsEqualTo(Guid.Parse(CrateGuid));
        await Assert.That(lid.Transform).IsEqualTo(SceneTransform.Identity);
        await Assert.That(lid.Components.Count).IsEqualTo(0);
    }

    [Test]
    public async Task an_identity_transform_is_omitted_on_write_and_defaulted_on_read()
    {
        var document = new SceneDocument();
        document.Objects.Add(new SceneObject(Guid.Parse(CrateGuid), "crate"));

        var text = SceneDocumentSerializer.Write(document);
        await Assert.That(text).IsEqualTo(
            "schema_version = 1\n\n[[objects]]\n" + $"guid = \"{CrateGuid}\"\n" + "name = \"crate\"\n");
        await Assert.That(SceneDocumentSerializer.Parse(text, "x").Objects[0].Transform).IsEqualTo(SceneTransform.Identity);
    }

    [Test]
    public async Task an_empty_scene_is_just_its_version()
    {
        var text = SceneDocumentSerializer.Write(new SceneDocument());

        await Assert.That(text).IsEqualTo("schema_version = 1\n");
        await Assert.That(SceneDocumentSerializer.Parse(text, "x").Objects.Count).IsEqualTo(0);
    }

    [Test]
    public async Task component_order_survives_the_round_trip()
    {
        // Component order is data: the runtime applies entries in document order, and the
        // export contract calls that order load-bearing.
        var sceneObject = new SceneObject(Guid.Parse(CrateGuid), "crate");
        sceneObject.Components.Add(new SceneComponent(Guid.Parse(LidGuid)));
        sceneObject.Components.Add(new SceneComponent(Guid.Parse(RenderableId)));
        var document = new SceneDocument();
        document.Objects.Add(sceneObject);

        var reread = SceneDocumentSerializer.Parse(SceneDocumentSerializer.Write(document), "x");

        await Assert.That(reread.Objects[0].Components.Select(c => c.Id).ToArray())
            .IsEquivalentTo(new[] { Guid.Parse(LidGuid), Guid.Parse(RenderableId) });
    }

    [Test]
    public async Task guids_parse_undashed_but_write_hyphenated()
    {
        // The Godot host stored 32-digit ids in node metadata; migrated scenes keep their
        // identities, but the canonical write normalizes the spelling.
        var undashed = CrateGuid.Replace("-", "");
        var document = SceneDocumentSerializer.Parse(
            $"schema_version = 1\n\n[[objects]]\nguid = \"{undashed}\"\nname = \"crate\"\n", "x");

        await Assert.That(document.Objects[0].Guid).IsEqualTo(Guid.Parse(CrateGuid));
        await Assert.That(SceneDocumentSerializer.Write(document)).Contains($"guid = \"{CrateGuid}\"");
    }

    [Test]
    [Arguments("schema_version = 2\n", "schema_version = 2")]
    [Arguments("objects = 3\nschema_version = 1\n", "array of tables")]
    [Arguments("schema_version = 1\nextra = 1\n", "unknown key 'extra'")]
    public async Task document_level_problems_name_the_offence(string toml, string fragment)
    {
        var error = await Assert.That(() => SceneDocumentSerializer.Parse(toml, "bad.scene.toml"))
            .Throws<SceneDocumentException>();

        await Assert.That(error!.Message).Contains(fragment);
        await Assert.That(error.Message).Contains("bad.scene.toml");
    }

    [Test]
    public async Task a_missing_version_is_an_error_not_a_default()
    {
        await Assert.That(() => SceneDocumentSerializer.Parse("", "x")).Throws<SceneDocumentException>();
    }

    [Test]
    [Arguments("guid = \"not-a-guid\"\nname = \"a\"", "non-empty UUID")]
    [Arguments("guid = \"00000000-0000-0000-0000-000000000000\"\nname = \"a\"", "non-empty UUID")]
    [Arguments("name = \"a\"", "missing 'guid'")]
    [Arguments("guid = \"1c9a2f4e-0d3b-4c5a-8e6f-7a8b9c0d1e2f\"", "missing 'name'")]
    [Arguments("guid = \"1c9a2f4e-0d3b-4c5a-8e6f-7a8b9c0d1e2f\"\nname = \"\"", "non-empty 'name'")]
    [Arguments("guid = \"1c9a2f4e-0d3b-4c5a-8e6f-7a8b9c0d1e2f\"\nname = \"a\"\nsurprise = 1", "unknown key 'surprise'")]
    public async Task object_level_problems_name_the_offence(string objectBody, string fragment)
    {
        var toml = $"schema_version = 1\n\n[[objects]]\n{objectBody}\n";

        var error = await Assert.That(() => SceneDocumentSerializer.Parse(toml, "x"))
            .Throws<SceneDocumentException>();

        await Assert.That(error!.Message).Contains(fragment);
    }

    [Test]
    public async Task duplicate_object_guids_are_rejected()
    {
        var toml =
            $"schema_version = 1\n\n[[objects]]\nguid = \"{CrateGuid}\"\nname = \"a\"\n" +
            $"\n[[objects]]\nguid = \"{CrateGuid}\"\nname = \"b\"\n";

        var error = await Assert.That(() => SceneDocumentSerializer.Parse(toml, "x"))
            .Throws<SceneDocumentException>();

        await Assert.That(error!.Message).Contains("twice");
    }

    [Test]
    public async Task duplicate_component_ids_on_one_object_are_rejected()
    {
        var toml =
            $"schema_version = 1\n\n[[objects]]\nguid = \"{CrateGuid}\"\nname = \"a\"\n" +
            $"\n[[objects.components]]\nid = \"{RenderableId}\"\n" +
            $"\n[[objects.components]]\nid = \"{RenderableId}\"\n";

        var error = await Assert.That(() => SceneDocumentSerializer.Parse(toml, "x"))
            .Throws<SceneDocumentException>();

        await Assert.That(error!.Message).Contains("twice");
    }

    [Test]
    public async Task a_dangling_parent_is_rejected()
    {
        var toml =
            $"schema_version = 1\n\n[[objects]]\nguid = \"{CrateGuid}\"\nname = \"a\"\nparent = \"{LidGuid}\"\n";

        var error = await Assert.That(() => SceneDocumentSerializer.Parse(toml, "x"))
            .Throws<SceneDocumentException>();

        await Assert.That(error!.Message).Contains("does not exist");
    }

    [Test]
    public async Task a_parent_cycle_is_rejected()
    {
        var toml =
            $"schema_version = 1\n" +
            $"\n[[objects]]\nguid = \"{CrateGuid}\"\nname = \"a\"\nparent = \"{LidGuid}\"\n" +
            $"\n[[objects]]\nguid = \"{LidGuid}\"\nname = \"b\"\nparent = \"{CrateGuid}\"\n";

        var error = await Assert.That(() => SceneDocumentSerializer.Parse(toml, "x"))
            .Throws<SceneDocumentException>();

        await Assert.That(error!.Message).Contains("cycle");
    }

    [Test]
    public async Task a_malformed_transform_is_rejected()
    {
        var toml =
            $"schema_version = 1\n\n[[objects]]\nguid = \"{CrateGuid}\"\nname = \"a\"\n" +
            "\n[objects.transform]\nposition = [0.0, 1.0]\nrotation = [0.0, 0.0, 0.0, 1.0]\nscale = [1.0, 1.0, 1.0]\n";

        var error = await Assert.That(() => SceneDocumentSerializer.Parse(toml, "x"))
            .Throws<SceneDocumentException>();

        await Assert.That(error!.Message).Contains("array of 3 numbers");
    }

    [Test]
    public async Task an_absent_payload_reads_as_an_empty_table()
    {
        // Matching AuthoredDocument: a component may be pure presence.
        var toml =
            $"schema_version = 1\n\n[[objects]]\nguid = \"{CrateGuid}\"\nname = \"a\"\n" +
            $"\n[[objects.components]]\nid = \"{RenderableId}\"\n";

        var document = SceneDocumentSerializer.Parse(toml, "x");

        await Assert.That(document.Objects[0].Components[0].Data.Count).IsEqualTo(0);
    }

    [Test]
    public async Task load_and_save_go_through_the_filesystem()
    {
        using var fileSystem = new MemoryFileSystem();
        fileSystem.CreateDirectory("/game/assets/scenes");
        var document = SceneDocumentSerializer.Parse(Canonical, "seed");

        SceneDocumentSerializer.Save(fileSystem, "/game/assets/scenes/district.scene.toml", document);
        var reread = SceneDocumentSerializer.Load(fileSystem, "/game/assets/scenes/district.scene.toml");

        await Assert.That(SceneDocumentSerializer.Write(reread)).IsEqualTo(Canonical);
    }

    [Test]
    public async Task a_missing_file_reports_the_path()
    {
        using var fileSystem = new MemoryFileSystem();

        var error = await Assert.That(() => SceneDocumentSerializer.Load(fileSystem, "/absent.scene.toml"))
            .Throws<SceneDocumentException>();

        await Assert.That(error!.Message).Contains("/absent.scene.toml");
    }
}
