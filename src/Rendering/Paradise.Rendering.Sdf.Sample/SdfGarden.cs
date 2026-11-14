using System.Numerics;
using System.Runtime.InteropServices;

namespace Paradise.Rendering.Sdf.Sample;

/// <summary>An animated scene touching every primitive and operation, with an orbit camera and
/// CPU picking against the same field the renderer traces.</summary>
internal sealed class SdfGarden
{
    private const int BlobCount = 5;
    private const float MarkerRadius = 0.12f;

    private static readonly Vector3 s_target = new(0f, 1.1f, 0f);
    private static readonly Vector3 s_die = new(-3.2f, 0.9f, 0.4f);

    private static readonly SdfMaterial s_ground = new(new Vector3(0.42f, 0.4f, 0.37f), 0.9f);
    private static readonly SdfMaterial s_ivory = new(new Vector3(0.88f, 0.84f, 0.74f), 0.35f);
    private static readonly SdfMaterial s_gold = new(new Vector3(1f, 0.77f, 0.34f), 0.28f, 1f);
    private static readonly SdfMaterial s_moss = new(new Vector3(0.25f, 0.62f, 0.3f), 0.6f);
    private static readonly SdfMaterial s_slate = new(new Vector3(0.36f, 0.4f, 0.48f), 0.45f);
    private static readonly SdfMaterial s_marker = new(Vector3.Zero, 1f, 0f, new Vector3(6f, 4.5f, 0.4f));

    private static readonly Vector3[] s_blobColors =
    [
        new(0.92f, 0.22f, 0.2f), new(0.95f, 0.55f, 0.12f), new(0.2f, 0.5f, 0.95f),
        new(0.6f, 0.25f, 0.85f), new(0.15f, 0.75f, 0.7f),
    ];

    private readonly SdfField _pickField = new();
    private readonly SdfLight _lamp = new()
    {
        Type = SdfLightType.Point,
        Color = new Vector3(0.4f, 0.6f, 1f),
        Intensity = 6f,
        Range = 9f,
    };

    private float _yaw;
    private float _pitch;
    private float _distance;
    private Vector3? _marker;

    public SdfGarden()
    {
        ResetCamera();
        Scene.Sky = Scene.Sky with { AmbientIntensity = 0.35f };
        Scene.Lights.Add(new SdfLight
        {
            Direction = new Vector3(0.45f, 0.85f, 0.3f),
            Color = new Vector3(1f, 0.95f, 0.88f),
            Intensity = 0.9f,
        });
        Scene.Lights.Add(_lamp);
    }

    public SdfScene Scene { get; } = new();

    /// <summary>Animation seconds; advances only while not <see cref="Paused"/>.</summary>
    public float Time { get; private set; }

    public bool Paused { get; set; }

    public void Drag(float deltaX, float deltaY)
    {
        _yaw -= deltaX * 0.008f;
        _pitch = Math.Clamp(_pitch + deltaY * 0.008f, 0.05f, 1.45f);
    }

    public void Zoom(float notches) => _distance = Math.Clamp(_distance * MathF.Pow(0.9f, notches), 3f, 30f);

    public void ResetCamera()
    {
        _yaw = 0.55f;
        _pitch = 0.4f;
        _distance = 8.5f;
    }

    public void ToggleShadows() => Scene.Tracing = Scene.Tracing with { Shadows = !Scene.Tracing.Shadows };

    public void ToggleAmbientOcclusion() =>
        Scene.Tracing = Scene.Tracing with { AmbientOcclusion = Scene.Tracing.AmbientOcclusion > 0f ? 0f : 1f };

    public void ToggleTonemap() => Scene.Tonemap = Scene.Tonemap with
    {
        Mode = Scene.Tonemap.Mode == SdfTonemapMode.Aces ? SdfTonemapMode.Linear : SdfTonemapMode.Aces,
    };

