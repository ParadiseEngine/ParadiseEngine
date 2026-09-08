using TUnit.Assertions.Enums;
using System.Numerics;
using Paradise.Features;
using Paradise.Rendering.Graph;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

/// <summary>Checks feature switches against submitted passes and rendered output.</summary>
/// <remarks>Covers startup overrides and runtime disable/re-enable transitions.</remarks>
public class FeatureSwitchTests
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

    /// <summary>A lit scene with a shadow-casting sun and a bright cube over a ground plane: every
    /// built-in feature has something to do with it.</summary>
    private static PbrScene BuildScene(PbrRenderer pbr)
    {
        var (vertices, indices) = Procedural.UnitCube();
        var groundId = pbr.Materials.AddDefaultMaterial(new Vector4(0.45f, 0.46f, 0.5f, 1f));
        var cubeId = pbr.Materials.AddDefaultMaterial(new Vector4(0.9f, 0.7f, 0.2f, 1f), metallic: 0.1f, roughness: 0.35f);
        var ground = new PbrMesh([pbr.UploadPrimitive(vertices, indices, groundId)]);
        var cube = new PbrMesh([pbr.UploadPrimitive(vertices, indices, cubeId)]);

        var eye = new Vector3(2.2f, 1.8f, 3.2f);
        var scene = new PbrScene
        {
            Camera = new PbrCamera
            {
                View = PbrMath.LookAt(eye, Vector3.Zero, Vector3.UnitY),
                Projection = PbrMath.Perspective(MathF.PI / 3f, 1f, 0.1f, 100f),
                Position = eye,
            },
            Tonemap = new PbrTonemap { Mode = PbrTonemapMode.Filmic, Exposure = 1.1f, White = 4f },
            Bloom = new PbrBloom { Enabled = true, Threshold = 0.9f, Knee = 0.4f, Intensity = 0.7f },
        };
        scene.Lights.Add(new PbrLight
        {
            Type = PbrLightType.Directional,
            Direction = Vector3.Normalize(new Vector3(0.45f, 1f, 0.55f)),
            Intensity = 3.2f,
            CastsShadows = true,
        });
        scene.Instances.Add(new PbrInstance
        {
            Mesh = ground,
            Model = Matrix4x4.CreateScale(new Vector3(6f, 0.12f, 6f)) * Matrix4x4.CreateTranslation(0f, -0.7f, 0f),
        });
        scene.Instances.Add(new PbrInstance { Mesh = cube });
        return scene;
    }

    /// <summary>Flip the capture switch and let the pipeline adopt it NOW. A switch is adopted at
    /// the start of a frame, so a host that wants to bind a material to the view before the next
    /// one begins the frame itself — which is what BeginFrame is public for.</summary>
    private static void SetCapture(PbrRenderer pbr, bool enabled)
    {
        pbr.Switches.Set(PbrFeatures.SceneColorCapture.Id, enabled);
        pbr.Pipeline.BeginFrame();
    }

    /// <summary>The capture feature, reached the way anything reaches a feature.</summary>
    private static SceneColorCaptureFeature Capture(PbrRenderer pbr) =>
        pbr.Pipeline.Find<SceneColorCaptureFeature>()!;

    private static int Passes(PbrRenderer pbr, string prefix) =>
        pbr.LastPassNames.Count(name => name.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>Shadows off between two frames: the depth-only pass stops being submitted AND the
    /// picture brightens where the cube's shadow was. Both halves matter — the pass count alone
    /// would still pass if the plan the feature left behind kept the scene sampling a stale
    /// layer, which is exactly the bug the disable hook exists to prevent.</summary>
    [Test]
    public async Task shadows_switched_off_at_runtime_leave_the_frame_and_the_picture()
    {
        var backend = TryCreateHeadlessOrSkip();
        if (backend is null) return;
        using var _ = backend;

        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = BuildScene(pbr);

        for (var i = 0; i < 3; i++) pbr.RenderFrame(scene);
        var shadowPassesOn = Passes(pbr, "Shadow.");
        var litWithShadows = Brightness(backend);

        pbr.Switches.Set(PbrFeatures.Shadows.Id, false);
        pbr.RenderFrame(scene);
        var shadowPassesOff = Passes(pbr, "Shadow.");
        var litWithout = Brightness(backend);

        pbr.Switches.Set(PbrFeatures.Shadows.Id, true);
        pbr.RenderFrame(scene);
        var shadowPassesBack = Passes(pbr, "Shadow.");

        await Assert.That(shadowPassesOn).IsEqualTo(1);
        await Assert.That(shadowPassesOff).IsEqualTo(0);
        await Assert.That(shadowPassesBack).IsEqualTo(1);
        await Assert.That(litWithout).IsGreaterThan(litWithShadows);
    }

    /// <summary>The two levels are different questions and behave differently, which is the whole
    /// reason there are two. The SCENE saying "no bloom here" leaves the chain declared and lets
    /// the graph cull it; the SWITCH saying "not in this build" means the feature never runs and
    /// there is nothing to cull.</summary>
    [Test]
    public async Task a_scene_that_wants_no_bloom_culls_the_chain_and_a_switch_never_declares_it()
    {
        var backend = TryCreateHeadlessOrSkip();
        if (backend is null) return;
        using var _ = backend;

        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = BuildScene(pbr);

        for (var i = 0; i < 3; i++) pbr.RenderFrame(scene);
        var withBloom = Passes(pbr, "Bloom.");

        scene.Bloom = scene.Bloom with { Enabled = false };
        pbr.RenderFrame(scene);
        var sceneOffPasses = Passes(pbr, "Bloom.");
        var sceneOffCulled = pbr.CulledPassCountForTest;

        scene.Bloom = scene.Bloom with { Enabled = true };
        pbr.Switches.Set(PbrFeatures.Bloom.Id, false);
        pbr.RenderFrame(scene);
        var switchOffPasses = Passes(pbr, "Bloom.");
        var switchOffCulled = pbr.CulledPassCountForTest;

        await Assert.That(withBloom).IsGreaterThan(0);
        await Assert.That(sceneOffPasses).IsEqualTo(0);
        await Assert.That(switchOffPasses).IsEqualTo(0);
        await Assert.That(sceneOffCulled - switchOffCulled).IsEqualTo(withBloom);
    }

    /// <summary>The config file is read before the renderer is constructed, so the override lands
    /// on a name nothing has declared yet and has to still be in force at the first frame.</summary>
    [Test]
    public async Task a_configuration_read_before_the_renderer_reaches_its_first_frame()
    {
        var backend = TryCreateHeadlessOrSkip();
        if (backend is null) return;
        using var _ = backend;

        var config = TomlEngineConfiguration.Read("""
            # The integrated GPU cannot afford either.
            [[features]]
            name = "rendering.shadows"
            enabled = false

            [[features]]
            name = "rendering.bloom"
            enabled = false
            """);
        var switches = new FeatureSwitches(config);

        using var pbr = new PbrRenderer(backend, switches, Size, Size);
        var scene = BuildScene(pbr);
        pbr.RenderFrame(scene);
        var neverEnabled = Brightness(backend);

        // The same renderer with the features on and then switched off: a build that starts with
        // shadows off must reach the picture a build that turns them off does. It does not, if
        // the shadow plan a frame never ran is left zeroed — every light then claims array layer
        // 0 and the scene samples a map nothing has drawn into.
        using var toggled = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var toggledScene = BuildScene(toggled);
        toggled.RenderFrame(toggledScene);
        toggled.Switches.Set(PbrFeatures.Shadows.Id, false);
        toggled.Switches.Set(PbrFeatures.Bloom.Id, false);
        toggled.RenderFrame(toggledScene);
        var switchedOff = Brightness(backend);

        await Assert.That(Passes(pbr, "Shadow.")).IsEqualTo(0);
        await Assert.That(Passes(pbr, "Bloom.")).IsEqualTo(0);
        await Assert.That(switches.Unknown).IsEmpty();
        await Assert.That(neverEnabled).IsEqualTo(switchedOff).Within(0.01);
    }

    /// <summary>Probe GI off at runtime must hand the scene back to the SKY ambient, not to
    /// black. The probe volume the scene binds is what says "a probe grid covers this pixel", and
    /// it outlives the feature that filled it: left claiming coverage with the feature off, every
    /// surface inside it shades against the black fallback atlas and the picture collapses.</summary>
    [Test]
    public async Task probe_gi_switched_off_returns_the_scene_to_the_sky_ambient()
    {
        var backend = TryCreateHeadlessOrSkip();
        if (backend is null) return;
        using var _ = backend;

        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = BuildScene(pbr);
        scene.Gi = new PbrGi { Enabled = true, RaysPerProbe = 32, Hysteresis = 0.5f, MaxProbes = 512 };
        for (var i = 0; i < 4; i++) pbr.RenderFrame(scene);
        var withProbes = Brightness(backend);

        pbr.Switches.Set(PbrFeatures.GlobalIllumination.Id, false);
        pbr.RenderFrame(scene);
        var switchedOff = Brightness(backend);

        // The same scene that never asked for probes: what "off" has to look like.
        using var never = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var neverScene = BuildScene(never);
        never.RenderFrame(neverScene);
        var withoutProbes = Brightness(backend);

        await Assert.That(Passes(pbr, "Gi.")).IsEqualTo(0);
        await Assert.That(withProbes).IsGreaterThan(0d);
        await Assert.That(switchedOff).IsEqualTo(withoutProbes).Within(0.01);
    }

    /// <summary>The pre-pass off takes every screen-space effect with it, and the flags that say
    /// so live in a buffer the scene binds every frame whether or not the pre-pass ran. Left
    /// alone, the last enabled frame's flags still claim the ray-traced occlusion texture is
    /// real, the scene binds the black fallback for it, and ambient is multiplied by zero
    /// everywhere for as long as the feature stays off.</summary>
    [Test]
    public async Task the_prepass_switched_off_stops_the_effects_that_read_it()
    {
        var backend = TryCreateHeadlessOrSkip();
        if (backend is null) return;
        using var _ = backend;

        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = BuildScene(pbr);
        scene.Ssao = new PbrSsao { Enabled = true, Radius = 0.6f, Intensity = 2f };
        scene.RayTracedAo = new PbrRayTracedAo { Enabled = true, RaysPerPixel = 4, MaxDistance = 1f };
        for (var i = 0; i < 3; i++) pbr.RenderFrame(scene);

        pbr.Switches.Set(PbrFeatures.Prepass.Id, false);
        pbr.RenderFrame(scene);
        var switchedOff = Brightness(backend);

        // The same scene with neither occlusion asked for: what "no occlusion" looks like.
        using var never = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var neverScene = BuildScene(never);
        never.RenderFrame(neverScene);
        var withoutOcclusion = Brightness(backend);

        await Assert.That(Passes(pbr, "Prepass.")).IsEqualTo(0);
        await Assert.That(Passes(pbr, "Rtao.")).IsEqualTo(0);
        await Assert.That(switchedOff).IsEqualTo(withoutOcclusion).Within(0.01);
    }

    /// <summary>Checks every built-in disabled at startup, separately and together.</summary>
    /// <remarks>Render twice to catch state that was never initialized, including probe buffers the
    /// scene still binds.</remarks>
    [Test]
    public async Task every_built_in_can_be_configured_off_before_the_first_frame()
    {
        var backend = TryCreateHeadlessOrSkip();
        if (backend is null) return;
        using var _ = backend;

        var failures = new List<string>();
        // null = every feature at once, the case each single-feature run is a slice of.
        foreach (var only in PbrFeatures.All.Append(null))
        {
            var switches = new FeatureSwitches();
            foreach (var definition in PbrFeatures.All)
            {
                if (only is null || definition.Id == only.Id) switches.Set(definition.Id, false);
            }
            try
            {
                using var pbr = new PbrRenderer(backend, switches, Size, Size);
                var scene = BuildScene(pbr);
                // Every scene-side switch ON, so nothing is skipped for want of being asked for:
                // the configuration is the only thing keeping a feature out of the frame.
                scene.Ssao = new PbrSsao { Enabled = true, Radius = 0.6f, Intensity = 2f };
                scene.RayTracedAo = new PbrRayTracedAo { Enabled = true };
                scene.Ssr = new PbrScreenSpaceReflection { Enabled = true };
                scene.Gi = new PbrGi { Enabled = true, RaysPerProbe = 32, MaxProbes = 256 };
                pbr.RenderFrame(scene);
                pbr.RenderFrame(scene); // the second frame reads what the first left behind
            }
            catch (Exception error)
            {
                failures.Add($"{only?.Name ?? "everything"}: {error.GetType().Name} {error.Message}");
            }
        }

        await Assert.That(failures).IsEmpty();
    }

    /// <summary>The capture feature's target exists exactly while its switch is on, and its view
    /// event fires on the transition — synchronously, on the thread that flipped it, which is what
    /// lets a host switch capture on and then create the materials that bind the view.
    ///
    /// <para>Reached through the pipeline like any other feature: the renderer used to publish a
    /// <c>SceneColorCapture</c> property and a <c>SceneColorView</c> of its own, which made this
    /// one built-in special for no reason a game's feature could ever share.</para></summary>
    [Test]
    public async Task the_capture_feature_follows_its_own_switch()
    {
        var backend = TryCreateHeadlessOrSkip();
        if (backend is null) return;
        using var _ = backend;

        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var capture = Capture(pbr);
        var changes = 0;
        capture.ViewChanged += () => changes++;

        var offByDefault = capture.View.IsValid;
        SetCapture(pbr, true);
        var viewWhenOn = capture.View.IsValid;

        SetCapture(pbr, false);
        var viewWhenOff = capture.View.IsValid;

        await Assert.That(offByDefault).IsFalse();
        await Assert.That(viewWhenOn).IsTrue();
        await Assert.That(viewWhenOff).IsFalse();
        await Assert.That(changes).IsEqualTo(2);
    }

    /// <summary>Every built-in is listable with a description, which is what a debug panel or a
    /// <c>--list-features</c> flag renders — and what stops a config file being written from
    /// memory.</summary>
    [Test]
    public async Task the_renderer_declares_every_built_in_feature()
    {
        var backend = TryCreateHeadlessOrSkip();
        if (backend is null) return;
        using var _ = backend;

        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);

        var declared = pbr.Switches.Definitions.Select(d => d.Name).ToArray();
        var describedAll = pbr.Switches.Definitions.All(d => d.Summary.Length > 0);

        // PbrFeatures.All is what a host lists without a GPU; it has to be the same set.
        await Assert.That(declared.Order()).IsEquivalentTo(PbrFeatures.All.Select(d => d.Name).Order(), CollectionOrdering.Matching);
        await Assert.That(describedAll).IsTrue();
        await Assert.That(pbr.Pipeline.Features.Count).IsEqualTo(PbrFeatures.All.Count);
    }

    /// <summary>A game's feature lands where its slot says, not where its <c>Add</c> happened to
    /// fall — which is what lets it publish something an engine feature reads — and it is switched
    /// exactly like a built-in one.</summary>
    [Test]
    public async Task a_host_feature_can_be_ordered_before_a_built_in_and_switched_like_one()
    {
        var backend = TryCreateHeadlessOrSkip();
        if (backend is null) return;
        using var _ = backend;

        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var probe = new OrderProbe();
        pbr.Pipeline.Add(probe, PbrFeatureOrder.Scene - 1);
        var scene = BuildScene(pbr);

        pbr.RenderFrame(scene);
        var ranBeforeTheScene = probe.SawHdrTarget == false;

        pbr.Switches.Set(probe.Definition.Id, false);
        pbr.RenderFrame(scene);
        var order = pbr.Pipeline.Features.Select(f => f.Definition.Name).ToArray();

        await Assert.That(probe.Setups).IsEqualTo(1);
        await Assert.That(ranBeforeTheScene).IsTrue();
        await Assert.That(order[^1]).IsEqualTo(PbrFeatures.Presentation.Name);
        await Assert.That(Array.IndexOf(order, probe.Definition.Name))
            .IsLessThan(Array.IndexOf(order, PbrFeatures.Scene.Name));
    }

    private sealed class OrderProbe : IRenderFeature
    {
        public int Setups;
        public bool SawHdrTarget = true;

        public FeatureDefinition Definition { get; } = new("game.orderProbe", true, "A test feature.");
        public FrameRequirements Requires => FrameRequirements.None;
        public void Resize(uint width, uint height) { }

        /// <summary>The scene publishes nothing this can see, but it does OWN the HDR target and
        /// declares it at construction — so the blackboard is the honest probe: the scene has not
        /// published anything yet when this runs before it.</summary>
        public void Setup(in FrameContext frame)
        {
            Setups++;
            SawHdrTarget = frame.Blackboard.TryGet(PbrResults.Bloom, out _);
        }

        public void Dispose() { }
    }

    /// <summary>Mean channel value of the presented frame — enough to tell a shadowed picture from
    /// an unshadowed one without pinning pixels to an adapter.</summary>
    private static double Brightness(WebGpuRenderer backend)
    {
        var pixels = backend.ReadbackColor(out _, out _);
        var total = 0L;
        foreach (var value in pixels) total += value;
        return (double)total / pixels.Length;
    }
}
