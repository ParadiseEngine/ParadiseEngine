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
        });
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
