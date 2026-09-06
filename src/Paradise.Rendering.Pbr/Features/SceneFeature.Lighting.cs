using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Paradise.Rendering.Pbr;

/// <summary>The frame uniforms and the Forward+ froxel clusters: what every lit draw reads
/// through group 1.</summary>
public sealed partial class SceneFeature
{
    // Forward+ froxel clustering: one uint bitmask per froxel (bit i = sceneLights[i] overlaps),
    // CPU-binned each frame from conservative view-space sphere bounds. 32x32 px tiles x 32
    // logarithmic Z slices, matching Godot's cluster shape (Godot bins on the GPU with a compute
    // rasterizer; the CPU route produces the same conservative result at our light counts).
    private const int ClusterTileSize = 32;
    private const int ClusterZSlices = 32;
    private BufferHandle _clusterBuffer;
    private uint[] _clusterMasks = [];
    private int _clusterTilesX;
    private int _clusterTilesY;
    // Cluster depth range for the CURRENT frame (extracted from the projection; the shader must
    // slice with the same values, so they ride the frame UBO).
    private float _clusterNear = 0.05f;
    private float _clusterFar = 100f;

    // The frame UBO's CPU mirror lives in a FIELD, never in a local. FrameUniformsGpu is 31 KB (64
    // lights + 384 shadow matrices), and Mono's wasm interpreter aborts the ENTIRE runtime when it
    // tiers up a method whose locals exceed its frame budget: "Unable to run method
    // UploadFrameUniforms: locals size too big". The abort lands a couple of seconds into steady
    // rendering — long after the method has been interpreting happily, and long after any short
    // smoke test has reported success — so it reads as a random browser crash rather than a struct
    // size problem. Filling this in place also saves a 31 KB stack copy per frame on every backend.
    private FrameUniformsGpu _frameUniforms;

    // Test-only readback of the per-frame packed light array (e.g. to assert ShadowAtlas.X survives
    // the shadow-caster rebuild). Off by default so production frames never pay the array copy.
    internal bool CaptureFrameLightsForTest;
    private SceneLightArray _lastFrameLightsForTest;
    internal Vector4 GetLightShadowAtlasForTest(int lightIndex) => _lastFrameLightsForTest[lightIndex].ShadowAtlas;
    internal Vector4 GetLightSizeParamsForTest(int lightIndex) => _lastFrameLightsForTest[lightIndex].SizeParams;

    // (Re)allocate the froxel mask buffer for the current resolution.
    private void EnsureClusterBuffer()
    {
        var tilesX = (int)((_ctx.Width + ClusterTileSize - 1) / ClusterTileSize);
        var tilesY = (int)((_ctx.Height + ClusterTileSize - 1) / ClusterTileSize);
        if (tilesX == _clusterTilesX && tilesY == _clusterTilesY && _clusterBuffer.IsValid) return;
        if (_clusterBuffer.IsValid) _ctx.Renderer.DestroyBuffer(_clusterBuffer);
        _clusterTilesX = tilesX;
        _clusterTilesY = tilesY;
        _clusterMasks = new uint[tilesX * tilesY * ClusterZSlices * 2]; // two mask words per froxel (64 lights)
        _clusterBuffer = _ctx.Renderer.CreateBuffer(new BufferDesc(
            "PbrClusterMasks", (ulong)(_clusterMasks.Length * sizeof(uint)),
            BufferUsage.Storage | BufferUsage.CopyDst));
    }

    // World-space camera forward from the row-vector view matrix (third column is -forward).
    private static Vector3 CameraForward(in Matrix4x4 view) =>
        Vector3.Normalize(new Vector3(-view.M13, -view.M23, -view.M33));

    private static int ClusterSlice(float viewZ, float near, float far) =>
        Math.Clamp((int)(MathF.Log(Math.Max(viewZ, near) / near) / MathF.Log(far / near) * ClusterZSlices),
            0, ClusterZSlices - 1);

