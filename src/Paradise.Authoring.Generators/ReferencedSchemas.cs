using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Paradise.Authoring.Generators;

/// <summary>One referenced assembly's published <c>AuthoringSchema.Json</c>.</summary>
internal readonly struct ReferencedSchema(string assembly, string json)
{
    /// <summary>The assembly that published it, for a diagnostic to name.</summary>
    public string Assembly { get; } = assembly;

    /// <summary>The document verbatim, exactly as that assembly's own generator emitted it.</summary>
    public string Json { get; } = json;
}

/// <summary>
/// Reading the schema documents that REFERENCED assemblies already published, so a project that
/// declares no <c>[Authored]</c> types of its own can still publish one document covering
/// everything it links against. Opted into with <c>ParadiseAuthoringScanReferences</c>; see
/// <see cref="AuthoringSchemaGenerator"/> for why an aggregate is wanted at all.
///
/// THE DOCUMENTS ARE MERGED, NOT RE-DERIVED, and that is the whole design rather than an
/// optimisation. A field's <c>default</c> is the record's own property initializer, which exists
/// only in SYNTAX — <c>DeclaringSyntaxReferences</c> is empty for a symbol loaded from metadata,
/// so re-reading a referenced type through <see cref="AuthoredModel"/> would silently produce the
/// same component with every default missing. Editors draw those defaults. Taking each assembly's
/// already-generated constant instead means a referenced component arrives at full fidelity, and
/// arrives identical to what that assembly's own dump would have written.
///
/// The consequence to know: a reference is only visible here if it was built by a generator that
/// emits the constant. That is every assembly declaring <c>[Authored]</c> types, because
/// <see cref="AuthoringSchemaGenerator"/> is not opt-in — but it does mean a stale binary
/// published at an older schema version is skipped with PAUT007 rather than half-read.
/// </summary>
internal static class ReferencedSchemas
{
    /// <summary>The generated holder, matching <c>Paradise.Authoring.SchemaDump</c>'s.</summary>
    private const string TypeName = "AuthoringSchema";
    private const string FieldName = "Json";

    /// <summary>The assembly every schema-publishing assembly must reference, since it is where
    /// <c>[Authored]</c> itself lives. Used to skip the BCL without walking it: the alternative
    /// is a recursive namespace scan of several hundred reference assemblies on every
    /// compilation, to find a type that can only exist in the handful that link this one.</summary>
    private const string AuthoringAssembly = "Paradise.Authoring";

    public static ImmutableArray<ReferencedSchema> Read(
        Compilation compilation, CancellationToken cancellation)
    {
        var found = ImmutableArray.CreateBuilder<ReferencedSchema>();
        foreach (var reference in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!LinksAuthoring(reference))
            {
                continue;
            }
            Collect(reference.GlobalNamespace, reference.Name, found, cancellation);
        }

