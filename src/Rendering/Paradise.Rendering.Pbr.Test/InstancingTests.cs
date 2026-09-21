using System.Numerics;
using Paradise.Rendering.Graph;
using Paradise.Rendering.Pbr.Test.Baseline;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

public class InstancingTests
{
    private const uint Size = 96;

    private sealed class MutateAfterCaptureFeature : IRenderFeature
    {
        public Action? Mutation { get; set; }
        public FeatureDefinition Definition { get; } = new("test.mutateAfterCapture", true, "Tests the frame capture boundary.");
        public FrameRequirements Requires => FrameRequirements.None;
        public void Setup(in FrameContext frame) => Mutation?.Invoke();
        public void Resize(uint width, uint height) { }
        public void Dispose() { }
    }

    private static WebGpuRenderer? Backend()
    {
        try { return WebGpuRenderer.CreateHeadless(Size, Size); }
        catch (Exception error) when (error is AdapterUnavailableException or DllNotFoundException)
        {
            Skip.Test($"No WebGPU adapter: {error.Message}");
            return null;
        }
    }

    private static PbrScene Scene(PbrRenderer pbr, bool skinned, bool transparent = false)
    {
        var (vertices, indices) = Procedural.UnitCube();
        var material = transparent ? pbr.Materials.AddMaterial(new PbrMaterialDesc
        {
            Name = "glass",
            BaseColorFactor = new Vector4(0.6f, 0.8f, 0.4f, 0.4f),
            AlphaMode = PbrAlphaMode.Blend,
        })
            : pbr.Materials.AddDefaultMaterial(new Vector4(0.6f, 0.8f, 0.4f, 1f));
        PbrPrimitive primitive;
        if (skinned)
        {
            var joints = new float[vertices.Length / 12 * 8];
            for (var i = 0; i < vertices.Length / 12; i++) joints[i * 8 + 4] = 1f;
            primitive = pbr.UploadSkinnedPrimitive(vertices, joints, indices, material);
            pbr.SetJointPalette(0, [Matrix4x4.CreateTranslation(0f, 0.2f, 0f), Matrix4x4.CreateTranslation(0f, -0.2f, 0f)]);
        }
        else primitive = pbr.UploadPrimitive(vertices, indices, material);
        var mesh = new PbrMesh([primitive]);
        var eye = new Vector3(0f, 2f, 6f);
        var scene = new PbrScene
        {
            Camera = new PbrCamera
            {
                View = PbrMath.LookAt(eye, Vector3.Zero, Vector3.UnitY),
                Position = eye,
                Projection = PbrMath.Perspective(1f, 1f, 0.1f, 30f),
            },
            ClearColor = new ColorRgba(0, 0, 0, 1),
            Ambient = new PbrAmbient { Sky = new Vector3(0.5f), Flat = true },
            Ssao = new PbrSsao { Enabled = !transparent },
        };
        scene.Lights.Add(new PbrLight { Type = PbrLightType.Directional, Direction = Vector3.UnitY, CastsShadows = true });
        for (var i = 0; i < 6; i++)
            scene.Instances.Add(new PbrInstance
            {
                Mesh = mesh,
                Model = Matrix4x4.CreateScale(0.7f, 0.9f, 0.5f) * Matrix4x4.CreateTranslation((i % 3 - 1) * 1.3f, 0f, i / 3 * -1.3f),
                Highlight = i * 0.15f,
                GiMode = i % 2 == 0 ? PbrGiMode.Disabled : PbrGiMode.Dynamic,
                JointOffset = skinned ? i % 2 : -1,
            });
        return scene;
    }

    private static PbrScene SceneWithMaterialRuns(PbrRenderer pbr, bool skinned)
    {
        var scene = Scene(pbr, skinned);
        var primitive = scene.Instances[0].Mesh.Primitives[0];
        var red = pbr.Materials.AddDefaultMaterial(new Vector4(0.8f, 0.1f, 0.2f, 1));
        var alternate = new PbrMesh([primitive with { MaterialId = red }]);
        for (var i = 2; i < 4; i++)
        {
            var source = scene.Instances[i];
            scene.Instances[i] = new PbrInstance
            {
                Mesh = alternate,
                Model = source.Model,
                JointOffset = source.JointOffset,
                Highlight = source.Highlight,
                GiMode = source.GiMode,
            };
        }
        return scene;
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task packed_regrouping_is_optional_and_can_toggle_without_stale_instance_data(bool skinned, bool bindPose)
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr, skinned);
        var primitive = scene.Instances[0].Mesh.Primitives[0];
        var red = pbr.Materials.AddDefaultMaterial(new Vector4(0.8f, 0.1f, 0.2f, 1));
        var alternate = new PbrMesh([primitive with { MaterialId = red }]);
        for (var i = 0; i < scene.Instances.Count; i++)
        {
            var source = scene.Instances[i];
            scene.Instances[i] = new PbrInstance
            {
                Mesh = i % 2 == 0 ? source.Mesh : alternate,
                Model = source.Model,
                JointOffset = bindPose ? -1 : source.JointOffset,
                Highlight = source.Highlight,
                GiMode = source.GiMode,
            };
        }
        if (bindPose) pbr.SetJointPalette(0, [Matrix4x4.CreateTranslation(100, 0, 0)]);
        await Assert.That(scene.Instancing.PackAndRegroup).IsFalse();
        pbr.RenderFrame(scene);
        var initial = backend.ReadbackColor(out _, out _).ToArray();
        await Assert.That(pbr.PackedDrawCapacityForTest).IsEqualTo(0);
        await Assert.That(pbr.FramePackingEnabledForTest).IsFalse();
        await Assert.That(pbr.Pipeline.Find<InstancingFeature>()!.DrawCalls).IsEqualTo(6);

