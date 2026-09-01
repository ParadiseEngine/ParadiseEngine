using System.Runtime.InteropServices;
using System.Text.Json;
using Paradise.Export.Data;

namespace Paradise.Export.Tests;

/// <summary>
/// The router over an id-and-payload document. Since contract v6 there is no engine tier: the
/// caller's registry — here the test assembly's own generated one, the exact mechanism a game
/// uses — is the only lookup, and these tests pin that each id comes back as the record the
/// registry declares while everything unknown is reported rather than dropped.
/// </summary>
public class AuthoredComponentRouterTests
{
    /// <summary>A component read through a HAND-WRITTEN registry, standing in for a game that
    /// implements <c>IAuthoredComponentRegistry</c> itself rather than generating it.</summary>
    private static readonly Guid LedgeId = new("f0000000-0000-4000-8000-000000000001");

    private const string LedgeType = "Paradise.Export.Tests.LedgeFixture";

    private static AuthoredComponentData Payload(Guid id, string json, string? type = null) =>
        new() { Id = id, Type = type, Data = JsonDocument.Parse(json).RootElement.Clone() };

    /// <summary>An object, which since schema v5 is nothing but its components.</summary>
    private static List<AuthoredComponentData> Object(params AuthoredComponentData[] components) =>
        [.. components];

    [Test]
    public async Task every_fixture_component_materializes_as_its_record()
    {
        var entity = Object(
            Payload(TestComponentIds.MoverId, """{"Kind":"Dynamic","Mass":2.5,"MoveSpeed":3.5}"""),
            Payload(TestComponentIds.GlowId, """{"ShadowMapSize":4096}"""),
            Payload(TestComponentIds.CrateId,
                """{"Colliders":[{"ShapeType":"Box","Size":[1,2,3],"IsTrigger":true}]}"""));

        var instances = AuthoredComponentRouter.Materialize(entity, TestRegistry.Default);

        // Three payloads in, three records out, each found by the id its own [Guid] declares.
        await Assert.That(instances.OfType<MoverFixture>().Single().MoveSpeed).IsEqualTo(3.5f);
        await Assert.That(instances.OfType<GlowFixture>().Single().ShadowMapSize).IsEqualTo(4096);
        await Assert.That(instances.OfType<CrateFixture>().Single().Colliders.Single().IsTrigger).IsTrue();
    }

    /// <summary>The reason Custom exists. A component whose id nothing claims is carried
    /// verbatim, because the reader cannot name its type and does not try.</summary>
    [Test]
    public async Task an_unknown_id_is_carried_verbatim()
    {
        var entity = Object(Payload(LedgeId, """{"Friction":0.35,"IsTrigger":false}""", LedgeType));

        var custom = entity.Single();
        await Assert.That(custom.Id).IsEqualTo(LedgeId);
        await Assert.That(custom.Type).IsEqualTo(LedgeType);
        await Assert.That(custom.Data.GetProperty("Friction").GetSingle()).IsEqualTo(0.35f);
        await Assert.That(custom.Data.GetProperty("IsTrigger").ValueKind).IsEqualTo(JsonValueKind.False);
    }

    /// <summary>The loading half: one registry, one list of records back.</summary>
    [Test]
    public async Task materialize_returns_registry_components_as_instances()
    {
        var entity = Object(
            Payload(TestComponentIds.MoverId, """{"Kind":"Dynamic","Mass":2.5}"""),
            Payload(TestComponentIds.CrateId,
                """{"Colliders":[{"ShapeType":"Sphere","IsTrigger":true}]}"""));

        var instances = AuthoredComponentRouter.Materialize(entity, TestRegistry.Default);

        var mover = instances.OfType<MoverFixture>().Single();
        await Assert.That(mover.Kind).IsEqualTo(MoverKind.Dynamic);
        await Assert.That(mover.Mass).IsEqualTo(2.5f);
        var crate = instances.OfType<CrateFixture>().Single();
        await Assert.That(crate.Colliders.Single().ShapeType).IsEqualTo(PhysicsShapeType.Sphere);
        await Assert.That(crate.Colliders.Single().IsTrigger).IsTrue();
    }

    /// <summary>Since v6 there is no engine tier: with no registry, NOTHING materializes and
    /// every payload is reported — the honest answer for a reader with no declarations.</summary>
    [Test]
    public async Task materialize_without_a_registry_reports_everything()
    {
        var entity = Object(
            Payload(TestComponentIds.MoverId, """{"MoveSpeed":3}"""),
            Payload(LedgeId, """{"Friction":0.1}"""));

        var unresolved = new List<AuthoredComponentData>();
        var instances = AuthoredComponentRouter.Materialize(entity, registry: null, unresolved);

        await Assert.That(instances).IsEmpty();
        await Assert.That(unresolved.Select(c => c.Id))
            .IsEquivalentTo(new[] { TestComponentIds.MoverId, LedgeId });
    }

