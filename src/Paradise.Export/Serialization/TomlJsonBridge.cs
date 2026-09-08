#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

using Tomlyn.Model;

namespace Paradise.Export.Serialization
{
    /// <summary>Bridges JSON nodes and Tomlyn's object model.</summary>
    /// <remarks>
    /// Both formats use the same source-generated JSON metadata and converters; Tomlyn formats build
    /// output, which requires value parity rather than the authored writer's byte-exact Python parity.
    /// TOML omits null-valued keys and spells null array elements as <c>{}</c> to preserve positions.
    /// The reader therefore also treats an empty object in an array as null; documents requiring those
    /// values to remain distinct need a different representation.
    /// </remarks>
    internal static class TomlJsonBridge
    {
        /// <summary>The entity array, whose component lists are wrapped for TOML table headers.</summary>
        /// <remarks>
        /// Contract v5 stores entities as arrays of component arrays. TOML wraps each entity in a
        /// table under <see cref="ComponentsKey"/> to allow <c>[[Entities.Components]]</c> headers.
        /// <see cref="Unwrap"/> restores the array shape on read; JSON keeps it unchanged.
        /// Value-parity tests verify that both encodings represent the same document.
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
            return Unwrap((JsonObject)Convert(table)!);
        }

        /// <summary>Wraps each entity's component list in a table, so TOML can head it.</summary>
        /// <remarks>Symmetric with <see cref="Unwrap"/>; see <see cref="EntitiesKey"/> for why.</remarks>
        private static JsonNode Wrap(JsonObject root)
        {
            if (root[EntitiesKey] is not JsonArray entities) return root;

            var entries = entities.ToArray();
            entities.Clear(); // Detach nodes before assigning their new parent.
            var wrapped = new JsonArray();
            foreach (var entity in entries)
            {
                wrapped.Add((JsonNode)new JsonObject { [ComponentsKey] = entity });
            }

            root[EntitiesKey] = wrapped;
            return root;
        }

        /// <summary>Undoes <see cref="Wrap"/>, so the reader sees the contract's own shape.</summary>
        private static JsonNode Unwrap(JsonObject root)
        {
            if (root[EntitiesKey] is not JsonArray entities) return root;

            var entries = entities.ToArray();
            entities.Clear();
            var flat = new JsonArray();
            foreach (var entry in entries)
            {
                // Accept bare component arrays as well as wrapped entity tables.
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

                default:
                    return JsonValue.Create(System.Convert.ToString(toml, CultureInfo.InvariantCulture));
            }
        }
    }
}
