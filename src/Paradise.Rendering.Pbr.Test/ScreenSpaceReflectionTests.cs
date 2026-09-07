using System.Numerics;
using Paradise.Assets.Gltf;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

/// <summary>Screen-space reflection, proven by a picture: a red emissive block standing on a
/// mirror floor is red in the floor below it only when the reflection pass runs. The floor is a
/// dark metal so nothing but a reflection can make it red, and the block is emissive so no light
/// is needed for it to be red.</summary>
public class ScreenSpaceReflectionTests
{
    private const uint Size = 128;

    private static WebGpuRenderer? TryCreateHeadlessOrSkip()
    {
        try
        {
            return WebGpuRenderer.CreateHeadless(Size, Size);
        }
        catch (Exception error) when (error is AdapterUnavailableException or DllNotFoundException)
        {
            Skip.Test($"No WebGPU adapter available on this host: {error.Message}");
            return null;
        }
    }

    private static PbrScene BuildScene(PbrRenderer pbr, bool ssr)
    {
        var (vertices, indices) = Procedural.UnitCube();
        var mirrorId = pbr.Materials.AddDefaultMaterial(new Vector4(0.9f, 0.9f, 0.9f, 1f), metallic: 1f, roughness: 0.05f);
        var redId = pbr.Materials.AddMaterial(new GltfMaterialData(
            Name: "red", BaseColorFactor: new Vector4(0f, 0f, 0f, 1f), MetallicFactor: 0f, RoughnessFactor: 1f,
            EmissiveFactor: new Vector3(4f, 0f, 0f), NormalScale: 1f, OcclusionStrength: 1f, TransmissionFactor: 0f,
            AlphaMode: GltfAlphaMode.Opaque, AlphaCutoff: 0.5f, DoubleSided: false,
            BaseColorImage: -1, MetallicRoughnessImage: -1, NormalImage: -1, OcclusionImage: -1, EmissiveImage: -1,
            BaseColorUvTransform: GltfUvTransform.Identity), []);
        var mirror = new PbrMesh([pbr.UploadPrimitive(vertices, indices, mirrorId)]);
        var red = new PbrMesh([pbr.UploadPrimitive(vertices, indices, redId)]);

        // Low and close: the block fills the upper half of the frame and its reflection the lower.
        var eye = new Vector3(0f, 0.8f, 4f);
        var scene = new PbrScene
        {
            Camera = new PbrCamera
            {
                View = PbrMath.LookAt(eye, new Vector3(0f, 0.6f, 0f), Vector3.UnitY),
                Projection = PbrMath.Perspective(MathF.PI / 3f, 1f, 0.1f, 100f),
                Position = eye,
            },
            Ambient = new PbrAmbient { Sky = new Vector3(0.02f), Equator = new Vector3(0.02f), Ground = new Vector3(0.02f), Flat = true },
            Tonemap = new PbrTonemap { Mode = PbrTonemapMode.Linear },
            ClearColor = new ColorRgba(0f, 0f, 0f, 1f),
            Ssr = new PbrScreenSpaceReflection { Enabled = ssr, MaxSteps = 64, MaxDistance = 10f, Thickness = 0.3f },
        };
        scene.Instances.Add(new PbrInstance
        {
            Mesh = mirror,
            Model = Matrix4x4.CreateScale(new Vector3(20f, 0.1f, 20f)) * Matrix4x4.CreateTranslation(0f, -0.05f, 0f),
        });
        scene.Instances.Add(new PbrInstance
        {
            Mesh = red,
            Model = Matrix4x4.CreateScale(new Vector3(2f, 1.6f, 0.5f)) * Matrix4x4.CreateTranslation(0f, 0.8f, -1f),
        });
        return scene;
    }

    /// <summary>Mean red minus blue over the floor's part of the frame: a reflection is red, a
    /// mirror reflecting the dark sky is grey, and the difference cancels grey out.</summary>
    private static double FloorRedness(byte[] pixels, TextureFormat format)
    {
        // The headless target is Bgra8Unorm; read the channels where the format puts them.
        var red = format is TextureFormat.Bgra8Unorm or TextureFormat.Bgra8UnormSrgb ? 2 : 0;
        var blue = 2 - red;
        long sum = 0;
        var count = 0;
        for (var y = (int)(Size * 0.7); y < Size; y++)
        {
            for (var x = (int)(Size * 0.3); x < (int)(Size * 0.7); x++)
            {
                var i = (y * (int)Size + x) * 4;
                sum += pixels[i + red] - pixels[i + blue];
                count++;
            }
        }
        return sum / (double)count;
    }

    private static byte[] Render(WebGpuRenderer backend, PbrRenderer pbr, PbrScene scene)
    {
        // The reflection reads the previous frame, so the third frame is the first steady one.
        for (var i = 0; i < 4; i++) pbr.RenderFrame(scene);
        var pixels = (byte[])backend.ReadbackColor(out var width, out var height).Clone();
        if (Environment.GetEnvironmentVariable("PARADISE_SSR_DUMP") is { } dump)
        {
            using var file = File.Create(Path.Combine(dump, $"ssr-{(scene.Ssr.Enabled ? "on" : "off")}.png"));
            PngWriter.Write(file, new ColorReadback(pixels, width, height), backend.ColorFormat);
        }
        return pixels;
    }

    [Test]
    public async Task Reflection_makes_the_mirror_floor_red_below_the_red_block()
    {
        using var backend = TryCreateHeadlessOrSkip();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, Size, Size);

        var without = FloorRedness(Render(backend, pbr, BuildScene(pbr, ssr: false)), backend.ColorFormat);
        await Assert.That(pbr.LastPassNames).DoesNotContain("Ssr.Trace");
        var with = FloorRedness(Render(backend, pbr, BuildScene(pbr, ssr: true)), backend.ColorFormat);
        await Assert.That(pbr.LastPassNames).Contains("Ssr.Trace");
        await Assert.That(pbr.LastPassNames).Contains("Ssr.History");

        await Assert.That(without).IsLessThan(8);
        await Assert.That(with).IsGreaterThan(without + 30);
    }

    [Test]
    public async Task First_frame_after_enabling_traces_nothing_and_reads_no_history()
    {
        using var backend = TryCreateHeadlessOrSkip();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, Size, Size);
        var scene = BuildScene(pbr, ssr: true);

        pbr.RenderFrame(scene);
        await Assert.That(pbr.LastPassNames).DoesNotContain("Ssr.Trace");
        await Assert.That(pbr.LastPassNames).Contains("Ssr.History");
        pbr.RenderFrame(scene);
        await Assert.That(pbr.LastPassNames).Contains("Ssr.Trace");

        // A resize drops the history; the trace waits one frame for a new one.
        pbr.Resize(Size + 8, Size + 8);
        pbr.RenderFrame(scene);
        await Assert.That(pbr.LastPassNames).DoesNotContain("Ssr.Trace");
        pbr.RenderFrame(scene);
        await Assert.That(pbr.LastPassNames).Contains("Ssr.Trace");
    }
}
