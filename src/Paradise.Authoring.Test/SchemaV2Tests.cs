using Paradise.Authoring;

namespace Paradise.Authoring.Test;

/// <summary>
/// Schema v2: the types and hints the ENGINE's own components need, without which
/// <c>EntityExport</c> cannot be replaced by declarations.
///
/// As with the v1 tests, these run over real <c>[Authored]</c> fixtures in this assembly, so they
/// exercise attribute → generator → document → typed reader rather than a hand-written string.
/// </summary>
public class SchemaV2Tests
{
    private static AuthoredComponentSchema V2() =>
        AuthoringSchemaReader.Read(AuthoringSchema.Json).Components
            .Single(c => c.Id == FixtureIds.V2Id);

    private static AuthoredFieldSchema Field(string name) => V2().Fields.Single(f => f.Name == name);

    [Test]
    public async Task the_document_declares_the_current_version()
    {
        var schema = AuthoringSchemaReader.Read(AuthoringSchema.Json);
        await Assert.That(schema.Version).IsEqualTo(AuthoringSchemaDocument.CurrentVersion);
    }

    /// <summary>A list is a repeated ROW, not a composed group: the element schema hangs off
    /// <c>items</c> so an editor can add and remove rows without inventing names for them.</summary>
    [Test]
    public async Task a_list_becomes_an_array_with_an_element_schema()
    {
        var colliders = Field("Colliders");
        await Assert.That(colliders.Type).IsEqualTo(AuthoredFieldTypes.Array);
        await Assert.That(colliders.Items).IsNotNull();

        // The element is a shape REFERENCE, and its own fields are what the exporter bakes out.
        await Assert.That(colliders.Items!.AuthoredBy).IsEqualTo(AuthoredBySources.Shape);
        await Assert.That(colliders.Items!.Fields!.Select(f => f.Name))
            .IsEquivalentTo(new[] { "Kind", "Size", "LocalCenter", "LocalRotation", "Radius" });
    }

    /// <summary>Fixed-size aggregates stay LEAVES. Decomposing a Vector3 into three floats would
    /// throw away the dedicated control every editor already has.</summary>
    [Test]
    public async Task vectors_and_colours_are_leaves_not_composed_objects()
    {
        await Assert.That(Field("QuadSize").Type).IsEqualTo(AuthoredFieldTypes.Vector2);
        await Assert.That(Field("Offset").Type).IsEqualTo(AuthoredFieldTypes.Vector3);
        await Assert.That(Field("Rotation").Type).IsEqualTo(AuthoredFieldTypes.Quaternion);
        await Assert.That(Field("Tint").Type).IsEqualTo(AuthoredFieldTypes.Color);

        foreach (var name in new[] { "QuadSize", "Offset", "Rotation", "Tint" })
        {
            await Assert.That(Field(name).Fields).IsNull();
        }
    }

    [Test]
    public async Task an_unsigned_int_is_published_as_an_int()
    {
        await Assert.That(Field("Seed").Type).IsEqualTo(AuthoredFieldTypes.Int);
    }

    [Test]
    public async Task host_object_kinds_survive_on_a_property()
    {
        await Assert.That(Field("MeshNode").AuthoredBy).IsEqualTo(AuthoredBySources.Mesh);
        await Assert.That(Field("Model").AuthoredBy).IsEqualTo(AuthoredBySources.Asset);
    }

    /// <summary>Accepted file kinds are what the file IS. A Godot filter string ("*.glb,*.gltf") in
    /// the document would make every other editor speak Godot.</summary>
    [Test]
    public async Task an_asset_reference_carries_its_accepted_kinds()
    {
        await Assert.That(Field("Model").AssetKinds).IsEquivalentTo(new[] { ".glb", ".gltf" });
        await Assert.That(AuthoringSchema.Json.Contains("*.glb")).IsFalse();
    }

    /// <summary>Conditional visibility as DATA. This is what EntityExport did in _ValidateProperty,
    /// which every other editor would have had to reimplement in its own language.</summary>
    [Test]
    public async Task a_field_can_be_guarded_by_a_bool_sibling()
    {
        var guard = Field("MoveSpeed").VisibleWhen;
        await Assert.That(guard).IsNotNull();
        await Assert.That(guard!.Field).IsEqualTo("IsAgent");
        await Assert.That(guard.EqualTo.GetBoolean()).IsTrue();
    }

    /// <summary>An enum guard compares BY NAME, matching how the contract serializes enums, so an
    /// editor needs no mapping table to evaluate it.</summary>
    [Test]
    public async Task a_field_can_be_guarded_by_an_enum_sibling()
    {
        var guard = Field("SphereRadius").VisibleWhen;
        await Assert.That(guard).IsNotNull();
        await Assert.That(guard!.Field).IsEqualTo("Shape");
        await Assert.That(guard.EqualTo.GetString()).IsEqualTo("Sphere");
    }

    /// <summary>A component can be authored by ONE host object rather than as a form — the engine's
    /// sprite animation reads its sheet, grid and quad size off the sprite. That has to live on the
    /// component, since there is no field to hang it on.</summary>
    [Test]
    public async Task a_whole_component_can_be_authored_by_a_host_object()
    {
        var schema = AuthoringSchemaReader.Read(AuthoringSchema.Json);
        await Assert.That(schema.Components.Single(c => c.Id == FixtureIds.BySpriteId).AuthoredBy)
            .IsEqualTo(AuthoredBySources.Sprite);

        // A component authored as a form says so by omission.
        await Assert.That(V2().AuthoredBy).IsNull();
    }

    [Test]
    public async Task an_unguarded_field_says_so_by_omission()
    {
        await Assert.That(Field("Seed").VisibleWhen).IsNull();
    }

    /// <summary><c>[AuthorNativeShape]</c> is shorthand for the shape kind, and publishes as that
    /// one name — the v1 <c>nativeShape</c> spelling is not written by anything now that no
    /// document older than v3 is readable.</summary>
    [Test]
    public async Task the_native_shape_shorthand_publishes_as_the_shape_kind()
    {
        var box = AuthoringSchemaReader.Read(AuthoringSchema.Json).Components
            .Single(c => c.Id == FixtureIds.EverythingId)
            .Fields.Single(f => f.Name == "Box");

        await Assert.That(box.AuthoredBy).IsEqualTo(AuthoredBySources.Shape);
        await Assert.That(AuthoringSchema.Json.Contains("nativeShape")).IsFalse();
    }
}
