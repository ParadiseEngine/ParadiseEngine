using System.Diagnostics;
using System.Numerics;
using Hexa.NET.ImGui;
using Microsoft.Extensions.Logging;
using Paradise.Features;
using Paradise.Assets.Gltf;
using Paradise.Rendering.Pbr;
using Paradise.Rendering.WebGPU;
using Paradise.Ui.ImGui;
using Paradise.Windowing;
using Paradise.Windowing.Sdl;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Paradise.Rendering.Sample;

/// <summary>Interactive renderer feature lab with an independent ImGui overlay.</summary>
internal sealed class RendererShowcase : IDisposable
{
    private readonly GiDemoScene _room;
    private readonly SkinnedMannequin _rig = new();
    private readonly Matrix4x4[] _palette = new Matrix4x4[SkinnedMannequin.JointCount];
    private readonly FeatureDefinition[] _definitions = PbrFeatures.All.ToArray();
    private readonly bool[] _initial;
    private readonly Dictionary<string, double> _passMilliseconds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _passOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _featureMilliseconds = new(StringComparer.Ordinal);
    private readonly string[] _featureLabels;
    private bool _timingSupported;
    private bool _profile = true;
    private double _renderMilliseconds;
    private double _submitMilliseconds;
    private Vector2 _allOffButton;
    private Vector2 _restoreButton;
    private int _frame;
    private bool _paused;
    private bool _soft = true;
    private float _focus = 8;
    private float _exposure;
    private int _cascades = 4;
    private float _split = 0.65f;
    private PbrRenderer Renderer => _room.Renderer;
    private PbrScene Scene => _room.Scene;

    private RendererShowcase(WebGpuRenderer backend, uint width, uint height, ILogger logger)
    {
        GiDemoScene.ProbeGi = GiDemoScene.RayTracedAo = true;
        GiDemoScene.Decals = GiDemoScene.Fog = GiDemoScene.Reflections = true;
        GiDemoScene.ExtraLights = 20;
        GiDemoScene.RaysPerProbe = 32;
        GiDemoScene.MaxProbes = 512;
        _timingSupported = backend.SupportsPassTiming;
        _room = new GiDemoScene(backend, width, height, null, logger);
        _room.Drag(0, 0);
        Scene.Ssao = new PbrSsao { Enabled = true };
        Scene.RayTracedAo = new PbrRayTracedAo { Enabled = true, RaysPerPixel = 4 };
        Scene.ContactShadows = new PbrContactShadows { Enabled = true };
        Scene.Visibility.OcclusionEnabled = true;
        Scene.MotionVectors = new PbrMotionVectors { Enabled = true };
        Scene.Taa = new PbrTaa { Enabled = true };
        Scene.Fxaa = new PbrFxaa { Enabled = true };
        Scene.Exposure = new PbrExposure { Enabled = true, CompensationEv = 0.2f };
        Scene.DepthOfField = new PbrDepthOfField { Enabled = true, FocusDistance = _focus, MaxRadiusPixels = 5 };
        Scene.MotionBlur = new PbrMotionBlur { Enabled = true, MaxRadiusPixels = 8 };
        Scene.ColorGrading = new PbrColorGrading { Enabled = true, Saturation = 1.08f, Temperature = 0.1f };
        Scene.LensDistortion = new PbrLensDistortion { Enabled = true, Strength = 0.025f };
        Scene.ChromaticAberration = new PbrChromaticAberration { Enabled = true, IntensityPixels = 0.6f };
        Scene.Vignette = new PbrVignette { Enabled = true, Intensity = 0.15f };
        Scene.FilmGrain = new PbrFilmGrain { Enabled = true, Intensity = 0.008f };
        Scene.Sharpening = new PbrSharpening { Enabled = true, Strength = 0.15f };
        _exposure = Scene.Exposure.CompensationEv;
        Renderer.Pipeline.Find<ShadowFeature>()!.MapSize = 2048;
        AddGeometry();
        _initial = _definitions.Select(d => Program.Features.IsEnabled(d.Id)).ToArray();
        _featureLabels = _definitions.Select(d => d.Name["rendering.".Length..]).ToArray();
    }

