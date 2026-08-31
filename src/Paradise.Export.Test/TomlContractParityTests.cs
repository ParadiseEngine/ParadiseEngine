using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Paradise.Export.Data;
using Paradise.Export.Geometry;
using Paradise.Export.Serialization;

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
    private static readonly Vector3 Position = new(1f, 0f, 2f);

    private static LevelData BuildLevel()
    {
        var components = new List<AuthoredComponentData>();
        components.Set(new NameComponentData { Value = "Crate" });
        components.Set(new TransformComponentData
        {
            World = ContractMatrix.Trs(Position, Quaternion.Identity, Vector3.One),
        });
        components.Set(new RenderableComponentData());
        components.Set(new ColliderComponentData
        {
            Colliders = new List<ColliderShapeData>
            {
                new()
                {
                    Id = "Box",
                    Path = "",
                    ShapeType = PhysicsShapeType.Box,
                    Size = ColliderScaleFold.BoxSize(new Vector3(2f, 4f, 6f), Vector3.One),
                },
            },
        });
        components.Set(new RigidbodyComponentData { BodyType = PhysicsBodyType.Static, Mass = 0f });

        return new LevelData { Entities = { components } };
    }

    [Test]
    public async Task a_level_written_as_toml_reads_back_the_same_as_json()
    {
        var document = BuildLevel();

        var fromJson = ExportJsonReader.ReadLevel(ExportJsonWriter.SerializeToString(document));
        var fromToml = ExportTomlReader.ReadLevel(ExportTomlWriter.SerializeToString(document));

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
        // a null-valued key is omitted -- and for a TYPED member that is lossless, because an
        // absent key deserializes to the member's default, which is what the null was.
        //
        // A component payload is NOT typed here: AuthoredComponentData.Data is a raw JsonElement,
        // so `{"Mesh": null}` and `{}` are genuinely different elements. They stop differing one
        // step later, at AuthoredComponentRouter, which deserializes the payload into its record
        // and gives absent and null the same default -- so no consumer can tell them apart. This
        // test exists so that stops being an argument and starts being a checked fact.
        var document = BuildLevel();

        var fromJson = ExportJsonReader.ReadLevel(ExportJsonWriter.SerializeToString(document));
        var fromToml = ExportTomlReader.ReadLevel(ExportTomlWriter.SerializeToString(document));

        var json = ExportJsonWriter.SerializeToString(fromJson);
        var toml = ExportJsonWriter.SerializeToString(fromToml);

        await Assert.That(json).Contains("\"Mesh\": null");
        await Assert.That(toml).DoesNotContain("\"Mesh\": null");

        // The entity is otherwise identical: same components, same ids, same order. The absent
        // key is the ONLY difference, which is what makes it safe to normalize away above.
        await Assert.That(fromToml.Entities[0].Count).IsEqualTo(fromJson.Entities[0].Count);
        await Assert.That(fromToml.Entities[0].Select(component => component.Id))
            .IsEquivalentTo(fromJson.Entities[0].Select(component => component.Id));
    }

    /// <summary>Re-serializes with null-valued properties removed, at any depth.</summary>
    private static string WithoutNulls(string json)
    {
        var node = JsonNode.Parse(json)!;
        Strip(node);
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        static void Strip(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var key in obj.Where(pair => pair.Value is null).Select(pair => pair.Key).ToList())
                    {
                        obj.Remove(key);
                    }

                    foreach (var (_, value) in obj) Strip(value);
                    break;

                case JsonArray array:
                    foreach (var item in array) Strip(item);
                    break;
            }
        }
    }

    [Test]
    public async Task the_values_that_have_converters_survive_the_round_trip()
    {
        var document = BuildLevel();

        var restored = ExportTomlReader.ReadLevel(ExportTomlWriter.SerializeToString(document));
        var json = JsonNode.Parse(ExportJsonWriter.SerializeToString(restored))!;
        var entity = json["Entities"]![0]!;

        // One value per converter, because a converter is exactly what a hand-written second
        // serializer would have failed to apply: a matrix (float[16], column-major), an enum by
        // name, and a vector.
        var world = (JsonArray)Payload(entity, typeof(TransformComponentData))["World"]!;
        await Assert.That((float)world[12]!).IsEqualTo(1f);
        await Assert.That((float)world[14]!).IsEqualTo(2f);

        var collider = Payload(entity, typeof(ColliderComponentData))["Colliders"]![0]!;
        await Assert.That((string?)collider["ShapeType"]).IsEqualTo("Box");
        await Assert.That((float)collider["Size"]![1]!).IsEqualTo(4f);

        await Assert.That((string?)Payload(entity, typeof(RigidbodyComponentData))["BodyType"])
            .IsEqualTo("Static");

        await Assert.That((string?)Payload(entity, typeof(NameComponentData))["Value"])
            .IsEqualTo("Crate");
    }

    [Test]
    public async Task an_empty_level_round_trips()
    {
        // The degenerate document, which is where an omit-nulls rule is most likely to lose
        // something: every collection is empty and every optional is absent.
        var restored = ExportTomlReader.ReadLevel(ExportTomlWriter.SerializeToString(new LevelData()));

        await Assert.That(ExportJsonWriter.SerializeToString(restored))
            .IsEqualTo(ExportJsonWriter.SerializeToString(new LevelData()));
    }

    [Test]
    public async Task malformed_toml_is_refused_rather_than_half_read()
    {
        await Assert.That(() => ExportTomlReader.ReadLevel("this = = not toml"))
            .Throws<System.IO.InvalidDataException>();
    }

    private static JsonNode Payload(JsonNode entity, Type type)
    {
        var id = type.GUID.ToString("D");
        foreach (var component in (JsonArray)entity)
        {
            if (string.Equals((string?)component!["Id"], id, StringComparison.OrdinalIgnoreCase))
            {
                return component["Data"]!;
            }
        }

        throw new InvalidOperationException($"no component with id {id} on the entity");
    }
}
