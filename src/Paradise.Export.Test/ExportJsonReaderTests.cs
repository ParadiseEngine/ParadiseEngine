using System.Collections.Generic;
using System.Numerics;
using Paradise.Export.Data;
using Paradise.Export.Serialization;

namespace Paradise.Export.Tests;

/// <summary>Round-trip guarantee for the read half: writer output must deserialize back to equal
/// values through every converter (vectors, quaternions, matrices, Color32, enums).</summary>
public class ExportJsonReaderTests
{
    [Test]
    public async Task level_document_round_trips_through_write_and_read()
    {
        var document = new LevelData();
        document.Entities.Add(new List<AuthoredComponentData>
        {
            AuthoredComponentList.Entry(new NameComponentData { Value = "Ground" }),
            AuthoredComponentList.Entry(new TransformComponentData { World = Matrix4x4.Identity }),
            AuthoredComponentList.Entry(new RigidbodyComponentData { BodyType = PhysicsBodyType.Static }),
            AuthoredComponentList.Entry(new ColliderComponentData
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
            }),
        });
        document.Entities.Add(new List<AuthoredComponentData>
        {
            AuthoredComponentList.Entry(new NameComponentData { Value = "Ball1" }),
            AuthoredComponentList.Entry(new TransformComponentData
            {
                World = Matrix4x4.CreateTranslation(1f, 0.85f, 2f),
            }),
            AuthoredComponentList.Entry(new RenderableComponentData
            {
                Mesh = "meshes/abc.glb",
                Materials = ["materials/mat_ball1.json"],
            }),
            AuthoredComponentList.Entry(new RigidbodyComponentData
            {
                BodyType = PhysicsBodyType.Dynamic,
                Mass = 2f,
            }),
            AuthoredComponentList.Entry(new ColliderComponentData
            {
                Colliders = [new ColliderShapeData { ShapeType = PhysicsShapeType.Sphere, Radius = 0.35f }],
            }),
        });

        var parsed = ExportJsonReader.ReadLevel(ExportJsonWriter.SerializeToString(document));

        await Assert.That(parsed.SchemaVersion).IsEqualTo(LevelData.CurrentSchemaVersion);

        var ground = parsed.Entities[0];
        await Assert.That(ground.Get<NameComponentData>()!.Value).IsEqualTo("Ground");
        await Assert.That(ground.Get<RigidbodyComponentData>()!.BodyType).IsEqualTo(PhysicsBodyType.Static);
        await Assert.That(ground.Get<ColliderComponentData>()!.Colliders[0].Size).IsEqualTo(new Vector3(20f, 1f, 20f));
        await Assert.That(ground.Get<ColliderComponentData>()!.Colliders[0].ShapeType).IsEqualTo(PhysicsShapeType.Box);

        var entity = parsed.Entities[1];
        await Assert.That(entity.Get<TransformComponentData>()!.World.Translation)
            .IsEqualTo(new Vector3(1f, 0.85f, 2f));
        await Assert.That(entity.Get<RenderableComponentData>()!.Mesh).IsEqualTo("meshes/abc.glb");
        await Assert.That(entity.Get<RigidbodyComponentData>()!.BodyType).IsEqualTo(PhysicsBodyType.Dynamic);
        await Assert.That(entity.Get<ColliderComponentData>()!.Colliders[0].Radius).IsEqualTo(0.35f);
    }

    /// <summary>
    /// A v4 document is REFUSED, and this is the test that earns the version gate.
    ///
    /// Without it a v4 document is not an error: its entities are JSON OBJECTS where this build
    /// expects arrays, so <c>Entities</c> deserializes to nothing and the scene loads as an empty
    /// world with no diagnostic anywhere.
    /// </summary>
    [Test]
    public async Task a_v4_document_is_refused_by_name()
    {
        const string v4 = """
            {"SchemaVersion":4,"Entities":[{"Id":"Ground","Components":[]}]}
            """;

        await Assert.That(() => ExportJsonReader.ReadLevel(v4))
            .Throws<System.Text.Json.JsonException>();
    }
}
