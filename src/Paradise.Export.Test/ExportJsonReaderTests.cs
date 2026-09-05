using System.Collections.Generic;
using System.Text.Json;
using Paradise.Export.Data;
using Paradise.Export.Serialization;

namespace Paradise.Export.Tests;

/// <summary>Round-trip guarantee for the read half: writer output must deserialize back to equal
/// values, payloads carried verbatim — including the well-known meta/transform ones a v6
/// document ships for every entity.</summary>
public class ExportJsonReaderTests
{
    private static AuthoredComponentData Payload(Guid id, string? type, string json) =>
        new() { Id = id, Type = type, Data = JsonDocument.Parse(json).RootElement.Clone() };

    [Test]
    public async Task level_document_round_trips_through_write_and_read()
    {
        var document = new PrefabData();
        document.Entities.Add(new List<AuthoredComponentData>
        {
            Payload(WellKnownEntityComponents.MetaId, WellKnownEntityComponents.MetaType,
                """{"Guid":"3f2a1b4c-5d6e-4f70-8192-a3b4c5d6e7f8","Name":"Ground"}"""),
            Payload(WellKnownEntityComponents.TransformId, WellKnownEntityComponents.TransformType,
                """{"Position":[0.0,-0.5,0.0],"Rotation":[0.0,0.0,0.0,1.0],"Scale":[20.0,1.0,20.0]}"""),
            Payload(TestComponentIds.CrateId, "Paradise.Export.Tests.CrateFixture",
                """{"Colliders":[{"Id":"Ground","IsStatic":true,"ShapeType":"Box","Size":[20,1,20]}]}"""),
        });
        document.Entities.Add(new List<AuthoredComponentData>
        {
            Payload(WellKnownEntityComponents.MetaId, WellKnownEntityComponents.MetaType,
                """{"Guid":"9a8b7c6d-5e4f-4031-8213-4c5d6e7f8091","Name":"Ball1","Parent":"3f2a1b4c-5d6e-4f70-8192-a3b4c5d6e7f8"}"""),
            Payload(WellKnownEntityComponents.TransformId, WellKnownEntityComponents.TransformType,
                """{"Position":[1.0,0.85,2.0],"Rotation":[0.0,0.0,0.0,1.0],"Scale":[1.0,1.0,1.0]}"""),
            Payload(TestComponentIds.MoverId, "Paradise.Export.Tests.MoverFixture",
                """{"Kind":"Dynamic","Mass":2.0}"""),
        });

        var parsed = ExportJsonReader.ReadPrefab(ExportJsonWriter.SerializeToString(document));

        await Assert.That(parsed.SchemaVersion).IsEqualTo(PrefabData.CurrentSchemaVersion);
        await Assert.That(parsed.Entities.Count).IsEqualTo(2);

        // The well-known payloads cross over untouched: identity and hierarchy SURVIVE.
        var ballMeta = parsed.Entities[1]
            .Single(c => c.Id == WellKnownEntityComponents.MetaId).Data;
        await Assert.That(ballMeta.GetProperty(WellKnownEntityComponents.Name).GetString())
            .IsEqualTo("Ball1");
        await Assert.That(ballMeta.GetProperty(WellKnownEntityComponents.Parent).GetString())
            .IsEqualTo("3f2a1b4c-5d6e-4f70-8192-a3b4c5d6e7f8");

        var ballTransform = parsed.Entities[1]
            .Single(c => c.Id == WellKnownEntityComponents.TransformId).Data;
        await Assert.That(ballTransform.GetProperty(WellKnownEntityComponents.Position)[1].GetSingle())
            .IsEqualTo(0.85f);

        // A game payload materializes through the game's registry, values intact.
        var mover = AuthoredComponentRouter.Materialize(parsed.Entities[1], TestRegistry.Default)
            .OfType<MoverFixture>().Single();
        await Assert.That(mover.Kind).IsEqualTo(MoverKind.Dynamic);
        await Assert.That(mover.Mass).IsEqualTo(2f);

        var crate = AuthoredComponentRouter.Materialize(parsed.Entities[0], TestRegistry.Default)
            .OfType<CrateFixture>().Single();
        await Assert.That(crate.Colliders.Single().Size)
            .IsEqualTo(new System.Numerics.Vector3(20f, 1f, 20f));
        await Assert.That(crate.Colliders.Single().ShapeType).IsEqualTo(PhysicsShapeType.Box);
    }

    /// <summary>
    /// A v5 document is REFUSED, and this is the test that earns the version gate: its entities
    /// carry baked world matrices this build has no reader for, so letting it parse would load a
    /// scene whose placement silently means nothing.
    /// </summary>
    [Test]
    public async Task a_v5_document_is_refused_by_name()
    {
        const string v5 = """
            {"SchemaVersion":5,"Entities":[[]]}
            """;

        await Assert.That(() => ExportJsonReader.ReadPrefab(v5))
            .Throws<System.Text.Json.JsonException>();
    }
}
