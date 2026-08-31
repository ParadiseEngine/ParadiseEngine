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
    /// <summary>Set when the property is TYPED as a value host kind (<c>HostLocalPosition</c>):
    /// the fully qualified host struct the generated reader wraps the wire value back into.
    /// <see cref="ClrKind"/>/<see cref="ClrType"/> then describe the kind's <c>Value</c>.</summary>
    public string? HostWrapperType;
    /// <summary>Element model when this field is a list. Arrays are a repeated ROW in an editor,
    /// not a composed group, so the element lives here rather than in Nested.</summary>
    public AuthoredField? Items;
    /// <summary>Accepted file extensions, when AuthoredBy is "asset".</summary>
    public List<string>? AssetKinds;
    /// <summary>Sibling field name this one is shown for, or null when always shown.</summary>
    public string? VisibleWhenField;
    /// <summary>The sibling value that reveals this field, already rendered as a JSON literal.</summary>
    public string? VisibleWhenValue;

    // ---- CLR-facing half, used by the generated READER rather than by any editor. ----
    // The schema deliberately collapses CLR widths (long and int are both "int"); the reader
    // cannot, because it assigns real properties.

    /// <summary>Reader kind: float/double/int/long/uint/ulong/bool/string/enum/vector2/vector3/
    /// vector4color/quaternion/color32/object. For a list field this describes the ELEMENT and
    /// lives on <see cref="Items"/>.</summary>
    public string? ClrKind;
    /// <summary>Fully qualified CLR type of the value (element type for lists) — what the reader
    /// casts an enum to, constructs for a composed group, or instantiates a list of.
    ///
    /// UNANNOTATED: no trailing <c>?</c> even for a nullable reference type, because this string
    /// is also a reader-method name fragment and an <c>Enum.Parse&lt;T&gt;</c> argument, and
    /// neither tolerates one. Nullability travels separately in <see cref="ClrNullable"/>.</summary>
    public string ClrType = "";
    /// <summary>The value is a NULLABLE reference type (<c>string?</c>, not <c>string</c>).
    ///
    /// Needed for exactly one emission: <c>new List&lt;T&gt;</c> for a list field, which must
    /// match the property's declared element annotation or the assignment is CS8619. A
    /// <c>List&lt;string?&gt;</c> property — material slots, where null means "the mesh's own
    /// material wins" — does not accept a <c>List&lt;string&gt;</c>.</summary>
    public bool ClrNullable;
    /// <summary>"array" (needs ToArray) or "list" (List&lt;T&gt; assigns to List/IList/
    /// IReadOnlyList) — set only on the wrapper field of a list.</summary>
    public string? ListKind;
    /// <summary>The property has a plain setter. Init-only cannot be assigned after
    /// construction, and the reader constructs first and assigns per JSON property.</summary>
    public bool Settable;
    /// <summary>C# `required`: `new T()` without it is a compile error, so the reader cannot
    /// construct the type at all.</summary>
    public bool Required;
    /// <summary>For an object field: the composed type has a public parameterless constructor.</summary>
    public bool NestedConstructible = true;
}

/// <summary>One authored record: an id, a display name, and its fields.</summary>
internal sealed class AuthoredType
{
    /// <summary>The declared id in canonical 8-4-4-4-12 form, or "" when the type is nested (a
    /// part, which needs no id) or its <c>[Guid]</c> is missing or malformed.</summary>
    public string ComponentId = "";
    /// <summary>The id exactly as it was written, kept only so a diagnostic can quote it back.</summary>
    public string DeclaredId = "";
    /// <summary>Set when the type is <c>[Authored]</c> with no <c>[Guid]</c> beside it.</summary>
    public bool IdMissing;
    /// <summary>Set when the <c>[Guid]</c> is present but <see cref="DeclaredId"/> is not a GUID.</summary>
    public bool IdMalformed;

    /// <summary>Either problem: the type is declared authored but has no usable identity, so
    /// PAUT005 is reported and nothing is emitted for it.</summary>
    public bool IdUnusable => IdMissing || IdMalformed;
    public string DisplayName = "";
    public List<AuthoredField> Fields = new();
    /// <summary>Optional wireframe box, as field names — see AuthorBoxGizmoAttribute.</summary>
    public string[]? BoxGizmo;
    /// <summary>Host-object kind the whole component is authored by referencing, or null.</summary>
    public string? AuthoredBy;
    /// <summary>Fully qualified name, for the reader to construct.</summary>
    public string FullTypeName = "";

