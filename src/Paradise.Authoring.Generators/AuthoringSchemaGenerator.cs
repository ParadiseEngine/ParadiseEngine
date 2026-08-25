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
    // The identity diagnostics live HERE rather than on the registry generator because this one
    // runs for every assembly declaring [Authored] types. The registry is opt-in, and a
    // schema-only assembly (Paradise.Export is one) would otherwise publish a component with no
    // identity, or two sharing one, and never hear a word about it.

    /// <summary>
    /// PAUT005: <c>[Authored]</c> without a usable <c>[Guid]</c> beside it.
    ///
    /// The attribute pair IS the declaration — <c>[Authored]</c> says "a human edits this" and
    /// <c>[Guid]</c> says which component it is — so a type carrying only the first has no
    /// identity, and every payload it ever produced would resolve to nothing.
    /// </summary>
    public static readonly DiagnosticDescriptor IdNotAGuid = new(
        id: "PAUT005",
        title: "Authored type needs a [Guid] beside it",
        messageFormat: "'{0}' is [Authored] but {1}",
        category: "Paradise.Authoring",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "An authored component is identified by the GUID in its "
            + "System.Runtime.InteropServices.GuidAttribute, so that renaming the component, or the "
            + "type behind it, cannot orphan documents that already author it. Add [Guid(\"...\")] "
            + "to the type. Any form Guid.Parse accepts will do; the canonical 8-4-4-4-12 spelling "
            + "is what the generator emits. Generate a fresh GUID rather than reusing another "
            + "component's.");

    /// <summary>
    /// PAUT006: two <c>[Authored]</c> types in one assembly share an id.
    ///
    /// Almost always a copy-paste, and silent without this: the component that loses the race is
    /// dropped from the schema and unreachable in the registry, so half the payloads materialize
    /// as the wrong record.
    /// </summary>
    public static readonly DiagnosticDescriptor DuplicateId = new(
        id: "PAUT006",
        title: "Two authored types share an id",
        messageFormat: "'{0}' and '{1}' are both [Guid(\"{2}\")]; an id identifies exactly one component",
        category: "Paradise.Authoring",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Authored ids are looked up in one table per assembly, so a duplicate makes "
            + "the component that loses unreachable. Give the newer type a freshly generated GUID.");

    /// <summary>
    /// PAUT007: a referenced assembly publishes a schema this build cannot read.
    ///
    /// Only reachable with reference scanning on. The version is the document's whole
    /// compatibility story, so a mismatch means the two assemblies were built against different
    /// engines — merging half of it would publish components whose fields this build would
    /// describe wrongly, which is worse than leaving them out and saying so.
    /// </summary>
    public static readonly DiagnosticDescriptor ReferenceVersionUnsupported = new(
        id: "PAUT007",
        title: "Referenced assembly publishes an unreadable authoring schema",
        messageFormat: "'{0}' publishes an authoring schema at version {1}; this build reads version {2}, so its components are not merged",
        category: "Paradise.Authoring",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ParadiseAuthoringScanReferences merges the schema documents referenced "
            + "assemblies already published. A document at another version describes its fields by "
            + "another set of rules, so it is skipped rather than half-read. Rebuild that assembly "
            + "against this engine version.");

    /// <summary>
    /// PAUT008: two of the merged assemblies claim one component id.
    ///
    /// The cross-assembly twin of PAUT006, and a warning rather than an error because neither
    /// declaration is necessarily this project's to fix.
    ///
    /// <b>The REFERENCE wins, including against this project's own declaration.</b> That is the
    /// rule <c>AuthoringSchemaReader.Merge</c> and the editors already apply — first source wins,
    /// and every host passes the engine's document first — mapped onto the only ordering a
    /// compilation has. The engine is always a reference here and the game is always local, so
    /// resolving it the other way would make this document disagree with every consumer that
    /// loads it about what component X is.
    ///
    /// Reported only when the two declarations actually DIFFER. A project between this one and the
    /// declaring assembly may also scan references, in which case the same component arrives twice
    /// with identical text; that is a re-merge, not a conflict.
    /// </summary>
    public static readonly DiagnosticDescriptor DuplicateIdAcrossAssemblies = new(
        id: "PAUT008",
        title: "Two assemblies publish the same authored id",
        messageFormat: "'{0}' and '{1}' both publish component id {2}; the referenced '{0}' wins and the other is dropped from the merged schema",
        category: "Paradise.Authoring",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An authored id identifies exactly one component, across every assembly an "
            + "editor loads together. The referenced declaration wins, matching the first-wins "
            + "order every host and editor merges with, so a project cannot shadow a component an "
            + "assembly it references already publishes. Give the newer component a freshly "
            + "generated GUID rather than reusing one that is already published.");

    /// <summary>
    /// PAUT009: a referenced document carries a component this build cannot address.
    ///
    /// Only reachable from a hand-written <c>AuthoringSchema</c> constant — the generator drops an
    /// id-less type at PAUT005, long before emission — but a component silently missing from a
    /// merged schema is the exact failure this feature exists to remove, so it is named.
    /// </summary>
    public static readonly DiagnosticDescriptor ReferenceComponentHasNoId = new(
        id: "PAUT009",
        title: "Referenced authoring schema has a component with no id",
        messageFormat: "'{0}' publishes a component ({1}) with no readable id; it is dropped from the merged schema",
        category: "Paradise.Authoring",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Components are merged by id, so one without an id cannot be placed. A "
            + "document this generator produced never contains one; a hand-written AuthoringSchema "
            + "constant can. Give the component an id or remove it.");

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

        // What referenced assemblies already published, when this project asked for an aggregate.
        // Gated INSIDE the select rather than by not building the provider, because an incremental
        // generator's pipeline shape is fixed at Initialize: the property is read per compilation
        // like any other, and a project that never opts in pays one bool comparison.
        var referenced = context.CompilationProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, cancellation) =>
            {
                pair.Right.GlobalOptions.TryGetValue(
                    "build_property.ParadiseAuthoringScanReferences", out var scan);
                return string.Equals(scan, "true", System.StringComparison.OrdinalIgnoreCase)
                    ? ReferencedSchemas.Read(pair.Left, cancellation)
                    : ImmutableArray<ReferencedSchema>.Empty;
            })
            .WithComparer(ReferencedSchemaComparer.Instance);

        context.RegisterSourceOutput(
            authored.Combine(namespaceName).Combine(referenced),
            static (ctx, pair) => Emit(ctx, pair.Left.Left!, pair.Left.Right, pair.Right));
    }

    /// <summary>An assembly name is not necessarily a legal namespace (it may contain dashes, or
    /// start with a digit). Fix it up rather than emitting source that will not compile.</summary>
    internal static string Sanitize(string? assemblyName)
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

    /// <summary>The version this generator writes, and therefore the only one it will merge a
    /// referenced document from. Must track AuthoringSchemaDocument.CurrentVersion — not
    /// referenced, because this analyzer targets netstandard2.0 and deliberately does not link the
    /// runtime package it feeds.</summary>
    private const int SchemaVersion = 3;

    private static void Emit(
        SourceProductionContext context,
        ImmutableArray<AuthoredType?> types,
        string namespaceName,
        ImmutableArray<ReferencedSchema> referenced)
    {
        var candidates = types.Where(t => t is not null).Select(t => t!)
            // Ordered so the schema is stable: an unordered generator makes every rebuild a diff.
            // By type name rather than by id, so the document a human reviews is in an order they
            // can predict and a regenerated GUID does not reshuffle it. It also fixes WHICH of two
            // types sharing an id is reported as the duplicate, rather than leaving it to
            // whichever order the compiler happened to hand them over in.
            .OrderBy(t => t.TypeName, System.StringComparer.Ordinal)
            .ToList();

        var present = new List<AuthoredType>();
        var claimed = new Dictionary<string, AuthoredType>(System.StringComparer.Ordinal);
        foreach (var type in candidates)
        {
            if (type.IdUnusable)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    IdNotAGuid, type.Declaration, ShortName(type.TypeName),
                    type.IdMissing
                        ? "has no [System.Runtime.InteropServices.Guid] attribute to identify it"
                        : "its [Guid(\"" + type.DeclaredId + "\")] is not a GUID"));
                continue;
            }
            if (claimed.TryGetValue(type.ComponentId, out var owner))
            {
                // Both named in FULL, unlike the single-type diagnostics above. A collision is a
                // comparison, and the reader needs to see how the two differ — which short names
                // hide exactly when it matters most, since two types that collide on an id are
                // often a copy-paste and therefore share a short name too. Only one of the pair
                // gets the squiggle; the other is reachable only through this text.
                context.ReportDiagnostic(Diagnostic.Create(
                    DuplicateId, type.Declaration,
                    owner.TypeName, type.TypeName, type.ComponentId));
                continue;
            }
            claimed.Add(type.ComponentId, type);
            present.Add(type);
        }

        // THE MERGE, REFERENCES FIRST. A referenced assembly's declaration wins an id collision
        // against this project's own, and that order is the whole point rather than an accident of
        // how the loops were written.
        //
        // It is the rule every other merge in the system already applies:
        // AuthoringSchemaReader.Merge is first-argument-wins, the hosts pass the ENGINE's document
        // first, and the Blender addon's merge() does the same. The engine is always a reference
        // here and the game is always local, so seeding from local would resolve the one collision
        // that matters — a game copying an engine component's id — the opposite way from every
        // consumer that loads the result. An editor reading this dumped document would then
        // describe component X by the game's fields while the exporter kept baking the engine's,
        // which is precisely the drift the dump exists to prevent.
        //
        // The cost is that a local declaration can lose to a referenced one, which reads as
        // surprising until you notice the only way to hit it is to duplicate an id that is already
        // published — PAUT008, right there in the build log, with both assemblies named.
        //
        // Among references the order is by assembly name (ReferencedSchemas.Read sorts), so a
        // reference-vs-reference collision resolves deterministically rather than by whatever
        // order the compiler handed them over in.
        var merged = new List<(string Id, string TypeName, string Element)>();
        var owners = new Dictionary<string, string>(System.StringComparer.Ordinal);
        var elements = new Dictionary<string, string>(System.StringComparer.Ordinal);

        foreach (var schema in referenced)
        {
            var version = ReferencedSchemas.Version(schema.Json);
            if (version != SchemaVersion)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    ReferenceVersionUnsupported, Location.None,
                    schema.Assembly,
                    version?.ToString(CultureInfo.InvariantCulture) ?? "an unstated version",
                    SchemaVersion));
                continue;
            }

            foreach (var (id, typeName, element) in ReferencedSchemas.Components(schema.Json))
            {
                if (id.Length == 0)
                {
                    // A component this build cannot address. Only reachable from a hand-written
                    // constant — the generator excludes an id-less type before emitting — but
                    // dropping it silently is the failure mode this whole feature exists to
                    // remove, so it is named rather than skipped.
                    context.ReportDiagnostic(Diagnostic.Create(
                        ReferenceComponentHasNoId, Location.None, schema.Assembly, typeName));
                    continue;
                }
                if (owners.TryGetValue(id, out var owner))
                {
                    // An id can legitimately arrive twice: a project between this one and the
                    // declaring assembly may ALSO scan references, so its aggregate and the
                    // original both carry the component. Identical text is a re-merge, not a
                    // conflict — reporting it would fire once per shared component and break any
                    // build with TreatWarningsAsErrors, for a document that is the same either way.
                    if (!string.Equals(elements[id], element, System.StringComparison.Ordinal))
                    {
                        // Location.None: both declarations are in other assemblies, and pointing at
                        // this project's syntax would blame a file that is not the problem.
                        context.ReportDiagnostic(Diagnostic.Create(
                            DuplicateIdAcrossAssemblies, Location.None, owner, schema.Assembly, id));
                    }
                    continue;
                }
                owners.Add(id, schema.Assembly);
                elements.Add(id, element);
                merged.Add((id, typeName, element));
            }
        }

        foreach (var type in present)
        {
            if (owners.TryGetValue(type.ComponentId, out var owner))
            {
                // The local type is the one being dropped, so this diagnostic CAN point at real
                // syntax — and should: it is the declaration the author can actually change.
                context.ReportDiagnostic(Diagnostic.Create(
                    DuplicateIdAcrossAssemblies, type.Declaration,
                    owner, namespaceName, type.ComponentId));
                continue;
            }
            owners.Add(type.ComponentId, namespaceName);
            merged.Add((type.ComponentId, type.TypeName, ComponentJson(type)));
        }

        if (merged.Count == 0)
        {
            return;
        }

        // Ordered by type name — and by id after it, which only a merge can need: two assemblies
        // CAN publish one type name where one assembly cannot, and an unstable order would make
        // every rebuild a diff.
        merged.Sort(static (a, b) =>
        {
            var byType = System.StringComparer.Ordinal.Compare(a.TypeName, b.TypeName);
            return byType != 0 ? byType : System.StringComparer.Ordinal.Compare(a.Id, b.Id);
        });

        var json = new StringBuilder();
        json.Append("{\"version\":").Append(SchemaVersion).Append(",\"components\":[");
        for (var i = 0; i < merged.Count; i++)
        {
            if (i > 0) json.Append(',');
            json.Append(merged[i].Element);
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
        source.AppendLine("    /// <summary>The schema document. Stable across rebuilds: components are ordered by type name.</summary>");
        // A VERBATIM literal, so the JSON is escaped exactly once (for JSON) and then only quotes
        // are doubled (for C#). Nesting two escape passes over the same text is how a generator
        // ends up emitting a document that parses locally and not on the other side.
        source.Append("    public const string Json = @\"").Append(json.ToString().Replace("\"", "\"\"")).AppendLine("\";");
        source.AppendLine("}");

        context.AddSource("AuthoringSchema.g.cs", source.ToString());
    }

    /// <summary>One component object, as it appears in the document's <c>components</c> array.
    ///
    /// Split out from the emission so a LOCAL component and a component merged in from a
    /// referenced assembly are the same kind of thing to the code that orders and joins them: a
    /// piece of text with an id and a type name. It also states, by existing, that a referenced
    /// component is re-published verbatim rather than reformatted — the two paths meet only as
    /// strings, so there is no second spelling of this document to keep in step.</summary>
    private static string ComponentJson(AuthoredType type)
    {
        var json = new StringBuilder();
        json.Append("{\"id\":").Append(Quote(type.ComponentId));
        // The fallback key, and the only thing in this document that tells a human which
        // component the GUID above belongs to.
        json.Append(",\"type\":").Append(Quote(type.TypeName));
        json.Append(",\"displayName\":").Append(Quote(type.DisplayName));
        if (type.AuthoredBy is { } componentSource)
        {
            json.Append(",\"authoredBy\":").Append(Quote(componentSource));
        }
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
        return json.ToString();
    }

    /// <summary>Fields, recursively: a composed field carries its own "fields" array, so an
    /// editor renders it as a nested group without knowing anything about the type inside.</summary>
    private static void AppendFields(StringBuilder json, List<AuthoredField> fields)
    {
        json.Append('[');
        for (var f = 0; f < fields.Count; f++)
        {
            if (f > 0) json.Append(',');
            AppendField(json, fields[f]);
        }
        json.Append(']');
    }

    /// <summary>One field object. Split out from <see cref="AppendFields"/> because an array's
    /// element is a single field object, not a one-element list — an editor reads
    /// <c>items.type</c>, never <c>items[0].type</c>.</summary>
    private static void AppendField(StringBuilder json, AuthoredField field)
    {
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
        if (field.AuthoredBy is { } source)
        {
            // Authored by pointing at one of the host's own objects; any nested fields below are
            // what gets BAKED out of it at export time.
            json.Append(",\"authoredBy\":").Append(Quote(source));
        }
        if (field.AssetKinds is { Count: > 0 } kinds)
        {
            json.Append(",\"assetKinds\":[");
            for (var k = 0; k < kinds.Count; k++)
            {
                if (k > 0) json.Append(',');
                json.Append(Quote(kinds[k]));
            }
            json.Append(']');
        }
        if (field.VisibleWhenField is { } guard && field.VisibleWhenValue is { } guardValue)
        {
            json.Append(",\"visibleWhen\":{\"field\":").Append(Quote(guard))
                .Append(",\"equals\":").Append(guardValue).Append('}');
        }
        if (field.Nested is { } nested)
        {
            json.Append(",\"fields\":");
            AppendFields(json, nested);
        }
        if (field.Items is { } items)
        {
            json.Append(",\"items\":");
            AppendField(json, items);
        }
        json.Append('}');
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

    /// <summary>"Pingu.Core.PoolConfig" → "PoolConfig", for a diagnostic that reads naturally.</summary>
    private static string ShortName(string typeName)
    {
        var dot = typeName.LastIndexOf('.');
        return dot < 0 ? typeName : typeName.Substring(dot + 1);
    }
}
