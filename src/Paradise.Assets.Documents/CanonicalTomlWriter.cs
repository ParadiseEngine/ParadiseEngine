using System.Globalization;
using System.Text;

namespace Paradise.Assets.Documents;

/// <summary>Writes a <see cref="CanonicalTomlTable"/> as canonical TOML — the writing spec itself, executable.</summary>
/// <remarks>
/// <para>
/// A cross-language contract: the Blender addon's Python writer must produce identical bytes,
/// because both sides write documents and only byte identity keeps a round-trip out of the diff.
/// <c>prefab-check</c> polices it. The spec, normative:
/// </para>
/// <list type="number">
/// <item>Encoding: UTF-8, no byte-order mark. Newline: LF. A non-empty document ends with one
/// LF; an empty document is zero bytes.</item>
/// <item>Only the TOML <b>1.0</b> subset is written (readers may accept 1.1). No comments — a
/// canonical write is a machine write; comment-preserving edits go through a trivia-preserving
/// rewriter instead, never through this writer.</item>
/// <item>Key order is <b>model order</b> (the caller adds keys in schema declaration order),
/// except that within one table every scalar and array key is written before any sub-table —
/// TOML itself demands that, because a <c>key = value</c> line after a <c>[header]</c> belongs
/// to that header.</item>
/// <item>Keys are written bare when non-empty and matching <c>[A-Za-z0-9_-]+</c>, otherwise as
/// basic quoted strings.</item>
/// <item>Strings are basic one-line strings. Escapes: <c>\"</c>, <c>\\</c>, <c>\b</c>,
/// <c>\t</c>, <c>\n</c>, <c>\f</c>, <c>\r</c>, and <c>\uXXXX</c> (uppercase hex) for every other
/// control character (U+0000–U+001F, U+007F). Never literal or multi-line strings.</item>
/// <item>Integers: decimal, no underscores, <c>-</c> for negatives.</item>
/// <item>Floats: shortest digits that round-trip, formatted by Python's <c>repr</c> rules —
/// positional when the decimal exponent of the leading digit is in [-4, 16), otherwise
/// <c>d.ddde±XX</c> with a lowercase <c>e</c>, an explicit sign and a two-digit-minimum
/// exponent. Positional floats always contain a <c>.</c> (integral values end in <c>.0</c>).
/// Specials are <c>inf</c>, <c>-inf</c>, <c>nan</c>; negative zero is <c>-0.0</c>. Matching
/// CPython's repr is deliberate: it makes the Python mirror's implementation one call.</item>
/// <item>Booleans: <c>true</c> / <c>false</c>.</item>
/// <item>Arrays are one line: <c>[1, 2, 3]</c> — <c>", "</c> between elements, no trailing
/// comma, empty is <c>[]</c>. Arrays hold scalars, nested arrays, or <b>inline tables</b>
/// (item 11): a table that is an array ELEMENT is inline by rule (issue #187), which is what
/// keeps a null slot <c>{}</c> expressible. A generic <see cref="CanonicalTomlTable"/> list in
/// the model is a different thing — an array of tables, item 10 — and the two never mix in one
/// value.</item>
/// <item>Every nested <see cref="CanonicalTomlTable"/> is a <c>[dotted.path]</c> header; every
/// array of tables is one <c>[[dotted.path]]</c> header per element, in element order.
/// Dotted-path segments are formatted as keys (item 4). One blank line precedes every header
/// except at the start of the document. Never dotted keys. An EMPTY generic table is written
/// <c>key = {}</c> and an empty array of tables <c>key = []</c>, at value position: a header with
/// nothing under it has no content for the reader to restore the form from, so the only empty
/// table these documents can hold is the inline one (a reference to nothing), and the writer
/// says so (issue #199).</item>
/// <item>A <see cref="CanonicalInlineTable"/> is written on one line as
/// <c>{ key = value, … }</c> — <c>", "</c> between pairs, in model order, keys by item 4 and
/// values by items 5–9. An empty one is <c>{}</c>, which is how a null element inside an array
/// is spelled. Inline tables never nest another table.
/// <para>Writing chooses the form by TYPE; reading restores it so that both readers rebuild
/// the same model from the same bytes (issue #187), which <c>TomlDocumentReader</c> and the
/// mirror's <c>restore_inline_tables</c> both implement: <b>a table at value position is inline
/// iff it is empty or has exactly the two string keys <c>guid</c> and <c>path</c></b>
/// (<see cref="AssetReferenceCodec.IsWrittenInline"/>), a shape therefore RESERVED for asset
/// references — content, because the Python mirror's parser cannot see the source form; and
/// <b>a table inside an array is inline regardless of content</b> (item 9), unless the array
/// was spelled as <c>[[header]]</c> blocks, which stay an array of tables (item 10) — a fact
/// Tomlyn reports as <c>TomlTableArray</c> and the mirror reads off the text, since a
/// <c>[[</c> header can only stand at the start of a line. The parity corpus in
/// <c>Paradise.Assets.Documents.Test/Fixtures/parity</c> pins every rule for both writers
/// (issue #209).</para></item>
/// </list>
/// </remarks>
public static class CanonicalTomlWriter
{
    public static string WriteString(CanonicalTomlTable document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var builder = new StringBuilder();
        WriteBody(builder, document, pathPrefix: null);
        return builder.ToString();
    }

    public static byte[] WriteBytes(CanonicalTomlTable document) => Encoding.UTF8.GetBytes(WriteString(document));