    /// <summary>Forward+ CPU binning: for every point/spot light, mark the froxels its bounding
    /// sphere (position, range) can touch. Conservative on purpose — tile/slice ranges from a
    /// projected view-space AABB, padded ±1 froxel against CPU/GPU float divergence at cell
    /// boundaries. A false-positive bit costs a near-zero shading add; a false negative would
    /// change pixels, so inclusion always wins. Directional lights are never clustered.</summary>
    private void BuildClusters(PbrScene scene)
    {
        Array.Clear(_clusterMasks);
        var view = scene.Camera.View;
        var proj = scene.Camera.Projection;
        // Near/far from the row-vector perspective projection (M33 = f/(n-f), M43 = n·f/(n-f)).
        // Degenerate extraction (orthographic/custom) keeps the previous values.
        if (MathF.Abs(proj.M33) > 1e-6f && MathF.Abs(proj.M33 + 1f) > 1e-6f)
        {
            var n = proj.M43 / proj.M33;
            var f = proj.M43 / (proj.M33 + 1f);
            if (n > 0f && f > n) { _clusterNear = n; _clusterFar = f; }
        }

        var count = Math.Min(scene.Lights.Count, FrameUniformsGpu.MaxSceneLights);
        for (var i = 0; i < count; i++)
        {
            var light = scene.Lights[i];
            if (light.Type == PbrLightType.Directional) continue;
            var radius = Math.Max(light.Range, 0.01f);
            var centerView = Vector3.Transform(light.Position, view);
            var viewZ = -centerView.Z; // row-vector look-at: -Z is forward
            if (viewZ + radius <= _clusterNear || viewZ - radius >= _clusterFar) continue;

            var slice0 = Math.Max(ClusterSlice(viewZ - radius, _clusterNear, _clusterFar) - 1, 0);
            var slice1 = Math.Min(ClusterSlice(viewZ + radius, _clusterNear, _clusterFar) + 1, ClusterZSlices - 1);

            // Screen rect from the 8 corners of the view-space AABB around the sphere. Any corner
            // at or in front of the near plane → the sphere may wrap the camera → full screen.
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            var fullScreen = false;
            for (var c = 0; c < 8 && !fullScreen; c++)
            {
                var corner = centerView + new Vector3(
                    (c & 1) == 0 ? -radius : radius,
                    (c & 2) == 0 ? -radius : radius,
                    (c & 4) == 0 ? -radius : radius);
                if (corner.Z >= -_clusterNear) { fullScreen = true; break; }
                var clip = Vector4.Transform(new Vector4(corner, 1f), proj);
                var ndcX = clip.X / clip.W;
                var ndcY = clip.Y / clip.W;
                var sx = (ndcX * 0.5f + 0.5f) * _ctx.Width;
                var sy = (1f - (ndcY * 0.5f + 0.5f)) * _ctx.Height;
                minX = Math.Min(minX, sx); maxX = Math.Max(maxX, sx);
                minY = Math.Min(minY, sy); maxY = Math.Max(maxY, sy);
            }

            int tx0 = 0, tx1 = _clusterTilesX - 1, ty0 = 0, ty1 = _clusterTilesY - 1;
            if (!fullScreen)
            {
                tx0 = Math.Clamp((int)(minX / ClusterTileSize) - 1, 0, _clusterTilesX - 1);
                tx1 = Math.Clamp((int)(maxX / ClusterTileSize) + 1, 0, _clusterTilesX - 1);
                ty0 = Math.Clamp((int)(minY / ClusterTileSize) - 1, 0, _clusterTilesY - 1);
                ty1 = Math.Clamp((int)(maxY / ClusterTileSize) + 1, 0, _clusterTilesY - 1);
                if (tx1 < tx0 || ty1 < ty0) continue; // fully off-screen
            }

            var word = i >> 5;
            var bit = 1u << (i & 31);
            for (var sz = slice0; sz <= slice1; sz++)
                for (var ty = ty0; ty <= ty1; ty++)
                {
                    var row = (sz * _clusterTilesY + ty) * _clusterTilesX;
                    for (var tx = tx0; tx <= tx1; tx++)
                        _clusterMasks[(row + tx) * 2 + word] |= bit;
                }
        }
        _ctx.Renderer.UpdateBuffer<uint>(_clusterBuffer, 0, _clusterMasks);
        // IsEnabled is checked explicitly because the counting loop below is the cost here —
        // [LoggerMessage] would guard the formatting but this walks every froxel word first.
        if (_ctx.Log.IsEnabled(LogLevel.Debug))
        {
            var empty = 0; var bits = 0L;
            foreach (var m in _clusterMasks)
            {
                if (m == 0) empty++;
                bits += BitOperations.PopCount(m);
            }
            var froxels = _clusterMasks.Length / 2;
            LogClusterStats(_ctx.Log, froxels, empty, (double)bits / froxels);
        }
    }

