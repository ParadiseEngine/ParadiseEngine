using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Paradise.Authoring.Generators;

/// <summary>
/// Emits an <c>IAuthoredComponentRegistry</c> for the compiling assembly: component id → the record
/// it deserializes into.
///
/// This is the LOADING half of <c>[Authored]</c>. Without it, filling an instance from an exported
/// payload is a hand-written accessor per component, naming both the id and the JsonTypeInfo — and
/// forgetting one means a component that authors, exports, and is then silently never read.
///
/// Only emitted when the assembly declares <c>[AuthoredJsonContext]</c>, because the generated code
/// has to name a context: reflection is out (it breaks Godot's hot-reload, and does not trim), and
/// this generator cannot OBSERVE what System.Text.Json's generator emits. It can REFERENCE it —
/// generated sources compile together, so <c>Ctx.Default.Record</c> resolves even though neither
/// generator saw the other. That was verified with a throwaway spike before this existed, because
/// the opposite limitation is exactly what made generating Godot's inspector impossible.
/// </summary>
[Generator]
public sealed class AuthoredRegistryGenerator : IIncrementalGenerator
{
    private const string ContextAttribute = "Paradise.Authoring.AuthoredJsonContextAttribute";
    private const string SerializableAttribute = "System.Text.Json.Serialization.JsonSerializableAttribute";

    /// <summary>
    /// PAUT001: an [Authored] type the context does not serialize.
    ///
    /// Without this the generator emitted <c>Ctx.Default.Foo</c> for a property System.Text.Json
    /// never generated, and the build failed with CS1061 pointing INSIDE AuthoredComponents.g.cs —
    /// a generated file naming a generated member, which reads like a bug in the toolchain rather
    /// than a missing one-line attribute in the game's own source.
    ///
    /// It cannot be fixed by generating the registration: System.Text.Json's generator only sees
    /// the original compilation, and post-initialization output — the one channel visible to
    /// another generator — takes no compilation input by construction, so it cannot enumerate
    /// [Authored] types. The attribute has to be written by hand; this says so, in the right place.
    /// </summary>
    public static readonly DiagnosticDescriptor NotSerializable = new(
        id: "PAUT001",
        title: "Authored type is not registered for serialization",
        messageFormat: "'{0}' is [Authored] but '{1}' cannot serialize it. Add [JsonSerializable(typeof({0}))] to '{1}'.",
        category: "Paradise.Authoring",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every [Authored] type is deserialized through the JsonSerializerContext named by "
            + "[assembly: AuthoredJsonContext]. A type missing from that context has no generated "
            + "JsonTypeInfo, so the registry cannot read it back from an exported scene.");

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var authored = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AuthoredModel.AuthoredAttribute,
                predicate: static (node, _) => true,
                transform: static (ctx, _) => ctx.TargetSymbol is INamedTypeSymbol type
                    ? AuthoredModel.ReadIdentity(type)
                    : null)
            .Where(static x => x is not null)
            .Collect();

        var settings = context.CompilationProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, _) =>
            {
                pair.Right.GlobalOptions.TryGetValue("build_property.RootNamespace", out var root);
                var ns = string.IsNullOrWhiteSpace(root) ? pair.Left.AssemblyName : root;

                string? contextName = null;
                string? contextShortName = null;
                var serializable = new List<string>();
                foreach (var a in pair.Left.Assembly.GetAttributes())
                {
                    if (a.AttributeClass?.ToDisplayString() == ContextAttribute &&
                        a.ConstructorArguments.Length == 1 &&
                        a.ConstructorArguments[0].Value is INamedTypeSymbol type)
                    {
                        contextName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        contextShortName = type.Name;

                        // What the context can actually serialize. These are ordinary source
                        // attributes on the user's own class, so they ARE visible here — it is only
                        // System.Text.Json's generated OUTPUT that this generator cannot observe.
                        foreach (var registration in type.GetAttributes())
                        {
                            if (registration.AttributeClass?.ToDisplayString() == SerializableAttribute &&
                                registration.ConstructorArguments.Length > 0 &&
                                registration.ConstructorArguments[0].Value is INamedTypeSymbol registered)
                            {
                                serializable.Add(registered.Name);
                            }
                        }
                    }
                }
                serializable.Sort(System.StringComparer.Ordinal);
                return (
                    Namespace: AuthoringSchemaGenerator.Sanitize(ns),
                    Context: contextName,
                    ContextShortName: contextShortName,
                    // Joined rather than kept as a collection: the incremental pipeline compares
                    // this value, and a List would compare by reference and re-run every pass.
                    Serializable: string.Join(",", serializable));
            });

        context.RegisterSourceOutput(
            authored.Combine(settings),
            static (ctx, pair) => Emit(
                ctx,
                pair.Left!,
                pair.Right.Namespace,
                pair.Right.Context,
                pair.Right.ContextShortName,
                pair.Right.Serializable));
    }

    private static void Emit(
        SourceProductionContext context,
        ImmutableArray<AuthoredIdentity?> types,
        string namespaceName,
        string? contextName,
        string? contextShortName,
        string serializable)
    {
        if (contextName is null)
        {
            // No context declared: this assembly publishes a schema for editors and never loads
            // payloads back. Emitting a registry it cannot fill would be worse than none.
            return;
        }

        var registered = new HashSet<string>(
            serializable.Length == 0
                ? System.Array.Empty<string>()
                : serializable.Split(','),
            System.StringComparer.Ordinal);

        var present = new List<AuthoredIdentity>();
        foreach (var type in types.Where(t => t is not null).Select(t => t!)
                     .OrderBy(t => t.ComponentId, System.StringComparer.Ordinal))
        {
            if (registered.Contains(type.TypeName))
            {
                present.Add(type);
                continue;
            }

            // Report and SKIP. Emitting the case anyway would bury this under a CS1061 inside the
            // generated file, which is the confusing error this diagnostic exists to replace.
            context.ReportDiagnostic(Diagnostic.Create(
                NotSerializable,
                type.Declaration,
                type.TypeName,
                contextShortName ?? contextName));
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
            source.Append("                component = global::System.Text.Json.JsonSerializer.Deserialize(data, ")
                  .Append(contextName).Append(".Default.").Append(type.TypeName).AppendLine(");");
            source.AppendLine("                return true;");
        }
        source.AppendLine("            default:");
        source.AppendLine("                component = null;");
        source.AppendLine("                return false;");
        source.AppendLine("        }");
        source.AppendLine("    }");
        source.AppendLine("}");

        context.AddSource("AuthoredComponents.g.cs", source.ToString());
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