    /// <summary>Advances the animation and rebuilds the frame's shapes, lights and camera.</summary>
    public void Update(float deltaSeconds, float aspect)
    {
        if (!Paused) Time += deltaSeconds;
        var t = Time;

        var eye = s_target + _distance * new Vector3(
            MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch), MathF.Cos(_pitch) * MathF.Cos(_yaw));
        Scene.Camera = new SdfCamera
        {
            View = Matrix4x4.CreateLookAt(eye, s_target, Vector3.UnitY),
            Projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, aspect, 0.1f, 100f),
        };
        _lamp.Position = new Vector3(3.5f * MathF.Cos(0.5f * t), 2.6f, 3.5f * MathF.Sin(0.5f * t));

        // The list is rebuilt in place every frame: shapes are plain values and nothing is retained.
        var shapes = Scene.Shapes;
        shapes.Clear();

        // A die: a box rounded by intersecting a sphere, then bored by an orbiting cutter. These
        // come first because a shape combines with everything before it; later in the list the
        // intersection would clip the whole scene and the cutter would bore the ground as well.
        shapes.Add(SdfShape.Box(Matrix4x4.CreateRotationY(0.25f * t) * Matrix4x4.CreateTranslation(s_die), new Vector3(0.9f)) with
        {
            Material = s_ivory,
        });
        shapes.Add(SdfShape.Sphere(s_die, 1.2f) with
        {
            Operation = SdfOperation.Intersection,
            Smoothness = 0.05f,
            Material = s_ivory,
        });
        var cutter = s_die + new Vector3(0.95f * MathF.Cos(0.7f * t), 0.35f * MathF.Sin(1.1f * t), 0.95f * MathF.Sin(0.7f * t));
        shapes.Add(SdfShape.Sphere(cutter, 0.5f) with { Operation = SdfOperation.Subtraction, Smoothness = 0.08f });

        shapes.Add(SdfShape.Plane() with { Material = s_ground });

        // Blobs melt into each other and draw the ground up as they dip, blending their colors.
        for (var i = 0; i < BlobCount; i++)
        {
            var phase = i * (MathF.Tau / BlobCount);
            var center = new Vector3(
                1.15f * MathF.Cos(0.6f * t + phase),
                1.55f + 0.6f * MathF.Sin(0.9f * t + 1.7f * phase),
                1.15f * MathF.Sin(0.78f * t + phase));
            shapes.Add(SdfShape.Sphere(center, 0.48f + 0.12f * MathF.Sin(1.3f * t + phase)) with
            {
                Smoothness = 0.6f,
                Material = new SdfMaterial(s_blobColors[i], 0.3f),
            });
        }

        shapes.Add(SdfShape.Torus(
            Matrix4x4.CreateRotationX(0.8f * t) * Matrix4x4.CreateRotationY(0.3f * t) * Matrix4x4.CreateTranslation(3.1f, 1.4f, 0.2f),
            0.85f, 0.22f) with
        {
            Material = s_gold,
        });
        shapes.Add(SdfShape.Capsule(Matrix4x4.CreateRotationZ(0.5f) * Matrix4x4.CreateTranslation(1.6f, 0.7f, 2.6f), 0.3f, 0.55f) with
        {
            Smoothness = 0.25f,
            Material = s_moss,
        });
        shapes.Add(SdfShape.Cylinder(Matrix4x4.CreateTranslation(-1.6f, 0.6f, 3f), 0.45f, 0.6f, 0.08f) with
        {
            Material = s_slate,
        });

        if (_marker is { } marker) shapes.Add(SdfShape.Sphere(marker, MarkerRadius) with { Material = s_marker });
    }

    /// <summary>Traces the ray under a screen point on the CPU and marks where it lands.</summary>
    /// <remarks>Reads the shapes and camera of the last <see cref="Update"/>, which is the frame
    /// on screen when the user clicked.</remarks>
    /// <param name="pixel">Pixels from the top-left corner of the viewport.</param>
    public bool TryPick(Vector2 pixel, Vector2 viewport, out Vector3 point, out SdfMaterial material)
    {
        var shapes = CollectionsMarshal.AsSpan(Scene.Shapes);
        // The previous marker is drawn but must not be what the next click lands on.
        if (_marker is not null) shapes = shapes[..^1];
        _pickField.Set(shapes);

        var camera = Scene.Camera;
        var tracing = Scene.Tracing;
        if (SdfMath.TryScreenPointToRay(pixel, viewport, camera.View * camera.Projection, out var origin, out var direction, out var length)
            && _pickField.TryRaycast(origin, direction, MathF.Min(length, tracing.MaxDistance), out var distance,
                tracing.MaxSteps, tracing.SurfaceEpsilon))
        {
            point = origin + direction * distance;
            _pickField.Sample(point, out material);
            _marker = point;
            return true;
        }

        point = default;
        material = default;
        _marker = null;
        return false;
    }
}
