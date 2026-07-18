using System.Numerics;
using Paradise.Export.Data;
using Paradise.Export.Serialization;

namespace Paradise.Export.Tests;

/// <summary>Round-trip guarantee for the new read half: writer output must deserialize back to
/// equal values through every converter (vectors, quaternions, matrices, Color32, enums).</summary>
public class ExportJsonReaderTests
{
    [Test]
    public async Task level_document_round_trips_through_write_and_read()
    {
        var document = new LevelData
        {
            Camera = new CameraData
            {
                Position = new Vector3(1.5f, 2.25f, -3.125f),
                Rotation = new Vector3(10f, 20f, 30f),
                OrthographicSize = 5.5f,
            },
            NavMeshFile = "sample.navmesh.bin",
        };
        document.Entities.Add(new LevelEntityData
        {
            Id = "Ground",
            WorldMatrix = Matrix4x4.Identity,
            Components = new EntityComponentsData
            {
                Rigidbody = new RigidbodyComponentData { BodyType = PhysicsBodyType.Static },
                Collider = new ColliderComponentData
                {
                    Colliders =
                    [
                        new ColliderShapeData
                        {
                            Id = "Ground",
                            IsStatic = true,
                            Layer = 0,
                            ShapeType = PhysicsShapeType.Box,
                            LocalCenter = new Vector3(0f, -0.5f, 0f),
                            LocalRotation = Quaternion.Identity,
                            Size = new Vector3(20f, 1f, 20f),
                        },
                    ],
                },
            },
        });
        document.Entities.Add(new LevelEntityData
        {
            Id = "Ball1",
            WorldMatrix = Matrix4x4.CreateTranslation(1f, 0.85f, 2f),
            Materials = ["materials/mat_ball1.json"],
            Components = new EntityComponentsData
            {
                Renderable = new RenderableComponentData { Mesh = "meshes/abc.glb" },
                Rigidbody = new RigidbodyComponentData { BodyType = PhysicsBodyType.Dynamic, Mass = 2f },
                Collider = new ColliderComponentData
                {
                    Colliders = [new ColliderShapeData { ShapeType = PhysicsShapeType.Sphere, Radius = 0.35f }],
                },
            },
        });

        var parsed = ExportJsonReader.ReadLevel(ExportJsonWriter.SerializeToString(document));

        await Assert.That(parsed.SchemaVersion).IsEqualTo(LevelData.CurrentSchemaVersion);
        await Assert.That(parsed.Camera!.Position).IsEqualTo(new Vector3(1.5f, 2.25f, -3.125f));

        var ground = parsed.Entities[0];
        await Assert.That(ground.Components.Rigidbody!.BodyType).IsEqualTo(PhysicsBodyType.Static);
        await Assert.That(ground.Components.Collider!.Colliders[0].Size).IsEqualTo(new Vector3(20f, 1f, 20f));
        await Assert.That(ground.Components.Collider!.Colliders[0].ShapeType).IsEqualTo(PhysicsShapeType.Box);

        var entity = parsed.Entities[1];
        await Assert.That(entity.WorldMatrix!.Value.Translation).IsEqualTo(new Vector3(1f, 0.85f, 2f));
        await Assert.That(entity.Components.Renderable!.Mesh).IsEqualTo("meshes/abc.glb");
        await Assert.That(entity.Components.Rigidbody!.BodyType).IsEqualTo(PhysicsBodyType.Dynamic);
        await Assert.That(entity.Components.Collider!.Colliders[0].Radius).IsEqualTo(0.35f);
    }

    // The committed_sample_scene_parses cross-check against a real editor export lives in the
    // editor repo (ParadiseGodotEditor), which owns the committed data/ fixtures.
}
