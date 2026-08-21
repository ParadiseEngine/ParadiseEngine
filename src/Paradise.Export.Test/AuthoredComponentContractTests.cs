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
/// Game-defined components riding along with an entity in <see cref="LevelEntityData.Components"/>.
///
/// The engine never deserializes these into a type — it carries the payload verbatim and the game
/// reads it back through its own source-generated context. These tests pin both halves of that.
/// They used to also pin "a document authoring nothing does not mention Custom", which was about
/// keeping the key absent from older files; there is no separate key to be absent now.
/// </summary>
public class AuthoredComponentContractTests
{
    private static LevelData LevelWith(params AuthoredComponentData[] custom)
    {
        var level = new LevelData();
        level.Entities.Add(new LevelEntityData
        {
            Id = "IceLedge",
            Components = [.. custom],
        });
        return level;
    }

    private static readonly Guid LedgeId = new("f0000000-0000-4000-8000-000000000001");
    private static readonly Guid OtherId = new("f0000000-0000-4000-8000-000000000002");

    private static AuthoredComponentData Ledge(LedgeFixture ledge) =>
        new()
        {
            Id = LedgeId,
            Type = "Paradise.Export.Tests.LedgeFixture",
            Data = JsonSerializer.SerializeToElement(ledge, LedgeFixtureJsonContext.Default.LedgeFixture),
        };

    [Test]
    public async Task an_authored_component_round_trips_back_into_the_games_own_record()
    {
        var authored = new LedgeFixture { Friction = 0.35f, IsTrigger = true, Label = "north" };
        var json = ExportJsonWriter.SerializeToString(LevelWith(Ledge(authored)));

        var custom = ExportJsonReader.ReadLevel(json).Entities.Single().Components!.Single();
        await Assert.That(custom.Id).IsEqualTo(LedgeId);
        await Assert.That(custom.Type).IsEqualTo("Paradise.Export.Tests.LedgeFixture");

        var restored = custom.Data.Deserialize(LedgeFixtureJsonContext.Default.LedgeFixture)!;
        await Assert.That(restored).IsEqualTo(authored);
    }

    /// <summary>The id travels as its canonical string, not as a number or a byte array — the
    /// document has to stay something a non-C# host can write with its own GUID library.</summary>
    [Test]
    public async Task the_id_travels_as_a_canonical_guid_string()
    {
        var json = ExportJsonWriter.SerializeToString(LevelWith(Ledge(new LedgeFixture())));
        await Assert.That(json).Contains(LedgeId.ToString("D"));
    }

    /// <summary>The bug this exists to prevent: the prototype serialized every authored value as a
    /// number, so <c>IsTrigger</c> arrived as <c>0</c> and deserialization into a bool failed.
    /// Typed payloads keep their types across the boundary.</summary>
    [Test]
    public async Task payload_values_keep_their_json_types()
    {
        var json = ExportJsonWriter.SerializeToString(
            LevelWith(Ledge(new LedgeFixture { Friction = 0f, IsTrigger = false, Label = "x" })));

        var data = ExportJsonReader.ReadLevel(json).Entities.Single().Components!.Single().Data;
        await Assert.That(data.GetProperty("IsTrigger").ValueKind).IsEqualTo(JsonValueKind.False);
        await Assert.That(data.GetProperty("Friction").ValueKind).IsEqualTo(JsonValueKind.Number);
        await Assert.That(data.GetProperty("Label").ValueKind).IsEqualTo(JsonValueKind.String);
    }

    [Test]
    public async Task an_entity_can_carry_several_authored_components()
    {
        var level = LevelWith(
            Ledge(new LedgeFixture { Label = "a" }),
            new AuthoredComponentData { Id = OtherId, Data = JsonSerializer.SerializeToElement(7) });

        var custom = ExportJsonReader.ReadLevel(ExportJsonWriter.SerializeToString(level))
            .Entities.Single().Components!;
        await Assert.That(custom.Select(c => c.Id)).IsEquivalentTo(new[] { LedgeId, OtherId });
    }

    /// <summary>The type name is optional on the wire, so a payload written without it still
    /// reads — it is a repair path, not a second required key.</summary>
    [Test]
    public async Task a_payload_without_a_type_name_still_reads()
    {
        var level = LevelWith(
            new AuthoredComponentData { Id = OtherId, Data = JsonSerializer.SerializeToElement(7) });

        var custom = ExportJsonReader.ReadLevel(ExportJsonWriter.SerializeToString(level))
            .Entities.Single().Components!.Single();
        await Assert.That(custom.Id).IsEqualTo(OtherId);
        await Assert.That(custom.Type).IsNull();
    }

    /// <summary>
    /// The compatibility promise. The contract writes with <c>DefaultIgnoreCondition = Never</c>,
    /// so without the <c>[JsonIgnore(WhenWritingNull)]</c> guard this new field would add
    /// <c>"Custom": null</c> to every entity of every document the four games export. Four games
    /// consume this contract; this is what makes "purely additive" a fact rather than a claim.
    /// </summary>
    [Test]
    public async Task a_document_that_authors_nothing_carries_an_empty_list()
    {
        // This used to assert the word "Custom" was absent from the written file — the trick that
        // kept scenes exported before authored components existed byte-identical. There is no
        // separate key to be absent any more: an entity that authors nothing has an empty array,
        // which says the same thing in the one place components are described.
        var level = new LevelData();
        level.Entities.Add(new LevelEntityData { Id = "Ground" });

        string json = ExportJsonWriter.SerializeToString(level);
        await Assert.That(json).Contains("\"Components\": []");
        await Assert.That(ExportJsonReader.ReadLevel(json).Entities.Single().Components).IsEmpty();
    }

    [Test]
    public async Task a_document_from_before_the_component_list_is_refused_by_version()
    {
        // v3 is the first change to this document that is NOT additive: v2 wrote "Components" as
        // an object of named slots, v3 writes an array. There is no way back — a slot cannot be
        // mapped to the id it came from without the very table this change deleted — so the
        // document is refused by version, naming it, rather than deserialized into entities that
        // silently author nothing.
        const string legacy = """
        {
          "SchemaVersion": 2,
          "Entities": [
            { "Id": "Ground", "Components": { "Renderable": null } }
          ]
        }
        """;

        var thrown = Assert.Throws<JsonException>(() => ExportJsonReader.ReadLevel(legacy));
        await Assert.That(thrown!.Message).Contains("version 2");
        await Assert.That(thrown.Message).Contains("Re-export");
    }

    [Test]
    public async Task a_document_from_a_newer_build_is_refused_too()
    {
        var thrown = Assert.Throws<JsonException>(
            () => ExportJsonReader.ReadLevel("""{"SchemaVersion":99,"Entities":[]}"""));
        await Assert.That(thrown!.Message).Contains("version 99");
    }
}
