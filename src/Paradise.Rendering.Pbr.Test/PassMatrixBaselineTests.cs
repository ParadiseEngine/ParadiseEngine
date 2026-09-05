using System.Globalization;
using System.Numerics;
using System.Text;
using Paradise.Assets.Gltf;
using Paradise.Rendering.Pbr.Test.Baseline;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

/// <summary>The frame-graph migration baseline: for every feature combination that
/// <c>PbrRenderer.RenderFrame</c>'s pass-index arithmetic distinguishes, freeze the pass table, the
/// command stream, and the rendered pixels.
///
/// <para>The arithmetic under test is a chain in which every index is expressed relative to the
/// count of the passes before it:</para>
/// <code>
/// prepassIndex     = hasPrepass ? _shadowViews.Count : -1
/// mainPassIndex    = _shadowViews.Count + (hasPrepass ? 1 : 0)
/// captureBlitIndex = mainPassIndex + 1
/// bloomStart       = mainPassIndex + 1 + capturePasses
/// compositeIndex   = bloomStart + (bloomEnabled ? 2 * _bloomLevels - 1 : 0)
/// </code>
/// <para>so the axes worth crossing are exactly the four terms: shadow view count, the SSAO
/// pre-pass, the scene-color capture split, and the bloom chain. Shadows take three values rather
/// than two because a point light contributes six views where a directional contributes one, and an
/// off-by-one in the layer loop only shows up above one.</para>
///
/// <para>Two assertions per case, doing different jobs. The <b>signature</b> is the contract: it is
/// computed from the submitted <c>RenderCommandStream</c>, so it is identical on every adapter and
/// it names the pass and attachment that drifted. The <b>pixels</b> are the backstop for what
/// structure cannot see — a pass wired to the right target with the wrong bind group — and they are
/// keyed by runtime identifier, because rasterization is not bit-identical across adapters and a
/// tolerance wide enough to span Metal and lavapipe would not catch anything worth catching.</para>
///
/// <para>Refresh with <c>PARADISE_UPDATE_GOLDEN=1 dotnet test …</c>. Through the migration the
/// expected diff is empty; a non-empty one is the finding.</para></summary>
public class PassMatrixBaselineTests
{
    private const uint Size = 128; // → bloom chain of 4 levels (64,32,16,8), i.e. 7 bloom passes

    private static WebGpuRenderer? TryCreateHeadlessOrSkip()
    {
        try
        {
            return WebGpuRenderer.CreateHeadless(Size, Size);
        }
        catch (AdapterUnavailableException ex)
        {
            Skip.Test($"No WebGPU adapter available on this host: {ex.Message}");
            return null;
        }
        catch (DllNotFoundException ex)
        {
            Skip.Test($"WebGPU native library not loadable on this host: {ex.Message}");
            return null;
        }
    }

    private enum Shadows { None, Directional, Point }

    private readonly record struct Case(Shadows Shadows, bool Ssao, bool Capture, bool Bloom)
    {
        internal string Name =>
            $"shadows-{Shadows.ToString().ToLowerInvariant()}" +
            $"_ssao-{On(Ssao)}_capture-{On(Capture)}_bloom-{On(Bloom)}";

        private static string On(bool b) => b ? "on" : "off";
    }

    private static IEnumerable<Case> Matrix()
    {
        foreach (var shadows in new[] { Shadows.None, Shadows.Directional, Shadows.Point })
            foreach (var ssao in new[] { false, true })
                foreach (var capture in new[] { false, true })
                    foreach (var bloom in new[] { false, true })
                        yield return new Case(shadows, ssao, capture, bloom);
    }

    [Test]
    public async Task pass_matrix_matches_the_committed_baseline()
    {
        var backend = TryCreateHeadlessOrSkip();
        if (backend is null) return;

        var structureDrift = new StringBuilder();
        var pixelDrift = new StringBuilder();
        var cullingDrift = new StringBuilder();
        var cases = 0;

        try
        {
            foreach (var testCase in Matrix())
            {
                cases++;
                var recorder = new RecordingRenderer(backend);

                // A renderer per case, not one reused across the matrix. Several of its resources
                // are grow-only (the shadow-map array, the draw ring), so a shared instance would
                // make each case's structure depend on which cases ran before it — and a golden
                // that only holds in matrix order is worse than none.
                using var pbr = new PbrRenderer(recorder, Size, Size)
                {
                    SceneColorCapture = testCase.Capture,
                };

                var scene = BuildScene(pbr, testCase);

                // Warm up: the first frames build pipeline variants lazily and grow the shadow
                // array, so the steady state is what the baseline should hold.
                for (var i = 0; i < 3; i++) pbr.RenderFrame(scene);
                recorder.Clear();
                pbr.RenderFrame(scene);

                CheckSignature(testCase, recorder, structureDrift);
                CheckPixels(testCase, backend, pixelDrift);
                CheckCulling(testCase, pbr, cullingDrift);
            }
        }
        finally
        {
            backend.Dispose();
        }

        await Assert.That(cases).IsEqualTo(24);

        var report = new StringBuilder();
        if (structureDrift.Length > 0)
            report.Append("Pass structure drifted:\n").Append(structureDrift);
        if (pixelDrift.Length > 0)
            report.Append("Pixels drifted:\n").Append(pixelDrift);
        if (cullingDrift.Length > 0)
            report.Append("Culling did not remove what it should:\n").Append(cullingDrift);
        if (report.Length > 0)
            report.Append("\nInspect the actual output under ").Append(GoldenStore.FailureDirectory)
                  .Append(", or re-baseline with PARADISE_UPDATE_GOLDEN=1.\n");

        await Assert.That(report.ToString()).IsEmpty();
    }