    private void AddGeometry()
    {
        var (vertices, indices) = Procedural.UnitCube();
        PbrMesh Cube(Vector4 color, float metallic = 0, float roughness = 0.6f) => new([
            Renderer.UploadPrimitive(vertices, indices, Renderer.Materials.AddDefaultMaterial(color, metallic, roughness))]);
        void Add(PbrMesh mesh, Vector3 scale, Vector3 position, PbrGiMode gi = PbrGiMode.Static) => Scene.Instances.Add(new PbrInstance
        { Mesh = mesh, Model = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(position), GiMode = gi });
        // Glossy floor catches SSR and the moving character. Repeated columns share one upload.
        Add(Cube(new Vector4(0.6f, 0.65f, 0.7f, 1), 0.85f, 0.12f), new Vector3(2.4f, 0.04f, 2), new Vector3(1.3f, 0.03f, 1.5f));
        var glassProgram = Renderer.RegisterMaterialProgram(
            ShaderProgramLoader.Load(typeof(RendererShowcase).Assembly, "Shaders.showcaseGlass"));
        var glass = new GltfMaterialData("Glass", new Vector4(0.3f, 0.8f, 0.9f, 0.35f), 0, 0.12f,
            Vector3.Zero, 1, 1, 0, GltfAlphaMode.Blend, 0.5f, true,
            -1, -1, -1, -1, -1, GltfUvTransform.Identity);
        var glassMaterial = Renderer.Materials.AddMaterial(in glass, [], glassProgram, [],
            [new MaterialTarget(7, PbrTargets.SceneColor)]);
        Add(new PbrMesh([Renderer.UploadPrimitive(vertices, indices, glassMaterial)]),
            new Vector3(1.1f, 1.7f, 0.04f), new Vector3(-1.6f, 0.9f, 2.1f));
        var repeated = Cube(new Vector4(0.8f, 0.55f, 0.12f, 1), 0.4f);
        for (var i = 0; i < 30; i++)
            Add(repeated, new Vector3(0.13f, 0.4f + i % 3 * 0.12f, 0.13f), new Vector3(-2.6f + i % 10 * 0.55f, 0.3f, -2.5f + i / 10 * 0.4f));
        // Deliberately hidden and off-camera draws give both culling controls observable work.
        for (var i = 0; i < 12; i++)
            Add(repeated, new Vector3(0.15f), new Vector3(i < 6 ? -1.1f : 30f, 0.4f + i * 0.05f, -1.3f), PbrGiMode.Disabled);
        var skin = Renderer.UploadSkinnedPrimitive(_rig.Vertices, _rig.JointsWeights, _rig.Indices,
            Renderer.Materials.AddDefaultMaterial(new Vector4(0.05f, 0.65f, 0.9f, 1)));
        Scene.Instances.Add(new PbrInstance { Mesh = new PbrMesh([skin]), JointOffset = 0,
            Model = Matrix4x4.CreateScale(0.65f) * Matrix4x4.CreateTranslation(1.9f, 0.1f, 1.8f), GiMode = PbrGiMode.Dynamic });
        Scene.Lights.Add(new PbrLight { Type = PbrLightType.Spot, Position = new Vector3(-2, 3, 2),
            Direction = Vector3.Normalize(new Vector3(1, -2, -2)), Color = new Vector3(1, 0.5f, 0.2f),
            Range = 8, Intensity = 7, CastsShadows = true, SoftShadows = true, SpotOuterDegrees = 38, SpotInnerDegrees = 22 });
    }

