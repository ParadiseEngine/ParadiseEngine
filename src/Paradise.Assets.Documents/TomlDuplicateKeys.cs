using Tomlyn.Syntax;

namespace Paradise.Assets.Documents;

/// <summary>
/// Refuses a key defined twice, as TOML 1.0 and the Python mirror's <c>tomllib</c> do (issue #198).
/// </summary>
/// <remarks>
/// Not Tomlyn's own <c>validate: true</c> pass: that one stops advancing the array-of-tables index
/// after a <c>[a.b.sub]</c> header, so the NEXT <c>[[a.b]]</c> element's plain key <c>sub</c> is
/// reported as a redefinition of the earlier element's subtable and a valid document is refused
/// (a light's <c>[objects.components.Value]</c> colour followed by a direction's
/// <c>Value = [...]</c> was the shape that hit; Tomlyn 2.10.1, issue #219). This walk resolves
/// every path through the current element of each array of tables it crosses.
/// <para>
/// The tree is keyed by exact segment, never by a joined string: a quoted <c>"a.b"</c> is one key
/// and <c>a.b</c> is two, and neither may alias the other or an array element.
/// </para>
/// </remarks>
internal static class TomlDuplicateKeys
{
    private enum Kind
    {
        Value,

        /// <summary>Created by a <c>[header]</c>.</summary>
        HeaderTable,

        /// <summary>Created by a header naming something beneath it; a later <c>[header]</c> may still define it.</summary>
        ImpliedTable,

        /// <summary>Created by a dotted key; TOML forbids a later <c>[header]</c> from reopening it.</summary>
        DottedTable,

        ArrayOfTables,
    }

    /// <summary>The first redefinition in document order as a message, or null when every key is defined once.</summary>
    public static string? FindDuplicate(DocumentSyntax document)
    {
        var root = new Node(Kind.HeaderTable);
        foreach (var keyValue in document.KeyValues)
        {
            if (DefineKeyValue(root, [], keyValue) is { } problem) return problem;
        }

        foreach (var table in document.Tables)
        {
            var problem = table is TableArraySyntax array ? DefineTableArray(root, array) : DefineTable(root, table);
            if (problem is not null) return problem;
        }

        return null;
    }

    private sealed class Node(Kind kind)
    {
        public Kind Kind { get; set; } = kind;
        public Dictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);
        public List<Node> Elements { get; } = [];

        /// <summary>The element a path descends into: the newest one, since a header can only ever address that.</summary>
        public Node Current => Elements[^1];
    }

    private static string? DefineTable(Node root, TableSyntaxBase table)
    {
        var segments = Segments(table.Name);
        var parent = ResolveParents(root, segments, table, out var problem);
        if (parent is null) return problem;

        var name = segments[^1];
        if (parent.Children.TryGetValue(name, out var existing))
        {
            if (existing.Kind != Kind.ImpliedTable) return Redefined(segments, table);
            existing.Kind = Kind.HeaderTable;
        }
        else
        {
            existing = new Node(Kind.HeaderTable);
            parent.Children[name] = existing;
        }

        return DefineItems(existing, segments, table.Items);
    }

    private static string? DefineTableArray(Node root, TableArraySyntax table)
    {
        var segments = Segments(table.Name);
        var parent = ResolveParents(root, segments, table, out var problem);
        if (parent is null) return problem;

        var name = segments[^1];
        if (parent.Children.TryGetValue(name, out var existing))
        {
            if (existing.Kind != Kind.ArrayOfTables) return Redefined(segments, table);
        }
        else
        {
            existing = new Node(Kind.ArrayOfTables);
            parent.Children[name] = existing;
        }

        var element = new Node(Kind.HeaderTable);
        existing.Elements.Add(element);
        return DefineItems(element, segments, table.Items);
    }

    private static string? DefineKeyValue(Node table, List<string> tablePath, KeyValueSyntax keyValue)
    {
        var segments = Segments(keyValue.Key);
        var node = table;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            if (node.Children.TryGetValue(segments[i], out var existing))
            {
                if (existing.Kind is not (Kind.DottedTable or Kind.ImpliedTable)) return Redefined([.. tablePath, .. segments[..(i + 1)]], keyValue);
                existing.Kind = Kind.DottedTable;
                node = existing;
            }
            else
            {
                var created = new Node(Kind.DottedTable);
                node.Children[segments[i]] = created;
                node = created;
            }
        }

        var name = segments[^1];
        if (node.Children.ContainsKey(name)) return Redefined([.. tablePath, .. segments], keyValue);
        var value = new Node(Kind.Value);
        node.Children[name] = value;
        return DefineValue(value, [.. tablePath, .. segments], keyValue.Value);
    }

    private static string? DefineItems(Node table, List<string> tablePath, SyntaxList<KeyValueSyntax> items)
    {
        foreach (var item in items)
        {
            if (DefineKeyValue(table, tablePath, item) is { } problem) return problem;
        }

        return null;
    }

    /// <summary>An inline table's keys are one scope; an array's inline tables are each their own.</summary>
    private static string? DefineValue(Node holder, List<string> path, ValueSyntax? value)
    {
        switch (value)
        {
            case InlineTableSyntax inline:
                foreach (var item in inline.Items)
                {
                    if (item.KeyValue is { } keyValue && DefineKeyValue(holder, path, keyValue) is { } problem) return problem;
                }

                break;
            case ArraySyntax array:
                foreach (var item in array.Items)
                {
                    var element = new Node(Kind.Value);
                    holder.Elements.Add(element);
                    if (DefineValue(element, path, item.Value) is { } problem) return problem;
                }

                break;
        }

        return null;
    }

    /// <summary>Every segment but the last, each descended into the CURRENT element when it names an array of tables — the step Tomlyn's validator skips. Null with a message when a segment is already a value.</summary>
    private static Node? ResolveParents(Node root, List<string> segments, SyntaxNode header, out string? problem)
    {
        problem = null;
        var node = root;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            if (node.Children.TryGetValue(segments[i], out var existing))
            {
                if (existing.Kind == Kind.Value)
                {
                    problem = $"The key `{Display(segments[..(i + 1)])}` is already defined as a value and cannot be used as a table (line {Line(header)})";
                    return null;
                }

                node = existing.Kind == Kind.ArrayOfTables ? existing.Current : existing;
            }
            else
            {
                var implied = new Node(Kind.ImpliedTable);
                node.Children[segments[i]] = implied;
                node = implied;
            }
        }

        return node;
    }

    private static string Redefined(List<string> path, SyntaxNode node)
        => $"The key `{Display(path)}` is already defined and cannot be redefined (line {Line(node)})";

    private static int Line(SyntaxNode node) => node.Span.Start.Line + 1;

    /// <summary>For the message only; the tree is keyed by segment, so a dotted display cannot alias anything.</summary>
    private static string Display(List<string> segments) => string.Join(".", segments);

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
}
