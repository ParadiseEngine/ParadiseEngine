using System.Collections;

namespace Paradise.Assets.Documents;

/// <summary>
/// The ordered document model: keys are written as added, never sorted, so a builder adding in
/// schema order is canonical by construction. The vocabulary is what both toolchains handle
/// losslessly; no dates, where TOML implementations disagree most.
/// </summary>
public sealed class CanonicalTomlTable : IEnumerable<KeyValuePair<string, object>>
{
    private readonly List<KeyValuePair<string, object>> _pairs = [];
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    public int Count => _pairs.Count;

    /// <summary><see cref="int"/> widens to <see cref="long"/>, <see cref="float"/> to <see cref="double"/> via <see cref="CanonicalFloat"/>.</summary>
    /// <exception cref="ArgumentException">The key is duplicated, or the value is outside the vocabulary.</exception>
    public void Add(string key, object value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        var normalized = Normalize(value, key);
        if (!_keys.Add(key)) throw new ArgumentException($"Duplicate key '{key}'.", nameof(key));
        _pairs.Add(new KeyValuePair<string, object>(key, normalized));
    }

    public bool ContainsKey(string key) => _keys.Contains(key);

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
        float widened => CanonicalFloat.Widen(widened),
        IReadOnlyList<CanonicalTomlTable> => value,
        IEnumerable<object> array => NormalizeArray(array, key),
        _ => throw new ArgumentException(
            $"Value for '{key}' is a {value.GetType().Name}; canonical TOML holds bool, long, " +
            "double, string, arrays of those, tables, arrays of tables and inline tables.",
            nameof(value)),
    };

    private static object NormalizeArray(IEnumerable<object> array, string key)
    {
        // Copied so a caller mutating its list afterwards cannot desynchronize the document.
        var elements = new List<object>();
        foreach (var element in array)
        {
            ArgumentNullException.ThrowIfNull(element, key);
            var normalized = Normalize(element, key);
            if (normalized is CanonicalTomlTable)
            {
                // Only the generic table is refused: its [[header]] form cannot appear mid-array.
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
