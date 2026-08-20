using System.Text.Json;
using Paradise.Authoring;

namespace Paradise.Authoring.Test;

/// <summary>
/// An editor faces more than one schema: the engine's own components and the game's. Merging is in
/// the engine so every host presents one list without reimplementing the join.
/// </summary>
public class AuthoringSchemaMergeTests
{
    /// <summary>A component keyed by a distinct id, named after the type it stands in for — the
    /// merge orders by type name, so the two have to be able to disagree.</summary>
    private static AuthoredComponentSchema Component(string type, int id) => new()
    {
        Id = new Guid($"c0000000-0000-4000-8000-{id:d12}"),
        Type = type,
        DisplayName = type,
    };

    private static AuthoringSchemaDocument Doc(params AuthoredComponentSchema[] components) =>
        new() { Components = [.. components] };

    [Test]
    public async Task merging_unions_the_components_and_orders_them_by_type()
    {
        var merged = AuthoringSchemaReader.Merge(
            Doc(Component("B.Two", 2), Component("A.One", 1)),
            Doc(Component("C.Three", 3)));

        await Assert.That(merged.Components.Select(c => c.Type))
            .IsEquivalentTo(new[] { "A.One", "B.Two", "C.Three" });
    }

    /// <summary>Earlier wins, so a host can pass the ENGINE schema first: a game that copies the
    /// rigidbody's id must not be able to redefine what the exporter bakes.</summary>
    [Test]
    public async Task the_first_source_of_an_id_wins()
    {
        var engine = Doc(new AuthoredComponentSchema
        {
            Id = Paradise.Export.Data.ParadiseComponentIds.Rigidbody,
            Type = "Paradise.Export.Data.RigidbodyComponentData",
            DisplayName = "Engine",
        });
        var game = Doc(new AuthoredComponentSchema
        {
            Id = Paradise.Export.Data.ParadiseComponentIds.Rigidbody,
            Type = "MyGame.Rigidbody",
            DisplayName = "Game",
        });

        var merged = AuthoringSchemaReader.Merge(engine, game);
        await Assert.That(merged.Components.Single().DisplayName).IsEqualTo("Engine");
    }

    /// <summary>An id nobody set is not an id. Merging on Guid.Empty would collapse every such
    /// component onto the first one seen.</summary>
    [Test]
    public async Task components_without_an_id_are_dropped_rather_than_collapsed()
    {
        var merged = AuthoringSchemaReader.Merge(Doc(
            new AuthoredComponentSchema { Type = "A.One" },
            new AuthoredComponentSchema { Type = "B.Two" }));

        await Assert.That(merged.Components).IsEmpty();
    }

    [Test]
    public async Task merging_nothing_yields_an_empty_document()
    {
        await Assert.That(AuthoringSchemaReader.Merge().Components).IsEmpty();
    }

    /// <summary>The engine's own components are published too, so a data-driven editor can build a
    /// UI for them from the same document a game's come through.</summary>
    [Test]
    public async Task the_engines_own_schema_is_readable_and_carries_the_rigidbody()
    {
        var engineJson = global::Paradise.Export.AuthoringSchema.Json;
        var engine = AuthoringSchemaReader.Read(engineJson);
        var rigidbody = engine.Components.Single(
            c => c.Id == Paradise.Export.Data.ParadiseComponentIds.Rigidbody);
        await Assert.That(rigidbody.Type).IsEqualTo("Paradise.Export.Data.RigidbodyComponentData");
        await Assert.That(rigidbody.Fields.Select(f => f.Name)).Contains("Mass");
        await Assert.That(rigidbody.Fields.Single(f => f.Name == "Mass").Unit)
            .IsEqualTo(AuthoredUnits.Kilograms);
        await Assert.That(rigidbody.Fields.Single(f => f.Name == "BodyType").Values)
            .Contains("Dynamic");
    }

    /// <summary>A future reader must refuse a document it cannot understand, rather than silently
    /// dropping the members it does not recognize — "my component vanished from the dropdown" is
    /// the worst possible failure mode here.</summary>
    [Test]
    public async Task a_newer_schema_version_is_rejected_by_name()
    {
        var future = $$"""{"version":{{AuthoringSchemaDocument.CurrentVersion + 1}},"components":[]}""";
        await Assert.That(() => AuthoringSchemaReader.Read(future)).Throws<JsonException>();
    }

    /// <summary>
    /// And a v2 document is refused for the mirror-image reason. Its components are keyed by name,
    /// and nothing here can turn <c>paradise.rigidbody</c> into the GUID it now travels under —
    /// so accepting it would produce a component list where every id is empty.
    /// </summary>
    [Test]
    public async Task a_document_from_before_ids_were_guids_is_rejected_by_name()
    {
        const string v2 = """
        {"version":2,"components":[{"id":"paradise.rigidbody","displayName":"Rigidbody","fields":[]}]}
        """;
        await Assert.That(() => AuthoringSchemaReader.Read(v2)).Throws<JsonException>();
    }
}
