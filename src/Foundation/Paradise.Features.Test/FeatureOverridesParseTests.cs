namespace Paradise.Features.Test;

/// <summary>The syntax of a <c>--features</c> flag and of <c>PARADISE_FEATURES</c>.</summary>
public class FeatureOverridesParseTests
{
    [Test]
    public async Task bare_names_and_prefixes_set_each_feature_state()
    {
        var overrides = FeatureOverrides.Parse("rendering.bloom,-rendering.shadows,+rendering.gi,!rendering.ssr");

        await Assert.That(overrides.Count).IsEqualTo(4);
        await Assert.That(overrides.TryGet("rendering.bloom", out var bloom)).IsTrue();
        await Assert.That(bloom).IsTrue();
        await Assert.That(overrides.TryGet("rendering.shadows", out var shadows)).IsTrue();
        await Assert.That(shadows).IsFalse();
        await Assert.That(overrides.TryGet("rendering.gi", out var gi)).IsTrue();
        await Assert.That(gi).IsTrue();
        await Assert.That(overrides.TryGet("rendering.ssr", out var ssr)).IsTrue();
        await Assert.That(ssr).IsFalse();
    }

    [Test]
    public async Task commas_semicolons_and_spaces_all_separate()
    {
        var overrides = FeatureOverrides.Parse(" a.one; -a.two   a.three ");

        await Assert.That(overrides.Count).IsEqualTo(3);
        await Assert.That(overrides.TryGet("a.one", out var one)).IsTrue();
        await Assert.That(one).IsTrue();
        await Assert.That(overrides.TryGet("a.two", out var two)).IsTrue();
        await Assert.That(two).IsFalse();
        await Assert.That(overrides.TryGet("a.three", out var three)).IsTrue();
        await Assert.That(three).IsTrue();
    }

    [Test]
    [Arguments("true", true)]
    [Arguments("on", true)]
    [Arguments("yes", true)]
    [Arguments("1", true)]
    [Arguments("false", false)]
    [Arguments("off", false)]
    [Arguments("no", false)]
    [Arguments("0", false)]
    [Arguments("TRUE", true)]
    [Arguments("OFF", false)]
    public async Task explicit_boolean_spellings_set_the_requested_state(string value, bool expected)
    {
        var overrides = FeatureOverrides.Parse($"rendering.bloom={value}");

        await Assert.That(overrides.Count).IsEqualTo(1);
        await Assert.That(overrides.TryGet("rendering.bloom", out var enabled)).IsTrue();
        await Assert.That(enabled).IsEqualTo(expected);
    }

    [Test]
    public async Task a_value_that_is_not_a_boolean_is_refused()
    {
        await Assert.That(() => FeatureOverrides.Parse("a.one=maybe"))
            .Throws<FormatException>().WithMessageContaining("a.one");
    }

    [Test]
    public async Task an_empty_list_is_the_shared_empty_layer()
    {
        await Assert.That(FeatureOverrides.Parse(null)).IsSameReferenceAs(FeatureOverrides.None);
        await Assert.That(FeatureOverrides.Parse("   ")).IsSameReferenceAs(FeatureOverrides.None);
    }
}
