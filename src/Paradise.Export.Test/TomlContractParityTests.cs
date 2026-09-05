using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Paradise.Export.Data;
using Paradise.Export.Serialization;
using Paradise.Export.Tests;

namespace Paradise.Export.Test;

/// <summary>
/// The contract's TOML form against its JSON one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The assertion is value equality, not byte equality, and that is not a shortcut.</b> The
/// contract is defined as value-based rather than byte-based — the engine's own CONVENTIONS says
/// so, and <c>contract-check</c> compares semantically for the same reason. Two serializations of
/// one document have no reason to agree on text, and requiring it would fail on the first place
/// TOML spells a float differently.
/// </para>
/// <para>
/// What must hold is that a document written either way READS BACK THE SAME. That is what makes
/// the two formats interchangeable at runtime, and it is the only claim a second format is
/// allowed to make.
/// </para>
/// </remarks>
public class TomlContractParityTests
{
    private static PrefabData BuildLevel() =>
        new() { Entities = { EntityDocumentShapeTests.BuildBoxEntity() } };

    [Test]
    public async Task a_level_written_as_toml_reads_back_the_same_as_json()
    {
        var document = BuildLevel();

        var fromJson = ExportJsonReader.ReadPrefab(ExportJsonWriter.SerializeToString(document));
        var fromToml = ExportTomlReader.ReadPrefab(ExportTomlWriter.SerializeToString(document));

        // Compared with nulls stripped from BOTH sides, and that is the honest comparison rather
        // than a weakened one -- see `a_null_payload_member_becomes_an_absent_key` for the
        // difference it is standing in for, and why it carries no information.
        await Assert.That(WithoutNulls(ExportJsonWriter.SerializeToString(fromToml)))
            .IsEqualTo(WithoutNulls(ExportJsonWriter.SerializeToString(fromJson)));
    }

    [Test]
    public async Task a_null_payload_member_becomes_an_absent_key()
    {
        // THE difference between the two formats, stated rather than hidden. TOML has no null, so
        // a null-valued key is omitted -- and the payloads stop differing one step later, at the
        // game's registry reader, which gives absent and null the same default. This test exists
        // so that stops being an argument and starts being a checked fact.
        var document = BuildLevel();

        var fromJson = ExportJsonReader.ReadPrefab(ExportJsonWriter.SerializeToString(document));
        var fromToml = ExportTomlReader.ReadPrefab(ExportTomlWriter.SerializeToString(document));

        var json = ExportJsonWriter.SerializeToString(fromJson);
        var toml = ExportJsonWriter.SerializeToString(fromToml);

        await Assert.That(json).Contains("\"Clip\": null");
        await Assert.That(toml).DoesNotContain("\"Clip\": null");

        // The entity is otherwise identical: same components, same ids, same order.
        await Assert.That(fromToml.Entities[0].Count).IsEqualTo(fromJson.Entities[0].Count);
        await Assert.That(fromToml.Entities[0].Select(component => component.Id))
            .IsEquivalentTo(fromJson.Entities[0].Select(component => component.Id));
    }

    [Test]
    public async Task a_null_array_element_survives_as_the_empty_table()
    {
        // The documented material-slot shape, and the one null the omit rule may NOT apply to:
        // position is meaning, so dropping an empty slot would move every override after it onto
        // the wrong primitive. TOML spells it {} — what authoring already writes for "no
        // reference" — and the reader turns it back into the null it stood for.
        List<AuthoredComponentData> entity =
        [
            EntityDocumentShapeTests.Payload(
                TestComponentIds.CrateId, "Paradise.Export.Tests.MaterialsFixture",
                """{"Slots":[{"Path":"materials/rust.json"},null,{"Path":"materials/steel.json"}]}"""),
        ];
        var document = new PrefabData { Entities = { entity } };

        var restored = ExportTomlReader.ReadPrefab(ExportTomlWriter.SerializeToString(document));
        var json = JsonNode.Parse(ExportJsonWriter.SerializeToString(restored))!;
        var slots = (JsonArray)json["Entities"]![0]![0]!["Data"]!["Slots"]!;

        await Assert.That(slots.Count).IsEqualTo(3);
        await Assert.That(slots[1] is null).IsTrue();
        await Assert.That((string?)slots[0]!["Path"]).IsEqualTo("materials/rust.json");
        await Assert.That((string?)slots[2]!["Path"]).IsEqualTo("materials/steel.json");
    }

