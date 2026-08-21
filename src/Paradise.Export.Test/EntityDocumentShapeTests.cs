using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Paradise.Export.Data;
using Paradise.Export.Geometry;
using Paradise.Export.Serialization;

namespace Paradise.Export.Tests;

// Validates the exported entity *shape* — the DTO→JSON path for a realistic entity (collider +
// rigidbody + agent). It builds the DTOs directly, so it pins the serialized structure the runtime
// consumes without depending on any editor. Named for the document, not for the node that used to
// produce it: authoring now goes through [Authored] records and AuthoredComponentRouter.
public class EntityDocumentShapeTests
{
    private static LevelEntityData BuildBoxAgentEntity()
    {
        // Right-handed contract = Godot-native; values are written verbatim.
        Vector3 pos = new Vector3(1f, 0f, 2f);
        Quaternion rot = Quaternion.Identity;
        var entity = new LevelEntityData
        {
            Id = "Crate",
            EntityGuid = Guid.Parse("0123456789abcdef0123456789abcdef"),
            StableId = "Crate",
            Kind = "Prop",
            SpawnPhase = "LevelStart",
            Prefab = "models/crate.glb",
            LocalPosition = pos,
            LocalRotation = rot,
            LocalScale = Vector3.One,
            LocalMatrix = ContractMatrix.Trs(pos, rot, Vector3.One),
        };
        entity.Set(new RenderableComponentData());
        entity.Set(new ColliderComponentData
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
        entity.Set(new RigidbodyComponentData { BodyType = PhysicsBodyType.Static, Mass = 0f });
        return entity;
    }

    [Test]
    public async Task entity_serializes_with_expected_component_shape()
    {
        var document = new LevelData { Entities = { BuildBoxAgentEntity() } };
        JsonNode json = JsonNode.Parse(ExportJsonWriter.SerializeToString(document))!;

        JsonNode entity = json["Entities"]![0]!;
        await Assert.That((string?)entity["Id"]).IsEqualTo("Crate");
        await Assert.That((string?)entity["Kind"]).IsEqualTo("Prop");
        await Assert.That((string?)entity["Prefab"]).IsEqualTo("models/crate.glb");

        JsonNode collider = Payload(entity, typeof(ColliderComponentData))["Colliders"]![0]!;
        await Assert.That((string?)collider["ShapeType"]).IsEqualTo("Box");
        await Assert.That((float)collider["Size"]![1]!).IsEqualTo(4f);

        await Assert.That((string?)Payload(entity, typeof(RigidbodyComponentData))["BodyType"])
            .IsEqualTo("Static");

        // Renderable is a present (empty) payload, since the entity has a model. It used to be a
        // named key whose absence meant "no mesh"; now its ENTRY's absence means that, which is
        // the same statement made once instead of by a null in a fixed slot.
        await Assert.That(Payload(entity, typeof(RenderableComponentData)).GetValueKind())
            .IsEqualTo(JsonValueKind.Object);
        await Assert.That(entity["Components"]!.GetValueKind()).IsEqualTo(JsonValueKind.Array);
    }

    /// <summary>One component's Data, found by the CLR name the entry carries. The list has no
    /// fixed positions, so a test that indexed it would pin the editor's emission order rather
    /// than the shape it means to assert.</summary>
    private static JsonNode Payload(JsonNode entity, Type type)
    {
        foreach (JsonNode? component in entity["Components"]!.AsArray())
        {
            if ((string?)component!["Type"] == type.FullName)
            {
                return component["Data"]!;
            }
        }
        throw new InvalidOperationException($"no {type.Name} entry on this entity");
    }

    [Test]
    public async Task entity_local_position_is_verbatim_right_handed()
    {
        var document = new LevelData { Entities = { BuildBoxAgentEntity() } };
        JsonNode json = JsonNode.Parse(ExportJsonWriter.SerializeToString(document))!;
        JsonArray local = (JsonArray)json["Entities"]![0]!["LocalPosition"]!;

        // Right-handed contract: Godot (1,0,2) is written verbatim.
        await Assert.That((float)local[2]!).IsEqualTo(2f);
    }
}
