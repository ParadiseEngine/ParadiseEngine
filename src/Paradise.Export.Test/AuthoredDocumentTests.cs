using System.Text.Json;
using Paradise.Authoring;
using Paradise.Export.Data;
using Zio;
using Zio.FileSystems;

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
                { "Id": "{{TestComponentIds.MoverId}}", "Data": {"BodyType":"Dynamic","Mass":2.5} },
                { "Id": "{{LedgeId}}", "Data": {"Friction":0.35,"Label":"north"} }
              ]
            }
            """);

        await Assert.That(document.Get<MoverFixture>().Mass).IsEqualTo(2.5f);
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

    /// <summary>
    /// Defaults are a FRESH instance per miss, deliberately.
    ///
    /// Sharing one would save an allocation on a path that barely runs — a document declaring the
    /// component is the normal case — and would cost an aliasing hazard that does not announce
    /// itself: these records need settable properties for the generated reader, so a caller that
    /// adjusted a defaulted result in place would corrupt every other document's defaults too.
    /// </summary>
    [Test]
    public async Task a_defaulted_component_is_not_shared_between_reads()
    {
        var document = Parse("""{ "Components": [] }""");

        var first = document.Get<LedgeFixture>();
        first.Label = "mutated";

        await Assert.That(document.Get<LedgeFixture>().Label).IsEqualTo("");
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
    public async Task load_reads_from_the_mounted_filesystem_and_names_the_file_in_an_error()
    {
        using var content = new MemoryFileSystem();
        var path = (UPath)"/tuning/authored.json";
        content.CreateDirectory(path.GetDirectory());
        content.WriteAllText(path, """{ "Components": [ { "Id": "nope", "Data": {} } ] }""");

        var thrown = Assert.Throws<InvalidDataException>(
            () => AuthoredDocument.Load(content, path, new LedgeRegistry()));

        await Assert.That(thrown!.Message).Contains(path.FullName);
    }

    /// <summary>
    /// The form is the FILE's to declare, and it stays so through a mount: a build that writes
    /// TOML hands the runtime a <c>.toml</c> document, and nothing between them is configured to
    /// expect one.
    /// </summary>
    [Test]
    public async Task load_bridges_a_toml_document_through_the_one_reader()
    {
        using var content = new MemoryFileSystem();
        var path = (UPath)"/tuning/authored.toml";
        content.CreateDirectory(path.GetDirectory());
        content.WriteAllText(path, $"""
            [[Components]]
            Id = "{LedgeId}"

            [Components.Data]
            Label = "from toml"
            """);

        var document = AuthoredDocument.Load(content, path, new LedgeRegistry());

        await Assert.That(document.Get<LedgeFixture>().Label).IsEqualTo("from toml");
    }


    /// <summary>
    /// The duplicate guard has to key on the RESOLVED TYPE, not on the id in the file.
    ///
    /// Two different ids can land on one record: the first resolves by id, the second by the
    /// Type-name fallback. The id guard waves both through, and the second then overwrites the
    /// first in a map keyed by type — silently, which is the exact failure this document refuses
    /// duplicates to prevent. Reachable precisely BECAUSE of the stale-guid fallback this reader
    /// exists to provide.
    /// </summary>
    [Test]
    public async Task two_ids_resolving_to_one_record_are_refused_rather_than_silently_merged()
    {
        var thrown = Assert.Throws<InvalidDataException>(() => Parse($$"""
            {
              "Components": [
                { "Id": "{{LedgeId}}", "Data": {"Label":"first"} },
                { "Id": "ffffffff-0000-4000-8000-00000000ffff", "Type": "{{LedgeType}}",
                  "Data": {"Label":"second"} }
              ]
            }
            """));

        // Both ids named: the fix is to delete one, and the author needs to know which two collided.
        await Assert.That(thrown!.Message).Contains(LedgeId.ToString());
        await Assert.That(thrown.Message).Contains("ffffffff-0000-4000-8000-00000000ffff");
        await Assert.That(thrown.Message).Contains(nameof(LedgeFixture));
    }


    /// <summary>Order is the file's, not the map's: a dictionary keyed by type enumerates in hash
    /// order, so this has to be tracked rather than projected.</summary>
    [Test]
    public async Task components_come_back_in_document_order()
    {
        var document = Parse($$"""
            {
              "Components": [
                { "Id": "{{LedgeId}}", "Data": {"Label":"first"} },
                { "Id": "{{TestComponentIds.MoverId}}", "Data": {"Mass":1} }
              ]
            }
            """);

        await Assert.That(document.Components.Select(c => c.GetType().Name).ToArray())
            .IsEquivalentTo(new[] { nameof(LedgeFixture), nameof(MoverFixture) });
    }

    [Test]
    public async Task components_is_the_same_list_on_every_read()
    {
        var document = Parse($$"""
            { "Components": [ { "Id": "{{LedgeId}}", "Data": {"Label":"x"} } ] }
            """);

        await Assert.That(document.Components).IsSameReferenceAs(document.Components);
    }

    /// <summary>A component with nothing worth writing is just its id. An absent Data is the same
    /// statement as an absent member keeping its initializer, one level up.</summary>
    [Test]
    public async Task an_omitted_data_materializes_the_record_with_its_defaults()
    {
        var document = Parse($$"""
            { "Components": [ { "Id": "{{LedgeId}}" } ] }
            """);

        await Assert.That(document.Has<LedgeFixture>()).IsTrue();
        await Assert.That(document.Get<LedgeFixture>().Label).IsEqualTo("");
        await Assert.That(document.Unresolved).IsEmpty();
    }

    /// <summary>Present and wrong is not the same as absent — an author who wrote a Data meant
    /// something by it.</summary>
    [Test]
    public async Task a_data_that_is_not_an_object_is_refused()
    {
        var thrown = Assert.Throws<InvalidDataException>(() => Parse($$"""
            { "Components": [ { "Id": "{{LedgeId}}", "Data": 5 } ] }
            """));

        await Assert.That(thrown!.Message).Contains("not an object");
    }

    [Test]
    public async Task with_keeps_a_replaced_component_in_its_original_position()
    {
        var document = Parse($$"""
            {
              "Components": [
                { "Id": "{{LedgeId}}", "Data": {"Label":"first"} },
                { "Id": "{{TestComponentIds.MoverId}}", "Data": {"Mass":1} }
              ]
            }
            """);

        var amended = document.With(new LedgeFixture { Label = "replaced" });

        await Assert.That(amended.Components.Select(c => c.GetType().Name).ToArray())
            .IsEquivalentTo(new[] { nameof(LedgeFixture), nameof(MoverFixture) });
        await Assert.That(amended.Components.Count).IsEqualTo(2);
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
            return component is not null || TestRegistry.Default.TryRead(id, data, out component);
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