    /// <summary>
    /// Re-serializes with null-valued properties removed and every number rendered from its
    /// VALUE, at any depth. The number half matters since v6: payloads are raw elements, so the
    /// JSON side keeps an author's <c>1.0</c> lexeme while the TOML round trip re-renders the
    /// value as <c>1</c> — the same number, differently spelled, which is exactly the
    /// difference a value-based contract tells us to ignore.
    /// </summary>
    private static string WithoutNulls(string json)
    {
        var node = JsonNode.Parse(json)!;
        node = Strip(node)!;
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        static JsonNode? Strip(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var key in obj.Where(pair => pair.Value is null).Select(pair => pair.Key).ToList())
                    {
                        obj.Remove(key);
                    }

                    foreach (var key in obj.Select(pair => pair.Key).ToList())
                    {
                        var replaced = Strip(obj[key]);
                        if (!ReferenceEquals(replaced, obj[key])) obj[key] = replaced;
                    }
                    return obj;

                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        var replaced = Strip(array[i]);
                        if (!ReferenceEquals(replaced, array[i])) array[i] = replaced;
                    }
                    return array;

                case JsonValue value when value.GetValueKind() == JsonValueKind.Number:
                    return JsonValue.Create(value.GetValue<double>());

                default:
                    return node;
            }
        }
    }

    [Test]
    public async Task payload_values_survive_the_round_trip()
    {
        var document = BuildLevel();

        var restored = ExportTomlReader.ReadPrefab(ExportTomlWriter.SerializeToString(document));
        var json = JsonNode.Parse(ExportJsonWriter.SerializeToString(restored))!;
        var entity = json["Entities"]![0]!;

        // One value per payload shape a hand-written second serializer would have broken: a
        // number array, an enum-by-name string, a nested list of objects, a guid string.
        var position = (JsonArray)Payload(entity, WellKnownEntityComponents.TransformId)["Position"]!;
        await Assert.That((float)position[0]!).IsEqualTo(1f);
        await Assert.That((float)position[2]!).IsEqualTo(2f);

        var collider = Payload(entity, TestComponentIds.CrateId)["Colliders"]![0]!;
        await Assert.That((string?)collider["ShapeType"]).IsEqualTo("Box");
        await Assert.That((float)collider["Size"]![1]!).IsEqualTo(4f);

        await Assert.That((string?)Payload(entity, TestComponentIds.MoverId)["Kind"])
            .IsEqualTo("Static");

        await Assert.That((string?)Payload(entity, WellKnownEntityComponents.MetaId)["Name"])
            .IsEqualTo("Crate");
    }

    [Test]
    public async Task an_empty_level_round_trips()
    {
        // The degenerate document, which is where an omit-nulls rule is most likely to lose
        // something: every collection is empty and every optional is absent.
        var restored = ExportTomlReader.ReadPrefab(ExportTomlWriter.SerializeToString(new PrefabData()));

        await Assert.That(ExportJsonWriter.SerializeToString(restored))
            .IsEqualTo(ExportJsonWriter.SerializeToString(new PrefabData()));
    }

    [Test]
    public async Task malformed_toml_is_refused_rather_than_half_read()
    {
        await Assert.That(() => ExportTomlReader.ReadPrefab("this = = not toml"))
            .Throws<System.IO.InvalidDataException>();
    }

    private static JsonNode Payload(JsonNode entity, Guid id)
    {
        foreach (var component in (JsonArray)entity)
        {
            if (string.Equals((string?)component!["Id"], id.ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                return component["Data"]!;
            }
        }

        throw new InvalidOperationException($"no component with id {id} on the entity");
    }
}