        var last = initial;
        foreach (var packed in new[] { true, false, true })
        {
            scene.Instances[2].Model *= Matrix4x4.CreateTranslation(0.1f, 0.15f, 0);
            scene.Instances[3].Highlight += 0.15f;
            if (skinned && !bindPose)
                pbr.SetJointPalette(1, [Matrix4x4.CreateTranslation(0.1f, scene.Instances[3].Highlight * 0.2f, 0)]);
            scene.Instancing = new PbrInstancing { Enabled = false, PackAndRegroup = true };
            pbr.RenderFrame(scene);
            var expected = backend.ReadbackColor(out _, out _).ToArray();
            await Assert.That(pbr.FramePackingEnabledForTest).IsFalse();

            scene.Instancing = new PbrInstancing { PackAndRegroup = packed };
            pbr.RenderFrame(scene);
            last = backend.ReadbackColor(out _, out _).ToArray();
            AssertPixels(expected, last);
            await Assert.That(pbr.FramePackingEnabledForTest).IsEqualTo(packed);
            await Assert.That(pbr.Pipeline.Find<InstancingFeature>()!.DrawCalls).IsEqualTo(packed ? 2 : 6);
        }
        await Assert.That(pbr.PackedDrawCapacityForTest).IsGreaterThan(0);
        await Assert.That(initial.SequenceEqual(last)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task disabled_draw_preparation_uses_current_direct_data_and_preserves_consecutive_instancing(bool skinned)
    {
        using var backend = Backend();
        if (backend is null) return;
        var switches = new FeatureSwitches();
        switches.Set(PbrFeatures.DrawPreparation.Id, false);
        var recording = new RecordingRenderer(backend);
        using var pbr = new PbrRenderer(recording, switches, Size, Size);
        var scene = SceneWithMaterialRuns(pbr, skinned);
        scene.Instancing = new PbrInstancing { PackAndRegroup = true };
        var feature = pbr.Pipeline.Find<InstancingFeature>()!;
        byte[]? previous = null;

        foreach (var enabled in new[] { false, true, false, true })
        {
            switches.Set(PbrFeatures.DrawPreparation.Id, enabled);
            scene.Instances[2].Model *= Matrix4x4.CreateTranslation(0.1f, 0.15f, 0);
            scene.Instances[3].Highlight += 0.15f;
            if (skinned)
                pbr.SetJointPalette(1, [Matrix4x4.CreateTranslation(0.1f, scene.Instances[3].Highlight * 0.2f, 0)]);

            pbr.RenderFrame(scene);
            var actual = backend.ReadbackColor(out _, out _).ToArray();
            await Assert.That(pbr.FramePackingEnabledForTest).IsEqualTo(enabled);
            await Assert.That(feature.DrawCalls).IsEqualTo(enabled ? 2 : 3);
            await Assert.That(feature.BatchedInstances).IsEqualTo(6);
            await Assert.That(MaterialDraws(recording).All(draw => draw.Draw.InstanceCount >= 2)).IsTrue();
            if (previous is not null) await Assert.That(previous.SequenceEqual(actual)).IsFalse();

            scene.Instancing = new PbrInstancing { Enabled = false, PackAndRegroup = true };
            pbr.RenderFrame(scene);
            AssertPixels(actual, backend.ReadbackColor(out _, out _).ToArray());
            await Assert.That(feature.DrawCalls).IsEqualTo(6);
            scene.Instancing = new PbrInstancing { PackAndRegroup = true };
            previous = actual;
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task setup_changes_to_preparation_switch_or_instancing_settings_take_effect_next_frame(
        bool initiallyEnabled, bool changeSwitch)
    {
        using var backend = Backend();
        if (backend is null) return;
        var switches = new FeatureSwitches();
        switches.Set(PbrFeatures.DrawPreparation.Id, !changeSwitch || initiallyEnabled);
        using var pbr = new PbrRenderer(backend, switches, Size, Size);
        var scene = SceneWithMaterialRuns(pbr, skinned: false);
        scene.Instancing = new PbrInstancing
        {
            Enabled = changeSwitch || initiallyEnabled,
            PackAndRegroup = changeSwitch || initiallyEnabled,
        };
        var mutation = new MutateAfterCaptureFeature();
        pbr.Pipeline.Add(mutation, PbrFeatureOrder.DrawPreparation - 1);
        var feature = pbr.Pipeline.Find<InstancingFeature>()!;
        pbr.RenderFrame(scene);
        var expected = backend.ReadbackColor(out _, out _).ToArray();
        var directCalls = changeSwitch ? 3 : 6;
        await Assert.That(feature.DrawCalls).IsEqualTo(initiallyEnabled ? 2 : directCalls);

        mutation.Mutation = () =>
        {
            if (changeSwitch) switches.Set(PbrFeatures.DrawPreparation.Id, !initiallyEnabled);
            else scene.Instancing = new PbrInstancing
            {
                Enabled = !initiallyEnabled,
                PackAndRegroup = !initiallyEnabled,
            };
        };
        pbr.RenderFrame(scene);
        AssertPixels(expected, backend.ReadbackColor(out _, out _).ToArray());
        await Assert.That(pbr.FramePackingEnabledForTest).IsEqualTo(initiallyEnabled);
        await Assert.That(feature.DrawCalls).IsEqualTo(initiallyEnabled ? 2 : directCalls);

        mutation.Mutation = null;
        pbr.RenderFrame(scene);
        AssertPixels(expected, backend.ReadbackColor(out _, out _).ToArray());
        await Assert.That(pbr.FramePackingEnabledForTest).IsEqualTo(!initiallyEnabled);
        await Assert.That(feature.DrawCalls).IsEqualTo(initiallyEnabled ? directCalls : 2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task simple_and_packed_paths_use_captured_transforms_flags_and_palette_offsets(bool packed)
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr, skinned: true);
        scene.Instancing = new PbrInstancing { PackAndRegroup = packed };
        // Different effective vertex paths must retain their own palette rules in one frame.
        scene.Instances[0].JointOffset = -1;
        var mutation = new MutateAfterCaptureFeature();
        pbr.Pipeline.Add(mutation, PbrFeatureOrder.DrawPreparation - 1);
        pbr.RenderFrame(scene);
        var expected = backend.ReadbackColor(out _, out _).ToArray();

        mutation.Mutation = () =>
        {
            foreach (var instance in scene.Instances)
            {
                instance.Model = Matrix4x4.CreateTranslation(100, 0, 0);
                instance.JointOffset = instance.JointOffset < 0 ? 1 : -1;
                instance.Highlight = 1f;
                instance.GiMode = PbrGiMode.Disabled;
                instance.ReceivesDecals = false;
            }
        };
        pbr.RenderFrame(scene);
        AssertPixels(expected, backend.ReadbackColor(out _, out _).ToArray());
        await Assert.That(pbr.FramePackingEnabledForTest).IsEqualTo(packed);

        mutation.Mutation = null;
        pbr.RenderFrame(scene);
        await Assert.That(expected.AsSpan().SequenceEqual(backend.ReadbackColor(out _, out _))).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task batching_preserves_pixels_and_reduces_actual_draw_calls(bool skinned, bool transparent)
    {
        using var backend = Backend();
        if (backend is null) return;
        var recording = new RecordingRenderer(backend);
        var switches = new FeatureSwitches();
        using var pbr = new PbrRenderer(recording, switches, Size, Size);
        var scene = Scene(pbr, skinned, transparent);
        var feature = pbr.Pipeline.Find<InstancingFeature>()!;
        pbr.RenderFrame(scene);
        var batched = backend.ReadbackColor(out _, out _).ToArray();
        var calls = recording.LastPresentedFrame.Commands.Count(c => c.Kind == RenderCommandKind.DrawIndexed);
        var depthSavings = pbr.Pipeline.Find<PrepassFeature>()!.SavedDrawCalls
            + pbr.Pipeline.Find<ShadowFeature>()!.SavedDrawCalls;
        await Assert.That(feature.DrawCalls).IsEqualTo(1);
        await Assert.That(feature.BatchedInstances).IsEqualTo(6);
        await Assert.That(feature.SavedDrawCalls).IsEqualTo(5);
        await Assert.That(recording.LastPresentedFrame.Commands.Any(c => c.Kind == RenderCommandKind.DrawIndexed && c.DrawIndexed.InstanceCount == 6)).IsTrue();

        switches.Set(PbrFeatures.Instancing.Id, false);
        pbr.RenderFrame(scene);
        var unbatched = backend.ReadbackColor(out _, out _).ToArray();
        var plainCalls = recording.LastPresentedFrame.Commands.Count(c => c.Kind == RenderCommandKind.DrawIndexed);
        await Assert.That(feature.DrawCalls).IsEqualTo(6);
        await Assert.That(plainCalls - calls).IsEqualTo(5 + depthSavings);
        AssertPixels(batched, unbatched);
        await Assert.That(batched.Where((_, i) => i % 4 != 3).Count(v => v > 20)).IsGreaterThan(100);

        switches.Set(PbrFeatures.Instancing.Id, true);
        scene.Instances[2].Model *= Matrix4x4.CreateTranslation(0.2f, 0.3f, 0f);
        pbr.RenderFrame(scene);
        var moved = backend.ReadbackColor(out _, out _).ToArray();
        scene.Instancing = new PbrInstancing { Enabled = false };
        pbr.RenderFrame(scene);
        AssertPixels(moved, backend.ReadbackColor(out _, out _).ToArray());
        await Assert.That(feature.DrawCalls).IsEqualTo(6);
    }

    [Test]
    public async Task separate_material_and_geometry_runs_preserve_nonzero_first_instance()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr, false);
        var (vertices, indices) = Procedural.UnitCube();
        var red = pbr.Materials.AddDefaultMaterial(new Vector4(0.9f, 0.1f, 0.1f, 1));
        scene.Instances.Insert(0, new PbrInstance
        {
            Mesh = new PbrMesh([pbr.UploadPrimitive(vertices, indices, red)]),
            Model = Matrix4x4.CreateScale(0.3f) * Matrix4x4.CreateTranslation(0f, 1f, 0f),
        });
        pbr.RenderFrame(scene);
        var batched = backend.ReadbackColor(out _, out _).ToArray();
        await Assert.That(pbr.Pipeline.Find<InstancingFeature>()!.DrawCalls).IsEqualTo(2);
        scene.Instancing = new PbrInstancing { Enabled = false };
        pbr.RenderFrame(scene);
        AssertPixels(batched, backend.ReadbackColor(out _, out _).ToArray());
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task interleaved_opaque_materials_preserve_pixels_and_program_boundaries(bool custom, bool masked)
    {
        using var backend = Backend();
        if (backend is null) return;
        var recording = new RecordingRenderer(backend);
        using var pbr = new PbrRenderer(recording, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr, false);
        scene.Instancing = new PbrInstancing { PackAndRegroup = true };
        var program = custom ? pbr.RegisterMaterialProgram(ShaderProgramLoader.Load(
            typeof(InstancingTests).Assembly, "Shaders.surfaceFixture")) : 0;
        var material = pbr.Materials.AddMaterial(new PbrMaterialDesc
        {
            Name = "alternate",
            BaseColorFactor = new Vector4(0.8f, 0.1f, 0.2f, 1f),
            AlphaMode = masked ? PbrAlphaMode.Mask : PbrAlphaMode.Opaque,
            ProcColorA = new Vector3(0f, 0f, 0.8f),
        }, default, program);
        var original = scene.Instances[0].Mesh.Primitives[0];
        var alternate = new PbrMesh([original with { MaterialId = material }]);
        for (var i = 1; i < scene.Instances.Count; i += 2)
        {
            var instance = scene.Instances[i];
            scene.Instances[i] = new PbrInstance
            {
                Mesh = alternate,
                Model = instance.Model,
                Highlight = instance.Highlight,
                GiMode = instance.GiMode,
            };
        }

        pbr.RenderFrame(scene);
        var batched = backend.ReadbackColor(out _, out _).ToArray();
        var feature = pbr.Pipeline.Find<InstancingFeature>()!;
        if (custom || masked)
        {
            var barrierDraws = DrawsWithMaterial(recording, pbr.Materials.GetBindGroup(material)).ToArray();
            await Assert.That(barrierDraws.Length).IsEqualTo(3);
            await Assert.That(barrierDraws.All(draw => draw.InstanceCount == 1)).IsTrue();
            await Assert.That(feature.DrawCalls).IsEqualTo(6);
            await Assert.That(feature.BatchedInstances).IsEqualTo(0);
        }
        else
        {
            await Assert.That(feature.DrawCalls).IsEqualTo(2);
            await Assert.That(feature.BatchedInstances).IsEqualTo(6);
            await Assert.That(feature.SavedDrawCalls).IsEqualTo(4);
            await Assert.That(DrawsWithMaterial(recording, pbr.Materials.GetBindGroup(material))
                .Single().InstanceCount).IsEqualTo(3u);
        }

        scene.Instancing = new PbrInstancing { Enabled = false };
        pbr.RenderFrame(scene);
        await Assert.That(feature.DrawCalls).IsEqualTo(6);
        AssertPixels(batched, backend.ReadbackColor(out _, out _).ToArray());
    }

    [Test]
    public async Task shared_buffers_keep_distinct_index_ranges_and_batch_equal_descriptor_copies()
    {
        using var backend = Backend();
        if (backend is null) return;
        var recording = new RecordingRenderer(backend);
        using var pbr = new PbrRenderer(recording, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr, false);
        scene.Instancing = new PbrInstancing { PackAndRegroup = true };
        var complete = scene.Instances[0].Mesh.Primitives[0];
        var partial = complete with { IndexCount = complete.IndexCount / 2 };
        for (var i = 0; i < scene.Instances.Count; i++)
        {
            var instance = scene.Instances[i];
            var descriptor = i % 2 == 0 ? complete : partial;
            // Equal copies must batch; different index ranges over shared buffers must not.
            scene.Instances[i] = new PbrInstance
            {
                Mesh = new PbrMesh([descriptor with { }]),
                Model = instance.Model,
                Highlight = instance.Highlight,
                GiMode = instance.GiMode,
            };
        }

        pbr.RenderFrame(scene);
        var batched = backend.ReadbackColor(out _, out _).ToArray();
        var feature = pbr.Pipeline.Find<InstancingFeature>()!;
        var draws = DrawsWithMaterial(recording, pbr.Materials.GetBindGroup(complete.MaterialId)).ToArray();
        await Assert.That(feature.DrawCalls).IsEqualTo(2);
        await Assert.That(feature.BatchedInstances).IsEqualTo(6);
        await Assert.That(feature.SavedDrawCalls).IsEqualTo(4);
        await Assert.That(draws.Length).IsEqualTo(2);
        await Assert.That(draws.Single(draw => draw.IndexCount == complete.IndexCount).InstanceCount).IsEqualTo(3u);
        await Assert.That(draws.Single(draw => draw.IndexCount == partial.IndexCount).InstanceCount).IsEqualTo(3u);

        scene.Instancing = new PbrInstancing { Enabled = false };
        pbr.RenderFrame(scene);
        await Assert.That(feature.DrawCalls).IsEqualTo(6);
        AssertPixels(batched, backend.ReadbackColor(out _, out _).ToArray());
    }

    [Test]
    public async Task repeated_multi_primitive_meshes_preserve_nonuniform_transforms_when_batched()
    {
        using var backend = Backend();
        if (backend is null) return;
        var recording = new RecordingRenderer(backend);
        using var pbr = new PbrRenderer(recording, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr, false);
        var green = scene.Instances[0].Mesh.Primitives[0].MaterialId;
        var red = pbr.Materials.AddDefaultMaterial(new Vector4(0.9f, 0.1f, 0.1f, 1));
        var (vertices, indices) = Procedural.UnitCube();
        // Each material owns different cube faces, so draw reordering never relies on depth ties.
        var mesh = new PbrMesh([
            pbr.UploadPrimitive(vertices, indices.AsSpan(0, indices.Length / 2), green),
            pbr.UploadPrimitive(vertices, indices.AsSpan(indices.Length / 2), red),
        ]);
        for (var i = 0; i < scene.Instances.Count; i++)
        {
            var instance = scene.Instances[i];
            scene.Instances[i] = new PbrInstance
            {
                Mesh = mesh,
                Model = Matrix4x4.CreateScale(0.55f + i * 0.03f, 0.7f + i * 0.07f, 0.35f + i * 0.02f)
                    * Matrix4x4.CreateRotationY(i * 0.23f)
                    * Matrix4x4.CreateTranslation(instance.Model.Translation),
                Highlight = instance.Highlight,
                GiMode = instance.GiMode,
            };
        }

        var feature = pbr.Pipeline.Find<InstancingFeature>()!;
        for (var frame = 0; frame < 2; frame++)
        {
            scene.Instancing = new PbrInstancing { Enabled = true, PackAndRegroup = true };
            pbr.RenderFrame(scene);
            var batched = backend.ReadbackColor(out _, out _).ToArray();
            var calls = recording.LastPresentedFrame.Commands.Count(command => command.Kind == RenderCommandKind.DrawIndexed);
            var depthSavings = pbr.Pipeline.Find<PrepassFeature>()!.SavedDrawCalls
                + pbr.Pipeline.Find<ShadowFeature>()!.SavedDrawCalls;
            await Assert.That(feature.DrawCalls).IsEqualTo(2);
            await Assert.That(feature.BatchedInstances).IsEqualTo(12);
            await Assert.That(feature.SavedDrawCalls).IsEqualTo(10);

            scene.Instancing = new PbrInstancing { Enabled = false };
            pbr.RenderFrame(scene);
            await Assert.That(feature.DrawCalls).IsEqualTo(12);
            await Assert.That(recording.LastPresentedFrame.Commands.Count(command => command.Kind == RenderCommandKind.DrawIndexed) - calls)
                .IsEqualTo(10 + depthSavings);
            AssertPixels(batched, backend.ReadbackColor(out _, out _).ToArray());
            scene.Instances[2].Model *= Matrix4x4.CreateTranslation(0.1f, 0.2f, 0f);
        }
    }

    [Test]
    public async Task mixed_rigid_and_skinned_primitives_use_the_same_instance_with_distinct_palette_rules()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr, false);
        scene.Instancing = new PbrInstancing { PackAndRegroup = true };
        var rigid = scene.Instances[0].Mesh.Primitives[0];
        var (vertices, indices) = Procedural.UnitCube();
        var weights = new float[vertices.Length / 12 * 8];
        for (var i = 0; i < vertices.Length / 12; i++) weights[i * 8 + 4] = 1f;
        var red = pbr.Materials.AddDefaultMaterial(new Vector4(0.9f, 0.1f, 0.1f, 1));
        var skinned = pbr.UploadSkinnedPrimitive(vertices, weights, indices, red);
        var mesh = new PbrMesh([rigid, skinned]);
        // A mistaken zero palette base moves the skinned part out of view rather than hiding in bind pose.
        pbr.SetJointPalette(0, [Matrix4x4.CreateTranslation(50, 0, 0), Matrix4x4.Identity,
            Matrix4x4.Identity, Matrix4x4.CreateTranslation(0.75f, 0, 0)]);
        for (var i = 0; i < scene.Instances.Count; i++)
        {
            var instance = scene.Instances[i];
            scene.Instances[i] = new PbrInstance
            {
                Mesh = mesh,
                Model = instance.Model,
                Highlight = instance.Highlight,
                GiMode = instance.GiMode,
                JointOffset = 3,
            };
        }

        pbr.RenderFrame(scene);
        var mixed = backend.ReadbackColor(out _, out _).ToArray();
        await Assert.That(pbr.Pipeline.Find<InstancingFeature>()!.DrawCalls).IsEqualTo(2);

        var instances = scene.Instances.ToArray();
        scene.Instances.Clear();
        foreach (var instance in instances)
        {
            scene.Instances.Add(new PbrInstance
            {
                Mesh = new PbrMesh([rigid]),
                Model = instance.Model,
                Highlight = instance.Highlight,
                GiMode = instance.GiMode,
            });
            scene.Instances.Add(new PbrInstance
            {
                Mesh = new PbrMesh([skinned]),
                Model = instance.Model,
                Highlight = instance.Highlight,
                GiMode = instance.GiMode,
                JointOffset = 3,
            });
        }
        scene.Instancing = new PbrInstancing { Enabled = false };
        pbr.RenderFrame(scene);
        await Assert.That(pbr.Pipeline.Find<InstancingFeature>()!.DrawCalls).IsEqualTo(12);
        AssertPixels(mixed, backend.ReadbackColor(out _, out _).ToArray());
    }

    [Test]
    public async Task separated_transparent_materials_keep_back_to_front_order()
    {
        using var backend = Backend();
        if (backend is null) return;
        var recording = new RecordingRenderer(backend);
        using var pbr = new PbrRenderer(recording, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr, false, transparent: true);
        scene.Instancing = new PbrInstancing { PackAndRegroup = true };
        var first = scene.Instances[0].Mesh;
        var red = pbr.Materials.AddMaterial(new PbrMaterialDesc
        {
            Name = "red glass",
            BaseColorFactor = new Vector4(0.9f, 0.1f, 0.1f, 0.5f),
            AlphaMode = PbrAlphaMode.Blend,
        });
        var middle = new PbrMesh([first.Primitives[0] with { MaterialId = red }]);
        var eye = new Vector3(0, 0, 6);
        scene.Camera = new PbrCamera
        {
            View = PbrMath.LookAt(eye, Vector3.Zero, Vector3.UnitY),
            Position = eye,
            Projection = PbrMath.Perspective(1f, 1f, 0.1f, 30f),
        };
        scene.Instances.Clear();
        // Submitted A, A, B; correct depth order is A, B, A, which cannot become one A batch.
        scene.Instances.Add(new PbrInstance { Mesh = first, Model = Matrix4x4.CreateScale(1f, 1f, 0.1f) * Matrix4x4.CreateTranslation(0, 0, 1) });
        scene.Instances.Add(new PbrInstance { Mesh = first, Model = Matrix4x4.CreateScale(1f, 1f, 0.1f) * Matrix4x4.CreateTranslation(0, 0, -1) });
        scene.Instances.Add(new PbrInstance { Mesh = middle, Model = Matrix4x4.CreateScale(1f, 1f, 0.1f) });

        pbr.RenderFrame(scene);
        var batched = backend.ReadbackColor(out _, out _).ToArray();
        var feature = pbr.Pipeline.Find<InstancingFeature>()!;
        await Assert.That(feature.DrawCalls).IsEqualTo(3);
        await Assert.That(feature.BatchedInstances).IsEqualTo(0);
        var orderedMaterials = MaterialDraws(recording).Select(draw => draw.Material).ToArray();
        await Assert.That(orderedMaterials.SequenceEqual(new[]
        {
            pbr.Materials.GetBindGroup(first.Primitives[0].MaterialId),
            pbr.Materials.GetBindGroup(red),
            pbr.Materials.GetBindGroup(first.Primitives[0].MaterialId),
        })).IsTrue();

        scene.Instancing = new PbrInstancing { Enabled = false };
        pbr.RenderFrame(scene);
        AssertPixels(batched, backend.ReadbackColor(out _, out _).ToArray());
    }

    [Test]
    public async Task opaque_reordering_is_opt_in_for_coplanar_materials()
    {
        using var backend = Backend();
        if (backend is null) return;
        var recording = new RecordingRenderer(backend);
        var switches = new FeatureSwitches();
        using var pbr = new PbrRenderer(recording, switches, Size, Size);
        var scene = Scene(pbr, false);
        scene.Ssao = new PbrSsao { Enabled = false };
        scene.Lights.Clear();
        var first = scene.Instances[0].Mesh;
        var red = pbr.Materials.AddDefaultMaterial(new Vector4(0.9f, 0.1f, 0.1f, 1));
        var middle = new PbrMesh([first.Primitives[0] with { MaterialId = red }]);
        var eye = new Vector3(0, 0, 6);
        scene.Camera = new PbrCamera
        {
            View = PbrMath.LookAt(eye, Vector3.Zero, Vector3.UnitY),
            Position = eye,
            Projection = PbrMath.Perspective(1f, 1f, 0.1f, 30f),
        };
        scene.Instances.Clear();
        scene.Instances.Add(new PbrInstance { Mesh = first, Model = Matrix4x4.CreateScale(1f, 1f, 0.1f) * Matrix4x4.CreateTranslation(-0.75f, 0, 0) });
        scene.Instances.Add(new PbrInstance { Mesh = middle, Model = Matrix4x4.CreateScale(2f, 1f, 0.1f) });
        scene.Instances.Add(new PbrInstance { Mesh = first, Model = Matrix4x4.CreateScale(1f, 1f, 0.1f) * Matrix4x4.CreateTranslation(0.75f, 0, 0) });

        pbr.RenderFrame(scene);
        var original = backend.ReadbackColor(out _, out _).ToArray();
        var feature = pbr.Pipeline.Find<InstancingFeature>()!;
        await Assert.That(feature.DrawCalls).IsEqualTo(3);
        await Assert.That(feature.BatchedInstances).IsEqualTo(0);
        await Assert.That(MaterialDraws(recording).Select(draw => draw.Material).SequenceEqual(new[]
        {
            pbr.Materials.GetBindGroup(first.Primitives[0].MaterialId),
            pbr.Materials.GetBindGroup(red),
            pbr.Materials.GetBindGroup(first.Primitives[0].MaterialId),
        })).IsTrue();

        scene.Instancing = new PbrInstancing { Enabled = false, PackAndRegroup = true };
        pbr.RenderFrame(scene);
        AssertPixels(original, backend.ReadbackColor(out _, out _).ToArray());
        await Assert.That(feature.DrawCalls).IsEqualTo(3);
        await Assert.That(feature.BatchedInstances).IsEqualTo(0);

        // Grouping the green draws changes which material wins equal-depth pixels on the right.
        scene.Instancing = new PbrInstancing { PackAndRegroup = true };
        pbr.RenderFrame(scene);
        await Assert.That(feature.DrawCalls).IsEqualTo(2);
        await Assert.That(original.AsSpan().SequenceEqual(backend.ReadbackColor(out _, out _))).IsFalse();

        switches.Set(PbrFeatures.Instancing.Id, false);
        pbr.RenderFrame(scene);
        AssertPixels(original, backend.ReadbackColor(out _, out _).ToArray());
        await Assert.That(feature.DrawCalls).IsEqualTo(3);
        await Assert.That(feature.BatchedInstances).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task visibility_preserves_original_slots_and_splits_batches(bool occlusion)
    {
        using var backend = Backend();
        if (backend is null) return;
        var recording = new RecordingRenderer(backend);
        using var pbr = new PbrRenderer(recording, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr, false);
        scene.Instances[2].Model = Matrix4x4.CreateTranslation(100, 0, 0);
        scene.Visibility.OcclusionEnabled = occlusion;
        pbr.RenderFrame(scene);
        var pixels = backend.ReadbackColor(out _, out _).ToArray();
        var feature = pbr.Pipeline.Find<InstancingFeature>()!;
        await Assert.That(pbr.Pipeline.Find<FrustumCullingFeature>()!.CulledDrawCount).IsEqualTo(1);
        await Assert.That(feature.DrawCalls).IsEqualTo(occlusion ? 5 : 2);
        await Assert.That(feature.SavedDrawCalls).IsEqualTo(occlusion ? 0 : 3);
        await Assert.That(recording.LastPresentedFrame.Commands.Count(c => c.Kind == RenderCommandKind.DrawIndexedIndirect))
            .IsEqualTo(occlusion ? 5 : 0);
        scene.Instancing = new PbrInstancing { Enabled = false };
        scene.Visibility.FrustumEnabled = false;
        scene.Visibility.OcclusionEnabled = false;
        pbr.RenderFrame(scene);
        AssertPixels(pixels, backend.ReadbackColor(out _, out _));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task regrouped_opaque_draws_keep_culled_gaps_aligned_across_passes(bool occlusion)
    {
        using var backend = Backend();
        if (backend is null) return;
        var recording = new RecordingRenderer(backend);
        using var pbr = new PbrRenderer(recording, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr, false);
        scene.Instancing = new PbrInstancing { PackAndRegroup = true };
        scene.Visibility.OcclusionEnabled = occlusion;
        var red = pbr.Materials.AddDefaultMaterial(new Vector4(0.9f, 0.1f, 0.1f, 1));
        var alternate = new PbrMesh([scene.Instances[0].Mesh.Primitives[0] with { MaterialId = red }]);
        for (var i = 1; i < scene.Instances.Count; i += 2)
        {
            var instance = scene.Instances[i];
            scene.Instances[i] = new PbrInstance
            {
                Mesh = alternate,
                Model = instance.Model,
                Highlight = instance.Highlight,
                GiMode = instance.GiMode,
            };
        }
        // Grouping produces A-visible, A-culled, A-visible followed by the three B draws.
        scene.Instances[2].Model = Matrix4x4.CreateTranslation(100, 0, 0);
        pbr.RenderFrame(scene);
        var grouped = backend.ReadbackColor(out _, out _).ToArray();
        var feature = pbr.Pipeline.Find<InstancingFeature>()!;
        await Assert.That(pbr.Pipeline.Find<FrustumCullingFeature>()!.CulledDrawCount).IsEqualTo(1);
        await Assert.That(feature.DrawCalls).IsEqualTo(occlusion ? 5 : 3);
        await Assert.That(feature.SavedDrawCalls).IsEqualTo(occlusion ? 0 : 2);
        await Assert.That(recording.LastPresentedFrame.Commands.Count(command => command.Kind == RenderCommandKind.DrawIndexedIndirect))
            .IsEqualTo(occlusion ? 5 : 0);

        scene.Instancing = new PbrInstancing { Enabled = false };
        scene.Visibility.FrustumEnabled = false;
        scene.Visibility.OcclusionEnabled = false;
        pbr.RenderFrame(scene);
        AssertPixels(grouped, backend.ReadbackColor(out _, out _).ToArray());
    }

    [Test]
    public async Task disabling_scene_clears_draw_statistics_when_instancing_is_already_off()
    {
        using var backend = Backend();
        if (backend is null) return;
        var switches = new FeatureSwitches();
        switches.Set(PbrFeatures.Instancing.Id, false);
        using var pbr = new PbrRenderer(backend, switches, Size, Size);
        var scene = Scene(pbr, false);
        var feature = pbr.Pipeline.Find<InstancingFeature>()!;

        pbr.RenderFrame(scene);
        await Assert.That(feature.DrawCalls).IsEqualTo(6);

        switches.Set(PbrFeatures.Scene.Id, false);
        pbr.RenderFrame(scene);
        await Assert.That(feature.DrawCalls).IsEqualTo(0);
        await Assert.That(feature.BatchedInstances).IsEqualTo(0);
        await Assert.That(feature.SavedDrawCalls).IsEqualTo(0);
    }

    [Test]
    public async Task skinned_stream_without_palette_draws_bind_pose_in_both_instance_paths()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Scene(pbr, false);
        scene.Ssao = new PbrSsao { Enabled = false };
        scene.Lights.Clear();
        pbr.RenderFrame(scene);
        var rigid = backend.ReadbackColor(out _, out _).ToArray();
        var (vertices, indices) = Procedural.UnitCube();
        var weights = new float[vertices.Length / 12 * 8];
        for (var i = 0; i < vertices.Length / 12; i++) weights[i * 8 + 4] = 1;
        var material = scene.Instances[0].Mesh.Primitives[0].MaterialId;
        var mesh = new PbrMesh([pbr.UploadSkinnedPrimitive(vertices, weights, indices, material)]);
        pbr.SetJointPalette(0, [Matrix4x4.CreateTranslation(100, 0, 0)]);
        for (var i = 0; i < scene.Instances.Count; i++)
        {
            var previous = scene.Instances[i];
            scene.Instances[i] = new PbrInstance
            {
                Mesh = mesh,
                Model = previous.Model,
                Highlight = previous.Highlight,
                GiMode = previous.GiMode,
                JointOffset = -1,
            };
        }
        pbr.RenderFrame(scene);
        AssertPixels(rigid, backend.ReadbackColor(out _, out _).ToArray());
        await Assert.That(pbr.Pipeline.Find<InstancingFeature>()!.DrawCalls).IsEqualTo(1);
        scene.Instancing = new PbrInstancing { Enabled = false };
        pbr.RenderFrame(scene);
        AssertPixels(rigid, backend.ReadbackColor(out _, out _).ToArray());
        await Assert.That(pbr.Pipeline.Find<InstancingFeature>()!.DrawCalls).IsEqualTo(6);
    }

    private static IEnumerable<DrawIndexedCommand> DrawsWithMaterial(RecordingRenderer recording, BindGroupHandle material) =>
        MaterialDraws(recording).Where(draw => draw.Material == material).Select(draw => draw.Draw);

    private static IEnumerable<(BindGroupHandle Material, DrawIndexedCommand Draw)> MaterialDraws(RecordingRenderer recording)
    {
        var material = default(BindGroupHandle);
        foreach (var command in recording.LastPresentedFrame.Commands)
        {
            if (command.Kind == RenderCommandKind.BeginPass) material = default;
            else if (command.Kind == RenderCommandKind.SetBindGroup && command.SetBindGroup.GroupIndex == 2)
                material = command.SetBindGroup.Group;
            else if (command.Kind == RenderCommandKind.DrawIndexed && material.IsValid)
                yield return (material, command.DrawIndexed);
        }
    }

    private static void AssertPixels(byte[] expected, byte[] actual)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            var differences = expected.Zip(actual).Count(pair => pair.First != pair.Second);
            throw new InvalidOperationException($"Instanced image differs in {differences} bytes.");
        }
    }
}
