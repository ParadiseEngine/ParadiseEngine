using System.Numerics;
using System.Runtime.InteropServices;

namespace Paradise.Rendering.Pbr.Test;

/// <summary>The shared joint palette grows with the skinned instances staged into it, rather than
/// skipping everything past a fixed 4,096 matrices.</summary>
public class JointPaletteGrowthTests
{
    [Test]
    public async Task staging_past_the_palette_grows_it_and_keeps_what_was_staged()
    {
        var backend = new ResourceTrackingRenderer();
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        await Assert.That(pbr.JointCapacityForTest).IsEqualTo(PbrRenderer.InitialSkinnedJoints);

        var staged = Matrix4x4.CreateTranslation(1, 2, 3);
        pbr.SetJointPalette(0, [staged]);
        pbr.SetJointPalette(PbrRenderer.InitialSkinnedJoints + 10, [Matrix4x4.CreateScale(2)]);

        await Assert.That(pbr.JointCapacityForTest).IsEqualTo(PbrRenderer.InitialSkinnedJoints * 2);
        var palette = backend.Buffers.Single(pair => pair.Value.Name == "PbrJointPalettes");
        await Assert.That(palette.Value.Size).IsEqualTo((ulong)PbrRenderer.InitialSkinnedJoints * 2 * 64);
        // The replacement starts from everything staged so far, and new slots are identity.
        var bytes = backend.BufferData[palette.Key];
        await Assert.That(MemoryMarshal.Read<Matrix4x4>(bytes)).IsEqualTo(staged);
        await Assert.That(MemoryMarshal.Read<Matrix4x4>(bytes.AsSpan(64 * (PbrRenderer.InitialSkinnedJoints + 1)))).IsEqualTo(Matrix4x4.Identity);

        // Past the binding limit is still an overflow: skipped, and the palette is unchanged.
        pbr.SetJointPalette(PbrRenderer.MaxSkinnedJoints, [Matrix4x4.Identity]);
        await Assert.That(pbr.JointCapacityForTest).IsEqualTo(PbrRenderer.InitialSkinnedJoints * 2);
    }

    [Test]
    public async Task frames_after_growth_bind_only_live_groups_over_live_buffers()
    {
        var backend = new ResourceTrackingRenderer();
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        var (vertices, indices) = Procedural.UnitCube();
        var material = pbr.Materials.AddDefaultMaterial(Vector4.One);
        var scene = new PbrScene();
        scene.MotionVectors = new PbrMotionVectors { Enabled = true };
        scene.Lights.Add(new PbrLight
        {
            Type = PbrLightType.Directional,
            Direction = Vector3.Normalize(new Vector3(0.4f, 1f, 0.5f)),
            Intensity = 3f,
            CastsShadows = true,
        });
        scene.Instances.Add(new PbrInstance { Mesh = new PbrMesh([pbr.UploadPrimitive(vertices, indices, material)]) });

        // A frame first, so every feature has built its joint group over the original palette.
        pbr.RenderFrame(scene);
        pbr.SetJointPalette(PbrRenderer.InitialSkinnedJoints * 3, [Matrix4x4.Identity]);
        pbr.RenderFrame(scene);

        var bound = backend.LastCommands!
            .Where(command => command.Kind == RenderCommandKind.SetBindGroup)
            .Select(command => command.SetBindGroup.Group)
            .Distinct()
            .ToArray();
        await Assert.That(bound).IsNotEmpty();
        foreach (var group in bound)
        {
            await Assert.That(backend.BindGroups.ContainsKey(group)).IsTrue();
            foreach (var entry in backend.BindGroups[group].Entries.ToArray())
            {
                if (entry.Kind == BindGroupEntryKind.Buffer)
                    await Assert.That(backend.Buffers.ContainsKey(entry.Buffer)).IsTrue();
            }
        }
    }
}
