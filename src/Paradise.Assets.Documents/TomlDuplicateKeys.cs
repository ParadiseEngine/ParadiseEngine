using Tomlyn.Syntax;

namespace Paradise.Assets.Documents;

/// <summary>
/// Refuses a key defined twice, as TOML and the Python mirror's <c>tomllib</c> do (issue #198).
/// </summary>
/// <remarks>
/// Not Tomlyn's own <c>validate: true</c> pass: that one stops advancing the array-of-tables index
/// after a <c>[a.b.sub]</c> header, so the NEXT <c>[[a.b]]</c> element's plain key <c>sub</c> is
/// reported as a redefinition of the earlier element's subtable and a valid document is refused
/// (a light's <c>[objects.components.Value]</c> colour followed by a direction's
/// <c>Value = [...]</c> was the shape that hit; Tomlyn 2.10.1). This walk resolves every path
/// through the current element of each array of tables it crosses.
/// </remarks>
internal static class TomlDuplicateKeys
{
    private enum Kind { Value, ExplicitTable, ImplicitTable, ArrayOfTables }

    /// <summary>The first redefinition in document order as a message, or null when every key is defined once.</summary>
    public static string? FindDuplicate(DocumentSyntax document)
    {
        var walk = new Walk();
        foreach (var keyValue in document.KeyValues)
        {
            if (walk.DefineKeyValue("", keyValue) is { } problem) return problem;
        }

        foreach (var table in document.Tables)
        {
            var problem = table switch
            {
                TableArraySyntax array => walk.DefineTableArray(array),
                _ => walk.DefineTable(table),
            };
            if (problem is not null) return problem;
        }

        return null;
    }

    private sealed class Walk
    {
        private readonly Dictionary<string, Kind> _defined = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _arrayLengths = new(StringComparer.Ordinal);

        public string? DefineTable(TableSyntaxBase table)
        {
            var segments = Segments(table.Name);
            if (ResolveParents(segments, out var parent, out var problem) is false) return problem;
            var path = Join(parent, segments[^1]);
            if (_defined.TryGetValue(path, out var existing) && existing != Kind.ImplicitTable)
            {
                return Redefined(path, table);
            }

            _defined[path] = Kind.ExplicitTable;
            return DefineItems(path, table.Items);
        }

        public string? DefineTableArray(TableArraySyntax table)
        {
            var segments = Segments(table.Name);
            if (ResolveParents(segments, out var parent, out var problem) is false) return problem;
            var path = Join(parent, segments[^1]);
            if (_defined.TryGetValue(path, out var existing) && existing != Kind.ArrayOfTables)
            {
                return Redefined(path, table);
            }

            _defined[path] = Kind.ArrayOfTables;
            var index = _arrayLengths.GetValueOrDefault(path);
            _arrayLengths[path] = index + 1;
            return DefineItems(Element(path, index), table.Items);
        }

        public string? DefineKeyValue(string prefix, KeyValueSyntax keyValue)
        {
            var segments = Segments(keyValue.Key);
            var path = prefix;
            for (var i = 0; i < segments.Count - 1; i++)
            {
                path = Join(path, segments[i]);
                if (_defined.TryGetValue(path, out var existing) && existing != Kind.ImplicitTable)
                {
                    return Redefined(path, keyValue);
                }

                _defined[path] = Kind.ImplicitTable;
            }

            path = Join(path, segments[^1]);
            if (_defined.ContainsKey(path)) return Redefined(path, keyValue);
            _defined[path] = Kind.Value;
            return DefineValue(path, keyValue.Value);
        }

        private string? DefineItems(string path, SyntaxList<KeyValueSyntax> items)
        {
            foreach (var item in items)
            {
                if (DefineKeyValue(path, item) is { } problem) return problem;
            }

            return null;
        }

        private string? DefineValue(string path, ValueSyntax? value)
        {
            switch (value)
            {
                case InlineTableSyntax inline:
                    foreach (var item in inline.Items)
                    {
                        if (item.KeyValue is { } keyValue && DefineKeyValue(path, keyValue) is { } problem) return problem;
                    }

                    break;
                case ArraySyntax array:
                    var index = 0;
                    foreach (var item in array.Items)
                    {
                        if (DefineValue(Element(path, index++), item.Value) is { } problem) return problem;
                    }

                    break;
            }

            return null;
        }

        /// <summary>Every segment but the last, each descended into the CURRENT element when it names an array of tables — the step Tomlyn's validator skips.</summary>
        private bool ResolveParents(List<string> segments, out string parent, out string? problem)
        {
            parent = "";
            problem = null;
            for (var i = 0; i < segments.Count - 1; i++)
            {
                var path = Join(parent, segments[i]);
                if (_defined.TryGetValue(path, out var existing))
                {
                    if (existing == Kind.Value)
                    {
                        problem = $"The key `{path}` is already defined as a value and cannot be used as a table";
                        return false;
                    }
                }
                else
                {
                    _defined[path] = Kind.ImplicitTable;
                }

                parent = existing == Kind.ArrayOfTables ? Element(path, _arrayLengths[path] - 1) : path;
            }

            return true;
        }

        private static string Redefined(string path, SyntaxNode node)
            => $"The key `{path}` is already defined and cannot be redefined (line {node.Span.Start.Line + 1})";
    }

    private static List<string> Segments(KeySyntax? key)
    {
        var segments = new List<string> { Text(key?.Key) };
        foreach (var dotted in key?.DotKeys ?? [])
        {
            segments.Add(Text(dotted.Key));
        }

        return segments;
    }

    private static string Text(BareKeyOrStringValueSyntax? key) => key switch
    {
        BareKeySyntax bare => bare.Key?.Text ?? "",
        StringValueSyntax quoted => quoted.Value ?? "",
        _ => "",
    };

    private static string Join(string prefix, string segment) => prefix.Length == 0 ? segment : prefix + "." + segment;

    private static string Element(string path, int index) => $"{path}[{index}]";
}
