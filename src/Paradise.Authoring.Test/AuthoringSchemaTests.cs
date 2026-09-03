using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Paradise.Authoring;

namespace Paradise.Authoring.Test;

/// <summary>
/// The generator, the document and the typed reader, exercised together over the fixtures in this
/// assembly. Every assertion here reads the const the generator actually emitted, so a change that
/// breaks the mechanism fails a test rather than quietly shipping an editor that shows nothing.
/// </summary>
public class AuthoringSchemaTests
{
    /// <summary>The generated document. A method rather than the const directly, because a
    /// constant assertion operand is a compile-time-decidable comparison and TUnit rejects it.</summary>
    private static string RawJson() => AuthoringSchema.Json;

    private static AuthoringSchemaDocument Schema() => AuthoringSchemaReader.Read(RawJson());

    private static AuthoredComponentSchema Everything() =>
        Schema().Components.Single(c => c.Id == FixtureIds.EverythingId);

    private static AuthoredFieldSchema Field(AuthoredComponentSchema component, string name) =>
        component.Fields.Single(f => f.Name == name);

    [Test]
    public async Task the_generated_schema_parses_through_the_typed_reader()
    {
        var schema = Schema();
        await Assert.That(schema.Version).IsEqualTo(AuthoringSchemaDocument.CurrentVersion);
        await Assert.That(schema.Components.Select(c => c.Id)).IsEquivalentTo(new[]
        {
            FixtureIds.EverythingId, FixtureIds.MinimalId, FixtureIds.V2Id, FixtureIds.BySpriteId,
            FixtureIds.HostBoundId, FixtureIds.ByLightId, FixtureIds.ByCameraId,
        });
    }

    // ---- typed host kinds ------------------------------------------------------------------

    private static AuthoredComponentSchema HostBound() =>
        Schema().Components.Single(c => c.Id == FixtureIds.HostBoundId);

    /// <summary>The generic attribute publishes the kind's own string — the schema JSON the
    /// (pure Python) addon reads does not change shape for the typed spelling.</summary>
    [Test]
    public async Task a_value_kind_bound_by_attribute_publishes_its_kind_string()
    {
        await Assert.That(Field(HostBound(), "Ident").AuthoredBy).IsEqualTo(AuthoredBySources.Id);
        await Assert.That(Field(HostBound(), "Label").AuthoredBy).IsEqualTo(AuthoredBySources.Name);
        await Assert.That(Field(HostBound(), "Spin").AuthoredBy).IsEqualTo(AuthoredBySources.LocalRotation);
        await Assert.That(Field(HostBound(), "Parent").AuthoredBy).IsEqualTo(AuthoredBySources.Parent);
        await Assert.That(Field(HostBound(), "Target").AuthoredBy).IsEqualTo(AuthoredBySources.Entity);
        await Assert.That(Field(HostBound(), "File").AuthoredBy).IsEqualTo(AuthoredBySources.Asset);
        await Assert.That(Field(HostBound(), "Mesh").AuthoredBy).IsEqualTo(AuthoredBySources.Mesh);
        await Assert.That(Field(HostBound(), "Sprite").AuthoredBy).IsEqualTo(AuthoredBySources.Sprite);
    }

    /// <summary>A property TYPED as a value kind binds with no attribute at all, and the schema
    /// describes it at the kind's VALUE type — the wire type, not the wrapper struct.</summary>
    [Test]
    public async Task a_value_kind_bound_by_type_publishes_kind_and_wire_type()
    {
        var position = Field(HostBound(), "Position");
        await Assert.That(position.AuthoredBy).IsEqualTo(AuthoredBySources.LocalPosition);
        await Assert.That(position.Type).IsEqualTo("vector3");

        var scale = Field(HostBound(), "Scale");
        await Assert.That(scale.AuthoredBy).IsEqualTo(AuthoredBySources.LocalScale);
        await Assert.That(scale.Type).IsEqualTo("vector3");
    }

