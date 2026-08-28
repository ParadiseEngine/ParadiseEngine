using System.Runtime.InteropServices;
using System.Text.Json;
using Paradise.Export.Data;
using Paradise.Export.Serialization;

namespace Paradise.Export.Tests;

/// <summary>
/// The router is what lets the engine's components be DECLARED without the exported document
/// changing shape. An editor knows only ids and JSON; these tests pin that each id still comes back
/// as the record the runtime reads, and that a game's own component is left alone.
/// </summary>
public class AuthoredComponentRouterTests
{
    /// <summary>A game's own component: an id the engine does not know, plus the type name that
    /// makes the payload identifiable when the id resolves to nothing.</summary>
    private static readonly Guid LedgeId = new("f0000000-0000-4000-8000-000000000001");

    private const string LedgeType = "Paradise.Export.Tests.LedgeFixture";

    private static AuthoredComponentData Payload(Guid id, string json, string? type = null) =>
        new() { Id = id, Type = type, Data = JsonDocument.Parse(json).RootElement.Clone() };

    /// <summary>An object, which since schema v5 is nothing but its components. It reads as a
    /// no-op because it IS one — the routing tier this used to stand for is gone, and these tests
    /// keep the spelling so what they assert stays comparable to what they asserted before.</summary>
    private static List<AuthoredComponentData> Object(params AuthoredComponentData[] components) =>
        [.. components];

    [Test]
    public async Task every_engine_component_reaches_its_typed_slot()
    {
        var entity = Object(
            Payload(typeof(RenderableComponentData).GUID, """{"Mesh":"Models/knight.glb"}"""),
            Payload(typeof(RigidbodyComponentData).GUID, """{"BodyType":"Dynamic","Mass":2.5}"""),
            Payload(typeof(AgentComponentData).GUID, """{"MoveSpeed":3.5,"IdleClip":"Idle"}"""),
            Payload(typeof(EntityInteractableComponentData).GUID, """{"DisplayName":"Lever"}"""),
            Payload(typeof(SpriteAnimationComponentData).GUID, """{"Sheet":"sprites/torch.ktx2","Columns":4}"""),
            Payload(typeof(ParticleEmitterComponentData).GUID, """{"Kind":"Voxel","EmitRate":12}"""),
            Payload(typeof(AudioEmitterComponentData).GUID, """{"StartEvent":"Play_Torch","Is3D":true}"""),
            Payload(typeof(ColliderComponentData).GUID,
                """{"Colliders":[{"ShapeType":"Box","Size":[1,2,3],"IsTrigger":true}]}"""));

        await Assert.That(entity.Get<RenderableComponentData>()!.Mesh).IsEqualTo("Models/knight.glb");
        await Assert.That(entity.Get<RigidbodyComponentData>()!.BodyType).IsEqualTo(PhysicsBodyType.Dynamic);
        await Assert.That(entity.Get<RigidbodyComponentData>()!.Mass).IsEqualTo(2.5f);
        await Assert.That(entity.Get<AgentComponentData>()!.MoveSpeed).IsEqualTo(3.5f);
        await Assert.That(entity.Get<EntityInteractableComponentData>()!.DisplayName).IsEqualTo("Lever");
        await Assert.That(entity.Get<SpriteAnimationComponentData>()!.Columns).IsEqualTo(4);
        await Assert.That(entity.Get<ParticleEmitterComponentData>()!.Kind).IsEqualTo(ParticleRenderKind.Voxel);
        await Assert.That(entity.Get<AudioEmitterComponentData>()!.StartEvent).IsEqualTo("Play_Torch");
        await Assert.That(entity.Get<ColliderComponentData>()!.Colliders.Single().ShapeType)
            .IsEqualTo(PhysicsShapeType.Box);
        await Assert.That(entity.Get<ColliderComponentData>()!.Colliders.Single().IsTrigger).IsTrue();

        // Eight payloads in, eight readable back. Nothing is unpacked on the way in any more, so
        // what this pins is that Get<T> finds each one by the id its own [Guid] declares — the
        // property the typed slots used to provide and the reason they could be deleted.
        await Assert.That(entity.Count).IsEqualTo(8);
    }

    /// <summary>The reason Custom exists. A game's component is carried verbatim, because the
    /// engine cannot name its type and does not try.</summary>
    [Test]
    public async Task an_unknown_id_is_carried_verbatim_in_custom()
    {
        var entity = Object(Payload(LedgeId, """{"Friction":0.35,"IsTrigger":false}""", LedgeType));

        var custom = entity.Single();
        await Assert.That(custom.Id).IsEqualTo(LedgeId);
        await Assert.That(custom.Type).IsEqualTo(LedgeType);
        await Assert.That(custom.Data.GetProperty("Friction").GetSingle()).IsEqualTo(0.35f);
        await Assert.That(custom.Data.GetProperty("IsTrigger").ValueKind).IsEqualTo(JsonValueKind.False);
    }

