using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Paradise.Export.Data;
using Paradise.Export.Geometry;
using Paradise.Export.Serialization;

namespace Paradise.Export.Tests;

// Validates the entity export *shape* the Godot adapter produces — the DTO→JSON path for a
// realistic entity (collider + rigidbody + agent). The Godot scene-walk that fills these DTOs is
// verified manually in-editor; this pins the serialized structure the runtime consumes.
public class EntityExportShapeTests
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
        entity.Components.Renderable = new RenderableComponentData();
        entity.Components.Collider = new ColliderComponentData
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
        };
        entity.Components.Rigidbody = new RigidbodyComponentData { BodyType = PhysicsBodyType.Static, Mass = 0f };
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

        JsonNode collider = entity["Components"]!["Collider"]!["Colliders"]![0]!;
        await Assert.That((string?)collider["ShapeType"]).IsEqualTo("Box");
        await Assert.That((float)collider["Size"]![1]!).IsEqualTo(4f);

        await Assert.That((string?)entity["Components"]!["Rigidbody"]!["BodyType"]).IsEqualTo("Static");
        // Renderable is a present (empty) component object, since the entity has a model.
        await Assert.That(entity["Components"]!["Renderable"]!.GetValueKind()).IsEqualTo(JsonValueKind.Object);
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
