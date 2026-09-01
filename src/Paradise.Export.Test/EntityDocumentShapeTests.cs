using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Paradise.Export.Data;
using Paradise.Export.Serialization;

namespace Paradise.Export.Tests;

// Validates the exported entity *shape* for a realistic object (meta + transform + collider +
// mover). It builds the payloads directly, so it pins the serialized structure the runtime
// consumes without depending on any editor.
//
// Since schema v5 an entity IS its component list, and since v6 there is no privileged tier at
// all: identity and placement are entries in the same array as the collider, found the same way,
// carrying the authoring format's own field spellings.
public class EntityDocumentShapeTests
{
    internal static AuthoredComponentData Payload(Guid id, string? type, string json) =>
        new() { Id = id, Type = type, Data = JsonDocument.Parse(json).RootElement.Clone() };

    internal static List<AuthoredComponentData> BuildBoxEntity() =>
    [
        Payload(WellKnownEntityComponents.MetaId, WellKnownEntityComponents.MetaType,
            """{"Guid":"3f2a1b4c-5d6e-4f70-8192-a3b4c5d6e7f8","Name":"Crate"}"""),
        // Right-handed contract = Godot-native; values are written verbatim.
        Payload(WellKnownEntityComponents.TransformId, WellKnownEntityComponents.TransformType,
            """{"Position":[1.0,0.0,2.0],"Rotation":[0.0,0.0,0.0,1.0],"Scale":[1.0,1.0,1.0]}"""),
        Payload(TestComponentIds.CrateId, "Paradise.Export.Tests.CrateFixture",
            """{"Colliders":[{"Id":"Box","Path":"","ShapeType":"Box","Size":[2.0,4.0,6.0]}]}"""),
        Payload(TestComponentIds.MoverId, "Paradise.Export.Tests.MoverFixture",
            """{"Kind":"Static","Mass":0.0,"Clip":null}"""),
    ];

    [Test]
    public async Task entity_serializes_as_a_bare_component_array()
    {
        var document = new LevelData { Entities = { BuildBoxEntity() } };
        JsonNode json = JsonNode.Parse(ExportJsonWriter.SerializeToString(document))!;

        JsonNode entity = json["Entities"]![0]!;
        // The whole assertion of v5: an entity has no shape of its own to get wrong.
        await Assert.That(entity.GetValueKind()).IsEqualTo(JsonValueKind.Array);

        await Assert.That((string?)PayloadOf(entity, WellKnownEntityComponents.MetaId)["Name"])
            .IsEqualTo("Crate");

        JsonNode collider = PayloadOf(entity, TestComponentIds.CrateId)["Colliders"]![0]!;
        await Assert.That((string?)collider["ShapeType"]).IsEqualTo("Box");
        await Assert.That((float)collider["Size"]![1]!).IsEqualTo(4f);

        await Assert.That((string?)PayloadOf(entity, TestComponentIds.MoverId)["Kind"])
            .IsEqualTo("Static");
    }

    [Test]
    public async Task transform_is_written_verbatim_right_handed()
    {
        var document = new LevelData { Entities = { BuildBoxEntity() } };
        JsonNode json = JsonNode.Parse(ExportJsonWriter.SerializeToString(document))!;
        var position = (JsonArray)PayloadOf(
            json["Entities"]![0]!, WellKnownEntityComponents.TransformId)["Position"]!;

        // Local TRS since v6 — no matrix, no flatten. Right-handed contract, so Godot's (1,0,2)
        // is written verbatim rather than flipped.
        await Assert.That((float)position[0]!).IsEqualTo(1f);
        await Assert.That((float)position[2]!).IsEqualTo(2f);
    }

    [Test]
    public async Task an_object_round_trips_through_the_reader()
    {
        var document = new LevelData { Entities = { BuildBoxEntity() } };
        LevelData read = ExportJsonReader.ReadLevel(ExportJsonWriter.SerializeToString(document));

        await Assert.That(read.Entities.Count).IsEqualTo(1);
        var meta = read.Entities[0].Single(c => c.Id == WellKnownEntityComponents.MetaId).Data;
        await Assert.That(meta.GetProperty(WellKnownEntityComponents.Name).GetString())
            .IsEqualTo("Crate");
    }

    /// <summary>One component's Data, found by id. The list has no fixed positions, so a test
    /// that indexed it would pin an emission order rather than the shape it means to assert.</summary>
    private static JsonNode PayloadOf(JsonNode entity, Guid id)
    {
        foreach (JsonNode? component in entity.AsArray())
        {
            if (string.Equals((string?)component!["Id"], id.ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                return component["Data"]!;
            }
        }
        throw new InvalidOperationException($"no component with id {id} on this entity");
    }
}
