namespace Paradise.Assets.Documents;

/// <summary>Reads canonical TOML text back into a <see cref="CanonicalTomlTable"/> — the counterpart
/// to <see cref="CanonicalTomlWriter"/>.</summary>
/// <remarks>
/// <para>
/// The writer has always been public and the reader has not, so anything holding a canonical table
/// could produce a file and then had no supported way to read one back. Every existing reader here
/// is typed for one document — <see cref="PrefabDocumentSerializer"/>, <see cref="SidecarMeta"/> —
/// which is right for the asset contract and wrong for a caller whose document this package must
/// not learn about. That caller writes its own typed reader over the table this returns.
/// </para>
/// <para>
/// Strict about syntax and nothing else: duplicate keys, bad escapes and values outside the
/// canonical vocabulary are refused, unknown KEYS are not — a document's own reader decides which
/// keys it knows. Document order is preserved, because the canonical form makes it load-bearing.
/// </para>
/// </remarks>
public static class CanonicalTomlReader
{
    /// <summary>Parse <paramref name="toml"/>. <paramref name="sourceName"/> names it in errors.</summary>
    /// <exception cref="InvalidDataException">The text is not valid TOML, or holds a value the
    /// canonical vocabulary cannot represent.</exception>
    public static CanonicalTomlTable Parse(string toml, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(toml);

        InvalidDataException Fail(string message) => new($"'{sourceName}': {message}");

        var table = TomlDocumentReader.Parse(toml, Fail);
        return TomlDocumentReader.ToCanonical(table, $"in '{sourceName}'", Fail);
    }
}
