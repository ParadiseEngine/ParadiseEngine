using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Paradise.Authoring.Generators;

/// <summary>One authored property, reduced to what an editor needs to draw a control for it.</summary>
internal sealed class AuthoredField
{
    public string Name = "";
    /// <summary>Editor-neutral kind: what the schema publishes. Mapped per editor.</summary>
    public string SchemaType = "";
    /// <summary>Semantic unit (meters/radians/seconds/kilograms/unit01), or null.</summary>
    public string? Unit;
    public string? Doc;
    public double? Minimum;
    public double? Maximum;
    /// <summary>The record's own initializer, reused verbatim as the editor's default so the two
    /// cannot disagree. Null when the property has none.</summary>
    public string? Default;
    /// <summary>Member names, when this field is an enum.</summary>
    public List<string>? EnumValues;
    /// <summary>Set when this field is itself a composed object — its own fields, recursively.
    /// An entity's authored data is a tree, not a row.</summary>
    public List<AuthoredField>? Nested;
    /// <summary>True when the composed type is authored by REFERENCING a native shape rather than
    /// by typing its numbers. The nested fields still describe what gets BAKED into the export.</summary>
    public bool NativeShape;
}

/// <summary>One authored record: an id, a display name, and its fields.</summary>
internal sealed class AuthoredType
{
    public string ComponentId = "";
    public string DisplayName = "";
    public List<AuthoredField> Fields = new();
    /// <summary>Optional wireframe box, as field names — see AuthorBoxGizmoAttribute.</summary>
    public string[]? BoxGizmo;
}

internal static class AuthoredModel
{
    private const string Namespace = "Paradise.Authoring";
    public const string AuthoredAttribute = Namespace + ".AuthoredAttribute";
    private const string BoxGizmoAttribute = Namespace + ".AuthorBoxGizmoAttribute";
    private const string NativeShapeAttribute = Namespace + ".AuthorNativeShapeAttribute";

    /// <summary>Read an [Authored] record into the editor-neutral model. Returns null when the
    /// symbol is not usable, which the generator treats as "emit nothing" rather than crashing a
    /// build over a half-typed declaration.</summary>
    public static AuthoredType? Read(INamedTypeSymbol type) => Read(type, 0);

    private static AuthoredType? Read(INamedTypeSymbol type, int depth)
    {
        var attribute = type.GetAttributes().FirstOrDefault(
            a => a.AttributeClass?.ToDisplayString() == AuthoredAttribute);
        // A NESTED type is a part, not a component: it needs no id of its own, and demanding one
        // would force every composed struct to invent a name nothing ever looks up.
        if (depth == 0 && (attribute is null || attribute.ConstructorArguments.Length == 0))
        {
            return null;
        }

        var componentId = attribute is { ConstructorArguments.Length: > 0 }
            ? attribute.ConstructorArguments[0].Value as string ?? ""
            : "";
        if (depth == 0 && componentId.Length == 0)
        {
            return null;
        }

        var displayName = type.Name;
        // Guarded, not null-coalesced: a NESTED type carries no [Authored] at all, and both
        // dereferencing null AND `?? default` on an ImmutableArray throw here. A generator that
        // throws emits NOTHING - every component vanishes from the schema with no error naming
        // this line. Watch for CS8785 when output disappears.
        if (attribute is not null)
        {
            foreach (var named in attribute.NamedArguments)
            {
                if (named.Key == "DisplayName" && named.Value.Value is string custom && custom.Length > 0)
                {
                    displayName = custom;
                }
            }
        }

        var result = new AuthoredType
        {
            ComponentId = componentId,
            DisplayName = displayName,
        };

        var gizmo = type.GetAttributes().FirstOrDefault(
            a => a.AttributeClass?.ToDisplayString() == BoxGizmoAttribute);
        if (gizmo is not null && gizmo.ConstructorArguments.Length == 3)
        {
            var names = gizmo.ConstructorArguments.Select(a => a.Value as string).ToArray();
            if (names.All(n => !string.IsNullOrEmpty(n)))
            {
                result.BoxGizmo = names!;
            }
        }

        foreach (var member in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (member.DeclaredAccessibility != Accessibility.Public ||
                member.IsStatic || member.SetMethod is null || member.IsIndexer)
            {
                continue;
            }

            var schemaType = SchemaTypeOf(member.Type);
            List<AuthoredField>? nested = null;
            List<string>? enumValues = null;
            var nativeShape = false;

            if (schemaType == "enum")
            {
                enumValues = EnumValuesOf(member.Type);
            }
            else if (schemaType is null)
            {
                // Not a scalar - but it may be a COMPOSED type: another record whose fields are
                // themselves authorable. Recursing is what lets a component own a BoxCollider and
                // have it appear nested rather than flattened by hand.
                nested = ComposedFieldsOf(member.Type, depth);
                if (nested is null)
                {
                    // Genuinely unsupported: skipped rather than guessed at, because a schema that
                    // claims a control an editor cannot draw is worse than an absent one.
                    continue;
                }
                nativeShape = member.Type.GetAttributes().Any(
                    a => a.AttributeClass?.ToDisplayString() == NativeShapeAttribute);
                schemaType = "object";
            }

            var field = new AuthoredField
            {
                Name = member.Name,
                SchemaType = schemaType,
                Default = DefaultOf(member),
                EnumValues = enumValues,
                Nested = nested,
                NativeShape = nativeShape,
            };

            foreach (var a in member.GetAttributes())
            {
                switch (a.AttributeClass?.ToDisplayString())
                {
                    case Namespace + ".MetersAttribute": field.Unit = "meters"; break;
                    case Namespace + ".RadiansAttribute": field.Unit = "radians"; break;
                    case Namespace + ".SecondsAttribute": field.Unit = "seconds"; break;
                    case Namespace + ".KilogramsAttribute": field.Unit = "kilograms"; break;
                    case Namespace + ".Unit01Attribute":
                        field.Unit = "unit01";
                        field.Minimum ??= 0d;
                        field.Maximum ??= 1d;
                        break;
                    case Namespace + ".AuthorDocAttribute" when a.ConstructorArguments.Length == 1:
                        field.Doc = a.ConstructorArguments[0].Value as string;
                        break;
                    case Namespace + ".AuthorRangeAttribute" when a.ConstructorArguments.Length == 2:
                        field.Minimum = ToDouble(a.ConstructorArguments[0].Value);
                        field.Maximum = ToDouble(a.ConstructorArguments[1].Value);
                        break;
                }
            }

            result.Fields.Add(field);
        }

        return result;
    }

