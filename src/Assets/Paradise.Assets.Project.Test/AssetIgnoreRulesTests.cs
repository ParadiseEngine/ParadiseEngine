namespace Paradise.Assets.Project.Test;

public class AssetIgnoreRulesTests
{
    private static readonly UPath s_assets = "/game/assets";

    [Test]
    [Arguments(".DS_Store", "/game/assets/models/.DS_Store", true)]
    [Arguments(".DS_Store", "/game/assets/models/DS_Store", false)]
    [Arguments("*.tmp", "/game/assets/models/crate.glb.tmp", true)]
    [Arguments("*.tmp", "/game/assets/models/crate.glb.TMP", false)]
    [Arguments("*~", "/game/assets/models/crate.glb~", true)]
    [Arguments(".#*", "/game/assets/models/.#crate.glb", true)]
    [Arguments("*.blend?", "/game/assets/props/crate.blend1", true)]
    [Arguments("*.blend?", "/game/assets/props/crate.blend", false)]
    [Arguments("*.blend??", "/game/assets/props/crate.blend32", true)]
    [Arguments("scratch/*", "/game/assets/scratch/a.prefab", true)]
    [Arguments("scratch/*", "/game/assets/scratch/deep/a.prefab", false)]
    [Arguments("scratch/**", "/game/assets/scratch/deep/a.prefab", true)]
    [Arguments("scratch/**", "/game/assets/models/scratch/a.prefab", false)]
    [Arguments("*/scratch/**", "/game/assets/models/scratch/a.prefab", true)]
    public async Task a_pattern_matches_the_name_or_with_a_slash_the_relative_path(string pattern, string path, bool expected)
    {
        var rules = AssetIgnoreRules.Parse([pattern]);

        await Assert.That(rules.Matches(s_assets, path)).IsEqualTo(expected);
    }

    [Test]
    public async Task no_rules_match_nothing()
    {
        await Assert.That(AssetIgnoreRules.None.Matches(s_assets, "/game/assets/.DS_Store")).IsFalse();
        await Assert.That(AssetIgnoreRules.None.Patterns).IsEmpty();
    }

    [Test]
    public async Task regex_metacharacters_in_a_pattern_are_literal()
    {
        var rules = AssetIgnoreRules.Parse(["a+b(c).txt"]);

        await Assert.That(rules.Matches(s_assets, "/game/assets/a+b(c).txt")).IsTrue();
        await Assert.That(rules.Matches(s_assets, "/game/assets/aab(c).txt")).IsFalse();
    }

    [Test]
    [Arguments("")]
    [Arguments("  ")]
    [Arguments("/rooted")]
    public async Task an_empty_or_rooted_pattern_is_refused(string pattern)
    {
        await Assert.That(() => AssetIgnoreRules.Parse([pattern])).Throws<ArgumentException>();
    }
}
