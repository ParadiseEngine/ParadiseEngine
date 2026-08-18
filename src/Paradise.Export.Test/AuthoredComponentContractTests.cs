using System.Text.Json;
using System.Text.Json.Serialization;
using Paradise.Export.Data;
using Paradise.Export.Serialization;

namespace Paradise.Export.Tests;

/// <summary>A game's own authored record. It lives in the TEST assembly on purpose: the engine
/// must be able to carry a type it cannot name, which is the entire point of the mechanism.</summary>
public sealed record LedgeFixture
{
    public float Friction { get; set; }
    public bool IsTrigger { get; set; }
    public string Label { get; set; } = "";
}

[JsonSerializable(typeof(LedgeFixture))]
internal sealed partial class LedgeFixtureJsonContext : JsonSerializerContext;

/// <summary>
/// <see cref="EntityComponentsData.Custom"/>: game-defined components riding along with an entity.
///
/// The engine never deserializes these into a type — it carries the payload verbatim and the game
/// reads it back through its own source-generated context. These tests pin both halves of that,
/// and the promise that a document authoring nothing is unchanged from before the field existed.
/// </summary>
public class AuthoredComponentContractTests
{
    private static LevelData LevelWith(params AuthoredComponentData[] custom)
    {
        var level = new LevelData();
        level.Entities.Add(new LevelEntityData
        {
            Id = "IceLedge",
            Components = new EntityComponentsData { Custom = [.. custom] },
        });
        return level;
    }

    private static AuthoredComponentData Ledge(LedgeFixture ledge) =>
        new()
        {
            Id = "test.ledge",
            Data = JsonSerializer.SerializeToElement(ledge, LedgeFixtureJsonContext.Default.LedgeFixture),
        };

    [Test]
    public async Task an_authored_component_round_trips_back_into_the_games_own_record()
    {
        var authored = new LedgeFixture { Friction = 0.35f, IsTrigger = true, Label = "north" };
        var json = ExportJsonWriter.SerializeToString(LevelWith(Ledge(authored)));

        var custom = ExportJsonReader.ReadLevel(json).Entities.Single().Components.Custom!.Single();
        await Assert.That(custom.Id).IsEqualTo("test.ledge");

        var restored = custom.Data.Deserialize(LedgeFixtureJsonContext.Default.LedgeFixture)!;
        await Assert.That(restored).IsEqualTo(authored);
    }

    /// <summary>The bug this exists to prevent: the prototype serialized every authored value as a
    /// number, so <c>IsTrigger</c> arrived as <c>0</c> and deserialization into a bool failed.
    /// Typed payloads keep their types across the boundary.</summary>
    [Test]
    public async Task payload_values_keep_their_json_types()
    {
        var json = ExportJsonWriter.SerializeToString(
            LevelWith(Ledge(new LedgeFixture { Friction = 0f, IsTrigger = false, Label = "x" })));

        var data = ExportJsonReader.ReadLevel(json).Entities.Single().Components.Custom!.Single().Data;
        await Assert.That(data.GetProperty("IsTrigger").ValueKind).IsEqualTo(JsonValueKind.False);
        await Assert.That(data.GetProperty("Friction").ValueKind).IsEqualTo(JsonValueKind.Number);
        await Assert.That(data.GetProperty("Label").ValueKind).IsEqualTo(JsonValueKind.String);
    }

    [Test]
    public async Task an_entity_can_carry_several_authored_components()
    {
        var level = LevelWith(
            Ledge(new LedgeFixture { Label = "a" }),
            new AuthoredComponentData { Id = "test.other", Data = JsonSerializer.SerializeToElement(7) });

        var custom = ExportJsonReader.ReadLevel(ExportJsonWriter.SerializeToString(level))
            .Entities.Single().Components.Custom!;
        await Assert.That(custom.Select(c => c.Id)).IsEquivalentTo(new[] { "test.ledge", "test.other" });
    }

    /// <summary>
    /// The compatibility promise. The contract writes with <c>DefaultIgnoreCondition = Never</c>,
    /// so without the <c>[JsonIgnore(WhenWritingNull)]</c> guard this new field would add
    /// <c>"Custom": null</c> to every entity of every document the four games export. Four games
    /// consume this contract; this is what makes "purely additive" a fact rather than a claim.
    /// </summary>
    [Test]
    public async Task a_document_that_authors_nothing_does_not_mention_custom()
    {
        var level = new LevelData();
        level.Entities.Add(new LevelEntityData
        {
            Id = "Ground",
            Components = new EntityComponentsData
            {
                Rigidbody = new RigidbodyComponentData { BodyType = PhysicsBodyType.Static },
            },
        });

        var json = ExportJsonWriter.SerializeToString(level);
        await Assert.That(json).DoesNotContain("Custom");

        // ...and reading it back leaves the field null rather than an empty list, so a consumer
        // can still tell "nothing was authored" from "an empty list was authored".
        await Assert.That(ExportJsonReader.ReadLevel(json).Entities.Single().Components.Custom).IsNull();
    }

    /// <summary>An older document, written before the field existed, must still read.</summary>
    [Test]
    public async Task a_document_from_before_the_field_existed_still_reads()
    {
        const string legacy = """
        {
          "SchemaVersion": 2,
          "Entities": [
            { "Id": "Ground", "Components": { "Renderable": null } }
          ]
        }
        """;

        var level = ExportJsonReader.ReadLevel(legacy);
        await Assert.That(level.Entities.Single().Components.Custom).IsNull();
    }
}
