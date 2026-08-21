using System.Text.Json;
using Paradise.Authoring;
using Paradise.Export.Data;

namespace Paradise.Export.Tests;

/// <summary>
/// A file of authored components, with no entity in it.
///
/// The shape a game's tuning file has, and the reason this type exists: reading one used to mean
/// hand-rolling a loop over the registry in every game, which is where the Type fallback went
/// missing. These pin the reading, and — just as importantly — the narrow set of things the
/// document REFUSES, because everything else is deliberately the caller's to decide.
/// </summary>
public class AuthoredDocumentTests
{
    private static readonly Guid LedgeId = new("f0000000-0000-4000-8000-000000000001");
    private const string LedgeType = "Paradise.Export.Tests.LedgeFixture";

    private static AuthoredDocument Parse(string json) =>
        AuthoredDocument.Parse(json, new LedgeRegistry(), "config.json");

    // ---- reading ------------------------------------------------------------------------

    [Test]
    public async Task a_document_reads_engine_and_game_components_alike()
    {
        var document = Parse($$"""
            {
              "Components": [
                { "Id": "{{ParadiseComponentIds.Rigidbody}}", "Data": {"BodyType":"Dynamic","Mass":2.5} },
                { "Id": "{{LedgeId}}", "Data": {"Friction":0.35,"Label":"north"} }
              ]
            }
            """);

        await Assert.That(document.Get<RigidbodyComponentData>().Mass).IsEqualTo(2.5f);
        await Assert.That(document.Get<LedgeFixture>().Label).IsEqualTo("north");
    }

    /// <summary>The behaviour a caller leans on everywhere: an absent component is its record's
    /// defaults, exactly as an absent MEMBER keeps its initializer.</summary>
    [Test]
    public async Task an_absent_component_comes_back_as_record_defaults()
    {
        var document = Parse("""{ "Components": [] }""");

        await Assert.That(document.Get<LedgeFixture>().Label).IsEqualTo("");
        await Assert.That(document.Has<LedgeFixture>()).IsFalse();
    }

    /// <summary>Defaults are SHARED, not freshly allocated per read: these are read from systems
    /// that run per frame, and a miss must not allocate.</summary>
    [Test]
    public async Task defaults_are_the_same_instance_every_time()
    {
        var document = Parse("""{ "Components": [] }""");

        await Assert.That(document.Get<LedgeFixture>()).IsSameReferenceAs(document.Get<LedgeFixture>());
    }

    [Test]
    public async Task a_document_with_no_components_key_is_empty_rather_than_an_error()
    {
        // A file may legitimately declare nothing and lean entirely on record defaults.
        var document = Parse("""{ "// note": "nothing authored yet" }""");

        await Assert.That(document.Components).IsEmpty();
        await Assert.That(document.Get<LedgeFixture>().Friction).IsEqualTo(0f);
    }

    /// <summary>Hand-edited by design, so the parser tolerates what a person writes.</summary>
    [Test]
    public async Task comments_and_a_trailing_comma_are_tolerated()
    {
        var document = Parse($$"""
            {
              // the tuning lives here
              "Components": [
                { "Id": "{{LedgeId}}", "Data": {"Label":"commented"} },
              ],
            }
            """);

        await Assert.That(document.Get<LedgeFixture>().Label).IsEqualTo("commented");
    }

    /// <summary>The gain over a hand-rolled loop: a payload whose id no registry knows still loads
    /// by type name, so regenerating a [Guid] does not make the file unreadable.</summary>
    [Test]
    public async Task an_unknown_id_still_loads_by_type_name()
    {
        var document = Parse($$"""
            {
              "Components": [
                { "Id": "ffffffff-0000-4000-8000-00000000ffff", "Type": "{{LedgeType}}",
                  "Data": {"Label":"survived"} }
              ]
            }
            """);

        await Assert.That(document.Get<LedgeFixture>().Label).IsEqualTo("survived");
        await Assert.That(document.Unresolved).IsEmpty();
    }

    // ---- what it refuses, and what it merely reports -------------------------------------

    /// <summary>Refused because the document cannot REPRESENT it: this is a map keyed by type, so
    /// a second payload has nowhere to go and quietly keeping the last is the edit that looks
    /// applied and is not.</summary>
    [Test]
    public async Task a_component_declared_twice_is_refused()
    {
        var thrown = Assert.Throws<InvalidDataException>(() => Parse($$"""
            {
              "Components": [
                { "Id": "{{LedgeId}}", "Data": {"Label":"first"} },
                { "Id": "{{LedgeId}}", "Data": {"Label":"second"} }
              ]
            }
            """));

        await Assert.That(thrown!.Message).Contains(LedgeId.ToString());
        await Assert.That(thrown.Message).Contains("twice");
    }

