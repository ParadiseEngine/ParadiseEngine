using Paradise.Editor.Core.Shell;

namespace Paradise.Editor.Test;

/// <summary>The palette's ranking. Pure logic, so it is tested without a frame.</summary>
public class FuzzyMatchTests
{
    private static int Score(string query, string candidate)
    {
        FuzzyMatch.TryScore(query, candidate, out var score);
        return score;
    }

    // A subsequence, not a substring — this is the whole reason a palette beats a menu.
    [Test]
    [Arguments("rsl", "Reset layout")]
    [Arguments("cp", "Command palette")]
    [Arguments("undo", "Undo")]
    [Arguments("elr", "editor.layout.reset")]
    public async Task a_subsequence_matches(string query, string candidate) =>
        await Assert.That(FuzzyMatch.TryScore(query, candidate, out _)).IsTrue();

    [Test]
    [Arguments("zzz", "Reset layout")]
    [Arguments("tuoyal", "Reset layout")]  // right letters, wrong order
    public async Task anything_that_is_not_a_subsequence_does_not(string query, string candidate) =>
        await Assert.That(FuzzyMatch.TryScore(query, candidate, out _)).IsFalse();

    // An empty query lists everything, so a palette that has just opened shows its commands
    // rather than nothing.
    [Test]
    public async Task an_empty_query_matches_everything_at_zero()
    {
        await Assert.That(FuzzyMatch.TryScore("", "anything", out var score)).IsTrue();
        await Assert.That(score).IsEqualTo(0);
    }

    // Ranking has to earn its keep: a subsequence match alone puts far too much in the list.
    [Test]
    public async Task word_starts_outrank_letters_buried_mid_word() =>
        await Assert.That(Score("rl", "Reset Layout")).IsGreaterThan(Score("rl", "Personal"));

    [Test]
    public async Task adjacent_characters_outrank_scattered_ones() =>
        await Assert.That(Score("res", "Reset layout")).IsGreaterThan(Score("res", "Rasterise every scene"));

    [Test]
    public async Task a_shorter_candidate_wins_an_otherwise_equal_match() =>
        await Assert.That(Score("undo", "Undo")).IsGreaterThan(Score("undo", "Undo every change in the document"));

    // Operator ids are spelled with dots and the labels with spaces; a palette that ranked one
    // form and not the other would make documentation and the UI disagree.
    [Test]
    public async Task a_dotted_id_and_a_spaced_label_both_rank_their_word_starts()
    {
        await Assert.That(Score("lr", "editor.layout.reset")).IsGreaterThan(0);
        await Assert.That(Score("rl", "Reset layout")).IsGreaterThan(0);
    }
}
