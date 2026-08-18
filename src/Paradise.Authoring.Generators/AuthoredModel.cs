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
    /// <summary>Which kind of host object this value is authored by referencing (shape, mesh,
    /// sprite, asset), or null when it is typed directly. Any nested fields still describe what
    /// gets BAKED out of that reference at export.</summary>
    public string? AuthoredBy;
    /// <summary>Element model when this field is a list. Arrays are a repeated ROW in an editor,
    /// not a composed group, so the element lives here rather than in Nested.</summary>
    public AuthoredField? Items;
    /// <summary>Accepted file extensions, when AuthoredBy is "asset".</summary>
    public List<string>? AssetKinds;
    /// <summary>Sibling field name this one is shown for, or null when always shown.</summary>
    public string? VisibleWhenField;
    /// <summary>The sibling value that reveals this field, already rendered as a JSON literal.</summary>
    public string? VisibleWhenValue;
}

/// <summary>One authored record: an id, a display name, and its fields.</summary>
internal sealed class AuthoredType
{
    public string ComponentId = "";
    public string DisplayName = "";
    public List<AuthoredField> Fields = new();
    /// <summary>Optional wireframe box, as field names — see AuthorBoxGizmoAttribute.</summary>
    public string[]? BoxGizmo;
    /// <summary>Host-object kind the whole component is authored by referencing, or null.</summary>
    public string? AuthoredBy;
}

/// <summary>Just enough of an [Authored] type to build a registry entry: the id it travels under
/// and the record it deserializes into.</summary>
internal sealed class AuthoredIdentity
{
    public string ComponentId = "";
    public string TypeName = "";
}

internal static class AuthoredModel
{
    /// <summary>The id and type name only. The registry does not care about fields, and reading the
    /// whole tree for it would make every [Authored] edit re-run work nothing consumes.</summary>
    public static AuthoredIdentity? ReadIdentity(INamedTypeSymbol type)
    {
        var attribute = type.GetAttributes().FirstOrDefault(
            a => a.AttributeClass?.ToDisplayString() == AuthoredAttribute);
        if (attribute is not { ConstructorArguments.Length: > 0 } ||
            attribute.ConstructorArguments[0].Value is not string id || id.Length == 0)
        {
            return null;
        }
        return new AuthoredIdentity { ComponentId = id, TypeName = type.Name };
    }

    private const string Namespace = "Paradise.Authoring";
    public const string AuthoredAttribute = Namespace + ".AuthoredAttribute";
    private const string BoxGizmoAttribute = Namespace + ".AuthorBoxGizmoAttribute";
    private const string NativeShapeAttribute = Namespace + ".AuthorNativeShapeAttribute";
    private const string AuthoredByHostAttribute = Namespace + ".AuthoredByHostAttribute";
    private const string AssetKindsAttribute = Namespace + ".AuthorAssetKindsAttribute";
    private const string VisibleWhenAttribute = Namespace + ".AuthorVisibleWhenAttribute";

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
            AuthoredBy = HostKindOf(type),
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

            var elementType = ElementTypeOf(member.Type);
            var valueType = elementType ?? member.Type;

            var schemaType = SchemaTypeOf(valueType);
            List<AuthoredField>? nested = null;
            List<string>? enumValues = null;
            string? authoredBy = HostKindOf(valueType) ?? HostKindOfMember(member);

            if (schemaType == "enum")
            {
                enumValues = EnumValuesOf(valueType);
            }
            else if (schemaType is null)
            {
                // Not a leaf - but it may be a COMPOSED type: another record whose fields are
                // themselves authorable. Recursing is what lets a component own a BoxCollider and
                // have it appear nested rather than flattened by hand.
                nested = ComposedFieldsOf(valueType, depth);
                if (nested is null)
                {
                    // Genuinely unsupported: skipped rather than guessed at, because a schema that
                    // claims a control an editor cannot draw is worse than an absent one.
                    continue;
                }
                schemaType = "object";
            }

            var value = new AuthoredField
            {
                Name = member.Name,
                SchemaType = schemaType,
                Default = elementType is null ? DefaultOf(member) : null,
                EnumValues = enumValues,
                Nested = nested,
                AuthoredBy = authoredBy,
            };

