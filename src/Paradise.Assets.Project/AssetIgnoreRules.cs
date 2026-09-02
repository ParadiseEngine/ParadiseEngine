using System.Text.RegularExpressions;

using Zio;

namespace Paradise.Assets.Project;

/// <summary>
/// The project's own list of files under <c>assets/</c> the pipeline pretends are not there —
/// <c>[assets] ignore</c> in <c>project.toml</c>. The engine ships no list of its own: which
/// scratch files an editor leaves beside the assets is the project's to know, and a rule the
/// project cannot see is a file that silently never builds.
/// </summary>
/// <remarks>
/// A pattern without a <c>/</c> matches the file name; one with a <c>/</c> matches the path
/// relative to <c>assets/</c>. <c>*</c> and <c>?</c> stay within one segment, <c>**</c> crosses
/// them. Matching is ordinal and case-sensitive, as the paths are.
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