    /// <summary>
    /// Nullable value-type fields (<c>int?</c>, <c>float?</c>) round-trip through the generated
    /// reader like any other leaf. Pinned because the generator once had no <c>Nullable&lt;T&gt;</c>
    /// case and SILENTLY skipped these fields. The absent half matters equally: no payload
    /// property (or an explicit JSON null) must keep the record's own null.
    /// </summary>
    [Test]
    public async Task nullable_value_fields_materialize_when_present_and_stay_null_when_absent()
    {
        var authored = Object(Payload(
            TestComponentIds.GlowId, """{"ShadowMapSize":4096,"ShadowBlur":2.8}"""));
        var glow = AuthoredComponentRouter.Materialize(authored, TestRegistry.Default)
            .OfType<GlowFixture>().Single();
        await Assert.That(glow.ShadowMapSize).IsEqualTo(4096);
        await Assert.That(glow.ShadowBlur).IsEqualTo(2.8f);

        var unset = Object(Payload(TestComponentIds.GlowId, """{"ShadowMapSize":null}"""));
        var defaults = AuthoredComponentRouter.Materialize(unset, TestRegistry.Default)
            .OfType<GlowFixture>().Single();
        await Assert.That(defaults.ShadowMapSize).IsNull();
        await Assert.That(defaults.ShadowBlur).IsNull();
    }

    /// <summary>
    /// The repair path, end to end. A payload whose id nothing claims still materializes when it
    /// names its type — the reason a GUID id is survivable rather than a one-way door.
    /// </summary>
    [Test]
    public async Task a_payload_with_an_unknown_id_falls_back_to_its_type_name()
    {
        var entity = Object(Payload(
            Guid.NewGuid(), """{"Friction":0.5,"Label":"south"}""", LedgeType));

        var unresolved = new List<AuthoredComponentData>();
        var instances = AuthoredComponentRouter.Materialize(entity, new LedgeRegistry(), unresolved);

        await Assert.That(instances.OfType<LedgeFixture>().Single().Label).IsEqualTo("south");
        await Assert.That(unresolved).IsEmpty();
    }

    /// <summary>
    /// And a payload with NO id at all repairs the same way, which is the case the type name was
    /// really added for: a document written before its component had an id.
    /// </summary>
    [Test]
    public async Task a_payload_with_no_id_at_all_still_repairs_by_type_name()
    {
        var entity = Object(Payload(Guid.Empty, """{"Friction":0.5,"Label":"north"}""", LedgeType));

        var unresolved = new List<AuthoredComponentData>();
        var instances = AuthoredComponentRouter.Materialize(entity, new LedgeRegistry(), unresolved);

        await Assert.That(instances.OfType<LedgeFixture>().Single().Label).IsEqualTo("north");
        await Assert.That(unresolved).IsEmpty();
    }

    /// <summary>A payload with neither an id nor a type name cannot be read: there is nothing to
    /// identify it by. It is REPORTED rather than silently skipped.</summary>
    [Test]
    public async Task a_payload_with_neither_an_id_nor_a_type_is_reported()
    {
        var entity = Object(Payload(Guid.Empty, """{"Friction":0.5}"""));

        var unresolved = new List<AuthoredComponentData>();
        await Assert.That(AuthoredComponentRouter.Materialize(entity, new LedgeRegistry(), unresolved))
            .IsEmpty();
        await Assert.That(unresolved.Count).IsEqualTo(1);
    }

    /// <summary>An id nobody claims is REPORTED. Silently dropping authored data is the failure
    /// this whole mechanism exists to prevent, so the loader must not fail that way either.</summary>
    [Test]
    public async Task materialize_reports_an_id_no_registry_claims()
    {
        var stranger = Guid.NewGuid();
        var entity = Object(Payload(stranger, """{"X":1}""", "Someone.Else"));
        var unresolved = new List<AuthoredComponentData>();

        await Assert.That(AuthoredComponentRouter.Materialize(entity, new LedgeRegistry(), unresolved))
            .IsEmpty();
        var reported = unresolved.Single();
        await Assert.That(reported.Id).IsEqualTo(stranger);
        await Assert.That(reported.Type).IsEqualTo("Someone.Else");
    }

    // ---- materializing a LIST, with no entity in sight ------------------------------------
    //
    // The same {"Id", "Data"} payloads are how a game's CONFIG DOCUMENT stores its tuning groups
    // — a file with no entities in it at all. These pin that the list overload is the same
    // reading, so a config document and an entity cannot drift apart on what a payload means.

    /// <summary>The shape a config document holds: payloads, no entity.</summary>
    [Test]
    public async Task a_bare_list_materializes_the_same_as_an_entity_would()
    {
        AuthoredComponentData[] document =
        [
            Payload(TestComponentIds.MoverId, """{"Kind":"Dynamic","Mass":2.5}"""),
            Payload(LedgeId, """{"Friction":0.35,"Label":"north"}""", LedgeType),
        ];

        var instances = AuthoredComponentRouter.Materialize(document, new CombinedRegistry());

        await Assert.That(instances.OfType<MoverFixture>().Single().Mass).IsEqualTo(2.5f);
        await Assert.That(instances.OfType<LedgeFixture>().Single().Label).IsEqualTo("north");
    }