    /// <summary><see cref="FullTypeName"/> without the <c>global::</c> prefix: the name as a human
    /// writes it, which is what the schema publishes and what the reader's fallback matches on.</summary>
    public string TypeName => FullTypeName.StartsWith("global::", System.StringComparison.Ordinal)
        ? FullTypeName.Substring("global::".Length)
        : FullTypeName;
    /// <summary>Where the record is declared, so a diagnostic about it points AT it rather than
    /// at the generated file that would otherwise fail to compile because of it.</summary>
    public Location? Declaration;
    /// <summary>The type has a public parameterless constructor the reader can call.</summary>
    public bool Constructible;
    /// <summary>Host-binding declaration problems (PAUT010–012), collected while reading —
    /// including from composed parts — and reported by the schema generator.</summary>
    public List<HostBindingProblem> HostProblems = new();
}

/// <summary>One host-binding declaration problem, carried to the schema generator's reporting
/// (the same route the identity diagnostics take), keyed by the PAUT id it maps to.</summary>
internal sealed class HostBindingProblem
{
    public string Id = "";
    public Location? Location;
    public string[] Args = System.Array.Empty<string>();
}



internal static class AuthoredModel
{
    /// <summary>The id and type name only. The registry does not care about fields, and reading the
    /// whole tree for it would make every [Authored] edit re-run work nothing consumes.</summary>

    private const string Namespace = "Paradise.Authoring";
    public const string AuthoredAttribute = Namespace + ".AuthoredAttribute";

    /// <summary>Where a component's IDENTITY comes from. The BCL's own attribute rather than a
    /// parameter of ours: .NET already has "the stable GUID of this type", and a second spelling
    /// would let one type carry two GUIDs and be right about neither.</summary>
    private const string GuidAttribute = "System.Runtime.InteropServices.GuidAttribute";
    private const string BoxGizmoAttribute = Namespace + ".AuthorBoxGizmoAttribute";
    private const string AuthoredByHostAttribute = Namespace + ".AuthoredByHostAttribute<THost>";
    private const string HostKindInterface = Namespace + ".IHostKind";
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
        if (depth == 0 && attribute is null)
        {
            return null;
        }

        // Identity comes from [Guid], and only at the top level: a NESTED type is a part, not a
        // component, so demanding one would force every composed struct to mint a GUID nothing
        // ever looks up.
        var guid = type.GetAttributes().FirstOrDefault(
            a => a.AttributeClass?.ToDisplayString() == GuidAttribute);
        var declaredId = guid is { ConstructorArguments.Length: > 0 }
            ? guid.ConstructorArguments[0].Value as string ?? ""
            : "";

