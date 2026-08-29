using System.Collections;

namespace Paradise.Assets.Documents;

/// <summary>
/// The document model the canonical writer emits: an ordered map of keys to values.
/// </summary>
/// <remarks>
/// <para>
/// Order is the point. Canonical TOML defines key order as <i>schema declaration order</i>, and
/// this type realizes that by writing keys exactly in the order they were added — the writer
/// never sorts. A document builder that adds keys in its schema's order therefore produces the
/// canonical document by construction, in C# and in the Python mirror alike.
/// </para>
/// <para>
/// The value vocabulary is deliberately the intersection both toolchains handle losslessly:
/// <see cref="bool"/>, <see cref="long"/>, <see cref="double"/>, <see cref="string"/>, arrays
/// (<c>IReadOnlyList&lt;object&gt;</c> of these), nested tables, and arrays of tables. No dates:
/// no authored document needs one, and TOML datetimes are where TOML implementations disagree
/// most.
/// </para>
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

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => _pairs.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static object Normalize(object value, string key) => value switch
    {
        bool or long or double or string or CanonicalTomlTable => value,
        int widened => (long)widened,
        float widened => WidenFloat(widened),
        IReadOnlyList<CanonicalTomlTable> => value,
        IEnumerable<object> array => NormalizeArray(array, key),
        _ => throw new ArgumentException(
            $"Value for '{key}' is a {value.GetType().Name}; canonical TOML holds bool, long, " +
            "double, string, arrays of those, tables and arrays of tables.", nameof(value)),
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
                throw new ArgumentException(
                    $"Array '{key}' contains a table. Tables in arrays must be an " +
                    "IReadOnlyList<CanonicalTomlTable>, which the writer emits as [[array of tables]].",
                    nameof(array));
            }

            elements.Add(normalized);
        }

        return elements;
    }
}
