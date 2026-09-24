using System.Text.RegularExpressions;

using Zio;

namespace Paradise.Assets.Project;

/// <summary>The project's <c>[assets] ignore</c> patterns for files beneath <c>assets/</c>.</summary>
/// <remarks>
/// Patterns without <c>/</c> match filenames; others match assets-relative paths.
/// <c>*</c> and <c>?</c> stay within a segment; <c>**</c> crosses segments.
/// Matching is ordinal and case-sensitive; the engine adds no implicit ignore rules.
/// </remarks>
public sealed class AssetIgnoreRules
{
    private readonly List<(string Pattern, Regex Name, bool AgainstPath)> _rules = [];

    private AssetIgnoreRules(IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            _rules.Add((pattern, new Regex(ToRegex(pattern), RegexOptions.CultureInvariant), pattern.Contains('/')));
        }
    }

    /// <summary>Ignores nothing.</summary>
    public static AssetIgnoreRules None { get; } = new([]);

    public IReadOnlyList<string> Patterns => _rules.ConvertAll(rule => rule.Pattern);

    /// <exception cref="ArgumentException">A pattern is empty, or would match a sidecar or the manifest.</exception>
    public static AssetIgnoreRules Parse(IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        var list = patterns.ToList();
        foreach (var pattern in list)
        {
            if (string.IsNullOrWhiteSpace(pattern)) throw new ArgumentException("an ignore pattern is empty", nameof(patterns));
            if (pattern.StartsWith('/')) throw new ArgumentException($"ignore pattern '{pattern}' starts with '/'; patterns are relative to assets/", nameof(patterns));
        }

        return new AssetIgnoreRules(list);
    }

    public bool Matches(UPath assetsRoot, UPath path)
    {
        if (_rules.Count == 0) return false;

        var name = path.GetName();
        var relative = path.IsInDirectory(assetsRoot, recursive: true) ? path.FullName[(assetsRoot.FullName.Length + 1)..] : path.FullName;
        foreach (var (_, regex, againstPath) in _rules)
        {
            if (regex.IsMatch(againstPath ? relative : name)) return true;
        }

        return false;
    }

    private static string ToRegex(string glob)
    {
        var regex = new System.Text.StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    regex.Append(".*");
                    i++;
                    break;
                case '*':
                    regex.Append("[^/]*");
                    break;
                case '?':
                    regex.Append("[^/]");
                    break;
                default:
                    regex.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return regex.Append('$').ToString();
    }
}