        // Canonicalized so an id typed in the other case cannot become a second entry in the
        // registry. Case is the only variation to defend against — the compiler rejects every
        // other form of [Guid] argument itself with CS0591, braces included — but PAUT005 still
        // covers the malformed value, because CS0591 does not say which component it broke.
        var componentId = "";
        var missing = depth == 0 && guid is null;
        var malformed = false;
        if (declaredId.Length > 0)
        {
            if (System.Guid.TryParse(declaredId, out var parsed))
            {
                componentId = parsed.ToString("D");
            }
            else
            {
                malformed = depth == 0;
            }
        }
        else if (guid is not null && depth == 0)
        {
            // [Guid] with an empty or non-string argument. Malformed rather than missing: the
            // author clearly meant to declare one, and "you have no [Guid]" would send them
            // looking for an attribute that is right there.
            malformed = true;
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

        var (typeAuthoredBy, typeHost) = HostKindOf(type);
        var result = new AuthoredType
        {
            ComponentId = componentId,
            DeclaredId = declaredId,
            IdMissing = missing,
            IdMalformed = malformed,
            DisplayName = displayName,
            AuthoredBy = typeAuthoredBy,
            FullTypeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Declaration = type.Locations.FirstOrDefault(),
            Constructible = HasParameterlessCtor(type),
        };

        // A value kind is one concrete value of one field; a whole record cannot be "a Guid".
        if (typeHost is not null && HostValueTypeOf(typeHost) is not null)
        {
            result.HostProblems.Add(new HostBindingProblem
            {
                Id = "PAUT011",
                Location = type.Locations.FirstOrDefault(),
                Args = new[] { type.Name, typeHost.Name },
            });
        }

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

            // A nullable VALUE type (int?, float?) authors as its underlying type: the wire
            // payload is the plain value, the generated assignment converts implicitly, and an
            // absent (or JSON-null) property keeps the record's own null initializer — which is
            // exactly what "unset leaves the default" fields mean by null. Without this unwrap
            // the type matches nothing below and the field is SILENTLY skipped from schema and
            // reader alike — which is how EnvironmentData.ShadowMapSize authored 4096 and
            // materialized null. Leaves only, not list elements: a List<int?> would need the
            // reader's element list to be nullable too, and no contract field wants one.
            if (elementType is null && valueType is INamedTypeSymbol
                { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableLeaf)
            {
                valueType = nullableLeaf.TypeArguments[0];
            }

            // A property TYPED as a value host kind binds by type: the kind is the struct's own
            // and the wire type is its Value's — the generated reader wraps the read value back
            // into the host struct (HostWrapperType).
            string? hostTypedKind = null;
            string? hostWrapper = null;
            if (IsHostKind(valueType) && HostValueTypeOf(valueType) is { } hostValue)
            {
                hostTypedKind = KindNameOf(valueType);
                hostWrapper = valueType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                valueType = hostValue is INamedTypeSymbol
                    { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableValue
                    ? nullableValue.TypeArguments[0]
                    : hostValue;
            }

            var schemaType = SchemaTypeOf(valueType);
            List<AuthoredField>? nested = null;
            List<string>? enumValues = null;
            // PROPERTY first: it is the more specific declaration, so a field that wants a
            // different kind from the one its type declares gets it. The type is the fallback.
            var (memberKind, memberHost) = HostKindOfMember(member);
            string? authoredBy = memberKind ?? hostTypedKind ?? HostKindOf(valueType).Kind;

            // The point of the typed spelling: a value kind DECLARES the type the field must
            // have, so a mismatch is a diagnostic instead of a schema an editor cannot fill.
            if (memberHost is not null && hostWrapper is null
                && HostValueTypeOf(memberHost) is { } declared)
            {
                var expected = declared is INamedTypeSymbol
                    { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableExpected
                    ? nullableExpected.TypeArguments[0]
                    : declared;
                if (!SymbolEqualityComparer.Default.Equals(expected, valueType))
                {
                    result.HostProblems.Add(new HostBindingProblem
                    {
                        Id = "PAUT010",
                        Location = member.Locations.FirstOrDefault(),
                        Args = new[]
                        {
                            member.Name, memberHost.Name,
                            expected.ToDisplayString(), valueType.ToDisplayString(),
                        },
                    });
                }
            }

            // Host-typed field carrying an attribute for a DIFFERENT kind: the attribute wins,
            // but the disagreement is said out loud.
            if (memberHost is not null && hostWrapper is not null && memberKind != hostTypedKind)
            {
                result.HostProblems.Add(new HostBindingProblem
                {
                    Id = "PAUT012",
                    Location = member.Locations.FirstOrDefault(),
                    Args = new[] { member.Name, memberHost.Name },
                });
            }

            if (schemaType == "enum")
            {
                enumValues = EnumValuesOf(valueType);
            }
            else if (schemaType is null)
            {
                // Not a leaf - but it may be a COMPOSED type: another record whose fields are
                // themselves authorable. Recursing is what lets a component own a BoxCollider and
                // have it appear nested rather than flattened by hand.
                nested = ComposedFieldsOf(valueType, depth, result);
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
                // A host-typed field's initializer would be a host-struct expression, which is
                // not a wire-type literal the schema could publish as a default.
                Default = elementType is null && hostWrapper is null ? DefaultOf(member) : null,
                SchemaType = schemaType,
                EnumValues = enumValues,
                Nested = nested,
                AuthoredBy = authoredBy,
                HostWrapperType = hostWrapper,
                ClrKind = nested is not null ? "object" : ClrKindOf(valueType),
                ClrType = valueType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                ClrNullable = valueType is { IsReferenceType: true, NullableAnnotation: NullableAnnotation.Annotated },
                Settable = member.SetMethod is { IsInitOnly: false },
                Required = member.IsRequired,
                NestedConstructible = nested is null || HasParameterlessCtor(valueType),
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
                    ClrType = member.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    ListKind = member.Type is IArrayTypeSymbol ? "array" : "list",
                    Settable = member.SetMethod is { IsInitOnly: false },
                    Required = member.IsRequired,
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
    /// id, because it is a part, not a component in its own right. Host-binding problems found
    /// inside the part bubble up to <paramref name="into"/> so they are reported once, at the
    /// component that reached them.</summary>
    private static List<AuthoredField>? ComposedFieldsOf(ITypeSymbol type, int depth, AuthoredType into)
    {
        if (depth >= MaxDepth || type is not INamedTypeSymbol named ||
            named.TypeKind is not (TypeKind.Class or TypeKind.Struct) ||
            named.SpecialType != SpecialType.None)
        {
            return null;
        }

        var composed = Read(named, depth + 1);
        if (composed is null)
        {
            return null;
        }

        into.HostProblems.AddRange(composed.HostProblems);
        return composed.Fields.Count > 0 ? composed.Fields : null;
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
    private static (string? Kind, ITypeSymbol? Host) HostKindOf(ITypeSymbol type)
    {
        foreach (var a in type.GetAttributes())
        {
            if (HostArgumentOf(a) is { } host)
            {
                return (KindNameOf(host), host);
            }
        }
        return (null, null);
    }

    /// <summary>The host-object kind a PROPERTY declares, which wins over its type's.</summary>
    private static (string? Kind, ITypeSymbol? Host) HostKindOfMember(IPropertySymbol member)
    {
        foreach (var a in member.GetAttributes())
        {
            if (HostArgumentOf(a) is { } host)
            {
                return (KindNameOf(host), host);
            }
        }
        return (null, null);
    }

    /// <summary>The type argument of a constructed <c>[AuthoredByHost&lt;THost&gt;]</c>, or null.
    /// The kind is a TYPE, not a string, so a kind that does not exist cannot compile and a kind
    /// that carries a value declares what type it carries.</summary>
    private static ITypeSymbol? HostArgumentOf(AttributeData attribute)
    {
        if (attribute.AttributeClass is not { IsGenericType: true } attrClass ||
            attrClass.ConstructedFrom.ToDisplayString() != AuthoredByHostAttribute ||
            attrClass.TypeArguments.Length != 1)
        {
            return null;
        }
        return attrClass.TypeArguments[0];
    }

    /// <summary>The <c>authoredBy</c> string a host struct publishes — its <c>Kind</c> const.</summary>
    private static string? KindNameOf(ITypeSymbol host) =>
        host.GetMembers("Kind").OfType<IFieldSymbol>().FirstOrDefault(f => f.IsConst)
            ?.ConstantValue as string;

    /// <summary>Whether the type is a host kind (implements <c>IHostKind</c>).</summary>
    private static bool IsHostKind(ITypeSymbol type) =>
        type.AllInterfaces.Any(i => i.ToDisplayString() == HostKindInterface);

    /// <summary>A VALUE kind's payload type — its <c>Value</c> property — or null for a marker
    /// kind, which names a referenced host object and carries no value of its own.</summary>
    private static ITypeSymbol? HostValueTypeOf(ITypeSymbol host) =>
        host.GetMembers("Value").OfType<IPropertySymbol>().FirstOrDefault()?.Type;

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
    /// <summary>Reader kind for a leaf value — the CLR-exact partner of <see
    /// cref="SchemaTypeOf"/>, which deliberately collapses widths for editors.</summary>
    private static string? ClrKindOf(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum })
        {
            return "enum";
        }
        switch (type.ToDisplayString())
        {
            case "System.Numerics.Vector2": return "vector2";
            case "System.Numerics.Vector3": return "vector3";
            // A Vector4 authors as a COLOR (see SchemaTypeOf), so its wire shape is the color
            // object {r,g,b,a}, not a 4-array.
            case "System.Numerics.Vector4": return "vector4color";
            case "System.Numerics.Quaternion": return "quaternion";
            case "System.Numerics.Matrix4x4": return "matrix4x4";
            case "Paradise.Export.Data.Color32": return "color32";
            // The wire form is the canonical guid STRING, like every id in the contract.
            case "System.Guid": return "guid";
        }
        return type.SpecialType switch
        {
            SpecialType.System_Single => "float",
            SpecialType.System_Double => "double",
            SpecialType.System_Int32 => "int",
            SpecialType.System_Int64 => "long",
            SpecialType.System_UInt32 => "uint",
            SpecialType.System_UInt64 => "ulong",
            SpecialType.System_Boolean => "bool",
            SpecialType.System_String => "string",
            _ => null,
        };
    }

    /// <summary>Whether the generated reader can say <c>new T()</c>.</summary>
    private static bool HasParameterlessCtor(ITypeSymbol type) =>
        type.IsValueType ||
        (type is INamedTypeSymbol named && named.InstanceConstructors.Any(
            c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public));

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
            case "System.Numerics.Matrix4x4": return "matrix4x4";
            case "Paradise.Export.Data.Color32": return "color";
            // Published as a string: an id is host-supplied, so no editor draws a control for it.
            case "System.Guid": return "string";
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