            // A list becomes an ARRAY field whose element carries everything just derived. The
            // element is unnamed: an editor labels rows by index, not by the property name.
            var field = elementType is null
                ? value
                : new AuthoredField
                {
                    Name = member.Name,
                    SchemaType = "array",
                    Items = value,
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
                    case AssetKindsAttribute when a.ConstructorArguments.Length == 1:
                        field.AssetKinds = a.ConstructorArguments[0].Values
                            .Select(v => v.Value as string)
                            .Where(v => !string.IsNullOrEmpty(v))
                            .Select(v => v!)
                            .ToList();
                        break;
                    case VisibleWhenAttribute when a.ConstructorArguments.Length == 2:
                        field.VisibleWhenField = a.ConstructorArguments[0].Value as string;
                        field.VisibleWhenValue = JsonLiteralOf(a.ConstructorArguments[1]);
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

    /// <summary>The element type of a list or array, or null when the type is not one. A list is
    /// the only collection shape supported: a dictionary has no obvious control anywhere.</summary>
    private static ITypeSymbol? ElementTypeOf(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
        {
            return array.ElementType;
        }
        if (type is INamedTypeSymbol { IsGenericType: true } named)
        {
            var definition = named.ConstructedFrom.ToDisplayString();
            if (definition == "System.Collections.Generic.List<T>" ||
                definition == "System.Collections.Generic.IReadOnlyList<T>" ||
                definition == "System.Collections.Generic.IList<T>")
            {
                return named.TypeArguments[0];
            }
        }
        return null;
    }

    /// <summary>The host-object kind a TYPE declares it is authored by referencing.</summary>
    private static string? HostKindOf(ITypeSymbol type)
    {
        foreach (var a in type.GetAttributes())
        {
            var name = a.AttributeClass?.ToDisplayString();
            if (name == NativeShapeAttribute)
            {
                return "shape";
            }
            if (name == AuthoredByHostAttribute && a.ConstructorArguments.Length == 1)
            {
                return a.ConstructorArguments[0].Value as string;
            }
        }
        return null;
    }

    /// <summary>The host-object kind a PROPERTY declares, which wins over its type's.</summary>
    private static string? HostKindOfMember(IPropertySymbol member)
    {
        foreach (var a in member.GetAttributes())
        {
            if (a.AttributeClass?.ToDisplayString() == AuthoredByHostAttribute &&
                a.ConstructorArguments.Length == 1)
            {
                return a.ConstructorArguments[0].Value as string;
            }
        }
        return null;
    }

    /// <summary>An attribute argument as a JSON literal, for the visibility comparison value.</summary>
    private static string? JsonLiteralOf(TypedConstant constant)
    {
        if (constant.Value is null)
        {
            return null;
        }
        // An enum arrives as its underlying integer; the schema compares enums BY NAME, matching
        // how the contract serializes them, so the member name is recovered here.
        if (constant.Type is { TypeKind: TypeKind.Enum } enumType)
        {
            foreach (var f in enumType.GetMembers().OfType<IFieldSymbol>())
            {
                if (f.ConstantValue is not null && f.ConstantValue.Equals(constant.Value))
                {
                    return "\"" + f.Name + "\"";
                }
            }
            return null;
        }
        return constant.Value switch
        {
            bool b => b ? "true" : "false",
            string s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
            _ => System.Convert.ToString(
                constant.Value, System.Globalization.CultureInfo.InvariantCulture),
        };
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

        // Small fixed-size aggregates are LEAVES, matched by name so this project keeps its
        // zero-dependency promise (Color32 lives in Paradise.Export, which depends on us, not the
        // other way round). Decomposing them into floats would discard the dedicated control every
        // editor already has.
        switch (type.ToDisplayString())
        {
            case "System.Numerics.Vector2": return "vector2";
            case "System.Numerics.Vector3": return "vector3";
            case "System.Numerics.Vector4": return "color";
            case "System.Numerics.Quaternion": return "quaternion";
            case "Paradise.Export.Data.Color32": return "color";
        }

        return type.SpecialType switch
        {
            SpecialType.System_Single or SpecialType.System_Double => "float",
            SpecialType.System_Int32 or SpecialType.System_Int64 => "int",
            SpecialType.System_UInt32 or SpecialType.System_UInt64 => "int",
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
