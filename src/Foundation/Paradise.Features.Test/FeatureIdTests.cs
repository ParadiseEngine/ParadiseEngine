namespace Paradise.Features.Test;

/// <summary>Checks feature-name validation and case-insensitive identity.</summary>
public class FeatureIdTests
{
    [Test]
    public async Task a_dotted_name_parses_and_keeps_its_spelling()
    {
        var id = new FeatureId("rendering.globalIllumination");

        await Assert.That(id.Value).IsEqualTo("rendering.globalIllumination");
        await Assert.That(id.Subsystem.ToString()).IsEqualTo("rendering");
        await Assert.That(id.ToString()).IsEqualTo("rendering.globalIllumination");
    }

    [Test]
    [Arguments("")]
    [Arguments("rendering.")]
    [Arguments(".bloom")]
    [Arguments("rendering..bloom")]
    [Arguments("rendering bloom")]
    [Arguments("rendering/bloom")]
    public async Task a_malformed_name_is_refused_by_both_ways_in(string name)
    {
        await Assert.That(() => new FeatureId(name)).Throws<ArgumentException>();
        await Assert.That(FeatureId.TryParse(name, out _)).IsFalse();
    }

    [Test]
    public async Task two_spellings_of_one_name_are_the_same_id()
    {
        var upper = new FeatureId("Rendering.Bloom");
        var lower = new FeatureId("rendering.bloom");

        await Assert.That(upper).IsEqualTo(lower);
        await Assert.That(upper == lower).IsTrue();
        await Assert.That(upper != lower).IsFalse();
        await Assert.That(upper.Equals((object)lower)).IsTrue();
        await Assert.That(upper.GetHashCode()).IsEqualTo(lower.GetHashCode());
        await Assert.That(upper.ToString()).IsEqualTo("Rendering.Bloom");
    }

    [Test]
    public async Task the_default_id_names_nothing()
    {
        await Assert.That(default(FeatureId).IsEmpty).IsTrue();
        await Assert.That(default(FeatureId).Value).IsEqualTo("");
        await Assert.That(() => new FeatureDefinition(default(FeatureId), true)).Throws<ArgumentException>();
    }
}
