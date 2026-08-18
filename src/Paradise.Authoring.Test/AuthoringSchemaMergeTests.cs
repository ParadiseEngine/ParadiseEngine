using System.Text.Json;
using Paradise.Authoring;

namespace Paradise.Authoring.Test;

/// <summary>
/// An editor faces more than one schema: the engine's own components and the game's. Merging is in
/// the engine so every host presents one list without reimplementing the join.
/// </summary>
public class AuthoringSchemaMergeTests
{
    private static AuthoringSchemaDocument Doc(params string[] ids) => new()
    {
        Components = [.. ids.Select(id => new AuthoredComponentSchema { Id = id, DisplayName = id })],
    };

    [Test]
    public async Task merging_unions_the_components_and_orders_them()
    {
        var merged = AuthoringSchemaReader.Merge(Doc("b.two", "a.one"), Doc("c.three"));
        await Assert.That(merged.Components.Select(c => c.Id))
            .IsEquivalentTo(new[] { "a.one", "b.two", "c.three" });
    }

    /// <summary>Earlier wins, so a host can pass the ENGINE schema first: a game that reuses
    /// <c>paradise.rigidbody</c> must not be able to redefine what the exporter bakes.</summary>
    [Test]
    public async Task the_first_source_of_an_id_wins()
    {
        var engine = new AuthoringSchemaDocument
        {
            Components = [new AuthoredComponentSchema { Id = "paradise.rigidbody", DisplayName = "Engine" }],
        };
        var game = new AuthoringSchemaDocument
        {
            Components = [new AuthoredComponentSchema { Id = "paradise.rigidbody", DisplayName = "Game" }],
        };

        var merged = AuthoringSchemaReader.Merge(engine, game);
        await Assert.That(merged.Components.Single().DisplayName).IsEqualTo("Engine");
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
        var rigidbody = engine.Components.Single(c => c.Id == "paradise.rigidbody");
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
}