        // By assembly name, so the merge order — and therefore which of two assemblies claiming
        // one id wins — is a property of the reference SET rather than of the order the compiler
        // happened to hand them over in.
        found.Sort(static (a, b) => string.CompareOrdinal(a.Assembly, b.Assembly));
        return found.ToImmutable();
    }

    private static bool LinksAuthoring(IAssemblySymbol assembly)
    {
        foreach (var module in assembly.Modules)
        {
            foreach (var identity in module.ReferencedAssemblies)
            {
                if (identity.Name == AuthoringAssembly)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static void Collect(
        INamespaceSymbol space,
        string assembly,
        ImmutableArray<ReferencedSchema>.Builder found,
        CancellationToken cancellation)
    {
        foreach (var member in space.GetMembers())
        {
            cancellation.ThrowIfCancellationRequested();
            switch (member)
            {
                case INamespaceSymbol nested:
                    Collect(nested, assembly, found, cancellation);
                    break;
                case INamedTypeSymbol type when type.Name == TypeName:
                    foreach (var field in type.GetMembers(FieldName))
                    {
                        if (field is IFieldSymbol { IsConst: true, ConstantValue: string json })
                        {
                            found.Add(new ReferencedSchema(assembly, json));
                        }
                    }
                    break;
            }
        }
    }

    // ---- Just enough JSON to take a published document apart again. ----
    //
    // A hand-rolled scanner rather than a parser dependency: this project targets netstandard2.0
    // as every analyzer must, and an analyzer may not carry a package the compiler host would
    // have to load. It only ever reads documents THIS generator wrote, so it needs to be correct
    // rather than tolerant — but it is written to the grammar, not to the emitter's current
    // whitespace and key order, because those are not a contract between two builds.

    /// <summary>The document's <c>version</c>, or null when it has none.</summary>
    public static int? Version(string json)
    {
        if (!TryFindMember(json, 0, "version", out var start, out var end))
        {
            return null;
        }
        return int.TryParse(json.Substring(start, end - start), out var version) ? version : null;
    }

    /// <summary>Every component object in the document's <c>components</c> array, each with the
    /// id and type name it is merged and ordered by, and its text verbatim so the merged document
    /// re-publishes exactly what the source assembly published.</summary>
    public static List<(string Id, string TypeName, string Element)> Components(string json)
    {
        var components = new List<(string, string, string)>();
        if (!TryFindMember(json, 0, "components", out var start, out var end)
            || start >= json.Length || json[start] != '[')
        {
            return components;
        }

        var i = SkipWhitespace(json, start + 1);
        while (i < end && json[i] != ']')
        {
            var elementEnd = SkipValue(json, i);
            if (elementEnd <= i)
            {
                // SkipValue stands still on ',', '}' and ']'. The first two are handled below and
                // the third ends the array, so a '}' at an element position is the one input that
                // would leave the index untouched and spin here forever — uninterruptibly, on the
                // compiler's generator thread, taking a command-line build or an IDE's IntelliSense
                // with it. The scanner is written to trust its input (see the note above), but
                // "correct rather than tolerant" cannot extend to non-termination: the discovery
                // rule in Read matches ANY const string named AuthoringSchema.Json, so a
                // hand-written one is reachable.
                break;
            }
            var element = json.Substring(i, elementEnd - i);
            TryFindMember(element, 0, "id", out var idStart, out var idEnd);
            TryFindMember(element, 0, "type", out var typeStart, out var typeEnd);
            components.Add((
                idStart < 0 ? "" : Unquote(element, idStart, idEnd),
                typeStart < 0 ? "" : Unquote(element, typeStart, typeEnd),
                element));
            i = SkipWhitespace(json, elementEnd);
            if (i < end && json[i] == ',')
            {
                i = SkipWhitespace(json, i + 1);
            }
        }
        return components;
    }

    /// <summary>The span of <paramref name="name"/>'s value in the object at
    /// <paramref name="objectStart"/>, or false with -1 bounds when the object has no such
    /// member. Members are walked rather than searched for, so a key nested inside a VALUE — a
    /// field's own "type", of which every component has several — cannot be mistaken for the
    /// component's.</summary>
    private static bool TryFindMember(
        string json, int objectStart, string name, out int valueStart, out int valueEnd)
    {
        valueStart = -1;
        valueEnd = -1;
        var i = SkipWhitespace(json, objectStart);
        if (i >= json.Length || json[i] != '{')
        {
            return false;
        }
        i = SkipWhitespace(json, i + 1);
        while (i < json.Length && json[i] == '"')
        {
            var keyEnd = SkipString(json, i);
            var key = Unquote(json, i, keyEnd);
            i = SkipWhitespace(json, keyEnd);
            if (i >= json.Length || json[i] != ':')
            {
                return false;
            }
            var start = SkipWhitespace(json, i + 1);
            var end = SkipValue(json, start);
            if (key == name)
            {
                valueStart = start;
                valueEnd = end;
                return true;
            }
            i = SkipWhitespace(json, end);
            if (i < json.Length && json[i] == ',')
            {
                i = SkipWhitespace(json, i + 1);
                continue;
            }
            return false;
        }
        return false;
    }

    private static int SkipWhitespace(string json, int i)
    {
        while (i < json.Length && (json[i] == ' ' || json[i] == '\t'
            || json[i] == '\r' || json[i] == '\n'))
        {
            i++;
        }
        return i;
    }

    /// <summary>Index one past the closing quote of the string starting at <paramref name="i"/>.</summary>
    private static int SkipString(string json, int i)
    {
        i++;
        while (i < json.Length)
        {
            if (json[i] == '\\')
            {
                i += 2;
                continue;
            }
            if (json[i] == '"')
            {
                return i + 1;
            }
            i++;
        }
        return i;
    }

    /// <summary>Index one past the value starting at <paramref name="i"/>. Objects and arrays are
    /// counted by their OWN bracket only — a nested one of the other kind is balanced within, and
    /// a bracket inside a string is never seen because strings are skipped whole.</summary>
    private static int SkipValue(string json, int i)
    {
        if (i >= json.Length)
        {
            return i;
        }
        if (json[i] == '"')
        {
            return SkipString(json, i);
        }
        if (json[i] == '{' || json[i] == '[')
        {
            var open = json[i];
            var close = open == '{' ? '}' : ']';
            var depth = 0;
            while (i < json.Length)
            {
                if (json[i] == '"')
                {
                    i = SkipString(json, i);
                    continue;
                }
                if (json[i] == open)
                {
                    depth++;
                }
                else if (json[i] == close && --depth == 0)
                {
                    return i + 1;
                }
                i++;
            }
            return i;
        }
        while (i < json.Length && json[i] != ',' && json[i] != '}' && json[i] != ']')
        {
            i++;
        }
        return i;
    }

    /// <summary>The string literal spanning [start, end) as its text. Not a general unescaper —
    /// it is only ever handed ids and type names — but it undoes what this generator's own
    /// <c>Quote</c> can produce, so a name it escaped is not merged under the escaped spelling.</summary>
    private static string Unquote(string json, int start, int end)
    {
        if (end - start < 2 || json[start] != '"')
        {
            return json.Substring(start, end - start);
        }
        var text = new StringBuilder(end - start - 2);
        for (var i = start + 1; i < end - 1; i++)
        {
            if (json[i] != '\\' || i + 1 >= end - 1)
            {
                text.Append(json[i]);
                continue;
            }
            var escape = json[++i];
            switch (escape)
            {
                case 'n': text.Append('\n'); break;
                case 'r': text.Append('\r'); break;
                case 't': text.Append('\t'); break;
                case 'b': text.Append('\b'); break;
                case 'f': text.Append('\f'); break;
                case 'u' when i + 4 < end - 1:
                    text.Append((char)Convert.ToInt32(json.Substring(i + 1, 4), 16));
                    i += 4;
                    break;
                default: text.Append(escape); break;
            }
        }
        return text.ToString();
    }
}

/// <summary>
/// Structural equality for the reference scan's result, so the source output re-runs only when a
/// referenced assembly's schema actually CHANGED.
///
/// Needed because the scan hangs off <c>CompilationProvider</c>, which invalidates on every
/// keystroke: without this the merged document would be rebuilt on each one, since
/// <c>ImmutableArray&lt;T&gt;</c>'s default equality is reference equality of its backing array.
/// </summary>
internal sealed class ReferencedSchemaComparer : IEqualityComparer<ImmutableArray<ReferencedSchema>>
{
    public static readonly ReferencedSchemaComparer Instance = new();

    public bool Equals(ImmutableArray<ReferencedSchema> x, ImmutableArray<ReferencedSchema> y)
    {
        if (x.Length != y.Length)
        {
            return false;
        }
        for (var i = 0; i < x.Length; i++)
        {
            if (!string.Equals(x[i].Assembly, y[i].Assembly, StringComparison.Ordinal)
                || !string.Equals(x[i].Json, y[i].Json, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    public int GetHashCode(ImmutableArray<ReferencedSchema> obj)
    {
        var hash = 17;
        foreach (var schema in obj)
        {
            hash = (hash * 31) + schema.Json.Length;
        }
        return hash;
    }
}
