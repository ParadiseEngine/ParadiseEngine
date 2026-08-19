using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Paradise.Authoring.Generators;

/// <summary>
/// Emits <c>AuthoredComponents</c>: the registry mapping component ids to the records they
/// deserialize into, with a generated reader per record. This is the LOADING half of
/// <c>[Authored]</c> — without it, filling an instance from an exported payload is a hand-written
/// accessor per component, and forgetting one means a component that authors, exports, and is then
/// silently never read.
///
/// The readers parse <see cref="System.Text.Json.JsonElement"/> directly rather than delegating to
/// a <c>JsonSerializerContext</c>. Delegating required every [Authored] record to ALSO be listed as
/// [JsonSerializable] on the game's context — a registration this generator could neither add (a
/// generator's output is invisible to System.Text.Json's generator; dotnet/roslyn#57239) nor
/// verify cheaply, and forgetting it failed the build with CS1061 inside the generated file.
/// Authored fields are a closed vocabulary the schema already enforces, so reading them directly
/// is a switch per field, and the whole registration ceremony — a context class, a
/// [JsonSerializable] line per record, an assembly attribute naming the context — is gone.
/// Emission is opted into with [assembly: AuthoredRegistry].
///
/// The wire contract matches what the Godot addon writes (AuthoredEntityCore.ValueOf): property
/// names are the schema's field names (compared case-insensitively, as the previous
/// PropertyNameCaseInsensitive contexts did), composed groups are nested objects, enums are their
/// integer values, Vector2/3 and Quaternion are float arrays, and Vector4 and Color32 author as a
/// color and travel as the {r,g,b,a} object. A property absent from the payload keeps the record's
/// own initializer — the constructor has already run.
/// </summary>
[Generator]
public sealed class AuthoredRegistryGenerator : IIncrementalGenerator
{
    /// <summary>
    /// PAUT002: the reader must construct the record, and cannot.
    /// </summary>
    public static readonly DiagnosticDescriptor NotConstructible = new(
        id: "PAUT002",
        title: "Authored type needs a public parameterless constructor",
        messageFormat: "'{0}' is [Authored] but has no public parameterless constructor, so a reader cannot be generated for it",
        category: "Paradise.Authoring",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generated reader materializes an authored record by constructing it and then "
            + "assigning each property present in the payload. A positional record or a type with only "
            + "parameterized constructors cannot be built that way; give it a parameterless constructor "
            + "and property initializers.");

    /// <summary>
    /// PAUT003: a property the reader cannot assign.
    /// </summary>
    public static readonly DiagnosticDescriptor NotAssignable = new(
        id: "PAUT003",
        title: "Authored property cannot be assigned by the generated reader",
        messageFormat: "'{0}.{1}' cannot be assigned by the generated authored reader because {2}",
        category: "Paradise.Authoring",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The reader constructs the record first and assigns properties as it walks the "
            + "payload, so every authored property needs a plain setter: init-only members cannot be "
            + "assigned after construction, and a required member makes the construction itself a "
            + "compile error.");

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