    private void DrawPanel()
    {
        ImGuiApi.SetNextWindowPos(new Vector2(12, 12), ImGuiCond.FirstUseEver);
        ImGuiApi.SetNextWindowSize(new Vector2(460, ImGuiApi.GetIO().DisplaySize.Y - 24), ImGuiCond.Always);
        ImGuiApi.Begin("Renderer laboratory");
        ImGuiText.Show($"{ImGuiApi.GetIO().Framerate:F0} FPS | {Scene.Instances.Count} objects");
        ImGuiText.Show("Drag outside panel to orbit; wheel to zoom.");
        ImGuiApi.Checkbox("Pause animation", ref _paused);
        if (ImGuiApi.Button("Restore initial switches")) Restore();
        _restoreButton = (ImGuiApi.GetItemRectMin() + ImGuiApi.GetItemRectMax()) * 0.5f;
        ImGuiApi.SameLine();
        if (ImGuiApi.Button("All off")) foreach (var d in _definitions) Program.Features.Set(d.Id, false);
        _allOffButton = (ImGuiApi.GetItemRectMin() + ImGuiApi.GetItemRectMax()) * 0.5f;
        if (_timingSupported)
        {
            ImGuiApi.Checkbox("Measure GPU timings (waits for GPU)", ref _profile);
            ImGuiText.Show(_profile
                ? $"Render + GPU wait: {_renderMilliseconds:F2} ms | CPU submit: {_submitMilliseconds:F2} ms"
                : $"CPU render/submit: {_submitMilliseconds:F2} ms | GPU measurement off");
        }
        else ImGuiText.Show("GPU timings need -p:ParadiseProfiling=true\nand an adapter with timestamp queries.");
        ImGuiApi.Separator();
        ImGuiApi.BeginChild("Feature switches", new Vector2(0, Math.Max(180, ImGuiApi.GetIO().DisplaySize.Y - 465)));
        ImGuiText.Disabled("Feature");
        ImGuiApi.SameLine(310);
        ImGuiText.Disabled("GPU pass sum");
        for (var index = 0; index < _definitions.Length; index++)
        {
            var definition = _definitions[index];
            var enabled = Program.Features.IsEnabled(definition.Id);
            var label = _featureLabels[index];
            if (ImGuiApi.Checkbox(label, ref enabled)) Program.Features.Set(definition.Id, enabled);
            if (ImGuiApi.IsItemHovered())
            {
                ImGuiApi.BeginTooltip();
                // A tooltip auto-sizes: wrapping at its initially empty right edge would
                // collapse the description to one character per line.
                var wrapWidth = Math.Min(ImGuiApi.GetFontSize() * 28f,
                    Math.Max(1f, ImGuiApi.GetIO().DisplaySize.X - 40f));
                ImGuiApi.PushTextWrapPos(ImGuiApi.GetCursorPosX() + wrapWidth);
                ImGuiText.Show(definition.Summary);
                ImGuiApi.PopTextWrapPos();
                ImGuiApi.EndTooltip();
            }
            ImGuiApi.SameLine(310);
            ImGuiText.Disabled(FeatureTiming(label, enabled));
        }
        ImGuiApi.EndChild();
        var ssao = Scene.Ssao.Enabled;
        if (ImGuiApi.Checkbox("SSAO (scene setting)", ref ssao)) Scene.Ssao = Scene.Ssao with { Enabled = ssao };
        ImGuiApi.SameLine(310);
        ImGuiText.Disabled(ssao ? "shared: Main" : "off");
        ImGuiApi.Separator();
        ImGuiApi.Checkbox("Poisson PCSS (off: hardware PCF)", ref _soft);
        if (ImGuiApi.SliderInt("Cascades", ref _cascades, 1, 4)) Renderer.Pipeline.Find<ShadowFeature>()!.CascadeCount = _cascades;
        if (ImGuiApi.SliderFloat("PSSM split", ref _split, 0, 1)) Renderer.Pipeline.Find<ShadowFeature>()!.CascadeSplitLambda = _split;
        if (ImGuiApi.SliderFloat("Focus (m)", ref _focus, 2, 15)) Scene.DepthOfField = Scene.DepthOfField with { FocusDistance = _focus };
        if (ImGuiApi.SliderFloat("Exposure EV", ref _exposure, -3, 3)) Scene.Exposure = Scene.Exposure with { CompensationEv = _exposure };
        var batching = Renderer.Pipeline.Find<InstancingFeature>()!;
        ImGuiText.Show($"Scene draws: {batching.DrawCalls}; saved: {batching.SavedDrawCalls}");
        ImGuiText.Show($"Frustum rejected: {Renderer.Pipeline.Find<FrustumCullingFeature>()!.CulledDrawCount}");
        ImGuiText.Show("Occlusion uses individual opaque draws.\nDisable it to compare instancing.\nDepth prepass gates screen-space effects.\nMotion vectors feed TAA and motion blur.");
        if (ImGuiApi.CollapsingHeader("Last frame passes"))
        {
            ImGuiText.Wrapped("GPU passes may overlap; sums are not exclusive feature cost. Inline effects share Main. Toggle features to compare full-frame time. Profiling waits for readback.");
            foreach (var (pass, ms) in _passMilliseconds) ImGuiText.Show($"{pass}: {ms:F3} ms");
            if (_passMilliseconds.Count == 0)
                foreach (var pass in Renderer.LastPassNames) ImGuiText.Show(pass);
        }
        ImGuiApi.End();
    }

