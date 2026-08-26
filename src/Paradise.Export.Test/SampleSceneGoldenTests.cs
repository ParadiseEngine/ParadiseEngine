using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Paradise.Export.Data;
using Paradise.Export.Geometry;
using Paradise.Export.Serialization;

namespace Paradise.Export.Tests;

// GOLDEN TEST. Reconstructs a small scene as LevelData and asserts our serializer reproduces the
// on-disk JSON byte-for-byte (newline-normalized). This pins the entire serialization stack —
// property order, float formatting, Color32 { r,g,b,a }, enum-by-name, null inclusion,
// scalar-vs-vector float rendering.
//
// WHAT IT IS AND IS NOT, since schema v5. The fixture used to be an external baseline: the Unity
// editor's own SampleScene.json, Z-mirrored into the contract's right-handed convention, so the
// test compared two independent implementations of the format. That comparison died with the
// document shape — there is no Camera block and no Lighting block for a Unity export to have
// written — and the fixture is now generated from this writer. So it catches DRIFT (a converter
// changed, a property moved, a float renders differently) and no longer catches a disagreement
// with another implementation. Regenerate it deliberately, never to make a red test go green:
// a diff here is the serialized contract changing, which is exactly the thing to look at.
//
// The values are the old baseline's, kept so the same float shapes are still exercised — the
// awkward ones (0.003921569f, 0.766044438f, an integer-valued 2f, a -1 mask) are why it is those
// numbers and not round ones.
public class SampleSceneGoldenTests
{
    [Test]
    public async Task serialized_sample_scene_matches_the_golden_document()
    {
        LevelData document = BuildSampleScene();
        string actual = Normalize(ExportJsonWriter.SerializeToString(document));
        string expected = Normalize(ReadFixture("SampleScene.expected.json"));

        await Assert.That(actual).IsEqualTo(expected);
    }

    /// <summary>
    /// The scene as v5 states it: two objects, each nothing but its components.
    ///
    /// The environment is an object of its own carrying nothing else, which is what "the scene's
    /// lighting is a component" means in practice — it is not attached to a light, or to a camera,
    /// or to a privileged first entity. The light is a second object, placed, carrying the light.
    /// </summary>
    private static LevelData BuildSampleScene()
    {
        var document = new LevelData();

        document.Entities.Add(new List<AuthoredComponentData>
        {
            AuthoredComponentList.Entry(new NameComponentData { Value = "Environment" }),
            AuthoredComponentList.Entry(new EnvironmentData
            {
                AmbientMode = "Skybox",
                AmbientColor = Color32.FromRgba(0.03529412f, 0.0431372561f, 0.05490196f, 1f),
                AmbientEquatorColor = Color32.FromRgba(0.0117647061f, 0.0156862754f, 0.0156862754f, 1f),
                AmbientGroundColor = Color32.FromRgba(0.003921569f, 0.003921569f, 0.003921569f, 1f),
                Exposure = 1f,
                FogEnabled = false,
                FogColor = Color32.FromRgba(0.215686277f, 0.215686277f, 0.215686277f, 1f),
                FogDensity = 0.01f,
            }),
        });

        document.Entities.Add(new List<AuthoredComponentData>
        {
            AuthoredComponentList.Entry(new NameComponentData { Value = "Directional Light" }),
            // ContractMatrix.Trs, NOT Matrix4x4.CreateTranslation. The contract's matrices are
            // column-vector — translation at flat indices 12/13/14 — and CreateTranslation builds
            // the row-vector form, which serializes the translation to 3/7/11 instead. Both are
            // matrices and both round-trip, so nothing fails; what breaks is every consumer that
            // follows the documented rule and transposes, which then reads this light at the
            // origin while its own SceneLightData.Position says (0, 3, 0).
            //
            // This fixture is generated from the writer rather than from an independent baseline,
            // so it is the reference an exporter author copies the convention from. It has to be
            // the convention.
            AuthoredComponentList.Entry(new TransformComponentData
            {
                World = ContractMatrix.Trs(new Vector3(0f, 3f, 0f), Quaternion.Identity, Vector3.One),
            }),
            AuthoredComponentList.Entry(new SceneLightData
            {
                Id = "Directional Light",
                Type = "Directional",
                Position = new Vector3(0f, 3f, 0f),
                Direction = new Vector3(0.3213938f, 0.766044438f, 0.5566705f),
                Color = Color32.FromRgba(1f, 0.78039217f, 0.619607866f, 1f),
                Enabled = true,
                Intensity = 2f,
                UseColorTemperature = true,
                ColorTemperature = 5000f,
                Range = 10f,
                SpotAngle = 30f,
                InnerSpotAngle = 21.80208f,
                AreaSize = Vector2.Zero,
                ShadowsEnabled = true,
                ShadowType = "Soft",
                ShadowStrength = 1f,
                LayerMask = -1,
                RenderingLayerMask = 1,
                Group = "Default",
            }),
        });

        return document;
    }

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    // Normalize line endings and trailing whitespace so the comparison is about data, not the
    // platform's newline style (the on-disk fixture carries a trailing newline from the atomic
    // writer; SerializeToString does not).
    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');
}