        var settings = context.CompilationProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, _) =>
            {
                pair.Right.GlobalOptions.TryGetValue("build_property.RootNamespace", out var root);
                var ns = string.IsNullOrWhiteSpace(root) ? pair.Left.AssemblyName : root;

                // The registry is opt-in: it is public surface, and an assembly that only
                // publishes a schema for editors (Paradise.Export itself is one) declares
                // [Authored] types with no business shipping a loader for them.
                var optedIn = false;
                foreach (var attribute in pair.Left.Assembly.GetAttributes())
                {
                    if (attribute.AttributeClass?.ToDisplayString()
                        == "Paradise.Authoring.AuthoredRegistryAttribute")
                    {
                        optedIn = true;
                    }
                }
                return (Namespace: AuthoringSchemaGenerator.Sanitize(ns), OptedIn: optedIn);
            });

        context.RegisterSourceOutput(
            authored.Combine(settings),
            static (ctx, pair) =>
            {
                if (pair.Right.OptedIn)
                {
                    Emit(ctx, pair.Left!, pair.Right.Namespace);
                }
            });
    }

    private static void Emit(
        SourceProductionContext context,
        ImmutableArray<AuthoredType?> types,
        string namespaceName)
    {
        var present = new List<AuthoredType>();
        foreach (var type in types.Where(t => t is not null).Select(t => t!)
                     .OrderBy(t => t.ComponentId, System.StringComparer.Ordinal))
        {
            // Report and SKIP the whole component: a reader for half a record would read the other
            // half silently wrong, and the emitted code would not compile anyway.
            if (Validate(context, type))
            {
                present.Add(type);
            }
        }
        if (present.Count == 0)
        {
            return;
        }

        var source = new StringBuilder();
        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable enable");
        source.AppendLine();
        source.Append("namespace ").Append(namespaceName).AppendLine(";");
        source.AppendLine();
        source.AppendLine("/// <summary>Materializes this assembly's [Authored] records from exported payloads.");
        source.AppendLine("/// Generated from every [Authored] type; do not edit.</summary>");
        source.AppendLine("public sealed class AuthoredComponents : global::Paradise.Authoring.IAuthoredComponentRegistry");
        source.AppendLine("{");
        source.AppendLine("    /// <summary>A shared instance: the registry is stateless.</summary>");
        source.AppendLine("    public static readonly AuthoredComponents Default = new();");
        source.AppendLine();
        source.AppendLine("    private static readonly string[] Ids =");
        source.AppendLine("    [");
        foreach (var type in present)
        {
            source.Append("        ").Append(Quote(type.ComponentId)).AppendLine(",");
        }
        source.AppendLine("    ];");
        source.AppendLine();
        source.AppendLine("    public global::System.Collections.Generic.IReadOnlyCollection<string> ComponentIds => Ids;");
        source.AppendLine();
        source.AppendLine("    public bool TryRead(string componentId, global::System.Text.Json.JsonElement data, out object? component)");
        source.AppendLine("    {");
        source.AppendLine("        switch (componentId)");
        source.AppendLine("        {");
        foreach (var type in present)
        {
            source.Append("            case ").Append(Quote(type.ComponentId)).AppendLine(":");
            source.Append("                component = ").Append(ReaderName(type.FullTypeName)).AppendLine("(data);");
            source.AppendLine("                return true;");
        }
        source.AppendLine("            default:");
        source.AppendLine("                component = null;");
        source.AppendLine("                return false;");
        source.AppendLine("        }");
        source.AppendLine("    }");

        // One reader per record, plus one per composed type reached from any record. Deduped by
        // type: two components sharing a BoxColliderConfig share its reader.
        var emitted = new HashSet<string>(System.StringComparer.Ordinal);
        var helpers = new HelperSet();
        foreach (var type in present)
        {
            EmitReader(source, type.FullTypeName, type.Fields, emitted, helpers);
        }
        helpers.Emit(source);

        source.AppendLine("}");
        context.AddSource("AuthoredComponents.g.cs", source.ToString());
    }

    /// <summary>True when a reader can be generated; reports why not otherwise.</summary>
    private static bool Validate(SourceProductionContext context, AuthoredType type)
    {
        bool ok = true;
        if (!type.Constructible)
        {
            context.ReportDiagnostic(Diagnostic.Create(NotConstructible, type.Declaration, ShortName(type.FullTypeName)));
            ok = false;
        }
        ok &= ValidateFields(context, type, ShortName(type.FullTypeName), type.Fields);
        return ok;
    }

    private static bool ValidateFields(
        SourceProductionContext context, AuthoredType type, string owner, List<AuthoredField> fields)
    {
        bool ok = true;
        foreach (var field in fields)
        {
            if (field.Required)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    NotAssignable, type.Declaration, owner, field.Name, "it is required"));
                ok = false;
            }
            else if (!field.Settable)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    NotAssignable, type.Declaration, owner, field.Name, "it is init-only"));
                ok = false;
            }

            var value = field.Items ?? field;
            if (value.Nested is not null)
            {
                if (!value.NestedConstructible)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        NotConstructible, type.Declaration, ShortName(value.ClrType)));
                    ok = false;
                }
                ok &= ValidateFields(context, type, ShortName(value.ClrType), value.Nested);
            }
        }
        return ok;
    }

    private static void EmitReader(
        StringBuilder source, string clrType, List<AuthoredField> fields,
        HashSet<string> emitted, HelperSet helpers)
    {
        if (!emitted.Add(clrType))
        {
            return;
        }

        source.AppendLine();
        source.Append("    private static ").Append(clrType).Append(' ')
              .Append(ReaderName(clrType)).AppendLine("(global::System.Text.Json.JsonElement json)");
        source.AppendLine("    {");
        source.Append("        var value = new ").Append(clrType).AppendLine("();");
        source.AppendLine("        foreach (var property in json.EnumerateObject())");
        source.AppendLine("        {");
        var first = true;
        foreach (var field in fields)
        {
            source.Append("            ").Append(first ? "if" : "else if")
                  .Append(" (string.Equals(property.Name, ").Append(Quote(field.Name))
                  .AppendLine(", global::System.StringComparison.OrdinalIgnoreCase))");
            source.AppendLine("            {");
            EmitAssignment(source, field, helpers);
            source.AppendLine("            }");
            first = false;
        }
        source.AppendLine("        }");
        source.AppendLine("        return value;");
        source.AppendLine("    }");

        // Composed types reached from here get their own reader.
        foreach (var field in fields)
        {
            var value = field.Items ?? field;
            if (value.Nested is not null)
            {
                EmitReader(source, value.ClrType, value.Nested, emitted, helpers);
            }
        }
    }

    private static void EmitAssignment(StringBuilder source, AuthoredField field, HelperSet helpers)
    {
        if (field.Items is { } element)
        {
            source.Append("                var items = new global::System.Collections.Generic.List<")
                  .Append(element.ClrType).AppendLine(">();");
            source.AppendLine("                foreach (var element in property.Value.EnumerateArray())");
            source.AppendLine("                {");
            source.Append("                    items.Add(").Append(ReadExpression(element, "element", helpers)).AppendLine(");");
            source.AppendLine("                }");
            source.Append("                value.").Append(field.Name).Append(" = items")
                  .AppendLine(field.ListKind == "array" ? ".ToArray();" : ";");
            return;
        }

        source.Append("                value.").Append(field.Name).Append(" = ")
              .Append(ReadExpression(field, "property.Value", helpers)).AppendLine(";");
    }

    /// <summary>The expression reading one value of the field's kind from a JsonElement.</summary>
    private static string ReadExpression(AuthoredField field, string element, HelperSet helpers)
    {
        if (field.Nested is not null)
        {
            return ReaderName(field.ClrType) + "(" + element + ")";
        }
        switch (field.ClrKind)
        {
            case "float": return element + ".GetSingle()";
            case "double": return element + ".GetDouble()";
            case "int": return element + ".GetInt32()";
            case "long": return element + ".GetInt64()";
            case "uint": return element + ".GetUInt32()";
            case "ulong": return element + ".GetUInt64()";
            case "bool": return element + ".GetBoolean()";
            // GetString is null for a JSON null, which is what the addon writes for "no value".
            case "string": return element + ".GetString()!";
            // The addon writes an enum's integer value; long-to-enum is an explicit conversion.
            case "enum": return "(" + field.ClrType + ")" + element + ".GetInt64()";
            case "vector2": helpers.Vector2 = true; return "ReadVector2(" + element + ")";
            case "vector3": helpers.Vector3 = true; return "ReadVector3(" + element + ")";
            case "quaternion": helpers.Quaternion = true; return "ReadQuaternion(" + element + ")";
            case "vector4color": helpers.Vector4Color = true; return "ReadColorVector4(" + element + ")";
            case "color32": helpers.Color32 = true; return "ReadColor32(" + element + ")";
            default:
                // Unreachable while the schema and the reader agree on the vocabulary: a type
                // neither recognizes never became a field at all.
                return "default!";
        }
    }

    /// <summary>Shared readers for the multi-float shapes, emitted once and only when used.</summary>
    private sealed class HelperSet
    {
        public bool Vector2;
        public bool Vector3;
        public bool Quaternion;
        public bool Vector4Color;
        public bool Color32;

        public void Emit(StringBuilder source)
        {
            if (Vector2 || Vector3 || Quaternion)
            {
                source.AppendLine();
                source.AppendLine("    /// <summary>The addon writes vectors as float arrays.</summary>");
                source.AppendLine("    private static float[] ReadFloats(global::System.Text.Json.JsonElement json, int count)");
                source.AppendLine("    {");
                source.AppendLine("        var values = new float[count];");
                source.AppendLine("        int i = 0;");
                source.AppendLine("        foreach (var element in json.EnumerateArray())");
                source.AppendLine("        {");
                source.AppendLine("            if (i >= count) { break; }");
                source.AppendLine("            values[i++] = element.GetSingle();");
                source.AppendLine("        }");
                source.AppendLine("        return values;");
                source.AppendLine("    }");
            }
            if (Vector2)
            {
                source.AppendLine();
                source.AppendLine("    private static global::System.Numerics.Vector2 ReadVector2(global::System.Text.Json.JsonElement json)");
                source.AppendLine("    {");
                source.AppendLine("        var v = ReadFloats(json, 2);");
                source.AppendLine("        return new global::System.Numerics.Vector2(v[0], v[1]);");
                source.AppendLine("    }");
            }
            if (Vector3)
            {
                source.AppendLine();
                source.AppendLine("    private static global::System.Numerics.Vector3 ReadVector3(global::System.Text.Json.JsonElement json)");
                source.AppendLine("    {");
                source.AppendLine("        var v = ReadFloats(json, 3);");
                source.AppendLine("        return new global::System.Numerics.Vector3(v[0], v[1], v[2]);");
                source.AppendLine("    }");
            }
            if (Quaternion)
            {
                source.AppendLine();
                source.AppendLine("    private static global::System.Numerics.Quaternion ReadQuaternion(global::System.Text.Json.JsonElement json)");
                source.AppendLine("    {");
                source.AppendLine("        var v = ReadFloats(json, 4);");
                source.AppendLine("        return new global::System.Numerics.Quaternion(v[0], v[1], v[2], v[3]);");
                source.AppendLine("    }");
            }
            if (Vector4Color || Color32)
            {
                source.AppendLine();
                source.AppendLine("    /// <summary>The addon writes colors as {r,g,b,a} floats — see Color32Converter.</summary>");
                source.AppendLine("    private static (float R, float G, float B, float A) ReadRgba(global::System.Text.Json.JsonElement json)");
                source.AppendLine("    {");
                source.AppendLine("        float r = 0f, g = 0f, b = 0f, a = 1f;");
                source.AppendLine("        foreach (var property in json.EnumerateObject())");
                source.AppendLine("        {");
                source.AppendLine("            switch (property.Name)");
                source.AppendLine("            {");
                source.AppendLine("                case \"r\": r = property.Value.GetSingle(); break;");
                source.AppendLine("                case \"g\": g = property.Value.GetSingle(); break;");
                source.AppendLine("                case \"b\": b = property.Value.GetSingle(); break;");
                source.AppendLine("                case \"a\": a = property.Value.GetSingle(); break;");
                source.AppendLine("            }");
                source.AppendLine("        }");
                source.AppendLine("        return (r, g, b, a);");
                source.AppendLine("    }");
            }
            if (Vector4Color)
            {
                source.AppendLine();
                source.AppendLine("    private static global::System.Numerics.Vector4 ReadColorVector4(global::System.Text.Json.JsonElement json)");
                source.AppendLine("    {");
                source.AppendLine("        var (r, g, b, a) = ReadRgba(json);");
                source.AppendLine("        return new global::System.Numerics.Vector4(r, g, b, a);");
                source.AppendLine("    }");
            }
            if (Color32)
            {
                source.AppendLine();
                source.AppendLine("    private static global::Paradise.Export.Data.Color32 ReadColor32(global::System.Text.Json.JsonElement json)");
                source.AppendLine("    {");
                source.AppendLine("        var (r, g, b, a) = ReadRgba(json);");
                source.AppendLine("        return global::Paradise.Export.Data.Color32.FromRgba(r, g, b, a);");
                source.AppendLine("    }");
            }
        }
    }

    /// <summary>"global::Pingu.PoolConfig" → "Read_Pingu_PoolConfig".</summary>
    private static string ReaderName(string fullTypeName)
    {
        var name = fullTypeName.StartsWith("global::", System.StringComparison.Ordinal)
            ? fullTypeName.Substring("global::".Length)
            : fullTypeName;
        var builder = new StringBuilder("Read_", name.Length + 8);
        foreach (var c in name)
        {
            builder.Append(char.IsLetterOrDigit(c) ? c : '_');
        }
        return builder.ToString();
    }

    private static string ShortName(string fullTypeName)
    {
        var dot = fullTypeName.LastIndexOf('.');
        return dot < 0 ? fullTypeName : fullTypeName.Substring(dot + 1);
    }

    private static string Quote(string value) =>
        SymbolDisplay.FormatLiteral(value, quote: true);
}