    private static string? PassOwner(string pass) => pass.Split('.')[0] switch
    {
        "Shadow" => "shadows", "Prepass" => "depthNormalPrepass", "Visibility" => "occlusionCulling",
        "MotionVectors" => "motionVectors", "Rtao" => "rayTracedAo", "Ssr" => "screenSpaceReflection",
        "Gi" => "globalIllumination", "LightCull" => "lightCulling", "Main" => "scene",
        "SceneColor" => "sceneColorCapture", "Fog" => "fog", "Taa" => "temporalAntiAliasing",
        "Exposure" => "exposure", "Bloom" => "bloom", "Composite" => "composite",
        "Fxaa" => "fxaa", "Presentation" => "presentation",
        "Post" when pass.Length > 5 => char.ToLowerInvariant(pass[5]) + pass[6..],
        _ => null,
    };

    private string FeatureTiming(string name, bool enabled)
    {
        if (!enabled) return "off";
        if (name is "frustumCulling" or "instancing") return "CPU / draws";
        if (name is "contactShadows" or "decals") return "shared: Main";
        if (!_timingSupported || !_profile) return "unavailable";
        return _featureMilliseconds.TryGetValue(name, out var milliseconds) ? $"{milliseconds:F3} ms" : "no pass";
    }

    private void Restore()
    {
        for (var i = 0; i < _definitions.Length; i++) Program.Features.Set(_definitions[i].Id, _initial[i]);
        Scene.TemporalHistoryVersion++;
    }