    /// <summary>The passes an "off" feature contributes are still DECLARED — they are removed by
    /// reachability, not by an <c>if</c>. Nothing in the submitted stream can tell that apart from
    /// never declaring them, so assert it directly: at 128x128 the bloom chain is 2x4-1 passes and
    /// the SSAO pre-pass is one.</summary>
    private static void CheckCulling(Case testCase, PbrRenderer pbr, StringBuilder drift)
    {
        var expected = (testCase.Bloom ? 0 : 7) + (testCase.Ssao ? 0 : 1);
        var actual = pbr.CulledPassCountForTest;
        if (actual != expected)
        {
            drift.Append("  ").Append(testCase.Name)
                 .Append(CultureInfo.InvariantCulture, $": culled {actual} passes, expected {expected}.\n");
        }
    }

    private static void CheckSignature(Case testCase, RecordingRenderer recorder, StringBuilder drift)
    {
        var actual = FrameSignature.Format(testCase.Name, recorder.LastPresentedFrame).ReplaceLineEndings("\n");

        if (GoldenStore.UpdateMode)
        {
            GoldenStore.WriteSignature(testCase.Name, actual);
            return;
        }

        var expected = GoldenStore.ReadSignature(testCase.Name);
        if (expected is null)
        {
            drift.Append("  ").Append(testCase.Name).Append(": no committed signature.\n");
            GoldenStore.WriteFailureArtifact(testCase.Name, "txt", Encoding.UTF8.GetBytes(actual));
            return;
        }
        if (expected == actual) return;

        GoldenStore.WriteFailureArtifact(testCase.Name, "txt", Encoding.UTF8.GetBytes(actual));
        drift.Append("  ").Append(testCase.Name).Append(":\n")
             .Append(FirstDifference(expected, actual));
    }

    private static void CheckPixels(Case testCase, WebGpuRenderer backend, StringBuilder drift)
    {
        var pixels = backend.ReadbackColor(out var width, out var height);
        var capture = new ColorReadback(pixels, width, height);

        using var encoded = new MemoryStream();
        PngWriter.Write(encoded, in capture, backend.ColorFormat);
        var png = encoded.ToArray();

        if (GoldenStore.UpdateMode)
        {
            GoldenStore.WritePixels(testCase.Name, png);
            return;
        }

        if (!GoldenStore.HasPixelBaseline) return; // no baseline for this adapter; signature still guards

        var goldenPng = GoldenStore.ReadPixels(testCase.Name);
        if (goldenPng is null)
        {
            drift.Append("  ").Append(testCase.Name).Append(": no committed image for this adapter.\n");
            GoldenStore.WriteFailureArtifact(testCase.Name, "png", png);
            return;
        }

        var (expected, ew, eh) = PngReader.ReadRgba(goldenPng);
        var (actual, aw, ah) = PngReader.ReadRgba(png);

        if (ew != aw || eh != ah)
        {
            drift.Append("  ").Append(testCase.Name)
                 .Append(CultureInfo.InvariantCulture, $": size {aw}x{ah}, expected {ew}x{eh}.\n");
            GoldenStore.WriteFailureArtifact(testCase.Name, "png", png);
            return;
        }

        var differing = 0;
        var worst = 0;
        for (var i = 0; i < expected.Length; i++)
        {
            var delta = Math.Abs(expected[i] - actual[i]);
            if (delta == 0) continue;
            differing++;
            if (delta > worst) worst = delta;
        }
        if (differing == 0) return;

        GoldenStore.WriteFailureArtifact(testCase.Name, "png", png);
        drift.Append("  ").Append(testCase.Name)
             .Append(CultureInfo.InvariantCulture,
                 $": {differing} of {expected.Length} channel samples differ (max delta {worst}).\n");
    }

    /// <summary>The first differing line, with a little context. A whole-file diff in an assertion
    /// message is unreadable; the pass name and the line are what identify the regression.</summary>
    private static string FirstDifference(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        for (var i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            var el = i < e.Length ? e[i] : "<end of file>";
            var al = i < a.Length ? a[i] : "<end of file>";
            if (el == al) continue;
            return $"    line {i + 1}\n      expected: {el}\n      actual:   {al}\n";
        }
        return "    (files differ only in trailing whitespace)\n";
    }

