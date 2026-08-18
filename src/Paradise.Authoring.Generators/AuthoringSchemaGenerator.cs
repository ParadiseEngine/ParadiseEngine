using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Paradise.Authoring.Generators;

/// <summary>
/// Emits the engine-neutral authoring schema as a <c>const string</c> on an
/// <c>AuthoringSchema</c> class in the compiling assembly's own namespace.
///
/// This is the half that serves the editors a C# generator cannot reach. The Blender addon
/// (Python) and the browser editor (TypeScript) do not get generated code — they read this
/// document and build their own UI from it, which means adding a field is a data change to them,
/// with no build step and no generated artefact to commit.
///
/// A const rather than a file written at build time, so there is no reflection over a compiled
/// assembly and exactly one view of the definitions: whatever the generator saw. A CLI that dumps
/// it to disk for a non-C# editor is then trivial.
///
/// Deliberately the ONLY emitter. An earlier attempt also generated the Godot node's [Export]
/// properties; it cannot work, because Godot's own ScriptPropertiesGenerator is a source generator
/// and two Roslyn generators cannot observe each other's output — the properties never reach the
/// inspector and the scene's values are silently dropped.
/// </summary>
[Generator]
public sealed class AuthoringSchemaGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var authored = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AuthoredModel.AuthoredAttribute,
                predicate: static (node, _) => true,
                transform: static (ctx, _) => ctx.TargetSymbol is INamedTypeSymbol type
                    ? AuthoredModel.Read(type)
                    : null)
            .Where(static x => x is not null)
            .Collect();

        // The namespace is the project's own, never a literal: this generator runs inside
        // Paradise.Export and inside every game that declares authored data, and a hardcoded
        // namespace would collide the moment two of them are loaded together.
        //
        // RootNamespace first, because that is where a project's hand-written code lives and
        // generated PUBLIC API belongs beside it. Assembly name only as a fallback: a project named
        // Game.Core with RootNamespace `Game` would otherwise publish `Game.Core.AuthoringSchema`,
        // which no file in it can see without qualifying.
        var namespaceName = context.AnalyzerConfigOptionsProvider
            .Combine(context.CompilationProvider)
            .Select(static (pair, _) =>
            {
                pair.Left.GlobalOptions.TryGetValue("build_property.RootNamespace", out var root);
                return Sanitize(string.IsNullOrWhiteSpace(root) ? pair.Right.AssemblyName : root);
            });

        context.RegisterSourceOutput(
            authored.Combine(namespaceName),
            static (ctx, pair) => Emit(ctx, pair.Left!, pair.Right));
    }

    /// <summary>An assembly name is not necessarily a legal namespace (it may contain dashes, or
    /// start with a digit). Fix it up rather than emitting source that will not compile.</summary>
    private static string Sanitize(string? assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            return "Paradise.Authoring.Generated";
        }

        var builder = new StringBuilder(assemblyName!.Length);
        foreach (var c in assemblyName)
        {
            builder.Append(char.IsLetterOrDigit(c) || c == '_' || c == '.' ? c : '_');
        }
        var result = builder.ToString();
        return char.IsDigit(result[0]) ? "_" + result : result;
    }

    private static void Emit(
        SourceProductionContext context, ImmutableArray<AuthoredType?> types, string namespaceName)
    {
        var present = types.Where(t => t is not null).Select(t => t!)
            // Ordered so the schema is stable: an unordered generator makes every rebuild a diff.
            .OrderBy(t => t.ComponentId, System.StringComparer.Ordinal)
            .ToList();
        if (present.Count == 0)
        {
            return;
        }

        var json = new StringBuilder();
        json.Append("{\"version\":1,\"components\":[");
        for (var i = 0; i < present.Count; i++)
        {
            var type = present[i];
            if (i > 0) json.Append(',');
            json.Append("{\"id\":").Append(Quote(type.ComponentId));
            json.Append(",\"displayName\":").Append(Quote(type.DisplayName));
            if (type.BoxGizmo is { } box)
            {
                // How the component LOOKS while you edit it, declared with everything else - so an
                // editor needs no per-component class to draw it.
                json.Append(",\"gizmo\":{\"kind\":\"box\",\"halfExtentX\":").Append(Quote(box[0]))
                    .Append(",\"halfExtentZ\":").Append(Quote(box[1]))
                    .Append(",\"depth\":").Append(Quote(box[2])).Append('}');
            }
            json.Append(",\"fields\":");
            AppendFields(json, type.Fields);
            json.Append('}');
        }
        json.Append("]}");

        var source = new StringBuilder();
        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable enable");
        source.AppendLine();
        source.Append("namespace ").Append(namespaceName).AppendLine(";");
        source.AppendLine();
        source.AppendLine("/// <summary>The authored-data schema for this assembly, for editors that build their UI");
        source.AppendLine("/// from data rather than from generated code. Generated from every [Authored] type;");
        source.AppendLine("/// do not edit. Parse it with Paradise.Authoring.AuthoringSchemaReader.</summary>");
        source.AppendLine("public static class AuthoringSchema");
        source.AppendLine("{");
        source.AppendLine("    /// <summary>The schema document. Stable across rebuilds: components are ordered by id.</summary>");
        // A VERBATIM literal, so the JSON is escaped exactly once (for JSON) and then only quotes
        // are doubled (for C#). Nesting two escape passes over the same text is how a generator
        // ends up emitting a document that parses locally and not on the other side.
        source.Append("    public const string Json = @\"").Append(json.ToString().Replace("\"", "\"\"")).AppendLine("\";");
        source.AppendLine("}");

        context.AddSource("AuthoringSchema.g.cs", source.ToString());
    }

    /// <summary>Fields, recursively: a composed field carries its own "fields" array, so an
    /// editor renders it as a nested group without knowing anything about the type inside.</summary>
    private static void AppendFields(StringBuilder json, List<AuthoredField> fields)
    {
        json.Append('[');
        for (var f = 0; f < fields.Count; f++)
        {
            var field = fields[f];
            if (f > 0) json.Append(',');
            json.Append("{\"name\":").Append(Quote(field.Name));
            json.Append(",\"type\":").Append(Quote(field.SchemaType));
            if (field.Unit is not null) json.Append(",\"unit\":").Append(Quote(field.Unit));
            if (field.Doc is not null) json.Append(",\"doc\":").Append(Quote(field.Doc));
            if (field.Minimum is { } min) json.Append(",\"minimum\":").Append(Number(min));
            if (field.Maximum is { } max) json.Append(",\"maximum\":").Append(Number(max));
            if (DefaultAsJson(field.Default, field.SchemaType) is { } literal)
            {
                json.Append(",\"default\":").Append(literal);
            }
            if (field.EnumValues is { Count: > 0 } values)
            {
                json.Append(",\"values\":[");
                for (var v = 0; v < values.Count; v++)
                {
                    if (v > 0) json.Append(',');
                    json.Append(Quote(values[v]));
                }
                json.Append(']');
            }
            if (field.NativeShape)
            {
                // Authored by pointing at the host's own shape object; the nested fields below are
                // what gets BAKED out of it at export time.
                json.Append(",\"authoredBy\":\"nativeShape\"");
            }
            if (field.Nested is { } nested)
            {
                json.Append(",\"fields\":");
                AppendFields(json, nested);
            }
            json.Append('}');
        }
        json.Append(']');
    }

    /// <summary>
    /// The property's default as a JSON VALUE, not as the C# text it was written in.
    ///
    /// `9f` is a perfectly good C# default and meaningless to a Python or TypeScript editor, which
    /// is the whole audience for this document — so the numeric suffix is stripped and the value
    /// emitted as a real JSON number. Anything that does not parse is DROPPED rather than published
    /// as a string an editor would have to guess at.
    /// </summary>
    private static string? DefaultAsJson(string? csharpLiteral, string schemaType)
    {
        if (csharpLiteral is null)
        {
            return null;
        }

        var text = csharpLiteral.Trim();

        // An enum default is written as `PhysicsBodyType.Static` in C# and travels as the bare
        // member NAME, matching how the export contract serializes enums.
        if (schemaType == "enum")
        {
            var dot = text.LastIndexOf('.');
            var name = dot >= 0 ? text.Substring(dot + 1) : text;
            return name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') ? Quote(name) : null;
        }

        if (text == "true" || text == "false")
        {
            return text;
        }
        if (text.Length > 1 && text[0] == '"' && text[text.Length - 1] == '"')
        {
            return text; // already a JSON-compatible string literal
        }

        var trimmed = text.TrimEnd('f', 'F', 'd', 'D', 'm', 'M', 'l', 'L');
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return Number(number);
        }
        return null;
    }

    /// <summary>A JSON string literal, escaped for JSON only.</summary>
    private static string Quote(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");
        return "\"" + escaped + "\"";
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
