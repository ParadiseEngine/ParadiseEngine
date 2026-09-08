using System.Numerics;
using System.Runtime.CompilerServices;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr.Test;

public class PostProcessingMathTests
{
    [Test]
    public async Task Exposure_adaptation_is_bounded_frame_rate_independent_and_rejects_invalid_time()
    {
        var once = PbrPostMath.AdaptExposureEv(-4f, 3f, 2f, 0.5f);
        var twice = PbrPostMath.AdaptExposureEv(PbrPostMath.AdaptExposureEv(-4f, 3f, 2f, 0.25f), 3f, 2f, 0.25f);
        await Assert.That(once).IsBetween(-4f, 3f);
        await Assert.That(twice).IsEqualTo(once).Within(1e-5f);
        await Assert.That(PbrPostMath.AdaptExposureEv(1f, 3f, 2f, float.NaN)).IsEqualTo(1f);
        await Assert.That(PbrPostMath.AdaptExposureEv(1f, 3f, 2f, -1f)).IsEqualTo(1f);
        await Assert.That(PbrPostMath.AdaptExposureEv(1f, 3f, 0f, 1f)).IsEqualTo(1f);
    }

    [Test]
    public async Task Thin_lens_focus_near_far_and_sensor_size_follow_signed_coc()
    {
        var settings = new PbrDepthOfField { FocusDistance = 5f, MaxRadiusPixels = 32f };
        var near = PbrPostMath.CircleOfConfusionPixels(2f, 1080, settings);
        var far = PbrPostMath.CircleOfConfusionPixels(10f, 1080, settings);
        await Assert.That(PbrPostMath.CircleOfConfusionPixels(5f, 1080, settings)).IsEqualTo(0f);
        await Assert.That(near).IsLessThan(0f);
        await Assert.That(far).IsGreaterThan(0f);
        await Assert.That(PbrPostMath.CircleOfConfusionPixels(10f, 1080, settings with { FNumber = 5.6f }))
            .IsEqualTo(far / 2f).Within(1e-6f);
        await Assert.That(PbrPostMath.CircleOfConfusionPixels(0.001f, 1080, settings)).IsEqualTo(-32f);
    }

    [Test]
    public async Task Flattened_lut_has_documented_axes_and_owns_its_input()
    {
        var lut = PbrColorLut.Identity(4);
        await Assert.That(lut.Colors[2 * 16 + 3 * 4 + 1]).IsEqualTo(new Vector3(1f / 3f, 2f / 3f, 1f));
        var colors = new Vector3[8];
        var copy = new PbrColorLut(2, colors);
        colors[0] = Vector3.One;
        await Assert.That(copy.Colors[0]).IsEqualTo(Vector3.Zero);
        await Assert.That(() => new PbrColorLut(1, new Vector3[1])).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new PbrColorLut(2, new Vector3[7])).Throws<ArgumentException>();
        colors[0] = new Vector3(float.NaN);
        await Assert.That(() => new PbrColorLut(2, colors)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Color_chain_refuses_stale_consumers_duplicate_publish_and_self_feedback()
    {
        var board = new FrameBlackboard();
        var original = new GraphTexture(1);
        var next = new GraphTexture(2);
        board.Publish(PbrResults.SceneColor, original);
        board.Advance(PbrResults.SceneColor, original, next);
        await Assert.That(board.GetOrDefault(PbrResults.SceneColor, default)).IsEqualTo(next);
        await Assert.That(() => board.Advance(PbrResults.SceneColor, original, new GraphTexture(3))).Throws<InvalidOperationException>();
        await Assert.That(() => board.Advance(PbrResults.SceneColor, next, next)).Throws<ArgumentException>();
        await Assert.That(() => board.Publish(PbrResults.SceneColor, original)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Post_shader_reflection_matches_owned_uniforms_and_only_one_bind_group()
    {
        var effects = ShaderPrograms.Load("Shaders.postEffects");
        await Assert.That(effects.Layout.Groups.Length).IsEqualTo(1);
        await Assert.That(effects.Layout.Groups[0].Entries.Length).IsEqualTo(6);
        await Assert.That(effects.Layout.Groups[0].Entries[3].Type).IsEqualTo(BindingResourceType.UnfilterableFloatTexture);
        await Assert.That(Unsafe.SizeOf<PostUniformsGpu>()).IsEqualTo(160);
        await Assert.That(effects.Layout.Groups[0].Entries[2].MinBufferSize).IsEqualTo(160UL);
        var exposure = ShaderPrograms.Load("Shaders.exposure");
        await Assert.That(exposure.Layout.Groups.Length).IsEqualTo(1);
        await Assert.That(exposure.Layout.Groups[0].Entries.Length).IsEqualTo(4);
        var presentation = ShaderPrograms.Load("Shaders.presentation");
        await Assert.That(presentation.Layout.Groups[0].Entries.Length).IsEqualTo(2);
    }
}