    /// <summary>How deep composition may nest. A record that reaches itself would otherwise
    /// recurse until the compiler gives up; six is far past anything an inspector should show.</summary>
    private const int MaxDepth = 6;

    /// <summary>The fields of a composed value type, or null when it is not one. Anything whose
    /// members are all authorable qualifies — the nested type does NOT need its own [Authored]
    /// id, because it is a part, not a component in its own right.</summary>
    private static List<AuthoredField>? ComposedFieldsOf(ITypeSymbol type, int depth)
    {
        if (depth >= MaxDepth || type is not INamedTypeSymbol named ||
            named.TypeKind is not (TypeKind.Class or TypeKind.Struct) ||
            named.SpecialType != SpecialType.None)
        {
            return null;
        }

        var composed = Read(named, depth + 1);
        return composed is { Fields.Count: > 0 } ? composed.Fields : null;
    }

    /// <summary>Editor-neutral type names. Deliberately few: every one of these has an obvious
    /// control in Godot, Blender and HTML alike, and each addition is work in every editor.</summary>
    private static string? SchemaTypeOf(ITypeSymbol type)
    {
        // Checked before SpecialType: an enum's SpecialType is None, but its UNDERLYING type is an
        // integer, so asking about the enum itself is the only way to tell them apart.
        if (type.TypeKind == TypeKind.Enum)
        {
            return "enum";
        }

        return type.SpecialType switch
        {
            SpecialType.System_Single or SpecialType.System_Double => "float",
            SpecialType.System_Int32 or SpecialType.System_Int64 => "int",
            SpecialType.System_Boolean => "bool",
            SpecialType.System_String => "string",
            _ => null,
        };
    }

    /// <summary>Enum member NAMES, in declaration order. Names rather than numbers because the
    /// export contract already writes enums by name (JsonStringEnumConverter), so an editor that
    /// stores the name produces a payload the runtime reads without a mapping table.</summary>
    private static List<string> EnumValuesOf(ITypeSymbol type) =>
        type.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => f.ConstantValue is not null)
            .Select(f => f.Name)
            .ToList();

    /// <summary>The property's initializer text, so the editor and the record cannot disagree
    /// about a default. Parsed from syntax because Roslyn exposes no "initializer value" on a
    /// property symbol.</summary>
    private static string? DefaultOf(IPropertySymbol property)
    {
        foreach (var reference in property.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax
                { Initializer: { } initializer })
            {
                return initializer.Value.ToString();
            }
        }
        return null;
    }

    private static double? ToDouble(object? value) => value switch
    {
        double d => d,
        float f => f,
        int i => i,
        _ => null,
    };
}
