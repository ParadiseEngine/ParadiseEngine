using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Paradise.Export.Data;
using Paradise.Export.Geometry;
using Paradise.Export.Serialization;

namespace Paradise.Export.Tests;

// Validates the exported entity *shape* — the DTO→JSON path for a realistic object (name +
// transform + collider + rigidbody). It builds the DTOs directly, so it pins the serialized
// structure the runtime consumes without depending on any editor.
//
// Since schema v5 an entity IS its component list, so what this asserts is a shape with no
// privileged tier in it: the name and the placement are entries in the same array as the collider,
// found the same way, and nothing is a key at entity level because there are no keys at entity
// level.
public class EntityDocumentShapeTests
{
    private static readonly Vector3 Position = new(1f, 0f, 2f);

    private static List<AuthoredComponentData> BuildBoxEntity()
    {
        // Right-handed contract = Godot-native; values are written verbatim.
        var components = new List<AuthoredComponentData>();
        components.Set(new NameComponentData { Value = "Crate" });
        components.Set(new TransformComponentData
        {
            World = ContractMatrix.Trs(Position, Quaternion.Identity, Vector3.One),
        });
        components.Set(new RenderableComponentData());
        components.Set(new ColliderComponentData
        {
            Colliders = new List<ColliderShapeData>
            {
                new()
                {
                    Id = "Box",
                    Path = "",
                    ShapeType = PhysicsShapeType.Box,
                    Size = ColliderScaleFold.BoxSize(new Vector3(2f, 4f, 6f), Vector3.One),
                },
            },
        });
        components.Set(new RigidbodyComponentData { BodyType = PhysicsBodyType.Static, Mass = 0f });
        return components;
    }

    [Test]
    public async Task entity_serializes_as_a_bare_component_array()
    {
        var document = new LevelData { Entities = { BuildBoxEntity() } };
        JsonNode json = JsonNode.Parse(ExportJsonWriter.SerializeToString(document))!;

        JsonNode entity = json["Entities"]![0]!;
        // The whole assertion of v5: an entity has no shape of its own to get wrong.
        await Assert.That(entity.GetValueKind()).IsEqualTo(JsonValueKind.Array);

        await Assert.That((string?)Payload(entity, typeof(NameComponentData))["Value"])
            .IsEqualTo("Crate");

        JsonNode collider = Payload(entity, typeof(ColliderComponentData))["Colliders"]![0]!;
        await Assert.That((string?)collider["ShapeType"]).IsEqualTo("Box");
        await Assert.That((float)collider["Size"]![1]!).IsEqualTo(4f);

        await Assert.That((string?)Payload(entity, typeof(RigidbodyComponentData))["BodyType"])
            .IsEqualTo("Static");

        // Renderable is a present (empty) payload, since the object has a model. Its ENTRY's
        // absence is what "no mesh" means — the same statement made once instead of by a null in
        // a fixed slot.
        await Assert.That(Payload(entity, typeof(RenderableComponentData)).GetValueKind())
            .IsEqualTo(JsonValueKind.Object);
    }

    /// <summary>One component's Data, found by the CLR name the entry carries. The list has no
    /// fixed positions, so a test that indexed it would pin the editor's emission order rather
    /// than the shape it means to assert.</summary>
    private static JsonNode Payload(JsonNode entity, Type type)
    {
        foreach (JsonNode? component in entity.AsArray())
        {
            if ((string?)component!["Type"] == type.FullName)
            {
                return component["Data"]!;
            }
        }
        throw new InvalidOperationException($"no {type.Name} entry on this entity");
    }

    [Test]
    public async Task transform_is_written_verbatim_right_handed()
    {
        var document = new LevelData { Entities = { BuildBoxEntity() } };
        JsonNode json = JsonNode.Parse(ExportJsonWriter.SerializeToString(document))!;
        var world = (JsonArray)Payload(json["Entities"]![0]!, typeof(TransformComponentData))["World"]!;

        // Column-major float[16]: the translation is the last column, elements 12..14. Right-handed
        // contract, so Godot's (1,0,2) is written verbatim rather than flipped.
        await Assert.That((float)world[12]!).IsEqualTo(1f);
        await Assert.That((float)world[14]!).IsEqualTo(2f);
    }

    [Test]
    public async Task an_object_round_trips_through_the_reader()
    {
        var document = new LevelData { Entities = { BuildBoxEntity() } };
        LevelData read = ExportJsonReader.ReadLevel(ExportJsonWriter.SerializeToString(document));

        await Assert.That(read.Entities.Count).IsEqualTo(1);
        await Assert.That(read.Entities[0].Get<NameComponentData>()?.Value).IsEqualTo("Crate");
        // TRANSPOSED to read the translation: the contract's matrices are column-vector, and
        // System.Numerics' Translation reads the row-vector slot. Getting this backwards reads
        // <0,0,0> for every object, which is exactly the bug the wire assertion above pins.
        System.Numerics.Matrix4x4 world = read.Entities[0].Get<TransformComponentData>()!.World;
        await Assert.That(Matrix4x4.Transpose(world).Translation).IsEqualTo(Position);
    }
}
