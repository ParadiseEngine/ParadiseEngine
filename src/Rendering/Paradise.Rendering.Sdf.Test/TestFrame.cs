using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Sdf.Test;

/// <summary>Shared GPU-test plumbing: a headless backend, linear-output scenes and BGRA pixels.</summary>
internal static class TestFrame
{
    public static readonly Vector3 SkyBlue = new(0f, 0f, 1f);

    public static WebGpuRenderer? Backend(uint size)
    {
        try
        {
            return WebGpuRenderer.CreateHeadless(size, size);
        }
        catch (Exception error) when (error is AdapterUnavailableException or DllNotFoundException)
        {
            Skip.Test($"No WebGPU adapter: {error.Message}");
            return null;
        }
    }

    /// <summary>A scene whose pixels are predictable: flat sky, no ambient, no tone curve.</summary>
    public static SdfScene LinearScene(SdfCamera camera) => new()
    {
        Camera = camera,
        Sky = new SdfSky { Zenith = SkyBlue, Horizon = SkyBlue, Ground = SkyBlue, AmbientIntensity = 0f },
        Tonemap = new SdfTonemap { Mode = SdfTonemapMode.Linear },
    };

    public static SdfCamera Perspective(Vector3 eye, Vector3 target, float fovY = MathF.PI / 3f, float near = 1f, float far = 30f) => new()
    {
        View = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY),
        Projection = Matrix4x4.CreatePerspectiveFieldOfView(fovY, 1f, near, far),
    };

    public static SdfCamera Orthographic(Vector3 eye, Vector3 target, float size) => new()
    {
        View = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY),
        Projection = Matrix4x4.CreateOrthographic(size, size, 0.1f, 30f),
    };

    public static SdfMaterial Emissive(float r, float g, float b) => new(Vector3.Zero, 1f, 0f, new Vector3(r, g, b));

    /// <summary>Every primitive kind and operation, each with its own emission, so a pixel's
    /// color says which shapes the trace blended.</summary>
    public static SdfScene ParityScene()
    {
        var scene = LinearScene(Perspective(new Vector3(2.5f, 2.2f, 4.5f), new Vector3(0f, 0.6f, 0f), MathF.PI / 3.5f, 0.5f));
        scene.Shapes.Add(SdfShape.Plane() with { Material = Emissive(0.2f, 0.2f, 0.2f) });
        scene.Shapes.Add(SdfShape.Sphere(new Vector3(0f, 0.9f, 0f), 0.8f) with
        {
            Smoothness = 0.3f,
            Material = Emissive(0.9f, 0.1f, 0.1f),
        });
        scene.Shapes.Add(SdfShape.Box(
            Matrix4x4.CreateRotationY(0.5f) * Matrix4x4.CreateTranslation(1.1f, 0.5f, 0.3f), new Vector3(0.5f), 0.1f) with
        {
            Smoothness = 0.25f,
            Material = Emissive(0.1f, 0.8f, 0.1f),
        });
        scene.Shapes.Add(SdfShape.Sphere(new Vector3(0.3f, 1.3f, 0.7f), 0.4f) with
        {
            Operation = SdfOperation.Subtraction,
            Smoothness = 0.1f,
        });
        scene.Shapes.Add(SdfShape.Torus(
            Matrix4x4.CreateRotationX(1.2f) * Matrix4x4.CreateTranslation(-1.4f, 0.8f, 0.4f), 0.5f, 0.18f) with
        {
            Material = Emissive(0.1f, 0.3f, 0.9f),
        });
        scene.Shapes.Add(SdfShape.Capsule(
            Matrix4x4.CreateScale(1.5f) * Matrix4x4.CreateRotationZ(0.6f) * Matrix4x4.CreateTranslation(-0.4f, 0.6f, 1.8f), 0.15f, 0.3f) with
        {
            Material = Emissive(0.9f, 0.8f, 0.1f),
        });
        scene.Shapes.Add(SdfShape.Cylinder(Matrix4x4.CreateTranslation(1.9f, 0.4f, 1.9f), 0.3f, 0.4f, 0.05f) with
        {
            Material = Emissive(0.7f, 0.2f, 0.8f),
        });
        // Clips the ground to a disc, so the horizon is sky.
        scene.Shapes.Add(SdfShape.Sphere(Vector3.Zero, 5f) with
        {
            Operation = SdfOperation.Intersection,
            Material = Emissive(0.5f, 0.5f, 0.1f),
        });
        return scene;
    }

    public static float DeviceDepth(SdfScene scene, Vector3 point)
    {
        var clip = Vector4.Transform(new Vector4(point, 1f), scene.Camera.View * scene.Camera.Projection);
        return clip.Z / clip.W;
    }

    public static byte[] Render(WebGpuRenderer backend, SdfRenderer renderer, SdfScene scene)
    {
        renderer.RenderFrame(scene);
        return backend.ReadbackColor(out _, out _);
    }

    /// <summary>Red, green and blue of one pixel of a top-down BGRA readback.</summary>
    public static (int R, int G, int B) Pixel(byte[] pixels, uint size, int x, int y)
    {
        var offset = (y * (int)size + x) * 4;
        return (pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    public static float ToSrgb(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        return value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;
    }

    public static (int R, int G, int B) Encode(Vector3 linear) => (
        (int)MathF.Round(ToSrgb(linear.X) * 255f), (int)MathF.Round(ToSrgb(linear.Y) * 255f), (int)MathF.Round(ToSrgb(linear.Z) * 255f));

    public static bool Near((int R, int G, int B) a, (int R, int G, int B) b, int tolerance) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    /// <summary>Offsets from a pixel's center, in pixels, that still lie well inside it.</summary>
    /// <remarks>A pixel whose CPU answer is the same at all of them does not sit on a silhouette
    /// or seam, so no rounding difference between float pipelines excuses a GPU disagreement.</remarks>
    public static readonly Vector2[] Interior = [new(-0.25f, -0.25f), new(0.25f, -0.25f), new(-0.25f, 0.25f), new(0.25f, 0.25f)];

    public static Vector2 PixelCenter(int x, int y) => new(x + 0.5f, y + 0.5f);

    /// <summary>What the renderer traces through a screen point, evaluated on the CPU.</summary>
    public static bool TryCpuHit(SdfField field, SdfScene scene, uint size, Vector2 screen, out Vector3 point)
    {
        point = default;
        var viewProjection = scene.Camera.View * scene.Camera.Projection;
        if (!SdfMath.TryScreenPointToRay(screen, new Vector2(size), viewProjection,
                out var origin, out var direction, out var length))
            return false;
        var tracing = scene.Tracing;
        if (!field.TryRaycast(origin, direction, MathF.Min(length, tracing.MaxDistance), out var distance,
                tracing.MaxSteps, tracing.SurfaceEpsilon))
            return false;
        point = origin + direction * distance;
        return true;
    }
}