    /// <summary>
    /// The loading half. Engine components arrive already typed; a game's come back through its
    /// registry — and the caller gets ONE list of records rather than typed properties mixed with
    /// raw JSON it has to remember to deserialize.
    /// </summary>
    [Test]
    public async Task materialize_returns_engine_and_game_components_as_instances()
    {
        var entity = Object(
            Payload(typeof(RigidbodyComponentData).GUID, """{"BodyType":"Dynamic","Mass":2.5}"""),
            Payload(typeof(RenderableComponentData).GUID, """{"Mesh":"Models/knight.glb"}"""),
            Payload(LedgeId, """{"Friction":0.35,"IsTrigger":true,"Label":"north"}"""));

        var instances = AuthoredComponentRouter.Materialize(entity, new LedgeRegistry());

        await Assert.That(instances.OfType<RigidbodyComponentData>().Single().Mass).IsEqualTo(2.5f);
        await Assert.That(instances.OfType<RenderableComponentData>().Single().Mesh)
            .IsEqualTo("Models/knight.glb");
        await Assert.That(instances.OfType<LedgeFixture>().Single().Friction).IsEqualTo(0.35f);
    }

    /// <summary>Without a registry the engine components still materialize — it cannot name a
    /// game's types and does not pretend to.</summary>
    [Test]
    public async Task materialize_without_a_registry_still_yields_the_engine_components()
    {
        var entity = Object(
            Payload(typeof(AgentComponentData).GUID, """{"MoveSpeed":3}"""),
            Payload(LedgeId, """{"Friction":0.1}"""));

        var unresolved = new List<AuthoredComponentData>();
        var instances = AuthoredComponentRouter.Materialize(entity, registry: null, unresolved);

        await Assert.That(instances.OfType<AgentComponentData>().Single().MoveSpeed).IsEqualTo(3f);
        await Assert.That(unresolved.Select(c => c.Id)).IsEquivalentTo(new[] { LedgeId });
    }

    /// <summary>
    /// Nullable value-type fields (<c>int?</c>, <c>float?</c>) round-trip through the generated
    /// reader like any other leaf. Pinned because the generator once had no <c>Nullable&lt;T&gt;</c>
    /// case and SILENTLY skipped these fields — the scene authored a 4096 shadow map, the record
    /// materialized null, and the renderer quietly ran at its 1024 default. The absent half
    /// matters equally: no payload property (or an explicit JSON null) must keep the record's own
    /// null, which is the contract's "unset leaves the renderer's default".
    /// </summary>
    [Test]
    public async Task nullable_value_fields_materialize_when_present_and_stay_null_when_absent()
    {
        var authored = Object(Payload(
            typeof(EnvironmentData).GUID, """{"ShadowMapSize":4096,"ShadowBlur":2.8}"""));
        var environment = AuthoredComponentRouter.Materialize(authored)
            .OfType<EnvironmentData>().Single();
        await Assert.That(environment.ShadowMapSize).IsEqualTo(4096);
        await Assert.That(environment.ShadowBlur).IsEqualTo(2.8f);

        var unset = Object(Payload(
            typeof(EnvironmentData).GUID, """{"ShadowMapSize":null,"AmbientMode":"Color"}"""));
        var defaults = AuthoredComponentRouter.Materialize(unset)
            .OfType<EnvironmentData>().Single();
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
    ///
    /// Worth pinning because the routing half is what makes or breaks it — dropping an id-less
    /// payload on the way in would leave the fallback below correct and unreachable.
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
    /// identify it by. It is REPORTED rather than silently skipped — the document still carries
    /// it, and a reader that quietly ignored an entry would hide the export bug that wrote it.
    /// </summary>
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
        // Reported as the whole component, so the caller's message can name the type as well as
        // the id — "could not read <guid>" is not something anyone can act on.
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
            Payload(typeof(RigidbodyComponentData).GUID, """{"BodyType":"Dynamic","Mass":2.5}"""),
            Payload(LedgeId, """{"Friction":0.35,"Label":"north"}"""),
        ];

        var instances = AuthoredComponentRouter.Materialize(document, new LedgeRegistry());

