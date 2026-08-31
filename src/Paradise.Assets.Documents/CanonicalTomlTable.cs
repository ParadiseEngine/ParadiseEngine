using System.Collections;

namespace Paradise.Assets.Documents;

/// <summary>
/// The document model the canonical writer emits: an ordered map of keys to values.
/// </summary>
/// <remarks>
/// Order is the point: keys are written exactly as added, never sorted, so a builder that adds
/// keys in schema order produces the canonical document by construction — in C# and the Python
/// mirror alike. The value vocabulary is the intersection both toolchains handle losslessly
/// (bool, long, double, string, arrays, tables, arrays of tables); no dates, which is where
/// TOML implementations disagree most.
/// </remarks>
public sealed class CanonicalTomlTable : IEnumerable<KeyValuePair<string, object>>
{
    private readonly List<KeyValuePair<string, object>> _pairs = [];
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    /// <summary>Number of keys in this table.</summary>
    public int Count => _pairs.Count;

    /// <summary>
    /// Adds a value under <paramref name="key"/>. Accepts the canonical vocabulary plus
    /// <see cref="int"/> (widened to <see cref="long"/>) and <see cref="float"/> (widened to
    /// <see cref="double"/>).
    /// </summary>
    /// <param name="key">The key, in schema declaration order relative to its siblings.</param>
    /// <param name="value">The value; see the class remarks for the vocabulary.</param>
    /// <exception cref="ArgumentException">The key is duplicated, or the value is outside the vocabulary.</exception>
    public void Add(string key, object value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        var normalized = Normalize(value, key);
        if (!_keys.Add(key)) throw new ArgumentException($"Duplicate key '{key}'.", nameof(key));
        _pairs.Add(new KeyValuePair<string, object>(key, normalized));
    }

    /// <summary>Whether <paramref name="key"/> exists in this table.</summary>
    public bool ContainsKey(string key) => _keys.Contains(key);

    /// <summary>The value under <paramref name="key"/>, or <see langword="null"/> when absent.</summary>
    public object? Value(string key)
    {
        foreach (var (candidate, value) in _pairs)
        {
            if (string.Equals(candidate, key, StringComparison.Ordinal)) return value;
        }

        return null;
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => _pairs.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static object Normalize(object value, string key) => value switch
    {
        bool or long or double or string or CanonicalTomlTable or CanonicalInlineTable => value,
        int widened => (long)widened,
        float widened => WidenFloat(widened),
        IReadOnlyList<CanonicalTomlTable> => value,
        IEnumerable<object> array => NormalizeArray(array, key),
        _ => throw new ArgumentException(
            $"Value for '{key}' is a {value.GetType().Name}; canonical TOML holds bool, long, " +
            "double, string, arrays of those, tables, arrays of tables and inline tables.",
            nameof(value)),
    };

    /// <summary>
    /// Widens a float32 through its shortest decimal form rather than bit-exactly, so
    /// <c>0.1f</c> writes as <c>0.1</c> and not <c>0.10000000149011612</c>. Reading the decimal
    /// back at float32 reproduces the value — the same convention the JSON contract's
    /// <c>f32_repr</c> established.
    /// </summary>
    private static double WidenFloat(float value)
    {
        if (!float.IsFinite(value)) return value;
        return double.Parse(
            value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static object NormalizeArray(IEnumerable<object> array, string key)
    {
        // Copied so a caller mutating its list afterwards cannot desynchronize the document,
        // and normalized so int/float elements get the same widening as top-level values.
        var elements = new List<object>();
        foreach (var element in array)
        {
            ArgumentNullException.ThrowIfNull(element, key);
            var normalized = Normalize(element, key);
            if (normalized is CanonicalTomlTable)
            {
                // A CanonicalInlineTable IS allowed here -- that is what it is for. This refuses
                // only the generic table, whose writer form is a [[header]] per element and so
                // cannot appear mid-array.
                throw new ArgumentException(
                    $"Array '{key}' contains a table. Use CanonicalInlineTable for a table INSIDE " +
                    "an array; a list of CanonicalTomlTable is an array-of-tables, which the writer " +
                    "emits as [[headers]].",
                    nameof(array));
            }

            elements.Add(normalized);
        }

        return elements;
    }
}