    private void UploadFrameUniforms(PbrScene scene)
    {
        // Filled IN PLACE through a ref to the field — never `var frame = new FrameUniformsGpu {…}`.
        // See _frameUniforms: a 31 KB local kills the wasm runtime at tier-up time.
        ref var frame = ref _frameUniforms;
        // The field persists between frames, so start from zero to keep the semantics of the fresh
        // struct this used to allocate. AmbientSh is the one that would actually bite: it is written
        // only when the scene carries SH coefficients, and its [0].w is the flag that switches the
        // shader onto the SH ambient path — a stale one keeps that path on after a scene drops it.
        frame = default;
        frame.Time = new Vector4(scene.ElapsedSeconds, 0f, 0f, 0f);
        frame.CameraPos = new Vector4(scene.Camera.Position, 0f);
        frame.Ambient = new Vector4(scene.Ambient.Sky, scene.Ambient.Exposure);
        frame.AmbientEquator = new Vector4(scene.Ambient.Equator, Math.Min(scene.Lights.Count, FrameUniformsGpu.MaxSceneLights));
        frame.AmbientGround = new Vector4(scene.Ambient.Ground, scene.Ambient.Flat ? 1f : 0f);
        // x: sky-reflection specular enabled (Godot reflected_light_source = Sky).
        frame.AaSettings = new Vector4(
            scene.HasSkyBackground && scene.SkyReflections ? 1f : 0f,
            _specularAaVariance, _specularAaClamp, 0f);
        frame.CameraForward = new Vector4(CameraForward(scene.Camera.View), _clusterNear);
        frame.ClusterParams = new Vector4(_clusterTilesX, _clusterTilesY, ClusterZSlices, _clusterFar);
        // x: 1/shadowMapSize (per-layer texel). yzw: tone mapping — mode, exposure, white point.
        frame.ShadowSettings = new Vector4(
            1f / _shadows.MapSize,
            (float)scene.Tonemap.Mode,
            scene.Tonemap.Exposure,
            scene.Tonemap.White);
        frame.ShadowFilter = new Vector4(_shadows.BlurTexels, 0f, 0f, 0f);
        // L2 sky-SH ambient: coefficients pass through verbatim; [0].w flags the SH path on.
        if (scene.Ambient.Sh is { Length: 9 } sh)
        {
            frame.AmbientSh[0] = new Vector4(sh[0], 1f);
            for (var i = 1; i < 9; i++)
            {
                frame.AmbientSh[i] = new Vector4(sh[i], 0f);
            }
        }
        for (var i = 0; i < scene.Lights.Count && i < FrameUniformsGpu.MaxSceneLights; i++)
        {
            frame.Lights[i] = scene.Lights[i].ToGpu();
        }

        // Per-face light-space matrices from the shadow plan.
        foreach (var (lightIndex, face, _, vp) in _shadows.Views)
        {
            frame.SceneLightShadowMatrices[lightIndex * 6 + face] = vp;
        }
        // Per-light shadow params: base array layer (spotAngles.z), strength (spotAngles.w),
        // face count (shadowAtlas.y), soft-shadow flag (shadowAtlas.w) and shadow texel world
        // size (sizeParams.y — the bias scale, see ShadowFeature). shadowAtlas.x carries
        // the distance-attenuation decay, .z the LIGHT_PARAM_SPECULAR amount and sizeParams.x
        // the light's angular/world size (all set by ToGpu) and must be preserved here.
        for (var i = 0; i < scene.Lights.Count && i < FrameUniformsGpu.MaxSceneLights; i++)
        {
            if (_shadows.BaseLayer(i) < 0) continue;
            var light = frame.Lights[i];
            light.SpotAngles.Z = _shadows.BaseLayer(i);
            light.SpotAngles.W = Math.Clamp(scene.Lights[i].ShadowStrength, 0f, 1f);
            light.ShadowAtlas = new Vector4(light.ShadowAtlas.X, _shadows.FaceCount(i), light.ShadowAtlas.Z, scene.Lights[i].SoftShadows ? 1f : 0f);
            light.SizeParams.Y = _shadows.TexelWorld(i);
            frame.Lights[i] = light;
        }
        _ctx.Renderer.UpdateBuffer<FrameUniformsGpu>(_frameUniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref frame, 1));
        if (CaptureFrameLightsForTest) _lastFrameLightsForTest = frame.Lights;
    }

    [LoggerMessage(EventId = 91, Level = LogLevel.Debug, Message = "froxels={Froxels} emptyWords={EmptyWords} avgBits={AverageBits:F2}")]
    private static partial void LogClusterStats(ILogger logger, int froxels, int emptyWords, double averageBits);
}