    /// <summary>A composed host kind nested as a property: the group is authoredBy the kind,
    /// and its leaves are the host-supplied geometry.</summary>
    [Test]
    public async Task a_composed_host_kind_nests_its_fields()
    {
        var collider = Field(HostBound(), "Collider");
        await Assert.That(collider.AuthoredBy).IsEqualTo(AuthoredBySources.Shape);
        await Assert.That(collider.Type).IsEqualTo(AuthoredFieldTypes.Object);
        await Assert.That(collider.Fields!.Select(f => f.Name))
            .IsEquivalentTo(new[]
            {
                "ShapeType", "LocalCenter", "LocalRotation", "Size", "Radius", "Height",
            });

        var lamp = Field(HostBound(), "Lamp");
        await Assert.That(lamp.AuthoredBy).IsEqualTo(AuthoredBySources.Light);
        await Assert.That(lamp.Type).IsEqualTo(AuthoredFieldTypes.Object);
        await Assert.That(lamp.Fields!.Any(f => f.Name == "Type")).IsTrue();
        await Assert.That(lamp.Fields!.Any(f => f.Name == "Color")).IsTrue();
        await Assert.That(lamp.Fields!.Any(f => f.Name == "Intensity")).IsTrue();

        var eye = Field(HostBound(), "Eye");
        await Assert.That(eye.AuthoredBy).IsEqualTo(AuthoredBySources.Camera);
        await Assert.That(eye.Type).IsEqualTo(AuthoredFieldTypes.Object);
        await Assert.That(eye.Fields!.Select(f => f.Name))
            .IsEquivalentTo(new[]
            {
                "Projection", "Fov", "OrthographicSize", "Near", "Far", "Position", "Rotation",
            });

        // The kinds' defaults reach the schema only as property initializers; a constructor body
        // is invisible to the generator and the editor would see an unspecified FOV.
        await Assert.That(eye.Fields!.Single(f => f.Name == "Fov").Default!.Value.GetSingle()).IsEqualTo(50f);
        await Assert.That(eye.Fields!.Single(f => f.Name == "Far").Default!.Value.GetSingle()).IsEqualTo(1000f);
        await Assert.That(eye.Fields!.Single(f => f.Name == "Projection").Default!.Value.GetString()).IsEqualTo("Perspective");
        await Assert.That(lamp.Fields!.Single(f => f.Name == "Enabled").Default!.Value.GetBoolean()).IsTrue();
        await Assert.That(lamp.Fields!.Single(f => f.Name == "Intensity").Default!.Value.GetSingle()).IsEqualTo(1f);
    }

    /// <summary>The sheet's GEOMETRY nests; its clock does not, because no sprite object holds a
    /// frame rate.</summary>
    [Test]
    public async Task a_sprite_sheet_kind_publishes_the_geometry_a_host_reads_off_a_sprite()
    {
        var flipbook = Field(HostBound(), "Flipbook");
        await Assert.That(flipbook.AuthoredBy).IsEqualTo(AuthoredBySources.SpriteSheet);
        await Assert.That(flipbook.Type).IsEqualTo(AuthoredFieldTypes.Object);
        await Assert.That(flipbook.Fields!.Select(f => f.Name))
            .IsEquivalentTo(new[] { "Sheet", "Columns", "Rows", "QuadSize", "Billboard" });
        await Assert.That(flipbook.Fields!.Single(f => f.Name == "Columns").Default!.Value.GetInt32()).IsEqualTo(1);
        await Assert.That(flipbook.Fields!.Single(f => f.Name == "Billboard").Default!.Value.GetBoolean()).IsTrue();
    }

    /// <summary>What a sky IS, not one renderer's fit to it: the gradient and its two curve
    /// exponents are here, and a shader's cosine thresholds are deliberately not — a host that
    /// integrates its own sky publishes the result through <c>AmbientSh</c> instead.</summary>
    [Test]
    public async Task an_environment_kind_publishes_a_gradient_and_no_host_shader_constants()
    {
        var mood = Field(HostBound(), "Mood");
        await Assert.That(mood.AuthoredBy).IsEqualTo(AuthoredBySources.Environment);
        await Assert.That(mood.Type).IsEqualTo(AuthoredFieldTypes.Object);

        var fields = mood.Fields!.Select(f => f.Name).ToList();
        await Assert.That(fields).Contains("SkyTop");
        await Assert.That(fields).Contains("SkyCurve");
        await Assert.That(fields).Contains("AmbientSh");
        await Assert.That(fields.Where(name => name.Contains("Cos", StringComparison.Ordinal))).IsEmpty();

        await Assert.That(mood.Fields!.Single(f => f.Name == "AmbientMode").Default!.Value.GetString())
            .IsEqualTo("Color");
        await Assert.That(mood.Fields!.Single(f => f.Name == "TonemapMode").Default!.Value.GetString())
            .IsEqualTo("Linear");
    }

    /// <summary>A nullable leaf publishes its underlying type, because "absent" is expressed by
    /// omitting the key rather than by a distinct schema type — this is what lets a scene say
    /// "leave the renderer's own" about a shadow map it never sized.</summary>
    [Test]
    public async Task an_environment_kinds_optional_leaves_publish_their_underlying_type()
    {
        var mood = Field(HostBound(), "Mood");
        await Assert.That(mood.Fields!.Single(f => f.Name == "ShadowMapSize").Type)
            .IsEqualTo(AuthoredFieldTypes.Int);
        await Assert.That(mood.Fields!.Single(f => f.Name == "ShadowBlur").Type)
            .IsEqualTo(AuthoredFieldTypes.Float);
    }

