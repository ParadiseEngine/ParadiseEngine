namespace Paradise.Editor.Core.Shell;

/// <summary>Subsequence matching with a score, for the command palette.</summary>
/// <remarks>
/// <para>
/// In Core rather than the UI layer because it is the part with behaviour worth arguing about, and
/// none of that behaviour is drawing. A palette that ranks badly is the difference between typing
/// three letters and reading a list.
/// </para>
/// <para>
/// The rule is a subsequence, not a substring: <c>rsl</c> finds "Reset layout", which is what
/// makes a palette faster than a menu. Ranking then has to earn its keep, because a subsequence
/// match alone puts far too much in the list — so a run of adjacent characters and a character
/// starting a word both score, and a match that started at the beginning scores again.
/// </para>
/// </remarks>
public static class FuzzyMatch
{
    private const int AdjacentBonus = 8;
    private const int WordStartBonus = 12;
    private const int LeadingBonus = 10;
    private const int GapPenalty = 1;

    /// <summary>Score <paramref name="candidate"/> against <paramref name="query"/>; higher is a
    /// better match. False when the query is not a subsequence of the candidate.</summary>
    /// <remarks>An empty query matches everything with a score of zero, so a palette that has just
    /// opened lists its operators in registration order rather than none of them.</remarks>
    public static bool TryScore(string? query, string candidate, out int score)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        score = 0;
        if (string.IsNullOrEmpty(query)) return true;

        var at = 0;
        var previousMatch = -1;
        foreach (var wanted in query)
        {
            if (char.IsWhiteSpace(wanted)) continue;

            var found = IndexOfIgnoringCase(candidate, wanted, at);
            if (found < 0)
            {
                score = 0;
                return false;
            }

            score += found == 0 ? LeadingBonus : 0;
            if (found == previousMatch + 1) score += AdjacentBonus;
            if (IsWordStart(candidate, found)) score += WordStartBonus;
            score -= Math.Min(found - at, 8) * GapPenalty;

            previousMatch = found;
            at = found + 1;
        }

        // A short candidate that matched everything is a better answer than a long one that
        // happened to contain the same letters spread out.
        score -= candidate.Length / 8;
        return true;
    }

    private static int IndexOfIgnoringCase(string candidate, char wanted, int from)
    {
        for (var i = from; i < candidate.Length; i++)
        {
            if (char.ToLowerInvariant(candidate[i]) == char.ToLowerInvariant(wanted)) return i;
        }
        return -1;
    }

    // A word starts after a separator, and also where a lower-case run turns upper — so "rl" ranks
    // "ResetLayout" as highly as "Reset layout", and an operator id spelled either way behaves the
    // same in the palette.
    private static bool IsWordStart(string candidate, int index)
    {
        if (index == 0) return true;
        var previous = candidate[index - 1];
        if (previous is ' ' or '.' or '_' or '-' or '/') return true;
        return char.IsLower(previous) && char.IsUpper(candidate[index]);
    }
}