    /// <summary>The case that actually happens: a document written before ids became GUIDs. Named
    /// where it is written rather than left to a JsonException about a token position.</summary>
    [Test]
    public async Task an_id_that_is_not_a_guid_is_refused_by_name()
    {
        var thrown = Assert.Throws<InvalidDataException>(() => Parse("""
            { "Components": [ { "Id": "shiningpie.tuning.player", "Data": {} } ] }
            """));

        await Assert.That(thrown!.Message).Contains("shiningpie.tuning.player");
        await Assert.That(thrown.Message).Contains("not a GUID");
    }

    /// <summary>REPORTED, not refused. Whether an unreadable payload is fatal depends on what the
    /// document is — a game's tuning refuses to start, a level's settings may not care — and this
    /// type cannot know which.</summary>
    [Test]
    public async Task an_unreadable_payload_is_reported_rather_than_thrown()
    {
        var stranger = new Guid("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
        var document = Parse($$"""
            {
              "Components": [
                { "Id": "{{LedgeId}}", "Data": {"Label":"kept"} },
                { "Id": "{{stranger}}", "Type": "Someone.Else", "Data": {"Whatever":1} }
              ]
            }
            """);

        await Assert.That(document.Get<LedgeFixture>().Label).IsEqualTo("kept");
        // The whole component, so a caller's message can name the type as well as the id.
        await Assert.That(document.Unresolved.Single().Id).IsEqualTo(stranger);
        await Assert.That(document.Unresolved.Single().Type).IsEqualTo("Someone.Else");
    }

    /// <summary>An unresolved payload's Data outlives the parse. It is cloned on the way in — the
    /// JsonDocument it came from is disposed when parsing ends, and without the clone this throws
    /// the moment a caller reads it.</summary>
    [Test]
    public async Task an_unresolved_payload_can_still_be_read_after_parsing()
    {
        var document = Parse("""
            {
              "Components": [
                { "Id": "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", "Data": {"Whatever":7} }
              ]
            }
            """);

        await Assert.That(document.Unresolved.Single().Data.GetProperty("Whatever").GetInt32())
            .IsEqualTo(7);
    }

    [Test]
    public async Task a_root_that_is_not_an_object_is_refused()
    {
        var thrown = Assert.Throws<InvalidDataException>(() => Parse("[]"));
        await Assert.That(thrown!.Message).Contains("must be a JSON object");
    }

    [Test]
    public async Task a_components_key_that_is_not_an_array_is_refused()
    {
        var thrown = Assert.Throws<InvalidDataException>(
            () => Parse("""{ "Components": {} }"""));
        await Assert.That(thrown!.Message).Contains("must be an array");
    }

    // ---- With ---------------------------------------------------------------------------

    [Test]
    public async Task with_replaces_a_component_and_leaves_the_original_alone()
    {
        var document = Parse($$"""
            { "Components": [ { "Id": "{{LedgeId}}", "Data": {"Label":"original"} } ] }
            """);

        var amended = document.With(new LedgeFixture { Label = "replaced" });

        await Assert.That(amended.Get<LedgeFixture>().Label).IsEqualTo("replaced");
        await Assert.That(document.Get<LedgeFixture>().Label).IsEqualTo("original");
    }

    [Test]
    public async Task with_keys_on_the_runtime_type_not_the_static_one()
    {
        // The loader only ever has a component as `object`; it must land under the key Get<T>()
        // looks up, or a document would read back empty.
        object component = new LedgeFixture { Label = "boxed" };

        await Assert.That(AuthoredDocument.Empty.With(component).Get<LedgeFixture>().Label)
            .IsEqualTo("boxed");
    }

    // ---- Load ---------------------------------------------------------------------------

    [Test]
    public async Task load_reads_from_disk_and_names_the_file_in_an_error()
    {
        string path = Path.Combine(Path.GetTempPath(), $"authored-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "Components": [ { "Id": "nope", "Data": {} } ] }""");
            var thrown = Assert.Throws<InvalidDataException>(
                () => AuthoredDocument.Load(path, new LedgeRegistry()));
            await Assert.That(thrown!.Message).Contains(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Stands in for the registry a game's [Authored] records generate.</summary>
    private sealed class LedgeRegistry : IAuthoredComponentRegistry
    {
        public IReadOnlyCollection<Guid> ComponentIds { get; } = new[] { LedgeId };

        public bool TryRead(Guid id, JsonElement data, out object? component)
        {
            component = id == LedgeId
                ? data.Deserialize(LedgeFixtureJsonContext.Default.LedgeFixture)
                : null;
            return component is not null;
        }

        public bool TryReadByType(string fullTypeName, JsonElement data, out object? component)
        {
            component = fullTypeName == LedgeType
                ? data.Deserialize(LedgeFixtureJsonContext.Default.LedgeFixture)
                : null;
            return component is not null;
        }
    }
}
