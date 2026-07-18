using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Paradise.Export.Data;
using Paradise.Export.Serialization;

namespace Paradise.Export.Tests;

// GOLDEN TEST. Reconstructs the SampleScene LevelData and asserts our serializer reproduces the
// on-disk JSON byte-for-byte (newline-normalized). This pins the entire serialization stack —
// property order, float formatting, Color32 { r,g,b,a }, enum-by-name, null inclusion,
// scalar-vs-vector float rendering. Fixture: Fixtures/SampleScene.expected.json — the Unity
// baseline (~/proj/ParadiseUnityEditor/data/scenes/SampleScene.json) with its Z-dependent values
// mirrored into the contract's right-handed convention (RH = Z-mirror of the Unity left-handed data).
public class SampleSceneGoldenTests
{
    [Test]
    public async Task serialized_sample_scene_matches_unity_baseline()
    {
        LevelData document = BuildSampleScene();
        string actual = Normalize(ExportJsonWriter.SerializeToString(document));
        string expected = Normalize(ReadFixture("SampleScene.expected.json"));

        await Assert.That(actual).IsEqualTo(expected);
    }

    private static LevelData BuildSampleScene() => new()
    {
        Camera = new CameraData
        {
            Position = new Vector3(0f, 1f, 10f),
            Rotation = new Vector3(0f, 0f, 0f),
            OrthographicSize = 5f,
            BackgroundColor = Color32.FromRgba(0.03137255f, 0.07450981f, 0.192156866f, 0f),
        },
        Lighting = new LightingData
        {
            ActiveState = "Default",
            States = new List<LightingStateData>
            {
                new()
                {
                    Name = "Default",
                    Environment = new EnvironmentData
                    {
                        AmbientMode = "Skybox",
                        AmbientColor = Color32.FromRgba(0.03529412f, 0.0431372561f, 0.05490196f, 1f),
                        AmbientEquatorColor = Color32.FromRgba(0.0117647061f, 0.0156862754f, 0.0156862754f, 1f),
                        AmbientGroundColor = Color32.FromRgba(0.003921569f, 0.003921569f, 0.003921569f, 1f),
                        Exposure = 1f,
                        FogEnabled = false,
                        FogColor = Color32.FromRgba(0.215686277f, 0.215686277f, 0.215686277f, 1f),
                        FogDensity = 0.01f,
                    },
                    Lights = new List<SceneLightData>
                    {
                        new()
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
                        },
                    },
                },
            },
        },
    };

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    // Normalize line endings and trailing whitespace so the comparison is about data, not the
    // platform's newline style (the on-disk fixture carries a trailing newline from the atomic
    // writer; SerializeToString does not).
    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');
}