    private static void WriteBody(StringBuilder builder, CanonicalTomlTable table, string? pathPrefix)
    {
        foreach (var (key, value) in table)
        {
            if (value is CanonicalTomlTable { Count: > 0 } or IReadOnlyList<CanonicalTomlTable> { Count: > 0 }) continue;
            builder.Append(FormatKey(key)).Append(" = ");
            switch (value)
            {
                case CanonicalTomlTable: builder.Append("{}"); break;
                case IReadOnlyList<CanonicalTomlTable>: builder.Append("[]"); break;
                default: WriteValue(builder, value); break;
            }

            builder.Append('\n');
        }

        foreach (var (key, value) in table)
        {
            var childPath = pathPrefix is null ? FormatKey(key) : $"{pathPrefix}.{FormatKey(key)}";
            switch (value)
            {
                case CanonicalTomlTable { Count: > 0 } child:
                    WriteHeader(builder, $"[{childPath}]");
                    WriteBody(builder, child, childPath);
                    break;

                case IReadOnlyList<CanonicalTomlTable> { Count: > 0 } elements:
                    foreach (var element in elements)
                    {
                        WriteHeader(builder, $"[[{childPath}]]");
                        WriteBody(builder, element, childPath);
                    }

                    break;
            }
        }
    }

    private static void WriteHeader(StringBuilder builder, string header)
    {
        if (builder.Length > 0) builder.Append('\n');
        builder.Append(header).Append('\n');
    }

    private static void WriteValue(StringBuilder builder, object value)
    {
        switch (value)
        {
            case bool boolean:
                builder.Append(boolean ? "true" : "false");
                break;

            case long integer:
                builder.Append(integer.ToString(CultureInfo.InvariantCulture));
                break;

            case double floating:
                builder.Append(FormatFloat(floating));
                break;

            case string text:
                WriteBasicString(builder, text);
                break;

            case CanonicalInlineTable inline:
                WriteInlineTable(builder, inline);
                break;

            case IReadOnlyList<object> array:
                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0) builder.Append(", ");
                    WriteValue(builder, array[i]);
                }

                builder.Append(']');
                break;

            default:
                // CanonicalTomlTable.Add is the gate; reaching this is a bug in THIS file.
                throw new InvalidOperationException($"Unwritable value of type {value.GetType().Name}.");
        }
    }

    private static void WriteInlineTable(StringBuilder builder, CanonicalInlineTable table)
    {
        if (table.Count == 0)
        {
            builder.Append("{}");
            return;
        }

        builder.Append("{ ");
        var first = true;
        foreach (var (key, value) in table)
        {
            if (!first) builder.Append(", ");
            first = false;
            builder.Append(FormatKey(key)).Append(" = ");
            WriteValue(builder, value);
        }

        builder.Append(" }");
    }

    private static string FormatKey(string key)
    {
        if (key.Length > 0 && key.All(static c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-'))
        {
            return key;
        }

        var builder = new StringBuilder(key.Length + 2);
        WriteBasicString(builder, key);
        return builder.ToString();
    }

    private static void WriteBasicString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\t': builder.Append("\\t"); break;
                case '\n': builder.Append("\\n"); break;
                case '\f': builder.Append("\\f"); break;
                case '\r': builder.Append("\\r"); break;
                default:
                    if (c < ' ' || c == (char)0x7F)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    /// <summary>CPython's <c>repr(float)</c>, reimplemented (spec item 7).</summary>
    internal static string FormatFloat(double value)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsPositiveInfinity(value)) return "inf";
        if (double.IsNegativeInfinity(value)) return "-inf";

        var negative = double.IsNegative(value);
        if (value == 0d) return negative ? "-0.0" : "0.0";

        var shortest = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var (digits, pointExponent) = SplitDigits(shortest);
        var sign = negative ? "-" : "";

        if (pointExponent is >= -4 and < 16)
        {
            return sign + Positional(digits, pointExponent);
        }

        var mantissa = digits.Length == 1 ? digits : $"{digits[0]}.{digits[1..]}";
        var exponentSign = pointExponent < 0 ? '-' : '+';
        return $"{sign}{mantissa}e{exponentSign}{Math.Abs(pointExponent):D2}";
    }

    private static (string Digits, int PointExponent) SplitDigits(string shortest)
    {
        var mantissa = shortest;
        var exponent = 0;
        var exponentAt = shortest.IndexOfAny(['E', 'e']);
        if (exponentAt >= 0)
        {
            mantissa = shortest[..exponentAt];
            exponent = int.Parse(shortest[(exponentAt + 1)..], CultureInfo.InvariantCulture);
        }

        var pointAt = mantissa.IndexOf('.');
        var digits = pointAt < 0 ? mantissa : mantissa.Remove(pointAt, 1);
        var integerLength = pointAt < 0 ? mantissa.Length : pointAt;

        var leadingZeros = 0;
        while (leadingZeros < digits.Length - 1 && digits[leadingZeros] == '0') leadingZeros++;
        digits = digits[leadingZeros..].TrimEnd('0');
        if (digits.Length == 0) digits = "0";

        return (digits, integerLength - 1 - leadingZeros + exponent);
    }

    private static string Positional(string digits, int pointExponent)
    {
        if (pointExponent < 0)
        {
            return "0." + new string('0', -pointExponent - 1) + digits;
        }

        if (pointExponent >= digits.Length - 1)
        {
            return digits + new string('0', pointExponent - (digits.Length - 1)) + ".0";
        }

        return digits[..(pointExponent + 1)] + "." + digits[(pointExponent + 1)..];
    }
}
