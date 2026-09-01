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

    /// <summary>
    /// A pose reference: authored by pointing at an object, exported as the numbers baked out of
    /// where it stands.
    ///
    /// The kind is what a host switches on, so it has to survive the round trip intact — and the
    /// nested fields are the CONTRACT for what gets baked, since an exporter fills them by NAME.
    /// A record declaring only part of the pose (this one takes position and yaw, not rotation or
    /// scale) must publish only that part, or a host would look for fields nobody declared.
    /// </summary>
    [Test]
    public async Task a_pose_reference_publishes_its_kind_and_the_fields_baked_from_it()
    {
        var destination = Field("Destination");
        await Assert.That(destination.Type).IsEqualTo(AuthoredFieldTypes.Object);
        await Assert.That(destination.AuthoredBy).IsEqualTo(AuthoredBySources.Transform);
        await Assert.That(destination.Fields!.Select(f => f.Name))
            .IsEquivalentTo(new[] { "Position", "Yaw" });
        await Assert.That(destination.Fields!.Single(f => f.Name == "Position").Type)
            .IsEqualTo(AuthoredFieldTypes.Vector3);
        await Assert.That(destination.Fields!.Single(f => f.Name == "Yaw").Unit)
            .IsEqualTo(AuthoredUnits.Radians);
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

    [Test]
    public async Task a_sprite_is_a_guid_on_the_field()
    {
        var schema = AuthoringSchemaReader.Read(AuthoringSchema.Json);
        var sheet = schema.Components.Single(c => c.Id == FixtureIds.BySpriteId)
            .Fields.Single(f => f.Name == "Sheet");
        await Assert.That(sheet.AuthoredBy).IsEqualTo(AuthoredBySources.Sprite);
        await Assert.That(sheet.Type).IsEqualTo(AuthoredFieldTypes.String);
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

    /// <summary>A component can be authored by ONE host object rather than as a form — a light's
    /// colour and energy are read off the host light. That has to live on the component, since
    /// there is no single field to hang a composed kind on.</summary>
    [Test]
    public async Task a_whole_component_can_be_authored_by_a_host_object()
    {
        var schema = AuthoringSchemaReader.Read(AuthoringSchema.Json);
        await Assert.That(schema.Components.Single(c => c.Id == FixtureIds.ByLightId).AuthoredBy)
            .IsEqualTo(AuthoredBySources.Light);
        await Assert.That(schema.Components.Single(c => c.Id == FixtureIds.ByCameraId).AuthoredBy)
            .IsEqualTo(AuthoredBySources.Camera);

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