    /// <summary>[AuthorDefault] exists because the kinds reach a game's generator as metadata; it
    /// is a second copy of the initializer, so this is what stops the two from drifting.</summary>
    [Test]
    public async Task composed_kind_attribute_defaults_match_their_initializers()
    {
        foreach (var kind in new[]
                 {
                     typeof(HostShape), typeof(HostLight), typeof(HostCamera),
                     typeof(HostSpriteSheet), typeof(HostEnvironment),
                 })
        {
            var fresh = Activator.CreateInstance(kind)!;
            foreach (var property in kind.GetProperties())
            {
                if (property.GetCustomAttribute<AuthorDefaultAttribute>() is not { } declared) continue;
                var actual = property.GetValue(fresh)!;
                var expected = Convert.ChangeType(declared.Value, property.PropertyType, CultureInfo.InvariantCulture);
                await Assert.That(actual).IsEqualTo(expected).Because($"{kind.Name}.{property.Name}");
            }
        }
    }

    /// <summary>An identity travels as the canonical guid STRING, like every id in the contract —
    /// and it is host-supplied, so no editor draws a control for it.</summary>
    [Test]
    public async Task a_host_id_publishes_as_a_string_field()
    {
        await Assert.That(Field(HostBound(), "Ident").Type).IsEqualTo("string");
    }

    /// <summary>The fallback key, and the only thing in the document that says which component a
    /// bare GUID is. Without it a schema is unreviewable and a broken payload is undiagnosable.</summary>
    [Test]
    public async Task every_component_publishes_its_fully_qualified_type_name()
    {
        await Assert.That(Everything().Type).IsEqualTo("Paradise.Authoring.Test.EverythingFixture");
        await Assert.That(Schema().Components.Select(c => c.Type))
            .All().Satisfy(type => type!.StartsWith("Paradise.Authoring.Test."));
    }

    /// <summary>By TYPE NAME rather than by id: ordering by GUID would be equally stable and
    /// completely arbitrary, and would reshuffle the whole document over one regenerated id.</summary>
    [Test]
    public async Task components_are_ordered_by_type_name_so_rebuilds_do_not_diff()
    {
        var types = Schema().Components.Select(c => c.Type).ToList();
        await Assert.That(types).IsEquivalentTo(types.OrderBy(t => t, StringComparer.Ordinal).ToList());
    }

    [Test]
    public async Task display_name_falls_back_to_the_type_name()
    {
        var schema = Schema();
        await Assert.That(schema.Components.Single(c => c.Id == FixtureIds.EverythingId).DisplayName)
            .IsEqualTo("Everything");
        await Assert.That(schema.Components.Single(c => c.Id == FixtureIds.MinimalId).DisplayName)
            .IsEqualTo("MinimalFixture");
    }

    [Test]
    public async Task units_are_semantic_and_carry_no_editor_vocabulary()
    {
        var everything = Everything();
        await Assert.That(Field(everything, "HalfExtentX").Unit).IsEqualTo(AuthoredUnits.Meters);
        await Assert.That(Field(everything, "Duration").Unit).IsEqualTo(AuthoredUnits.Seconds);
        await Assert.That(Field(everything, "Heading").Unit).IsEqualTo(AuthoredUnits.Radians);
        await Assert.That(Field(everything, "Mass").Unit).IsEqualTo(AuthoredUnits.Kilograms);
        await Assert.That(Field(everything, "Friction").Unit).IsEqualTo(AuthoredUnits.Unit01);

        // The drift guard. The moment one editor's vocabulary reaches the document, every other
        // editor inherits it forever — so the document is asserted to name none of them.
        foreach (var forbidden in new[]
                 {
                     "PropertyHint", "PROPERTY_HINT", "subtype", "bpy.props", "Godot",
                     "Blender", "NodePath", "Variant",
                 })
        {
            await Assert.That(RawJson()).DoesNotContain(forbidden);
        }
    }

    [Test]
    public async Task unit01_implies_its_own_bounds()
    {
        var friction = Field(Everything(), "Friction");
        await Assert.That(friction.Minimum).IsEqualTo(0d);
        await Assert.That(friction.Maximum).IsEqualTo(1d);
    }