        await Assert.That(instances.OfType<RigidbodyComponentData>().Single().Mass).IsEqualTo(2.5f);
        await Assert.That(instances.OfType<LedgeFixture>().Single().Label).IsEqualTo("north");
    }

    /// <summary>The gain over a hand-rolled loop, and the reason to share this reader at all: a
    /// payload whose id no registry knows still loads by TYPE NAME. A config document that
    /// hand-rolled its own reading did not get this, so regenerating a [Guid] made the file
    /// unreadable where the identical payload on an entity survived.</summary>
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

    /// <summary>It materializes and REPORTS; it does not enforce. A document that requires a
    /// payload to be present, unique, or of a kind that document may carry checks that itself —
    /// the router cannot know which of those any given document requires.</summary>
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

    /// <summary>Duplicates are the caller's problem, and stating it here is the point: a config
    /// document rejects them, a scene may not, and the router must not decide that for either.</summary>
    [Test]
    public async Task a_list_materializes_duplicates_rather_than_deciding_about_them()
    {
        AuthoredComponentData[] document =
            [Payload(LedgeId, """{"Label":"first"}"""), Payload(LedgeId, """{"Label":"second"}""")];

        var instances = AuthoredComponentRouter.Materialize(document, new LedgeRegistry());

        await Assert.That(instances.OfType<LedgeFixture>().Select(l => l.Label))
            .IsEquivalentTo(new[] { "first", "second" });
    }

    /// <summary>An empty document is empty, not an error — a config file may legitimately declare
    /// no components and lean entirely on the records' own defaults.</summary>
    [Test]
    public async Task an_empty_list_materializes_to_nothing()
    {
        await Assert.That(AuthoredComponentRouter.Materialize([], new LedgeRegistry())).IsEmpty();
    }

    /// <summary>Stands in for the registry a game's [Authored] records generate.</summary>
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

    /// <summary>
    /// The engine's component ids, pinned to their literal values.
    ///
    /// These are WIRE CONTRACT. Every committed scene in every game repo stores them as the
    /// <c>Id</c> of each authored payload, so changing one does not fail a build — it makes every
    /// document that carries the old value route nowhere, which surfaces as a component that
    /// silently stopped being read. Nothing else in the engine would notice: the generator, the
    /// registry and the router all read the same <c>[Guid]</c>, so they agree with each other
    /// about a value that is now wrong everywhere else.
    ///
    /// This replaced a test asserting the router's constant matched the record's attribute. That
    /// pairing was real while <c>ParadiseComponentIds</c> held a second copy; once the attribute
    /// became the only source, the assertion reduced to "Type.GUID reads GuidAttribute" — a test
    /// of the BCL. The risk it was aimed at (an id drifting unnoticed) is the one pinned here.
    ///
    /// A NEW component adds a line. A CHANGED line is the thing to stop and think about.
    /// </summary>
    [Test]
    public async Task the_engine_component_ids_are_what_every_exported_document_already_says()
    {
        (Type Record, string Id)[] contract =
        [
            (typeof(NameComponentData), "f83f51f4-093a-42c9-aa7a-f50f48c3b5f9"),
            (typeof(TransformComponentData), "5b1a2ea9-a4bb-4ba2-be15-b645ccf50004"),
            (typeof(RenderableComponentData), "f2c0357e-94dd-4a5a-9803-518066cb54b2"),
            (typeof(ColliderComponentData), "e1cd1bc8-86f2-4225-adc9-4a324c70ebf9"),
            (typeof(RigidbodyComponentData), "b7ab4dd8-c8da-4dc2-9e5e-192fd74deb11"),
            (typeof(AgentComponentData), "5801915b-3d0c-4940-8970-7d1487b991cf"),
            (typeof(EntityInteractableComponentData), "0283ee5f-775b-412b-a91c-03ecd9b61165"),
            (typeof(SpriteAnimationComponentData), "d3e53cd4-89c6-4ca8-851e-7596da889c68"),
            (typeof(ParticleEmitterComponentData), "1b4d1bdd-dea1-4b86-9b6a-879c46346b9e"),
            (typeof(AudioEmitterComponentData), "e6ec7f42-df09-4ec9-af06-128ddf3eda8e"),
            (typeof(SceneLightData), "fc886b84-c48c-4415-afd9-b03d6faf5ab7"),
            (typeof(EnvironmentData), "f5f4a867-fe27-426a-82f2-1a2de5aceb2f"),
        ];

        foreach ((Type record, string id) in contract)
        {
            await Assert.That(record.GUID).IsEqualTo(Guid.Parse(id))
                .Because($"{record.Name}'s id is stored in every exported document");
        }

        // Distinct, because two records sharing an id makes whichever the registry reaches first
        // answer for both. The generator raises PAUT006 for that inside one assembly; this covers
        // the list above being edited by copy-paste, which is how a duplicate gets written.
        await Assert.That(contract.Select(c => c.Id).Distinct().Count()).IsEqualTo(contract.Length);
    }

    /// <summary>A payload that cannot be read as the component it claims to be is REPORTED, not
    /// silently dropped — losing authored data without a word is the worst outcome here.
    ///
    /// Materialize is where it surfaces, because Materialize is the only thing that reads. Nothing
    /// deserializes a payload on the way INTO a document any more: an object is its component list
    /// and the list is carried verbatim.</summary>
    [Test]
    public async Task an_unreadable_engine_payload_is_reported()
    {
        var entity = Object(
            Payload(typeof(RigidbodyComponentData).GUID, """{"Mass":"not a number"}"""),
            Payload(typeof(AgentComponentData).GUID, """{"MoveSpeed":3}"""));

        var unresolved = new List<AuthoredComponentData>();
        IReadOnlyList<object> instances =
            AuthoredComponentRouter.Materialize(entity, registry: null, unresolved);

        await Assert.That(unresolved.Select(c => c.Id))
            .IsEquivalentTo(new[] { typeof(RigidbodyComponentData).GUID });
        // The good one still materialized: one bad component does not cost the object the rest.
        await Assert.That(instances.OfType<AgentComponentData>().Single().MoveSpeed).IsEqualTo(3f);
    }
}