    private void Render(WebGpuRenderer backend)
    {
        var started = Stopwatch.GetTimestamp();
        backend.PassTimingEnabled = _profile && _timingSupported;
        _rig.Pose(_frame / 60f, Matrix4x4.Identity, _palette);
        Renderer.SetJointPalette(0, _palette);
        _room.SoftShadowsOverride = _soft;
        _room.RenderFrame(!_paused);
        if (!_paused) _frame++;
        _submitMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _passMilliseconds.Clear();
        _featureMilliseconds.Clear();
        if (backend.PassTimingEnabled)
        {
            var values = backend.ReadPassTimings();
            if (values.Length == Renderer.LastPassNames.Count)
                for (var i = 0; i < values.Length; i++)
                {
                    var pass = Renderer.LastPassNames[i];
                    _passMilliseconds[pass] = _passMilliseconds.GetValueOrDefault(pass) + values[i];
                    if (!_passOwners.TryGetValue(pass, out var owner))
                    {
                        owner = PassOwner(pass);
                        _passOwners.Add(pass, owner);
                    }
                    if (owner is not null)
                        _featureMilliseconds[owner] = _featureMilliseconds.GetValueOrDefault(owner) + values[i];
                }
        }
        _renderMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    public static int Run(string[] args, ILoggerFactory logs)
    {
        string? Value(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var headless = args.Contains("--headless");
        var frames = 0;
        if (headless && !int.TryParse(Value("--headless"), out frames))
            throw new ArgumentException("--headless needs a positive frame count.");
        var sweep = args.Contains("--sweep");
        if (sweep && !headless) throw new ArgumentException("--sweep requires --headless N.");
        if (headless && frames < 1) throw new ArgumentException("--headless needs a positive frame count.");
        uint width = 1280, height = 800;
        using var platform = new SdlWindowPlatform();
        using var window = headless ? null : platform.CreateWindow(new WindowOptions("Paradise renderer laboratory", width, height));
        width = window?.Width ?? width;
        height = window?.Height ?? height;
        using var backend = headless ? WebGpuRenderer.CreateHeadless(width, height, logs.CreateLogger("WebGPU"))
            : new WebGpuRenderer(window!.CreateSurface(), logger: logs.CreateLogger("WebGPU"));
        using var scene = new RendererShowcase(backend, width, height, logs.CreateLogger("Pbr"));
        scene._profile = !args.Contains("--no-profile");
        using var ui = new ImGuiUiCore(width, height);
        ui.DisableIniFile();
        using var overlay = new ImGuiWebGpuRenderer(backend.NativeDevice, backend.NativeColorFormat);
        ui.AddDraw(scene.DrawPanel);
        var pending = new List<ImGuiTextureOp>();
        var clock = Stopwatch.StartNew();
        var pointer = Vector2.Zero;
        var dragging = false;
        if (window is not null) window.Resized += (w, h) => { backend.Resize(w, h); scene._room.Resize(w, h); };
        var observedPasses = new SortedSet<string>(StringComparer.Ordinal);
        var benchmark = args.Contains("--bench");
        using var process = Process.GetCurrentProcess();
        var sampleTime = 0.0;
        var sampleFrames = 0;
        var maxSaved = 0;
        var maxCulled = 0;
        var count = headless ? Math.Max(frames, sweep ? scene._definitions.Length * 4 + 12 : frames) : int.MaxValue;
        for (var frame = 0; frame < count && window?.CloseRequested != true; frame++)
        {
            var frameStart = Stopwatch.GetTimestamp();
            platform.Pump();
            if (window is not null) while (window.TryReadEvent(out var input))
            {
                var e = input.Event;
                var consumed = ui.Input.Handle(e);
                if (e.Kind == WindowEventKind.Button && e.Source == EventSource.Mouse && e.Code == (byte)PointerButton.Left)
                {
                    dragging = e.Pressed && !consumed;
                    if (dragging) pointer = new Vector2(e.X, e.Y);
                }
                if (e.Kind == WindowEventKind.PointerMove)
                {
                    var next = new Vector2(e.X, e.Y);
                    if (dragging && !consumed) scene._room.Drag(next.X - pointer.X, next.Y - pointer.Y);
                    pointer = next;
                }
                if (e.Kind == WindowEventKind.Scroll && !consumed) scene._room.Zoom(e.Y);
            }
            if (sweep)
            {
                if (frame is 10 or 14)
                {
                    width = frame == 10 ? 960u : 1280u;
                    height = frame == 10 ? 640u : 800u;
                    backend.Resize(width, height);
                    scene._room.Resize(width, height);
                    ui.Input.Handle(WindowEvent.Resize(width, height));
                }
                // Drive real ImGui buttons through the same input path as SDL, including the
                // presentation-disabled interval, before testing every individual switch.
                if (frame is 2 or 3 or 5 or 6)
                {
                    var point = frame < 5 ? scene._allOffButton : scene._restoreButton;
                    ui.Input.Handle(WindowEvent.Mouse(PointerButton.Left, frame is 2 or 5, point.X, point.Y));
                }
                if (frame == 4 && scene._definitions.Any(d => Program.Features.IsEnabled(d.Id)))
                    throw new InvalidOperationException("ImGui All off did not disable every feature.");
                if (frame == 7 && scene._definitions.Where((d, i) => Program.Features.IsEnabled(d.Id) != scene._initial[i]).Any())
                    throw new InvalidOperationException("ImGui Restore did not restore the initial switches.");
                var step = frame - 8;
                if (step >= 0 && step < scene._definitions.Length * 4)
                    Program.Features.Set(scene._definitions[step / 4].Id, step % 4 >= 2);
                if (step == scene._definitions.Length * 4) scene.Restore();
            }
            ui.Input.Tick(headless ? frame / 60.0 : clock.Elapsed.TotalSeconds);
            var snapshot = ui.AcquireSnapshotForRender(pending, out _);
            backend.OverlayPass = (encoder, view) =>
            {
                // Clear within the existing submission when the PBR chain cannot fill the
                // backbuffer. The UI must remain usable without presenting a second frame.
                if (!Program.Features.IsEnabled(PbrFeatures.Scene.Id)
                    || !Program.Features.IsEnabled(PbrFeatures.Composite.Id)
                    || !Program.Features.IsEnabled(PbrFeatures.Presentation.Id))
                {
                    var clear = new WebGpuSharp.RenderPassDescriptor
                    {
                        Label = "ShowcaseFallbackClear",
                        ColorAttachments = new WebGpuSharp.RenderPassColorAttachment[]
                        {
                            new()
                            {
                                View = view,
                                LoadOp = WebGpuSharp.LoadOp.Clear,
                                StoreOp = WebGpuSharp.StoreOp.Store,
                                ClearValue = new WebGpuSharp.Color(0.04, 0.05, 0.07, 1),
                                DepthSlice = null,
                            },
                        },
                    };
                    var pass = encoder.BeginRenderPass(in clear);
                    pass.End();
                }
                overlay.ApplyTextureOps(pending);
                if (snapshot is not null) overlay.Render(encoder, view, window?.Width ?? width, window?.Height ?? height, snapshot);
            };
            scene.Render(backend);
            if (benchmark && frame >= 60)
            {
                sampleTime += Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds;
                sampleFrames++;
                if (sampleFrames == 300 || frame == count - 1)
                {
                    process.Refresh();
                    Console.WriteLine($"[showcase bench] frame {frame + 1}: {sampleTime / sampleFrames:F3} ms/iteration; RSS {process.WorkingSet64 / 1048576.0:F1} MiB; managed {GC.GetTotalMemory(false) / 1048576.0:F1} MiB; GPU wait {(backend.PassTimingEnabled ? "on" : "off")}");
                    sampleTime = 0;
                    sampleFrames = 0;
                }
            }
            if (sweep)
            {
                observedPasses.UnionWith(scene.Renderer.LastPassNames);
                maxSaved = Math.Max(maxSaved, scene.Renderer.Pipeline.Find<InstancingFeature>()!.SavedDrawCalls);
                maxCulled = Math.Max(maxCulled, scene.Renderer.Pipeline.Find<FrustumCullingFeature>()!.CulledDrawCount);
            }
        }
        backend.OverlayPass = null;
        if (Value("--screenshot") is { } path)
        {
            var pixels = backend.ReadbackColor(out var w, out var h);
            using var file = File.Create(path);
            PngWriter.Write(file, new ColorReadback(pixels, w, h), backend.ColorFormat);
        }
        if (sweep)
        {
            Console.WriteLine($"ImGui All off/Restore and resize checks passed; {scene._definitions.Length} switches exercised.");
            Console.WriteLine($"Peak saved draws: {maxSaved}; peak frustum rejected: {maxCulled}.");
            Console.WriteLine($"Observed passes: {string.Join(", ", observedPasses)}");
        }
        Console.WriteLine($"Renderer showcase completed{(sweep ? " runtime feature sweep" : "")}.");
        return 0;
    }

    public void Dispose() => _room.Dispose();
}