    /// <summary>A payload whose id no registry knows still loads by TYPE NAME — regenerating a
    /// [Guid] must not make a config file unreadable.</summary>
    [Test]
    public async Task a_list_payload_with_an_unknown_id_still_loads_by_type_name()
    {
        var stale = Guid.Parse("ffffffff-0000-0000-0000-00000000ffff");
        AuthoredComponentData[] document =
            [Payload(stale, """{"Friction":0.75,"Label":"west"}""", LedgeType)];

        var unresolved = new List<AuthoredComponentData>();
        var instances = AuthoredComponentRouter.Materialize(document, new LedgeRegistry(), unresolved);

        await Assert.That(instances.OfType<LedgeFixture>().Single().Friction).IsEqualTo(0.75f);
        await Assert.That(unresolved).IsEmpty();
    }

    /// <summary>It materializes and REPORTS; it does not enforce. Which payloads a document may
    /// carry is that document's rule, not the router's.</summary>
    [Test]
    public async Task a_list_reports_what_it_could_not_read_rather_than_throwing()
    {
        var stranger = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        AuthoredComponentData[] document =
        [
            Payload(LedgeId, """{"Label":"kept"}"""),
            Payload(stranger, """{"Whatever":1}""", "Someone.Else"),
        ];

        var unresolved = new List<AuthoredComponentData>();
        var instances = AuthoredComponentRouter.Materialize(document, new LedgeRegistry(), unresolved);

        await Assert.That(instances.OfType<LedgeFixture>().Single().Label).IsEqualTo("kept");
        await Assert.That(unresolved.Single().Id).IsEqualTo(stranger);
    }

    /// <summary>Duplicates are the caller's problem: a config document rejects them, a scene may
    /// not, and the router must not decide that for either.</summary>
    [Test]
    public async Task a_list_materializes_duplicates_rather_than_deciding_about_them()
    {
        AuthoredComponentData[] document =
            [Payload(LedgeId, """{"Label":"first"}"""), Payload(LedgeId, """{"Label":"second"}""")];

        var instances = AuthoredComponentRouter.Materialize(document, new LedgeRegistry());

        await Assert.That(instances.OfType<LedgeFixture>().Select(l => l.Label))
            .IsEquivalentTo(new[] { "first", "second" });
    }

    /// <summary>An empty document is empty, not an error.</summary>
    [Test]
    public async Task an_empty_list_materializes_to_nothing()
    {
        await Assert.That(AuthoredComponentRouter.Materialize([], new LedgeRegistry())).IsEmpty();
    }

    /// <summary>An unreadable payload is REPORTED, not silently dropped — and one bad component
    /// does not cost the object the rest.</summary>
    [Test]
    public async Task an_unreadable_payload_is_reported()
    {
        var entity = Object(
            Payload(TestComponentIds.MoverId, """{"Mass":"not a number"}"""),
            Payload(TestComponentIds.GlowId, """{"ShadowMapSize":512}"""));

        var unresolved = new List<AuthoredComponentData>();
        IReadOnlyList<object> instances =
            AuthoredComponentRouter.Materialize(entity, TestRegistry.Default, unresolved);

        await Assert.That(unresolved.Select(c => c.Id))
            .IsEquivalentTo(new[] { TestComponentIds.MoverId });
        await Assert.That(instances.OfType<GlowFixture>().Single().ShadowMapSize).IsEqualTo(512);
    }

    /// <summary>Stands in for a game that hand-implements the registry interface.</summary>
    private sealed class LedgeRegistry : Paradise.Authoring.IAuthoredComponentRegistry
    {
        public IReadOnlyCollection<Guid> ComponentIds { get; } = new[] { LedgeId };

        public bool TryRead(Guid id, JsonElement data, out object? component)
        {
            if (id != LedgeId)
            {
                component = null;
                return false;
            }
            component = data.Deserialize(LedgeFixtureJsonContext.Default.LedgeFixture);
            return true;
        }

        public bool TryReadByType(string fullTypeName, JsonElement data, out object? component)
        {
            if (fullTypeName != LedgeType)
            {
                component = null;
                return false;
            }
            component = data.Deserialize(LedgeFixtureJsonContext.Default.LedgeFixture);
            return true;
        }
    }

    /// <summary>The generated registry plus the hand-written one — what a game composing several
    /// sources looks like to the router: one interface, whoever answers first.</summary>
    private sealed class CombinedRegistry : Paradise.Authoring.IAuthoredComponentRegistry
    {
        private static readonly Paradise.Authoring.IAuthoredComponentRegistry[] Sources =
            [TestRegistry.Default, new LedgeRegistry()];

        public IReadOnlyCollection<Guid> ComponentIds { get; } =
            Sources.SelectMany(s => s.ComponentIds).ToArray();

        public bool TryRead(Guid id, JsonElement data, out object? component)
        {
            foreach (var source in Sources)
            {
                if (source.TryRead(id, data, out component)) return true;
            }
            component = null;
            return false;
        }

        public bool TryReadByType(string fullTypeName, JsonElement data, out object? component)
        {
            foreach (var source in Sources)
            {
                if (source.TryReadByType(fullTypeName, data, out component)) return true;
            }
            component = null;
            return false;
        }
    }
}
