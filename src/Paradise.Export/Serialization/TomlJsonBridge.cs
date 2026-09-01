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
    /// at all</b> — there is no representation to choose. A null-valued KEY is therefore OMITTED,
    /// and reading relies on System.Text.Json giving an absent key the member's default, which is
    /// the same value the null deserialized to. A null ARRAY ELEMENT cannot be omitted — position
    /// is meaning (an empty material slot) — so it is spelled as the empty inline table <c>{}</c>,
    /// the same spelling authoring uses for a reference to nothing, and read back as null. That is
    /// an argument, not a proof, which is why the parity test reads a document both ways and
    /// compares the results rather than trusting it.
    /// </para>
    /// </remarks>
    internal static class TomlJsonBridge
    {
        /// <summary>
        /// The contract's one array-of-arrays member, and the key its elements are wrapped in so
        /// TOML can give them headers.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>LevelData.Entities</c> is <c>List&lt;List&lt;AuthoredComponentData&gt;&gt;</c> — an
        /// entity has no shape of its own, which is the whole assertion of contract v5. TOML has
        /// no header form for an array of ARRAYS, so mirroring that shape mechanically collapses
        /// every entity in the document onto one enormous inline line, which is worse to read and
        /// diff than the JSON it was meant to improve on.
        /// </para>
        /// <para>
        /// It is not a limitation of the format: the authored <c>*.prefab</c> documents are TOML
        /// and read beautifully, because they nest through OBJECTS — each is a table, so
        /// <c>[[objects.components]]</c> has something to hang a header on. So the TOML form wraps
        /// each entity in a table with this one key, and unwraps it on read.
        /// </para>
        /// <para>
        /// <b>This changes the encoding, never the value.</b> The contract is value-based rather
        /// than byte-based, and the JSON form is untouched — it keeps the array of arrays exactly
        /// as v5 specifies. Choosing how to spell a value is what a second serialization is FOR;
        /// the parity test is what holds the two spellings to the same meaning.
        /// </para>
        /// </remarks>
        internal const string EntitiesKey = "Entities";

        /// <summary>The key each entity's component list is wrapped in. See <see cref="EntitiesKey"/>.</summary>
        internal const string ComponentsKey = "Components";

        /// <summary>Converts a serialized document to Tomlyn's model.</summary>
        /// <exception cref="InvalidOperationException">The root is not an object.</exception>
        public static TomlTable ToToml(JsonNode? node)
        {
            if (node is not JsonObject root)
            {
                throw new InvalidOperationException(
                    "the contract's documents are objects at the root, and TOML has no other shape for one");
            }

            return (TomlTable)Convert(Wrap(root))!;
        }

        /// <summary>Converts Tomlyn's model back to a node tree the contract's reader accepts.</summary>
        public static JsonNode ToJson(TomlTable table)
        {
            ArgumentNullException.ThrowIfNull(table);
            return Unwrap((JsonNode)Convert(table)!);
        }

        /// <summary>Wraps each entity's component list in a table, so TOML can head it.</summary>
        /// <remarks>Symmetric with <see cref="Unwrap"/>; see <see cref="EntitiesKey"/> for why.</remarks>
        private static JsonNode Wrap(JsonNode node)
        {
            if (node is not JsonObject root || root[EntitiesKey] is not JsonArray entities) return node;

            var wrapped = new JsonArray();
            foreach (var entity in entities.ToList())
            {
                // Detached first: a node belongs to one parent, and adding one that still has a
                // parent throws rather than moving it.
                entities.Remove(entity);
                wrapped.Add((JsonNode)new JsonObject { [ComponentsKey] = entity });
            }

            root[EntitiesKey] = wrapped;
            return root;
        }

        /// <summary>Undoes <see cref="Wrap"/>, so the reader sees the contract's own shape.</summary>
        private static JsonNode Unwrap(JsonNode node)
        {
            if (node is not JsonObject root || root[EntitiesKey] is not JsonArray entities) return node;

            var flat = new JsonArray();
            foreach (var entry in entities.ToList())
            {
                entities.Remove(entry);

                // A bare array is accepted as well as the wrapped form: a hand-written document
                // spelling it the JSON way is still the same value, and refusing it would make the
                // wrapper a rule rather than a convenience.
                if (entry is JsonObject wrapper && wrapper[ComponentsKey] is JsonArray components)
                {
                    wrapper.Remove(ComponentsKey);
                    flat.Add((JsonNode?)components);
                }
                else
                {
                    flat.Add((JsonNode?)entry);
                }
            }

            root[EntitiesKey] = flat;
            return root;
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
                        // and silently move every element after it. Since v6, payloads are opaque
                        // and DO carry null array elements (an empty material slot, whose position
                        // is meaning), so the null is spelled as the empty inline table -- the same
                        // spelling authoring uses for "a reference to nothing" -- and the read side
                        // turns it back into the null it stood for.
                        values.Add(Convert(item, nested: true) ?? new TomlTable(inline: true));
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
                    foreach (var value in values)
                    {
                        // The write side's spelling of a null element, undone. Only PLAIN arrays
                        // carry it: a `[[header]]` array never holds a null (a null among objects
                        // forces the whole array inline), so an empty table there stays an object.
                        array.Add(value is TomlTable { Count: 0 } ? null : (JsonNode?)Convert(value));
                    }

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