    [Test]
    public async Task advisory_ranges_and_docs_survive()
    {
        var halfExtent = Field(Everything(), "HalfExtentX");
        await Assert.That(halfExtent.Minimum).IsEqualTo(1d);
        await Assert.That(halfExtent.Maximum).IsEqualTo(100d);
        await Assert.That(halfExtent.Doc).IsEqualTo("Half-width on X.");
    }

    /// <summary>The original bug: `9f` is a C# literal and means nothing to the Python or
    /// TypeScript editors this document exists for.</summary>
    [Test]
    public async Task defaults_are_json_values_at_their_own_type()
    {
        var everything = Everything();

        await Assert.That(Field(everything, "HalfExtentX").Default!.Value.GetSingle()).IsEqualTo(9f);
        await Assert.That(Field(everything, "Count").Default!.Value.GetInt32()).IsEqualTo(5);
        await Assert.That(Field(everything, "Label").Default!.Value.GetString()).IsEqualTo("unnamed");
        await Assert.That(Field(everything, "Shape").Default!.Value.GetString()).IsEqualTo("Capsule");

        await Assert.That(Field(everything, "HalfExtentX").Default!.Value.ValueKind)
            .IsEqualTo(JsonValueKind.Number);
        await Assert.That(RawJson()).DoesNotContain("9f");
    }

    /// <summary>A property with no initializer publishes no default, rather than publishing a
    /// guess an editor would write into the scene as though a human had chosen it.</summary>
    [Test]
    public async Task a_property_without_an_initializer_has_no_default()
    {
        await Assert.That(Field(Everything(), "Heading").Default).IsNull();
        await Assert.That(Field(Everything(), "IsTrigger").Default).IsNull();
    }

    [Test]
    public async Task enums_travel_as_names_not_numbers()
    {
        var shape = Field(Everything(), "Shape");
        await Assert.That(shape.Type).IsEqualTo(AuthoredFieldTypes.Enum);
        await Assert.That(shape.Values).IsEquivalentTo(new[] { "Box", "Sphere", "Capsule" });
    }

    [Test]
    public async Task scalar_types_map_to_the_neutral_names()
    {
        var everything = Everything();
        await Assert.That(Field(everything, "HalfExtentX").Type).IsEqualTo(AuthoredFieldTypes.Float);
        await Assert.That(Field(everything, "Count").Type).IsEqualTo(AuthoredFieldTypes.Int);
        await Assert.That(Field(everything, "IsTrigger").Type).IsEqualTo(AuthoredFieldTypes.Bool);
        await Assert.That(Field(everything, "Label").Type).IsEqualTo(AuthoredFieldTypes.String);
    }

    /// <summary>Authored data is a tree, not a row: a composed part nests, and keeps its own
    /// units, so an editor can render it as a group without knowing the type inside.</summary>
    [Test]
    public async Task composition_nests_rather_than_flattening()
    {
        var box = Field(Everything(), "Box");
        await Assert.That(box.Type).IsEqualTo(AuthoredFieldTypes.Object);
        await Assert.That(box.Fields!.Select(f => f.Name))
            .IsEquivalentTo(new[] { "SizeX", "SizeY", "SizeZ" });
        await Assert.That(box.Fields!.Single(f => f.Name == "SizeX").Unit)
            .IsEqualTo(AuthoredUnits.Meters);
    }

    /// <summary>Authored as a REFERENCE, exported as a VALUE — the nested fields describe what the
    /// exporter bakes out of the host's own shape object.</summary>
    [Test]
    public async Task a_native_shape_part_is_marked_as_such()
    {
        await Assert.That(Field(Everything(), "Box").AuthoredBy).IsEqualTo(AuthoredBySources.Shape);
    }

    [Test]
    public async Task a_declared_gizmo_names_the_fields_that_size_it()
    {
        var gizmo = Everything().Gizmo;
        await Assert.That(gizmo).IsNotNull();
        await Assert.That(gizmo!.Kind).IsEqualTo("box");
        await Assert.That(gizmo.HalfExtentX).IsEqualTo("HalfExtentX");
        await Assert.That(gizmo.HalfExtentZ).IsEqualTo("HalfExtentZ");
        await Assert.That(gizmo.Depth).IsEqualTo("Depth");
        await Assert.That(Schema().Components.Single(c => c.Id == FixtureIds.MinimalId).Gizmo).IsNull();
    }

    [Test]
    public async Task the_document_round_trips_through_write_and_read()
    {
        var rewritten = AuthoringSchemaReader.Read(AuthoringSchemaReader.Write(Schema()));
        await Assert.That(AuthoringSchemaReader.Write(rewritten))
            .IsEqualTo(AuthoringSchemaReader.Write(Schema()));
    }
}
