using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Paradise.Export.Data;
using Paradise.Export.Serialization;

namespace Paradise.Export.Tests;

// GOLDEN TEST. Reconstructs a small v6 scene as PrefabData and asserts our serializer reproduces
// the on-disk JSON byte-for-byte (newline-normalized). This pins the entire serialization stack —
// property order, float formatting, null inclusion, payload passthrough, guid spelling.
//
// The fixture is generated from this writer, so it catches DRIFT (a property moved, a float
// renders differently) rather than a disagreement with another implementation. Regenerate it
// deliberately, never to make a red test go green: run with PARADISE_REGENERATE_GOLDEN=1 and
// review the diff — a diff here is the serialized contract changing, which is exactly the thing
// to look at.
//
// The values keep awkward float shapes on purpose (0.766044438f-style fractions, an
// integer-valued 2, a negative) so the same renderings the old baseline exercised still are.
public class SampleSceneGoldenTests
{
    [Test]
    public async Task serialized_sample_scene_matches_the_golden_document()
    {
        PrefabData document = BuildSampleScene();
        string actual = Normalize(ExportJsonWriter.SerializeToString(document));

        var fixture = FixturePath("SampleScene.expected.json");
        if (Environment.GetEnvironmentVariable("PARADISE_REGENERATE_GOLDEN") == "1")
        {
            File.WriteAllText(fixture, ExportJsonWriter.SerializeToString(document));
        }

        string expected = Normalize(File.ReadAllText(fixture));
        await Assert.That(actual).IsEqualTo(expected);
    }

    /// <summary>
    /// The scene as v6 states it: two objects, each nothing but its components — identity and
    /// placement riding as the well-known meta/transform payloads, everything else the game's.
    /// </summary>
    private static PrefabData BuildSampleScene()
    {
        var document = new PrefabData();

        document.Entities.Add(new List<AuthoredComponentData>
        {
            Payload(WellKnownEntityComponents.MetaId, WellKnownEntityComponents.MetaType,
                """{"Guid":"3f2a1b4c-5d6e-4f70-8192-a3b4c5d6e7f8","Name":"Ground"}"""),
            Payload(WellKnownEntityComponents.TransformId, WellKnownEntityComponents.TransformType,
                """{"Position":[0.0,-0.5,0.0],"Rotation":[0.0,0.766044438,0.0,0.642787635],"Scale":[20.0,1.0,20.0]}"""),
            Payload(TestComponentIds.CrateId, "Paradise.Export.Tests.CrateFixture",
                """{"Colliders":[{"Id":"Ground","IsStatic":true,"ShapeType":"Box","Size":[20.0,1.0,20.0],"LayerMask":-1}]}"""),
        });

        document.Entities.Add(new List<AuthoredComponentData>
        {
            Payload(WellKnownEntityComponents.MetaId, WellKnownEntityComponents.MetaType,
                """{"Guid":"9a8b7c6d-5e4f-4031-8213-4c5d6e7f8091","Name":"Ball","Parent":"3f2a1b4c-5d6e-4f70-8192-a3b4c5d6e7f8"}"""),
            Payload(WellKnownEntityComponents.TransformId, WellKnownEntityComponents.TransformType,
                """{"Position":[1.0,0.85,2.0],"Rotation":[0.0,0.0,0.0,1.0],"Scale":[2.0,2.0,2.0]}"""),
            Payload(TestComponentIds.MoverId, "Paradise.Export.Tests.MoverFixture",
                """{"Kind":"Dynamic","Mass":2.5,"MoveSpeed":0.003921569,"Clip":null}"""),
        });

        return document;
    }

    private static AuthoredComponentData Payload(Guid id, string? type, string json) =>
        new() { Id = id, Type = type, Data = JsonDocument.Parse(json).RootElement.Clone() };

    /// <summary>The SOURCE fixture path (via CallerFilePath), so a gated regeneration writes the
    /// committed file rather than the build-output copy that would be overwritten next build.</summary>
    private static string FixturePath(
        string name, [System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "Fixtures", name);

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
}
