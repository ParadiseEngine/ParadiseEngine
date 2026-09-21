using System.Numerics;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

/// <summary>Shadow views are budgeted and addressed by their own ARRAY LAYER, not by the index of
/// the light that cast them.
///
/// <para>Every other shadow test in this suite lights its scene with a single caster at light
/// index 0, where the old <c>lightIndex * 6</c> addressing and the layer are both zero and the two
/// schemes cannot be told apart. These put a caster at a NON-zero light index, which is the only
/// arrangement where they differ.</para></summary>
public class ShadowLayerTests
{
    private const uint Size = 96;

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

    /// <summary>A floor with a block standing on it, lit by one shadow-casting point light above.
    /// <paramref name="quietLightsFirst"/> puts that many non-casting, zero-intensity lights ahead
    /// of it, which moves its light index without moving its shadow layer or changing the
    /// picture.</summary>
    private static PbrScene BuildScene(PbrRenderer pbr, int quietLightsFirst, bool casts = true)
    {
        var (vertices, indices) = Procedural.UnitCube();
        var materialId = pbr.Materials.AddDefaultMaterial(new Vector4(0.85f, 0.85f, 0.85f, 1f));
        var mesh = new PbrMesh([pbr.UploadPrimitive(vertices, indices, materialId)]);

        var eye = new Vector3(0f, 3.5f, 5f);
        var scene = new PbrScene
        {
            Camera = new PbrCamera
            {
                View = PbrMath.LookAt(eye, Vector3.Zero, Vector3.UnitY),
                Projection = PbrMath.Perspective(MathF.PI / 3f, 1f, 0.1f, 100f),
                Position = eye,
            },
            Ambient = new PbrAmbient { Sky = Vector3.Zero, Equator = Vector3.Zero, Ground = Vector3.Zero, Flat = true },
            Tonemap = new PbrTonemap { Mode = PbrTonemapMode.Linear },
        };
        scene.Instances.Add(new PbrInstance
        {
            Mesh = mesh,
            Model = Matrix4x4.CreateScale(new Vector3(8f, 0.1f, 8f)) * Matrix4x4.CreateTranslation(0f, -0.05f, 0f),
        });
        scene.Instances.Add(new PbrInstance
        {
            Mesh = mesh,
            Model = Matrix4x4.CreateScale(new Vector3(1f, 1.5f, 1f)) * Matrix4x4.CreateTranslation(0f, 0.75f, 0f),
        });

        // Zero intensity and no shadow: these occupy a light slot and contribute nothing, so the
        // only thing they change is the index the caster lands on.
        for (var i = 0; i < quietLightsFirst; i++)
        {
            scene.Lights.Add(new PbrLight
            {
                Type = PbrLightType.Point,
                Position = new Vector3(0f, 20f, 0f),
                Intensity = 0f,
                Range = 0.1f,
                CastsShadows = false,
            });
        }
        scene.Lights.Add(new PbrLight
        {
            Type = PbrLightType.Point,
            Position = new Vector3(2.2f, 3.2f, 2.2f),
            Color = Vector3.One,
            Intensity = 12f,
            Range = 20f,
            CastsShadows = casts,
        });
        return scene;
    }

    [Test]
    public async Task a_caster_at_a_non_zero_light_index_shadows_exactly_as_it_does_at_zero()
    {
        var backend = TryCreateHeadlessOrSkip();
        if (backend is null) return;
        using var _ = backend;

        using var first = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        first.RenderFrame(BuildScene(first, quietLightsFirst: 0));
        var atZero = (byte[])backend.ReadbackColor(out var width, out var height).Clone();

        // Light index 3, shadow layer still 0: the three ahead of it cast nothing, so they take no
        // layer. Addressed by lightIndex * 6 this would read matrix 18, which nothing wrote.
        using var moved = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        moved.RenderFrame(BuildScene(moved, quietLightsFirst: 3));
        var atThree = (byte[])backend.ReadbackColor(out var movedWidth, out var movedHeight).Clone();

        await Assert.That((width, height)).IsEqualTo((movedWidth, movedHeight));
        for (var i = 0; i < atZero.Length; i++)
        {
            if (atZero[i] == atThree[i]) continue;
            throw new InvalidOperationException(
                $"Byte {i} (pixel {i / 4}, channel {i % 4}) differs: caster at index 0 {atZero[i]}, at index 3 {atThree[i]}.");
        }

        // And not vacuously: the scene is lit, and the block actually casts something.
        using var unshadowed = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        unshadowed.RenderFrame(BuildScene(unshadowed, quietLightsFirst: 3, casts: false));
        var withoutShadow = Brightness(backend);
        var withShadow = Brightness(atZero);
        await Assert.That(withShadow).IsGreaterThan(1.0);
        await Assert.That(withoutShadow).IsGreaterThan(withShadow + 1.0);
    }

    private static double Brightness(WebGpuRenderer backend) => Brightness(backend.ReadbackColor(out _, out _));

    private static double Brightness(byte[] pixels)
    {
        long total = 0;
        foreach (var value in pixels) total += value;
        return (double)total / pixels.Length;
    }
}