    /// <summary>One scene, exercising every bucket the pass table branches on: a sky background, an
    /// opaque bucket (so the SSAO pre-pass and the shadow casters have work), and a blend bucket (so
    /// the capture split has a second half to render). Bright emissive-ish albedo under a strong
    /// light keeps some pixels above the bloom threshold, so the bloom chain has something to
    /// gather — a black bloom mip would make the case pass for the wrong reason.</summary>
    private static PbrScene BuildScene(PbrRenderer pbr, Case testCase)
    {
        var (vertices, indices) = Procedural.UnitCube();

        var groundId = pbr.Materials.AddDefaultMaterial(new Vector4(0.45f, 0.46f, 0.5f, 1f));
        var cubeId = pbr.Materials.AddDefaultMaterial(new Vector4(0.85f, 0.62f, 0.18f, 1f), metallic: 0.1f, roughness: 0.35f);
        var glassId = pbr.Materials.AddMaterial(BlendMaterial(), []);

        var ground = new PbrMesh([pbr.UploadPrimitive(vertices, indices, groundId)]);
        var cube = new PbrMesh([pbr.UploadPrimitive(vertices, indices, cubeId)]);
        var glass = new PbrMesh([pbr.UploadPrimitive(vertices, indices, glassId)]);

        var eye = new Vector3(2.2f, 1.8f, 3.2f);
        var scene = new PbrScene
        {
            Camera = new PbrCamera
            {
                View = PbrMath.LookAt(eye, Vector3.Zero, Vector3.UnitY),
                Projection = PbrMath.Perspective(MathF.PI / 3f, 1f, 0.1f, 100f),
                Position = eye,
            },
            HasSkyBackground = true,
            SkyTopColor = new Vector3(0.10f, 0.22f, 0.52f),
            SkyHorizonColor = new Vector3(0.52f, 0.60f, 0.72f),
            SkyGroundBottom = new Vector3(0.04f, 0.04f, 0.05f),
            SkyGroundHorizon = new Vector3(0.18f, 0.17f, 0.16f),
            Tonemap = new PbrTonemap { Mode = PbrTonemapMode.Filmic, Exposure = 1.1f, White = 4f },
            Bloom = new PbrBloom { Enabled = testCase.Bloom, Threshold = 0.9f, Knee = 0.4f, Intensity = 0.7f },
            Ssao = new PbrSsao { Enabled = testCase.Ssao, Radius = 0.6f, Intensity = 2f },
        };

        scene.Lights.Add(testCase.Shadows switch
        {
            Shadows.Point => new PbrLight
            {
                Type = PbrLightType.Point,
                Position = new Vector3(1.6f, 2.4f, 1.6f),
                Color = new Vector3(1f, 0.92f, 0.78f),
                Intensity = 14f,
                Range = 12f,
                CastsShadows = true,
            },
            Shadows.Directional => new PbrLight
            {
                Type = PbrLightType.Directional,
                Direction = Vector3.Normalize(new Vector3(0.45f, 1f, 0.55f)),
                Intensity = 3.2f,
                CastsShadows = true,
            },
            _ => new PbrLight
            {
                Type = PbrLightType.Directional,
                Direction = Vector3.Normalize(new Vector3(0.45f, 1f, 0.55f)),
                Intensity = 3.2f,
            },
        });

        scene.Instances.Add(new PbrInstance
        {
            Mesh = ground,
            Model = Matrix4x4.CreateScale(new Vector3(6f, 0.12f, 6f)) * Matrix4x4.CreateTranslation(0f, -0.7f, 0f),
        });
        scene.Instances.Add(new PbrInstance { Mesh = cube });
        scene.Instances.Add(new PbrInstance
        {
            Mesh = glass,
            Model = Matrix4x4.CreateScale(new Vector3(1.4f, 1.4f, 0.06f)) * Matrix4x4.CreateTranslation(-0.15f, 0.35f, 1.15f),
        });
        return scene;
    }

    private static GltfMaterialData BlendMaterial() => new(
        Name: "baseline-blend",
        BaseColorFactor: new Vector4(0.55f, 0.78f, 0.95f, 0.45f),
        MetallicFactor: 0f,
        RoughnessFactor: 0.2f,
        EmissiveFactor: Vector3.Zero,
        NormalScale: 1f,
        OcclusionStrength: 1f,
        TransmissionFactor: 0f,
        AlphaMode: GltfAlphaMode.Blend,
        AlphaCutoff: 0.5f,
        DoubleSided: true,
        BaseColorImage: -1,
        MetallicRoughnessImage: -1,
        NormalImage: -1,
        OcclusionImage: -1,
        EmissiveImage: -1,
        BaseColorUvTransform: GltfUvTransform.Identity);
}
