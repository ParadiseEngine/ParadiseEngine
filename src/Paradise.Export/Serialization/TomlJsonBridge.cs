#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

using Tomlyn.Model;

namespace Paradise.Export.Serialization
{
    /// <summary>
    /// Converts between a <see cref="JsonNode"/> tree and Tomlyn's object model, which is all the
    /// contract's TOML form needs to be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The contract is not serialized twice.</b> Both formats go through the SAME
    /// System.Text.Json type model — the source-generated <c>ParadiseJsonContext</c> and the
    /// hand-written converters for Color32, the vector/matrix shapes and the string enums — and
    /// this bridges only at the node tree. So the shape, the converters and the contract's own
    /// rules apply by construction, and there is no second serializer to drift from the first.
    /// A hand-written per-DTO TOML serializer would have to restate every one of those, and
    /// nothing would notice the day it stopped agreeing.
    /// </para>
    /// <para>
    /// Tomlyn owns the FORMATTING for the same reason: which values become inline tables, which
    /// become <c>[[arrays of tables]]</c>, and how each is quoted are its decisions, not ours.
    /// (Note the contrast with <c>Paradise.Assets.Documents.CanonicalTomlWriter</c>, which is
    /// hand-rolled precisely because authored documents are a byte-exact cross-language contract
    /// with a Python mirror. Build output is machine-to-machine inside .NET and needs no such
    /// promise — only that what is written reads back equal.)
    /// </para>
    /// <para>
    /// <b>The one place the formats genuinely differ is null.</b> The contract writes with
    /// <c>DefaultIgnoreCondition = Never</c>, so its JSON carries nulls, and <b>TOML has no null
    /// at all</b> — there is no representation to choose. A null-valued key is therefore OMITTED,
    /// and reading relies on System.Text.Json giving an absent key the member's default, which is
    /// the same value the null deserialized to. That is an argument, not a proof, which is why
    /// the parity test reads a document both ways and compares the results rather than trusting
    /// it.
    /// </para>
    /// </remarks>
    internal static class TomlJsonBridge
    {
        /// <summary>Converts a serialized document to Tomlyn's model.</summary>
        /// <exception cref="InvalidOperationException">The root is not an object.</exception>
        public static TomlTable ToToml(JsonNode? node)
        {
            if (node is not JsonObject root)
            {
                throw new InvalidOperationException(
                    "the contract's documents are objects at the root, and TOML has no other shape for one");
            }

            return (TomlTable)Convert(root)!;
        }

        /// <summary>Converts Tomlyn's model back to a node tree the contract's reader accepts.</summary>
        public static JsonNode ToJson(TomlTable table)
        {
            ArgumentNullException.ThrowIfNull(table);
            return (JsonNode)Convert(table)!;
        }

        /// <param name="nested">Whether this value sits inside an array, where `[[header]]` is illegal.</param>
        private static object? Convert(JsonNode? node, bool nested = false)
        {
            switch (node)
            {
                case null:
                    return null;

                case JsonObject obj:
                {
                    // Inline is inherited: TOML lets an inline table hold only inline values, so
                    // once a value is inside one, everything below it is too.
                    var table = new TomlTable(nested);
                    foreach (var (key, value) in obj)
                    {
                        // Omitted rather than represented: see the remarks. A key that is absent
                        // deserializes to the member's default, which is what the null was.
                        var converted = Convert(value, nested);
                        if (converted is not null) table[key] = converted;
                    }

                    return table;
                }

                case JsonArray array:
                {
                    // Decided BEFORE converting, because the two forms need their tables built
                    // differently: a `[[header]]` array holds non-inline tables, and an array
                    // holds only inline ones. Tomlyn refuses either mixture outright rather than
                    // emitting something unparseable, which is how this announced itself twice.
                    if (!nested && array.Count > 0 && array.All(item => item is JsonObject))
                    {
                        var tables = new TomlTableArray();
                        foreach (var item in array) tables.Add((TomlTable)Convert(item, nested: false)!);
                        return tables;
                    }

                    var values = new TomlArray();
                    foreach (var item in array)
                    {
                        // A null INSIDE an array cannot be omitted -- that would shorten the array
                        // and silently move every element after it. The contract has no nullable
                        // array elements, so this is a guard against one appearing rather than a
                        // case to handle.
                        values.Add(Convert(item, nested: true) ?? throw new InvalidOperationException(
                            "a null inside an array has no TOML form, and dropping it would renumber the array"));
                    }

                    return values;
                }

                case JsonValue value:
                    return Scalar(value);

                default:
                    throw new InvalidOperationException($"unexpected node '{node.GetType().Name}'");
            }
        }

        private static object Scalar(JsonValue value)
        {
            if (value.TryGetValue<bool>(out var flag)) return flag;
            if (value.TryGetValue<string>(out var text)) return text;

            // Integers before floats: TOML distinguishes them, and a count written as 3.0 comes
            // back as a double that the reader must then narrow.
            if (value.TryGetValue<long>(out var integer)) return integer;
            if (value.TryGetValue<double>(out var number)) return number;

            return value.ToJsonString();
        }

        private static object? Convert(object? toml)
        {
            switch (toml)
            {
                case null:
                    return null;

                case TomlTable table:
                {
                    var obj = new JsonObject();
                    foreach (var (key, value) in table) obj[key] = (JsonNode?)Convert(value);
                    return obj;
                }

                case TomlTableArray tables:
                {
                    var array = new JsonArray();
                    foreach (var table in tables) array.Add((JsonNode?)Convert(table));
                    return array;
                }

                case TomlArray values:
                {
                    var array = new JsonArray();
                    foreach (var value in values) array.Add((JsonNode?)Convert(value));
                    return array;
                }

                case bool flag:
                    return JsonValue.Create(flag);

                case string text:
                    return JsonValue.Create(text);

                case long integer:
                    return JsonValue.Create(integer);

                case double number:
                    return JsonValue.Create(number);

                case DateTime or DateTimeOffset:
                    // The contract has no date member. Carried as text rather than dropped, so a
                    // document that grows one fails loudly at the reader instead of losing it.
                    return JsonValue.Create(System.Convert.ToString(toml, CultureInfo.InvariantCulture));

                default:
                    return JsonValue.Create(System.Convert.ToString(toml, CultureInfo.InvariantCulture));
            }
        }
    }
}
